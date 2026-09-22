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
                /// Если Lavalink не ответил — повторяет попытку несколько раз с нарастающей задержкой,
                /// чтобы пережить холодный старт JVM (первый запуск после долгого простоя).
                /// </summary>
                public async Task StartAsync(CancellationToken cancellationToken = default)
                {
                    if (!_config.Enabled)
                    {
                        Log("[Music] Музыка отключена в конфиге (Music.Enabled = false).");
                        return;
                    }

                    var ready = await StartLavalinkWithRetryAsync(cancellationToken).ConfigureAwait(false);
                    BuildServices();
                    await StartHostedServicesAsync(cancellationToken);
                    if (!ready)
                        Log("[Music] ⚠ Lavalink не поднялся — аудиосервис стартует в degraded-режиме, переподключения обработает reconnect-loop.");
                }

                /// <summary>
                /// Один полный цикл запуска Lavalink-процесса + проверки готовности через /version.
                /// Делает до <paramref name="maxAttempts"/> попыток; между попытками — нарастающий backoff.
                /// </summary>
                private async Task<bool> StartLavalinkWithRetryAsync(CancellationToken cancellationToken, int maxAttempts = 3)
                {
                    var attempt = 0;
                    var delay = TimeSpan.FromSeconds(2);
                    while (attempt < maxAttempts)
                    {
                        attempt++;
                        Log($"[Music] Попытка запуска Lavalink #{attempt}/{maxAttempts}…");
                        var ok = await StartLavalinkProcessAsync(cancellationToken).ConfigureAwait(false);
                        if (ok) return true;
                        if (attempt >= maxAttempts) break;
                        Log($"[Music] Lavalink не ответил, повтор через {delay.TotalSeconds:0}с…");
                        try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return false; }
                        delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
                    }
                    return false;
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
                    if (!SuppressPrepareLog)
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
                    // Эта строка раньше жила в PrepareAsync и дрейфовала между ЗАПУСК и Этапом 4.
                    // Теперь DI-контейнер уже собран внутри ЗАПУСК (через PrepareAsync с
                    // SuppressPrepareLog=true), а пользователю лог показывается именно здесь,
                    // внутри Этапа 4, рядом с реальным запуском Lavalink.
                    Log("[Music] DI-контейнер собран, DiscordClientWrapper подписан на события ✓");
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
                // Fallback: поиск вверх по дереву директорий (до 5 уровней), как у Lavalink
                var foundServerTs = FindFileUpward(Path.Combine(_config.YtCipherPath, "server.ts"), AppContext.BaseDirectory, 5);
                if (foundServerTs is not null)
                {
                    ytCipherDir = Path.GetDirectoryName(foundServerTs)!;
                    serverTs = foundServerTs;
                }
            }
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
                // Fallback: поиск вверх по дереву директорий (до 5 уровней)
                jarPath = FindFileUpward(_config.JarPath, AppContext.BaseDirectory, 5) ?? jarPath;
            }

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

        /// <summary>
        /// Ищет файл по относительному пути, поднимаясь вверх по дереву директорий от startDir.
        /// Например, "Lavalink/Lavalink.jar" будет искаться в startDir, startDir/.., startDir/../.. и т.д.
        /// </summary>
        private static string? FindFileUpward(string relativePath, string startDir, int maxLevels)
        {
            var dir = startDir;
            for (int i = 0; i <= maxLevels; i++)
            {
                if (string.IsNullOrEmpty(dir)) break;
                var candidate = Path.Combine(dir, relativePath);
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent == dir) break;
                dir = parent;
            }
            return null;
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
                if (before.VoiceChannel?.Id == after.VoiceChannel?.Id) return Task.CompletedTask;
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

            services.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));
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

        // ─── Надёжный Join с ожиданием voice state ──────────────────────

        /// <summary>
        /// Подключается к голосовому каналу и надёжно ждёт пока Discord
        /// пришлёт VOICE_STATE_UPDATE для бота (с правильным channelId и sessionId).
        /// Lavalink4NET.JoinAsync внутри ждёт свой собственный TCS, но если бот
        /// уже был в каком-то канале ранее и стейт устарел — он кидает
        /// "player could not be retrieved within the specified time" с channelId:null.
        /// Здесь же мы ждём реальное событие от Discord с явным таймаутом.
        /// </summary>
        private async Task<NotifyingPlayer?> JoinAndAwaitVoiceAsync(
            SocketGuildUser user,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (_audioService is null || user.VoiceChannel is null)
                return null;

            var guildId = user.Guild.Id;
            var targetChannelId = user.VoiceChannel.Id;
            var client = _discordClient;
            var selfId = client.CurrentUser?.Id ?? 0;

            // Если бот уже подключён к нужному каналу — пропускаем join, сразу создаём плеер
            var botCurrent = user.Guild.GetUser(selfId)?.VoiceChannel;
            if (botCurrent?.Id == targetChannelId)
            {
                Log($"[Music] JoinAndAwaitVoice: бот уже в канале {targetChannelId}, join не требуется");
            }
            else
            {
                // Подписываемся на voice state ДО JoinAsync — иначе можем пропустить событие
                var tcs = new TaskCompletionSource<(ulong? channelId, string? sessionId)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                Task handler(SocketUser u, SocketVoiceState before, SocketVoiceState after)
                {
                    if (u.Id != selfId) return Task.CompletedTask;
                    if (after.VoiceChannel?.Id != targetChannelId) return Task.CompletedTask;
                    Log($"[Music][DBG] JoinAndAwaitVoice: бот подтвердил voice state channelId={after.VoiceChannel?.Id} sessionId='{after.VoiceSessionId}'");
                    tcs.TrySetResult((after.VoiceChannel?.Id, after.VoiceSessionId));
                    return Task.CompletedTask;
                }

                client.UserVoiceStateUpdated += handler;
                try
                {
                    // Запускаем JoinAsync на отдельном потоке — gateway должен обрабатывать события
                    var joinTask = Task.Run(() => _audioService.Players.JoinAsync(
                        user.VoiceChannel,
                        PlayerFactory.Create<NotifyingPlayer, QueuedLavalinkPlayerOptions>(
                            props => new NotifyingPlayer(props)),
                        Options.Create(new QueuedLavalinkPlayerOptions()),
                        cancellationToken).AsTask(), cancellationToken);

                    // Параллельно ждём реальное событие от Discord (надёжнее внутреннего TCS Lavalink4NET)
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(timeout);
                    var completedTask = await Task.WhenAny(tcs.Task, joinTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));
                    if (completedTask == tcs.Task)
                    {
                        var info = await tcs.Task;
                        if (info.channelId is null)
                        {
                            Log($"[Music] JoinAndAwaitVoice: событие voice state без channelId — отмена");
                            return null;
                        }
                    }
                    else if (completedTask == joinTask)
                    {
                        // JoinAsync сам завершился — попробуем достать плеер
                        if (joinTask.IsFaulted)
                        {
                            Log($"[Music] JoinAndAwaitVoice: JoinAsync упал — {joinTask.Exception?.GetBaseException().Message}");
                            return null;
                        }
                    }
                    else
                    {
                        Log($"[Music] JoinAndAwaitVoice: таймаут {timeout.TotalSeconds:F0}с ожидания voice state");
                        return null;
                    }
                }
                finally
                {
                    client.UserVoiceStateUpdated -= handler;
                }
            }

            // Получаем плеер (он должен быть уже создан JoinAsync; если нет — создастся)
            try { return await _audioService.Players.GetPlayerAsync<NotifyingPlayer>(guildId, cancellationToken); }
            catch (Exception ex) { Log($"[Music] JoinAndAwaitVoice: GetPlayerAsync — {ex.Message}"); return null; }
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

        /// <summary>
        /// Удаляет состояние (вызывается при Stop) и корректно гасит фоновые
        /// таймеры, иначе AutoPauseTimer/AutoStopTimer удерживают ссылку на
        /// state и callback'и могут выстрелить уже после удаления.
        /// </summary>
        public void RemoveState(ulong guildId)
        {
            if (_playerStates.TryRemove(guildId, out var state))
            {
                state.AutoPauseTimer?.Dispose();
                state.AutoStopTimer?.Dispose();
                state.AutoPauseTimer = null;
                state.AutoStopTimer = null;
            }
        }

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
                    Log($"[Music] JoinAndAwaitVoice: guild={guildId} channel={voiceChannel.Id} ({voiceChannel.Name})");
                    player = await JoinAndAwaitVoiceAsync(user, TimeSpan.FromSeconds(15), cancellationToken);
                    if (player is null)
                    {
                        Log($"[Music] JoinAndAwaitVoice: не удалось получить плеер после ожидания voice state");
                        return $"❌ Ошибка подключения к голосовому каналу: не дождались voice state от Discord.";
                    }
                    Log($"[Music] JoinAndAwaitVoice: успех, player state={player?.State}");
                }
                catch (Exception ex)
                {
                    Log($"[Music] JoinAndAwaitVoice exception: {ex}");
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

                        // Если player не удалось получить после JoinAsync — сообщим об ошибке
                        if (player is null)
                            return "❌ Не удалось получить плеер для воспроизведения.";

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
            foreach (var t in results.Tracks.Take(25))
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

            bool freshJoin = player is null;
            if (player is null)
            {
                try
                {
                    player = await JoinAndAwaitVoiceAsync(user, TimeSpan.FromSeconds(15), cancellationToken);
                    if (player is null)
                        return new PlayResult { Message = "❌ Ошибка подключения: не дождались voice state от Discord. Попробуй ещё раз." };
                }
                catch (Exception ex) { return new PlayResult { Message = $"❌ Ошибка подключения: {ex.Message}" }; }
            }

                        if (player is null)
                            return new PlayResult { Message = "❌ Не удалось получить плеер." };

                        if (freshJoin)
            {
                var savedVolume = GetVolume(guildId);
                try { await player.SetVolumeAsync(savedVolume / 100f, cancellationToken); }
                catch (Exception ex) { Log($"[Music] PlayRichAsync: не удалось установить громкость — {ex.Message}"); }
            }

            // Для YouTube-плейлистов (URL содержит list=) используем загрузку всего плейлиста
            if (IsPlaylistUrl(query))
                return await LoadAndQueuePlaylistAsync(player, guildId, query, cancellationToken);

            var track = await _audioService.Tracks.LoadTrackAsync(query, TrackSearchMode.None, cancellationToken: cancellationToken);
            if (track is null)
                return new PlayResult { Message = $"❌ Трек не найден: `{query}`" };

            var state = GetOrCreateState(guildId);

            // Добавляем в MasterQueue (дедупликация по URL)
            await state.MasterQueueLock.WaitAsync(cancellationToken);
            try
            {
                var exists = state.MasterQueue.FindIndex(e =>
                    string.Equals(e.Url, track.Uri?.ToString(), StringComparison.OrdinalIgnoreCase));
                if (exists < 0)
                {
                    state.MasterQueue.Add(new MasterTrackEntry
                    {
                        Number     = state.MasterQueue.Count + 1,
                        Title      = track.Title,
                        Author     = track.Author,
                        Url        = track.Uri?.ToString() ?? query,
                        ArtworkUrl = track.ArtworkUri?.ToString(),
                        Duration   = track.Duration,
                    });
                }
            }
            finally { state.MasterQueueLock.Release(); }

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
            ulong guildId,
            string url,
            CancellationToken ct)
        {
            var result = await _audioService!.Tracks.LoadTracksAsync(url, TrackSearchMode.None, cancellationToken: ct);
            Log($"[Music][DBG] Playlist load: isPlaylist={result.IsPlaylist}, hasMatches={result.HasMatches}, count={result.Count}, playlist={result.Playlist?.Name}, exception={result.Exception?.Message}");
            var tracks = result.Tracks;
            if (tracks.IsDefaultOrEmpty)
            {
                if (result.Exception is not null)
                    return new PlayResult { Message = $"❌ Ошибка загрузки плейлиста: {result.Exception?.Message}" };
                return new PlayResult { Message = "❌ Плейлист не найден или пуст. Убедись, что он публичный." };
            }

            var state = GetOrCreateState(guildId);
            var playlistName = result.Playlist?.Name ?? "YouTube Playlist";
            var queueCountBefore = player.Queue.Count;
            int added = 0;

            foreach (var track in tracks)
            {
                var trackUrl = track.Uri?.ToString() ?? "";

                // Добавляем в MasterQueue (дедупликация по URL)
                await state.MasterQueueLock.WaitAsync(ct);
                try
                {
                    if (!state.MasterQueue.Any(e =>
                        string.Equals(e.Url, trackUrl, StringComparison.OrdinalIgnoreCase)))
                    {
                        state.MasterQueue.Add(new MasterTrackEntry
                        {
                            Number     = state.MasterQueue.Count + 1,
                            Title      = track.Title,
                            Author     = track.Author,
                            Url        = trackUrl,
                            ArtworkUrl = track.ArtworkUri?.ToString(),
                            Duration   = track.Duration,
                        });
                    }
                }
                finally { state.MasterQueueLock.Release(); }

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

        public int GetVolume(ulong guildId) => _volumes.GetOrAdd(guildId, 10);

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

            var state = GetOrCreateState(guildId);
            await state.MasterQueueLock.WaitAsync(ct);
            try
            {
                if (state.MasterQueue.Count == 0) return "📋 Очередь пуста.";

                // Перемешиваем всю MasterQueue целиком (история + текущий + будущие)
                var rng = new Random();
                var all = state.MasterQueue.ToList();
                for (int i = all.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (all[i], all[j]) = (all[j], all[i]);
                }
                state.MasterQueue.Clear();
                state.MasterQueue.AddRange(all);
                state.RenumberMasterQueue();

                // Текущий трек ищем по URL в новом порядке
                var currentUrl = player.CurrentItem?.Track?.Uri?.ToString();
                if (currentUrl is not null)
                {
                    var idx = state.MasterQueue.FindIndex(e =>
                        string.Equals(e.Url, currentUrl, StringComparison.OrdinalIgnoreCase));
                    state.MasterCurrentIndex = idx >= 0 ? idx : 0;
                }
                else
                {
                    state.MasterCurrentIndex = 0;
                }

                // Обновляем Lavalink-очередь: треки после текущего
                for (int i = player.Queue.Count - 1; i >= 0; i--)
                    await player.Queue.RemoveAtAsync(i, ct);

                var future = state.MasterQueue.Skip(state.MasterCurrentIndex + 1).ToList();
                foreach (var entry in future)
                {
                                    if (_audioService is null) break;
                                    var loaded = await _audioService.Tracks.LoadTrackAsync(entry.Url, TrackSearchMode.None, cancellationToken: ct);
                                    if (loaded is not null)
                                        await player.Queue.AddAsync(new TrackQueueItem(loaded), ct);
                                }
            }
            finally { state.MasterQueueLock.Release(); }

            return "🔀 Вся очередь перемешана.";
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
            var state = GetOrCreateState(guildId);
            await state.MasterQueueLock.WaitAsync(ct);
            try
            {
                // Возвращаем всю очередь целиком: история + текущий + следующие
                return state.MasterQueue
                    .Select(e => e.Url)
                    .Where(u => !string.IsNullOrEmpty(u))
                    .ToList();
            }
            finally { state.MasterQueueLock.Release(); }
        }

        /// <summary>
        /// Подключается к голосовому каналу пользователя (если ещё не подключён)
        /// и ждёт пока Lavalink установит сессию (VOICE_SERVER_UPDATE).
        /// Возвращает null при ошибке.
        /// </summary>
        public async Task<NotifyingPlayer?> EnsureJoinedAsync(
            SocketGuildUser user,
            CancellationToken cancellationToken = default)
        {
            if (_audioService is null || user.VoiceChannel is null)
                return null;

            var guildId = user.Guild.Id;

            NotifyingPlayer? player;
            try { player = await _audioService.Players.GetPlayerAsync<NotifyingPlayer>(guildId, cancellationToken); }
            catch { player = null; }

            bool freshJoin = player is null;

            if (player is null)
            {
                try
                {
                    // Используем надёжный helper: ждём реальное событие от Discord
                    // о voice state бота (с явным таймаутом 15с), параллельно запустив
                    // JoinAsync на отдельном потоке, чтобы gateway мог обрабатывать события.
                    player = await JoinAndAwaitVoiceAsync(user, TimeSpan.FromSeconds(15), cancellationToken);
                    if (player is null)
                    {
                        Log($"[Music] EnsureJoinedAsync: не дождались voice state от Discord");
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Music] EnsureJoinedAsync: ошибка JoinAndAwaitVoice — {ex.Message}");
                    return null;
                }
            }

            // После нового подключения ждём пока Lavalink получит VOICE_SERVER_UPDATE от Discord
            // и установит аудио-сессию (обычно 300–800 мс, ждём до 5 сек).
            if (freshJoin)
            {
                await Task.Delay(500, cancellationToken);
                for (int i = 0; i < 18; i++)
                {
                    if (player.State != PlayerState.Destroyed)
                        break;
                    await Task.Delay(250, cancellationToken);
                }
                Log($"[Music] EnsureJoinedAsync: плеер готов (state={player.State})");

                // Восстанавливаем сохранённую громкость после подключения
                var savedVolume = GetVolume(guildId);
                try { await player.SetVolumeAsync(savedVolume / 100f, cancellationToken); }
                catch (Exception ex) { Log($"[Music] EnsureJoinedAsync: не удалось установить громкость — {ex.Message}"); }
            }

            return player;
        }

        /// <summary>Останавливает воспроизведение и отключает бота.</summary>
        public async Task<string> StopAsync(ulong guildId, CancellationToken cancellationToken = default)
        {
            var player = await GetPlayerAsync(guildId, cancellationToken);
            if (player is null) return "❌ Ничего не играет.";

            await player.StopAsync(cancellationToken);
            await player.DisconnectAsync(cancellationToken);

            var state = GetOrCreateState(guildId);
            await state.MasterQueueLock.WaitAsync(cancellationToken);
            try { state.MasterQueue.Clear(); state.MasterCurrentIndex = -1; }
            finally { state.MasterQueueLock.Release(); }

            return "⏹ Воспроизведение остановлено.";
        }

        /// <summary>
        /// Останавливает текущий трек и очищает очередь, НЕ отключаясь от канала.
        /// Используется перед загрузкой нового плейлиста.
        /// </summary>
        public async Task StopPlaybackOnlyAsync(ulong guildId, CancellationToken cancellationToken = default)
        {
            var player = await GetPlayerAsync(guildId, cancellationToken);
            if (player is null) return;

            await player.StopAsync(cancellationToken);
            for (int i = player.Queue.Count - 1; i >= 0; i--)
                try { await player.Queue.RemoveAtAsync(i, cancellationToken); } catch { break; }

            var state = GetOrCreateState(guildId);
            await state.MasterQueueLock.WaitAsync(cancellationToken);
            try { state.MasterQueue.Clear(); state.MasterCurrentIndex = -1; }
            finally { state.MasterQueueLock.Release(); }
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

            // Если прошло >15 секунд — перемотать в начало (только для кнопки ⏮)
            if (elapsed.TotalSeconds > 15)
            {
                await player.SeekAsync(TimeSpan.Zero, cancellationToken);
                state.TrackStartedAtUtc = DateTime.UtcNow;
                return "⏮ Перемотано в начало трека.";
            }

            int cur = state.MasterCurrentIndex;
            if (cur <= 0)
            {
                await player.SeekAsync(TimeSpan.Zero, cancellationToken);
                state.TrackStartedAtUtc = DateTime.UtcNow;
                return "⏮ Первый трек, перемотано в начало.";
            }

            return await GoToMasterIndexAsync(player, state, cur - 1, cancellationToken);
        }

        /// <summary>Переходит к следующему треку.</summary>
        public async Task<string> PlaylistNextAsync(ulong guildId, CancellationToken cancellationToken = default)
        {
            var player = await GetPlayerAsync(guildId, cancellationToken);
            if (player is null) return "❌ Ничего не играет.";

            var state = GetOrCreateState(guildId);
            int cur = state.MasterCurrentIndex;

            if (cur < 0 || cur >= state.MasterQueue.Count - 1)
                return "⏭ Это последний трек.";

            // Lavalink сам возьмёт следующий из очереди
            await player.SkipAsync(cancellationToken: cancellationToken);
            return "⏭ Следующий трек.";
        }

        /// <summary>Переходит к треку с указанным номером (Number) из MasterQueue. Без проверки 15 сек.</summary>
        public async Task<string> GoToTrackNumberAsync(ulong guildId, int trackNumber, CancellationToken cancellationToken = default)
        {
            var player = await GetPlayerAsync(guildId, cancellationToken);
            if (player is null) return "❌ Ничего не играет.";

            var state = GetOrCreateState(guildId);
            var idx = state.MasterQueue.FindIndex(e => e.Number == trackNumber);
            if (idx < 0) return $"❌ Трек #{trackNumber} не найден в очереди.";
            if (idx == state.MasterCurrentIndex) return $"▶️ Трек #{trackNumber} уже играет.";

            return await GoToMasterIndexAsync(player, state, idx, cancellationToken);
        }

        /// <summary>Общая логика перехода к треку по индексу в MasterQueue.</summary>
        private async Task<string> GoToMasterIndexAsync(
            NotifyingPlayer player, MusicPlayerState state, int targetIndex,
            CancellationToken cancellationToken)
        {
            var target = state.MasterQueue[targetIndex];
            var track  = await _audioService!.Tracks.LoadTrackAsync(
                target.Url, TrackSearchMode.None, cancellationToken: cancellationToken);
            if (track is null) return $"❌ Не удалось загрузить трек «{target.Title}».";

            // Очищаем Lavalink-очередь
            for (int i = player.Queue.Count - 1; i >= 0; i--)
                try { await player.Queue.RemoveAtAsync(i, cancellationToken); } catch { break; }

            await player.Queue.InsertAsync(0, new TrackQueueItem(track), cancellationToken);
            await player.SkipAsync(cancellationToken: cancellationToken);

            // Добавляем треки после target в Lavalink-очередь фоново
            _ = Task.Run(async () =>
            {
                await state.MasterQueueLock.WaitAsync();
                List<string> afterUrls;
                try
                {
                    afterUrls = state.MasterQueue
                        .Skip(targetIndex + 1)
                        .Select(e => e.Url)
                        .ToList();
                }
                finally
                {
                    state.MasterQueueLock.Release();
                }

                foreach (var url in afterUrls)
                {
                    try
                    {
                        var t = await _audioService!.Tracks.LoadTrackAsync(url, TrackSearchMode.None);
                        if (t is not null) await player.Queue.AddAsync(new TrackQueueItem(t));
                    }
                    catch { /* пропускаем недоступный трек */ }
                }
            });

            return $"⏩ Перехожу к треку **#{target.Number}**: {target.Title}";
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

            var state = GetOrCreateState(guildId);

            await state.MasterQueueLock.WaitAsync(cancellationToken);
            string result;
            try
            {
                var idx = state.MasterQueue.FindIndex(e => e.Number == number);
                if (idx < 0) return $"❌ Трек #{number} не найден.";
                if (idx == state.MasterCurrentIndex) return "❌ Нельзя удалить текущий трек.";

                var removed = state.MasterQueue[idx];
                state.MasterQueue.RemoveAt(idx);
                // Корректируем указатель, если удалили трек перед текущим
                if (idx < state.MasterCurrentIndex)
                    state.MasterCurrentIndex--;
                state.RenumberMasterQueue();

                // Синхронизируем Lavalink-очередь: удаляем трек по позиции относительно current
                // Lavalink-очередь содержит треки ПОСЛЕ текущего; индекс в ней = idx - (MasterCurrentIndex + 1)
                int lavalinkIdx = idx - (state.MasterCurrentIndex + 1);
                if (lavalinkIdx >= 0 && lavalinkIdx < player.Queue.Count)
                {
                    try { await player.Queue.RemoveAtAsync(lavalinkIdx, cancellationToken); } catch { }
                }

                result = $"🗑️ Удалён: **{(removed.Title.Length > 60 ? removed.Title[..60] + "…" : removed.Title)}**";
            }
            finally { state.MasterQueueLock.Release(); }

            return result;
        }

        public async Task<string> GetQueueInfoAsync(ulong guildId)
        {
            var player = await GetPlayerAsync(guildId);
            if (player is null) return "❌ Ничего не играет.";
            if (player.CurrentItem is null) return "❌ Ничего не играет.";

            var state = GetOrCreateState(guildId);
            var sb = new System.Text.StringBuilder();

            await state.MasterQueueLock.WaitAsync();
            try
            {
                int cur = state.MasterCurrentIndex;
                if (cur < 0 || state.MasterQueue.Count == 0)
                {
                    sb.AppendLine($"▶️ Сейчас: **{player.CurrentItem.Track?.Title ?? "?"}**");
                    sb.AppendLine("📋 Очередь пуста.");
                    return sb.ToString().TrimEnd();
                }

                var current = state.MasterQueue[cur];
                sb.AppendLine($"▶️ **#{current.Number}** **{current.Title}** `{FormatDuration(current.Duration)}`");

                // до 3 треков истории
                int histStart = Math.Max(0, cur - 3);
                if (histStart < cur)
                {
                    sb.AppendLine("— история:");
                    for (int i = histStart; i < cur; i++)
                    {
                        var e = state.MasterQueue[i];
                        sb.AppendLine($"  — #{e.Number} {(e.Title.Length > 50 ? e.Title[..50] + "…" : e.Title)}");
                    }
                }

                // до 5 следующих треков
                int futureEnd = Math.Min(state.MasterQueue.Count, cur + 6);
                if (cur + 1 < state.MasterQueue.Count)
                {
                    sb.AppendLine($"📋 В очереди: {state.MasterQueue.Count - cur - 1} треков:");
                    for (int i = cur + 1; i < futureEnd; i++)
                    {
                        var e = state.MasterQueue[i];
                        sb.AppendLine($"  #{e.Number} {(e.Title.Length > 50 ? e.Title[..50] + "…" : e.Title)}");
                    }
                    if (futureEnd < state.MasterQueue.Count)
                        sb.AppendLine("  ...");
                }
                else
                {
                    sb.AppendLine("📋 Больше треков нет.");
                }
            }
            finally { state.MasterQueueLock.Release(); }

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
                    // В startup-режиме (sink установлен) пишем ТОЛЬКО в рендерер.
                    // BotLogger.Info параллельно шлёт в UI через SetUiSink и в файл Music.log —
                    // получили бы дубль. После завершения старта (sink == null) возвращаемся
                    // к обычному пути: BotLogger пишет в файл и UI, в консоль НЕ выводим.
                    if (StartupLogSink != null)
                    {
                        StartupLogSink.Invoke(message);
                        return;
                    }
                    BotLogger.Info(LogCategory.Music, message);
                }

        /// <summary>
                /// Когда установлен — Log() пишет сюда вместо консоли.
                /// Program.cs устанавливает перед LaunchProcessAsync и забирает после,
                /// включая собранные строки в startup-бокс этапа.
                /// </summary>
                public Action<string>? StartupLogSink { get; set; }

                /// <summary>Дополнительный sink для записи в файл (назначается из Program.cs).</summary>
                public Action<string>? FileSink { get; set; }

                                /// <summary>Когда true — Log() внутри PrepareAsync не пишет строку «DI-контейнер собран».
                                /// Program.cs включает это во время блока ЗАПУСК, чтобы строка появилась уже в Этапе 4
                                /// (рядом с LaunchProcessAsync), а не дрейфовала между блоком ЗАПУСК и Этапом 4.</summary>
                                public bool SuppressPrepareLog { get; set; }

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
