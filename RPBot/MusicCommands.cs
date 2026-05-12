using Discord.WebSocket;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>
    /// Обработчик команды /music — единая точка входа для всех музыкальных действий.
    /// </summary>
    public class MusicCommands
    {
        private readonly LavalinkService _lavalink;

        public Action<string>? LogSink { get; set; }

        public MusicCommands(LavalinkService lavalink)
        {
            _lavalink = lavalink;
        }

        /// <summary>
        /// Диспетчер команды /music action:[играть|стоп|пауза|продолжить|пропустить|очередь]
        /// </summary>
        public async Task HandleMusicAsync(SocketSlashCommand command)
        {
            // DeferAsync должен быть на gateway потоке — сразу отвечаем Discord что запрос принят
            await command.DeferAsync();

            // Дальнейшее выполнение — в отдельном потоке, чтобы не блокировать gateway
            // (JoinAsync ждёт VoiceStateUpdate от gateway — иначе дедлок)
            _ = Task.Run(async () =>
            {
                var action = command.Data.Options
                    .FirstOrDefault(o => o.Name == "action")?.Value as string ?? "";

                switch (action)
                {
                    case "play":   await HandlePlayInternalAsync(command);   break;
                    case "stop":   await HandleStopInternalAsync(command);   break;
                    case "pause":  await HandlePauseInternalAsync(command);  break;
                    case "resume": await HandleResumeInternalAsync(command); break;
                    case "skip":   await HandleSkipInternalAsync(command);   break;
                    case "queue":  await HandleQueueInternalAsync(command);  break;
                    default:
                        await command.FollowupAsync("❌ Неизвестное действие.", ephemeral: true);
                        break;
                }
            });
        }

        // ─── играть ──────────────────────────────────────────────────────

        private async Task HandlePlayInternalAsync(SocketSlashCommand command)
        {
            var user = command.User as SocketGuildUser;
            if (user is null) { await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true); return; }

            var url = command.Data.Options.FirstOrDefault(o => o.Name == "url")?.Value as string ?? "";
            if (string.IsNullOrWhiteSpace(url))
            {
                await command.FollowupAsync("❌ Укажи ссылку: `/music action:играть url:<ссылка>`", ephemeral: true);
                return;
            }

            try
            {
                var result = await _lavalink.PlayAsync(user, url);
                await command.FollowupAsync(result);
            }
            catch (Exception ex)
            {
                Log($"[Music] Ошибка play: {ex.Message}");
                await command.FollowupAsync($"❌ Ошибка воспроизведения: {ex.Message}", ephemeral: true);
            }
        }

        // ─── стоп ─────────────────────────────────────────────────────────

        private async Task HandleStopInternalAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null) { await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true); return; }

            try { await command.FollowupAsync(await _lavalink.StopAsync(guildId.Value)); }
            catch (Exception ex) { Log($"[Music] Ошибка stop: {ex.Message}"); await command.FollowupAsync($"❌ {ex.Message}", ephemeral: true); }
        }

        // ─── пауза ────────────────────────────────────────────────────────

        private async Task HandlePauseInternalAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null) { await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true); return; }

            try { await command.FollowupAsync(await _lavalink.PauseAsync(guildId.Value)); }
            catch (Exception ex) { Log($"[Music] Ошибка pause: {ex.Message}"); await command.FollowupAsync($"❌ {ex.Message}", ephemeral: true); }
        }

        // ─── продолжить ───────────────────────────────────────────────────

        private async Task HandleResumeInternalAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null) { await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true); return; }

            try { await command.FollowupAsync(await _lavalink.ResumeAsync(guildId.Value)); }
            catch (Exception ex) { Log($"[Music] Ошибка resume: {ex.Message}"); await command.FollowupAsync($"❌ {ex.Message}", ephemeral: true); }
        }

        // ─── пропустить ───────────────────────────────────────────────────

        private async Task HandleSkipInternalAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null) { await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true); return; }

            try { await command.FollowupAsync(await _lavalink.SkipAsync(guildId.Value)); }
            catch (Exception ex) { Log($"[Music] Ошибка skip: {ex.Message}"); await command.FollowupAsync($"❌ {ex.Message}", ephemeral: true); }
        }

        // ─── очередь ─────────────────────────────────────────────────────

        private async Task HandleQueueInternalAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null) { await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true); return; }

            try { await command.FollowupAsync(await _lavalink.GetQueueInfoAsync(guildId.Value)); }
            catch (Exception ex) { Log($"[Music] Ошибка queue: {ex.Message}"); await command.FollowupAsync($"❌ {ex.Message}", ephemeral: true); }
        }

        // ─── Вспомогательные ─────────────────────────────────────────────

        private static ulong? GetGuildId(SocketSlashCommand command)
            => (command.Channel as SocketGuildChannel)?.Guild.Id;

        private void Log(string message) => LogSink?.Invoke(message);
    }
}
