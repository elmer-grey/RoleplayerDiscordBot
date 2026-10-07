using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using RPBot;
using RPBot.Common;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RPBot
{
    public class RollDiceCommands : ModuleBase<SocketCommandContext>
    {
        // Per-guild: параллельные !roll от разных серверов не должны друг друга ждать.
                private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> _sessionSemaphores = new();

                private static SemaphoreSlim GetGuildSemaphore(ulong guildId)
                    => _sessionSemaphores.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));

                // Per-user cooldown: не чаще 1 броска в 2 секунды
                private static readonly ConcurrentDictionary<ulong, DateTime> _lastRollTime = new();
        private static readonly TimeSpan _rollCooldown = TimeSpan.FromSeconds(2);

                        // 🩹 perf: кэш PNG для /roll20 (одиночный куб). Файл читается с диска ОДИН раз
                        // за сессию бота, дальше byte[] отдаётся через FileAttachment → нет file-lock
                        // на Windows и нет disk-IO при повторных бросках. Ключ = полный путь.
                        private static readonly ConcurrentDictionary<string, byte[]> _roll20PngCache =
                            new(StringComparer.OrdinalIgnoreCase);

        private static bool IsOnCooldown(ulong userId)
        {
            if (_lastRollTime.TryGetValue(userId, out var last))
                return DateTime.UtcNow - last < _rollCooldown;
            return false;
        }

        private static void UpdateCooldown(ulong userId)
            => _lastRollTime[userId] = DateTime.UtcNow;
        private string? ValidateRollInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "Ввод не может быть пустым.";

            if (!input.Contains('d', StringComparison.OrdinalIgnoreCase))
                return $"Введено `{input}`. Ввод должен содержать символ `d` (например, `2d6`).";

            var parts = input.Split(new[] { 'd', '+', '-' }, StringSplitOptions.RemoveEmptyEntries);

            // Проверяем количество частей
            if (parts.Length < 1 || parts.Length > 3)
                return $"Введено `{input}`. Некорректное количество параметров. Используйте формат `XdY`, `dY`, `XdY+Z`, `XdY-Z`, `Xd[min,max]+Z` или `d[min,max]-Z`.";

            // Проверяем, что все части являются числами
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out _) && !part.StartsWith('[') && !part.EndsWith(']'))
                    return $"Некорректное значение: `{part}`. Ожидается число или диапазон в формате `[min,max]`.";
            }

            // Проверяем диапазоны (если есть)
            if (input.Contains('[') || input.Contains(']'))
            {
                var rangePattern = @"\[\d+,\d+\]";
                if (!Regex.IsMatch(input, rangePattern))
                    return $"Введено `{input}`. Некорректный формат диапазона. Используйте `[min,max]`, где `min` и `max` — числа.";
            }

            // Если всё в порядке, возвращаем null
            return null;
        }

        /// <summary>
        /// Извлекает тип куба из строки ввода (например, из "2d6" получает "d6", из "3d20" получает "d20")
        /// </summary>
        private string ExtractDiceType(string input)
        {
            try
            {
                // Ищем паттерн "dX" где X - число
                var match = Regex.Match(input, @"d(\d+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var diceValue = match.Groups[1].Value; // Извлекаем число после "d"
                    return $"d{diceValue}"; // Возвращаем "d6", "d20", и т.д.
                }

                // Если паттерн не найден, возвращаем "d6" по умолчанию
                return "d6";
            }
            catch
            {
                return "d6"; // По умолчанию d6 при любой ошибке
            }
        }

        private static string BuildRollResponse(IReadOnlyList<int> results, bool hasRange, int minValue, int maxValue, int modifier)
        {
            var resultLabel = results.Count == 1 ? "**Результат броска:**" : "**Результаты броска:**";
            var totalLabel = results.Count == 1 ? "**Итоговый результат:**" : "**Итоговые результаты:**";
            var resultValues = string.Join(", ", results);
            var lines = new List<string>();

            if (hasRange)
                lines.Add($"**Диапазон:** {minValue}-{maxValue}");

            lines.Add($"{resultLabel} {resultValues}");

            if (modifier != 0)
            {
                var totalValues = string.Join(", ", results.Select(r => r + modifier));
                var modifierText = $"{(modifier >= 0 ? "+" : string.Empty)}{modifier}";
                lines.Add($"**Модификатор:** {modifierText}");
                lines.Add($"{totalLabel} {totalValues}");
            }

            return string.Join("\n", lines);
        }

        private static void WriteCompletedRollLog(string input, IReadOnlyList<int> results, bool hasRange, int minValue, int maxValue, int modifier)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"Бросок: {input}");
            if (hasRange) sb.Append($" [{minValue}-{maxValue}]");
            sb.Append($" → {string.Join(", ", results)}");
            if (modifier != 0) sb.Append($" (мод {(modifier >= 0 ? "+" : string.Empty)}{modifier} → {string.Join(", ", results.Select(r => r + modifier))})");
            var msg = sb.ToString();
            BotLogger.Info(LogCategory.Rolls, msg);
            Program.Instance?.IncrementRollsToday();
        }

        private static void WriteCompletedRollLog(string input, int result)
        {
            BotLogger.Info(LogCategory.Rolls, $"Бросок: {input} → {result}");
            Program.Instance?.IncrementRollsToday();
        }

        private static async Task DeleteOriginalResponseSafeAsync(SocketSlashCommand command, int delayMs)
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                await command.DeleteOriginalResponseAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

                /// <summary>
                /// Отправляет ephemeral Followup с уведомлением о паузе (видно только автору).
                /// Если pauseMsg == null — ничего не делает.
                /// Применяется после основного Followup (PNG или текст результата), чтобы:
                ///   1) ACK «Бот думает...» не удалялся и пользователь понимал, что slash отработал;
                ///   2) текст о паузе и Followup с результатом жили независимо, без гонки;
                ///   3) ephemeral не путал текст в общем канале.
                /// </summary>
                private static async Task TrySendPauseFollowupAsync(SocketSlashCommand command, string? pauseMsg)
                {
                    if (pauseMsg == null) return;
                    try
                    {
                        await command.FollowupAsync(pauseMsg, ephemeral: true).ConfigureAwait(false);
                    }
                    catch
                    {
                        // ephemeral может быть запрещён в этом канале / истёк токен — не критично.
                    }
                }

        [Command("roll")]
        public async Task RollDice(SocketSlashCommand command, string input)
        {
            // Сразу освобождаем шлюз и подтверждаем взаимодействие. Это нужно, чтобы Discord
            // не показал «Приложение не отвечает», даже если дальнейшая обработка залипнет.
            //
            // Если предварительный ACK уже выполнен в Program.OnSlashCommandExecuted
            // (ранний defer для тяжёлых команд — см. Program._preDeferCommands),
            // повторно DeferAsync НЕ зовём: он упадёт с «Cannot defer an already deferred interaction».
                        if (Program.IsPreDeferFailed(command.Id))
                        {
                            // 🩹 perf: PreDefer окончательно не прошёл (3 ретрая 10062 — interaction
                            // не синхронизирован). Не пытаемся выполнить бросок — Discord всё равно
                            // уже отвалился, и Followup-ответ не доставится. Просто выходим.
                            Program.ClearPreDeferFailed(command.Id);
                            return;
                        }
                        var alreadyDeferred = Program.IsPreDeferDone(command.Id);
            if (alreadyDeferred)
            {
                Program.ClearPreDeferDone(command.Id);
            }
            else
            {
                try { await command.DeferAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    DeferFailureLogger.Log("RollDice", ex, command, input);
                    return;
                }
            }

            string user = command.User?.GlobalName ?? "<unknown>";
            ulong? guildId = null;
            ulong channelId = 0;
            try
            {
                guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
                channelId = command.Channel.Id;
                if (guildId == null)
                {
                    await command.FollowupAsync("Команда доступна только на сервере.", ephemeral: true).ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[RollDice:init] user={user} {ex.GetType().Name}: {ex.Message}");
                try { await command.FollowupAsync("Ошибка при подготовке команды.", ephemeral: true).ConfigureAwait(false); } catch { }
                return;
            }

            // Боевой блок: всё, что ниже, обёрнуто в один try/catch.
            // Раньше исключение из regex/валидации/queue/semafora проглатывалось, и Discord показывал таймаут.
            try
            {
                await RollDiceInternalAsync(command, input, guildId.Value, channelId, user).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[RollDice:{input}] user={user} guild={guildId} {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("RollDice", ex, $"user={user} guild={guildId} input={input}").ConfigureAwait(false); } catch { }
                try { await command.FollowupAsync("Ошибка при броске. Подробности в логах.", ephemeral: true).ConfigureAwait(false); } catch { }
            }
        }

        private async Task RollDiceInternalAsync(SocketSlashCommand command, string input, ulong guildId, ulong channelId, string user)
        {
            if (IsOnCooldown(command.User.Id))
            {
                        // ephemeral: кулдаун — личное уведомление, остальным в канале оно не нужно.
                        await command.FollowupAsync("Подождите немного перед следующим броском.", ephemeral: true);
                return;
            }
            UpdateCooldown(command.User.Id);

            // Получаем ID канала статистики и канала бросков из конфига
            var statsConfig = Program.ServerConfigResolver?.Invoke(guildId);
            var statsChannelId = statsConfig?.StatsChannelID ?? 0UL;
            var rollChannelId = statsConfig?.RollChannelID ?? 0UL;
            var rollPicturesEnabled = statsConfig?.RollPicturesEnabled ?? true;

            // Проверяем, сделан ли бросок в канале статистики или в канале бросков.
            // В дашборде флаг «сбор бросков» ожидаемо показывает, что бросок УЧИТЫВАЕТСЯ в сессии,
            // а не только в stats-канале. Учитываем оба.
            bool isStatsChannel = statsChannelId != 0 && channelId == statsChannelId;
            bool isRollChannel  = rollChannelId  != 0 && channelId == rollChannelId;

            var guildUser = command.User as SocketGuildUser;
            if (guildUser == null)
            {
                await command.FollowupAsync("Не удалось получить информацию о пользователе.");
                return;
            }

            var _input = input.Trim();

            // Проверяем ввод
            var errorMessage = ValidateRollInput(_input);
            if (errorMessage != null)
            {
                await command.FollowupAsync($"Ошибка: {errorMessage}");
                BotLogger.Warn(LogCategory.Cmd, $"Неверный формат броска: {errorMessage}");
                return;
            }

            string diceType = ExtractDiceType(_input);

            var match = Regex.Match(_input, @"^(?:(\d*)d(?:([1-9]\d*)|\[(\d+),(\d+)\]))([+-]\d+)?$", RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                await command.FollowupAsync("Неверный формат! Используйте `XdY`, `dY`, `XdY+Z` или `XdY-Z`, где `X`, `Y`, `Z` — строго больше 0.");
                BotLogger.Warn(LogCategory.Cmd, "Неверный формат броска (regex не совпал).");
                return;
            }

            int count = 1;
            int min = 1;
            int max = 1;
            int modifier = 0;
            var hasRange = false;

            if (match.Groups[1].Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                count = int.Parse(match.Groups[1].Value);

            if (match.Groups[2].Success && !string.IsNullOrWhiteSpace(match.Groups[2].Value))
            {
                max = int.Parse(match.Groups[2].Value);
            }
            else if (match.Groups[3].Success && match.Groups[4].Success)
            {
                min = int.Parse(match.Groups[3].Value);
                max = int.Parse(match.Groups[4].Value);
                hasRange = true;
            }

            if (match.Groups[5].Success)
                modifier = int.Parse(match.Groups[5].Value);

            if (count <= 0 || min <= 0 || max <= 0)
            {
                await command.FollowupAsync("Количество бросков и верхняя граница должны быть больше нуля.", ephemeral: true);
                return;
            }
            if (min > max)
            {
                await command.FollowupAsync("Левая граница диапазона не может быть больше правой.", ephemeral: true);
                return;
            }
            if (count > 10)
            {
                await command.FollowupAsync("Давайте сильно не наглеть? 10 бросков - это максимум.", ephemeral: false);
                return;
            }

            string? pauseMsg = null;

                        if (isStatsChannel)
                        {
                            var sem = GetGuildSemaphore(guildId);
                                        // Таймаут 2 сек, как в Roll20 — если сессия залипла, не держим
                                        // Discord-interaction >3 сек (это вызовет "Приложение не отвечает").
                                        bool gotLock = await sem.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                                        if (!gotLock)
                                        {
                                            BotLogger.Warn(LogCategory.Cmd, $"[RollDice] семафор сессий занят >2с, пропускаю запись в сессии");
                                        }
                                        else
                                        try
                                        {
                                if (GameSessionCommands._sessions.TryGetValue(guildId, out var sessions))
                                {
                                    // ✅ Bug 3: пауза НЕ блокирует бросок. Бросок всегда можно совершить;
                                    // глобальный счётчик IncrementRollsToday() инкрементится в
                                    // WriteCompletedRollLog (см. ниже). В session.Rolls пишется
                                    // только если сессия активна и TrackRolls=true.
                                    //
                                    // Однако: если ВСЕ сессии со сбором бросков на паузе — бросок
                                    // покажется без пометки (что было бы странно для пользователя,
                                    // который не понимает, почему ничего не засчиталось). В этом
                                    // случае пишем сообщение, что бросок учтён только глобально.
                                    var activeCollecting = sessions.Values
                                        .Where(s => !s.IsStopped && !s.IsPaused && s.TrackRolls)
                                        .ToList();
                                    var pausedCollecting = sessions.Values
                                        .Where(s => !s.IsStopped && s.IsPaused && s.TrackRolls)
                                        .ToList();

                                    // ✅ Bug 3: информируем пользователя о пауза-сессиях.
                                    // Раньше текст отправлялся отдельным Followup + DeleteOriginalFile-
                                    // SafeAsync(command, 5000). Из-за гонки с PNG-Followup это выглядело
                                    // как «оповещение о паузе и картинка броска сливаются в один ответ и
                                    // вместе удаляются». Теперь текст сохраняется в pauseMsg и отправляется
                                    // ephemeral-Followup ПОСЛЕ PNG — видно только автору, не висит в канале,
                                    // ACK «Бот думает...» не удаляется.
                                    if (activeCollecting.Count == 0 && pausedCollecting.Count > 0)
                                    {
                                        var names = string.Join(", ", pausedCollecting.Select(p => $"«{p.GameName}»"));
                                        pauseMsg = $"Игра {names} на паузе. Бросок засчитан только в глобальный счётчик, в сессии он не пойдёт.";
                                    }
                                    else if (activeCollecting.Count > 0 && pausedCollecting.Count > 0)
                                    {
                                        var names = string.Join(", ", pausedCollecting.Select(p => $"«{p.GameName}»"));
                                        pauseMsg = $"⚠️ Бросок засчитан в активные сессии. Сессии на паузе не учли его: {names}.";
                                    }
                                }
                            }
                            finally
                            {
                                                            if (gotLock) sem.Release();
                            }
                        }

            Random random = Random.Shared;
            List<int> results = Enumerable.Range(0, count)
                .Select(_ => random.Next(min, max + 1))
                .ToList();

            var numbersDir = BotConfig.ResolvePath(BotConfig.Current?.NumbersDirectory ?? "Numbers");
            var diceSubfolder = Path.Combine(numbersDir, diceType);
            bool hasImages = !hasRange && Directory.Exists(diceSubfolder);

            if (isStatsChannel || isRollChannel)
            {
                var sem = GetGuildSemaphore(guildId);
                            bool gotLock = await sem.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                            if (!gotLock)
                {
                                BotLogger.Warn(LogCategory.Cmd, $"[RollDice] семафор сессий занят >2с, пропускаю запись в сессии");
                            }
                            else
                            try
                            {
                                if (GameSessionCommands._sessions.TryGetValue(guildId, out var sessions))
                                {
                                    // ✅ Bug 3: пишем бросок ТОЛЬКО в активные сессии с TrackRolls=true.
                                    // Глобальный счётчик (RollsToday) уже инкрементнут через
                                    // WriteCompletedRollLog ниже.
                        var activeSessions = sessions.Where(s =>
                            !s.Value.IsStopped &&
                            !s.Value.IsPaused &&
                            s.Value.TrackRolls).ToList();

                        foreach (var session in activeSessions)
                        {
                            foreach (var result in results)
                            {
                                session.Value.Rolls.Add(new RollStatistic
                                {
                                    PlayerName = command.User.GlobalName,
                                    RollValue = result,
                                    DiceType = diceType
                                });
                            }
                        }
                    }
                }
                finally
                {
                                                    if (gotLock) sem.Release();
                }
            }

            if (rollPicturesEnabled && hasImages && modifier == 0)
            {
                if (count == 1)
                {
                    var result = results[0];
                    var filePath = Path.Combine(diceSubfolder, $"{result}.png");

                    if (File.Exists(filePath))
                    {
                        var embed = new EmbedBuilder()
                            .WithImageUrl($"attachment://{Path.GetFileName(filePath)}")
                            .WithColor(GetGradientColor(result, 1, max))
                            .Build();
                                            // 🩹 perf: берём PNG из кэша (см. _roll20PngCache). Тот же кэш
                                            // работает и для /roll с одним кубом, потому что ключ — полный путь.
                                            var pngBytes = _roll20PngCache.GetOrAdd(filePath, path => File.ReadAllBytes(path));
                                            var attachment = new FileAttachment(new MemoryStream(pngBytes, writable: false), Path.GetFileName(filePath));
                                            await command.FollowupWithFilesAsync(
                                                attachments: new[] { attachment },
                                                embeds: new[] { embed });
                                                                                        await TrySendPauseFollowupAsync(command, pauseMsg);
                                                                                        WriteCompletedRollLog(_input, results, hasRange, min, max, modifier);
                                                                                        return;
                                                                                    }
                }
                else
                {
                    // Несколько кубов: склеиваем в один strip (требование пользователя).
                    // Цвет embed — по СРЕДНЕМУ значению (требование пользователя).
                    // Fallback на multi-embed с отдельными эмбедами — если не удалось склеить
                    // (например, не хватает части файлов).
                                    //
                                    // 🩹 perf: ComposeStrip делается в фоне (Task.Run), чтобы GC-паузы при
                                    // склейке PNG (10d20 → 168*10 = 1680 px bitmap + PNG-энкод) не
                                    // блокировали ACK-поток >3 сек и не вызывали "Приложение не отвечает".
                                    // Defer уже отправлен, фоновый Task.Run — это disk/GC-работа.
                                    // ВАЖНО: out-параметр missing мы возвращаем через замыкание (массив из 1 элемента).
                                    var missingBox = new List<int>[] { null! };
                                    var stripTask = Task.Run(() =>
                                        DiceStripComposer.ComposeStrip(results, numbersDir, diceType, out missingBox[0]));
                                    var stripStream = await stripTask.ConfigureAwait(false);
                                    var missingForStrip = missingBox[0] ?? new List<int>();

                                    if (stripStream != null)
                                    {
                                        try
                                        {
                                            var avg = results.Average();
                                            var embed = new EmbedBuilder()
                                                .WithImageUrl("attachment://roll_strip.png")
                                                .WithColor(DiceStripComposer.ColorForAverage(avg, min, max))
                                                .Build();

                                            var label = count == 2
                                                ? "Результаты броска (помеха/преимущество):"
                                                : $"Результаты {count} бросков:";

                                            var attachment = new FileAttachment(stripStream, "roll_strip.png");
                                            await command.FollowupWithFilesAsync(
                                                attachments: new[] { attachment },
                                                text: label,
                                                embeds: new[] { embed });

                                                                                        await TrySendPauseFollowupAsync(command, pauseMsg);

                                                                                        if (missingForStrip.Count > 0)
                                                                                            BotLogger.Warn(LogCategory.Rolls,
                                                                                                $"[strip] не нашлись файлы для значений: {string.Join(",", missingForStrip)}");

                                                                                        WriteCompletedRollLog(_input, results, hasRange, min, max, modifier);
                                                                                        return;
                                        }
                                        finally
                                        {
                                            stripStream.Dispose();
                                        }
                                    }
                                    else
                                    {
                                        // Не получилось склеить (вообще нет файлов). Падаем в общий fallback ниже.
                                        BotLogger.Warn(LogCategory.Rolls,
                                            $"[strip] для {diceType} нет ни одного PNG, fallback на текст.");
                                    }
                }
            }

            var resultMessage = BuildRollResponse(results, hasRange, min, max, modifier);
            await command.FollowupAsync(resultMessage);
                        await TrySendPauseFollowupAsync(command, pauseMsg);
                        WriteCompletedRollLog(_input, results, hasRange, min, max, modifier);
                    }

        [Command("roll20")]
        public async Task Roll20(SocketSlashCommand command)
        {
            // Сразу подтверждаем взаимодействие, чтобы Discord не показывал «Приложение не отвечает».
            //
            // Если предварительный ACK уже выполнен в Program.OnSlashCommandExecuted
            // (ранний defer для тяжёлых команд — см. Program._preDeferCommands),
            // повторно DeferAsync НЕ зовём.
            var alreadyDeferred = Program.IsPreDeferDone(command.Id);
            if (alreadyDeferred)
            {
                Program.ClearPreDeferDone(command.Id);
            }
            else
            {
                try { await command.DeferAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    DeferFailureLogger.Log("Roll20", ex, command, input: null);
                    return;
                }
            }

            ulong? guildIdNullable = null;
            ulong channelId = 0;
            string userName = command.User?.GlobalName ?? "<unknown>";
            try
            {
                guildIdNullable = (command.Channel as SocketGuildChannel)?.Guild.Id;
                channelId = command.Channel.Id;
                if (guildIdNullable == null)
                {
                    await command.FollowupAsync("Команда доступна только на сервере.").ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[Roll20:init] user={userName} {ex.GetType().Name}: {ex.Message}");
                try { await command.FollowupAsync("Ошибка при подготовке команды.", ephemeral: true).ConfigureAwait(false); } catch { }
                return;
            }

            ulong guildId = guildIdNullable.Value;

            // Боевой блок: всё, что ниже, обёрнуто в один try/catch. Раньше исключение из валидации,
            // семафора или работы с диском проглатывалось, и Discord показывал таймаут без следа.
            try
            {
                await Roll20InternalAsync(command, guildId, channelId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[Roll20] user={userName} guild={guildId} {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("Roll20", ex, $"user={userName} guild={guildId}").ConfigureAwait(false); } catch { }
                try { await command.FollowupAsync("Ошибка при броске d20. Подробности в логах.", ephemeral: true).ConfigureAwait(false); } catch { }
            }
        }

        private async Task Roll20InternalAsync(SocketSlashCommand command, ulong guildId, ulong channelId)
        {
            if (IsOnCooldown(command.User.Id))
            {
                        // ephemeral: кулдаун — личное уведомление, остальным в канале оно не нужно.
                        await command.FollowupAsync("Подождите немного перед следующим броском.", ephemeral: true);
                return;
            }
            UpdateCooldown(command.User.Id);

            // Получаем ID канала статистики и канала бросков из конфига
            var statsConfig = Program.ServerConfigResolver?.Invoke(guildId);
            var statsChannelId = statsConfig?.StatsChannelID ?? 0;
            var rollChannelId = statsConfig?.RollChannelID ?? 0;
            var rollPicturesEnabled = statsConfig?.RollPicturesEnabled ?? true;

            // Если бросок сделан в канале статистики или в канале бросков
            bool isStatsChannel = statsChannelId != 0 && channelId == statsChannelId;
            bool isRollChannel  = rollChannelId  != 0 && channelId == rollChannelId;

            Random random = Random.Shared;
            int result = random.Next(1, 21);

            string? pauseMsg = null;

                        // Если это канал статистики или канал бросков, проверяем сессии.
                        // Семафор ждём с таймаутом — если он залип, не подвешиваем взаимодействие на >3с.
                        //
                        // ✅ Bug 3: бросок ВСЕГДА совершается. Глобальный счётчик
                        // (RollsToday через IncrementRollsToday ниже) инкрементится
                        // независимо от паузы. В session.Rolls пишется только если
                        // сессия активна и TrackRolls=true.
                        if (isStatsChannel || isRollChannel)
                        {
                            bool gotLock = false;
                            var sem = GetGuildSemaphore(guildId);
                            try
                            {
                                gotLock = await sem.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                                if (!gotLock)
                                {
                                    BotLogger.Warn(LogCategory.Cmd, $"[Roll20] семафор сессий занят >2с, пропускаю запись броска");
                                }
                                else
                                {
                                    if (GameSessionCommands._sessions.TryGetValue(guildId, out var sessions))
                                    {
                                        // ✅ Bug 2/4: каждая сессия сама решает, собирает ли она броски (TrackRolls).
                                        var sessionsWithRolls = sessions.Values
                                            .Where(s => s.TrackRolls && !s.IsStopped)
                                            .ToList();

                                        // Пишем бросок только в активные (не на паузе) сессии.
                                        var activeCollecting = sessionsWithRolls
                                            .Where(s => !s.IsPaused)
                                            .ToList();
                                        foreach (var session in activeCollecting)
                                        {
                                            session.Rolls.Add(new RollStatistic
                                            {
                                                PlayerName = command.User.GlobalName,
                                                RollValue = result,
                                                DiceType = "d20"  // ✅ Roll20 всегда d20
                                            });
                                        }

                                        // ✅ Bug 3: информируем пользователя о пауза-сессиях.
                                        // Раньше текст отправлялся отдельным Followup + DeleteOriginalFile-
                                        // SafeAsync(command, 5000). Из-за гонки с PNG-Followup это выглядело
                                        // как «оповещение о паузе и картинка броска сливаются в один ответ и
                                        // вместе удаляются». Теперь: PNG отправляется в канал ОДИН Followup,
                                        // текст о паузе идёт вторым ephemeral-Followup (видно только автору),
                                        // ACK «Бот думает...» не удаляется.
                                        var pausedCollecting = sessionsWithRolls
                                            .Where(s => s.IsPaused)
                                            .ToList();
                                        if (pausedCollecting.Any())
                                        {
                                            var names = string.Join(", ", pausedCollecting.Select(p => $"«{p.GameName}»"));
                                            pauseMsg = activeCollecting.Any()
                                                ? $"⚠️ Бросок засчитан в активные сессии. Сессии на паузе не учли его: {names}."
                                                : $"Игра {names} на паузе. Бросок засчитан только в глобальный счётчик, в сессии он не пойдёт.";
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                if (gotLock) sem.Release();
                            }
                        }

                        // Показываем результат броска (в любом случае)
                        var numbersDir = BotConfig.ResolvePath(BotConfig.Current?.NumbersDirectory ?? "Numbers");
                        var diceSubfolder = Path.Combine(numbersDir, "d20");
                        var filePath = Path.Combine(diceSubfolder, $"{result}.png");

                        if (rollPicturesEnabled && Directory.Exists(diceSubfolder) && File.Exists(filePath))
                        {
                            var embed = new EmbedBuilder()
                                .WithImageUrl($"attachment://{Path.GetFileName(filePath)}")
                                .WithColor(GetGradientColor(result, 1, 20))
                                .Build();
                                        // 🩹 perf: берём PNG из кэша, не с диска. Кэш наполняется через
                                        // File.ReadAllBytesAsync при первом запросе и больше никогда не меняется.
                                        var pngBytes = _roll20PngCache.GetOrAdd(filePath, path => File.ReadAllBytes(path));
                                        var attachment = new FileAttachment(new MemoryStream(pngBytes, writable: false), Path.GetFileName(filePath));
                                        await command.FollowupWithFilesAsync(
                                            attachments: new[] { attachment },
                                            embeds: new[] { embed });
                                                                                    await TrySendPauseFollowupAsync(command, pauseMsg);
                                                                                }
                                                                                else
                                                                                {
                                                                                    await command.FollowupAsync($"**Результат броска:** {result}");
                                                                                    await TrySendPauseFollowupAsync(command, pauseMsg);
                                                                                }
                                                                    WriteCompletedRollLog("d20", result);
                                                                }

        private Color GetGradientColor(int value, int minValue, int maxValue)
        {
            float normalizedValue = (float)(value - minValue) / (maxValue - minValue);

            int r, g, b;

            if (normalizedValue < 0.5f) // от 1 до 10
            {
                r = 255;
                g = (int)(255 * (normalizedValue * 2));
                b = 0;
            }
            else // от 10 до 20
            {
                r = (int)(255 * (1 - (normalizedValue - 0.5f) * 2));
                g = 255;
                b = 0;
            }

            return new Color(r, g, b);
        }
    }


}