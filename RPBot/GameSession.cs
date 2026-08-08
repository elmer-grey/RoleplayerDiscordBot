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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot
{
    public class RollStatistic
    {
        public string PlayerName { get; set; } = string.Empty;
        public int RollValue { get; set; }
        public string DiceType { get; set; } = string.Empty;
    }

    public class GameSession
    {
        public ulong SessionId { get; set; }
        public ulong GuildId { get; set; }
        public string GameName { get; set; } = string.Empty;
        public string GameComment { get; set; } = string.Empty;
        public string MasterName { get; set; } = string.Empty;
        public ulong MasterId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public List<(DateTime Start, DateTime? End)> PausePeriods { get; set; } = new();
        public bool IsPaused { get; set; }
        public bool IsStopped => EndTime.HasValue;
        public List<RollStatistic> Rolls { get; set; } = new();
        public string EventDescription { get; set; } = string.Empty;
        public ulong ControlMessageId { get; set; }
        /// <summary>Канал, в котором было отправлено сообщение управления. Записывается при создании control message.</summary>
        public ulong ControlChannelId { get; set; }
        public ulong StatsMessageId { get; set; }
        public CancellationTokenSource? PauseReminderCTS { get; set; }
        public CancellationTokenSource? ControlMessageUpdateCTS { get; set; }
        public bool TrackRolls { get; set; }
        /// <summary>true — сбор бросков включён автоматически при старте; сбрасывается при ручном включении.</summary>
        public bool TrackRollsAutoEnabled { get; set; }
        public ulong? EventId { get; set; }
        public ulong ChannelId { get; set; }
        public ulong? PauseReminderMessageId { get; set; }
        public ulong? ConfirmationMessageId { get; set; }
        public bool StatsSent { get; set; }
        public object StatsSync { get; } = new();
        /// <summary>1-based номер строки в Google Sheets, куда записана эта сессия. 0 = не записано.</summary>
        public int SheetRowIndex { get; set; } = 0;
        /// <summary>Семафор для защиты от параллельных нажатий кнопок управления одной сессией.</summary>
        public SemaphoreSlim ButtonSemaphore { get; } = new SemaphoreSlim(1, 1);
    }

    public class GameSessionCommands : ModuleBase<SocketCommandContext>
    {
        private readonly DiscordSocketClient _client;
        private readonly GoogleSheetsService? _googleSheets;
        internal Action<string>? _logSinkOverride;
        public static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, GameSession>> _sessions = new();
        private static readonly SemaphoreSlim _sessionSemaphore = new(1, 1);
        private static readonly SemaphoreSlim _saveSessionsSemaphore = new(1, 1);

        private static readonly string _sessionsStatePath = Path.Combine(AppContext.BaseDirectory, "Data", "sessions_state.json");

        public GameSessionCommands(DiscordSocketClient client, GoogleSheetsService? googleSheets = null)
        {
            _client = client;
            _googleSheets = googleSheets;
        }

        private void Log(string message)
            => BotLogger.Info(LogCategory.Session, message);

        private static void LogDebug(string message)
            => BotLogger.Debug(LogCategory.Session, message);

        private static void LogWarn(string message)
            => BotLogger.Warn(LogCategory.Session, message);

        private static void LogError(string message)
            => BotLogger.Error(LogCategory.Session, message);

        private static async Task SaveSessionsAsync()
        {
            try
            {
                var dataDir = Path.GetDirectoryName(_sessionsStatePath);
                if (dataDir != null && !Directory.Exists(dataDir))
                    Directory.CreateDirectory(dataDir);

                await _saveSessionsSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    var sessionsToSave = new Dictionary<string, object>();

                    foreach (var guild in _sessions)
                    {
                        foreach (var session in guild.Value.Values.Where(s => !s.IsStopped))
                        {
                            var key = $"{guild.Key}:{session.SessionId}";
                            sessionsToSave[key] = new
                            {
                                session.SessionId,
                                session.GuildId,
                                session.ChannelId,
                                session.GameName,
                                session.MasterName,
                                session.MasterId,
                                session.StartTime,
                                session.EventDescription,
                                session.GameComment,
                                session.EventId,
                                session.ControlMessageId,
                                session.ControlChannelId,
                                session.IsPaused,
                                session.TrackRolls,
                                Rolls = session.Rolls
                            };
                        }
                    }

                    var json = System.Text.Json.JsonSerializer.Serialize(sessionsToSave, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(_sessionsStatePath, json).ConfigureAwait(false);

                    if (sessionsToSave.Count > 0)
                    {
                        var totalRolls = _sessions.Values.SelectMany(g => g.Values.Where(s => !s.IsStopped)).Sum(s => s.Rolls.Count);
                        BotLogger.Debug(LogCategory.Session, $"Сохранено {sessionsToSave.Count} активных сессий ({totalRolls} бросков)");
                    }
                }
                finally
                {
                    _saveSessionsSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Session, $"Ошибка при сохранении сессий: {ex.Message}");
            }
        }

        public static async Task LoadSessionsAsync(DiscordSocketClient client)
        {
            try
            {
                if (!File.Exists(_sessionsStatePath))
                {
                    BotLogger.Debug(LogCategory.Session, "Файл сохранённых сессий не найден");
                    return;
                }

                var json = await File.ReadAllTextAsync(_sessionsStatePath).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    BotLogger.Debug(LogCategory.Session, "Файл сессий пуст, загрузка пропущена");
                    return;
                }
                var doc = System.Text.Json.JsonDocument.Parse(json);

                var commands = new GameSessionCommands(client);
                int restorCount = 0;
                int skippedOldCount = 0;
                // Сессии старше этого порога считаются устаревшими и не восстанавливаются
                const int MaxSessionAgeHours = 48;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    try
                    {
                        var elem = prop.Value;
                        var sessionId = elem.GetProperty("SessionId").GetUInt64();
                        var guildId = elem.GetProperty("GuildId").GetUInt64();
                        var gameName = elem.GetProperty("GameName").GetString() ?? "Unknown";
                        var masterName = elem.GetProperty("MasterName").GetString() ?? "Unknown";
                        var masterId = elem.GetProperty("MasterId").GetUInt64();
                        var startTime = DateTime.Parse(elem.GetProperty("StartTime").GetString() ?? DateTime.Now.ToString());

                        // Пропускаем слишком старые сессии — они не восстанавливаются
                        if ((DateTime.Now - startTime).TotalHours > MaxSessionAgeHours)
                        {
                            skippedOldCount++;
                            BotLogger.Debug(LogCategory.Session, $"Пропущена устаревшая сессия {sessionId}: \"{gameName}\" (начало: {startTime:dd.MM.yyyy HH:mm}, прошло: {(DateTime.Now - startTime).TotalHours:0}ч > {MaxSessionAgeHours}ч)");
                            continue;
                        }
                        var eventDescription = elem.TryGetProperty("EventDescription", out var ed) ? ed.GetString() : null;
                        var gameComment = elem.TryGetProperty("GameComment", out var gc) ? gc.GetString() : null;
                        var eventId = elem.TryGetProperty("EventId", out var eid) && eid.ValueKind != System.Text.Json.JsonValueKind.Null ? (ulong?)eid.GetUInt64() : null;
                        var controlMessageId = elem.GetProperty("ControlMessageId").GetUInt64();
                        var controlChannelId = elem.TryGetProperty("ControlChannelId", out var cci) ? cci.GetUInt64() : 0UL;
                        var channelId = elem.GetProperty("ChannelId").GetUInt64();
                        var isPaused = elem.TryGetProperty("IsPaused", out var ip) && ip.GetBoolean();
                        var trackRolls = elem.TryGetProperty("TrackRolls", out var tr) && tr.GetBoolean();

                        var rolls = new List<RollStatistic>();
                        if (elem.TryGetProperty("Rolls", out var rollsElem) && rollsElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var rollElem in rollsElem.EnumerateArray())
                            {
                                try
                                {
                                    var roll = new RollStatistic
                                    {
                                        PlayerName = rollElem.GetProperty("PlayerName").GetString() ?? "Unknown",
                                        RollValue = rollElem.GetProperty("RollValue").GetInt32(),
                                        DiceType = rollElem.TryGetProperty("DiceType", out var dt) ? (dt.GetString() ?? "unknown") : "unknown"
                                    };
                                    rolls.Add(roll);
                                }
                                catch { }
                            }
                        }

                        var session = new GameSession
                        {
                            SessionId = sessionId,
                            GuildId = guildId,
                            ChannelId = channelId,
                            GameName = gameName,
                            MasterName = masterName,
                            MasterId = masterId,
                            StartTime = startTime,
                            EventDescription = eventDescription ?? string.Empty,
                            GameComment = gameComment ?? string.Empty,
                            EventId = eventId,
                            ControlMessageId = controlMessageId,
                            ControlChannelId = controlChannelId,
                            IsPaused = isPaused,
                            TrackRolls = trackRolls,
                            Rolls = rolls
                        };

                        if (!_sessions.TryGetValue(guildId, out var guildSessions))
                        {
                            guildSessions = new ConcurrentDictionary<ulong, GameSession>();
                            _sessions[guildId] = guildSessions;
                        }

                        if (guildSessions.TryAdd(sessionId, session))
                        {
                            restorCount++;
                            BotLogger.Info(LogCategory.Session, $"Восстановлена сессия {sessionId}: \"{gameName}\" (мастер: {masterName}, бросков: {rolls.Count})");
                        }
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Error(LogCategory.Session, $"Ошибка при восстановлении сессии из {prop.Name}: {ex.Message}");
                    }
                }

                if (restorCount > 0 || skippedOldCount > 0)
                {
                    var totalRestorRolls = _sessions.Values.SelectMany(g => g.Values).Sum(s => s.Rolls.Count);
                    BotLogger.Info(LogCategory.Session, $"Восстановлено {restorCount} сессий ({totalRestorRolls} бросков), пропущено устаревших: {skippedOldCount}");

                    if (restorCount > 0)
                        _ = RecreateControlMessagesAsync(client);
                }
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Session, $"Ошибка при загрузке сессий: {ex.Message}");
            }
        }

        private static async Task RecreateControlMessagesAsync(DiscordSocketClient client)
        {
            try
            {
                var commands = new GameSessionCommands(client);
                int recreatedCount = 0;

                foreach (var guild in _sessions)
                {
                    foreach (var session in guild.Value.Values.ToList())
                    {
                        try
                        {
                            // Приоритет: ControlChannelId (точный канал control message) → ChannelId (fallback)
                            var resolvedChannelId = session.ControlChannelId != 0 ? session.ControlChannelId : session.ChannelId;
                            var channel = client.GetChannel(resolvedChannelId) as ITextChannel
                                ?? client.GetGuild(session.GuildId)?.GetTextChannel(resolvedChannelId);

                            if (channel == null)
                                continue;

                            if (session.ControlMessageId != 0)
                            {
                                try
                                {
                                    var existingMessage = await channel.GetMessageAsync(session.ControlMessageId).ConfigureAwait(false);
                                    if (existingMessage != null)
                                    {
                                        commands.StartControlMessageAutoRefresh(session, channel);
                                        continue;
                                    }
                                }
                                catch
                                {
                                }
                            }

                            var activeDuration = CalculateActiveDuration(session, DateTime.Now);
                            var embed = new EmbedBuilder()
                                .WithTitle($"Сессия: \"{session.GameName}\" ⚠️ Восстановлено")
                                .WithDescription($"Мастер: {session.MasterName}\n" +
                                               $"Начало: {DiscordTimeFormatter.FullDateTime(session.StartTime)}\n" +
                                               $"Статус: {(session.IsPaused ? "⏸ На паузе" : "▶ В процессе")}\n" +
                                               $"Длительность (активная): {FormatDurationCompact(activeDuration)}\n" +
                                               (session.TrackRolls
                                                   ? (session.TrackRollsAutoEnabled
                                                       ? "Сбор бросков: ✅ Включен (автоматически)\n"
                                                       : "Сбор бросков: ✅ Включен\n")
                                                   : "Сбор бросков: ❌ Выключен\n") +
                                               $"{(string.IsNullOrEmpty(session.EventDescription) ? "" : $"Описание: {session.EventDescription}\n")}" +
                                               $"⚠️ *Сообщение управления было пересоздано после перезапуска бота*")
                                .WithColor(Color.DarkOrange)
                                .Build();

                            // Используем кнопку force_stop для пересозданных сообщений, т.к. обычный
                            // confirm_stop требует DeferAsync который не работает на "свежих" interaction
                            var buttons = new ComponentBuilder()
                                .WithButton("⏸ Пауза", $"pause_session:{session.SessionId}", ButtonStyle.Primary, disabled: session.IsPaused)
                                .WithButton("▶ Продолжить", $"resume_session:{session.SessionId}", ButtonStyle.Success, disabled: !session.IsPaused)
                                .WithButton("✏️ Изменить", $"edit_session:{session.SessionId}", ButtonStyle.Secondary)
                                .WithButton("⚠️ Завершить", $"force_stop:{session.SessionId}", ButtonStyle.Danger);
                            var message = await channel.SendMessageAsync(embed: embed, components: buttons.Build());
                            session.ControlMessageId = message.Id;
                            session.ControlChannelId = channel.Id;
                            commands.StartControlMessageAutoRefresh(session, channel);

                            recreatedCount++;
                            BotLogger.Info(LogCategory.Session, $"Пересоздано сообщение управления для сессии {session.SessionId}");
                            await SaveSessionsAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            BotLogger.Error(LogCategory.Session, $"Ошибка при пересоздании сообщения для сессии {session.SessionId}: {ex.Message}");
                        }
                    }
                }

                if (recreatedCount > 0)
                    BotLogger.Info(LogCategory.Session, $"Пересоздано {recreatedCount} сообщений управления");
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Session, $"Ошибка при пересоздании сообщений: {ex.Message}");
            }
        }

        private async Task<GameSession> StartSessionInternal(
        ulong guildId,
        string gameName,
        SocketGuildUser master,
                string? gameComment = null,
                string? eventDescription = null,
        ulong? eventId = null,
        ulong channelId = 0)
        {
            await _sessionSemaphore.WaitAsync();
            try
            {
                Log($"Попытка создать сессию для гильдии {guildId}, игра: \"{gameName}\"");

                if (eventId.HasValue && _sessions.TryGetValue(guildId, out var guildSessions))
                {
                    var existingSession = guildSessions.Values.FirstOrDefault(s => s.EventId == eventId && !s.IsStopped);
                    if (existingSession != null)
                    {
                        Log($"Найдена существующая активная сессия для события {eventId}: ID {existingSession.SessionId}");
                        return existingSession;
                    }
                }

                var masterDisplayName = master?.DisplayName ?? "Неопознанный мастер";
                // Проверяем маппинг имён в конфиге сервера
                if (master != null)
                {
                    var serverCfg = Program.ServerConfigResolver?.Invoke(guildId);
                    if (serverCfg?.MasterNameMap?.TryGetValue(master.Id.ToString(), out var mappedName) == true
                        && !string.IsNullOrWhiteSpace(mappedName))
                        masterDisplayName = mappedName;
                }

                var newSession = new GameSession
                {
                    SessionId = (ulong)DateTime.Now.Ticks,
                    GuildId = guildId,
                    ChannelId = channelId,
                    GameName = gameName,
                    MasterName = masterDisplayName,
                    MasterId = master?.Id ?? 0,
                    GameComment = gameComment ?? string.Empty,
                    EventDescription = eventDescription ?? string.Empty,
                    StartTime = DateTime.Now,
                    EventId = eventId,
                    TrackRolls = true,
                    TrackRollsAutoEnabled = true
                };

                if (!_sessions.TryGetValue(guildId, out var sessions))
                {
                    sessions = new ConcurrentDictionary<ulong, GameSession>();
                    _sessions[guildId] = sessions;
                    Log($"Создан новый словарь сессий для гильдии {guildId}");
                }

                if (sessions.TryAdd(newSession.SessionId, newSession))
                {
                    Log($"Успешно создана новая сессия: ID {newSession.SessionId}, игра: \"{gameName}\"");
                }
                else
                {
                    Log($"Ошибка при создании сессии для игры \"{gameName}\"");
                }

                return newSession;
            }
            catch (Exception ex)
            {
                Log($"Ошибка при создании сессии: {ex.Message}");
                throw;
            }
            finally
            {
                _sessionSemaphore.Release();
            }
        }

        private ComponentBuilder CreateControlButtons(GameSession session)
        {
            if (session.IsStopped)
                return new ComponentBuilder();

            var builder = new ComponentBuilder()
                .WithButton(session.IsPaused ? "▶️ Продолжить" : "⏸ Пауза",
                    session.IsPaused ? $"resume_session:{session.SessionId}" : $"pause_session:{session.SessionId}",
                    ButtonStyle.Secondary)
                .WithButton("✏️ Изменить", $"edit_session:{session.SessionId}", ButtonStyle.Primary)
                .WithButton("⏹ Завершить", $"stop_session:{session.SessionId}", ButtonStyle.Danger)
                .WithButton(session.TrackRolls ? "🎲 Броски: ✅" : "🎲 Броски: ❌",
                    $"toggle_rolls:{session.SessionId}",
                    ButtonStyle.Success);

            return builder;
        }

        private static TimeSpan CalculateActiveDuration(GameSession session, DateTime now)
        {
            var end = session.EndTime ?? now;
            if (end < session.StartTime)
                return TimeSpan.Zero;

            var total = end - session.StartTime;
            var pause = TimeSpan.Zero;
            foreach (var p in session.PausePeriods)
            {
                var pauseEnd = p.End ?? now;
                if (pauseEnd <= p.Start)
                    continue;
                pause += pauseEnd - p.Start;
            }

            var active = total - pause;
            return active < TimeSpan.Zero ? TimeSpan.Zero : active;
        }

        private static string FormatDurationCompact(TimeSpan value)
        {
            if (value.TotalHours >= 1)
                return $"{(int)value.TotalHours}ч {value.Minutes:00}м";
            return $"{value.Minutes}м";
        }

        private async Task UpdateControlMessage(GameSession session, IMessageChannel channel)
        {
            try
            {
                Log($"Попытка обновить сообщение управления для сессии {session.SessionId}");

                if (session.ControlMessageId == 0)
                {
                    Log($"ControlMessageId = 0 для сессии {session.SessionId}");
                    return;
                }

                var message = await channel.GetMessageAsync(session.ControlMessageId) as IUserMessage;
                if (message == null)
                {
                    Log($"Сообщение {session.ControlMessageId} не найдено, попытка найти по footer сессии...");
                    var messages = await channel.GetMessagesAsync(20).FlattenAsync();
                    message = messages.FirstOrDefault(m =>
                        m.Components.Any(c => c is ActionRowComponent row &&
                            row.Components.Any(b => b is ButtonComponent btn &&
                                btn.CustomId != null && btn.CustomId.EndsWith($":{session.SessionId}")))) as IUserMessage;

                    if (message == null)
                    {
                        Log($"Сообщение для сессии {session.SessionId} не найдено");
                        return;
                    }
                    session.ControlMessageId = message.Id;
                    Log($"Найдено сообщение по footer: ID {message.Id}");
                }

                // Получаем последний период паузы (текущий)
                var currentPause = session.PausePeriods.LastOrDefault();
                var pauseTimeInfo = currentPause.Start != DateTime.MinValue ?
                    $"\nНа паузе с: {DiscordTimeFormatter.TimeOnly(currentPause.Start)}" : "";

                var statusText = session.IsStopped
                    ? "✅ Завершена"
                    : session.IsPaused
                        ? $"⏸ На паузе{pauseTimeInfo}"
                        : "▶ В процессе";

                var now = DateTime.Now;
                var activeDuration = CalculateActiveDuration(session, now);

                var descriptionLines = new List<string>
                {
                    $"Мастер: {session.MasterName}",
                    $"Начало: {DiscordTimeFormatter.FullDateTime(session.StartTime)}",
                    $"Статус: {statusText}",
                    $"Длительность (активная): {FormatDurationCompact(activeDuration)}",
                    session.TrackRolls
                        ? (session.TrackRollsAutoEnabled
                            ? "Сбор бросков: ✅ Включен (автоматически)"
                            : "Сбор бросков: ✅ Включен")
                        : "Сбор бросков: ❌ Выключен",
                };

                if (!string.IsNullOrWhiteSpace(session.EventDescription))
                    descriptionLines.Add($"Описание: {session.EventDescription}");

                if (!string.IsNullOrWhiteSpace(session.GameComment))
                    descriptionLines.Add($"Комментарий: {session.GameComment}");

                var embed = new EmbedBuilder()
                    .WithTitle($"Сессия: \"{session.GameName}\"")
                    .WithDescription(string.Join("\n", descriptionLines))
                    .WithColor(session.IsStopped ? Color.DarkGrey : session.IsPaused ? Color.Orange : Color.Green)
                    .Build();

                await message.ModifyAsync(m =>
                {
                    m.Embed = embed;
                    m.Components = CreateControlButtons(session).Build();
                });

                Log($"Сообщение управления для сессии {session.SessionId} успешно обновлено");
            }
            catch (Exception ex)
            {
                Log($"Ошибка при обновлении сообщения управления: {ex.Message}");
            }
        }

        private void StartControlMessageAutoRefresh(GameSession session, IMessageChannel channel)
        {
            try
            {
                session.ControlMessageUpdateCTS?.Cancel();
                session.ControlMessageUpdateCTS?.Dispose();
            }
            catch
            {
            }

            var cts = new CancellationTokenSource();
            session.ControlMessageUpdateCTS = cts;
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(10), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (token.IsCancellationRequested || session.IsStopped)
                        return;

                    try
                    {
                        await UpdateControlMessage(session, channel).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log($"Ошибка автообновления control message для сессии {session.SessionId}: {ex.Message}");
                    }
                }
            }, token);
        }

        public static async Task OnGuildScheduledEventStarted(SocketGuildEvent guildEvent, DiscordSocketClient client)
        {
            var guildId = guildEvent.Guild.Id;
            var creator = guildEvent.Creator as SocketGuildUser;

            var commands = new GameSessionCommands(client);
            var session = await commands.StartSessionInternal(
                guildId,
                guildEvent.Name,
                creator,
                eventDescription: guildEvent.Description,
                eventId: guildEvent.Id);

            if (session == null)
            {
                commands.Log("Не удалось создать сессию для события");
                return;
            }

            ulong channelId = Program.ServerConfigs.TryGetValue(guildId, out var config)
                ? config.RecordChannelID
                : guildEvent.Guild.SystemChannel.Id;

            if (client.GetChannel(channelId) is ITextChannel channel)
            {
                var activeDuration = CalculateActiveDuration(session, DateTime.Now);
                var embed = new EmbedBuilder()
                    .WithTitle($"Сессия: \"{session.GameName}\"")
                    .WithDescription($"Мастер: {session.MasterName}\n" +
                                   $"Начало: {DiscordTimeFormatter.FullDateTime(session.StartTime)}\n" +
                                   $"Статус: ▶ В процессе\n" +
                                   $"Длительность (активная): {FormatDurationCompact(activeDuration)}\n" +
                                   $"Сбор бросков: ✅ Включен (автоматически)\n" +
                                   $"{(string.IsNullOrEmpty(session.EventDescription) ? "" : $"Описание: {session.EventDescription}")}")
                    .WithColor(Color.Green)
                    .Build();

                var buttons = commands.CreateControlButtons(session);
                var message = await channel.SendMessageAsync(embed: embed, components: buttons.Build());
                session.ControlMessageId = message.Id;
                session.ControlChannelId = channel.Id;
                commands.StartControlMessageAutoRefresh(session, channel);

                _ = Task.Run(() => SaveSessionsAsync());
                commands.Log($"Создано сообщение управления для сессии {session.SessionId} (ID сообщения: {message.Id})");
            }
            else
            {
                commands.Log($"Не удалось найти канал {channelId} для создания сообщения управления");
            }
        }

        [Command("start")]
        public async Task StartGameSession(
            SocketSlashCommand command,
            string gameName,
            SocketUser? masterUser = null,
            string? gameComment = null)
        {
            await command.DeferAsync();
            var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId == null) return;

            var user = command.User as SocketGuildUser;
            var config = Program.ServerConfigResolver?.Invoke(guildId.Value);
            var hasMasterRole = config?.MasterRoleId.HasValue == true && user != null && user.Roles.Any(r => r.Id == config.MasterRoleId.Value);
            var hasSuperUserRole = config?.SuperUserRoleId.HasValue == true && user != null && user.Roles.Any(r => r.Id == config.SuperUserRoleId.Value);
            var isAdmin = user?.GuildPermissions.Administrator ?? false;
            if (!isAdmin && !hasMasterRole && !hasSuperUserRole)
            {
                await SendTemporaryEphemeralResponse(command, "Только мастера могут запускать игру.");
                return;
            }

            var master = masterUser as SocketGuildUser ?? user;
            if (master == null)
            {
                await SendTemporaryEphemeralResponse(command, "Не удалось определить мастера.");
                return;
            }
            var session = await StartSessionInternal(
                guildId.Value,
                gameName,
                master,
                gameComment,
                channelId: command.Channel.Id);

            if (session == null)
            {
                await SendTemporaryEphemeralResponse(command, "Не удалось создать сессию.");
                return;
            }

            var activeDuration = CalculateActiveDuration(session, DateTime.Now);
            var embed = new EmbedBuilder()
                .WithTitle($"Сессия: \"{session.GameName}\"")
                .WithDescription($"Мастер: {session.MasterName}\n" +
                               $"Начало: {DiscordTimeFormatter.FullDateTime(session.StartTime)}\n" +
                               $"Статус: ▶ В процессе\n" +
                               $"Длительность (активная): {FormatDurationCompact(activeDuration)}\n" +
                               $"Сбор бросков: ✅ Включен (автоматически)\n" +
                               $"{(string.IsNullOrEmpty(session.GameComment) ? "" : $"Комментарий: {session.GameComment}")}")
                .WithColor(Color.Green)
                .Build();

            var buttons = CreateControlButtons(session);
            var message = await command.FollowupAsync(embed: embed, components: buttons.Build());
            session.ControlMessageId = message.Id;
            session.ControlChannelId = command.Channel.Id;
            StartControlMessageAutoRefresh(session, command.Channel);

            _ = Task.Run(() => SaveSessionsAsync());
        }

        public async Task HandleControlButton(SocketMessageComponent component)
        {
            var guildId = (component.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId == null) return;

            var channelId = component.Channel.Id;

            var parts = component.Data.CustomId.Split(':');
            if (parts.Length < 2 || !ulong.TryParse(parts[1], out var sessionId))
            {
                await SendTemporaryEphemeralResponse(component, "Не удалось определить сессию.");
                return;
            }

            try
            {
                if (!_sessions.TryGetValue(guildId.Value, out var guildSessions))
                {
                    Log($"[RESTART] Сессии для сервера {guildId} не найдены (вероятно бот был перезагружен)");
                    await SendTemporaryEphemeralResponse(component, "❌ Сессия больше не активна. Это может произойти если бот был перезагружен.\n\nПожалуйста, создайте новую сессию командой `/start`");
                    return;
                }

                if (!guildSessions.TryGetValue(sessionId, out var session))
                {
                    Log($"[RESTART] Сессия {sessionId} не найдена (вероятно бот был перезагружен)");
                    await SendTemporaryEphemeralResponse(component, "❌ Эта сессия больше не активна. Это может произойти если бот был перезагружен.\n\nПожалуйста, создайте новую сессию командой `/start`");
                    return;
                }

                // Edge case: сессия помечена как завершённая, но ещё в словаре
                if (session.IsStopped)
                {
                    RemoveSession(session);
                    await SaveSessionsAsync().ConfigureAwait(false);
                    await component.RespondAsync("ℹ️ Эта сессия уже была завершена.", ephemeral: true);
                    return;
                }

                // Проверка прав: только мастер сессии, пользователь с ролью мастера или администратор
                var guildUser = component.User as SocketGuildUser;
                if (!IsMasterOrAdmin(guildUser, guildId.Value, session))
                {
                    await component.RespondAsync("❌ Только мастер может управлять сессией.", ephemeral: true);
                    return;
                }

                // Защита от параллельных нажатий на кнопки одной сессии
                if (!await session.ButtonSemaphore.WaitAsync(0))
                {
                    await component.RespondAsync("⏳ Идёт обработка предыдущего действия, подождите...", ephemeral: true);
                    return;
                }

                try
                {

                switch (parts[0])
                {
                    case "pause_session":
                    case "resume_session":
                    case "stop_session":
                    case "confirm_stop":
                    case "cancel_stop":
                    case "toggle_rolls":
                        await component.DeferAsync();
                        break;
                }

                switch (parts[0])
                {
                    case "pause_session":
                        await HandlePauseSession(component, session);
                        break;
                    case "resume_session":
                        await HandleResumeSession(component, session);
                        break;
                    case "edit_session":
                        await HandleEditSession(component, session);
                        break;
                    case "stop_session":
                        await HandleStopSession(component, session);
                        break;
                    case "confirm_stop":
                        await HandleConfirmStop(component, session);
                        break;
                    case "cancel_stop":
                        await HandleCancelStop(component, session);
                        break;
                    case "toggle_rolls":
                        await HandleToggleRolls(component, session);
                        break;
                    case "force_stop":
                        await HandleForceStop(component, session);
                        break;
                }

                } // end ButtonSemaphore try
                finally
                {
                    session.ButtonSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка обработки кнопки: {ex.Message}");
            }
        }

        private async Task HandlePauseSession(SocketMessageComponent component, GameSession session)
        {
            if (session.IsPaused)
            {
                Log($"Сессия {session.SessionId} уже на паузе");
                await SendTemporaryEphemeralResponse(component, "Игра уже на паузе.");
                return;
            }

            var pauseStartTime = DateTime.Now;
            session.PausePeriods.Add((pauseStartTime, null));
            session.IsPaused = true;

            Log($"Сессия {session.SessionId} поставлена на паузу в {pauseStartTime:HH:mm:ss}");

            await UpdateControlMessage(session, component.Channel);
            await SendTemporaryEphemeralResponse(component, $"Игра приостановлена в {pauseStartTime:HH:mm}");

            // ✅ НОВОЕ: Сохраняем состояние
            _ = Task.Run(() => SaveSessionsAsync());

            var channel = component.Channel;
            var userId = component.User.Id;

            if (session.PauseReminderCTS != null)
            {
                try { session.PauseReminderCTS.Cancel(); } catch { }
                try { session.PauseReminderCTS.Dispose(); } catch { }
                session.PauseReminderCTS = null;
            }
            session.PauseReminderCTS = new CancellationTokenSource();
            // Захватываем токен локально — иначе при Resume/Dispose поля может возникнуть
            // ObjectDisposedException внутри Task.Run при обращении к session.PauseReminderCTS
            var pauseReminderToken = session.PauseReminderCTS.Token;
            _ = Task.Run(async () =>
            {
                // Первое напоминание через 10 минут от начала паузы
                var nextReminder = TimeSpan.FromMinutes(10);

                while (!pauseReminderToken.IsCancellationRequested)
                {
                    // Ждем до следующего напоминания
                    var delay = nextReminder - (DateTime.Now - pauseStartTime);
                    if (delay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(delay, pauseReminderToken);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }

                    if (pauseReminderToken.IsCancellationRequested)
                        return;

                    try
                    {
                        var duration = DateTime.Now - pauseStartTime;
                        var totalMinutes = (int)duration.TotalMinutes;

                        // Проверяем, что прошло ровное количество 10-минутных интервалов
                        if (totalMinutes % 10 == 0)
                        {
                            Log($"Напоминание о паузе для сессии {session.SessionId} (длительность: {totalMinutes} мин)");

                            var user = await channel.GetUserAsync(userId) as IUser;
                            if (user != null)
                            {
                                var reminderMessage = await channel.SendMessageAsync(
                                    $"{user.Mention}, игра на паузе с {pauseStartTime:HH:mm} (уже {totalMinutes} мин)");

                                session.PauseReminderMessageId = reminderMessage.Id;

                                await Task.Delay(TimeSpan.FromMinutes(2), pauseReminderToken).ContinueWith(_ => { });
                                if (!pauseReminderToken.IsCancellationRequested)
                                {
                                    try { await reminderMessage.DeleteAsync(); } catch { }
                                }
                            }
                        }

                        nextReminder += TimeSpan.FromMinutes(10);
                    }
                    catch (Exception ex)
                    {
                        Log($"Ошибка в напоминании о паузе: {ex.Message}");
                        nextReminder += TimeSpan.FromMinutes(10);
                    }
                }
            }, pauseReminderToken);
        }

        private async Task HandleResumeSession(SocketMessageComponent component, GameSession session)
        {
            if (!session.IsPaused)
            {
                Log($"Попытка возобновить сессию {session.SessionId}, которая не на паузе");
                await SendTemporaryEphemeralResponse(component, "Игра не на паузе.");
                return;
            }

            session.PauseReminderCTS?.Cancel();
            try { session.PauseReminderCTS?.Dispose(); } catch { }
            session.PauseReminderCTS = null;
            var lastPause = session.PausePeriods.Last();
            session.PausePeriods[^1] = (lastPause.Start, DateTime.Now);
            session.IsPaused = false;

            Log($"Сессия {session.SessionId} возобновлена после паузы");

            await UpdateControlMessage(session, component.Channel);
            await SendTemporaryEphemeralResponse(component, "Игра продолжена.");

            // ✅ НОВОЕ: Сохраняем состояние
            _ = Task.Run(() => SaveSessionsAsync());
        }

        private async Task HandleEditSession(SocketMessageComponent component, GameSession session)
        {
            var modal = new ModalBuilder()
                .WithTitle("Изменение параметров игры")
                .WithCustomId($"edit_modal:{session.SessionId}")
                .AddTextInput("Название игры", "game_name", TextInputStyle.Short, value: session.GameName)
                .AddTextInput("Мастер", "game_master", TextInputStyle.Short, value: session.MasterName)
                .AddTextInput("Комментарий", "game_comment", TextInputStyle.Paragraph, value: session.GameComment ?? "", required: false)
                .Build();

            await component.RespondWithModalAsync(modal);
        }

        public async Task HandleEditModal(SocketModal modal)
        {
            await modal.DeferAsync();
            var guildId = (modal.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId == null) return;

            var parts = modal.Data.CustomId.Split(':');
            if (parts.Length < 2 || !ulong.TryParse(parts[1], out var sessionId))
            {
                await SendTemporaryEphemeralResponse(modal, "Не удалось определить сессию.");
                return;
            }

            await _sessionSemaphore.WaitAsync();
            try
            {
                if (!_sessions.TryGetValue(guildId.Value, out var guildSessions))
                {
                    await SendTemporaryEphemeralResponse(modal, "Активные сессии не найдены.");
                    return;
                }

                if (!guildSessions.TryGetValue(sessionId, out var session))
                {
                    await SendTemporaryEphemeralResponse(modal, "Сессия не найдена.");
                    return;
                }

                var newName = (modal.Data.Components.First(x => x.CustomId == "game_name").Value ?? string.Empty).Trim();
                var newMaster = (modal.Data.Components.First(x => x.CustomId == "game_master").Value ?? string.Empty).Trim();
                var newCommentRaw = modal.Data.Components.First(x => x.CustomId == "game_comment").Value;
                var newComment = NormalizeOptionalText(newCommentRaw) ?? string.Empty;
                var oldComment = NormalizeOptionalText(session.GameComment) ?? string.Empty;

                var changes = new List<string>();
                if (session.GameName != newName) changes.Add($"Название: {session.GameName} → {newName}");
                if (session.MasterName != newMaster) changes.Add($"Мастер: {session.MasterName} → {newMaster}");
                if (!string.Equals(oldComment, newComment, StringComparison.Ordinal))
                    changes.Add($"Комментарий: {(string.IsNullOrEmpty(oldComment) ? "(пусто)" : oldComment)} → {(string.IsNullOrEmpty(newComment) ? "(пусто)" : newComment)}");

                if (changes.Count == 0)
                {
                    await modal.FollowupAsync("Изменений не внесено.", ephemeral: true);
                    return;
                }

                session.GameName = newName;
                session.MasterName = newMaster;
                session.GameComment = newComment;

                await UpdateControlMessage(session, modal.Channel);
                await SendTemporaryEphemeralResponse(modal, $"Изменения сохранены:\n{string.Join("\n", changes)}");

                // Обновляем строку в Google Sheets если сессия уже была туда записана
                if (_googleSheets != null && session.SheetRowIndex > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _googleSheets.UpdateSessionRowAsync(session).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Log($"Ошибка обновления Google Sheets: {ex.Message}");
                        }
                    });
                }
            }
            finally
            {
                _sessionSemaphore.Release();
            }
        }

        private static string? NormalizeOptionalText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return value.Trim();
        }

        /// <summary>
        /// Возвращает true если пользователь — администратор, имеет роль мастера,
        /// имеет специальную роль суперпользователя,
        /// или является мастером этой конкретной сессии.
        /// </summary>
        private static bool IsMasterOrAdmin(SocketGuildUser? user, ulong guildId, GameSession session)
        {
            if (user == null) return false;
            if (user.GuildPermissions.Administrator) return true;
            // Мастер самой сессии всегда имеет право
            if (session.MasterId != 0 && user.Id == session.MasterId) return true;
            // Пользователь с ролью мастера в конфиге сервера
            var config = Program.ServerConfigResolver?.Invoke(guildId);
            if (config?.MasterRoleId.HasValue == true && user.Roles.Any(r => r.Id == config.MasterRoleId.Value))
                return true;
            // Пользователь со специальной ролью суперпользователя
            if (config?.SuperUserRoleId.HasValue == true && user.Roles.Any(r => r.Id == config.SuperUserRoleId.Value))
                return true;
            return false;
        }

        private async Task HandleStopSession(SocketMessageComponent component, GameSession session)
        {
            Log($"Пользователь {component.User.Id} запросил остановку сессии {session.SessionId} ({session.GameName})");

            var confirmBuilder = new ComponentBuilder()
                .WithButton("✅ Да", $"confirm_stop:{session.SessionId}", ButtonStyle.Danger)
                .WithButton("❌ Нет", $"cancel_stop:{session.SessionId}", ButtonStyle.Secondary);

            try
            {
                await component.Message.ModifyAsync(m =>
                {
                    m.Components = confirmBuilder.Build();
                });
                Log($"Кнопки подтверждения остановки для сессии {session.SessionId} успешно обновлены");
            }
            catch (Exception ex)
            {
                Log($"Ошибка при обновлении кнопок подтверждения остановки: {ex.Message}");
            }

            var confirmMessage = await component.FollowupAsync("Вы уверены, что хотите завершить игру?", ephemeral: true);
            session.ConfirmationMessageId = confirmMessage.Id;

            // Удаляем через 10 секунд
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                try
                {
                    await confirmMessage.DeleteAsync();
                }
                catch { }
                session.ConfirmationMessageId = null;
            });
        }

        private async Task HandleConfirmStop(SocketMessageComponent component, GameSession session)
        {
            // ✅ DeferAsync уже вызван в HandleControlButton (строка 6079), не дублируем!
            Log($"Попытка подтверждения остановки сессии {session.SessionId}. Текущий счётчик семафора: {_sessionSemaphore.CurrentCount}");

            try
            {
                // Убедитесь, что сессия существует
                if (session == null)
                {
                    await SendTemporaryEphemeralResponse(component, "Сессия не найдена.");
                    return;
                }

                Log($"Начало обработки остановки сессии {session.SessionId}...");

                if (session.IsPaused)
                {
                    Log($"Снятие паузы для сессии {session.SessionId}...");
                    session.PauseReminderCTS?.Cancel();
                    var lastPause = session.PausePeriods.Last();
                    session.PausePeriods[^1] = (lastPause.Start, DateTime.Now);
                    session.IsPaused = false;
                }

                session.EndTime = DateTime.Now;
                Log($"Время окончания установлено: {session.EndTime}, сессия помечена как остановленная (IsStopped={session.IsStopped})");


                if (session.EventId.HasValue)
                {
                    try
                    {
                        var guild = _client.GetGuild(session.GuildId);
                        var guildEvent = guild != null ? await guild.GetEventAsync(session.EventId.Value) : null;
                        if (guildEvent?.Status == GuildScheduledEventStatus.Active)
                        {
                            await guildEvent.ModifyAsync(props => props.Status = GuildScheduledEventStatus.Completed);
                            Log($"Связанное событие {session.EventId} помечено как завершённое");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Ошибка завершения события: {ex.Message}");
                    }
                }

                // ВАЖНО: Сначала удаляем контрольное сообщение, потом выводим статистику
                await DeleteControlMessageAsync(session, component.Channel);

                await SendSessionStats(session, component.Channel);

                // Очистка сессии происходит:
                // 1. В SendSessionStats → RemoveSession() если нет бросков (строка 6621)
                // 2. В обработке кнопок статистики (no_stats, general_stats, detailed_stats)
                //    после вывода статистики - там вызывается RemoveSession()
            }
            catch (Exception ex)
            {
                // ✅ ДОБАВЛЕНО: логирование ошибки
                Log($"КРИТИЧЕСКАЯ ОШИБКА в HandleConfirmStop для сессии {session.SessionId}: {ex.Message}");
                Log($"StackTrace: {ex.StackTrace}");
            }
            finally
            {
                //_sessionSemaphore.Release();
                //Log($"Семафор освобожден. Текущий счётчик: {_sessionSemaphore.CurrentCount}");
            }
        }

        private IMessageChannel? ResolveControlChannel(GameSession session, IMessageChannel? overrideChannel = null)
        {
            if (overrideChannel != null)
                return overrideChannel;

            // Используем ControlChannelId — точный канал, в котором было создано сообщение управления
            if (session.ControlChannelId != 0)
            {
                var channel = _client.GetChannel(session.ControlChannelId) as IMessageChannel
                    ?? _client.GetGuild(session.GuildId)?.GetTextChannel(session.ControlChannelId);
                if (channel != null)
                    return channel;
            }

            // Fallback: канал, переданный при создании сессии через /start (старые сессии без ControlChannelId)
            if (session.ChannelId != 0)
            {
                var channel = _client.GetChannel(session.ChannelId) as IMessageChannel
                    ?? _client.GetGuild(session.GuildId)?.GetTextChannel(session.ChannelId);
                if (channel != null)
                    return channel;
            }

            return null;
        }

        private async Task DeleteControlMessageAsync(GameSession session, IMessageChannel? channel = null)
        {
            if (session.ControlMessageId == 0)
                return;

            var targetChannel = ResolveControlChannel(session, channel);
            if (targetChannel == null)
            {
                Log($"Не удалось определить канал управления для удаления message_id={session.ControlMessageId}");
                session.ControlMessageId = 0;
                return;
            }

            try
            {
                var message = await targetChannel.GetMessageAsync(session.ControlMessageId).ConfigureAwait(false);
                if (message != null)
                {
                    await message.DeleteAsync().ConfigureAwait(false);
                    Log($"Сообщение управления {session.ControlMessageId} удалено");
                }
                else
                {
                    Log($"Сообщение управления {session.ControlMessageId} не найдено в канале при удалении");
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка удаления сообщения управления: {ex.Message}");
            }
            finally
            {
                session.ControlMessageId = 0;
            }
        }

        private async Task HandleCancelStop(SocketMessageComponent component, GameSession session)
        {
            Log($"Пользователь {component.User.Id} отменил остановку сессии {session.SessionId}");

            try
            {
                await UpdateControlMessage(session, component.Channel);
                LogDebug($"Сообщение управления сессии {session.SessionId} успешно восстановлено");
            }
            catch (Exception ex)
            {
                LogWarn($"Ошибка при восстановлении сообщения управления: {ex.Message}");
            }

            await SendTemporaryEphemeralResponse(component, "Отмена завершения игры.");
        }

        /// <summary>
        /// Принудительное завершение сессии для пересозданных control messages после перезапуска бота.
        /// Использует RespondAsync вместо DeferAsync + FollowupAsync, так как это свежий interaction.
        /// </summary>
        private async Task HandleForceStop(SocketMessageComponent component, GameSession session)
        {
            Log($"Принудительное завершение сессии {session.SessionId} ({session.GameName}) пользователем {component.User.Id}");

            try
            {
                await component.RespondAsync("⏳ Завершение сессии...", ephemeral: true);

                if (session.IsPaused)
                {
                    session.PauseReminderCTS?.Cancel();
                    var lastPause = session.PausePeriods.LastOrDefault();
                    if (lastPause.Start != default)
                        session.PausePeriods[^1] = (lastPause.Start, DateTime.Now);
                    session.IsPaused = false;
                }

                session.EndTime = DateTime.Now;

                if (session.EventId.HasValue)
                {
                    try
                    {
                        var guild = _client.GetGuild(session.GuildId);
                        var guildEvent = guild != null ? await guild.GetEventAsync(session.EventId.Value) : null;
                        if (guildEvent?.Status == GuildScheduledEventStatus.Active)
                        {
                            await guildEvent.ModifyAsync(props => props.Status = GuildScheduledEventStatus.Completed);
                            Log($"Связанное событие {session.EventId} помечено как завершённое");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Ошибка завершения события: {ex.Message}");
                    }
                }

                await DeleteControlMessageAsync(session, component.Channel);
                await SendSessionStats(session, component.Channel);
            }
            catch (Exception ex)
            {
                LogError($"Ошибка в HandleForceStop для сессии {session.SessionId}: {ex.Message}");
                try { await component.ModifyOriginalResponseAsync(m => m.Content = "❌ Ошибка при завершении сессии."); } catch { }
            }
        }

        private async Task HandleToggleRolls(SocketMessageComponent component, GameSession session)
        {
            session.TrackRolls = !session.TrackRolls;
            // При ручном включении снимаем пометку "автоматически"
            if (session.TrackRolls)
                session.TrackRollsAutoEnabled = false;
            await UpdateControlMessage(session, component.Channel);
            await SendTemporaryEphemeralResponse(component, $"Сбор статистики бросков {(session.TrackRolls ? "включен" : "выключен")}.");

            // ✅ НОВОЕ: Сохраняем состояние
            _ = Task.Run(() => SaveSessionsAsync());
        }

        public static async Task OnGuildScheduledEventCompleted(SocketGuildEvent guildEvent, DiscordSocketClient client, GoogleSheetsService? googleSheets = null, Action<string>? logSink = null)
        {
            var commands = new GameSessionCommands(client, googleSheets);
            commands._logSinkOverride = logSink;
            LogDebug($"Событие {guildEvent.Id} завершено - обработка связанной сессии...");

            try
            {
                var guildId = guildEvent.Guild.Id;

                await _sessionSemaphore.WaitAsync();
                try
                {
                    LogDebug($"Поиск сессий для гильдии {guildId} и события {guildEvent.Id}...");

                    if (_sessions.TryGetValue(guildId, out var guildSessions))
                    {
                        var session = guildSessions.Values.FirstOrDefault(s => s.EventId == guildEvent.Id);
                        if (session != null)
                        {
                            LogDebug($"Найдена сессия {session.SessionId} для завершения");

                            if (session.IsPaused)
                            {
                                LogDebug($"Снятие паузы для сессии {session.SessionId}...");
                                try { session.PauseReminderCTS?.Cancel(); } catch { }
                                try { session.PauseReminderCTS?.Dispose(); } catch { }
                                session.PauseReminderCTS = null;
                                var lastPause = session.PausePeriods.Last();
                                session.PausePeriods[^1] = (lastPause.Start, DateTime.Now);
                                session.IsPaused = false;
                            }

                            session.EndTime = DateTime.Now;
                            LogDebug($"Установлено время окончания для сессии {session.SessionId}");

                            var recordChannelId = Program.ServerConfigResolver?.Invoke(guildId)?.RecordChannelID ?? 0;
                            // Fallback: если RecordChannelID не задан — используем канал control message
                            if (recordChannelId == 0)
                                recordChannelId = session.ControlChannelId;
                            var channel = client.GetChannel(recordChannelId) as ISocketMessageChannel
                                ?? client.GetGuild(guildId)?.GetTextChannel(recordChannelId) as ISocketMessageChannel;

                            if (channel != null)
                            {
                                LogDebug($"Отправка статистики для сессии {session.SessionId} в канал {recordChannelId}...");
                                // Сначала удаляем сообщение управления, затем отправляем статистику
                                await commands.DeleteControlMessageAsync(session);
                                await commands.SendSessionStats(session, channel);
                            }
                            else
                            {
                                LogWarn($"Канал для статистики не найден (RecordChannelID={recordChannelId})");
                                await commands.DeleteControlMessageAsync(session);
                            }
                        }
                        else
                        {
                            LogDebug($"Активная сессия для события {guildEvent.Id} не найдена");
                        }
                    }
                    else
                    {
                        LogDebug($"Активные сессии для гильдии {guildId} не найдены");
                    }
                }
                finally
                {
                    _sessionSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                LogError($"Критическая ошибка при обработке завершения события: {ex}");
            }
        }

        private string BuildSessionStats(GameSession session)
        {
            var totalDuration = session.EndTime!.Value - session.StartTime;
            var pauseDuration = session.PausePeriods
                .Where(p => p.End.HasValue)
                            .Sum(p => (p.End!.Value - p.Start).TotalSeconds);
            var activeDuration = totalDuration.TotalSeconds - pauseDuration;

            var message = new StringBuilder();
            message.AppendLine($"# Игра **\"{session.GameName}\"** завершена");
            message.AppendLine($"- **Мастер:** {session.MasterName}");
            message.AppendLine($"- **Начало:** {DiscordTimeFormatter.FullDateTime(session.StartTime)}");
            message.AppendLine($"- **Конец:** {DiscordTimeFormatter.FullDateTime(session.EndTime!.Value)}");
            message.AppendLine($"- **Общее время:** {FormatTimeSpan(totalDuration)}");
            if (session.PausePeriods.Any())
            {
                message.AppendLine($"- **Активное время:** {FormatTimeSpan(TimeSpan.FromSeconds(activeDuration))}");

                var totalPauseDuration = TimeSpan.FromSeconds(pauseDuration);
                var pauseCount = session.PausePeriods.Count(p => p.End.HasValue);

                if (pauseCount == 1)
                {
                    message.AppendLine($"- **Продолжительность перерыва:** {FormatTimeSpan(totalPauseDuration)}");
                }
                else if (pauseCount > 1)
                {
                    message.AppendLine($"- **Продолжительность перерывов:** {FormatTimeSpan(totalPauseDuration)}");
                }
            }

            if (!string.IsNullOrEmpty(session.EventDescription))
                message.AppendLine($"- **Описание события:** {session.EventDescription}");

            if (!string.IsNullOrEmpty(session.GameComment))
                message.AppendLine($"- **Комментарий:** {session.GameComment}");

            if (session.PausePeriods.Any())
            {
                message.AppendLine("## Перерывы:");
                foreach (var pause in session.PausePeriods)
                    message.AppendLine($"- {DiscordTimeFormatter.TimeOnly(pause.Start)} — {(pause.End.HasValue ? DiscordTimeFormatter.TimeOnly(pause.End.Value) : "не завершён")}");
            }

            return message.ToString();
        }

        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            var hours = (int)timeSpan.TotalHours;
            var minutes = timeSpan.Minutes;
            //var seconds = timeSpan.Seconds;

            var hoursText = hours > 0 ? $"{hours} час{(hours == 1 ? "" : hours < 5 ? "а" : "ов")}" : "";
            var minutesText = minutes > 0 ? $"{minutes} минут{(minutes == 1 ? "а" : minutes < 5 ? "ы" : "")}" : "";
            //var secondsText = seconds > 0 ? $"{seconds} секунд{(seconds == 1 ? "а" : seconds < 5 ? "ы" : "")}" : "";

            if (string.IsNullOrEmpty(hoursText) && string.IsNullOrEmpty(minutesText))
                return "0 минут";
            
            return string.Join(" ", new[] { hoursText, minutesText }.Where(s => !string.IsNullOrEmpty(s)));
            //return string.Join(" ", new[] { hoursText, minutesText, secondsText }.Where(s => !string.IsNullOrEmpty(s)));
        }

        private async Task SendSessionStats(GameSession session, ISocketMessageChannel channel)
        {
            lock (session.StatsSync)
            {
                if (session.StatsSent)
                {
                    LogDebug($"Статистика для сессии {session.SessionId} уже была отправлена");
                    return;
                }

                session.StatsSent = true;
            }

            LogDebug($"Формирование статистики для сессии {session.SessionId}...");

            try
            {
                var statsMessage = BuildSessionStats(session);
                await channel.SendMessageAsync(statsMessage);
                LogDebug($"Статистика по времени для сессии {session.SessionId} отправлена");

                if (session.Rolls.Count > 0)
                {
                    LogDebug($"Сессия {session.SessionId} содержит {session.Rolls.Count} бросков - подготовка кнопок статистики");

                    if (Program.ServerConfigs.TryGetValue(session.GuildId, out var config))
                    {
                        if (_client.GetChannel(config.StatsChannelID) is ITextChannel statsChannel)
                        {
                            var buttons = new ComponentBuilder()
                                .WithButton("Не надо", "no_stats", ButtonStyle.Secondary)
                                .WithButton("Общая", "general_stats", ButtonStyle.Primary)
                                .WithButton("Подробная", "detailed_stats", ButtonStyle.Primary)
                                .Build();

                            var masterMention = session.MasterId != 0
                                ? MentionUtils.MentionUser(session.MasterId)
                                : session.MasterName;

                            var buttonsMsg = await statsChannel.SendMessageAsync(
                                $"{masterMention}, какую статистику бросков вывести для игры `{session.GameName}`? Нажми на одну из кнопок ниже",
                                components: buttons);

                            session.StatsMessageId = buttonsMsg.Id;
                            LogDebug($"Кнопки статистики отправлены в канал {statsChannel.Id}, ID сообщения: {buttonsMsg.Id}");
                        }
                        else
                        {
                            LogWarn($"Канал статистики {config.StatsChannelID} не найден");
                        }
                    }
                    else
                    {
                        LogWarn($"Конфигурация сервера {session.GuildId} не найдена");
                    }
                }
                else
                {
                    LogDebug($"Сессия {session.SessionId} не содержит бросков - немедленное удаление");
                    RemoveSession(session);
                    _ = Task.Run(() => SaveSessionsAsync());
                }

                // Google Sheets — в последнюю очередь, после всех Discord-сообщений
                if (_googleSheets != null)
                {
                    try
                    {
                        var row = await _googleSheets.AppendSessionAsync(session).ConfigureAwait(false);
                        if (row > 0)
                        {
                            session.SheetRowIndex = row;
                        }
                        else
                        {
                            LogWarn($"[Sheets] Запись не удалась — AppendSessionAsync вернул {row}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError($"[Sheets] Ошибка записи: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                else
                {
                    LogDebug($"[Sheets] Сервис не инициализирован — запись пропущена");
                }
            }
            catch (Exception ex)
            {
                LogError($"Ошибка при отправке статистики: {ex.Message}");
            }
        }

        private void RemoveSession(GameSession session)
        {
            try
            {
                // Отменяем все pending операции
                session.PauseReminderCTS?.Cancel();
                session.PauseReminderCTS?.Dispose();
                session.ControlMessageUpdateCTS?.Cancel();
                session.ControlMessageUpdateCTS?.Dispose();

                if (_sessions.TryGetValue(session.GuildId, out var guildSessions))
                {
                    if (guildSessions.TryRemove(session.SessionId, out _))
                    {
                        LogDebug($"Сессия {session.SessionId} успешно удалена из словаря");
                    }
                    else
                    {
                        LogWarn($"Не удалось удалить сессию {session.SessionId} из словаря");
                    }

                    if (guildSessions.IsEmpty)
                    {
                        if (_sessions.TryRemove(session.GuildId, out _))
                        {
                            LogDebug($"Словарь сессий для гильдии {session.GuildId} удален (пуст)");
                        }
                    }
                }
                else
                {
                    LogDebug($"Не найден словарь сессий для гильдии {session.GuildId} при удалении");
                }

                try
                {
                    var guild = _client.GetGuild(session.GuildId);
                    if (guild == null) return;

                    // Используем ControlChannelId — точный канал control message (напоминания о паузе тоже там)
                    var channelId = session.ControlChannelId != 0
                        ? session.ControlChannelId
                        : session.ChannelId;
                    var channel = guild.GetTextChannel(channelId);
                    if (channel == null) return;

                    // Удаляем последнее напоминание о паузе
                    if (session.PauseReminderMessageId.HasValue)
                    {
                        _ = channel.DeleteMessageAsync(session.PauseReminderMessageId.Value)
                            .ContinueWith(t => { if (t.IsFaulted) LogWarn($"Ошибка при удалении напоминания о паузе: {t.Exception?.InnerException?.Message}"); });
                    }

                    // Удаляем сообщение подтверждения остановки
                    if (session.ConfirmationMessageId.HasValue)
                    {
                        _ = channel.DeleteMessageAsync(session.ConfirmationMessageId.Value)
                            .ContinueWith(t => { if (t.IsFaulted) LogWarn($"Ошибка при удалении подтверждения остановки: {t.Exception?.InnerException?.Message}"); });
                    }
                }
                catch (Exception ex)
                {
                    LogWarn($"Ошибка при очистке сообщений сессии: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                LogWarn($"Ошибка при очистке ресурсов сессии: {ex.Message}");
            }
        }

        public async Task HandleStatsButton(SocketMessageComponent component)
        {
            var guildId = (component.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId == null)
            {
                LogWarn("Не удалось получить ID гильдии при обработке кнопки статистики");
                return;
            }

            await _sessionSemaphore.WaitAsync();
            try
            {
                LogDebug($"Обработка кнопки статистики для гильдии {guildId}");

                if (!_sessions.TryGetValue(guildId.Value, out var guildSessions))
                {
                    LogWarn($"[RESTART] Активные сессии для гильдии {guildId} не найдены (вероятно бот был перезагружен)");
                    try { await component.Message.DeleteAsync(); } catch { }
                    await component.RespondAsync("❌ Сессия больше не активна.\n\nЭто может произойти если бот был перезагружен. Статистика была потеряна.", ephemeral: true);
                    return;
                }

                var session = guildSessions.Values.FirstOrDefault(s => s.StatsMessageId == component.Message.Id);
                if (session == null)
                {
                    LogWarn($"[RESTART] Сессия для сообщения статистики {component.Message.Id} не найдена");
                    try { await component.Message.DeleteAsync(); } catch { }
                    await component.RespondAsync("❌ Сессия не найдена.\n\nЭто может произойти если бот был перезагружен.", ephemeral: true);
                    return;
                }

                LogDebug($"Найдена сессия {session.SessionId} для обработки статистики");

                switch (component.Data.CustomId)
                {
                    case "no_stats":
                        await component.Message.DeleteAsync();
                        RemoveSession(session);
                        _ = Task.Run(() => SaveSessionsAsync());
                        LogDebug($"Статистика для сессии {session.SessionId} отклонена, сессия удалена");
                        break;

                    case "general_stats":
                        await ShowGeneralStats(component, session);
                        RemoveSession(session);
                        _ = Task.Run(() => SaveSessionsAsync());
                        LogDebug($"Показана общая статистика для сессии {session.SessionId}, сессия удалена");
                        break;

                    case "detailed_stats":
                        await ShowDetailedStats(component, session);
                        RemoveSession(session);
                        _ = Task.Run(() => SaveSessionsAsync());
                        LogDebug($"Показана детальная статистика для сессии {session.SessionId}, сессия удалена");
                        break;
                }
            }
            finally
            {
                _sessionSemaphore.Release();
            }
        }

        private async Task ShowGeneralStats(SocketMessageComponent component, GameSession session)
        {
            LogDebug($"Формирование общей статистики для сессии {session.SessionId}");

            // ✅ УЛУЧШЕНО: Группируем по типам кубиков
            var rollsByDiceType = session.Rolls
                .GroupBy(r => r.DiceType ?? "unknown")
                .OrderBy(g => g.Key)
                .ToList();

            var message = new StringBuilder("**Общая статистика бросков:**\n");

            // Если только один тип куба - показываем просто по значениям
            if (rollsByDiceType.Count == 1)
            {
                var diceType = rollsByDiceType[0].Key;
                var rolls = rollsByDiceType[0]
                    .GroupBy(r => r.RollValue)
                    .Select(g => new { Value = g.Key, Count = g.Count() })
                    .OrderBy(g => g.Value);

                var averageValue = rollsByDiceType[0].Average(r => r.RollValue);

                message.AppendLine($"**{diceType}:**");
                foreach (var roll in rolls)
                {
                    message.AppendLine($"{roll.Value}: {roll.Count} раз");
                }
                message.AppendLine($"**Среднее:** {averageValue:F2}");
            }
            else
            {
                // Если несколько типов - группируем по типам
                foreach (var diceGroup in rollsByDiceType)
                {
                    var diceType = diceGroup.Key;
                    var rolls = diceGroup
                        .GroupBy(r => r.RollValue)
                        .Select(g => new { Value = g.Key, Count = g.Count() })
                        .OrderBy(g => g.Value);

                    var averageValue = diceGroup.Average(r => r.RollValue);

                    message.AppendLine($"**{diceType}:**");
                    foreach (var roll in rolls)
                    {
                        message.AppendLine($"{roll.Value}: {roll.Count} раз");
                    }
                    message.AppendLine($"Среднее: {averageValue:F2}");
                    message.AppendLine();
                }
            }

            await component.Channel.SendMessageAsync(message.ToString());
            await component.Message.DeleteAsync();
        }

        private async Task ShowDetailedStats(SocketMessageComponent component, GameSession session)
        {
            LogDebug($"Формирование детальной статистики для сессии {session.SessionId}");

            // ✅ УЛУЧШЕНО: Группируем по типам кубиков для каждого игрока
            var players = session.Rolls
                .GroupBy(r => r.PlayerName)
                .Select(g => new {
                    Player = g.Key,
                    RollsByDiceType = g.GroupBy(r => r.DiceType ?? "unknown")
                        .OrderBy(dg => dg.Key)
                        .Select(dg => new {
                            DiceType = dg.Key,
                            Rolls = dg.GroupBy(r => r.RollValue)
                                .Select(r => new { Value = r.Key, Count = r.Count() })
                                .OrderBy(r => r.Value)
                                .ToList(),
                            Average = dg.Average(r => r.RollValue)
                        })
                        .ToList(),
                    OverallAverage = g.Average(r => r.RollValue)
                });

            var message = new StringBuilder("**Подробная статистика бросков:**\n");
            foreach (var player in players)
            {
                message.AppendLine($"*{player.Player}:*");

                // Если только один тип куба - не показываем тип
                if (player.RollsByDiceType.Count == 1)
                {
                    var diceStats = player.RollsByDiceType[0];
                    foreach (var roll in diceStats.Rolls)
                    {
                        message.AppendLine($"{roll.Value}: {roll.Count} раз");
                    }
                    message.AppendLine($"**Среднее:** {diceStats.Average:F2}");
                    message.AppendLine();
                }
                else
                {
                    // Если несколько типов - показываем с разделением
                    foreach (var diceStats in player.RollsByDiceType)
                    {
                        message.AppendLine($"**{diceStats.DiceType}:**");
                        foreach (var roll in diceStats.Rolls)
                        {
                            message.AppendLine($"{roll.Value}: {roll.Count} раз");
                        }
                        message.AppendLine($"Среднее: {diceStats.Average:F2}");
                    }
                    message.AppendLine($"**Общее среднее:** {player.OverallAverage:F2}");
                    message.AppendLine();
                }
            }

            await component.Channel.SendMessageAsync(message.ToString());
            await component.Message.DeleteAsync();
        }

        private async Task SendTemporaryEphemeralResponse(SocketInteraction interaction, string message)
        {
            var response = await interaction.FollowupAsync(message, ephemeral: true);
            _ = Task.Run(async () =>
            {
                await Task.Delay(10000);
                try { await response.DeleteAsync(); } catch { }
            });
        }

        /// <summary>
        /// Вызывается при ежедневной перезагрузке: удаляет висящие кнопки статистики
        /// и отправляет сообщение об очистке вместо них. Затем очищает только завершённые сессии.
        /// Активные (незавершённые) сессии не трогаются.
        /// </summary>
        public static async Task ClearSessionsOnDailyRestartAsync(DiscordSocketClient client)
        {
            var sessionsToRemove = new List<GameSession>();

            foreach (var guildEntry in _sessions)
            {
                var guildId = guildEntry.Key;
                if (!Program.ServerConfigs.TryGetValue(guildId, out var config))
                    continue;

                var statsChannel = client.GetChannel(config.StatsChannelID) as ITextChannel;

                foreach (var session in guildEntry.Value.Values)
                {
                    // Трогаем только завершённые сессии с висящим сообщением статистики
                    if (!session.IsStopped || session.StatsMessageId == 0)
                        continue;

                    sessionsToRemove.Add(session);

                    try
                    {
                        // Удаляем висящее сообщение с кнопками
                        if (statsChannel != null)
                        {
                            try
                            {
                                var msg = await statsChannel.GetMessageAsync(session.StatsMessageId);
                                if (msg != null)
                                    await msg.DeleteAsync();
                            }
                            catch { }

                            await statsChannel.SendMessageAsync(
                                $"📋 Статистика бросков по игре **{session.GameName}** очищена (ежедневная перезагрузка).");
                        }
                    }
                    catch { }
                }
            }

            // Удаляем только завершённые сессии с висящей статистикой
            foreach (var session in sessionsToRemove)
            {
                if (_sessions.TryGetValue(session.GuildId, out var guildSessions))
                {
                    guildSessions.TryRemove(session.SessionId, out _);
                    if (guildSessions.IsEmpty)
                        _sessions.TryRemove(session.GuildId, out _);
                }
            }

            if (sessionsToRemove.Count > 0)
                await SaveSessionsAsync();
        }
    }


}