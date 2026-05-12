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
        private readonly DiscordSocketClient _discordClient;

        private ServiceProvider? _serviceProvider;
        private IAudioService? _audioService;
        private Process? _lavalinkProcess;
        private bool _disposed;

        public Action<string>? LogSink { get; set; }

        public LavalinkService(DiscordSocketClient discordClient, MusicConfig config)
        {
            _discordClient = discordClient;
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
            var psi = new ProcessStartInfo
            {
                FileName = "java",
                Arguments = $"-jar \"{jarPath}\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            Log($"[Music] Запуск Lavalink: java -jar {jarPath}");
            _lavalinkProcess = Process.Start(psi)!;
            _lavalinkProcess.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) Log($"[Lavalink] {e.Data}");
            };
            _lavalinkProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) Log($"[Lavalink][ERR] {e.Data}");
            };
            _lavalinkProcess.BeginOutputReadLine();
            _lavalinkProcess.BeginErrorReadLine();

            await WaitUntilReadyAsync(cancellationToken);
        }

        private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(_config.StartupTimeoutSeconds);
            var url = $"http://{_config.Host}:{_config.Port}/version";

            Log($"[Music] Ожидание готовности Lavalink (до {_config.StartupTimeoutSeconds}с)...");
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var resp = await http.GetAsync(url, cancellationToken);
                    if (resp.IsSuccessStatusCode) { Log("[Music] Lavalink готов ✓"); return; }
                }
                catch { }
                await Task.Delay(1000, cancellationToken);
            }
            Log("[Music] Lavalink не ответил в отведённое время.");
        }

        private void BuildServices()
        {
            var services = new ServiceCollection();

            // Lavalink4NET.DiscordNet требует DiscordSocketClient в DI
            services.AddSingleton(_discordClient);

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
            var voiceChannelId = user.VoiceChannel.Id;

            // Получаем или создаём плеер
            var player = await _audioService.Players.GetPlayerAsync<QueuedLavalinkPlayer>(guildId, cancellationToken);
            if (player is null)
            {
                player = await _audioService.Players.JoinAsync(
                    guildId,
                    voiceChannelId,
                    PlayerFactory.Queued,
                    Options.Create(new QueuedLavalinkPlayerOptions()),
                    cancellationToken);
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

        private void Log(string message) => LogSink?.Invoke(message);

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
        }
    }
}
