using Discord.WebSocket;
using Lavalink4NET;
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
        /// </summary>
        public async Task LaunchProcessAsync(CancellationToken cancellationToken = default)
        {
            if (!_config.Enabled) return;
            await StartYtCipherProcessAsync(cancellationToken);
            await StartLavalinkProcessAsync(cancellationToken);
            await StartHostedServicesAsync(cancellationToken);
            Log("[Music] AudioServiceHost запущен после Lavalink ✓");
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
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidate = Path.Combine(home, ".deno", "bin", "deno.exe");
            if (File.Exists(candidate)) return candidate;

            try
            {
                var where = Process.Start(new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = "deno",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                });
                if (where is not null)
                {
                    var output = where.StandardOutput.ReadToEnd();
                    where.WaitForExit();
                    var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                    if (!string.IsNullOrEmpty(first) && File.Exists(first)) return first;
                }
            }
            catch { }

            return null;
        }

        private async Task StartLavalinkProcessAsync(CancellationToken cancellationToken)
        {
            if (!_config.AutoStart)
            {
                Log("[Music] AutoStart выключен, Lavalink запускать не буду.");
                return;
            }

            var jarPath = BotConfig.ResolvePath(_config.JarPath);
            if (!File.Exists(jarPath))
            {
                Log($"[Music] Lavalink.jar не найден: {jarPath}. Запуск пропущен.");
                return;
            }

            if (_lavalinkProcess is { HasExited: false })
            {
                Log("[Music] Lavalink уже запущен.");
                return;
            }

            var workDir = Path.GetDirectoryName(jarPath) ?? ".";
            var javaExe = ResolveJavaExecutable();
            Log($"[Music] Java: {javaExe}");

            // UseShellExecute = true — запускаем через оболочку, чтобы не конфликтовать с Terminal.Gui.
            // Lavalink сам пишет логи в ./logs/, поэтому перенаправление не нужно.
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = $"-jar \"{jarPath}\"",
                WorkingDirectory = workDir,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Log($"[Music] Запуск Lavalink: {javaExe} -jar {jarPath}");
            try
            {
                _lavalinkProcess = Process.Start(psi);
                if (_lavalinkProcess is null)
                {
                    Log("[Music] Process.Start вернул null — не удалось запустить java.");
                    return;
                }
                Log($"[Music] Lavalink процесс запущен, PID={_lavalinkProcess.Id}");
            }
            catch (Exception ex)
            {
                Log($"[Music] Не удалось запустить Lavalink: {ex.Message}");
                return;
            }

            await WaitUntilReadyAsync(cancellationToken);
        }

        private static string ResolveJavaExecutable()
        {
            // 1. JAVA_HOME — приоритет
            var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome))
            {
                var candidate = Path.Combine(javaHome, "bin", "java.exe");
                if (File.Exists(candidate)) return candidate;
            }

            // 2. Ищем через where.exe (корректно находит даже если PATH задан только для cmd)
            try
            {
                var where = Process.Start(new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = "java",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                });
                if (where is not null)
                {
                    var output = where.StandardOutput.ReadToEnd();
                    where.WaitForExit();
                    var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                    if (!string.IsNullOrEmpty(first) && File.Exists(first)) return first;
                }
            }
            catch { }

            // 3. Fallback — надеемся что java есть в PATH
            return "java";
        }

        private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
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
                    if (resp.IsSuccessStatusCode) { Log("[Music] Lavalink готов ✓"); return; }
                    Log($"[Music] Зонд: HTTP {(int)resp.StatusCode}");
                }
                catch (Exception ex) { Log($"[Music] Зонд: {ex.Message}"); }
                await Task.Delay(1000, cancellationToken);
            }
            Log("[Music] Lavalink не ответил в отведённое время.");
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

        // ─── Воспроизведение ──────────────────────────────────────────────

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
            QueuedLavalinkPlayer? player;
            try
            {
                player = await _audioService.Players.GetPlayerAsync<QueuedLavalinkPlayer>(guildId, cancellationToken);
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
                        PlayerFactory.Queued,
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
            return $"▶️ Воспроизвожу: **{track.Title}** ({FormatDuration(track.Duration)})";
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
            if (player.State == PlayerState.Paused) return "⏸ Уже на паузе.";
            if (player.CurrentItem is null) return "❌ Ничего не играет.";

            await player.PauseAsync(cancellationToken);
            return $"⏸ Пауза: **{player.CurrentItem.Track?.Title ?? "трек"}**";
        }

        /// <summary>Возобновляет воспроизведение после паузы.</summary>
        public async Task<string> ResumeAsync(ulong guildId, CancellationToken cancellationToken = default)
        {
            var player = await GetPlayerAsync(guildId, cancellationToken);
            if (player is null) return "❌ Ничего не играет.";
            if (player.State != PlayerState.Paused) return "▶️ Воспроизведение не на паузе.";

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

        private async Task<QueuedLavalinkPlayer?> GetPlayerAsync(
            ulong guildId,
            CancellationToken cancellationToken = default)
        {
            if (_audioService is null) return null;
            return await _audioService.Players.GetPlayerAsync<QueuedLavalinkPlayer>(guildId, cancellationToken);
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
            LogSink?.Invoke(message);
            FileSink?.Invoke(message);
        }

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
