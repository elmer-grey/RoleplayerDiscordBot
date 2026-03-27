using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>
    /// Отвечает за отправку уведомлений в Discord каналы
    /// </summary>
    public class StatusNotifier
    {
        private readonly DiscordSocketClient _client;
        private readonly Dictionary<ulong, ServerConfig> _serverConfigs;
        private StartupType _lastStartupType = StartupType.FirstStart;
        private string? _lastStartupReason;

        public Action<string>? LogSink { get; set; }

        public StatusNotifier(DiscordSocketClient client, Dictionary<ulong, ServerConfig> serverConfigs)
        {
            _client = client;
            _serverConfigs = serverConfigs;
        }

        public void SetStartupType(StartupType type)
        {
            _lastStartupType = type;
        }

        public void SetStartupContext(StartupType type, string? reason)
        {
            _lastStartupType = type;
            _lastStartupReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
        }

        private string GetStartupTypeDisplay()
        {
            return _lastStartupType switch
            {
                StartupType.Restart => "Перезапуск",
                StartupType.Reconnect => "Переподключение",
                _ => "Первичный запуск"
            };
        }

        private string? GetStartupReasonDisplay(string fallbackReason)
        {
            if (_lastStartupType == StartupType.FirstStart)
                return null;

            if (!string.IsNullOrWhiteSpace(_lastStartupReason))
                return _lastStartupReason;

            return string.IsNullOrWhiteSpace(fallbackReason) ? null : fallbackReason;
        }

        public async Task<bool> SendAllSystemsActive(string reason)
        {
            var anyFailure = false;
            foreach (var guild in _client.Guilds)
            {
                if (_serverConfigs.TryGetValue(guild.Id, out var config))
                {
                    // Защита: если channel id не задан — пропускаем отправку
                    if (config.ModerateChannelID == 0) continue;
                    var ok = await SendSystemsActiveToGuild(guild, config, reason);
                    if (!ok) anyFailure = true;
                }
            }

            return !anyFailure;
        }

        /// <summary>
        /// Отправляет сообщение о запуске систем на конкретный сервер
        /// </summary>
        public async Task<bool> SendSystemsActiveToGuild(SocketGuild guild, ServerConfig config, string reason)
        {
            try
            {
                var channel = await _client.GetChannelAsync(config.ModerateChannelID) as ITextChannel;
                if (channel == null) return true; // nothing to do — treat as success

                var startupType = GetStartupTypeDisplay();
                var startupReason = GetStartupReasonDisplay(reason);

                var embed = new EmbedBuilder()
                    .WithTitle("🟢 ВСЕ СИСТЕМЫ АКТИВНЫ")
                    .WithColor(Color.Green)
                    .WithDescription($"Бот {_client.CurrentUser.Username} успешно запущен и работает в штатном режиме.")
                    .AddField("📊 Статус", "✅ Онлайн", true)
                    .AddField("⏱️ Время", DateTime.Now.ToString("HH:mm:ss"), true)
                    .AddField("📶 Задержка", $"{_client.Latency} мс", true)
                    .AddField("🔄 Версия", BotConfig.Current?.BotVersion ?? "?", true)
                    .WithFooter(f => f.Text = "Система мониторинга")
                    .WithCurrentTimestamp();

                embed.AddField("🔧 Тип запуска", startupType, true);

                if (!string.IsNullOrWhiteSpace(startupReason))
                {
                    embed.AddField("📋 Причина", startupReason, true);
                }

                await channel.SendMessageAsync(embed: embed.Build());
                return true;
            }
            catch (Exception ex)
            {
                LogSink?.Invoke($"[StatusNotifier] Ошибка отправки статуса на {guild.Name}: {ex.Message}");
                try
                {
                    var logDirRaw = BotConfig.Current?.LogDirectory;
                    var logDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDirRaw) ? "Logs" : logDirRaw);
                    System.IO.Directory.CreateDirectory(logDir);
                    var path = System.IO.Path.Combine(logDir, "ErrorLog.txt");
                    System.IO.File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [StatusNotifier] Ошибка отправки статуса на {guild.Name}: {ex}\n");
                }
                catch { }
                return false;
            }
        }

        /// <summary>
        /// Отправляет сообщение о проблемах с подключением
        /// </summary>
        public async Task SendConnectionIssue(string reason, int attempt)
        {
            foreach (var guild in _client.Guilds)
            {
                if (_serverConfigs.TryGetValue(guild.Id, out var config))
                {
                    try
                    {
                        // Защита: если channel id не задан или равен 0 — пропускаем отправку
                        if (config.ModerateChannelID == 0) continue;
                        var channel = await _client.GetChannelAsync(config.ModerateChannelID) as ITextChannel;
                        if (channel == null) continue;

                        var embed = new EmbedBuilder()
                            .WithTitle("⚠️ ПРОБЛЕМЫ С ПОДКЛЮЧЕНИЕМ")
                            .WithColor(Color.Orange)
                            .WithDescription("Бот испытывает проблемы с подключением к Discord")
                            .AddField("📋 Причина", reason, true)
                            .AddField("🔄 Попытка", attempt.ToString(), true)
                            .AddField("⏱️ Время", DateTime.Now.ToString("HH:mm:ss"), true)
                            .WithFooter(f => f.Text = "Пытаюсь переподключиться...")
                            .WithCurrentTimestamp();

                        await channel.SendMessageAsync(embed: embed.Build());
                    }
                    catch (Exception ex)
                    {
					LogSink?.Invoke($"[StatusNotifier] SendConnectionIssue error for {guild.Name}: {ex.Message}");
					try
					{
						var logDirRaw = BotConfig.Current?.LogDirectory;
						var logDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDirRaw) ? "Logs" : logDirRaw);
						System.IO.Directory.CreateDirectory(logDir);
						var dateSuffix = DateTime.Now.ToString("yyyyMMdd");
						var path = System.IO.Path.Combine(logDir, $"ErrorLog_{dateSuffix}.txt");
						System.IO.File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [StatusNotifier] SendConnectionIssue error for {guild.Name}: {ex}\n");
					}
					catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Отправляет сообщение о перезагрузке бота
        /// </summary>
        public async Task SendRestartNotification(string reason = "Плановая перезагрузка")
        {
            foreach (var guild in _client.Guilds)
            {
                if (_serverConfigs.TryGetValue(guild.Id, out var config))
                {
                    try
                    {
                        if (config.ModerateChannelID == 0) continue;
                        var channel = await _client.GetChannelAsync(config.ModerateChannelID) as ITextChannel;
                        if (channel == null) continue;

                        var embed = new EmbedBuilder()
                            .WithTitle("🔄 ПЕРЕЗАГРУЗКА БОТА")
                            .WithColor(Color.Purple)
                            .WithDescription("Бот перезагружается... Это займёт несколько секунд")
                            .AddField("📋 Причина", reason, true)
                            .AddField("⏱️ Время", DateTime.Now.ToString("HH:mm:ss"), true)
                            .WithFooter(f => f.Text = "Ожидайте восстановления связи")
                            .WithCurrentTimestamp();

                        await channel.SendMessageAsync(embed: embed.Build());
                    }
                    catch (Exception ex)
                    {
					LogSink?.Invoke($"[StatusNotifier] SendRestartNotification error for {guild.Name}: {ex.Message}");
					try
					{
						var logDirRaw = BotConfig.Current?.LogDirectory;
						var logDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDirRaw) ? "Logs" : logDirRaw);
						System.IO.Directory.CreateDirectory(logDir);
						var dateSuffix = DateTime.Now.ToString("yyyyMMdd");
						var path = System.IO.Path.Combine(logDir, $"ErrorLog_{dateSuffix}.txt");
						System.IO.File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [StatusNotifier] SendRestartNotification error for {guild.Name}: {ex}\n");
					}
					catch { }
                    }
                }
            }
        }

        public async Task SendReconnectNotification(string reason = "Ручной реконнект по команде")
        {
            foreach (var guild in _client.Guilds)
            {
                if (_serverConfigs.TryGetValue(guild.Id, out var config))
                {
                    try
                    {
                        if (config.ModerateChannelID == 0) continue;
                        var channel = await _client.GetChannelAsync(config.ModerateChannelID) as ITextChannel;
                        if (channel == null) continue;

                        var embed = new EmbedBuilder()
                            .WithTitle("🔄 ПЕРЕПОДКЛЮЧЕНИЕ БОТА")
                            .WithColor(Color.Blue)
                            .WithDescription("Бот выполняет переподключение к Discord.")
                            .AddField("📋 Причина", reason, true)
                            .AddField("⏱️ Время", DateTime.Now.ToString("HH:mm:ss"), true)
                            .WithFooter(f => f.Text = "Ожидайте восстановления связи")
                            .WithCurrentTimestamp();

                        await channel.SendMessageAsync(embed: embed.Build());
                    }
                    catch (Exception ex)
                    {
					LogSink?.Invoke($"[StatusNotifier] SendReconnectNotification error for {guild.Name}: {ex.Message}");
					try
					{
						var logDirRaw = BotConfig.Current?.LogDirectory;
						var logDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDirRaw) ? "Logs" : logDirRaw);
						System.IO.Directory.CreateDirectory(logDir);
						var dateSuffix = DateTime.Now.ToString("yyyyMMdd");
						var path = System.IO.Path.Combine(logDir, $"ErrorLog_{dateSuffix}.txt");
						System.IO.File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [StatusNotifier] SendReconnectNotification error for {guild.Name}: {ex}\n");
					}
					catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Отправляет сообщение о полном выключении бота
        /// </summary>
        public async Task SendShutdownNotification(string reason = "Плановое завершение работы")
        {
            foreach (var guild in _client.Guilds)
            {
                if (_serverConfigs.TryGetValue(guild.Id, out var config))
                {
                    try
                    {
                        if (config.ModerateChannelID == 0) continue;
                        var channel = await _client.GetChannelAsync(config.ModerateChannelID) as ITextChannel;
                        if (channel == null) continue;

                        var embed = new EmbedBuilder()
                            .WithTitle("⏹️ БОТ ОСТАНОВЛЕН")
                            .WithColor(Color.DarkGrey)
                            .WithDescription("Бот завершил работу и сейчас находится офлайн.")
                            .AddField("📋 Причина", reason, true)
                            .AddField("📊 Статус", "⛔ Оффлайн", true)
                            .AddField("⏱️ Время", DateTime.Now.ToString("HH:mm:ss"), true)
                            .WithFooter(f => f.Text = "Система мониторинга")
                            .WithCurrentTimestamp();

                        await channel.SendMessageAsync(embed: embed.Build());
                    }
                    catch (Exception ex)
                    {
                        LogSink?.Invoke($"[StatusNotifier] SendShutdownNotification error for {guild.Name}: {ex.Message}");
                        try
                        {
                            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt");
                            System.IO.File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [StatusNotifier] SendShutdownNotification error for {guild.Name}: {ex}\n");
                        }
                        catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Отправляет сообщение об успешном переподключении
        /// </summary>
        public async Task SendReconnectSuccess(int attempts, string lastReason)
        {
            foreach (var guild in _client.Guilds)
            {
                if (_serverConfigs.TryGetValue(guild.Id, out var config))
                {
                    try
                    {
                        if (config.ModerateChannelID == 0) continue;
                        var channel = await _client.GetChannelAsync(config.ModerateChannelID) as ITextChannel;
                        if (channel == null) continue;

                        var embed = new EmbedBuilder()
                            .WithTitle("🔄 ПЕРЕПОДКЛЮЧЕНИЕ УСПЕШНО")
                            .WithColor(Color.Blue)
                            .WithDescription("Бот успешно переподключился к Discord")
                            .AddField("📊 Статус", "✅ Онлайн", true)
                            .AddField("🔄 Попыток", attempts.ToString(), true)
                            .AddField("⚠️ Последняя причина", lastReason, true)
                            .AddField("⏱️ Время", DateTime.Now.ToString("HH:mm:ss"), true)
                            .WithFooter(f => f.Text = "Соединение восстановлено")
                            .WithCurrentTimestamp();

                        await channel.SendMessageAsync(embed: embed.Build());
                    }
                    catch (Exception ex)
                    {
                        LogSink?.Invoke($"[StatusNotifier] SendReconnectSuccess error for {guild.Name}: {ex.Message}");
                        try
                        {
                            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt");
                            System.IO.File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [StatusNotifier] SendReconnectSuccess error for {guild.Name}: {ex}\n");
                        }
                        catch { }
                    }
                }
            }
        }
    }
}
