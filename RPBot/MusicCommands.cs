using Discord;
using Discord.WebSocket;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>
    /// Slash-команды для управления музыкой.
    /// Регистрируются через Program.cs вместе с остальными командами.
    /// </summary>
    public class MusicCommands
    {
        private readonly LavalinkService _lavalink;

        public Action<string>? LogSink { get; set; }

        public MusicCommands(LavalinkService lavalink)
        {
            _lavalink = lavalink;
        }

        // ─── Регистрация команд ───────────────────────────────────────────

        /// <summary>
        /// Возвращает список SlashCommandProperties для регистрации на сервере.
        /// </summary>
        public static SlashCommandProperties[] BuildCommands()
        {
            var play = new SlashCommandBuilder()
                .WithName("play")
                .WithDescription("Воспроизвести трек по ссылке")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("url")
                    .WithDescription("Прямая ссылка на трек (YouTube, SoundCloud, и др.)")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true))
                // Задел: параметр поиска по названию (пока не активен)
                // .AddOption(new SlashCommandOptionBuilder()
                //     .WithName("search")
                //     .WithDescription("Поиск по названию (YouTube)")
                //     .WithType(ApplicationCommandOptionType.String)
                //     .WithRequired(false))
                .Build();

            var stop = new SlashCommandBuilder()
                .WithName("mstop")
                .WithDescription("Остановить музыку и покинуть голосовой канал")
                .Build();

            var skip = new SlashCommandBuilder()
                .WithName("mskip")
                .WithDescription("Пропустить текущий трек")
                .Build();

            var queue = new SlashCommandBuilder()
                .WithName("mqueue")
                .WithDescription("Показать текущий трек и очередь")
                .Build();

            return new[] { play, stop, skip, queue };
        }

        // ─── Обработчики команд ───────────────────────────────────────────

        public async Task HandlePlayAsync(SocketSlashCommand command)
        {
            await command.DeferAsync();

            var user = command.User as SocketGuildUser;
            if (user == null)
            {
                await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true);
                return;
            }

            var url = command.Data.Options
                .FirstOrDefault(o => o.Name == "url")?.Value as string ?? "";
            if (string.IsNullOrWhiteSpace(url))
            {
                await command.FollowupAsync("❌ Укажи ссылку на трек.", ephemeral: true);
                return;
            }

            try
            {
                var result = await _lavalink.PlayAsync(user, url);
                await command.FollowupAsync(result);
            }
            catch (Exception ex)
            {
                Log($"[Music] Ошибка /play: {ex.Message}");
                await command.FollowupAsync($"❌ Ошибка воспроизведения: {ex.Message}", ephemeral: true);
            }
        }

        public async Task HandleStopAsync(SocketSlashCommand command)
        {
            await command.DeferAsync();

            var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId == null)
            {
                await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true);
                return;
            }

            try
            {
                var result = await _lavalink.StopAsync(guildId.Value);
                await command.FollowupAsync(result);
            }
            catch (Exception ex)
            {
                Log($"[Music] Ошибка /stop: {ex.Message}");
                await command.FollowupAsync($"❌ Ошибка: {ex.Message}", ephemeral: true);
            }
        }

        public async Task HandleSkipAsync(SocketSlashCommand command)
        {
            await command.DeferAsync();

            var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId == null)
            {
                await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true);
                return;
            }

            try
            {
                var result = await _lavalink.SkipAsync(guildId.Value);
                await command.FollowupAsync(result);
            }
            catch (Exception ex)
            {
                Log($"[Music] Ошибка /skip: {ex.Message}");
                await command.FollowupAsync($"❌ Ошибка: {ex.Message}", ephemeral: true);
            }
        }

        public async Task HandleQueueAsync(SocketSlashCommand command)
        {
            await command.DeferAsync();

            var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId == null)
            {
                await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true);
                return;
            }

            try
            {
                var result = await _lavalink.GetQueueInfoAsync(guildId.Value);
                await command.FollowupAsync(result);
            }
            catch (Exception ex)
            {
                Log($"[Music] Ошибка /queue: {ex.Message}");
                await command.FollowupAsync($"❌ Ошибка: {ex.Message}", ephemeral: true);
            }
        }

        private void Log(string message) => LogSink?.Invoke(message);
    }
}
