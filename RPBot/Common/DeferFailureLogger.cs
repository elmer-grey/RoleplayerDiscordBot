using System;
using Discord.WebSocket;
using RPBot;

namespace RPBot.Common
{
    /// <summary>
    /// Подробное логирование ошибок <c>DeferAsync</c> для slash-команд и компонентов.
    ///
    /// <para>Discord требует ACK (первый ответ, обычно <c>DeferAsync</c>) в течение
    /// 3 секунд после interaction; иначе interaction отваливается. Ошибки бывают:</para>
    ///
    /// <list type="bullet">
    ///   <item><b>TimeoutException</b> — бот не успел ответить (долгий старт, GC/IO-пауза).</item>
    ///   <item><b>HttpException 404 / code 10062 (Unknown interaction)</b> — interaction
    ///   уже отвалился по таймауту, либо пользователь нажал дважды, либо бот
    ///   перезапустился между нажатием и нашим ответом.</item>
    ///   <item>Прочие сетевые/IO — теряется связь с Discord.</item>
    /// </list>
    ///
    /// <para>Игроков эти случаи сильно волнуют («нажал — ничего не произошло»), и нам
    /// важно по логу сразу понимать: какая команда, кто вызвал, в каком канале/гильдии,
    /// какая была гипотеза причины. Раньше каждый модуль (RollDice, InfoCommands,
    /// ModerationCommands, MusicCommands, GameSession) логировал по-своему — где-то
    /// пустое <c>ex.Message</c>, где-то вообще swallow с <c>return;</c>. Этот
    /// хелпер приводит логи к единому виду (audit bug #10).</para>
    /// </summary>
    internal static class DeferFailureLogger
    {
        public static void Log(string commandName, Exception ex, SocketSlashCommand command, string? input = null)
        {
            var userId = command.User?.Id ?? 0;
            var userName = command.User?.GlobalName ?? command.User?.Username ?? "<unknown>";
            ulong guildId = 0;
            ulong channelId = 0;
            try
            {
                guildId = (command.Channel as SocketGuildChannel)?.Guild.Id ?? 0;
                channelId = command.Channel?.Id ?? 0;
            }
            catch { /* контекст — best effort */ }

            string hint = ex switch
            {
                TimeoutException =>
                    "Discord требует ACK в течение 3 секунд. Скорее всего GC/IO-пауза или долгий старт обработчика; бот опоздал ответить.",
                Discord.Net.HttpException httpEx when httpEx.HttpCode == System.Net.HttpStatusCode.NotFound
                    => "Discord вернул 404: interaction уже отвалился (10062 Unknown interaction). Обычно это рестарт бота между нажатием и ответом, либо пользователь нажал дважды.",
                _ => "Прочее исключение; см. стек ниже."
            };

            BotLogger.Error(LogCategory.Cmd,
                $"[{commandName}:DeferAsync] failed user={userName}({userId}) guild={guildId} channel={channelId} " +
                $"interaction={command.Id} input={input ?? "-"} {ex.GetType().Name}: {ex.Message} | hint: {hint}");
        }

        public static void Log(string commandName, Exception ex, SocketMessageComponent component, string? customId = null)
        {
            var userId = component.User?.Id ?? 0;
            var userName = component.User?.GlobalName ?? component.User?.Username ?? "<unknown>";
            ulong guildId = 0;
            ulong channelId = 0;
            try
            {
                guildId = (component.Channel as SocketGuildChannel)?.Guild.Id ?? 0;
                channelId = component.Channel?.Id ?? 0;
            }
            catch { /* контекст — best effort */ }

            string hint = ex switch
            {
                TimeoutException =>
                    "Discord требует ACK в течение 3 секунд. Скорее всего GC/IO-пауза или долгий старт обработчика; бот опоздал ответить.",
                Discord.Net.HttpException httpEx when httpEx.HttpCode == System.Net.HttpStatusCode.NotFound
                    => "Discord вернул 404: interaction уже отвалился (10062 Unknown interaction). Обычно это рестарт бота между нажатием и ответом, либо пользователь нажал дважды.",
                _ => "Прочее исключение; см. стек ниже."
            };

            BotLogger.Error(LogCategory.Cmd,
                $"[{commandName}:DeferAsync] failed (component) user={userName}({userId}) guild={guildId} channel={channelId} " +
                $"interaction={component.Id} customId={customId ?? "<unknown>"} {ex.GetType().Name}: {ex.Message} | hint: {hint}");
        }

        public static void Log(string commandName, Exception ex, SocketModal modal, string? customId = null)
        {
            var userId = modal.User?.Id ?? 0;
            var userName = modal.User?.GlobalName ?? modal.User?.Username ?? "<unknown>";
            ulong guildId = 0;
            ulong channelId = 0;
            try
            {
                guildId = (modal.Channel as SocketGuildChannel)?.Guild.Id ?? 0;
                channelId = modal.Channel?.Id ?? 0;
            }
            catch { /* контекст — best effort */ }

            string hint = ex switch
            {
                TimeoutException =>
                    "Discord требует ACK в течение 3 секунд.",
                Discord.Net.HttpException httpEx when httpEx.HttpCode == System.Net.HttpStatusCode.NotFound
                    => "Discord вернул 404: interaction уже отвалился (10062). Обычно это рестарт бота между submit и ответом, либо пользователь нажал дважды.",
                _ => "Прочее исключение; см. стек ниже."
            };

            BotLogger.Error(LogCategory.Cmd,
                $"[{commandName}:DeferAsync] failed (modal) user={userName}({userId}) guild={guildId} channel={channelId} " +
                $"interaction={modal.Id} customId={customId ?? "<unknown>"} {ex.GetType().Name}: {ex.Message} | hint: {hint}");
        }
    }
}
