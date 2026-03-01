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

        public StatusNotifier(DiscordSocketClient client, Dictionary<ulong, ServerConfig> serverConfigs)
        {
            _client = client;
            _serverConfigs = serverConfigs;
        }

        public void SetStartupType(StartupType type)
        {
            _lastStartupType = type;
        }

        public async Task SendAllSystemsActive(string reason)
        {
            foreach (var guild in _client.Guilds)
            {
                if (_serverConfigs.TryGetValue(guild.Id, out var config))
                {
                    await SendSystemsActiveToGuild(guild, config, reason);
                }
            }
        }

        /// <summary>
        /// Отправляет сообщение о запуске систем на конкретный сервер
        /// </summary>
        public async Task SendSystemsActiveToGuild(SocketGuild guild, ServerConfig config, string reason)
        {
            try
            {
                var channel = await _client.GetChannelAsync(config.ModerateChannelID) as ITextChannel;
                if (channel == null) return;

                var isReconnect = _lastStartupType == StartupType.Reconnect ||
                                 (_lastStartupType == StartupType.Restart && reason.Contains("переподключение"));

                var embed = new EmbedBuilder()
                    .WithTitle("🟢 ВСЕ СИСТЕМЫ АКТИВНЫ")
                    .WithColor(Color.Green)
                    .WithDescription($"Бот {_client.CurrentUser.Username} успешно запущен и работает в штатном режиме.")
                    .AddField("📊 Статус", "✅ Онлайн", true)
                    .AddField("⏱️ Время", DateTime.Now.ToString("HH:mm:ss"), true)
                    .AddField("🔧 Тип запуска", isReconnect ? "Переподключение" : "Первичный запуск", true)
                    .AddField("📋 Причина", reason, true)
                    .AddField("🔄 Версия", "0.6.0.0", true)
                    .WithFooter(f => f.Text = "Система мониторинга")
                    .WithCurrentTimestamp();

                await channel.SendMessageAsync(embed: embed.Build());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StatusNotifier] Ошибка отправки статуса на {guild.Name}: {ex.Message}");
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
        public async Task SendReconnectSuccess(int attempts, string lastReason)
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
                    catch { }
                }
            }
        }
    }
}
