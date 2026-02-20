using Discord;
using Discord.WebSocket;
using DiscordBot;
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

        // Флаг, чтобы не спамить при множественных реконнектах
        private DateTime _lastStatusMessageTime = DateTime.MinValue;
        private readonly TimeSpan _statusCooldown = TimeSpan.FromSeconds(30);

        public StatusNotifier(DiscordSocketClient client, Dictionary<ulong, ServerConfig> serverConfigs)
        {
            _client = client;
            _serverConfigs = serverConfigs;
        }

        /// <summary>
        /// Отправляет сообщение "Все системы активны" во все настроенные каналы
        /// </summary>
        public async Task SendAllSystemsActive(string additionalInfo = "")
        {
            // Защита от спама
            if ((DateTime.UtcNow - _lastStatusMessageTime) < _statusCooldown)
            {
                Console.WriteLine($"[NOTIFIER] Пропускаем статус (кулдаун)");
                return;
            }

            _lastStatusMessageTime = DateTime.UtcNow;

            foreach (var guild in _client.Guilds)
            {
                if (_serverConfigs.TryGetValue(guild.Id, out var config))
                {
                    await SendSystemsActiveToGuild(guild, config, additionalInfo);
                }
            }
        }

        /// <summary>
        /// Отправляет сообщение о запуске систем на конкретный сервер
        /// </summary>
        public async Task SendSystemsActiveToGuild(SocketGuild guild, ServerConfig config, string additionalInfo)
        {
            try
            {
                var channel = await _client.GetChannelAsync(config.ModerateChannelID) as ITextChannel;
                if (channel == null)
                {
                    Console.WriteLine($"[NOTIFIER] Канал {config.ModerateChannelID} не найден на сервере {guild.Name}");
                    return;
                }

                var embed = new EmbedBuilder()
                    .WithTitle("🟢 СИСТЕМЫ АКТИВНЫ")
                    .WithColor(Color.Green)
                    .WithDescription("Бот работает в штатном режиме и ожидает команд")
                    .AddField("🤖 Бот", _client.CurrentUser?.Username ?? "N/A", true)
                    .AddField("⏱️ Время", DateTime.Now.ToString("HH:mm:ss"), true)
                    .AddField("📊 Статус", "🟢 Онлайн", true)
                    .AddField("🔌 Соединение", "✅ Стабильное", true);

                if (!string.IsNullOrEmpty(additionalInfo))
                {
                    embed.AddField("📋 Информация", additionalInfo, false);
                }

                // Добавляем статистику если доступна
                if (_client.Latency > 0)
                {
                    var latencyColor = _client.Latency < 200 ? "🟢" : _client.Latency < 500 ? "🟡" : "🔴";
                    embed.AddField("📶 Задержка", $"{latencyColor} {_client.Latency} мс", true);
                }

                embed.WithFooter(f => f.Text = $"ID: {_client.CurrentUser?.Id}")
                     .WithCurrentTimestamp();

                await channel.SendMessageAsync(embed: embed.Build());

                Console.WriteLine($"[NOTIFIER] Статус отправлен на {guild.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NOTIFIER] Ошибка отправки на {guild.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Отправляет сообщение о проблемах с подключением
        /// </summary>
        public async Task SendConnectionIssue(string reason, int attempt = 0)
        {
            foreach (var guild in _client.Guilds)
            {
                if (_serverConfigs.TryGetValue(guild.Id, out var config))
                {
                    try
                    {
                        var channel = await _client.GetChannelAsync(config.ModerateChannelID) as ITextChannel;
                        if (channel == null) continue;

                        var embed = new EmbedBuilder()
                            .WithTitle("⚠️ ПРОБЛЕМЫ С ПОДКЛЮЧЕНИЕМ")
                            .WithColor(Color.Orange)
                            .WithDescription("Бот испытывает трудности с подключением к Discord")
                            .AddField("📋 Причина", reason, true)
                            .AddField("🔄 Попытка", attempt > 0 ? attempt.ToString() : "N/A", true)
                            .AddField("⏱️ Время", DateTime.Now.ToString("HH:mm:ss"), true)
                            .WithFooter(f => f.Text = "Пытаюсь переподключиться...")
                            .WithCurrentTimestamp();

                        await channel.SendMessageAsync(embed: embed.Build());
                    }
                    catch { }
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
                    catch { }
                }
            }
        }

        /// <summary>
        /// Отправляет сообщение об успешном переподключении
        /// </summary>
        public async Task SendReconnectSuccess(int attempts, string previousReason)
        {
            foreach (var guild in _client.Guilds)
            {
                if (_serverConfigs.TryGetValue(guild.Id, out var config))
                {
                    try
                    {
                        var channel = await _client.GetChannelAsync(config.ModerateChannelID) as ITextChannel;
                        if (channel == null) continue;

                        var embed = new EmbedBuilder()
                            .WithTitle("✅ ПЕРЕПОДКЛЮЧЕНИЕ УСПЕШНО")
                            .WithColor(Color.Green)
                            .WithDescription("Бот восстановил соединение с Discord")
                            .AddField("🔄 Попыток", attempts.ToString(), true)
                            .AddField("⚠️ Причина отключения", previousReason, true)
                            .AddField("⏱️ Время", DateTime.Now.ToString("HH:mm:ss"), true)
                            .AddField("📊 Статус", "🟢 Системы активны", true)
                            .WithFooter(f => f.Text = "Реконнект выполнен автоматически")
                            .WithCurrentTimestamp();

                        await channel.SendMessageAsync(embed: embed.Build());
                    }
                    catch { }
                }
            }
        }
    }
}
