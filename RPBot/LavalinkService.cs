using Discord;
using Discord.WebSocket;
using Lavalink4NET;
using RPBot.Music;
using Lavalink4NET.DiscordNet;
using Lavalink4NET.Extensions;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Rest.Entities.Tracks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>
    /// Управляет жизненным циклом Lavalink-процесса и предоставляет
    /// высокоуровневые методы воспроизведения для музыкальных команд.
    /// </summary>
    public sealed class LavalinkService : IAsyncDisposable, IDisposable
    {
        private readonly MusicConfig _config;
        private readonly Func<DiscordSocketClient> _getClient;
        private DiscordSocketClient _discordClient => _getClient();

        private ServiceProvider? _serviceProvider;
        private IAudioService? _audioService;
        private Process? _lavalinkProcess;
        private Process? _ytCipherProcess;
        private bool _disposed;

        public Action<string>? LogSink { get; set; }

        public LavalinkService(Func<DiscordSocketClient> getClient, MusicConfig config)
        {
            _getClient = getClient;
            _config = config;
        }

        // ─── Запуск ───────────────────────────────────────────────────────

        /// <summary>
        /// Запускает Lavalink-процесс (если AutoStart) и инициализирует IAudioService.
        /// Вызывается из OnReady после того, как Discord-клиент залогинен.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (!_config.Enabled)
            {
                Log("[Music] Музыка отключена в конфиге (Music.Enabled = false).");
                return;
            }

            await StartLavalinkProcessAsync(cancellationToken);
            BuildServices();
            await StartHostedServicesAsync(cancellationToken);
        }

        /// <summary>
        /// Фаза 1: строим DI-контейнер и подписываемся на Discord-события.
        /// Должна вызываться ДО LoginAsync, чтобы DiscordClientWrapper
        /// поймал событие Ready в нужный момент.
        /// AudioServiceHost НЕ запускается здесь — он стартует только после Lavalink.
        /// </summary>
        public Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            if (!_config.Enabled) return Task.CompletedTask;
            BuildServices();
            Log("[Music] DI-контейнер собран, DiscordClientWrapper подписан на события ✓");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Фаза 2: запускаем Lavalink-процесс, затем поднимаем AudioServiceHost.
        /// Вызывается после того как Discord-клиент уже подключён (после Ready).
        /// Возвращает true если Lavalink ответил на зонд в отведённое время.
        /// </summary>
        public async Task<bool> LaunchProcessAsync(CancellationToken cancellationToken = default)
        {
            if (!_config.Enabled) return false;
            await StartYtCipherProcessAsync(cancellationToken);
            var ready = await StartLavalinkProcessAsync(cancellationToken);
            await StartHostedServicesAsync(cancellationToken);
            Log("[Music] AudioServiceHost запущен после Lavalink ✓");
            return ready;
        }

        /// <summary>
        /// Пересобирает DI-контейнер с актуальным Discord-клиентом.
        /// Вызывается при перезапуске бота когда создаётся новый DiscordSocketClient.
        /// </summary>
        public async Task RebuildClientAsync(CancellationToken cancellationToken = default)
        {
            if (_serviceProvider is null) return;
            Log("[Music] Пересборка DI-контейнера с новым Discord-клиентом...");
            await StopHostedServicesAsync(cancellationToken);
            _serviceProvider.Dispose();
            _serviceProvider = null;
            _audioService = null;
            BuildServices();
            await StartHostedServicesAsync(cancellationToken);
            Log("[Music] DI-контейнер пересобран ✓");
        }

        private async Task StopHostedServicesAsync(CancellationToken cancellationToken)
        {
            if (_serviceProvider is null) return;
            foreach (var svc in _serviceProvider.GetServices<IHostedService>())
            {
                try { await svc.StopAsync(cancellationToken); } catch { }
            }
        }

        private async Task StartYtCipherProcessAsync(CancellationToken cancellationToken)
        {
            if (!_config.YtCipherAutoStart) return;

            if (_ytCipherProcess is { HasExited: false })
            {
                Log("[Music] yt-cipher уже запущен.");
                return;
            }

            var denoExe = ResolveDeno();
            if (denoExe is null)
            {
                Log("[Music] deno не найден — yt-cipher не будет запущен.");
                return;
            }

            var ytCipherDir = BotConfig.ResolvePath(_config.YtCipherPath);
            var serverTs = Path.Combine(ytCipherDir, "server.ts");
            if (!File.Exists(serverTs))
            {
                Log($"[Music] server.ts не найден: {serverTs} — yt-cipher не будет запущен.");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = denoExe,
                Arguments = "run --allow-net --allow-env --allow-read --allow-write --allow-run server.ts",
                WorkingDirectory = ytCipherDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.Environment["PORT"] = _config.YtCipherPort.ToString();
            psi.Environment["OVERRIDE_SCRIPT_VARIANT"] = "IAS";

            Log($"[Music] Запуск yt-cipher: {denoExe} в {ytCipherDir} на порту {_config.YtCipherPort}");
            try
            {
                _ytCipherProcess = Process.Start(psi);
                if (_ytCipherProcess is null)
                {
                    Log("[Music] Не удалось запустить yt-cipher процесс.");
                    return;
                }
                Log($"[Music] yt-cipher PID={_ytCipherProcess.Id}");
            }
            catch (Exception ex)
            {
                Log($"[Music] Ошибка запуска yt-cipher: {ex.Message}");
                return;
            }

            // Ждём готовности — до 20 сек
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(20);
            var url = $"http://127.0.0.1:{_config.YtCipherPort}/metrics";
            Log("[Music] Ожидание готовности yt-cipher...");
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var resp = await http.GetAsync(url, cancellationToken);
                    if (resp.IsSuccessStatusCode) { Log("[Music] yt-cipher готов ✓"); return; }
                }
                catch { }
                await Task.Delay(1000, cancellationToken);
            }
            Log("[Music] yt-cipher не ответил в отведённое время.");
        }

        private static string? ResolveDeno()
        {
            // 1. Стандартный путь установки Deno (~/.deno/bin/deno или deno.exe)
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var exeName = OperatingSystem.IsWindows() ? "deno.exe" : "deno";
            var candidate = Path.Combine(home, ".deno", "bin", exeName);
            if (File.Exists(candidate)) return candidate;

            // 2. Ищем в PATH через where (Windows) / which (Unix)
            var finder = OperatingSystem.IsWindows() ? "where.exe" : "which";
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = finder,
                    Arguments = "deno",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                });
                if (proc is not null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                    if (!string.IsNullOrEmpty(first) && File.Exists(first)) return first;
                }
            }
            catch { }

            // 3. Fallback — надеемся что deno есть в PATH
            return exeName;
        }

        private async Task<bool> StartLavalinkProcessAsync(CancellationToken cancellationToken)
        {
            if (!_config.AutoStart)
            {
                Log("[Music] AutoStart выключен, Lavalink запускать не буду.");
                return false;
            }

            var jarPath = BotConfig.ResolvePath(_config.JarPath);
            if (!File.Exists(jarPath))
            {
                Log($"[Music] Lavalink.jar не найден: {jarPath}. Запуск пропущен.");
                return false;
            }

            if (_lavalinkProcess is { HasExited: false })
            {
                Log("[Music] Lavalink уже запущен.");
                // Проверим — может он уже и отвечает
                return await WaitUntilReadyAsync(cancellationToken);
            }

            var workDir = Path.GetDirectoryName(jarPath) ?? ".";
            var javaExe = ResolveJavaExecutable();
            Log($"[Music] Java: {javaExe}");

            // Всегда запускаем без оболочки — перехватываем stdout/stderr и пишем построчно.
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = $"-jar \"{jarPath}\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };

            Log($"[Music] Запуск Lavalink: {javaExe} -jar {jarPath}");
            try
            {
                _lavalinkProcess = Process.Start(psi);
                if (_lavalinkProcess is null)
                {
                    Log("[Music] Process.Start вернул null — не удалось запустить java.");
                    return false;
                }
                Log($"[Music] Lavalink процесс запущен, PID={_lavalinkProcess.Id}");

                // Логи Lavalink намеренно не выводятся — достаточно [Music] логов
                _lavalinkProcess.OutputDataReceived += (_, e) => { };
                _lavalinkProcess.ErrorDataReceived  += (_, e) => { };
                _lavalinkProcess.BeginOutputReadLine();
                _lavalinkProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Log($"[Music] Не удалось запустить Lavalink: {ex.Message}");
                return false;
            }

            return await WaitUntilReadyAsync(cancellationToken);
        }

        private static string ResolveJavaExecutable()
        {
            var exeName = OperatingSystem.IsWindows() ? "java.exe" : "java";

            // 1. JAVA_HOME — приоритет
            var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome))
            {
                var candidate = Path.Combine(javaHome, "bin", exeName);
                if (File.Exists(candidate)) return candidate;
            }

            // 2. Ищем через where (Windows) / which (Unix)
            var finder = OperatingSystem.IsWindows() ? "where.exe" : "which";
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = finder,
                    Arguments = "java",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                });
                if (proc is not null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                    if (!string.IsNullOrEmpty(first) && File.Exists(first)) return first;
                }
            }
            catch { }

            // 3. Fallback — надеемся что java есть в PATH
            return "java";
        }

        private async Task<bool> WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.DefaultRequestHeaders.Add("Authorization", _config.Password);
            var deadline = DateTime.UtcNow.AddSeconds(_config.StartupTimeoutSeconds);
            var url = $"http://{_config.Host}:{_config.Port}/version";

            Log($"[Music] Ожидание готовности Lavalink (до {_config.StartupTimeoutSeconds}с)...");
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var resp = await http.GetAsync(url, cancellationToken);
                    if (resp.IsSuccessStatusCode) { Log("[Music] Lavalink готов ✓"); return true; }
                    Log($"[Music] Зонд: HTTP {(int)resp.StatusCode}");
                }
                catch (Exception ex) { Log($"[Music] Зонд: {ex.Message}"); }
                await Task.Delay(1000, cancellationToken);
            }
            Log("[Music] Lavalink не ответил в отведённое время.");
            return false;
        }

        private void BuildServices()
        {
            var services = new ServiceCollection();

            // Lavalink4NET.DiscordNet требует DiscordSocketClient в DI
            // Регистрируем и как DiscordSocketClient, и как BaseSocketClient —
            // DiscordClientWrapper запрашивает именно BaseSocketClient
            var client = _discordClient;

            // Диагностика: логируем gateway-события чтобы убедиться что они приходят
            client.UserVoiceStateUpdated += (user, before, after) =>
            {
                Log($"[Music][DBG] UserVoiceStateUpdated: user={user.Id} before={before.VoiceChannel?.Id} after={after.VoiceChannel?.Id} sessionId='{after.VoiceSessionId}' isSelf={user.Id == client.CurrentUser?.Id}");
                return Task.CompletedTask;
            };
            client.VoiceServerUpdated += server =>
            {
                Log($"[Music][DBG] VoiceServerUpdated: guild={server.Guild.Id} endpoint={server.Endpoint} token={(server.Token?.Length > 0 ? "ok" : "EMPTY")}");
                return Task.CompletedTask;
            };

            services.AddSingleton(client);
            services.AddSingleton<Discord.WebSocket.BaseSocketClient>(client);

            // Регистрируем IAudioService + IDiscordClientWrapper (DiscordClientWrapper)
            services.AddLavalink();

            // Конфигурация ноды
            services.ConfigureLavalink(opts =>
            {
                opts.BaseAddress = new Uri($"http://{_config.Host}:{_config.Port}");
                opts.Passphrase = _config.Password;
                opts.ReadyTimeout = TimeSpan.FromSeconds(_config.StartupTimeoutSeconds);
            });

            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddHttpClient();
            services.AddMemoryCache();

            _serviceProvider = services.BuildServiceProvider();
            _audioService = _serviceProvider.GetRequiredService<IAudioService>();
        }

        private async Task StartHostedServicesAsync(CancellationToken cancellationToken)
        {
            // AudioServiceHost реализует IHostedService — запускаем вручную
            foreach (var svc in _serviceProvider!.GetServices<IHostedService>())
            {
                await svc.StartAsync(cancellationToken);
                Log($"[Music] Hosted service запущен: {svc.GetType().Name}");
            }
        }

        // ─── Health-probe ─────────────────────────────────────────────────

        /// <summary>Возвращает null при успехе или строку с ошибкой.</summary>
        public async Task<string?> ProbeAsync()
        {
            if (!_config.Enabled) return "отключён в конфиге";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            http.DefaultRequestHeaders.Add("Authorization", _config.Password);
            try
            {
                var resp = await http.GetAsync($"http://{_config.Host}:{_config.Port}/version");
                return resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode}";
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ─── Колбэки смены трека ─────────────────────────────────────────

        /// <summary>
        /// Вызывается когда Lavalink начинает воспроизведение нового трека.
        /// Устанавливается из MusicCommands.
        /// </summary>
        public Func<ulong, string, string?, TimeSpan, string?, string?, Task>? OnTrackStartedCallback { get; set; }

        /// <summary>
        /// Вызывается когда трек завершился (кроме случаев skip/stop).
        /// </summary>
        public Func<ulong, Task>? OnTrackEndedCallback { get; set; }

        /// <summary>Подключает глобальные колбэки NotifyingPlayer к LavalinkService.</summary>
        public void SetTrackCallbacks(
            Func<ulong, string, string?, TimeSpan, string?, string?, Task> onStarted,
            Func<ulong, Task> onEnded)
        {
            OnTrackStartedCallback = onStarted;
            OnTrackEndedCallback   = onEnded;
            NotifyingPlayer.OnTrackStartedGlobal = onStarted;
            NotifyingPlayer.OnTrackEndedGlobal   = onEnded;
        }

        // ─── Состояние плеера (UI) ─────────────────────────────────────────

        private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, MusicPlayerState>
            _playerStates = new();

        /// <summary>Возвращает или создаёт UI-состояние для гильдии.</summary>
        public MusicPlayerState GetOrCreateState(ulong guildId)
            => _playerStates.GetOrAdd(guildId, _ => new MusicPlayerState());

        /// <summary>Все активные состояния (для фонового обновления прогресса).</summary>
        public IEnumerable<KeyValuePair<ulong, MusicPlayerState>> GetAllStates()
            => _playerStates;

        /// <summary>Удаляет состояние (вызывается при Stop).</summary>
        public void RemoveState(ulong guildId) => _playerStates.TryRemove(guildId, out _);

        // ─── Воспроизведение ──────────────────────────────────────────────

        /// <summary>Результат команды play — содержит и строку-ответ и данные трека.</summary>
        public sealed class PlayResult
        {
            public string Message { get; init; } = "";
            public bool IsNewTrack { get; init; }
            public bool IsQueued  { get; init; }
            public bool IsPlaylist { get; init; }
            public string? PlaylistName      { get; init; }
            public int PlaylistTracksCount   { get; init; }
            public string? TrackTitle   { get; init; }
            public TimeSpan? Duration   { get; init; }
            public string? ArtworkUrl   { get; init; }
            public string? TrackUrl     { get; init; }
            public string? Author       { get; init; }
            public int QueueCount       { get; init; }
        }

        /// <summary>Воспроизводит трек по прямой ссылке в голосовом канале пользователя.</summary>
        public async Task<string> PlayAsync(
            SocketGuildUser user,
            string query,
            CancellationToken cancellationToken = default)
        {
            if (_audioService is null)
                return "❌ Музыкальный сервис не инициализирован. Проверьте конфиг (Music.Enabled).";

            if (user.VoiceChannel is null)
                return "❌ Ты должен быть в голосовом канале.";

            var guildId = user.Guild.Id;
            var voiceChannel = user.VoiceChannel;

            Log($"[Music] PlayAsync: guild={guildId} voiceChannel={voiceChannel.Id} query={query}");
            Log($"[Music] Client hash={_discordClient.GetHashCode()} guilds={_discordClient.Guilds.Count} state={_discordClient.ConnectionState} login={_discordClient.LoginState}");

            // Получаем или создаём плеер, передавая IVoiceChannel напрямую
            NotifyingPlayer? player;
            try
            {
                player = await _audioService.Players.GetPlayerAsync<NotifyingPlayer>(guildId, cancellationToken);
                Log($"[Music] GetPlayer: {(player is null ? "null, создаю новый" : $"найден state={player.State}")}");
            }
            catch (Exception ex)
            {
                Log($"[Music] GetPlayer exception: {ex.Message}");
                player = null;
            }

            if (player is null)
            {
                try
                {
                    Log($"[Music] JoinAsync: guild={guildId} channel={voiceChannel.Id} ({voiceChannel.Name})");
                    player = await _audioService.Players.JoinAsync(
                        voiceChannel,
                        PlayerFactory.Create<NotifyingPlayer, QueuedLavalinkPlayerOptions>(props => new NotifyingPlayer(props)),
                        Options.Create(new QueuedLavalinkPlayerOptions()),
                        cancellationToken);
                    Log($"[Music] JoinAsync: успех, player state={player?.State}");
                }
                catch (Exception ex)
                {
                    Log($"[Music] JoinAsync exception: {ex}");
                    return $"❌ Ошибка подключения к голосовому каналу: {ex.Message}";
                }
            }

            // TODO (поиск): заменить TrackSearchMode.None на TrackSearchMode.YouTube
            // когда будет реализован поиск по названию вместо прямой ссылки
            var track = await _audioService.Tracks.LoadTrackAsync(
                query,
                TrackSearchMode.None,
                cancellationToken: cancellationToken);

            if (track is null)
                return $"❌ Трек не найден: `{query}`";

            // Если уже играет — в очередь (задел на многотрековую очередь)
            if (player.CurrentItem is not null)
            {
                await player.Queue.AddAsync(new TrackQueueItem(track), cancellationToken);
                return $"📋 Добавлено в очередь: **{track.Title}** ({FormatDuration(track.Duration)})";
            }

            await player.PlayAsync(track, cancellationToken: cancellationToken);
            return $"▶️ Воспроизвожу: **{track.Title}** ({FormatDuration(track.Duration)})";  // kept for compat
        }

        /// <summary>Поиск треков по названию — возвращает до 5 результатов.</summary>
        public async Task<List<TrackSearchResult>> SearchTracksAsync(string query, CancellationToken cancellationToken = default)
        {
            if (_audioService is null) return new();
            var results = await _audioService.Tracks.LoadTracksAsync(query, TrackSearchMode.YouTube, cancellationToken: cancellationToken);
            var list = new List<TrackSearchResult>();
            foreach (var t in results.Tracks.Take(5))
            {
                list.Add(new TrackSearchResult
                {
                    Title      = t.Title,
                    Author     = t.Author,
                    Duration   = t.Duration,
                    Url        = t.Uri?.ToString() ?? "",
                    ArtworkUrl = t.ArtworkUri?.ToString(),
                });
            }
            return list;
        }

        public sealed class TrackSearchResult
        {
            public string   Title      { get; init; } = "";
            public string?  Author     { get; init; }
            public TimeSpan Duration   { get; init; }
            public string   Url        { get; init; } = "";
            public string?  ArtworkUrl { get; init; }
        }

        /// <summary>Полная версия play, возвращает богатый PlayResult.</summary>
        public async Task<PlayResult> PlayRichAsync(
            SocketGuildUser user,
            string query,
            CancellationToken cancellationToken = default)
        {
            if (_audioService is null)
                return new PlayResult { Message = "❌ Музыкальный сервис не инициализирован." };

            if (user.VoiceChannel is null)
                return new PlayResult { Message = "❌ Ты должен быть в голосовом канале." };

            var guildId = user.Guild.Id;
            var voiceChannel = user.VoiceChannel;

            NotifyingPlayer? player;
            try { player = await _audioService.Players.GetPlayerAsync<NotifyingPlayer>(guildId, cancellationToken); }
            catch { player = null; }

            if (player is null)
            {
                try
                {
                    player = await _audioService.Players.JoinAsync(
                        voiceChannel, PlayerFactory.Create<NotifyingPlayer, QueuedLavalinkPlayerOptions>(props => new NotifyingPlayer(props)),
                        Options.Create(new QueuedLavalinkPlayerOptions()),
                        cancellationToken);
                }
                catch (Exception ex) { return new PlayResult { Message = $"❌ Ошибка подключения: {ex.Message}" }; }
            }

            // Для YouTube-плейлистов (URL содержит list=) используем загрузку всего плейлиста
            if (IsPlaylistUrl(query))
                return await LoadAndQueuePlaylistAsync(player, query, cancellationToken);

            var track = await _audioService.Tracks.LoadTrackAsync(query, TrackSearchMode.None, cancellationToken: cancellationToken);
            if (track is null)
                return new PlayResult { Message = $"❌ Трек не найден: `{query}`" };

            var queueCount = player.Queue.Count;

            if (player.CurrentItem is not null)
            {
                await player.Queue.AddAsync(new TrackQueueItem(track), cancellationToken);
                return new PlayResult
                {
                    Message     = $"📋 Добавлено в очередь: **{track.Title}**",
                    IsQueued    = true,
                    TrackTitle  = track.Title,
                    Duration    = track.Duration,
                    ArtworkUrl  = track.ArtworkUri?.ToString(),
                    TrackUrl    = track.Uri?.ToString(),
                    Author      = track.Author,
                    QueueCount  = queueCount + 1,
                };
            }

            await player.PlayAsync(track, cancellationToken: cancellationToken);
            return new PlayResult
            {
                Message     = $"▶️ Воспроизвожу: **{track.Title}**",
                IsNewTrack  = true,
                TrackTitle  = track.Title,
                Duration    = track.Duration,
                ArtworkUrl  = track.ArtworkUri?.ToString(),
                TrackUrl    = track.Uri?.ToString(),
                Author      = track.Author,
                QueueCount  = queueCount,
            };
        }

        private static bool IsPlaylistUrl(string query)
        {
            if (!Uri.TryCreate(query, UriKind.Absolute, out var uri)) return false;
            var query2 = uri.Query;
            return query2.Contains("list=", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<PlayResult> LoadAndQueuePlaylistAsync(
            NotifyingPlayer player,
            string url,
            CancellationToken ct)
        {
            // Lavalink загружает плейлист целиком когда в URL есть list=
            var result = await _audioService!.Tracks.LoadTracksAsync(url, TrackSearchMode.None, cancellationToken: ct);
            Console.WriteLine($"[Music][DBG] Playlist load: isPlaylist={result.IsPlaylist}, hasMatches={result.HasMatches}, count={result.Count}, playlist={result.Playlist?.Name}, exception={result.Exception?.Message}");
            Log($"[Music][DBG] Playlist load: isPlaylist={result.IsPlaylist}, hasMatches={result.HasMatches}, count={result.Count}, playlist={result.Playlist?.Name}, exception={result.Exception?.Message}");
            var tracks = result.Tracks;
            if (tracks.IsDefaultOrEmpty)
            {
                if (result.Exception is not null)
                    return new PlayResult { Message = $"❌ Ошибка загрузки плейлиста: {result.Exception?.Message}" };
                return new PlayResult { Message = "❌ Плейлист не найден или пуст. Убедись, что он публичный." };
            }

            var playlistName = result.Playlist?.Name ?? "YouTube Playlist";
            var queueCountBefore = player.Queue.Count;
            int added = 0;

            foreach (var track in tracks)
            {
                if (player.CurrentItem is null && added == 0)
                    await player.PlayAsync(track, cancellationToken: ct);
                else
                    await player.Queue.AddAsync(new TrackQueueItem(track), ct);
                added++;
            }

            var firstTrack = tracks[0];
            return new PlayResult
            {
                Message            = $"📋 Плейлист **{playlistName}**: загружено {added} треков.",
                IsPlaylist         = true,
                PlaylistName       = playlistName,
                PlaylistTracksCount = added,
                IsNewTrack         = player.CurrentItem is not null,
                TrackTitle         = firstTrack.Title,
                Duration           = firstTrack.Duration,
                ArtworkUrl         = firstTrack.ArtworkUri?.ToString(),
                TrackUrl           = firstTrack.Uri?.ToString(),
                Author             = firstTrack.Author,
                QueueCount         = queueCountBefore + added,
            };
        }

        // ─── Loop ─────────────────────────────────────────────────────────

        public async Task<string> SetLoopAsync(ulong guildId, LoopMode mode, CancellationToken ct = default)
        {
            var state = GetOrCreateState(guildId);
            state.LoopMode = mode;

            var player = await GetPlayerAsync(guildId, ct);
            if (player is not null)
            {
                player.RepeatMode = mode switch
                {
                    LoopMode.Track => TrackRepeatMode.Track,
                    LoopMode.Queue => TrackRepeatMode.Queue,
                    _              => TrackRepeatMode.None,
                };
            }

            return mode switch
            {
                LoopMode.Track => "🔂 Повтор трека включён.",
                LoopMode.Queue => "🔁 Повтор очереди включён.",
                _              => "➡️ Повтор отключён.",
            };
        }

        public LoopMode GetLoopMode(ulong guildId) => GetOrCreateState(guildId).LoopMode;

        // ─── Volume ───────────────────────────────────────────────────────

        private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, int> _volumes = new();

        public int GetVolume(ulong guildId) => _volumes.GetOrAdd(guildId, 100);

        public async Task<string> SetVolumeAsync(ulong guildId, int volume, CancellationToken ct = default)
        {
            volume = Math.Clamp(volume, 0, 200);
            _volumes[guildId] = volume;
            var player = await GetPlayerAsync(guildId, ct);
            if (player is not null)
                await player.SetVolumeAsync(volume / 100f, ct);
            return $"🔊 Громкость: **{volume}%**";
        }

        // ─── Shuffle ──────────────────────────────────────────────────────

        public async Task<string> ShuffleAsync(ulong guildId, CancellationToken ct = default)
        {
            var player = await GetPlayerAsync(guildId, ct);
            if (player is null) return "❌ Ничего не играет.";
            if (player.Queue.IsEmpty) return "📋 Очередь пуста.";
            // ITrackQueue не имеет Shuffle/Clear — перемешиваем через RemoveAtAsync
            var items = player.Queue.ToList();
            var rng = new Random();
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
            // Удаляем с конца (индексы стабильны при удалении с хвоста)
            for (int i = player.Queue.Count - 1; i >= 0; i--)
                await player.Queue.RemoveAtAsync(i, ct);
            foreach (var item in items)
                await player.Queue.AddAsync(item, ct);
            return "🔀 Очередь перемешана.";
        }

        // ─── Позиция / прогресс ───────────────────────────────────────────

        /// <summary>Возвращает (elapsed, duration) текущего трека.</summary>
        public async Task<(TimeSpan Elapsed, TimeSpan Duration)?> GetPositionAsync(
            ulong guildId, CancellationToken ct = default)
        {
            var player = await GetPlayerAsync(guildId, ct);
            if (player?.CurrentItem is null) return null;
            var duration = player.CurrentItem.Track?.Duration ?? TimeSpan.Zero;
            var pos      = player.Position?.Position ?? TimeSpan.Zero;
            return (pos, duration);
        }

        // ─── Очередь (данные для embed) ───────────────────────────────────

        public async Task<bool?> IsPlayerPausedAsync(ulong guildId, CancellationToken ct = default)
        {
            var player = await GetPlayerAsync(guildId, ct);
            if (player is null) return null;
            return player.State == PlayerState.Paused;
        }

        public async Task<(string? CurrentTitle, TimeSpan? CurrentDuration,
                            List<(string Title, TimeSpan? Duration)> Queue)?>
            GetQueueDataAsync(ulong guildId, CancellationToken ct = default)
        {
            var player = await GetPlayerAsync(guildId, ct);
            if (player is null) return null;
            var cur = player.CurrentItem?.Track;
            var list = new List<(string, TimeSpan?)>();
            foreach (var item in player.Queue)
                list.Add((item.Track?.Title ?? "?", item.Track?.Duration));
            return (cur?.Title, cur?.Duration, list);
        }

        /// <summary>Возвращает URL-ы всех треков: текущего + очереди.</summary>
        public async Task<List<string>> GetQueueUrlsAsync(ulong guildId, CancellationToken ct = default)
        {
            var player = await GetPlayerAsync(guildId, ct);
            var urls = new List<string>();
            if (player is null) return urls;
            // Только очередь (без текущего трека — он сохраняется отдельно как CurrentUrl)
            foreach (var item in player.Queue)
                if (item.Track?.Uri is { } uri)
                    urls.Add(uri.ToString());
            return urls;
        }

        /// <summary>Останавливает воспроизведение и отключает бота.</summary>
        public async Task<string> StopAsync(ulong guildId, CancellationToken cancellationToken = default)
        {
            var player = await GetPlayerAsync(guildId, cancellationToken);
            if (player is null) return "❌ Ничего не играет.";

            await player.StopAsync(cancellationToken);
            await player.DisconnectAsync(cancellationToken);
            return "⏹ Воспроизведение остановлено.";
        }

        /// <summary>Ставит воспроизведение на паузу.</summary>
        public async Task<string> PauseAsync(ulong guildId, CancellationToken cancellationToken = default)
        {
            var player = await GetPlayerAsync(guildId, cancellationToken);
            if (player is null) return "❌ Ничего не играет.";
            if (player.CurrentItem is null) return "❌ Ничего не играет.";
            if (player.State == PlayerState.Paused) return $"⏸ Пауза: **{player.CurrentItem.Track?.Title ?? "трек"}**";

            await player.PauseAsync(cancellationToken);
            return $"⏸ Пауза: **{player.CurrentItem.Track?.Title ?? "трек"}**";
        }

        /// <summary>Возобновляет воспроизведение после паузы.</summary>
        public async Task<string> ResumeAsync(ulong guildId, CancellationToken cancellationToken = default)
        {
            var player = await GetPlayerAsync(guildId, cancellationToken);
            if (player is null) return "❌ Ничего не играет.";
            if (player.State != PlayerState.Paused) return $"▶️ Продолжаю: **{player.CurrentItem?.Track?.Title ?? "трек"}**";

            await player.ResumeAsync(cancellationToken);
            return $"▶️ Продолжаю: **{player.CurrentItem?.Track?.Title ?? "трек"}**";
        }

        /// <summary>
        /// Пропускает текущий трек.
        /// При наличии очереди автоматически запустится следующий (QueuedLavalinkPlayer).
        /// </summary>
        public async Task<string> SkipAsync(ulong guildId, CancellationToken cancellationToken = default)
        {
            var player = await GetPlayerAsync(guildId, cancellationToken);
            if (player is null) return "❌ Ничего не играет.";
            if (player.CurrentItem is null && player.Queue.IsEmpty) return "❌ Очередь пуста.";

            await player.SkipAsync(cancellationToken: cancellationToken);
            return "⏭ Трек пропущен.";
        }

        /// <summary>
        /// Назад: если прошло >15 с — перематывает в начало, иначе играет предыдущий трек из истории.
        /// </summary>
        public async Task<string> PreviousAsync(ulong guildId, CancellationToken cancellationToken = default)
        {
            var player = await GetPlayerAsync(guildId, cancellationToken);
            if (player is null) return "❌ Ничего не играет.";

            var state = GetOrCreateState(guildId);
            var elapsed = DateTime.UtcNow - state.TrackStartedAtUtc;

            // Если прошло >15 секунд — перемотать в начало
            if (elapsed.TotalSeconds > 15)
            {
                await player.SeekAsync(TimeSpan.Zero, cancellationToken);
                state.TrackStartedAtUtc = DateTime.UtcNow;
                return "⏮ Перемотано в начало трека.";
            }

            // Иначе — предыдущий трек из истории
            if (state.TrackHistory.Count == 0)
            {
                await player.SeekAsync(TimeSpan.Zero, cancellationToken);
                state.TrackStartedAtUtc = DateTime.UtcNow;
                return "⏮ История пуста, перемотано в начало.";
            }

            var prev = state.TrackHistory.First!.Value;
            state.TrackHistory.RemoveFirst();

            // Ставим текущий трек первым в очередь, чтобы он не потерялся
            if (player.CurrentItem is not null)
                await player.Queue.InsertAsync(0, player.CurrentItem, cancellationToken);

            var track = await _audioService!.Tracks.LoadTrackAsync(prev.Url, TrackSearchMode.None, cancellationToken: cancellationToken);
            if (track is null) return "❌ Не удалось загрузить предыдущий трек.";

            await player.PlayAsync(track, cancellationToken: cancellationToken);
            return $"⏮ Предыдущий трек: **{track.Title}**";
        }

        /// <summary>Перематывает текущий трек на указанную позицию.</summary>
        public async Task<string> SeekAsync(ulong guildId, TimeSpan position, CancellationToken cancellationToken = default)
        {
            var player = await GetPlayerAsync(guildId, cancellationToken);
            if (player is null) return "❌ Ничего не играет.";
            if (player.CurrentItem is null) return "❌ Нет текущего трека.";

            var duration = player.CurrentItem.Track?.Duration ?? TimeSpan.Zero;
            if (position > duration) position = duration;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;

            await player.SeekAsync(position, cancellationToken);

            var state = GetOrCreateState(guildId);
            state.TrackStartedAtUtc = DateTime.UtcNow - position;

            return $"⏩ Перемотано на `{MusicEmbedBuilder.FormatTime(position)}`.";
        }

        /// <summary>
        /// Удаляет трек из очереди по номеру (1-based). Не трогает текущий трек.
        /// </summary>
        public async Task<string> RemoveFromQueueAsync(ulong guildId, int number, CancellationToken cancellationToken = default)
        {
            var player = await GetPlayerAsync(guildId, cancellationToken);
            if (player is null) return "❌ Ничего не играет.";

            var count = player.Queue.Count;
            if (count == 0) return "❌ Очередь пуста.";
            if (number < 1 || number > count) return $"❌ Номер должен быть от 1 до {count}.";

            var index = number - 1;
            var items = new List<ITrackQueueItem>(player.Queue);
            var removed = items[index];
            await player.Queue.RemoveAtAsync(index, cancellationToken);

            return $"🗑️ Удалён: **{removed.Track?.Title ?? "?"}**";
        }

        /// <summary>
        /// Возвращает статус плеера и очередь.
        /// Задел: показывает до 5 треков из очереди.
        /// </summary>
        public async Task<string> GetQueueInfoAsync(ulong guildId)
        {
            var player = await GetPlayerAsync(guildId);
            if (player is null) return "❌ Ничего не играет.";

            var current = player.CurrentItem?.Track;
            if (current is null) return "❌ Ничего не играет.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"▶️ Сейчас: **{current.Title}** ({FormatDuration(current.Duration)})");

            if (!player.Queue.IsEmpty)
            {
                sb.AppendLine($"📋 Треков в очереди: **{player.Queue.Count}**");
                int i = 1;
                foreach (var item in player.Queue)
                {
                    if (i > 5) { sb.AppendLine("  ..."); break; }
                    sb.AppendLine($"  {i++}. {item.Track?.Title ?? "?"} ({FormatDuration(item.Track?.Duration)})");
                }
            }
            else
            {
                sb.AppendLine("📋 Очередь пуста.");
            }

            return sb.ToString().TrimEnd();
        }

        // ─── Вспомогательные ─────────────────────────────────────────────

        private async Task<NotifyingPlayer?> GetPlayerAsync(
            ulong guildId,
            CancellationToken cancellationToken = default)
        {
            if (_audioService is null) return null;
            return await _audioService.Players.GetPlayerAsync<NotifyingPlayer>(guildId, cancellationToken);
        }

        private static string FormatDuration(TimeSpan? duration)
        {
            if (duration is null) return "?";
            return duration.Value.TotalHours >= 1
                ? duration.Value.ToString(@"h\:mm\:ss")
                : duration.Value.ToString(@"m\:ss");
        }

        private void Log(string message)
        {
            // Если активен буфер запуска — перехватываем в него (не дублируем в консоль),
            // в файловый sink пишем всегда.
            if (_startupLogBuffer is not null)
                _startupLogBuffer.Add(message);
            else
                LogSink?.Invoke(message);

            FileSink?.Invoke(message);
        }

        /// <summary>
        /// Когда установлен — Log() пишет сюда вместо консоли.
        /// Program.cs устанавливает перед LaunchProcessAsync и забирает после,
        /// включая собранные строки в startup-бокс этапа.
        /// </summary>
        public List<string>? StartupLogBuffer { get; set; }

        // Ссылка на текущий активный буфер (совпадает с StartupLogBuffer, хранится отдельно
        // чтобы Log() работал без лишних property-read)
        private List<string>? _startupLogBuffer => StartupLogBuffer;

        /// <summary>Дополнительный sink для записи в файл (назначается из Program.cs).</summary>
        public Action<string>? FileSink { get; set; }

        // ─── Dispose ─────────────────────────────────────────────────────

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            if (_serviceProvider is not null)
            {
                foreach (var svc in _serviceProvider.GetServices<IHostedService>())
                    try { await svc.StopAsync(CancellationToken.None); } catch { }
                await _serviceProvider.DisposeAsync();
            }

            KillLavalinkProcess();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _serviceProvider?.Dispose();
            KillLavalinkProcess();
        }

        private void KillLavalinkProcess()
        {
            try
            {
                if (_lavalinkProcess is { HasExited: false })
                {
                    _lavalinkProcess.Kill(entireProcessTree: true);
                    Log("[Music] Lavalink процесс остановлен.");
                }
                _lavalinkProcess?.Dispose();
            }
            catch (Exception ex)
            {
                Log($"[Music] Ошибка при остановке Lavalink: {ex.Message}");
            }

            try
            {
                if (_ytCipherProcess is { HasExited: false })
                {
                    _ytCipherProcess.Kill(entireProcessTree: true);
                    Log("[Music] yt-cipher процесс остановлен.");
                }
                _ytCipherProcess?.Dispose();
            }
            catch (Exception ex)
            {
                Log($"[Music] Ошибка при остановке yt-cipher: {ex.Message}");
            }
        }
    }
}
