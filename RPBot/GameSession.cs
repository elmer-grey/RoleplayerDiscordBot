using Discord;
using Discord.Commands;
using Discord.WebSocket;
using RPBot;
using RPBot.Startup;
using RPBot.Util;
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
                /// <summary>Стартовое время "активной фазы" — обнуляется при Resume после паузы, чтобы
                /// авто-таймер обновления сообщения управления считал отсюда.</summary>
                public DateTime ActiveStartTime { get; set; }
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

    /// <summary>
    /// ✅ Round 7-C8: мост к PredictionService из статических методов очистки
    /// сессий. Program.cs при инициализации устанавливает accessor, а cleanup
    /// вызывает его после финализации осиротевшей сессии, чтобы отменить
    /// связанный с событием прогноз.
    /// </summary>
    public static class GameSessionPredictionBridge
    {
        public static Func<PredictionService?>? PredictionServiceAccessor { get; set; }
    }

        public class GameSessionCommands : ModuleBase<SocketCommandContext>
        {
        private readonly DiscordSocketClient _client;
        private readonly GoogleSheetsService? _googleSheets;
        internal Action<string>? _logSinkOverride;
        public static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, GameSession>> _sessions = new();
            // Завершённые сессии хранятся в памяти и в файле до тех пор, пока
            // связанное Discord-событие активно. Нужно, чтобы кнопки статистики
            // продолжали работать после перезагрузки бота.
            public static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, GameSession>> _stoppedSessions = new();
            // Per-guild: параллельные команды из разных гильдий не должны блокировать друг друга.
            // Внутри одной гильдии по-прежнему сериализуем доступ к _sessions / диску.
            private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> _guildSemaphores = new();
            private static SemaphoreSlim GetGuildSemaphore(ulong guildId)
                => _guildSemaphores.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
            // Старое имя оставлено как алиас для редких путей без guildId (если такие остались).
            private static readonly SemaphoreSlim _sessionSemaphore = new(1, 1);
            private static readonly SemaphoreSlim _saveSessionsSemaphore = new(1, 1);

        private static readonly string _sessionsStatePath = BotConfig.ResolvePath(Path.Combine(BotConfig.DataFolderName, "sessions_state.json"));

        public GameSessionCommands(DiscordSocketClient client, GoogleSheetsService? googleSheets = null)
        {
            _client = client;
            _googleSheets = googleSheets;
        }

        /// <summary>
        /// ✅ Round 7-C8: после финализации осиротевшей сессии попробовать отменить
        /// связанный с её событием прогноз. Вызывается из FinalizeSessionAsOrphanAsync
        /// и из CleanupStaleSessionByEventAsync. Ничего не делает, если accessor
        /// не установлен или активного прогноза, привязанного к этому событию, нет.
        /// </summary>
        private static async Task TryCancelLinkedPredictionAsync(GameSession session, string reason)
        {
            try
            {
                if (!session.EventId.HasValue) return;
                var accessor = GameSessionPredictionBridge.PredictionServiceAccessor;
                var predictionService = accessor?.Invoke();
                if (predictionService == null) return;

                var (ok, error) = await predictionService.CancelPredictionForEventAsync(
                    session.GuildId, session.EventId.Value, reason).ConfigureAwait(false);
                if (!ok && !string.IsNullOrEmpty(error))
                {
                    BotLogger.Warn(LogCategory.Session,
                        $"[STALE] Не удалось отменить связанный прогноз для сессии {session.SessionId} (event={session.EventId}): {error}");
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn(LogCategory.Session,
                    $"[STALE] Ошибка TryCancelLinkedPredictionAsync для сессии {session.SessionId}: {ex.Message}");
            }
        }

        private void Log(string message)
            => BotLogger.Info(LogCategory.Session, message);

        private static void LogDebug(string message)
            => BotLogger.Debug(LogCategory.Session, message);

        private static void LogWarn(string message)
            => BotLogger.Warn(LogCategory.Session, message);

        private static void LogError(string message)
            => BotLogger.Error(LogCategory.Session, message);

        /// <summary>
        /// Пишет строку в визуализацию старта (ЭТАП 3) через StartupRenderer.
        /// StartupRenderer уже подключён к FileSink, который зеркалит в
        /// logs/run.log — поэтому НЕ зовём BotLogger параллельно для той же
        /// строки: иначе в run.log/GUI появится логический дубль (с [DEBUG]
        /// и без). Если нужна и категорийная запись для расследования, пишите
        /// туда ОТДЕЛЬНУЮ по смыслу строку, не копию этой.
        ///
        /// Вне старта (после EndStartup) StartupRenderer может быть в неактивном
        /// состоянии, но sinks продолжают работать — поэтому ничего не теряется.
        /// </summary>
        private static void WriteStageLine(string message)
        {
            try
            {
                StartupRenderer.Instance.WriteLine(message);
            }
            catch
            {
                // Вне UI-режима — тихо игнорируем.
            }
        }

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
                                    sessionsToSave[$"{guild.Key}:{session.SessionId}"] = BuildSessionSnapshot(session);
                                }
                            }

                            // Сохраняем и архивные сессии (завершённые, но ещё ожидающие выбора статистики).
                            foreach (var guild in _stoppedSessions)
                            {
                                foreach (var session in guild.Value.Values)
                                {
                                    sessionsToSave[$"{guild.Key}:{session.SessionId}"] = BuildSessionSnapshot(session);
                                }
                            }

                            var json = System.Text.Json.JsonSerializer.Serialize(sessionsToSave, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                            // Атомарная запись + межпроцессная блокировка (advisory).
                            FileStream? lockHandle = SafeJsonIO.AcquireLock(_sessionsStatePath, retries: 5, retryDelayMs: 50);
                            try
                            {
                                await SafeJsonIO.WriteAtomicAsync(_sessionsStatePath, json).ConfigureAwait(false);
                            }
                            finally
                            {
                                lockHandle?.Dispose();
                            }

                            if (sessionsToSave.Count > 0)
                            {
                                var totalRolls = _sessions.Values.SelectMany(g => g.Values).Sum(s => s.Rolls.Count)
                                    + _stoppedSessions.Values.SelectMany(g => g.Values).Sum(s => s.Rolls.Count);
                                BotLogger.Debug(LogCategory.Session, $"Сохранено {sessionsToSave.Count} сессий (активных+архив) ({totalRolls} бросков)");
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

                private static object BuildSessionSnapshot(GameSession session) => new
                {
                    session.SessionId,
                    session.GuildId,
                    session.ChannelId,
                    session.GameName,
                    session.MasterName,
                    session.MasterId,
                    session.StartTime,
                    session.ActiveStartTime,
                    session.EventDescription,
                    session.GameComment,
                    session.EventId,
                    session.ControlMessageId,
                    session.ControlChannelId,
                    session.IsPaused,
                    session.TrackRolls,
                    session.StatsMessageId,
                    session.EndTime,
                    // PausePeriods нужен, чтобы после рестарта CalculateActiveDuration
                    // не считал время "из начала" — иначе все прошлые паузы терялись
                    // и счётчик длительности "убегал вперёд".
                    PausePeriods = session.PausePeriods.Select(p => new
                    {
                        Start = p.Start,
                        End = p.End,
                    }).ToList(),
                    Rolls = session.Rolls
                };

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
                int activeCount = 0;
                int archivedCount = 0;
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
                        DateTime activeStartTime = startTime;
                        if (elem.TryGetProperty("ActiveStartTime", out var ast) && ast.ValueKind == System.Text.Json.JsonValueKind.String
                            && DateTime.TryParse(ast.GetString(), out var parsedActive))
                        {
                            activeStartTime = parsedActive;
                        }

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

                                                // Восстанавливаем историю пауз — без неё CalculateActiveDuration считает
                                                // время с начала сессии, не вычитая прошлые паузы.
                                                var pausePeriods = new List<(DateTime Start, DateTime? End)>();
                                                if (elem.TryGetProperty("PausePeriods", out var ppElem) && ppElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                                                {
                                                    foreach (var pElem in ppElem.EnumerateArray())
                                                    {
                                                        try
                                                        {
                                                            var pStartStr = pElem.GetProperty("Start").GetString();
                                                            if (string.IsNullOrWhiteSpace(pStartStr)) continue;
                                                            var pStart = DateTime.Parse(pStartStr);
                                                            DateTime? pEnd = null;
                                                            if (pElem.TryGetProperty("End", out var endProp) && endProp.ValueKind != System.Text.Json.JsonValueKind.Null)
                                                            {
                                                                var pEndStr = endProp.GetString();
                                                                if (!string.IsNullOrWhiteSpace(pEndStr))
                                                                    pEnd = DateTime.Parse(pEndStr);
                                                            }
                                                            pausePeriods.Add((pStart, pEnd));
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
                                                                                                    ActiveStartTime = isPaused ? startTime : (activeStartTime == startTime ? DateTime.Now : activeStartTime),
                                                                                                    EventDescription = eventDescription ?? string.Empty,
                                                                                                    GameComment = gameComment ?? string.Empty,
                                                                                                    EventId = eventId,
                                                                                                    ControlMessageId = controlMessageId,
                                                                                                    ControlChannelId = controlChannelId,
                                                                                                    IsPaused = isPaused,
                                                                                                    TrackRolls = trackRolls,
                                                                                                    PausePeriods = pausePeriods,
                                                                                                    Rolls = rolls
                                                                                                };

                                                                        // Восстанавливаем EndTime и StatsMessageId, если они есть в сохранении —
                                                                        // иначе сессия в архиве потеряет связь с висящим сообщением статистики.
                                                                        if (elem.TryGetProperty("EndTime", out var endTimeProp)
                                                                            && endTimeProp.ValueKind == System.Text.Json.JsonValueKind.String
                                                                            && DateTime.TryParse(endTimeProp.GetString(), out var parsedEnd))
                                                                        {
                                                                            session.EndTime = parsedEnd;
                                                                        }
                                                                        if (elem.TryGetProperty("StatsMessageId", out var smi) && smi.ValueKind == System.Text.Json.JsonValueKind.Number)
                                                                        {
                                                                            session.StatsMessageId = smi.GetUInt64();
                                                                        }

                                                                        // Активные (незавершённые) сессии кладём в обычный словарь,
                                                                        // завершённые — в архив, чтобы кнопки статистики продолжали работать.
                                                                        var target = session.IsStopped ? _stoppedSessions : _sessions;
                                                                        if (!target.TryGetValue(guildId, out var guildSessions))
                                                                        {
                                                                            guildSessions = new ConcurrentDictionary<ulong, GameSession>();
                                                                            target[guildId] = guildSessions;
                                                                        }

                                                                        if (guildSessions.TryAdd(sessionId, session))
                                                                        {
                                                                            restorCount++;
                                                                            if (session.IsStopped) archivedCount++;
                                                                            else activeCount++;
                                                                            var eventIdStr = session.EventId.HasValue ? session.EventId.Value.ToString() : "null";
                                                                            // Единая строка для UI/run.log через StartupRenderer.
                                                                            // Категорийный Session.log получит копию через WriteStageLine
                                                                            // (его FileSink зеркалит в run.log; сами строки не дублируем).
                                                                            WriteStageLine($"Загружена сессия {sessionId}: \"{gameName}\" (EventId={eventIdStr}, мастер: {masterName}, бросков: {rolls.Count}, isPaused={isPaused})");
                                                                        }
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Error(LogCategory.Session, $"Ошибка при восстановлении сессии из {prop.Name}: {ex.Message}");
                    }
                }

                if (restorCount > 0 || skippedOldCount > 0)
                                {
                                    WriteStageLine($"Загружено {restorCount} записей из файла (активных: {activeCount}, архивных: {archivedCount}), пропущено устаревших: {skippedOldCount}");

                                    if (restorCount > 0)
                                                                        {
                                                                            // ВАЖНО: cleanup осиротевших сессий должен идти ДО
                                                                            // RecreateControlMessagesAsync, иначе только что
                                                                            // восстановленное control message повисит ~1 секунду
                                                                            // до того, как его удалит cleanup.
                                                                            await CleanupStaleSessionsAsync(client);
                                                                            await RecreateControlMessagesAsync(client);
                                                                        }
                                                                            }
                                            }
                                            catch (Exception ex)
                                            {
                                                                            BotLogger.Error(LogCategory.Session, $"Ошибка при загрузке сессий: {ex.Message}");
                                            }
        }

                        /// <summary>
                                /// Останавливает все активные таймеры автообновления control message. Нужно
                                /// вызывать при ежедневной перезагрузке/рестарте до того, как старый
                                /// Discord-клиент будет уничтожен, чтобы фоновые Task'и не дёргали
                                /// disposed HttpClient.
                                /// </summary>
                                public static void StopAllAutoRefresh()
                                {
                                    int stopped = 0;
                                    foreach (var guild in _sessions)
                                    {
                                        foreach (var session in guild.Value.Values)
                                        {
                                            if (session.ControlMessageUpdateCTS == null) continue;
                                            try { session.ControlMessageUpdateCTS.Cancel(); } catch { }
                                            try { session.ControlMessageUpdateCTS.Dispose(); } catch { }
                                            session.ControlMessageUpdateCTS = null;
                                            stopped++;
                                        }
                                    }
                                    if (stopped > 0)
                                        BotLogger.Debug(LogCategory.Session, $"Остановлено {stopped} таймеров автообновления control message");
                                }

                                /// <summary>
                                /// После восстановления сессий из файла проверяет, существуют ли ещё
                                /// связанные Discord-события. Если событие удалено/завершено пока бот
                                /// был оффлайн — сессия финализируется по тому же потоку, что и !stop
                                /// (FinalizeSessionAsOrphanAsync: EndTime → Discord-event Completed →
                                /// DeleteControlMessage → SendSessionStats в канал control message).
                                /// </summary>
                                public static async Task CleanupStaleSessionsAsync(DiscordSocketClient client)
                                {
                                    int cleaned = 0;
                                    var stale = new List<(ulong GuildId, GameSession Session)>();

                                    foreach (var guild in _sessions)
                                    {
                                        foreach (var session in guild.Value.Values)
                                        {
                                            if (!session.EventId.HasValue) continue;
                                            if (session.IsStopped) continue;
                                            stale.Add((guild.Key, session));
                                        }
                                    }

                                    // Стартовая строка cleanup — только через визуализацию, чтобы не было
                                    // тройных дублей в run.log (BotLogger.Info + WriteStageLine).
                                    WriteStageLine($"[STALE] Cleanup: проверяю {stale.Count} активных сессий.");

                                    foreach (var (guildId, session) in stale)
                                    {
                                        try
                                        {
                                            var guild = client.GetGuild(guildId);
                                            if (guild == null)
                                            {
                                                BotLogger.Warn(LogCategory.Session, $"[STALE] Гильдия {guildId} недоступна для сессии {session.SessionId} — пропускаю.");
                                                continue;
                                            }
                                            var guildEvent = await guild.GetEventAsync(session.EventId.Value).ConfigureAwait(false);
                                            if (guildEvent == null)
                                            {
                                                // Кэш SocketGuild.GetEventAsync может возвращать null, даже если событие живёт.
                                                // Дёрнем REST по гильдии — он ходит напрямую и видит актуальные Completed/Cancelled.
                                                try
                                                {
                                                    var restGuild = await client.Rest.GetGuildAsync(session.GuildId).ConfigureAwait(false);
                                                    if (restGuild != null)
                                                    {
                                                        var restEvent = await restGuild.GetEventAsync(session.EventId.Value).ConfigureAwait(false);
                                                        if (restEvent != null)
                                                            guildEvent = restEvent;
                                                    }
                                                }
                                                catch (Exception restEx)
                                                {
                                                    BotLogger.Debug(LogCategory.Session, $"[STALE] REST-фоллбэк для события {session.EventId.Value} не удался: {restEx.Message}");
                                                }
                                            }

                                            var eventStatusLine = $"[STALE] Сессия {session.SessionId} \"{session.GameName}\" → GetEventAsync({session.EventId}) = {(guildEvent == null ? "null" : $"\"{guildEvent.Name}\" status={guildEvent.Status}")}";
                                            // В run.log/GUI — через WriteStageLine (одна видимая строка).
                                            // Дополнительно сохраняем в категорийный Session.log через BotLogger.Debug
                                            // для расследования: туда идёт та же строка, но в отдельный файл,
                                            // в run.log/GUI повторно не попадает.
                                            WriteStageLine(eventStatusLine);
                                            BotLogger.Debug(LogCategory.Session, eventStatusLine);

                                            // Считаем сессию осиротевшей, если:
                                            //   - связанное событие уже не существует (null);
                                            //   - ИЛИ событие уже завершилось на стороне Discord
                                            //     (Completed/Cancelled) — бот не сможет продолжать
                                            //     синхронизацию, событие больше нельзя менять.
                                            bool isOrphan = guildEvent == null
                                                || guildEvent.Status == GuildScheduledEventStatus.Completed
                                                || guildEvent.Status == GuildScheduledEventStatus.Cancelled;

                                            if (isOrphan)
                                            {
                                                string orphanReason = guildEvent == null
                                                    ? "связанное Discord-событие больше не существует"
                                                    : $"связанное Discord-событие в статусе {guildEvent.Status}";
                                                BotLogger.Warn(LogCategory.Session,
                                                    $"[STALE] Сессия {session.SessionId} (\"{session.GameName}\") — {orphanReason}, принудительное завершение");

                                                // Финализируем сессию по тому же потоку, что и !stop:
                                                //   - помечаем EndTime;
                                                //   - завершаем связанное Discord-событие, если ещё живо;
                                                //   - удаляем control message;
                                                //   - отправляем статистику в канал control message.
                                                // Это та же HandleConfirmStop-логика, но без interactive-кнопок:
                                                // cleanup приходит из старта/фонового слушателя событий, и
                                                // считать, что пользователь нажмёт «Да, завершить», мы не можем.
                                                var commands = new GameSessionCommands(client);
                                                await commands.FinalizeSessionAsOrphanAsync(session).ConfigureAwait(false);
                                                cleaned++;
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            BotLogger.Error(LogCategory.Session, $"[STALE] Ошибка проверки сессии {session.SessionId}: {ex.Message}");
                                        }
                                    }

                                    if (cleaned > 0)
                                    {
                                        await SaveSessionsAsync().ConfigureAwait(false);
                                        // Через визуализацию, чтобы не дублировать.
                                        WriteStageLine($"[STALE] Очищено {cleaned} осиротевших сессий");
                                    }
                                    else
                                    {
                                        WriteStageLine("[STALE] Осиротевших сессий не обнаружено.");
                                    }
                                }

                                /// <summary>
                                /// Целевая версия cleanup: ищет в _sessions сессию с заданным EventId
                                /// и принудительно завершает. Зовётся из RESYNC, когда для одного
                                /// конкретного события точно известно, что оно уже не существует.
                                /// </summary>
                                public static async Task CleanupStaleSessionByEventAsync(DiscordSocketClient client, ulong guildId, ulong eventId)
                                {
                                    if (!_sessions.TryGetValue(guildId, out var guildSessions))
                                        return;
                                    foreach (var session in guildSessions.Values.ToList())
                                    {
                                        if (!session.EventId.HasValue || session.EventId.Value != eventId)
                                            continue;
                                        if (session.IsStopped)
                                            continue;

                                        BotLogger.Warn(LogCategory.Session,
                                            $"[STALE] Сессия {session.SessionId} (\"{session.GameName}\") связана с несуществующим событием {eventId} — принудительное завершение");

                                        // Финализируем сессию по тому же потоку, что и !stop
                                        // (EndTime → Discord-event Completed → DeleteControlMessage
                                        // → SendSessionStats в канал control message).
                                        // Раньше здесь просто удаляли сессию, из-за чего
                                        // пропадала статистика и связанное событие оставалось Active.
                                        var commands = new GameSessionCommands(client);
                                        await commands.FinalizeSessionAsOrphanAsync(session).ConfigureAwait(false);

                                        // Единая строка для категорийного лога и run.log.
                                        // (WriteStageLine сюда не нужен — функция вызывается в рантайме,
                                        // не во время визуализации старта.)
                                        BotLogger.Info(LogCategory.Session, $"[STALE] Осиротевшая сессия {session.SessionId} (\"{session.GameName}\") завершена принудительно.");
                                        return;
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
                                        // Старое control message на месте — не пересоздаём,
                                        // а только обновляем embed до актуального состояния.
                                        try
                                        {
                                    var activeDurationExisting = CalculateActiveDuration(session, DateTime.Now);
                                    var refreshed = BuildActiveControlEmbed(session, activeDurationExisting);
                                    var refreshedButtons = commands.CreateControlButtons(session);
                                    await ((IUserMessage)existingMessage).ModifyAsync(m =>
                                    {
                                        m.Embed = refreshed;
                                        m.Components = refreshedButtons.Build();
                                    }).ConfigureAwait(false);
                                    commands.StartControlMessageAutoRefresh(session, channel);
                                    LogDebug($"Control message для сессии {session.SessionId} уже на месте — обновлён");
                                    continue;
                                        }
                                        catch (Exception editEx)
                                        {
                                    LogWarn($"Не удалось обновить существующий control message сессии {session.SessionId}: {editEx.Message}");
                                    commands.StartControlMessageAutoRefresh(session, channel);
                                    continue;
                                                                }
                                    }
                                                            }
                                                            catch
                                                            {
                                                            }
                                                        }

                            var activeDuration = CalculateActiveDuration(session, DateTime.Now);
                                                        var embed = BuildActiveControlEmbed(session, activeDuration);

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
            var sem = GetGuildSemaphore(guildId);
            await sem.WaitAsync();
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
                                    ActiveStartTime = DateTime.Now,
                                    EventId = eventId,
                                    TrackRolls = true,
                                    TrackRollsAutoEnabled = true
                                                                        // ActiveStartTime = StartTime; — после Resume больше не
                                                                        // сбрасываем, "Длительность (активная)" = общая минус паузы.
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
                sem.Release();
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
                            // Активная длительность = (now - StartTime) минус все паузы,
                            // которые пересекаются с этим интервалом. Так счётчик продолжает
                            // расти от старта сессии, а паузы из него вычитаются.
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
                                // Учитываем только ту часть паузы, что попадает в [StartTime, end].
                                var segStart = p.Start < session.StartTime ? session.StartTime : p.Start;
                                var segEnd = pauseEnd > end ? end : pauseEnd;
                                if (segEnd <= segStart) continue;
                                pause += segEnd - segStart;
                            }

                            var active = total - pause;
                            return active < TimeSpan.Zero ? TimeSpan.Zero : active;
                        }

        private static Embed BuildActiveControlEmbed(GameSession session, TimeSpan activeDuration)
                {
                    var currentPause = session.PausePeriods.LastOrDefault();
                    var pauseTimeInfo = session.IsPaused && currentPause.Start != DateTime.MinValue
                        ? $"\nНа паузе с: {DiscordTimeFormatter.TimeOnly(currentPause.Start)}"
                        : "";

                    var statusText = session.IsStopped
                        ? "✅ Завершена"
                        : session.IsPaused
                            ? $"⏸ На паузе{pauseTimeInfo}"
                            : "▶ В процессе";

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

                    return new EmbedBuilder()
                        .WithTitle($"Сессия: \"{session.GameName}\"")
                        .WithDescription(string.Join("\n", descriptionLines))
                        .WithColor(session.IsStopped ? Color.DarkGrey : session.IsPaused ? Color.Orange : Color.Green)
                        .Build();
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
                        // Захватываем клиента локально — после рестарта бота _client остаётся
                        // старым (disposed), и его DiscordHttpClient уже непригоден.
                        var clientRef = _client;

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

                                // На паузе таймер автообновления ничего полезного не делает,
                                // а ещё вызывает "шум" в логах и рискует затереть напоминание о паузе.
                                if (session.IsPaused)
                                    continue;

                                // Клиент мог быть пересоздан — пропускаем обновление до восстановления.
                                if (clientRef == null || clientRef.ConnectionState != ConnectionState.Connected
                                    || clientRef.LoginState != LoginState.LoggedIn)
                                {
                                    continue;
                                }

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

            ulong channelId = (Program.ServerConfigResolver?.Invoke(guildId)) is { } cfg && cfg.RecordChannelID != 0
                ? cfg.RecordChannelID
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

                // Дополнительная защита от cross-session bleed: проверяем, что нажатая
                // кнопка действительно принадлежит control-message этой сессии
                // (а не чужой сессии, у которой тот же sessionId после рестарта
                // или пересоздания словаря). GuildId-скоуп уже выше, а вот
                // MessageId иногда пропускали — добавляем.
                if (session.ControlMessageId != 0 && component.Message.Id != session.ControlMessageId)
                {
                    Log($"[RESTART] Кнопка пришла с чужого message_id={component.Message.Id} (ожидался {session.ControlMessageId} для сессии {session.SessionId}) — игнорирую.");
                    await component.RespondAsync("❌ Эта кнопка принадлежит другой сессии.", ephemeral: true);
                    return;
                }

                // Защита от параллельных нажатий на кнопки одной сессии.
                // WaitAsync(0) — non-blocking probe: если семафор занят, сообщаем
                // пользователю «подождите» и выходим, gateway не блокируется.
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
                                    $"{user.Mention}, игра на паузе с {DiscordTimeFormatter.TimeOnly(pauseStartTime)} (уже {totalMinutes} мин)");

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
                                    // Не сбрасываем ActiveStartTime: "Длительность (активная)" — это общая
                                    // длительность сессии минус паузы, а не таймер "от Resume".

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

            var sem = GetGuildSemaphore(guildId.Value);
            await sem.WaitAsync();
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
                sem.Release();
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

                    // Меняем кнопки control-сообщения на «✅ Да / ❌ Нет» — пользователь жмёт прямо по ним,
                    // это один synchronous round-trip к Discord (без отдельного ephemeral FollowupAsync),
                    // и укладываемся в 3-секундный ack-таймаут без риска 10062/10008.
                    var confirmBuilder = new ComponentBuilder()
                        .WithButton("✅ Да, завершить", $"confirm_stop:{session.SessionId}", ButtonStyle.Danger)
                        .WithButton("❌ Отмена", $"cancel_stop:{session.SessionId}", ButtonStyle.Secondary);

                    try
                    {
                        await component.Message.ModifyAsync(m => m.Components = confirmBuilder.Build());
                        Log($"Кнопки подтверждения остановки для сессии {session.SessionId} успешно обновлены");
                    }
                    catch (Exception ex)
                    {
                        Log($"Ошибка при обновлении кнопок подтверждения остановки: {ex.Message}");
                    }

                    // Сигналим пользователю ephemeral, что ждём подтверждения (не задерживая ack).
                    await component.FollowupAsync("Вы уверены, что хотите завершить игру? Нажмите **«✅ Да, завершить»** ниже.", ephemeral: true);
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
                                // Пока пользователь не нажал ни одну из кнопок, сессия хранится
                                // в архиве (_stoppedSessions) — это позволяет пережить рестарт бота.
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
        /// Завершает сессию в рамках «нормального потока» (как HandleConfirmStop / HandleForceStop),
        /// но без интерактивных подтверждений: cleanup не знает, кто мастер, и ждать нажатия кнопки
        /// нельзя. Используется CleanupStaleSessionsAsync и CleanupStaleSessionByEventAsync,
        /// когда связанное Discord-событие удалено/завершено, пока бот был оффлайн.
        ///
        /// Поток:
        ///   1. Если сессия на паузе — закрыть паузу (как в HandleConfirmStop).
        ///   2. Поставить EndTime = now, пометив сессию как IsStopped.
        ///   3. По возможности завершить связанное Discord-событие (Active → Completed).
        ///      Уже-завершённые/удалённые события трогать не пытаемся — это нормальное состояние
        ///      для осиротевших сессий.
        ///   4. Удалить control message (по ControlChannelId / ChannelId).
        ///   5. Отправить статистику в канал control message. Дальше SendSessionStats сам архивирует
        ///      сессию и при отсутствии бросков удаляет её окончательно.
        ///   6. ✅ Round 7-C8: отменить связанный с событием прогноз, если он активен.
        ///
        /// Уведомления в канал от мастера не отправляем — об этом решении договорились: для
        /// автозавершения это лишний шум. Архив всё равно сохраняется, и пользователь при желании
        /// может запросить статистику по кнопке.
        /// </summary>
        internal async Task FinalizeSessionAsOrphanAsync(GameSession session)
        {
            if (session == null) return;
            if (session.IsStopped)
            {
                LogDebug($"FinalizeSessionAsOrphanAsync: сессия {session.SessionId} уже остановлена — пропускаю");
                return;
            }

            try
            {
                if (session.IsPaused)
                {
                    LogDebug($"FinalizeSessionAsOrphanAsync: снимаю паузу для сессии {session.SessionId}");
                    try { session.PauseReminderCTS?.Cancel(); } catch { }
                    try { session.PauseReminderCTS?.Dispose(); } catch { }
                    session.PauseReminderCTS = null;
                    var lastPause = session.PausePeriods.LastOrDefault();
                    if (lastPause.Start != default)
                        session.PausePeriods[^1] = (lastPause.Start, DateTime.Now);
                    session.IsPaused = false;
                }

                session.EndTime = DateTime.Now;
                Log($"[STALE] Сессия {session.SessionId} ({session.GameName}) помечена как остановленная (IsStopped={session.IsStopped}), EndTime={session.EndTime}");

                if (session.EventId.HasValue)
                {
                    try
                    {
                        var guild = _client.GetGuild(session.GuildId);
                        var guildEvent = guild != null
                            ? await guild.GetEventAsync(session.EventId.Value).ConfigureAwait(false)
                            : null;
                        // Если события нет в кэше — пробуем REST. Cleanup вызывается сразу после
                        // восстановления сессий, когда кэш ещё мог не прогреться.
                        if (guildEvent == null)
                        {
                            try
                            {
                                var restGuild = await _client.Rest.GetGuildAsync(session.GuildId).ConfigureAwait(false);
                                if (restGuild != null)
                                {
                                    var restEvent = await restGuild.GetEventAsync(session.EventId.Value).ConfigureAwait(false);
                                    if (restEvent != null)
                                        guildEvent = restEvent;
                                }
                            }
                            catch (Exception restEx)
                            {
                                LogDebug($"[STALE] REST-фоллбэк для события {session.EventId.Value} не удался: {restEx.Message}");
                            }
                        }

                        if (guildEvent?.Status == GuildScheduledEventStatus.Active)
                        {
                            await guildEvent.ModifyAsync(props => props.Status = GuildScheduledEventStatus.Completed).ConfigureAwait(false);
                            Log($"Связанное событие {session.EventId} помечено как завершённое");
                        }
                        else if (guildEvent != null)
                        {
                            LogDebug($"Связанное событие {session.EventId} уже в статусе {guildEvent.Status} — оставляю как есть");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Ошибка завершения события: {ex.Message}");
                    }
                }

                // Канал control message: точно туда же, куда уходит статистика у !stop.
                var statsChannel = ResolveControlChannel(session) as ISocketMessageChannel;
                if (statsChannel != null)
                {
                    await DeleteControlMessageAsync(session, statsChannel).ConfigureAwait(false);
                    await SendSessionStats(session, statsChannel).ConfigureAwait(false);
                }
                else
                {
                    LogWarn($"Канал для статистики осиротевшей сессии {session.SessionId} не найден — только архивирую");
                    await DeleteControlMessageAsync(session).ConfigureAwait(false);
                    ArchiveStoppedSession(session);
                    _ = Task.Run(() => SaveSessionsAsync());
                }

                // ✅ Round 7-C8: после финализации сессии пробуем отменить связанный
                // с её событием прогноз. Ничего не делает, если прогноз не привязан
                // к этому событию или уже завершён.
                await TryCancelLinkedPredictionAsync(session, "⚠️ Событие завершено. Все ставки возвращены.").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogError($"FinalizeSessionAsOrphanAsync: ошибка при принудительном завершении сессии {session.SessionId}: {ex.Message}");
                // Даже если что-то пошло не так, помечаем сессию как остановленную и архивируем,
                // чтобы при следующем запуске не пытаться чистить её снова.
                try
                {
                    if (!session.IsStopped) session.EndTime = DateTime.Now;
                    ArchiveStoppedSession(session);
                    _ = Task.Run(() => SaveSessionsAsync());
                }
                catch (Exception fallback)
                {
                    LogError($"FinalizeSessionAsOrphanAsync: не удалось даже архивировать сессию {session.SessionId}: {fallback.Message}");
                }
            }
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

                var sem = GetGuildSemaphore(guildId);
                await sem.WaitAsync();
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
                                                            // Sheets-запись и сохранение в файл всё равно делаем:
                                                            // событие завершилось, статистика не зависит от того,
                                                            // получилось ли её отправить в Discord-канал.
                                                            await commands.PersistCompletedSessionAsync(session);
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
                    sem.Release();
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

                                        if (Program.ServerConfigResolver?.Invoke(session.GuildId) is { } statsCfg)
                                        {
                                            if (_client.GetChannel(statsCfg.StatsChannelID) is ITextChannel statsChannel)
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
                                                LogWarn($"Канал статистики {statsCfg.StatsChannelID} не найден");
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
                                                        // Архивировать нечего: статистика не нужна.
                                                        // Sheets-запись и сохранение файла всё равно выполняем.
                                                        await PersistCompletedSessionAsync(session);
                                                        return;
                                                    }

                                                    // Архивируем сессию: пока пользователь не нажал ни одну кнопку
                                                    // статистики, данные хранятся в памяти и не теряются при рестарте.
                                                    ArchiveStoppedSession(session);

                                                    // Google Sheets — в последнюю очередь, после всех Discord-сообщений.
                                                    await PersistCompletedSessionAsync(session);
                                }
                                catch (Exception ex)
                                {
                                    LogError($"Ошибка при отправке статистики: {ex.Message}");
                                }
                }

                /// <summary>
                /// Финализирует завершённую сессию: записывает её в Google Sheets (если
                /// доступен) и сохраняет обновлённый state в файл. Вызывается независимо от
                /// того, удалось ли отправить статистику в Discord-канал: данные сессии —
                /// факт, а не побочный эффект доставки embed-а.
                /// </summary>
                private async Task PersistCompletedSessionAsync(GameSession session)
                {
                                // Сохранение файла сессий — всегда.
                                try { _ = Task.Run(() => SaveSessionsAsync()); }
                                catch (Exception ex) { LogError($"[Sessions] Ошибка запуска SaveSessionsAsync: {ex.Message}"); }

                                if (_googleSheets == null)
                                {
                                    LogDebug($"[Sheets] Сервис не инициализирован — запись пропущена");
                                    return;
                                }

                                try
                                {
                                    var row = await _googleSheets.AppendSessionAsync(session).ConfigureAwait(false);
                                    if (row > 0)
                                    {
                                        session.SheetRowIndex = row;
                                        LogDebug($"[Sheets] Запись выполнена: row={row}, session={session.SessionId}");
                                    }
                                    else
                                    {
                                        LogWarn($"[Sheets] Запись не удалась — AppendSessionAsync вернул {row}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogError($"[Sheets] Ошибка записи session={session.SessionId}: {ex.GetType().Name}: {ex.Message}");
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

                        if (_sessions.TryGetValue(session.GuildId, out var guildSessions)
                            && guildSessions.TryRemove(session.SessionId, out _))
                        {
                            LogDebug($"Сессия {session.SessionId} удалена из активных");
                        }
                        else if (_stoppedSessions.TryGetValue(session.GuildId, out var stoppedGuildSessions)
                                && stoppedGuildSessions.TryRemove(session.SessionId, out _))
                        {
                            LogDebug($"Сессия {session.SessionId} удалена из завершённых");
                        }
                        else
                        {
                            LogDebug($"Не найден словарь сессий для гильдии {session.GuildId} при удалении");
                        }

                        // Полная очистка пустых словарей
                        if (_sessions.TryGetValue(session.GuildId, out var active)
                            && active.IsEmpty)
                {
                            _sessions.TryRemove(session.GuildId, out _);
                            LogDebug($"Словарь активных сессий для гильдии {session.GuildId} удален (пуст)");
                        }
                        if (_stoppedSessions.TryGetValue(session.GuildId, out var stopped)
                            && stopped.IsEmpty)
                        {
                            _stoppedSessions.TryRemove(session.GuildId, out _);
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
                                                            .ContinueWith(t =>
                                                            {
                                                                // 10008 / Unknown Message — нормальная гонка: сообщение уже удалено
                                                                // (например, в SendSessionStats мы удаляем control message параллельно).
                                                                // Не логируем как WARN, чтобы не захламлять лог.
                                                                if (t.IsFaulted && t.Exception?.InnerException is not Discord.Net.HttpException hex)
                                                                    LogWarn($"Ошибка при удалении напоминания о паузе: {t.Exception?.InnerException?.Message}");
                                                            });
                            }

                                                    // Удаляем сообщение подтверждения остановки
                                                    if (session.ConfirmationMessageId.HasValue)
                                                    {
                                                        _ = channel.DeleteMessageAsync(session.ConfirmationMessageId.Value)
                                                            .ContinueWith(t =>
                                                            {
                                                                if (t.IsFaulted && t.Exception?.InnerException is not Discord.Net.HttpException)
                                                                    LogWarn($"Ошибка при удалении подтверждения остановки: {t.Exception?.InnerException?.Message}");
                                                            });
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

                /// <summary>
                /// Помечает сессию завершённой и переносит её в архив. Кнопки статистики
                /// продолжат находить сессию до окончательного удаления.
                /// </summary>
                private void ArchiveStoppedSession(GameSession session)
                {
                    try
                    {
                        if (_sessions.TryGetValue(session.GuildId, out var guildSessions))
                        {
                            if (guildSessions.TryRemove(session.SessionId, out _))
                            {
                                LogDebug($"Сессия {session.SessionId} перенесена из активных в архив");
                            }
                            if (guildSessions.IsEmpty)
                            {
                                _sessions.TryRemove(session.GuildId, out _);
                            }
                        }

                        if (!_stoppedSessions.TryGetValue(session.GuildId, out var stoppedGuild))
                        {
                            stoppedGuild = new ConcurrentDictionary<ulong, GameSession>();
                            _stoppedSessions[session.GuildId] = stoppedGuild;
                        }
                        stoppedGuild[session.SessionId] = session;
                    }
                    catch (Exception ex)
                    {
                        LogWarn($"Ошибка при архивировании сессии {session.SessionId}: {ex.Message}");
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

            var sem = GetGuildSemaphore(guildId.Value);
            await sem.WaitAsync();
            try
            {
                LogDebug($"Обработка кнопки статистики для гильдии {guildId}");

                        // Ищем среди активных и в архиве (на случай, если сессия уже завершена
                        // и бот успел перезагрузиться, прежде чем пользователь нажал кнопку).
                        GameSession? session = null;
                        if (_sessions.TryGetValue(guildId.Value, out var guildSessions))
                        {
                            session = guildSessions.Values.FirstOrDefault(s => s.StatsMessageId == component.Message.Id);
                }
                        if (session == null && _stoppedSessions.TryGetValue(guildId.Value, out var stoppedGuildSessions))
                        {
                            session = stoppedGuildSessions.Values.FirstOrDefault(s => s.StatsMessageId == component.Message.Id);
                        }

                        if (session == null)
                        {
                            LogWarn($"[STATS] Сессия для сообщения статистики {component.Message.Id} не найдена (ни активная, ни в архиве)");
                            try { await component.Message.DeleteAsync(); } catch { }
                            await component.RespondAsync("❌ Сессия не найдена.\n\nВозможно, она была очищена или архив был удалён.", ephemeral: true);
                            return;
                        }

                        LogDebug($"Найдена сессия {session.SessionId} для обработки статистики (IsStopped={session.IsStopped})");

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
                        sem.Release();
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
                if (Program.ServerConfigResolver?.Invoke(guildId) is not { } config)
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
