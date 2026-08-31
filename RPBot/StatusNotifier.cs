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
        private readonly Func<ulong, ServerConfig?> _getServerConfig;
        private StartupType _lastStartupType = StartupType.FirstStart;
        private string? _lastStartupReason;

        public Action<string>? LogSink { get; set; }

        public StatusNotifier(DiscordSocketClient client, Func<ulong, ServerConfig?> getServerConfig)
        {
            _client = client;
            _getServerConfig = getServerConfig;
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
                if (_getServerConfig(guild.Id) is { } config)
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
        public async Task<bool> SendSystemsActiveToGuild(SocketGuild guild, ServerConfig config, string reason, List<SystemHealthCheck>? healthChecks = null)
        {
            try
            {
                var channel = await _client.GetChannelAsync(config.ModerateChannelID) as ITextChannel;
                if (channel == null) return true; // nothing to do — treat as success

                var startupType = GetStartupTypeDisplay();
                var startupReason = GetStartupReasonDisplay(reason);

                // Определяем цвет на основе результатов проверки
                var allHealthy = healthChecks == null || healthChecks.All(c => c.IsHealthy);
                var embedColor = allHealthy ? Color.Green : Color.Orange;
                var title = allHealthy ? "🟢 ВСЕ СИСТЕМЫ АКТИВНЫ" : "🟡 СИСТЕМЫ ЗАПУЩЕНЫ С ПРЕДУПРЕЖДЕНИЯМИ";

                var embed = new EmbedBuilder()
                    .WithTitle(title)
                    .WithColor(embedColor)
                    .WithDescription($"Бот {_client.CurrentUser.Username} запущен.")
                    .AddField("📊 Статус", allHealthy ? "✅ Онлайн" : "⚠️ Онлайн (есть проблемы)", true)
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

                // Добавляем результаты проверки систем
                if (healthChecks != null && healthChecks.Count > 0)
                {
                    var healthSummary = new StringBuilder();
                    foreach (var check in healthChecks)
                    {
                        var icon = check.IsHealthy ? "✅" : "❌";
                        // Берём только первую строку для краткости
                        var msg = check.Message.Split('\n')[0];
                        // Ограничиваем длину для красивого отображения
                        if (msg.Length > 50)
                            msg = msg.Substring(0, 47) + "...";
                        healthSummary.AppendLine($"{icon} {check.SystemName}");
                    }

                    embed.AddField("🔍 Проверка систем", healthSummary.ToString(), false);
                }

                await channel.SendMessageAsync(embed: embed.Build());
                return true;
            }
            catch (Exception ex)
            {
                BotLogger.Warn(LogCategory.Discord, $"[StatusNotifier] Ошибка отправки статуса на {guild.Name}: {ex.Message}");
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
                if (_getServerConfig(guild.Id) is { } config)
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
    BotLogger.Warn(LogCategory.Discord, $"[StatusNotifier] SendConnectionIssue error for {guild.Name}: {ex.Message}");
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
                if (_getServerConfig(guild.Id) is { } config)
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
    BotLogger.Warn(LogCategory.Discord, $"[StatusNotifier] SendRestartNotification error for {guild.Name}: {ex.Message}");
    }
                }
            }
        }

        public async Task SendReconnectNotification(string reason = "Ручной реконнект по команде")
        {
            foreach (var guild in _client.Guilds)
            {
                if (_getServerConfig(guild.Id) is { } config)
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
    BotLogger.Warn(LogCategory.Discord, $"[StatusNotifier] SendReconnectNotification error for {guild.Name}: {ex.Message}");
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
                if (_getServerConfig(guild.Id) is { } config)
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
                        BotLogger.Warn(LogCategory.Discord, $"[StatusNotifier] SendShutdownNotification error for {guild.Name}: {ex.Message}");
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
                if (_getServerConfig(guild.Id) is { } config)
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
                        BotLogger.Warn(LogCategory.Discord, $"[StatusNotifier] SendReconnectSuccess error for {guild.Name}: {ex.Message}");
                    }
                }
            }
        }
    }
}
