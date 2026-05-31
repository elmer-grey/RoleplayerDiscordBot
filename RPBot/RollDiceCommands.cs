using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using RPBot;
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
        private static readonly SemaphoreSlim _sessionSemaphore = new(1, 1);

        // Per-user cooldown: не чаще 1 броска в 2 секунды
        private static readonly ConcurrentDictionary<ulong, DateTime> _lastRollTime = new();
        private static readonly TimeSpan _rollCooldown = TimeSpan.FromSeconds(2);

        private static bool IsOnCooldown(ulong userId)
        {
            if (_lastRollTime.TryGetValue(userId, out var last))
                return DateTime.UtcNow - last < _rollCooldown;
            return false;
        }

        private static void UpdateCooldown(ulong userId)
            => _lastRollTime[userId] = DateTime.UtcNow;
        private string ValidateRollInput(string input)
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
            BotLogger.Debug(LogCategory.Cmd, sb.ToString());
        }

        private static void WriteCompletedRollLog(string input, int result)
        {
            BotLogger.Debug(LogCategory.Cmd, $"Бросок: {input} → {result}");
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

        [Command("roll")]
        public async Task RollDice(SocketSlashCommand command, string input)
        {
            await command.DeferAsync();
            var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
            var channelId = command.Channel.Id;

            if (guildId == null)
            {
                await command.FollowupAsync("Команда доступна только на сервере.", ephemeral: true);
                return;
            }

            if (IsOnCooldown(command.User.Id))
            {
                await command.FollowupAsync("Подождите немного перед следующим броском.", ephemeral: true);
                return;
            }
            UpdateCooldown(command.User.Id);

            // Получаем ID канала статистики из конфига
            var statsConfig = Program.ServerConfigResolver?.Invoke(guildId.Value);
            var statsChannelId = statsConfig?.StatsChannelID ?? 0UL;
            var rollPicturesEnabled = statsConfig?.RollPicturesEnabled ?? true;

            // Проверяем, сделан ли бросок в канале статистики
            bool isStatsChannel = channelId == statsChannelId && statsChannelId != 0;

            var user = command.User as SocketGuildUser;
            if (user == null)
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

            if (isStatsChannel)
            {
                await _sessionSemaphore.WaitAsync();
                try
                {
                    if (GameSessionCommands._sessions.TryGetValue(guildId.Value, out var sessions))
                    {
                        var activeSessions = sessions.Where(s =>
                            !s.Value.IsStopped &&
                            s.Value.TrackRolls).ToList();

                        if (activeSessions.Any())
                        {
                            var pausedSessions = activeSessions.Where(s => s.Value.IsPaused).ToList();
                            if (pausedSessions.Any())
                            {
                                await command.FollowupAsync(
                                    $"Игра **{pausedSessions.First().Value.GameName}** на паузе. Броски не учитываются.",
                                    ephemeral: false);
                                 _ = DeleteOriginalResponseSafeAsync(command, 5000);
                                return;
                            }
                        }
                    }
                }
                finally
                {
                    _sessionSemaphore.Release();
                }
            }

            Random random = new Random();
            List<int> results = Enumerable.Range(0, count)
                .Select(_ => random.Next(min, max + 1))
                .ToList();

            var numbersDir = BotConfig.ResolvePath(BotConfig.Current?.NumbersDirectory ?? "Numbers");
            var diceSubfolder = Path.Combine(numbersDir, diceType);
            bool hasImages = !hasRange && Directory.Exists(diceSubfolder);

            if (isStatsChannel)
            {
                await _sessionSemaphore.WaitAsync();
                try
                {
                    if (GameSessionCommands._sessions.TryGetValue(guildId.Value, out var sessions))
                    {
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
                    _sessionSemaphore.Release();
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
                        await command.FollowupWithFileAsync(filePath, embed: embed);
                        WriteCompletedRollLog(_input, results, hasRange, min, max, modifier);
                        return;
                    }
                }
                else
                {
                    var embeds = new List<Embed>();
                    var files = new List<FileAttachment>();
                    var textResults = new List<string>();

                    for (int i = 0; i < results.Count; i++)
                    {
                        var result = results[i];
                        var filePath = Path.Combine(diceSubfolder, $"{result}.png");

                        if (File.Exists(filePath))
                        {
                            var uniqueFileName = $"{result}_{i + 1}.png";
                            files.Add(new FileAttachment(filePath, uniqueFileName));
                            embeds.Add(new EmbedBuilder()
                                .WithImageUrl($"attachment://{uniqueFileName}")
                                .WithColor(GetGradientColor(result, 1, max))
                                .Build());
                        }
                        else
                        {
                            textResults.Add(result.ToString());
                        }
                    }

                    if (files.Count > 0)
                    {
                        var combinedMessage = files.Count switch
                        {
                            2 => "Результаты броска с помехой/преимуществом:",
                            _ => $"Результаты {files.Count} бросков:"
                        };

                        if (textResults.Count > 0)
                        {
                            combinedMessage += $"\n(Без картинок: {string.Join(", ", textResults)})";
                        }

                        await command.FollowupWithFilesAsync(
                            attachments: files,
                            text: combinedMessage,
                            embeds: embeds.ToArray());
                        WriteCompletedRollLog(_input, results, hasRange, min, max, modifier);
                        return;
                    }
                }
            }

            var resultMessage = BuildRollResponse(results, hasRange, min, max, modifier);
            await command.FollowupAsync(resultMessage);
            WriteCompletedRollLog(_input, results, hasRange, min, max, modifier);
        }

        [Command("roll20")]
        public async Task Roll20(SocketSlashCommand command)
        {
            await command.DeferAsync();
            var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
            var channelId = command.Channel.Id;

            if (guildId == null)
            {
                await command.FollowupAsync("Команда доступна только на сервере.");
                return;
            }

            if (IsOnCooldown(command.User.Id))
            {
                await command.FollowupAsync("Подождите немного перед следующим броском.", ephemeral: true);
                return;
            }
            UpdateCooldown(command.User.Id);

            // Получаем ID канала статистики из конфига
            var statsConfig = Program.ServerConfigResolver?.Invoke(guildId.Value);
            var statsChannelId = statsConfig?.StatsChannelID ?? 0;
            var rollPicturesEnabled = statsConfig?.RollPicturesEnabled ?? true;

            // Если бросок сделан в канале статистики
            bool isStatsChannel = channelId == statsChannelId;

            Random random = new Random();
            int result = random.Next(1, 21);

            // Если это канал статистики, проверяем сессии
            if (isStatsChannel)
            {
                await _sessionSemaphore.WaitAsync();
                try
                {
                    if (GameSessionCommands._sessions.TryGetValue(guildId.Value, out var sessions))
                    {
                        // Находим ВСЕ сессии с включённой записью бросков (TrackRolls = true)
                        var sessionsWithRolls = sessions.Where(s =>
                            s.Value.TrackRolls &&
                            !s.Value.IsStopped).ToList();

                        if (sessionsWithRolls.Any())
                        {
                            // Проверяем, есть ли сессии на паузе
                            var pausedSessions = sessionsWithRolls.Where(s => s.Value.IsPaused).ToList();
                            if (pausedSessions.Any())
                            {
                                // Выводим уведомление о паузе с названием первой найденной сессии
                                await command.FollowupAsync(
                                    $"Игра **{pausedSessions.First().Value.GameName}** на паузе. Броски не учитываются.",
                                    ephemeral: false
                                );
                                 _ = DeleteOriginalResponseSafeAsync(command, 5000);
                                return;
                            }

                            foreach (var session in sessionsWithRolls.Where(s => !s.Value.IsPaused))
                            {
                                session.Value.Rolls.Add(new RollStatistic
                                {
                                    PlayerName = command.User.GlobalName,
                                    RollValue = result,
                                    DiceType = "d20"  // ✅ Roll20 всегда d20
                                });
                            }
                        }
                    }
                }
                finally
                {
                    _sessionSemaphore.Release();
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
                await command.FollowupWithFileAsync(filePath, embed: embed);
            }
            else
            {
                await command.FollowupAsync($"**Результат броска:** {result}");
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