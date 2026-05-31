using Discord;
using Discord.Commands;
using Discord.WebSocket;
using RPBot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>
    /// Изолированное состояние очереди бросков для одной гильдии.
    /// </summary>
    internal sealed class GuildQueueState : IDisposable
    {
        public Dictionary<SocketGuildUser, List<int>> UserRolls { get; } = new();
        public List<IUserMessage> MessagesToDelete { get; } = new();
        public int MaxRolls { get; set; }
        public int RollCount { get; set; }
        public Timer? RollTimer { get; set; }
        public bool IsActive { get; set; }
        public IUserMessage? StartMessage { get; set; }
        public ITextChannel? Channel { get; set; }

        public void Reset()
        {
            MaxRolls = 0;
            RollCount = 0;
            UserRolls.Clear();
            MessagesToDelete.Clear();
            IsActive = false;
            StartMessage = null;
            Channel = null;
        }

        public void Dispose()
        {
            try { RollTimer?.Dispose(); } catch { }
            RollTimer = null;
        }
    }

    public class QueueModule : ModuleBase<SocketCommandContext>
    {
        private static readonly ConcurrentDictionary<ulong, GuildQueueState> _guildQueues = new();

        private static Task Log(string message)
        {
            BotLogger.Info(LogCategory.Cmd, message);
            return Task.CompletedTask;
        }

        private static GuildQueueState GetOrCreateState(ulong guildId)
            => _guildQueues.GetOrAdd(guildId, _ => new GuildQueueState());

        [Command("queue")]
        public async Task QueueCommand(SocketSlashCommand command, int count)
        {
            var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId == null)
            {
                await command.RespondAsync("Команда доступна только на сервере.", ephemeral: true);
                return;
            }

            if (command.User is SocketGuildUser guildUser)
            {
                var config = Program.ServerConfigResolver?.Invoke(guildId.Value);
                var hasMasterRole = config?.MasterRoleId.HasValue == true && guildUser.Roles.Any(r => r.Id == config.MasterRoleId.Value);
                if (!guildUser.GuildPermissions.Administrator && !hasMasterRole)
                {
                    await command.RespondAsync("У вас нет необходимой роли для выполнения этой команды.", ephemeral: true);
                    BotLogger.Warn(LogCategory.Cmd, $"Недостаточно прав у пользователя {guildUser.DisplayName} для команды очереди.");
                    return;
                }
            }

            var state = GetOrCreateState(guildId.Value);

            if (state.IsActive)
            {
                BotLogger.Warn(LogCategory.Cmd, "Попытка создания новой очереди, когда одна уже активна.");
                await command.RespondAsync($"Очередь уже создана на {state.MaxRolls} бросков. Если вы хотите её остановить принудительно, введите `/stop_q`.", ephemeral: true);
                return;
            }

            if (count <= 0)
            {
                await command.RespondAsync("Пожалуйста, укажите положительное число.");
                BotLogger.Warn(LogCategory.Cmd, $"При создании очереди указано не положительное число ({count}).");
                return;
            }

            state.MaxRolls = count;
            state.RollCount = 0;
            state.UserRolls.Clear();
            state.MessagesToDelete.Clear();
            state.IsActive = true;

            await command.RespondAsync(
                "Вы запустили создание очереди. Уведомьте об этом своих игроков. Бот остальную информацию уже сообщил.\nДанное сообщение можно скрыть или оно удалится автоматически.",
                ephemeral: true);

            _ = Task.Run(async () =>
            {
                await Task.Delay(7000);
                await command.DeleteOriginalResponseAsync();
            });

            state.Channel = (ITextChannel)command.Channel;
            state.StartMessage = await state.Channel.SendMessageAsync(
                $"Очередь активирована. Ожидаем {count} бросков. Вводите значения в формате `/q dY`. Таймер на минуту ожидания запущен.");
            state.MessagesToDelete.Add(state.StartMessage);

            // Захватываем guildId в замыкание для async-таймера
            var capturedGuildId = guildId.Value;
            state.RollTimer = new Timer(
                async _ => await ResetQueueAsync(capturedGuildId),
                null,
                TimeSpan.FromMinutes(1),
                Timeout.InfiniteTimeSpan);

            await Log($"Очередь создана: {count} бросков, гильдия {guildId}. Таймер запущен.");
        }

        [Command("q")]
        public async Task QIn_RollDice(SocketSlashCommand command, string input)
        {
            var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId == null) return;

            var state = GetOrCreateState(guildId.Value);

            if (!state.IsActive)
            {
                await Log("Предупреждение: Очередь не активна.");
                await command.RespondAsync("Пожалуйста, запустите очередь перед выполнением этой команды.", ephemeral: true);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    await command.DeleteOriginalResponseAsync();
                });
                return;
            }

            if (!input.StartsWith("d") || !int.TryParse(input[1..], out int y) || y <= 0)
            {
                await Log("Ошибка: Введены некорректные данные.");
                await command.RespondAsync("Пожалуйста, укажите корректные данные.", ephemeral: true);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    await command.DeleteOriginalResponseAsync();
                });
                return;
            }

            var user = command.User as SocketGuildUser;
            if (user == null || (state.UserRolls.ContainsKey(user) && state.UserRolls[user].Count >= state.MaxRolls))
                return;

            // Сбрасываем таймер при каждом броске
            state.RollTimer?.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);

            int result = new Random().Next(1, y + 1);
            if (!state.UserRolls.ContainsKey(user))
                state.UserRolls[user] = new List<int>();

            state.UserRolls[user].Add(result);
            state.RollCount++;

            await command.RespondAsync("Вывод результата:", ephemeral: false);
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                await command.DeleteOriginalResponseAsync();
            });

            var waitingMessage = await state.Channel!.SendMessageAsync(
                $"{user.DisplayName}, ваш бросок d{y}: {result}. Ещё {state.MaxRolls - state.RollCount} бросков. Ожидание следующего броска...");
            state.MessagesToDelete.Add(waitingMessage);

            await Log($"Бросок: {user.DisplayName} → {result}. Таймер обновлён.");

            if (state.RollCount >= state.MaxRolls)
                await DisplayResultsAsync(guildId.Value);
        }

        private static async Task DisplayResultsAsync(ulong guildId)
        {
            if (!_guildQueues.TryGetValue(guildId, out var state)) return;

            state.RollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            state.IsActive = false;

            foreach (var msg in state.MessagesToDelete)
            {
                try
                {
                    if (msg != null)
                    {
                        await msg.DeleteAsync();
                        await Task.Delay(100);
                    }
                }
                catch (Exception ex)
                {
                    await Log($"Ошибка удаления сообщения: {ex.Message}");
                }
            }

            if (state.UserRolls == null || state.UserRolls.Count == 0)
            {
                await Log("UserRolls пуст — нечего отображать.");
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle("Последовательность ходов")
                .WithColor(Color.Green);

            var sortedResults = state.UserRolls
                .SelectMany(ur => ur.Value.Select((roll, idx) => new { User = ur.Key, Roll = roll, Index = idx + 1 }))
                .OrderByDescending(x => x.Roll)
                .ToList();

            var resultString = new StringBuilder();
            int seq = 1;
            foreach (var res in sortedResults)
            {
                var nick = res.User.DisplayName;
                var line = sortedResults.Count(r => r.User == res.User) > 1
                    ? $"{seq} - {nick} - {res.Roll} (# {res.Index})"
                    : $"{seq} - {nick} - {res.Roll}";
                resultString.AppendLine(line);
                seq++;
            }

            embed.AddField("Результаты", resultString.ToString(), false);
            await state.Channel!.SendMessageAsync(embed: embed.Build());
            await Log("Результаты выведены. Очередь завершена.");

            state.Reset();
        }

        private static async Task ResetQueueAsync(ulong guildId)
        {
            if (!_guildQueues.TryGetValue(guildId, out var state) || state.Channel == null) return;

            state.IsActive = false;

            var timeoutMsg = await state.Channel.SendMessageAsync(
                ":exclamation: Время ожидания истекло, введите команду для создания очереди по новой. :exclamation:");
            state.MessagesToDelete.Add(timeoutMsg);

            _ = CleanupQueueMessagesAsync(state, TimeSpan.FromSeconds(6));

            await Log($"Таймер истёк для гильдии {guildId}. Очередь сброшена.");
        }

        private static async Task CleanupQueueMessagesAsync(GuildQueueState state, TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                foreach (var msg in state.MessagesToDelete)
                {
                    try { await msg.DeleteAsync().ConfigureAwait(false); }
                    catch (Exception ex) { await Log($"Ошибка очистки: {ex.Message}").ConfigureAwait(false); }
                }
            }
            finally
            {
                state.Reset();
            }
        }

        [Command("stop_q")]
        public async Task StopQueue(SocketSlashCommand command)
        {
            var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId == null) return;

            if (command.User is SocketGuildUser guildUser)
            {
                var config = Program.ServerConfigResolver?.Invoke(guildId.Value);
                var hasMasterRole = config?.MasterRoleId.HasValue == true && guildUser.Roles.Any(r => r.Id == config.MasterRoleId.Value);
                if (!guildUser.GuildPermissions.Administrator && !hasMasterRole)
                {
                    await command.RespondAsync("У вас нет необходимой роли для выполнения этой команды.", ephemeral: true);
                    await Log($"Ошибка: У пользователя {guildUser.DisplayName} недостаточно прав");
                    return;
                }
            }

            var state = GetOrCreateState(guildId.Value);

            if (!state.IsActive)
            {
                await command.RespondAsync("Очередь не активна.", ephemeral: false);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(7000);
                    await command.DeleteOriginalResponseAsync();
                });
                return;
            }

            state.RollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            state.IsActive = false;

            foreach (var msg in state.MessagesToDelete)
            {
                try { await msg.DeleteAsync(); }
                catch (Exception ex) { BotLogger.Error(LogCategory.Cmd, $"Ошибка удаления сообщения очереди", ex); }
            }

            state.Reset();

            await command.RespondAsync("Запись очереди принудительно отменена мастером.");
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                await command.DeleteOriginalResponseAsync();
            });
        }

        /// <summary>
        /// Graceful shutdown — освобождает все таймеры и очищает состояние всех гильдий.
        /// </summary>
        public static void ShutdownQueue()
        {
            try
            {
                foreach (var kv in _guildQueues)
                {
                    try { kv.Value.Dispose(); } catch { }
                    try { kv.Value.Reset(); } catch { }
                }
                _guildQueues.Clear();
            }
            catch (Exception ex)
            {
                try
                {
                    var logDir = BotConfig.ResolvePath(
                        string.IsNullOrWhiteSpace(BotConfig.Current?.LogDirectory) ? "Logs" : BotConfig.Current.LogDirectory);
                    Directory.CreateDirectory(logDir);
                    File.AppendAllText(
                        Path.Combine(logDir, "ErrorLog.txt"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ShutdownQueue error: {ex.Message}\n");
                }
                catch { }
            }
        }
    }
}
