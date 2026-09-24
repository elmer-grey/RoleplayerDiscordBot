using Discord;
using Discord.WebSocket;
using RPBot.Predictions;
using RPBot.Util;
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
    /// Управляет жизненным циклом прогнозов: создание, ставки, закрытие ставок по времени и завершение.
    /// </summary>
    public class PredictionService
    {
        private readonly DiscordSocketClient _client;
        private readonly PointsService _points;
        // ✅ pred-parallelization: вложенный словарь активных прогнозов.
        // Раньше на гильдию мог быть только ОДИН прогноз (привязан к EventVoiceChannelID).
        // Теперь на одной гильдии может быть НЕСКОЛЬКО прогнозов — по одному на каждый
        // голосовой канал, где сейчас активно Discord-событие. Внешний ключ — guildId,
        // внутренний — channelId (он же key в _activeChannels). Это позволяет мастерам
        // параллельно делать прогнозы в разных каналах, а игрокам — ставить только в
        // своём канале.
        private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, ActivePrediction>> _active = new();
        // ✅ pred-parallelization: каналы сообщений для каждого (guildId, channelId).
        // Ключ композитный — guildId:channelId (строкой), потому что ConcurrentDictionary
        // не поддерживает tuple-ключи «из коробки».
        private readonly ConcurrentDictionary<string, ISocketMessageChannel> _activeChannels = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly string _stateFilePath;
        private readonly SemaphoreSlim _stateFileGate = new(1, 1);
        private readonly object _shutdownLock = new();
        private int _shutdownStarted; // 0 = running, 1 = shutting down (Interlocked guard)
        private volatile bool _disposed;

        // ✅ Round 7-C3: "безопасный режим" SaveStateAsync.
        // Файл прогнозов может быть стёрт в `{}`, если SaveStateAsync срабатывает
        // ПРЕЖДЕ чем LoadStateAsync восстановит прогнозы из файла (Shutdown
        // предыдущего процесса / OnClientDisconnected с пустым _active).
        // Правила:
        //   - _stateFileHadContentOnStartup: был ли файл НЕпустой на момент запуска.
        //   - _firstReadyValidated: фаза 2 (ValidateActiveAfterReadyAsync) уже отработала.
        // Пока фаза 2 не прошла, _active пуст — мы НЕ должны затирать файл.
        private volatile bool _stateFileHadContentOnStartup;
                private volatile bool _firstReadyValidated;

                        // ✅ Round 7-C7: true, как только LoadStateAsync завершился (даже если файл был пуст).
                        // Используется в SaveStateAsync как часть safe-mode: пока LoadStateAsync ещё не отработал,
                        // мы не знаем, что на диске — и при пустом _active НЕ должны затирать файл.
                        // Это спасает от «второй итерации restart-loop»: процесс стартует, не успев прочитать файл,
                        // получает команду Shutdown → SaveStateAsync, и без этой защиты пишет {}.
                        private volatile bool _weLoadedStateAlready;

                                                // ✅ Round 7-C7: различаем «это наш собственный shutdown (reconnect, stop, restart)»
                                                // и «реальный offline». В Program.cs.StopInternalAsync / GracefulShutdownAsync
                                                // мы СРАЗУ шлём сообщения «Бот ушёл» через публичный AnnounceShutdownAsync(),
                                                // пока клиент ещё живой. Здесь же, в OnClientDisconnected, мы только логируем
                                                // и не пытаемся слать — иначе HttpClient уже будет disposed.
                                                internal static volatile bool _shutdownInProgress;

                // ✅ Round 7-C5: буфер для логов восстановления/проверки предиктов.
        // Строки пишутся в этот список синхронно, а потом "проигрываются" в
        // ЭТАПЕ 3/4 загрузки через StartupRenderer — чтобы пользователь
        // видел весь отчёт в одном месте, а не вразнобой в Predict.log.
        private readonly List<string> _restoreReport = new();
        private readonly object _restoreReportLock = new();
        // Кол-во записей истории, уже загруженных в LoadHistoryAsync. Запоминаем
        // ДО LoadStateAsync, чтобы потом вывести в ЭТАП 3/4.
        private int _historyEntriesLoaded;

        /// <summary>
        /// ✅ Round 7-C5: отдать собранный отчёт по восстановлению/проверке
        /// прогнозов (плюс кол-во записей истории) и очистить буфер. Вызывается
        /// из Program.cs в ЭТАП 3/4. Все строки — это уже отформатированные
        /// "[PRED] ..." сообщения, готовые для вывода через StartupRenderer.
        /// </summary>
        public IReadOnlyList<string> DrainRestoreReport()
        {
            lock (_restoreReportLock)
            {
                var copy = _restoreReport.ToArray();
                _restoreReport.Clear();
                return copy;
            }
        }

        /// <summary>
        /// ✅ Round 7-C5: отдать кол-во записей истории прогнозов, загруженных
        /// при старте (для строки "[PRED] HISTORY_LOADED entries=N").
        /// </summary>
        public int HistoryEntriesLoaded => _historyEntriesLoaded;

        private void AppendRestoreReport(string line)
        {
            // Префикс [PRED] обязателен — парсер дашборда/тестов опирается
            // на него, чтобы отличать эти строки от прочих.
            var prefixed = line.StartsWith("[PRED] ", StringComparison.Ordinal)
                ? line
                : "[PRED] " + line;
            lock (_restoreReportLock)
            {
                _restoreReport.Add(prefixed);
            }
        }

                // ✅ Bug 6 / Round 7-A2: путь к файлу-флагу "следующее отключение — это рестарт".
                                // Совпадает с Program.GetRestartPendingFlagPath(): Data/.restart_pending.
                                // Читается в OnClientDisconnected, удаляется в AnnounceOnlineAsync.
                                private static string RestartPendingFlagPath =>
                                    BotConfig.ResolvePath(Path.Combine(BotConfig.DataFolderName, ".restart_pending"));

                                private static bool IsRestartPending()
                                {
                                    try { return File.Exists(RestartPendingFlagPath); }
                                    catch { return false; }
                                }

        // ✅ НОВОЕ: История прогнозов, статистика и достижения
        private PredictionHistoryStore _history = new();
        private readonly ConcurrentDictionary<ulong, Dictionary<ulong, UserPredictionStats>> _userStats = new(); // guildId -> userId -> stats
        private readonly ConcurrentDictionary<ulong, List<UserAchievement>> _userAchievements = new(); // guildId -> achievements
        private readonly string _historyFilePath;
        private readonly string _statsFilePath;
        private readonly string _achievementsFilePath;
        private readonly SemaphoreSlim _historyGate = new(1, 1);
        private readonly SemaphoreSlim _statsGate = new(1, 1);
        private readonly SemaphoreSlim _achievementsGate = new(1, 1);

        public PredictionService(DiscordSocketClient client, PointsService points)
                {
                    _client = client;
                    _points = points;
                            // State file for persisting active predictions across restarts
                                        // Кладём файлы в production-каталог данных (см. BotConfig.GetDataDirectory),
                                        // чтобы они не терялись при пересборке/clean.
                                        var dataDir = BotConfig.GetDataDirectory();
                                        Directory.CreateDirectory(dataDir);
                                        _stateFilePath = Path.Combine(dataDir, "predictions_state.json");
                                        _historyFilePath = Path.Combine(dataDir, "predictions_history.json");
                                        _statsFilePath = Path.Combine(dataDir, "predictions_stats.json");
                                        _achievementsFilePath = Path.Combine(dataDir, "predictions_achievements.json");

                            // ✅ НОВОЕ: История прогнозов, статистика и достижения.
                            // Загружаем ДО старта, чтобы RunStage3RestoreAsync мог сразу выдать
                            // строку "HISTORY_LOADED entries=N" в ЭТАП 3/4. Никаких больше
                            // параллельных логов в Predict.log — всё в одном блоке дашборда.
                            try { LoadHistoryAsync().GetAwaiter().GetResult(); } catch { /* проглотим, уже отрапортовано */ }
                            try { LoadStatsAsync().GetAwaiter().GetResult(); } catch { }
                            try { LoadAchievementsAsync().GetAwaiter().GetResult(); } catch { }

                            // Фоновая задача для авто-блокировки ставок по истечении времени.
                            _ = Task.Run(() => MonitorLoopAsync(_cts.Token));

                            // ✅ Round 7-C6: убрали подписку на Ready/Connected для OnClientReadyForRestore.
                            // Теперь восстановление и валидация прогнозов выполняются ОДИН раз,
                            // синхронно, внутри ЭТАП 3/4 через RunStage3RestoreAsync().
                            // Раньше валидация уходила в Task.Run на Ready event, и её результат
                            // терял гонку с Stage 3/4 — половина строк RESTORE_* печаталась мимо
                            // дашборда, и потом приходилось подмешивать через буфер.

                            // ✅ Bug 6: Обновляем сообщения при отключении бота
                            _client.Disconnected += OnClientDisconnected;
                        }

        // ✅ R6 fix: события для очистки внешнего UI (кнопки «Продолжить» в Program.cs).
        // PredictionResolveOccured / PredictionCancelledOccured — когда активный прогноз
        // покинул _active (resolve/cancel). Используются вызывающей стороной, чтобы
        // почистить _pendingBetUi для затронутых гильдий.
        public event Action<ulong>? PredictionResolved;
        public event Action<ulong>? PredictionCancelled;

        private async Task OnClientDisconnected(Exception exception)
        {
            // Обновляем все активные прогнозы с пометкой "Бот неактивен"
            var nowUtc = DateTimeOffset.UtcNow;
                                                        // ✅ Round 7-C4: разделение событий по типу.
                                                        //   - "restart" — наш собственный рестарт (.restart_pending флаг
                                                        //     выставлен через /restart или Ctrl+C). Шлём в канал.
                                                        //   - "reconnect" — короткий микроразрыв Discord (GatewayReconnect
                                                        //     или WebSocketException). НЕ шлём в канал, только в лог.
                                                        //   - "offline" — реальный offline (нет reconnect >5 сек). Шлём в канал.
                                                        // ✅ Round 7-C7: если _shutdownInProgress уже true (Program.cs.StopInternalAsync
                                                        // или GracefulShutdownAsync уже отправили сообщения через AnnounceShutdownAsync
                                                        // ДО _client.StopAsync()), то OnClientDisconnected не должен пытаться слать
                                                        // ещё раз — клиент уже на пути к dispose. Только обновить embed и состояние.
                                                        string kind;
                                                        if (_shutdownInProgress)
                                                        {
                                                            // Наш собственный shutdown — сообщения уже отправлены из Program.cs.
                                                            // Здесь только обновляем embed, помечаем offline и сохраняем состояние.
                                                            kind = "shutdownHandled";
                                                        }
                                                        else if (IsRestartPending())
                                                        {
                                                            kind = "restart";
                                                        }
                                                        else if (exception is Discord.WebSocket.GatewayReconnectException
                                                                || exception is System.Net.WebSockets.WebSocketException)
                                                        {
                                                            kind = "reconnect";
                                                        }
                                                        else
                                                        {
                                                            kind = "offline";
                                                        }
                            var note = exception?.GetType().Name ?? "Disconnected";
                                                                // ✅ Round 7-C7: для kind=="restart" / kind=="shutdownHandled" обрабатываем СИНХРОННО в обработчике события,
                                                                                                                                // но БЕЗ повторной отправки AnnounceOfflineAsync — сообщения уже отправлены
                                                                                                                                // через PredictionService.AnnounceShutdownAsync() из Program.cs.StopInternalAsync
                                                                                                                                // / GracefulShutdownAsync ДО _client.StopAsync(). Здесь только обновляем embed,
                                                                                                                                // фиксируем момент offline и сохраняем состояние.
                                                                                                                                // Для kind=="offline" оставляем fire-and-forget (там клиент ещё живой).
                                                                                                                                // kind=="reconnect" — выходим (только лог, без Discord API).
                                                                                                                                if (string.Equals(kind, "reconnect", StringComparison.Ordinal))
                                                                                                                                {
                                                                                                                                    return;
                                                                                                                                }
                                                                                                                                if (string.Equals(kind, "restart", StringComparison.Ordinal)
                                                                                                                                || string.Equals(kind, "shutdownHandled", StringComparison.Ordinal))
                                                                                                                                {
                                                                                                                                    // ✅ Round 7-C7: вся работа (UpdateMessageAsync, markOffline, OfflineEvents, AnnounceOfflineAsync,
                                                                                                                                    // SaveStateAsync) уже сделана в AnnounceShutdownAsync, который Program.cs зовёт ДО
                                                                                                                                    // _client.StopAsync(). HttpClient здесь уже на пути к disposed — любые Discord API
                                                                                                                                    // вызовы сорвутся ObjectDisposedException. Поэтому sync-блок только логирует
                                                                                                                                    // факт прибытия и выходит.
                                                                                                                                    try
                                                                                                                                    {
                                                                                                                                    foreach (var p in SnapshotAllActive())
                                                                                                                                    {
                                                                                                                                        if (p.IsResolved) continue;
                                                                                                                                    }
                                                                                                                                    }
                                                                                                                                    catch
                                                                                                                                    {
                                                                                                                                    }
                                                                                                                                    return;
                                                                                                                                }
                                                                _ = Task.Run(async () =>
                                                                {
                                                                    try
                                                                    {
                                                                        foreach (var p in SnapshotAllActive())
                                                                        {
                                                                            if (!p.IsResolved)
                                                                            {
                                                                                // ✅ Bug 5: фиксируем момент ухода в offline для последующего
                                                                                // сдвига BetsCloseAtUtc после восстановления.
                                                                                // Round 7-C4: для "reconnect" НЕ сдвигаем таймер — это микроразрыв.
                                                                                if (!p.IsLocked && kind != "reconnect" && !p.BotOfflineAtUtc.HasValue)
                                                                                {
                                                                                    p.BotOfflineAtUtc = nowUtc;
                                                                                    p.WasBotOfflineOnShutdown = true;
                                                                                }
                                                                                // ✅ Bug 6: добавляем событие в лог
                                                                                p.OfflineEvents = p.OfflineEvents ?? new List<OfflineEvent>();
                                                                                // Защита от спама: если последняя запись — это Disconnected <5 сек назад, не дублируем.
                                                                                var lastEvent = p.OfflineEvents.LastOrDefault();
                                                                                var isDuplicate = lastEvent != null
                                                                                    && lastEvent.Kind == OfflineEventKind.Disconnected
                                                                                    && (nowUtc - lastEvent.AtUtc) < TimeSpan.FromSeconds(5);
                                                                                if (!isDuplicate)
                                                                                {
                                                                                    p.OfflineEvents.Add(new OfflineEvent
                                                                                    {
                                                                                        AtUtc = nowUtc,
                                                                                        Kind = OfflineEventKind.Disconnected,
                                                                                        Severity = kind,
                                                                                        Note = note
                                                                                    });
                                                                                    // ✅ Round 7-C4: ограничиваем историю последними 20 событиями,
                                                                                    // чтобы лог не разрастался и embed оставался компактным.
                                                                                    if (p.OfflineEvents.Count > 20)
                                                                                    {
                                                                                        p.OfflineEvents.RemoveRange(0, p.OfflineEvents.Count - 20);
                                                                                    }
                                                                                }
                                                                                await UpdateMessageAsync(p, showLocked: p.IsLocked, botOffline: true).ConfigureAwait(false);
                                                                                // ✅ Bug 6: оповещаем участников в канале, чтобы они видели,
                                                                                // что бот ушёл на рестарт/offline, и кнопки скрыты.
                                                                                try
                                                                                {
                                                                                    await AnnounceOfflineAsync(p, kind, nowUtc).ConfigureAwait(false);
                                                                                }
                                                                                catch (Exception annEx)
                                                                                {
                                                                                    await PredictionErrorLogger.LogAsync("AnnounceOfflineAsync", annEx, $"guild={p.GuildId} channel={p.ChannelId}").ConfigureAwait(false);
                                                                                }
                                                                            }
                                                                        }
                                                                        // Сохраняем состояние, чтобы при следующем рестарте видеть BotOfflineAtUtc.
                                                                        try { await SaveStateAsync().ConfigureAwait(false); }
                                                                        catch (Exception saveEx)
                                                                        {
                                                                            await PredictionErrorLogger.LogAsync("OnClientDisconnected:save", saveEx, "Failed to persist offline state").ConfigureAwait(false);
                                                                        }
                                                                    }
                                                                    catch (Exception ex)
                                                                    {
                                                                        await PredictionErrorLogger.LogAsync("OnClientDisconnected", ex, "Failed to update predictions on disconnect").ConfigureAwait(false);
                                                                    }
                                                                });
                                                                                        }

                                                                        /// <summary>
                /// ✅ Round 7-C6: вход для ЭТАП 3/4 загрузки.
                /// Последовательно выполняет:
                ///   1) LoadStateAsync (без обращения к Discord API, синхронно),
                ///   2) ValidateActiveAfterReadyAsync (проверка канала/сообщения),
                ///   3) AnnounceOnlineForRestoredAsync (сдвиг таймера, "Бот снова в сети").
                /// Никакого Ready/Connected event, никаких Task.Run — всё синхронно
                /// в коде Program.cs, между запуском бота и ЭТАП 3/4.
                /// Все строки отчёта попадают в буфер `DrainRestoreReport()`,
                /// который ЭТАП 3/4 тут же печатает через StartupRenderer.
                /// </summary>
                public async Task RunStage3RestoreAsync()
                {
                                    await LoadStateAsync(validate: false).ConfigureAwait(false);
                                    await ValidateActiveAfterReadyAsync().ConfigureAwait(false);
                                    await AnnounceOnlineForRestoredAsync().ConfigureAwait(false);
                                    _firstReadyValidated = true;
                                }

                                // ✅ Round 7-C7: публичный метод для Program.cs.StopInternalAsync / GracefulShutdownAsync.
                                // Вызывается ДО _client.StopAsync(), пока клиент ещё живой. Для каждого активного
                                // прогноза:
                                //   1) обновляет embed с пометкой "Бот неактивен" (ВАЖНО: пока HttpClient жив,
                                //      иначе OnClientDisconnected прилетит поздно и не сможет достучаться до REST);
                                //   2) шлёт сообщение «Бот ушёл…» в канал;
                                //   3) фиксирует BotOfflineAtUtc/WasBotOfflineOnShutdown/OfflineEvents,
                                //      чтобы при следующем старте LoadStateAsync корректно сдвинул BetsCloseAtUtc.
                                // Эти сообщения сохраняются в OfflineAnnouncementMessageIds — и при следующем
                                // запуске AnnounceOnlineAsync их удалит.
                                // Дополнительно: ставит флаг _shutdownInProgress, чтобы OnClientDisconnected не
                                // пытался слать повторно (и попадать в disposed HttpClient).
                                // kind="restart": «Бот ушёл на перезагрузку» + таймер сдвинется.
                                // kind="stop": «Бот завершил работу» + всё сохранится на диск.
                                // kind="offline" или любой другой: «Зафиксировано отключение бота» (используется
                                //   в OnClientReadyForAnnouncements fallback, если флаг не был установлен заранее).
                                public async Task AnnounceShutdownAsync(string kind)
                                {
                                    _shutdownInProgress = true;
                                    var nowUtc = DateTimeOffset.UtcNow;
                                    foreach (var p in SnapshotAllActive())
                                    {
                                        if (p.IsResolved) continue;
                                        // (1) Фиксируем moment offline + обновляем embed ДО _client.StopAsync(),
                                        // пока HttpClient ещё жив. Раньше этот шаг жил в OnClientDisconnected,
                                        // но там HttpClient уже на пути к disposed — embed не обновлялся.
                                        try
                                        {
                                            if (!p.IsLocked && !p.BotOfflineAtUtc.HasValue)
                                            {
                                                p.BotOfflineAtUtc = nowUtc;
                                                p.WasBotOfflineOnShutdown = true;
                                            }
                                            p.OfflineEvents = p.OfflineEvents ?? new List<OfflineEvent>();
                                            var lastEvent = p.OfflineEvents.LastOrDefault();
                                            var isDuplicate = lastEvent != null
                                                && lastEvent.Kind == OfflineEventKind.Disconnected
                                                && (nowUtc - lastEvent.AtUtc) < TimeSpan.FromSeconds(5);
                                            if (!isDuplicate)
                                            {
                                                p.OfflineEvents.Add(new OfflineEvent
                                                {
                                                    AtUtc = nowUtc,
                                                    Kind = OfflineEventKind.Disconnected,
                                                    Severity = kind,
                                                    Note = $"Shutdown:{kind}"
                                                });
                                                if (p.OfflineEvents.Count > 20)
                                                {
                                                    p.OfflineEvents.RemoveRange(0, p.OfflineEvents.Count - 20);
                                                }
                                            }
                                            await UpdateMessageAsync(p, showLocked: p.IsLocked, botOffline: true).ConfigureAwait(false);
                                        }
                                        catch (Exception upEx)
                                        {
                                            await PredictionErrorLogger.LogAsync("AnnounceShutdownAsync:UpdateMessage", upEx, $"guild={p.GuildId} kind={kind}").ConfigureAwait(false);
                                        }
                                        // (2) Шлём сообщение «Бот ушёл…» в канал.
                                        try
                                        {
                                            await AnnounceOfflineAsync(p, kind, nowUtc).ConfigureAwait(false);
                                        }
                                        catch (Exception ex)
                                        {
                                            await PredictionErrorLogger.LogAsync("AnnounceShutdownAsync", ex, $"guild={p.GuildId}").ConfigureAwait(false);
                                        }
                                    }
                                    // (3) Сохраняем состояние, чтобы BotOfflineAtUtc/WasBotOfflineOnShutdown дошли до файла.
                                    try
                                    {
                                        await SaveStateAsync().ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        await PredictionErrorLogger.LogAsync("AnnounceShutdownAsync:save", ex).ConfigureAwait(false);
                                    }
                                }

                // ✅ Round 7-C6: метод OnClientReadyForRestore больше не нужен —
                // вся логика восстановления переехала в RunStage3RestoreAsync,
                // который зовётся синхронно из Program.cs в ЭТАП 3/4.
                // Подписки _client.Ready/Connected тоже сняты в конструкторе,
                // потому что вся гонка с Stage 3/4 исчезла.

                /// <summary>
                /// ✅ Round 7-C3: после ValidateActiveAfterReadyAsync прогоняем тот же цикл,
                /// что и OnClientReadyForAnnouncements, но без зависимости от Disconnected event.
                /// Удаляет "мусорные" offline-сообщения из канала и шлёт "Бот снова в сети".
                /// </summary>
                private async Task AnnounceOnlineForRestoredAsync()
        {
            var nowUtc = DateTimeOffset.UtcNow;
            foreach (var p in SnapshotAllActive())
            {
                if (p.IsResolved) continue;

                // Сдвиг BetsCloseAtUtc, если был offline.
                if (p.BotOfflineAtUtc.HasValue)
                {
                    var offlineDuration = nowUtc - p.BotOfflineAtUtc.Value;
                    if (offlineDuration > TimeSpan.Zero)
                    {
                        p.LastOfflineDurationMinutes = Math.Round(offlineDuration.TotalMinutes, 2);
                    }
                    p.BotOfflineAtUtc = null;
                    p.WasBotOfflineOnShutdown = false;
                    if (!p.IsLocked)
                    {
                        var oldClose = p.BetsCloseAtUtc;
                        if (p.BetsCloseAtUtc < nowUtc + TimeSpan.FromSeconds(15))
                        {
                            p.BetsCloseAtUtc = nowUtc + TimeSpan.FromSeconds(15);
                        }
                        if (p.BetsCloseAtUtc != oldClose)
                        {
                            // ✅ Round 7-C5: OFFLINE_SHIFT_ON_RESTORE — это лог
                            // восстановления, должен попадать в ЭТАП 3/4.
                            AppendRestoreReport(
                                $"OFFLINE_SHIFT_ON_RESTORE guild={p.GuildId} oldClose='{oldClose:HH:mm:ss}' " +
                                $"newClose='{p.BetsCloseAtUtc:HH:mm:ss}'");
                        }
                    }
                }

                // ✅ Round 7-C3: фиксируем событие "Reconnected" в истории прогноза,
                // иначе embed-«Состояние бота» остаётся без финальной "🟢 онлайн".
                p.OfflineEvents = p.OfflineEvents ?? new List<OfflineEvent>();
                var lastEvent = p.OfflineEvents.LastOrDefault();
                var isDuplicateReady = lastEvent != null
                    && lastEvent.Kind == OfflineEventKind.Reconnected
                    && (nowUtc - lastEvent.AtUtc) < TimeSpan.FromSeconds(2);
                if (!isDuplicateReady)
                {
                    p.OfflineEvents.Add(new OfflineEvent
                    {
                        AtUtc = nowUtc,
                        Kind = OfflineEventKind.Reconnected,
                        Severity = "online",
                        Note = "Restart"
                    });
                }

                // Возвращаем кнопки и шлём сообщение в канал.
                try
                {
                    await UpdateMessageAsync(p, showLocked: p.IsLocked, botOffline: false).ConfigureAwait(false);
                }
                catch (Exception upEx)
                {
                    await PredictionErrorLogger.LogAsync("AnnounceOnlineForRestored:UpdateMessage", upEx, $"guild={p.GuildId}").ConfigureAwait(false);
                }

                // Шлём "Бот снова в сети" и удаляем offline-сообщения.
                try
                {
                    await AnnounceOnlineAsync(p, nowUtc).ConfigureAwait(false);
                }
                catch (Exception annEx)
                {
                    await PredictionErrorLogger.LogAsync("AnnounceOnlineForRestored:AnnounceOnline", annEx, $"guild={p.GuildId} channel={p.ChannelId}").ConfigureAwait(false);
                }

                // ✅ Round 7-C4: после восстановления прогноза планируем удаление
                // старых «online»-сообщений, которые не успели удалиться до рестарта.
                if (p.OnlineAnnouncementMessageIds != null && p.OnlineAnnouncementMessageIds.Count > 0)
                {
                    var stale = p.OnlineAnnouncementMessageIds.ToList();
                    foreach (var om in stale)
                    {
                        var sentAt = om.SentAtUtc;
                        var elapsed = nowUtc - sentAt;
                        var remaining = TimeSpan.FromMinutes(5) - elapsed;
                        if (remaining <= TimeSpan.Zero)
                        {
                            // Уже пора удалять — делаем это синхронно, чтобы канал не копил мусор.
                            _ = ScheduleOnlineMessageCleanupAsync(p, om.MessageId, TimeSpan.Zero);
                        }
                        else
                        {
                            // Ещё рано — планируем на оставшееся время.
                            _ = ScheduleOnlineMessageCleanupAsync(p, om.MessageId, remaining);
                        }
                    }
                }
            }
            try { await SaveStateAsync().ConfigureAwait(false); }
            catch (Exception saveEx)
            {
                await PredictionErrorLogger.LogAsync("AnnounceOnlineForRestored:save", saveEx).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// ✅ Bug C / Round 7-C2: после Ready валидируем все активные прогнозы,
        /// загруженные фазой 1 без обращения к Discord API. Прогнозы без канала
        /// или сообщения автоматически отменяются и ставки возвращаются.
        /// </summary>
        private async Task ValidateActiveAfterReadyAsync()
        {
            if (_active.IsEmpty) return;
            foreach (var p in SnapshotAllActive())
            {
                if (p.IsResolved) continue;

                // ✅ Round 7-C3: на первом Ready локальный кеш Discord ещё может
                // быть пустым (guilds/channels появляются чуть позже). Ждём
                // до ~3 секунд с короткими ретраями, прежде чем считать
                // канал/сообщение отсутствующим — иначе словим ложный
                // RESTORE_FAIL_PHASE2 reason=channel_missing при штатном restart.
                ISocketMessageChannel? ch = null;
                for (int attempt = 0; attempt < 6 && ch == null; attempt++)
                {
                    ch = _client.GetChannel(p.ChannelId) as ISocketMessageChannel
                        ?? _client.GetGuild(p.GuildId)?.GetChannel(p.ChannelId) as ISocketMessageChannel;
                    if (ch == null)
                    {
                        try
                        {
                            var fetched = await _client.GetChannelAsync(p.ChannelId).ConfigureAwait(false);
                            ch = fetched as ISocketMessageChannel;
                        }
                        catch (Exception ex)
                        {
                            await PredictionErrorLogger.LogAsync("ValidateActiveAfterReadyAsync:GetChannel", ex, $"guild={p.GuildId} channel={p.ChannelId}").ConfigureAwait(false);
                        }
                    }
                    if (ch == null && attempt < 5)
                    {
                        await Task.Delay(500).ConfigureAwait(false);
                    }
                }

                if (ch == null || p.MessageId == 0)
                {
                    // ✅ Round 7-C5: RESTORE_FAIL_PHASE2 — это лог проверки,
                    // должен быть виден в ЭТАП 3/4.
                    AppendRestoreReport($"RESTORE_FAIL_PHASE2 guild={p.GuildId} reason=channel_missing channelId={p.ChannelId} messageId={p.MessageId} bets={p.Bets.Count}");
                    await AutoCancelRestoredPredictionAsync(p, cancelReason: "Восстановление невозможно: сообщение прогноза не найдено. Прогноз отменён, ставки возвращены.").ConfigureAwait(false);
                    // ✅ pred-parallelization: RemovePrediction чистит вложенный словарь.
                    RemovePrediction(p);
                    continue;
                }

                IMessage? msg = null;
                int msgAttempts = 0;
                while (msg == null && msgAttempts < 5)
                {
                    try
                    {
                        msg = await ch.GetMessageAsync(p.MessageId).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Discord.Net не имеет публичного HttpException-типа в этой версии;
                        // любой transient сбой пытаемся ретраить в общем цикле.
                        await PredictionErrorLogger.LogAsync("ValidateActiveAfterReadyAsync:GetMessage", ex, $"guild={p.GuildId} channel={p.ChannelId} message={p.MessageId}").ConfigureAwait(false);
                        if (msgAttempts >= 4)
                        {
                            await AutoCancelRestoredPredictionAsync(p, cancelReason: "Восстановление невозможно: ошибка проверки сообщения. Прогноз отменён, ставки возвращены.").ConfigureAwait(false);
                            // ✅ pred-parallelization: RemovePrediction чистит вложенный словарь.
                            RemovePrediction(p);
                            msg = null;
                            break;
                        }
                    }
                    if (msg == null && msgAttempts < 4)
                    {
                        await Task.Delay(500).ConfigureAwait(false);
                    }
                    msgAttempts++;
                }
                if (msg == null && GetActive(p.GuildId, p.ChannelId) == null)
                {
                    // уже отменён через catch выше
                    continue;
                }
                if (msg == null)
                {
                    // ✅ Round 7-C5: RESTORE_FAIL_PHASE2 should also surface in ЭТАП 3/4.
                    AppendRestoreReport($"RESTORE_FAIL_PHASE2 guild={p.GuildId} reason=message_missing channelId={p.ChannelId} messageId={p.MessageId} bets={p.Bets.Count}");
                    await AutoCancelRestoredPredictionAsync(p, cancelReason: "Восстановление невозможно: сообщение прогноза удалено. Прогноз отменён, ставки возвращены.").ConfigureAwait(false);
                    // ✅ pred-parallelization: RemovePrediction чистит вложенный словарь.
                    RemovePrediction(p);
                    continue;
                }

                // ✅ pred-parallelization: сохраняем канал сообщений по составному ключу.
                _activeChannels[ChannelKey(p.GuildId, p.ChannelId)] = ch;
                AppendRestoreReport($"RESTORE_OK_PHASE2 guild={p.GuildId} channelId={p.ChannelId} messageId={p.MessageId} bets={p.Bets.Count} pool={p.TotalPool}");

                                // ✅ Round 7-C8 bugfix: после успешной валидации прогноза проверить,
                                // что связанное Discord-событие всё ещё живо. Если бот был offline и
                                // за это время событие успели завершить/отменить на стороне Discord —
                                // прогноз должен быть автоматически отменён (ставки возвращены),
                                // потому что правило «нет активного события в канале → нельзя
                                // создавать прогноз» работает в обе стороны.
                                if (p.EventId.HasValue && p.EventId.Value != 0)
                                {
                                    await CheckEventStatusAndCancelIfDoneAsync(p).ConfigureAwait(false);
                                    // CancelAsync удаляет прогноз из _active. Если он уже отменён,
                                    // пропускаем остальные шаги (embed/online-анонс) — этого
                                    // прогноза больше нет в системе.
                                    // ✅ pred-parallelization: сравниваем по (guildId, channelId).
                                    var stillActive = GetActive(p.GuildId, p.ChannelId);
                                    if (stillActive != p)
                                    {
                                        continue;
                                    }
                                }
                            }

                            // ✅ Round 7-C3: после валидации можно безопасно сохранять пустой _active.
                            try { await SaveStateAsync().ConfigureAwait(false); } catch { }
                            _firstReadyValidated = true;
                        }

                        /// <summary>
                        /// ✅ Round 7-C8 bugfix: проверяет, что Discord-событие, к которому привязан
                        /// прогноз, ещё активно. Если событие завершено/отменено (Completed/Cancelled)
                        /// или удалено — отменяет прогноз с isAdminOverride=true и причиной
                        /// «Событие завершено во время offline бота». Используется после
                        /// валидации прогноза в ЭТАП 3/4 (ValidateActiveAfterReadyAsync).
                        /// </summary>
                        private async Task CheckEventStatusAndCancelIfDoneAsync(ActivePrediction p)
                        {
                            if (!p.EventId.HasValue || p.IsResolved) return;
                            var eventId = p.EventId.Value;
                            try
                            {
                                IGuildScheduledEvent? statusSource = null;
                                var guild = _client.GetGuild(p.GuildId);
                                if (guild != null)
                                {
                                    try { statusSource = await guild.GetEventAsync(eventId).ConfigureAwait(false); }
                                    catch (Exception ex)
                                    {
                                        await PredictionErrorLogger.LogAsync("CheckEventStatus:GetEventAsync", ex, $"guild={p.GuildId} eventId={eventId}").ConfigureAwait(false);
                                    }
                                }
                                // REST-фоллбэк: кэш SocketGuild может быть пустым сразу после рестарта.
                                if (statusSource == null)
                                {
                                    try
                                    {
                                        var restGuild = await _client.Rest.GetGuildAsync(p.GuildId).ConfigureAwait(false);
                                        if (restGuild != null)
                                        {
                                            try
                                            {
                                                var restEvent = await restGuild.GetEventAsync(eventId).ConfigureAwait(false);
                                                if (restEvent != null) statusSource = restEvent;
                                            }
                                            catch (Exception ex)
                                            {
                                                await PredictionErrorLogger.LogAsync("CheckEventStatus:RestGetEventAsync", ex, $"guild={p.GuildId} eventId={eventId}").ConfigureAwait(false);
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        await PredictionErrorLogger.LogAsync("CheckEventStatus:RestGetGuild", ex, $"guild={p.GuildId}").ConfigureAwait(false);
                                    }
                                }

                                bool shouldCancel;
                                string reason;
                                if (statusSource == null)
                                {
                                    shouldCancel = true;
                                    reason = "Связанное Discord-событие удалено. Все ставки возвращены.";
                                }
                                else if (statusSource.Status == GuildScheduledEventStatus.Completed
                                        || statusSource.Status == GuildScheduledEventStatus.Cancelled)
                                {
                                    shouldCancel = true;
                                    reason = "Связанное Discord-событие завершено во время offline бота. Все ставки возвращены.";
                                }
                                else
                                {
                                    shouldCancel = false;
                                    reason = string.Empty;
                                }

                                if (shouldCancel)
                                {
                                    AppendRestoreReport($"RESTORE_STALE_CANCEL guild={p.GuildId} eventId={eventId} status={(statusSource?.Status.ToString() ?? "null")} bets={p.Bets.Count}");
                                    var (ok, error) = await CancelAsync(p.GuildId, resolverId: 0, isAdminOverride: true, cancelReason: reason).ConfigureAwait(false);
                                    if (!ok && !string.IsNullOrEmpty(error))
                                    {
                                        await PredictionErrorLogger.LogAsync("CheckEventStatus:Cancel", new Exception(error), $"guild={p.GuildId} eventId={eventId}").ConfigureAwait(false);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                await PredictionErrorLogger.LogAsync("CheckEventStatusAndCancelIfDoneAsync", ex, $"guild={p.GuildId} eventId={eventId}").ConfigureAwait(false);
                            }
                        }

                            private Task OnClientReadyForAnnouncements()
                {
                    var nowUtc = DateTimeOffset.UtcNow;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            foreach (var p in SnapshotAllActive())
                            {
                                if (!p.IsResolved)
                                {
                                    // ✅ Bug 6: фиксируем длительность offline для embed'а
                                    if (p.BotOfflineAtUtc.HasValue)
                                    {
                                        var offlineDuration = nowUtc - p.BotOfflineAtUtc.Value;
                                        if (offlineDuration > TimeSpan.Zero)
                                        {
                                            p.LastOfflineDurationMinutes = Math.Round(offlineDuration.TotalMinutes, 2);
                                        }
                                        p.BotOfflineAtUtc = null;
                                        p.WasBotOfflineOnShutdown = false;
                                        if (!p.IsLocked)
                                        {
                                            var oldClose = p.BetsCloseAtUtc;
                                            if (p.BetsCloseAtUtc < nowUtc + TimeSpan.FromSeconds(15))
                                            {
                                                p.BetsCloseAtUtc = nowUtc + TimeSpan.FromSeconds(15);
                                            }
                                            if (p.BetsCloseAtUtc != oldClose)
                                            {
                                                // ✅ Round 7-C5: сдвиг таймера при возврате
                                                // из reconnect/disconnect цикла — это лог
                                                // восстановления, должен быть в ЭТАП 3/4.
                                                AppendRestoreReport(
                                                    $"OFFLINE_SHIFT_ON_READY guild={p.GuildId} oldClose='{oldClose:HH:mm:ss}' " +
                                                    $"newClose='{p.BetsCloseAtUtc:HH:mm:ss}'");
                                            }
                                        }
                                    }
                                    p.OfflineEvents = p.OfflineEvents ?? new List<OfflineEvent>();
                                    // Защита от дублей Reconnected-событий: только если последнее
                                    // событие было Disconnected и прошло >= 0.5 сек.
                                    var lastEvent = p.OfflineEvents.LastOrDefault();
                                    var isDuplicateReady = lastEvent != null
                                        && lastEvent.Kind == OfflineEventKind.Reconnected
                                        && (nowUtc - lastEvent.AtUtc) < TimeSpan.FromSeconds(2);
                                    if (!isDuplicateReady)
                                    {
                                        p.OfflineEvents.Add(new OfflineEvent
                                        {
                                            AtUtc = nowUtc,
                                            Kind = OfflineEventKind.Reconnected,
                                            Severity = "online",
                                            Note = "Ready"
                                        });
                                    }
                                    // ✅ Bug 6: возвращаем кнопки и шлём сообщение в канал
                                    await UpdateMessageAsync(p, showLocked: p.IsLocked, botOffline: false).ConfigureAwait(false);
                                    try
                                    {
                                        await AnnounceOnlineAsync(p, nowUtc).ConfigureAwait(false);
                                    }
                                    catch (Exception annEx)
                                    {
                                        await PredictionErrorLogger.LogAsync("AnnounceOnlineAsync", annEx, $"guild={p.GuildId} channel={p.ChannelId}").ConfigureAwait(false);
                                    }
                                }
                            }
                            try { await SaveStateAsync().ConfigureAwait(false); }
                            catch (Exception saveEx)
                            {
                                await PredictionErrorLogger.LogAsync("OnClientReadyForAnnouncements:save", saveEx).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            await PredictionErrorLogger.LogAsync("OnClientReadyForAnnouncements", ex).ConfigureAwait(false);
                        }
                    });
                    return Task.CompletedTask;
                }

                private async Task AnnounceOfflineAsync(ActivePrediction p, string kind, DateTimeOffset nowUtc)
                {
                    try
                    {
                        var channel = GetActiveMessageChannel(p);
                        if (channel == null)
                            return;
                        var phase = p.IsLocked ? "Ожидание разрешения прогноза" : "Сбор ставок";
                        string text;
                        if (string.Equals(kind, "restart", StringComparison.Ordinal))
                        {
                                            // ✅ Round 7-C7: разные тексты для рестарта и для остановки.
                                            if (p.IsLocked)
                                            {
                                                text = $"🔄 **Бот ушёл на перезагрузку.**\n" +
                                                        $"Прогноз «{p.Title}» ({phase}) скоро станет доступен снова.";
                                            }
                                            else
                                            {
                                                text = $"🔄 **Бот ушёл на перезагрузку.**\n" +
                                                        $"Прогноз «{p.Title}» ({phase}) скоро станет доступен снова — " +
                                                        $"все ставки в безопасности, таймер будет сдвинут на длительность offline.";
                                            }
                                        }
                                        else if (string.Equals(kind, "stop", StringComparison.Ordinal))
                                        {
                                            if (p.IsLocked)
                                            {
                                                text = $"⛔ **Бот завершил работу.**\n" +
                                                        $"Прогноз «{p.Title}» ({phase}) остаётся в текущем состоянии до следующего запуска бота.";
                                            }
                                            else
                                            {
                                                text = $"⛔ **Бот завершил работу.**\n" +
                                                        $"Прогноз «{p.Title}» ({phase}) остаётся в текущем состоянии — " +
                                                        $"все ставки сохранены на диск, таймер продолжит отсчёт при следующем запуске бота.";
                                            }
                                        }
                                        else
                                        {
                                            if (p.IsLocked)
                                            {
                                                text = $"⛔ **Зафиксировано отключение бота.**\n" +
                                                        $"Прогноз «{p.Title}» ({phase}) будет недоступен до возвращения бота в сеть.";
                                            }
                                            else
                                            {
                                                text = $"⛔ **Зафиксировано отключение бота.**\n" +
                                                        $"Прогноз «{p.Title}» ({phase}) будет недоступен до возвращения бота в сеть. " +
                                                        $"Таймер будет пересчитан с учётом времени offline.";
                                            }
                                        }
                                        // ✅ Bug 6: сохраняем ID сообщения, чтобы потом удалить при возвращении бота.
                                                                                var msg = await channel.SendMessageAsync(text, allowedMentions: new Discord.AllowedMentions { MentionRepliedUser = false })
                                                                                    .ConfigureAwait(false);
                                                                                if (msg != null)
                                                                                {
                                                                                    lock (p.OfflineAnnouncementMessageIds)
                                                                                    {
                                                                                        p.OfflineAnnouncementMessageIds.Add(msg.Id);
                                                                                    }
                                                                                }
                                                                            }
                                    catch (Exception ex)
                    {
                                        await PredictionErrorLogger.LogAsync("AnnounceOfflineAsync", ex, $"guild={p.GuildId}").ConfigureAwait(false);
                                    }
                                }

                                private async Task AnnounceOnlineAsync(ActivePrediction p, DateTimeOffset nowUtc)
                                {
                                    try
                                    {
                                        var channel = GetActiveMessageChannel(p);
                                        if (channel == null)
                                            return;
                                        // ✅ Bug 6 / Round 7-A2: снимаем флаг «идёт рестарт» после первого успешного Ready.
                                        try { Program.ClearRestartPendingFlag(); } catch { }
                                        // ✅ Bug 6: сначала удаляем все накопленные offline-сообщения,
                                        // чтобы не плодить мусор в канале при каждом реконнекте.
                                        List<ulong> toDelete;
                                        lock (p.OfflineAnnouncementMessageIds)
                                        {
                                            toDelete = p.OfflineAnnouncementMessageIds.ToList();
                                            p.OfflineAnnouncementMessageIds.Clear();
                                        }
                                        foreach (var msgId in toDelete)
                                        {
                                            try
                                            {
                                                var oldMsg = await channel.GetMessageAsync(msgId).ConfigureAwait(false) as IUserMessage;
                                                if (oldMsg != null)
                                                {
                                                    await oldMsg.DeleteAsync().ConfigureAwait(false);
                                                }
                                            }
                                            catch (Exception delEx)
                                            {
                                                // ✅ Round 7-C7: HTTP 10008 (Unknown Message) — нормальная ситуация
                                                // (сообщение удалено пользователем вручную или каналом). Не логируем.
                                                int code = 0;
                                                if (delEx is Discord.Net.HttpException hex) code = (int)hex.HttpCode;
                                                if (code != 10008)
                                                {
                                                    await PredictionErrorLogger.LogAsync("AnnounceOnlineAsync:delete", delEx, $"guild={p.GuildId} msgId={msgId}").ConfigureAwait(false);
                                                }
                                            }
                                        }
                                        var phase = p.IsLocked ? "Ожидание разрешения прогноза" : "Сбор ставок";
                                        string offlineInfo = string.Empty;
                                        // ✅ Round 7-C8: для IsLocked (прогноз уже в фазе разрешения,
                                        // таймер не сдвигается) упоминание offline длительности
                                        // неуместно — печатаем только если фаза — сбор ставок.
                                        if (!p.IsLocked && p.LastOfflineDurationMinutes.HasValue && p.LastOfflineDurationMinutes.Value > 0)
                                        {
                                            var dur = p.LastOfflineDurationMinutes.Value;
                                            offlineInfo = dur < 1
                                                ? $" Бот был offline менее минуты."
                                                : $" Бот был offline ~{dur:F1} мин.";
                                        }
                                        var text = $"✅ **Бот снова в сети.** Прогноз «{p.Title}» ({phase}) снова активен.{offlineInfo}";
                                        var onlineMsg = await channel.SendMessageAsync(text, allowedMentions: new Discord.AllowedMentions { MentionRepliedUser = false })
                                            .ConfigureAwait(false);
                                        // ✅ Round 7-C4: сохраняем ID «online»-сообщения, чтобы удалить
                                        // его через 5 минут (канал не должен копить мусор).
                                        if (onlineMsg != null)
                                        {
                                            p.OnlineAnnouncementMessageIds.Add(new OnlineAnnouncementMessage
                                            {
                                                MessageId = onlineMsg.Id,
                                                SentAtUtc = nowUtc
                                            });
                                            _ = ScheduleOnlineMessageCleanupAsync(p, onlineMsg.Id, TimeSpan.FromMinutes(5));
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        await PredictionErrorLogger.LogAsync("AnnounceOnlineAsync", ex, $"guild={p.GuildId}").ConfigureAwait(false);
                                    }
                                }

        // ✅ Round 7-C4: через заданное время пытаемся удалить «online»-сообщение.
        // Запускается фоновым таском; ошибки игнорируются (сообщение могло быть
        // удалено пользователем вручную).
        private async Task ScheduleOnlineMessageCleanupAsync(ActivePrediction p, ulong msgId, TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                // Проверяем, что прогноз ещё существует и ID всё ещё актуален.
                // ✅ pred-parallelization: сравниваем по (guildId, channelId).
                if (GetActive(p.GuildId, p.ChannelId) != p)
                    return;
                var channel = GetActiveMessageChannel(p);
                if (channel == null) return;
                try
                {
                    var msg = await channel.GetMessageAsync(msgId).ConfigureAwait(false) as IUserMessage;
                    if (msg != null)
                    {
                        await msg.DeleteAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception delEx)
                                {
                                    // ✅ Round 7-C7: HTTP 10008 (Unknown Message) и NotFound — это нормальная
                                    // ситуация (сообщение удалено пользователем вручную или каналом).
                                    // Не логируем как ошибку, но убираем ID из списка, чтобы не пытаться
                                    // удалять несуществующее сообщение снова при следующем восстановлении.
                                    int code = 0;
                                    if (delEx is Discord.Net.HttpException hex) code = (int)hex.HttpCode;
                                    if (code != 10008)
                                    {
                                        await PredictionErrorLogger.LogAsync("ScheduleOnlineMessageCleanupAsync:delete", delEx, $"guild={p.GuildId} msgId={msgId}").ConfigureAwait(false);
                                    }
                                }
                finally
                                {
                                    // Убираем ID из списка, даже если удаление не удалось.
                                    p.OnlineAnnouncementMessageIds.RemoveAll(o => o.MessageId == msgId);
                                    try { await SaveStateAsync().ConfigureAwait(false); } catch { }
                                }
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("ScheduleOnlineMessageCleanupAsync", ex).ConfigureAwait(false);
            }
        }

        internal class PersistentPrediction
        {
            public ulong GuildId { get; set; }
            public ulong CreatorId { get; set; }
            public ulong ChannelId { get; set; }
            public ulong MessageId { get; set; }
            public string Title { get; set; } = string.Empty;
            // ✅ Round 7-C8: связь прогноза с Discord-событием (см. ActivePrediction.EventId).
            public ulong? EventId { get; set; }

            // ✅ Новое поле для поддержки N исходов
            public List<PredictionOutcome>? Outcomes { get; set; }

            // Старые поля для обратной совместимости
            public PredictionOutcome? Outcome1 { get; set; }
            public PredictionOutcome? Outcome2 { get; set; }

            public DateTimeOffset CreatedAtUtc { get; set; }
            public DateTimeOffset BetsCloseAtUtc { get; set; }
            // ✅ Bug 5: если бот был офлайн в момент активного приёма ставок,
            // здесь сохраняется время ухода в offline. При LoadStateAsync мы
            // сдвигаем BetsCloseAtUtc на длительность offline, чтобы таймер
            // не "схлопнулся" в прошлое и приём ставок не закрылся мгновенно.
            public DateTimeOffset? BotOfflineAtUtc { get; set; }
            // Длительность последнего offline-периода (мин), отображается в embed'ах
            public double? LastOfflineDurationMinutes { get; set; }
            // true = бот сейчас offline (для восстановления состояния кнопок/embed)
            public bool WasBotOfflineOnShutdown { get; set; }
            // ✅ Bug 6: лог offline/online для embed результата и истории
            public List<OfflineEvent> OfflineEvents { get; set; } = new();
            // ✅ Bug 6 (новое): ID сообщений-объявлений offline/online для удаления
            // их из канала при возвращении бота в сеть. Сохраняем в файле, чтобы
            // корректно убрать «мусорные» сообщения даже после рестарта.
            public List<ulong> OfflineAnnouncementMessageIds { get; set; } = new();
            // ✅ Round 7-C4: ID «online»-сообщений + время отправки для отложенного
            // удаления через 5 минут. Сериализуется, чтобы не терять ID при рестарте.
            public List<OnlineAnnouncementMessage> OnlineAnnouncementMessageIds { get; set; } = new();
            public bool IsLocked { get; set; }
            public bool IsResolved { get; set; }
            public int? WinningOutcomeId { get; set; }
            public Dictionary<ulong, PredictionBet> Bets { get; set; } = new();
        }

        private async Task SaveStateAsync()
        {
                    await _stateFileGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        // ✅ Round 7-C3 / Round 7-C7: защита от стирания файла при пустом _active.
                                                // Два условия:
                                                //   1) weLoadedStateAlready: в текущем ЖЦ LoadStateAsync уже прочитал файл.
                                                //      До этого момента SaveStateAsync НЕ имеет права писать — мы не знаем,
                                                //      что лежит на диске, и при _active.IsEmpty затёрли бы содержимое.
                                                //      Это спасает в сценарии, когда restart-loop итерирует несколько раз:
                                                //      во второй итерации _active ещё пуст, а LoadStateAsync ещё не звался.
                                                //   2) hadContent && !firstValidated: классический safe-mode из R7-C3.
                                                // Если оба условия сработали, не пишем ничего в файл.
                                                // ✅ pred-parallelization: _active теперь вложенный. Проверка IsEmpty
                                                // должна идти по всем гильдиям и всем каналам.
                                                bool allEmpty = true;
                                                foreach (var kv in _active)
                                                {
                                                    if (kv.Value != null && !kv.Value.IsEmpty)
                                                    {
                                                        allEmpty = false;
                                                        break;
                                                    }
                                                }
                                                if (!_weLoadedStateAlready && allEmpty)
                                                {
                                                    AppendRestoreReport("SAVE_STATE_BLOCKED reason=_active_empty_pre_load");
                                                    return;
                                                }
                                                if (allEmpty && _stateFileHadContentOnStartup && !_firstReadyValidated)
                                                {
                                                    AppendRestoreReport("SAVE_STATE_BLOCKED reason=_active_empty_pre_ready");
                                                    return;
                                                }
                        // ✅ pred-parallelization: вложенный snapshot. Внешний ключ — guildId,
                        // внутренний — channelId. Это позволяет иметь несколько прогнозов
                        // на одной гильдии (по одному на каждый активный голосовой канал).
                        var snapshot = new Dictionary<ulong, Dictionary<ulong, PersistentPrediction>>();
                        foreach (var kv in _active)
                        {
                            if (kv.Value == null) continue;
                            var guildDict = new Dictionary<ulong, PersistentPrediction>();
                            foreach (var inner in kv.Value)
                            {
                                var v = inner.Value;
                                guildDict[inner.Key] = new PersistentPrediction
                                {
                                    GuildId = v.GuildId,
                                    CreatorId = v.CreatorId,
                                    ChannelId = v.ChannelId,
                                    MessageId = v.MessageId,
                                    Title = v.Title,
                                    Outcomes = v.Outcomes,
                                    EventId = v.EventId,
                                    CreatedAtUtc = v.CreatedAtUtc,
                                    BetsCloseAtUtc = v.BetsCloseAtUtc,
                                    BotOfflineAtUtc = v.BotOfflineAtUtc,
                                    LastOfflineDurationMinutes = v.LastOfflineDurationMinutes,
                                    WasBotOfflineOnShutdown = v.WasBotOfflineOnShutdown,
                                    OfflineEvents = v.OfflineEvents?.ToList() ?? new List<OfflineEvent>(),
                                    OfflineAnnouncementMessageIds = v.OfflineAnnouncementMessageIds != null
                                        ? new List<ulong>(v.OfflineAnnouncementMessageIds)
                                        : new List<ulong>(),
                                    OnlineAnnouncementMessageIds = v.OnlineAnnouncementMessageIds != null
                                        ? v.OnlineAnnouncementMessageIds.Select(o => new OnlineAnnouncementMessage { MessageId = o.MessageId, SentAtUtc = o.SentAtUtc }).ToList()
                                        : new List<OnlineAnnouncementMessage>(),
                                    IsLocked = v.IsLocked,
                                    IsResolved = v.IsResolved,
                                    WinningOutcomeId = v.WinningOutcomeId,
                                    Bets = new Dictionary<ulong, PredictionBet>(v.Bets)
                                };
                            }
                            snapshot[kv.Key] = guildDict;
                        }

                        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                        var json = System.Text.Json.JsonSerializer.Serialize(snapshot, options);
                        // Атомарная запись: исключает повреждение файла при крэше посреди сереализации
                        // и при одновременной записи с другого процесса (lock + запись во временный файл + rename).
                        await SafeJsonIO.WriteAtomicAsync(_stateFilePath, json).ConfigureAwait(false);
            }
                    catch (Exception ex)
            {
                        await PredictionErrorLogger.LogAsync("SaveStateAsync", ex).ConfigureAwait(false);
                    }
                    finally
                    {
                        _stateFileGate.Release();
                    }
                }

        public async Task<bool> EnsureStateFileAsync()
        {
                    // ✅ Bug C: если файл уже есть — НЕ перезаписываем его пустым _active.
                    // Раньше этот метод всегда звал SaveStateAsync(), который сериализовал
                    // пустой ConcurrentDictionary (_active ещё не заполнен до LoadStateAsync)
                    // и затирал сохранённый offline-прогноз. Теперь на рестарте файл
                    // остаётся нетронутым, и LoadStateAsync сможет прочитать прогноз.
                    if (File.Exists(_stateFilePath)) return false;
                    await SaveStateAsync().ConfigureAwait(false);
                    if (!File.Exists(_stateFilePath))
                    {
                        var dir = Path.GetDirectoryName(_stateFilePath) ?? AppContext.BaseDirectory;
                        Directory.CreateDirectory(dir);
                        await _stateFileGate.WaitAsync().ConfigureAwait(false);
                        try
                        {
                                        // ✅ R6 fix: создание пустого файла через SafeJsonIO (атомарно).
                                        await SafeJsonIO.WriteAtomicAsync(_stateFilePath, "{}", CancellationToken.None).ConfigureAwait(false);
                                    }
                                    finally
                                    {
                                        _stateFileGate.Release();
                                    }
                                }
                                return true;
                            }

                /// <summary>
                                                /// ✅ Round 7-C6: устаревший прямой вход в LoadStateAsync больше
                                                /// не нужен — вся загрузка/проверка прогнозов живёт в
                                                /// RunStage3RestoreAsync(), который вызывается из ЭТАП 3/4.
                                                /// Метод оставлен как deprecated-обёртка для будущих вызовов
                                                /// из юнит-тестов и не должен вызываться из production-кода.
                                                /// </summary>
                                                [Obsolete("Use RunStage3RestoreAsync from ЭТАП 3/4 instead.")]
                                                public Task LoadStateOnStartupAsync() => LoadStateAsync(validate: false);

        /// <summary>
        /// ✅ pred-parallelization: десериализация нового вложенного формата.
        /// Если структура не подходит (это старый формат) — возвращаем null и
        /// вызывающий код пробует <see cref="MigrateLegacyFormat"/>.
        /// </summary>
        private static Dictionary<ulong, Dictionary<ulong, PersistentPrediction>>? TryDeserializeNested(string json)
        {
            try
            {
                var result = System.Text.Json.JsonSerializer.Deserialize<Dictionary<ulong, Dictionary<ulong, PersistentPrediction>>>(json);
                if (result == null) return null;
                // Эвристика: если хоть одна value — это PersistentPrediction напрямую
                // (а не вложенный dict), значит формат старый. JsonSerializer десериализует
                // и то, и другое без ошибок — отличить можно по наличию поля ChannelId
                // на верхнем уровне. Используем try-каст через проверку.
                foreach (var kv in result)
                {
                    foreach (var inner in kv.Value)
                    {
                        if (inner.Value != null) return result;
                    }
                    // пустой внутренний dict — не считаем ошибкой, но и не сигнализируем.
                }
                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// ✅ pred-parallelization: миграция старого формата (один прогноз на гильдию)
        /// в новый вложенный. ChannelId берётся из PersistentPrediction.ChannelId.
        /// Если десериализация не удалась — возвращаем пустой словарь.
        /// </summary>
        private static Dictionary<ulong, Dictionary<ulong, PersistentPrediction>>? MigrateLegacyFormat(string json)
        {
            try
            {
                var legacy = System.Text.Json.JsonSerializer.Deserialize<Dictionary<ulong, PersistentPrediction>>(json);
                if (legacy == null) return new Dictionary<ulong, Dictionary<ulong, PersistentPrediction>>();
                var nested = new Dictionary<ulong, Dictionary<ulong, PersistentPrediction>>();
                foreach (var kv in legacy)
                {
                    if (kv.Value == null) continue;
                    var guildId = kv.Value.GuildId != 0 ? kv.Value.GuildId : kv.Key;
                    var channelId = kv.Value.ChannelId != 0 ? kv.Value.ChannelId : 0UL;
                    if (channelId == 0) continue;
                    if (!nested.TryGetValue(guildId, out var inner))
                    {
                        inner = new Dictionary<ulong, PersistentPrediction>();
                        nested[guildId] = inner;
                    }
                    inner[channelId] = kv.Value;
                }
                return nested;
            }
            catch
            {
                return null;
            }
        }

                        private async Task LoadStateAsync(bool validate = true)
        {
                                    await _stateFileGate.WaitAsync().ConfigureAwait(false);
                                    try
                                    {
                                        if (!File.Exists(_stateFilePath))
                                        {
                                                                                    // ✅ Round 7-C7: нет файла на диске — это тоже «состояние загружено».
                                                                                    // Иначе первая же попытка SaveStateAsync (например, в Shutdown)
                                                                                    // создаст пустой {} и прибьёт любые параллельные попытки восстановления.
                                                                                    _stateFileHadContentOnStartup = false;
                                                                                    _weLoadedStateAlready = true;
                                                                                    return;
                                                                                }
                                        var json = await File.ReadAllTextAsync(_stateFilePath).ConfigureAwait(false);
                                                        // ✅ Round 7-C3: фиксируем, что файл был НЕпустой на момент старта —
                                                        // это включает "безопасный режим" SaveStateAsync.
                                                        try
                                                        {
                                                            var len = (await File.ReadAllBytesAsync(_stateFilePath).ConfigureAwait(false)).Length;
                                                            _stateFileHadContentOnStartup = len > 2; // больше "{}"
                                                        }
                                                        catch { _stateFileHadContentOnStartup = false; }
                                                        // ✅ pred-parallelization: новый формат — вложенный словарь.
                                                        // Для обратной совместимости со СТАРЫМ форматом (один прогноз
                                                        // на гильдию) пробуем сначала новый формат, при неудаче —
                                                        // старый, и мигрируем «на лету».
                                                        Dictionary<ulong, Dictionary<ulong, PersistentPrediction>>? nested =
                                                            TryDeserializeNested(json)
                                                            ?? MigrateLegacyFormat(json);
                                                        // ✅ Round 7-C7: отметить, что LoadStateAsync отработал в этом ЖЦ —
                                                        // с этого момента SaveStateAsync может писать.
                                                        _weLoadedStateAlready = true;
                                                        if (nested == null) return;
                                                        // ✅ pred-parallelization: track каналы, которые были удалены из-за
                                                        // RESTORE_FAIL, чтобы потом пересохранить очищенный snapshot.
                                                        var dirtyGuilds = new HashSet<ulong>();

                foreach (var guildKv in nested)
                {
                    var guildId = guildKv.Key;
                    foreach (var channelKv in guildKv.Value)
                    {
                        try
                        {
                            var p = channelKv.Value;

                            // Skip already resolved/cancelled items.
                            if (p.IsResolved)
                                continue;

                            var ap = new ActivePrediction
                            {
                                GuildId = p.GuildId,
                                CreatorId = p.CreatorId,
                                ChannelId = p.ChannelId,
                                MessageId = p.MessageId,
                                Title = p.Title,
                                EventId = p.EventId,
                                CreatedAtUtc = p.CreatedAtUtc,
                                BetsCloseAtUtc = p.BetsCloseAtUtc,
                                BotOfflineAtUtc = p.BotOfflineAtUtc,
                                LastOfflineDurationMinutes = p.LastOfflineDurationMinutes,
                                WasBotOfflineOnShutdown = p.WasBotOfflineOnShutdown,
                                OfflineEvents = p.OfflineEvents ?? new List<OfflineEvent>(),
                                OfflineAnnouncementMessageIds = p.OfflineAnnouncementMessageIds ?? new List<ulong>(),
                                OnlineAnnouncementMessageIds = p.OnlineAnnouncementMessageIds ?? new List<OnlineAnnouncementMessage>(),
                                IsLocked = p.IsLocked,
                                IsResolved = p.IsResolved,
                                WinningOutcomeId = p.WinningOutcomeId,
                                Bets = p.Bets ?? new Dictionary<ulong, PredictionBet>()
                            };

                            AppendRestoreReport($"RESTORE_DEBUG guild={p.GuildId} betsFromFile={p.Bets?.Count ?? 0} betsInAP={ap.Bets.Count}");

                            if (!ap.IsLocked && !ap.IsResolved && p.BotOfflineAtUtc.HasValue && p.WasBotOfflineOnShutdown)
                            {
                                var offlineAt = p.BotOfflineAtUtc.Value;
                                var nowUtc = DateTimeOffset.UtcNow;
                                var offlineDuration = nowUtc - offlineAt;
                                if (offlineDuration > TimeSpan.Zero)
                                {
                                    var oldCloseAt = ap.BetsCloseAtUtc;
                                    ap.BetsCloseAtUtc = oldCloseAt + offlineDuration;
                                    ap.LastOfflineDurationMinutes = Math.Round(offlineDuration.TotalMinutes, 2);
                                    AppendRestoreReport(
                                        $"OFFLINE_SHIFT guild={p.GuildId} channel={p.ChannelId} offlineAt='{offlineAt:yyyy-MM-dd HH:mm:ss}' " +
                                        $"offlineDuration={offlineDuration} oldClose='{oldCloseAt:HH:mm:ss}' newClose='{ap.BetsCloseAtUtc:HH:mm:ss}'");
                                }
                                ap.BotOfflineAtUtc = null;
                                ap.WasBotOfflineOnShutdown = false;
                            }

                            if (p.Outcomes != null && p.Outcomes.Count > 0)
                            {
                                ap.Outcomes = p.Outcomes;
                            }
                            else
                            {
                                ap.Outcomes.Add(p.Outcome1 ?? new PredictionOutcome { Id = 1 });
                                ap.Outcomes.Add(p.Outcome2 ?? new PredictionOutcome { Id = 2 });
                            }

                            foreach (var outcome in ap.Outcomes)
                            {
                                outcome.TotalStake = 0;
                            }

                            foreach (var b in ap.Bets.Values)
                            {
                                var outcome = ap.GetOutcomeById(b.OutcomeId);
                                if (outcome != null)
                                {
                                    outcome.TotalStake += b.Amount;
                                }
                            }

                            var restoredOutcomesList = string.Join(", ", ap.Outcomes.Select(o => $"{o.Id}: {o.Name}"));
                            ap.UseCompactOutcomeLabels = $"Исход ({restoredOutcomesList})".Length > 45;
                            ap.UseInlineOutcomeFields = ap.Outcomes.Count <= 3
                                && ap.Outcomes.All(o => $"📊 Исход {o.Id}: {o.Name}".Length <= 256);

                            if (!validate)
                            {
                                // ✅ pred-parallelization: добавляем во вложенный словарь.
                                var byChannel = _active.GetOrAdd(ap.GuildId, _ => new ConcurrentDictionary<ulong, ActivePrediction>());
                                byChannel[ap.ChannelId] = ap;
                                AppendRestoreReport($"RESTORE_PHASE1 guild={p.GuildId} channelId={p.ChannelId} messageId={p.MessageId} bets={ap.Bets.Count} pool={ap.TotalPool} deferred=true");
                                continue;
                            }

                            ISocketMessageChannel? ch = _client.GetChannel(p.ChannelId) as ISocketMessageChannel
                                ?? _client.GetGuild(p.GuildId)?.GetChannel(p.ChannelId) as ISocketMessageChannel;

                            if (ch == null)
                            {
                                try
                                {
                                    var fetched = await _client.GetChannelAsync(p.ChannelId).ConfigureAwait(false);
                                    ch = fetched as ISocketMessageChannel;
                                }
                                catch (Exception ex)
                                {
                                    await PredictionErrorLogger.LogAsync("LoadStateAsync:GetChannel", ex, $"guild={p.GuildId} channel={p.ChannelId}").ConfigureAwait(false);
                                }
                            }

                            if (ch == null || p.MessageId == 0)
                            {
                                AppendRestoreReport($"RESTORE_FAIL guild={p.GuildId} reason=channel_missing channelId={p.ChannelId} messageId={p.MessageId} bets={ap.Bets.Count}");
                                await AutoCancelRestoredPredictionAsync(ap, cancelReason: "Восстановление невозможно: сообщение прогноза не найдено. Прогноз отменён, ставки возвращены.").ConfigureAwait(false);
                                dirtyGuilds.Add(guildId);
                                continue;
                            }

                            try
                            {
                                var msg = await ch.GetMessageAsync(p.MessageId).ConfigureAwait(false);
                                if (msg == null)
                                {
                                    AppendRestoreReport($"RESTORE_FAIL guild={p.GuildId} reason=message_missing channelId={p.ChannelId} messageId={p.MessageId} bets={ap.Bets.Count}");
                                    await AutoCancelRestoredPredictionAsync(ap, cancelReason: "Восстановление невозможно: сообщение прогноза удалено. Прогноз отменён, ставки возвращены.").ConfigureAwait(false);
                                    dirtyGuilds.Add(guildId);
                                    continue;
                                }
                            }
                            catch
                            {
                                AppendRestoreReport($"RESTORE_FAIL guild={p.GuildId} reason=message_check_error channelId={p.ChannelId} messageId={p.MessageId} bets={ap.Bets.Count}");
                                await AutoCancelRestoredPredictionAsync(ap, cancelReason: "Восстановление невозможно: ошибка проверки сообщения. Прогноз отменён, ставки возвращены.").ConfigureAwait(false);
                                dirtyGuilds.Add(guildId);
                                continue;
                            }

                            // ✅ pred-parallelization: добавляем во вложенный словарь и
                            // регистрируем канал по составному ключу.
                            var byCh = _active.GetOrAdd(ap.GuildId, _ => new ConcurrentDictionary<ulong, ActivePrediction>());
                            byCh[ap.ChannelId] = ap;
                            _activeChannels[ChannelKey(ap.GuildId, ap.ChannelId)] = ch;

                            AppendRestoreReport($"RESTORE_OK guild={p.GuildId} channelId={p.ChannelId} messageId={p.MessageId} bets={ap.Bets.Count} pool={ap.TotalPool}");
                        }
                        catch (Exception ex)
                        {
                            await PredictionErrorLogger.LogAsync("LoadStateAsync:entry", ex, $"guild={guildKv.Key} channel={channelKv.Key}").ConfigureAwait(false);
                        }
                    }
                }

                _ = Task.Run(async () => await SaveStateAsync().ConfigureAwait(false));

                // Если были удалены сломанные записи — пересохраняем очищенный snapshot.
                // Это делается автоматически через _ = Task.Run выше, дополнительно явно
                // сериализуем только если были dirty-гильдии (на случай гонки).
                if (dirtyGuilds.Count > 0)
                {
                    try
                    {
                        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                        var snapshot = new Dictionary<ulong, Dictionary<ulong, PersistentPrediction>>();
                        foreach (var kv in _active)
                        {
                            if (kv.Value == null) continue;
                            var guildDict = new Dictionary<ulong, PersistentPrediction>();
                            foreach (var inner in kv.Value)
                            {
                                var v = inner.Value;
                                guildDict[inner.Key] = new PersistentPrediction
                                {
                                    GuildId = v.GuildId,
                                    CreatorId = v.CreatorId,
                                    ChannelId = v.ChannelId,
                                    MessageId = v.MessageId,
                                    Title = v.Title,
                                    Outcomes = v.Outcomes,
                                    EventId = v.EventId,
                                    CreatedAtUtc = v.CreatedAtUtc,
                                    BetsCloseAtUtc = v.BetsCloseAtUtc,
                                    BotOfflineAtUtc = v.BotOfflineAtUtc,
                                    LastOfflineDurationMinutes = v.LastOfflineDurationMinutes,
                                    WasBotOfflineOnShutdown = v.WasBotOfflineOnShutdown,
                                    OfflineEvents = v.OfflineEvents?.ToList() ?? new List<OfflineEvent>(),
                                    OfflineAnnouncementMessageIds = v.OfflineAnnouncementMessageIds != null
                                        ? new List<ulong>(v.OfflineAnnouncementMessageIds)
                                        : new List<ulong>(),
                                    OnlineAnnouncementMessageIds = v.OnlineAnnouncementMessageIds != null
                                        ? v.OnlineAnnouncementMessageIds.Select(o => new OnlineAnnouncementMessage { MessageId = o.MessageId, SentAtUtc = o.SentAtUtc }).ToList()
                                        : new List<OnlineAnnouncementMessage>(),
                                    IsLocked = v.IsLocked,
                                    IsResolved = v.IsResolved,
                                    WinningOutcomeId = v.WinningOutcomeId,
                                    Bets = new Dictionary<ulong, PredictionBet>(v.Bets)
                                };
                            }
                            snapshot[kv.Key] = guildDict;
                        }
                        var cleanedJson = System.Text.Json.JsonSerializer.Serialize(snapshot, options);
                        await SafeJsonIO.WriteAtomicAsync(_stateFilePath, cleanedJson).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await PredictionErrorLogger.LogAsync("LoadStateAsync:saveCleanedState", ex).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("LoadStateAsync", ex).ConfigureAwait(false);
            }
            finally
            {
                _stateFileGate.Release();
            }
        }

        private async Task AutoCancelRestoredPredictionAsync(ActivePrediction p, string cancelReason)
        {
            try
            {
                // Refund
                await p.Sync.WaitAsync().ConfigureAwait(false);
                try
                {
                    foreach (var bet in p.Bets.Values)
                        _points.Add(p.GuildId, bet.UserId, bet.Amount);
                }
                finally
                {
                    p.Sync.Release();
                }

                // Try to notify in channel
                try
                {
                    var channel = _client.GetChannel(p.ChannelId) as ISocketMessageChannel
                        ?? _client.GetGuild(p.GuildId)?.GetChannel(p.ChannelId) as ISocketMessageChannel;
                    if (channel != null)
                    {
                        var cancelEmbed = BuildCancelEmbed(p, cancelReason);
                        await channel.SendMessageAsync(embed: cancelEmbed).ConfigureAwait(false);
                    }
                }
                            catch (Exception ex)
                            {
                                await PredictionErrorLogger.LogAsync("LoadStateAsync:GetChannelAsync", ex, $"guild={p.GuildId} channel={p.ChannelId}").ConfigureAwait(false);
                            }

                AppendRestoreReport($"AUTO_CANCEL_RESTORE guild={p.GuildId} channelId={p.ChannelId} bets={p.Bets.Count} reason='{cancelReason}'");
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("AutoCancelRestoredPredictionAsync", ex, $"guild={p.GuildId} channel={p.ChannelId}").ConfigureAwait(false);
            }
        }

        public Task HandleSlashCommand(SocketSlashCommand command)
        {
            return command.RespondAsync("Команда prediction временно недоступна.", ephemeral: true);
        }

        /// <summary>
        /// ✅ pred-parallelization: итерация по ВСЕМ активным прогнозам на ВСЕХ гильдиях
        /// во вложенном словаре. Возвращает плоский список для удобства foreach.
        /// </summary>
        private IEnumerable<ActivePrediction> EnumerateAllActive()
        {
            foreach (var guildKv in _active)
            {
                if (guildKv.Value == null) continue;
                foreach (var innerKv in guildKv.Value)
                {
                    if (innerKv.Value != null) yield return innerKv.Value;
                }
            }
        }

        /// <summary>
        /// ✅ pred-parallelization: то же самое, но как ToArray-снимок (безопасно для
        /// асинхронной работы внутри цикла).
        /// </summary>
        private ActivePrediction[] SnapshotAllActive()
        {
            var list = new List<ActivePrediction>();
            foreach (var p in EnumerateAllActive()) list.Add(p);
            return list.ToArray();
        }

        // ✅ pred-parallelization: составной ключ для _activeChannels. Прогнозов может
        // быть несколько на одной гильдии, поэтому ключ — это пара (guildId, channelId).
        internal static string ChannelKey(ulong guildId, ulong channelId) => $"{guildId}:{channelId}";

        // ✅ pred-parallelization: основной метод получения прогноза. Возвращает прогноз
        // для конкретного голосового канала. Если такого нет — null. Используется везде,
        // где раньше был `GetActive(guildId)`, но с дополнительным знанием channelId.
        public ActivePrediction? GetActive(ulong guildId, ulong channelId)
        {
            if (_active.TryGetValue(guildId, out var byChannel) && byChannel != null)
            {
                if (byChannel.TryGetValue(channelId, out var p)) return p;
            }
            return null;
        }

        /// <summary>
        /// ✅ pred-parallelization: legacy-метод для обратной совместимости со старыми
        /// вызовами (info/profile/buttons). Возвращает ЛЮБОЙ активный прогноз на гильдии
        /// (первый по внутреннему словарю). Новый код должен использовать перегрузку с
        /// channelId или <see cref="GetAllActive"/>.
        /// </summary>
        public ActivePrediction? GetActive(ulong guildId)
        {
            if (_active.TryGetValue(guildId, out var byChannel) && byChannel != null && !byChannel.IsEmpty)
            {
                foreach (var p in byChannel.Values) return p;
            }
            return null;
        }

        /// <summary>
        /// ✅ pred-parallelization: вернуть все активные прогнозы на гильдии. Используется
        /// в info/profile для отображения списка, а также во внутренних итерациях.
        /// </summary>
        public IReadOnlyList<ActivePrediction> GetAllActive(ulong guildId)
        {
            if (_active.TryGetValue(guildId, out var byChannel) && byChannel != null)
            {
                return byChannel.Values.ToList();
            }
            return Array.Empty<ActivePrediction>();
        }

        public async Task<(bool ok, string error, ActivePrediction? prediction)> CreateAsync(
            ulong guildId,
            ulong creatorId,
            ISocketMessageChannel targetChannel,
            string title,
            string outcome1Name,
            string outcome2Name,
            TimeSpan duration)
        {
            // Используем новую перегрузку с 2 исходами
            return await CreateAsync(guildId, creatorId, targetChannel, title, new[] { outcome1Name, outcome2Name }, duration, eventId: null).ConfigureAwait(false);
        }

        /// <summary>
        /// ✅ Round 7-C8: перегрузка с привязкой к Discord-событию. Используется, когда
        /// прогноз создаётся во время активного события, чтобы при завершении/отмене
        /// события (включая stale-cleanup) прогноз автоматически отменился.
        /// </summary>
        public async Task<(bool ok, string error, ActivePrediction? prediction)> CreateAsync(
            ulong guildId,
            ulong creatorId,
            ISocketMessageChannel targetChannel,
            string title,
            string outcome1Name,
            string outcome2Name,
            TimeSpan duration,
            ulong? eventId)
        {
            return await CreateAsync(guildId, creatorId, targetChannel, title, new[] { outcome1Name, outcome2Name }, duration, eventId).ConfigureAwait(false);
        }

        /// <summary>
        /// ✅ НОВАЯ ПЕРЕГРУЗКА: Создание прогноза с произвольным количеством исходов
        /// </summary>
        public async Task<(bool ok, string error, ActivePrediction? prediction)> CreateAsync(
            ulong guildId,
            ulong creatorId,
            ISocketMessageChannel targetChannel,
            string title,
            string[] outcomeNames,
            TimeSpan duration)
        {
            return await CreateAsync(guildId, creatorId, targetChannel, title, outcomeNames, duration, eventId: null).ConfigureAwait(false);
        }

        /// <summary>
        /// ✅ Round 7-C8: перегрузка с привязкой к Discord-событию и N исходами.
        /// </summary>
        public async Task<(bool ok, string error, ActivePrediction? prediction)> CreateAsync(
            ulong guildId,
            ulong creatorId,
            ISocketMessageChannel targetChannel,
            string title,
            string[] outcomeNames,
            TimeSpan duration,
            ulong? eventId)
        {
            // ✅ pred-parallelization: проверяем наличие прогноза в КОНКРЕТНОМ канале,
            // а не на всей гильдии. Теперь на одной гильдии может быть несколько
            // параллельных прогнозов в разных каналах.
            if (targetChannel == null)
            {
                await LogAsync($"CREATE_FAIL_CHANNEL guild={guildId} creator={creatorId} channelId=0 rawType=null rawName='(без имени)'");
                return (false, "Не удалось найти канал для создания прогноза.", null);
            }

            var channelId = targetChannel.Id;
            if (GetActive(guildId, channelId) != null)
                return (false, "В этом голосовом канале уже есть активный прогноз.", null);

            if (outcomeNames == null || outcomeNames.Length < 2)
                return (false, "Должно быть минимум 2 исхода.", null);

            if (outcomeNames.Length > 10)
                return (false, "Максимум 10 исходов.", null);

            if (duration <= TimeSpan.Zero)
                duration = TimeSpan.FromMinutes(1);
            var now = DateTimeOffset.UtcNow;
            var closeAt = now + duration;

            var guild = _client.GetGuild(guildId);
            var guildName = guild?.Name ?? "(без_названия)";
            var creatorGuildUser = guild?.GetUser(creatorId);
            var creatorUser = creatorGuildUser ?? _client.GetUser(creatorId);
            var creatorName = creatorGuildUser?.DisplayName
                                ?? creatorGuildUser?.Nickname
                                ?? creatorGuildUser?.Username
                                ?? creatorUser?.Username
                                ?? creatorId.ToString();
            var channelName = targetChannel is IChannel c ? c.Name ?? "(без имени)" : "(без имени)";

            var prediction = new ActivePrediction
            {
                GuildId = guildId,
                CreatorId = creatorId,
                ChannelId = channelId,
                Title = title,
                CreatedAtUtc = now,
                BetsCloseAtUtc = closeAt,
                IsLocked = false,
                IsResolved = false,
                // ✅ Round 7-C8: связь с Discord-событием (если прогноз создан во время события).
                EventId = eventId
            };

            // Создаём исходы из массива названий
            for (int i = 0; i < outcomeNames.Length; i++)
            {
                prediction.Outcomes.Add(new PredictionOutcome
                {
                    Id = i + 1,
                    Name = outcomeNames[i].Trim()
                });
            }

            var outcomesList = string.Join(", ", prediction.Outcomes.Select(o => $"{o.Id}: {o.Name}"));
            prediction.UseCompactOutcomeLabels = $"Исход ({outcomesList})".Length > 45;
            prediction.UseInlineOutcomeFields = prediction.Outcomes.Count <= 3
                && prediction.Outcomes.All(o => $"📊 Исход {o.Id}: {o.Name}".Length <= 256);

            var embed = BuildEmbed(prediction, showLocked: false);
            var components = BuildComponents(prediction, showLocked: false);
            var message = await targetChannel.SendMessageAsync(embed: embed, components: components.Build()).ConfigureAwait(false);
            prediction.MessageId = message.Id;

            // ✅ pred-parallelization: добавляем во вложенный словарь по (guildId, channelId).
            // GetOrAdd гарантирует, что для одной гильдии всегда один inner-словарь,
            // и TryAdd на inner-уровне — атомарно. Если TryAdd вернул false (гонка с
            // другим создателем), откатываем отправленное сообщение.
            var byChannel = _active.GetOrAdd(guildId, _ => new ConcurrentDictionary<ulong, ActivePrediction>());
            if (byChannel.TryAdd(channelId, prediction))
            {
                _activeChannels[ChannelKey(guildId, channelId)] = targetChannel;

                if (!_userStats.TryGetValue(guildId, out var guildStats))
                {
                    guildStats = new Dictionary<ulong, UserPredictionStats>();
                    _userStats[guildId] = guildStats;
                }

                if (!guildStats.TryGetValue(creatorId, out var creatorStats))
                {
                    creatorStats = new UserPredictionStats { UserId = creatorId };
                    guildStats[creatorId] = creatorStats;
                }

                creatorStats.CreatedPredictions++;

                var totalPool = prediction.TotalPool;
                var outcomesInfo = string.Join("; ", prediction.Outcomes.Select(o => $"{o.Id}:'{o.Name}'"));

                await LogAsync(
                    $"CREATE guild={guildId}({guildName}) channel={channelId}({channelName}) creator={creatorId}({creatorName}) title='{title}' dur={duration} outcomes=[{outcomesInfo}] pool={totalPool}");

                // Persist prediction state so it survives bot restarts
                _ = Task.Run(async () => await SaveStateAsync().ConfigureAwait(false));
                _ = Task.Run(async () => await SaveStatsAsync().ConfigureAwait(false));

                return (true, string.Empty, prediction);
            }

            try { await message.DeleteAsync().ConfigureAwait(false); } catch { }
            return (false, "Не удалось зарегистрировать прогноз.", null);
        }

        public async Task<(bool ok, string error, ActivePrediction? prediction)> CreateAsync(
            ulong guildId,
            ulong creatorId,
            ulong channelId,
            string title,
            string outcome1Name,
            string outcome2Name,
            TimeSpan duration)
        {
            return await CreateAsync(guildId, creatorId, channelId, title, outcome1Name, outcome2Name, duration, eventId: null).ConfigureAwait(false);
        }

        /// <summary>
        /// ✅ Round 7-C8: перегрузка с привязкой к Discord-событию (ulong channelId).
        /// </summary>
        public async Task<(bool ok, string error, ActivePrediction? prediction)> CreateAsync(
            ulong guildId,
            ulong creatorId,
            ulong channelId,
            string title,
            string outcome1Name,
            string outcome2Name,
            TimeSpan duration,
            ulong? eventId)
        {
            var rawChannel = _client.GetChannel(channelId) ?? _client.GetGuild(guildId)?.GetChannel(channelId);
            var channel = rawChannel as ISocketMessageChannel;
            if (channel == null)
            {
                var rawType = rawChannel?.GetType().FullName ?? "null";
                var rawName = (rawChannel as IChannel)?.Name ?? "(без имени)";
                await LogAsync($"CREATE_FAIL_CHANNEL guild={guildId} creator={creatorId} channelId={channelId} rawType={rawType} rawName='{rawName}'");
                return (false, "Не удалось найти канал для создания прогноза.", null);
            }

            return await CreateAsync(guildId, creatorId, channel, title, outcome1Name, outcome2Name, duration, eventId).ConfigureAwait(false);
        }

        public async Task<(bool ok, string error)> PlaceBetAsync(
                    ulong guildId,
                    ulong userId,
                    int outcomeId,
                    long amount)
                {
                    // ✅ pred-parallelization: legacy-обёртка, ищет ЛЮБОЙ активный прогноз
                    // на гильдии. Новая логика (из Program.Prediction.cs) передаёт channelId
                    // через перегрузку ниже, чтобы ставка точно попала в прогноз нужного канала.
                    var p = GetActive(guildId);
                    if (p == null)
                        return (false, "Активного прогноза нет.");
                    return await PlaceBetAsync(p, userId, outcomeId, amount).ConfigureAwait(false);
                }

        /// <summary>
        /// ✅ pred-parallelization: основной метод ставки. Прогноз передаётся явно,
        /// чтобы ставка шла именно в канал, в котором сидит игрок (а не в любой
        /// активный прогноз на гильдии). Канал-валидация (user.VoiceChannel?.Id ==
        /// p.ChannelId) выполняется на стороне Program.Prediction.cs до вызова.
        /// </summary>
        public async Task<(bool ok, string error)> PlaceBetAsync(
            ActivePrediction p,
            ulong userId,
            int outcomeId,
            long amount)
        {
            await p.Sync.WaitAsync().ConfigureAwait(false);
            try
            {
                if (p.IsLocked)
                    return (false, "Приём ставок уже завершён.");

                if (DateTimeOffset.UtcNow >= p.BetsCloseAtUtc)
                {
                    p.IsLocked = true;
                    await UpdateMessageAsync(p, showLocked: true).ConfigureAwait(false);
                    _ = Task.Run(async () => await SaveStateAsync().ConfigureAwait(false));

                    var betsDuration = DateTimeOffset.UtcNow - p.CreatedAtUtc;
                    await LogAsync($"LOCK guild={p.GuildId} channel={p.ChannelId} title='{p.Title}' bets={p.Bets.Count} duration={betsDuration.TotalSeconds:F0}s totalPool={p.TotalPool}");

                    return (false, "Время приёма ставок истекло.");
                }

                if (amount <= 0)
                    return (false, "Сумма ставки должна быть положительной.");

                var guildId = p.GuildId;

                // Если пользователь уже ставил
                if (p.Bets.TryGetValue(userId, out var existing))
                {
                    // Разрешаем только добавление на тот же исход
                    if (existing.OutcomeId != outcomeId)
                        return (false, "Вы уже сделали ставку на другой исход — изменить её нельзя.");

                    // Тратим дополнительные очки
                    if (!_points.TrySpend(guildId, userId, amount))
                        return (false, "Недостаточно костяшек для этой ставки.");

                    existing.Amount += amount;

                    var outcome = p.GetOutcomeById(outcomeId);
                    if (outcome == null)
                        return (false, "Неверный ID исхода.");

                    outcome.TotalStake += amount;
                    if (!outcome.TopUserId.HasValue || existing.Amount > outcome.TopUserStake)
                    {
                        outcome.TopUserId = userId;
                        outcome.TopUserStake = existing.Amount;
                    }

                    await UpdateMessageAsync(p, showLocked: false).ConfigureAwait(false);

                    _ = Task.Run(async () => await SaveStateAsync().ConfigureAwait(false));

                    await LogAsync($"BET_ADD guild={guildId} channel={p.ChannelId} user={userId} outcome={outcomeId} added={amount} total={existing.Amount}");
                    return (true, string.Empty);
                }

                // Новая ставка
                if (!_points.TrySpend(guildId, userId, amount))
                    return (false, "Недостаточно костяшек для этой ставки.");

                var bet = new PredictionBet
                {
                    UserId = userId,
                    OutcomeId = outcomeId,
                    Amount = amount
                };

                p.Bets[userId] = bet;

                if (!_userStats.TryGetValue(guildId, out var guildStats))
                {
                    guildStats = new Dictionary<ulong, UserPredictionStats>();
                    _userStats[guildId] = guildStats;
                }

                if (!guildStats.TryGetValue(userId, out var userStats))
                {
                    userStats = new UserPredictionStats { UserId = userId };
                    guildStats[userId] = userStats;
                }

                if (p.Bets.Count == 1)
                {
                    userStats.FirstBets++;
                }

                var outcomeNew = p.GetOutcomeById(outcomeId);
                if (outcomeNew == null)
                    return (false, "Неверный ID исхода.");

                outcomeNew.TotalStake += amount;
                if (!outcomeNew.TopUserId.HasValue || amount > outcomeNew.TopUserStake)
                {
                    outcomeNew.TopUserId = userId;
                    outcomeNew.TopUserStake = amount;
                }

                await UpdateMessageAsync(p, showLocked: false).ConfigureAwait(false);

                _ = Task.Run(async () => await SaveStateAsync().ConfigureAwait(false));

                await LogAsync($"BET guild={guildId} channel={p.ChannelId} user={userId} outcome={outcomeId} amount={amount}");
                return (true, string.Empty);
            }
            finally
            {
                p.Sync.Release();
            }
        }

                                public async Task<(bool ok, string error)> ResolveAsync(
                                    ulong guildId,
                                    ulong resolverId,
                                    bool isAdminOverride,
                                    int winningOutcomeId)
                                {
                                    // ✅ pred-parallelization: без channelId — отменяем ЛЮБОЙ
                                    // активный прогноз, где resolverId является создателем или
                                    // передаётся admin-флаг. Используется из info-кнопок и
                                    // auto-cancel (CancelPredictionForEventAsync вызывает
                                    // Resolve-сценарий только когда знает channelId/eventId).
                                    var p = GetActive(guildId);
                                    if (p == null)
                                        return (false, "Активного прогноза нет.");
                                    return await ResolveAsync(p, resolverId, isAdminOverride, winningOutcomeId).ConfigureAwait(false);
                                }

                                /// <summary>
                                /// ✅ pred-parallelization: основной Resolve — принимает конкретный
                                /// прогноз. Команда /prediction resolve должна сначала найти
                                /// прогноз через GetActive(guildId, channelId), затем передать сюда.
                                /// </summary>
                                public async Task<(bool ok, string error)> ResolveAsync(
                                    ActivePrediction p,
                                    ulong resolverId,
                                    bool isAdminOverride,
                                    int winningOutcomeId)
                                {
                                    var guildId = p.GuildId;
                                    if (p.IsResolved)
                                        return (false, "Прогноз уже завершён.");

                                    // Нельзя завершать прогноз до окончания приёма ставок, если нет админского оверрайда
                                    if (!isAdminOverride && DateTimeOffset.UtcNow < p.BetsCloseAtUtc)
                                    {
                                        return (false, "Прогноз ещё идёт: приём ставок не завершён. Дождитесь окончания времени или используйте административный доступ.");
                                    }

                                    if (!(resolverId == p.CreatorId || isAdminOverride))
                                        return (false, "Завершить прогноз может только создатель или администратор.");

                                    p.IsResolved = true;
                                    p.WinningOutcomeId = winningOutcomeId;
            p.IsLocked = true;

            // ✅ Обновлено: получение победившего исхода динамически
            var winningOutcome = p.GetOutcomeById(winningOutcomeId);
            if (winningOutcome == null)
                return (false, $"Неверный ID исхода: {winningOutcomeId}");

            var totalPool = p.TotalPool;
            var winningPool = winningOutcome.TotalStake;

            // Коэффициент показа округляем для пользователя, но выплаты считаем по тому же отображаемому значению.
            double coefRaw = winningPool <= 0 ? 1.0 : (double)totalPool / winningPool;
            double coefRounded = Math.Round(coefRaw, 2, MidpointRounding.AwayFromZero);

            long topWinnerUserId = 0;
            long topWinnerProfit = 0;
            long winnersProfitTotal = 0;
            int winnersCount = 0;

            // Ensure thread-safety when distributing payouts
            await p.Sync.WaitAsync().ConfigureAwait(false);

            // Список для логирования выплат
            var payoutLogs = new List<string>();
            payoutLogs.Add($"=== ВЫПЛАТА ПОИНТОВ ===");
            payoutLogs.Add($"Прогноз: {p.Title}");
            payoutLogs.Add($"Сервер: {guildId}");
            payoutLogs.Add($"Победивший исход: {winningOutcome.Name} (ID={winningOutcomeId})");
            payoutLogs.Add($"Коэффициент: {coefRounded:F2}");
            payoutLogs.Add($"Общий пул: {totalPool}");
            payoutLogs.Add($"Дата: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            payoutLogs.Add($"");
            payoutLogs.Add($"ДЕТАЛИ ВЫПЛАТ:");

            try
            {
                foreach (var bet in p.Bets.Values.Where(b => b.OutcomeId == winningOutcomeId))
                {
                    // ✅ ИСПРАВЛЕНО: выигрыш = ставка × коэффициент, прибыль = выигрыш - ставка
                    var totalReturn = CalculatePayout(bet.Amount, coefRounded);
                    var profit = totalReturn - bet.Amount; // Чистая прибыль (без ставки)

                    _points.Add(guildId, bet.UserId, totalReturn);

                    winnersProfitTotal += profit;
                    winnersCount++;

                    if (profit > topWinnerProfit)
                    {
                        topWinnerProfit = profit;
                        topWinnerUserId = (long)bet.UserId;
                    }

                    // Логируем каждую выплату
                    payoutLogs.Add($"  UserID {bet.UserId}: ставка {bet.Amount}, прибыль {profit}, итого {totalReturn}");
                }

                payoutLogs.Add($"");
                payoutLogs.Add($"ИТОГО:");
                payoutLogs.Add($"  Победителей: {winnersCount}");
                payoutLogs.Add($"  Общая прибыль: {winnersProfitTotal}");
                payoutLogs.Add($"  Топовый участник: UserID {topWinnerUserId}, прибыль {topWinnerProfit}");
                payoutLogs.Add($"======================");

                // Записываем в отдельный файл
                await LogPayoutsAsync(payoutLogs);
            }
            finally
            {
                p.Sync.Release();
            }

            var othersProfit = winnersProfitTotal - topWinnerProfit;
            var othersCount = Math.Max(0, winnersCount - (topWinnerUserId != 0 ? 1 : 0));

            // Удаляем старое сообщение и публикуем новое с результатом
            try
            {
                var channel = GetActiveMessageChannel(p);
                if (channel != null)
                {
                    if (p.MessageId != 0)
                    {
                        try
                        {
                            var msg = await channel.GetMessageAsync(p.MessageId).ConfigureAwait(false);
                            if (msg != null)
                                await msg.DeleteAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            await PredictionErrorLogger.LogAsync("ResolveAsync:DeleteMessage", ex, $"guild={p.GuildId} channel={p.ChannelId} message={p.MessageId}").ConfigureAwait(false);
                        }
                    }

                    var resultEmbed = BuildResultEmbed(
                        p,
                        winningOutcome,
                        coefRounded,
                        totalPool,
                        topWinnerUserId == 0 ? (ulong?)null : (ulong)topWinnerUserId,
                        topWinnerProfit,
                        othersProfit,
                        othersCount);
                    await channel.SendMessageAsync(embed: resultEmbed).ConfigureAwait(false);

                    // ✅ НОВОЕ: Обновляем статистику и достижения
                    try
                    {
                        await UpdateUserStatsAfterResolution(guildId, p, winningOutcomeId, coefRounded).ConfigureAwait(false);
                        await UpdateAchievementsAsync(guildId, p).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await PredictionErrorLogger.LogAsync("ResolveAsync:UpdateStats", ex, $"guild={guildId}").ConfigureAwait(false);
                    }

                    // ✅ НОВОЕ: Отправляем сообщение о достижениях (после небольшой задержки для обновления статистики)
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        try
                        {
                            var achievementsEmbed = BuildAchievementsEmbed(guildId, p.Title);
                            if (achievementsEmbed != null)
                            {
                                await channel.SendMessageAsync(embed: achievementsEmbed).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            await PredictionErrorLogger.LogAsync("ResolveAsync:AchievementsPost", ex, $"guild={guildId}").ConfigureAwait(false);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("ResolveAsync:ResultPost", ex, $"guild={guildId} resolver={resolverId} win={winningOutcomeId}").ConfigureAwait(false);
            }

            // ✅ НОВОЕ: Добавляем в историю
            await AddToHistoryAsync(p, winningOutcomeId, wasCancelled: false);

            // ✅ pred-parallelization: удаляем из вложенного словаря по (guildId, channelId).
            RemovePrediction(p);
            _ = Task.Run(async () => await SaveStateAsync().ConfigureAwait(false));

            // ✅ R6 fix: уведомляем владельца (Program.cs) о завершении прогноза,
            // чтобы тот почистил _pendingBetUi для этой гильдии.
            try { PredictionResolved?.Invoke(guildId); } catch { }

            // ✅ УЛУЧШЕНО: Логируем с информацией о ставках и победителях
            var totalBets = p.Bets.Count;
            var winningBets = p.Bets.Values.Count(b => b.OutcomeId == winningOutcomeId);
            var losingBets = totalBets - winningBets;
            await LogAsync($"RESOLVE guild={guildId} resolver={resolverId} win={winningOutcomeId} coef={coefRounded:F2} totalBets={totalBets} winners={winningBets} losers={losingBets}");
            return (true, string.Empty);
        }

        public async Task<(bool ok, string error)> CancelAsync(
            ulong guildId,
            ulong resolverId,
            bool isAdminOverride,
            string? cancelReason = null)
        {
            // ✅ pred-parallelization: legacy-обёртка. Если есть только один активный
            // прогноз — отменяет его. Если несколько — отменяет первый, на котором
            // resolverId является создателем, либо ничего (нужно указывать channelId).
            var p = GetActive(guildId);
            if (p == null)
                return (false, "Активного прогноза нет.");

            // Если resolver не админ и не создатель — пробуем найти «свой» прогноз.
            if (!isAdminOverride && resolverId != p.CreatorId)
            {
                foreach (var candidate in GetAllActive(guildId))
                {
                    if (candidate.CreatorId == resolverId)
                    {
                        p = candidate;
                        break;
                    }
                }
                if (!isAdminOverride && resolverId != p.CreatorId)
                {
                    return (false, "Отменить прогноз может только создатель или администратор.");
                }
            }

            return await CancelAsync(p, resolverId, isAdminOverride, cancelReason).ConfigureAwait(false);
        }

        /// <summary>
        /// ✅ pred-parallelization: основной метод отмены. Принимает конкретный
        /// ActivePrediction (по channelId). Используется из auto-cancel при
        /// завершении/отмене события, а также из /prediction cancel после нахождения
        /// прогноза по каналу.
        /// </summary>
        public async Task<(bool ok, string error)> CancelAsync(
            ActivePrediction p,
            ulong resolverId,
            bool isAdminOverride,
            string? cancelReason = null)
        {
            var guildId = p.GuildId;
            if (p.IsResolved)
                return (false, "Прогноз уже завершён.");

            if (!(resolverId == p.CreatorId || isAdminOverride))
                return (false, "Отменить прогноз может только создатель или администратор.");

            // Refunds - perform under lock
            await p.Sync.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var bet in p.Bets.Values)
                {
                    _points.Add(guildId, bet.UserId, bet.Amount);
                }
            }
            finally
            {
                p.Sync.Release();
            }

            try
            {
                var channel = GetActiveMessageChannel(p);
                if (channel != null)
                {
                    if (p.MessageId != 0)
                    {
                        try
                        {
                            var msg = await channel.GetMessageAsync(p.MessageId).ConfigureAwait(false);
                            if (msg != null)
                                await msg.DeleteAsync().ConfigureAwait(false);
                        }
                        catch { }
                    }

                    var cancelEmbed = BuildCancelEmbed(p, cancelReason);
                    await channel.SendMessageAsync(embed: cancelEmbed).ConfigureAwait(false);
                }
            }
            catch { }

            await AddToHistoryAsync(p, winningOutcomeId: null, wasCancelled: true);

            // ✅ pred-parallelization: удаляем из вложенного словаря по (guildId, channelId).
            // Если внутренний словарь стал пустым — удаляем и его (чтобы память не утекала).
            RemovePrediction(p);

            // ✅ R6 fix: уведомляем владельца (Program.cs) об отмене прогноза,
            // чтобы тот почистил _pendingBetUi для этой гильдии.
            try { PredictionCancelled?.Invoke(guildId); } catch { }

            var totalBets = p.Bets.Count;
            var totalAmount = p.Bets.Values.Sum(b => b.Amount);
            await LogAsync($"CANCEL guild={guildId} channel={p.ChannelId} resolver={resolverId} totalBets={totalBets} refundedAmount={totalAmount} reason='{cancelReason ?? "manual"}'");
            return (true, string.Empty);
        }

        /// <summary>
        /// ✅ pred-parallelization: удаление прогноза из всех внутренних словарей.
        /// Вызывается из ResolveAsync/CancelAsync/AutoCancelRestoredPredictionAsync.
        /// </summary>
        private void RemovePrediction(ActivePrediction p)
        {
            if (_active.TryGetValue(p.GuildId, out var byChannel) && byChannel != null)
            {
                byChannel.TryRemove(p.ChannelId, out _);
                if (byChannel.IsEmpty)
                {
                    _active.TryRemove(p.GuildId, out _);
                }
            }
            _activeChannels.TryRemove(ChannelKey(p.GuildId, p.ChannelId), out _);
        }

        /// <summary>
        /// ✅ Round 7-C8: отменить прогноз, привязанный к конкретному Discord-событию.
        /// Используется в CleanupStaleSessionsAsync / CleanupStaleSessionByEventAsync
        /// (когда событие Completed/Cancelled на стороне Discord), и из Program.cs
        /// после финализации сессии по событию. Если прогноз не привязан к этому
        /// событию или уже завершён — выходим тихо.
        /// </summary>
        public async Task<(bool ok, string error)> CancelPredictionForEventAsync(
            ulong guildId,
            ulong eventId,
            string cancelReason)
        {
            // ✅ pred-parallelization: ищем прогноз по eventId среди ВСЕХ активных
            // прогнозов на гильдии (теперь их может быть несколько — по одному
            // на каждый голосовой канал). Старая логика с _active[guildId] смотрела
            // только один прогноз и не учитывала остальные.
            var candidates = GetAllActive(guildId);
            ActivePrediction? target = null;
            foreach (var cand in candidates)
            {
                if (cand.IsResolved) continue;
                if (cand.EventId.HasValue && cand.EventId.Value != eventId) continue;
                target = cand;
                break;
            }
            if (target == null)
                return (true, string.Empty); // нет подходящего прогноза — выходим тихо

            await LogAsync($"PRED_CANCEL_FOR_EVENT guild={guildId} eventId={eventId} channel={target.ChannelId} reason='{cancelReason}'");
            // isAdminOverride=true: событие завершилось системно (не вручную), проверка
            // создателя/adмина здесь неуместна. Передаём конкретный прогноз (target),
            // а не ищем его заново через GetActive(guildId) (который вернёт первый).
            return await CancelAsync(target, resolverId: 0, isAdminOverride: true, cancelReason: cancelReason).ConfigureAwait(false);
        }

        private Embed BuildCancelEmbed(ActivePrediction p, string? cancelReason = null)
        {
            var totalRefund = p.Bets.Values.Sum(b => b.Amount);
            var description = string.IsNullOrWhiteSpace(cancelReason)
                ? "Все исходы не сыграли. Все ставки возвращены участникам в полном объёме."
                : cancelReason;

            return new EmbedBuilder()
                .WithTitle($"Прогноз отменён: {p.Title}")
                .WithDescription(description)
                .AddField("Возвращено костяшек", totalRefund.ToString(), false)
                .WithColor(Color.DarkGrey)
                .Build();
        }

        private Embed BuildEmbed(ActivePrediction p, bool showLocked, bool botOffline = false)
        {
            var builder = new EmbedBuilder()
                .WithTitle($"🎯 Прогноз: {p.Title}")
                .WithColor(botOffline ? Color.Red : (showLocked ? Color.Orange : Color.Blue));

            // ✅ БАГ 6: Показываем предупреждение если бот offline
            if (botOffline)
            {
                builder.WithDescription("⚠️ **БОТ НЕАКТИВЕН** — таймер может отставать");
            }

            // ✅ Bug 5: после рестарта показываем пользователю, что окно приёма ставок
            // было автоматически сдвинуто из-за offline-периода.
            if (!showLocked && !botOffline && p.LastOfflineDurationMinutes.HasValue && p.LastOfflineDurationMinutes.Value > 0)
            {
                var minutes = p.LastOfflineDurationMinutes.Value;
                var minsText = minutes >= 1
                    ? $"{Math.Round(minutes)} мин"
                    : $"{Math.Round(minutes * 60)} сек";
                builder.AddField("⏰ Сдвиг таймера", $"Бот был неактивен ~{minsText}, приём ставок продлён на это время.", false);
            }

            // ✅ Bug 6: текущее состояние бота в embed'е (одна строка: последний переход).
            // Полный лог хранится в p.OfflineEvents и доступен в !history.
            if (p.OfflineEvents != null && p.OfflineEvents.Count > 0)
            {
                var last = p.OfflineEvents
                    .OrderBy(e => e.AtUtc)
                    .LastOrDefault();
                if (last != null)
                {
                    string icon = last.Kind == OfflineEventKind.Disconnected
                        ? (string.Equals(last.Severity, "restart", StringComparison.Ordinal) ? "🔄" : "⛔")
                        : "✅";
                    var local = last.AtUtc.ToLocalTime();
                    var suffix = last.Kind == OfflineEventKind.Disconnected
                        ? (string.Equals(last.Severity, "restart", StringComparison.Ordinal) ? "реконнект" : "offline")
                        : "online";
                    builder.AddField("🛰️ Состояние бота", $"{icon} {local:HH:mm:ss} — {suffix}", false);
                }
            }

            var totalPool = p.TotalPool;

            // ✅ ОБНОВЛЕНО: Прогресс-бары и коэффициенты
            foreach (var outcome in p.Outcomes)
            {
                var field = new StringBuilder();

                // Процент от общего банка
                var percentage = totalPool > 0 ? (double)outcome.TotalStake / totalPool * 100 : 0;

                // Прогресс-бар (20 блоков = 100%)
                var filledBlocks = (int)(percentage / 5);
                var emptyBlocks = 20 - filledBlocks;
                var progressBar = new string('█', Math.Max(0, filledBlocks)) + new string('░', Math.Max(0, emptyBlocks));

                // Количество ставок на этот исход
                var betCount = p.Bets.Values.Count(b => b.OutcomeId == outcome.Id);

                field.AppendLine($"{progressBar} {percentage:F1}%");
                field.AppendLine($"💰 {outcome.TotalStake:N0} костяшек ({betCount} ставок)");

                var coef = p.GetCoefficient(outcome.Id);
                field.AppendLine($"📈 Коэффициент: **{coef:F2}x**");
                field.AppendLine($"└─ На 100 → вернётся {(100 * coef):N0}");

                if (outcome.TopUserId.HasValue)
                {
                    field.AppendLine($"🏆 Топ: <@{outcome.TopUserId}> — {outcome.TopUserStake:N0}");
                }

                builder.AddField($"📊 Исход {outcome.Id}: {outcome.Name}", field.ToString(), inline: p.UseInlineOutcomeFields);
            }

            builder.AddField("💎 Общий банк", $"{totalPool:N0} костяшек", false);

            // Время окончания показываем только пока приём ставок открыт
            if (!showLocked && TryGetMoscowTime(p.BetsCloseAtUtc.UtcDateTime, out var mskTime))
            {
                var left = p.BetsCloseAtUtc - DateTimeOffset.UtcNow;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                var leftSeconds = (int)Math.Ceiling(left.TotalSeconds);
                var leftStr = leftSeconds <= 0
                    ? "0с"
                    : leftSeconds < 1
                        ? "<1с"
                        : leftSeconds >= 3600
                            ? $"{leftSeconds / 3600}ч {(leftSeconds % 3600) / 60}м {leftSeconds % 60}с"
                            : leftSeconds >= 60
                                ? $"{leftSeconds / 60}м {leftSeconds % 60}с"
                                : $"{leftSeconds}с";

                builder.AddField("⏱️ До закрытия", leftStr, true);
                builder.AddField("📅 Закрытие", $"{mskTime:HH:mm} МСК", true);
            }

            builder.WithFooter(
                showLocked ? "⏸️ Приём ставок завершён — ожидание результата" :
                (botOffline && !showLocked) ? "⚠️ Бот неактивен — таймер может отставать" :
                botOffline && showLocked ? "⚠️ Бот неактивен" :
                "💰 Ставьте костяшки до указанного времени");

            return builder.Build();
        }

        private ComponentBuilder BuildComponents(ActivePrediction p, bool showLocked)
        {
            var mb = new ComponentBuilder();

            if (!p.IsLocked && !p.IsResolved)
            {
                            // Пока приём ставок открыт: кнопки сделать ставку и отменить.
                            // ✅ pred-parallelization: передаём ChannelId в customId, чтобы обработчики могли
                            // найти именно этот прогноз во вложенном словаре (а не первый попавшийся на гильдии).
                            mb.WithButton("Сделать ставку", customId: $"pred_bet:{p.GuildId}:{p.ChannelId}", style: ButtonStyle.Primary);
                            mb.WithButton("Отменить прогноз", customId: $"pred_cancel:{p.GuildId}:{p.ChannelId}", style: ButtonStyle.Danger);
                        }
                        else if (p.IsLocked && !p.IsResolved)
                        {
                            // ✅ Обновлено: динамические кнопки для всех исходов
                            foreach (var outcome in p.Outcomes.Take(5)) // Discord позволяет макс 5 кнопок в ряду
                            {
                                mb.WithButton(
                                    $"Выбрать: {outcome.Name}", 
                                    customId: $"pred_resolve:{p.GuildId}:{p.ChannelId}:{outcome.Id}", 
                                    style: ButtonStyle.Success);
                            }

                            mb.WithButton("Отменить прогноз", customId: $"pred_cancel:{p.GuildId}:{p.ChannelId}", style: ButtonStyle.Danger);
                        }

            return mb;
        }

        private Embed BuildResultEmbed(
            ActivePrediction p,
            PredictionOutcome winningOutcome,
            double coef,
            long totalPool,
            ulong? topWinnerUserId,
            long topWinnerProfit,
            long othersProfit,
            int othersCount)
        {
            var builder = new EmbedBuilder()
                .WithTitle($"Результат прогноза: {p.Title}")
                .WithColor(Color.Green);

            builder.AddField("Победивший исход", winningOutcome.Name, false);
            builder.AddField("Ставки на победивший исход", winningOutcome.TotalStake.ToString(), true);

            // ✅ Обновлено: показываем все проигравшие исходы
            var losingOutcomes = p.Outcomes.Where(o => o.Id != winningOutcome.Id).ToList();
            if (losingOutcomes.Count == 1)
            {
                builder.AddField("Ставки на другой исход", losingOutcomes[0].TotalStake.ToString(), true);
            }
            else if (losingOutcomes.Count > 1)
            {
                var losingStakes = string.Join(", ", losingOutcomes.Select(o => $"{o.Name}: {o.TotalStake}"));
                builder.AddField("Ставки на проигравшие исходы", losingStakes, false);
            }

            builder.AddField("Общий пул", totalPool.ToString(), false);
            builder.AddField("Коэффициент", Math.Max(0, coef).ToString("F2"), false);

            // Всегда показываем информацию о выигрыше, даже если победитель один
            if (topWinnerUserId.HasValue && topWinnerProfit > 0)
            {
                var topWinnerBet = p.Bets.Values.FirstOrDefault(b => b.UserId == (ulong)topWinnerUserId && b.OutcomeId == winningOutcome.Id);
                var topWinnerReturn = topWinnerBet != null ? CalculatePayout(topWinnerBet.Amount, coef) : 0;

                builder.AddField("🏆 Топ выигрыш", 
                    $"<@{topWinnerUserId}>:\n" +
                    $"  💰 Ставка: {topWinnerBet?.Amount ?? 0:N0}\n" +
                    $"  📈 Возврат: {topWinnerReturn:N0} ({coef:F2}x)\n" +
                    $"  💎 Прибыль: **+{topWinnerProfit:N0}**", 
                    false);

                // Показываем остальных только если их больше одного
                if (othersCount > 0 && othersProfit > 0)
                {
                    builder.AddField("Остальные победители", $"Ещё {othersCount} участников заработали {othersProfit:N0} костяшек прибыли", false);
                }
            }
            else if (winningOutcome.TotalStake > 0)
            {
                // Если нет топового участника, но были ставки - показываем общую сумму
                var totalWinnings = CalculatePayout(winningOutcome.TotalStake, coef);
                var totalProfit = totalWinnings - winningOutcome.TotalStake;
                builder.AddField("Выплаты победителям", 
                    $"Всего возвращено: {totalWinnings:N0} костяшек\n" +
                    $"Общая прибыль: **+{totalProfit:N0}**", 
                    false);
            }

            return builder.Build();
        }

        private static long CalculatePayout(long amount, double coefficient)
        {
            return (long)Math.Round(amount * coefficient, MidpointRounding.AwayFromZero);
        }

        private async Task UpdateMessageAsync(ActivePrediction p, bool showLocked, bool botOffline = false)
        {
            try
            {
                var channel = GetActiveMessageChannel(p);
                if (channel == null || p.MessageId == 0)
                    return;

                var msg = await channel.GetMessageAsync(p.MessageId).ConfigureAwait(false) as IUserMessage;
                if (msg == null)
                    return;

                var embed = BuildEmbed(p, showLocked, botOffline);
                // Build components only if prediction is not resolved/cancelled
                MessageComponent? comps = null;
                if (!p.IsResolved && !botOffline) // ✅ Отключаем кнопки при offline
                {
                    var cb = BuildComponents(p, showLocked);
                    comps = cb?.Build();
                }

                await msg.ModifyAsync(props =>
                {
                    props.Embed = embed;
                    props.Components = comps;
                }).ConfigureAwait(false);
            }
            catch
            {
                // лог ошибок ниже, чтобы не спамить
            }
        }

        private ISocketMessageChannel? GetActiveMessageChannel(ActivePrediction p)
        {
            // ✅ pred-parallelization: ключ теперь составной — (guildId, channelId).
            if (_activeChannels.TryGetValue(ChannelKey(p.GuildId, p.ChannelId), out var activeChannel) && activeChannel != null)
                return activeChannel;

            return _client.GetChannel(p.ChannelId) as ISocketMessageChannel
                ?? _client.GetGuild(p.GuildId)?.GetChannel(p.ChannelId) as ISocketMessageChannel;
        }

        private async Task MonitorLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var delaySeconds = 10;
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var p in SnapshotAllActive())
                    {
                        try
                        {
                            if (!p.IsLocked)
                            {
                                var toClose = p.BetsCloseAtUtc - now;
                                if (toClose <= TimeSpan.FromSeconds(15) && toClose > TimeSpan.Zero)
                                    delaySeconds = 1;
                            }

                            if (!p.IsLocked && now >= p.BetsCloseAtUtc)
                            {
                                p.IsLocked = true;
                                await UpdateMessageAsync(p, showLocked: true).ConfigureAwait(false);
                                _ = Task.Run(async () => await SaveStateAsync().ConfigureAwait(false));

                                // ✅ УЛУЧШЕНО: Расширенное логирование закрытия приёма ставок
                                var betsDuration = now - p.CreatedAtUtc;
                                await LogAsync($"LOCK guild={p.GuildId} title='{p.Title}' bets={p.Bets.Count} duration={betsDuration.TotalSeconds:F0}s totalPool={p.TotalPool}");
                            }
                            else if (!p.IsResolved)
                            {
                                // Keep refreshing while active so the countdown stays up-to-date.
                                await UpdateMessageAsync(p, showLocked: p.IsLocked).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            await LogAsync($"MONITOR_ERROR guild={p.GuildId} msg={p.MessageId} err='{ex.Message}'").ConfigureAwait(false);
                        }
                    }
                }
                catch
                {
                    // игнорируем ошибки цикла
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private Task LogAsync(string message)
        {
            BotLogger.Info(LogCategory.Predict, message);
            return Task.CompletedTask;
        }

        private Task LogPayoutsAsync(List<string> payoutLines)
        {
            foreach (var line in payoutLines)
                BotLogger.Info(LogCategory.Predict, line);
            return Task.CompletedTask;
        }

        private static bool TryGetMoscowTime(DateTime utc, out DateTime msk)
                    => MoscowTime.TryConvertFromUtc(utc, out msk);

        // ==================== ИСТОРИЯ ПРОГНОЗОВ ====================

        private async Task LoadHistoryAsync()
        {
            await _historyGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(_historyFilePath))
                {
                    _history = new PredictionHistoryStore();
                    return;
                }

                var json = await File.ReadAllTextAsync(_historyFilePath).ConfigureAwait(false);
                _history = System.Text.Json.JsonSerializer.Deserialize<PredictionHistoryStore>(json) ?? new();
                _historyEntriesLoaded = _history.History.Sum(kv => kv.Value.Count);
                // ✅ Round 7-C5: не дублируем в Predict.log — достаточно буфера
                // для ЭТАП 3/4, где пользователь увидит "HISTORY_LOADED entries=N".
                AppendRestoreReport($"HISTORY_LOADED entries={_historyEntriesLoaded}");
            }
            catch (Exception ex)
            {
                await LogAsync($"HISTORY_LOAD_ERROR: {ex.Message}");
                _history = new PredictionHistoryStore();
            }
            finally
            {
                _historyGate.Release();
            }
        }

        private async Task SaveHistoryAsync()
        {
            await _historyGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(_history, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        // ✅ R6 fix: атомарная запись через SafeJsonIO, как SaveStateAsync —
                        // при падении процесса в момент сереализации файл не будет обрезан.
                        await SafeJsonIO.WriteAtomicAsync(_historyFilePath, json).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await LogAsync($"HISTORY_SAVE_ERROR: {ex.Message}");
                    }
                    finally
                    {
                        _historyGate.Release();
                    }
                }

        private async Task AddToHistoryAsync(ActivePrediction pred, int? winningOutcomeId, bool wasCancelled)
        {
            try
            {
                if (!_history.History.ContainsKey(pred.GuildId))
                {
                    _history.History[pred.GuildId] = new List<PredictionHistoryEntry>();
                }

                var entry = new PredictionHistoryEntry
                {
                    GuildId = pred.GuildId,
                    Title = pred.Title,
                    StartTime = pred.CreatedAtUtc.DateTime,
                    EndTime = DateTime.UtcNow,
                    Outcomes = pred.Outcomes,
                    WinningOutcomeId = winningOutcomeId,
                    WinningOutcomeName = winningOutcomeId.HasValue ? pred.GetOutcomeById(winningOutcomeId.Value)?.Name : null,
                    TotalPool = pred.Bets.Values.Sum(b => b.Amount),
                    WasCancelled = wasCancelled,
                    CreatorId = pred.CreatorId,
                    Bets = new List<BetResult>()
                };

                // Рассчитываем результаты ставок
                foreach (var bet in pred.Bets.Values)
                {
                    long payout = 0;
                    bool won = false;
                    double coefficient = 1.0;
                    var outcomeCoefficient = pred.GetCoefficient(bet.OutcomeId);

                    if (wasCancelled)
                    {
                        payout = bet.Amount; // Возврат
                        coefficient = outcomeCoefficient;
                    }
                    else if (winningOutcomeId.HasValue && bet.OutcomeId == winningOutcomeId.Value)
                    {
                        var outcome = pred.GetOutcomeById(bet.OutcomeId);
                        if (outcome != null)
                        {
                            coefficient = pred.GetCoefficient(bet.OutcomeId);
                            payout = CalculatePayout(bet.Amount, coefficient);
                            won = true;
                        }
                    }

                    entry.Bets.Add(new BetResult
                    {
                        UserId = bet.UserId,
                        OutcomeId = bet.OutcomeId,
                        Amount = bet.Amount,
                        Payout = payout,
                        Won = won,
                        Coefficient = coefficient,
                        WasFavorite = outcomeCoefficient < 2,
                        WasUnderdog = outcomeCoefficient > 10
                    });
                }

                // ✅ ИСПРАВЛЕНО: TotalPayout = чистый выигрыш (прибыль без возврата ставки)
                entry.TotalPayout = entry.Bets.Where(b => b.Won).Sum(b => b.Payout - b.Amount);

                // Добавляем в начало списка (новые сверху)
                _history.History[pred.GuildId].Insert(0, entry);

                // Ограничиваем размер истории
                if (_history.History[pred.GuildId].Count > PredictionHistoryStore.MaxHistoryPerGuild)
                {
                    _history.History[pred.GuildId].RemoveAt(_history.History[pred.GuildId].Count - 1);
                }

                await SaveHistoryAsync();

                await LogAsync($"HISTORY_ADDED guild={pred.GuildId} title='{pred.Title}' cancelled={wasCancelled}");
            }
            catch (Exception ex)
            {
                await LogAsync($"HISTORY_ADD_ERROR: {ex.Message}");
            }
        }

        public List<PredictionHistoryEntry> GetHistory(ulong guildId, int page = 0, int pageSize = 10)
        {
            if (!_history.History.TryGetValue(guildId, out var entries))
                return new List<PredictionHistoryEntry>();

            return entries.Skip(page * pageSize).Take(pageSize).ToList();
        }

        public int GetHistoryPageCount(ulong guildId, int pageSize = 10)
        {
            if (!_history.History.TryGetValue(guildId, out var entries))
                return 0;

            return (int)Math.Ceiling((double)entries.Count / pageSize);
        }

        // ✅ НОВОЕ: Загрузка/сохранение статистики пользователей
        private async Task LoadStatsAsync()
        {
            await _statsGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(_statsFilePath))
                    return;

                var json = await File.ReadAllTextAsync(_statsFilePath).ConfigureAwait(false);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<ulong, Dictionary<ulong, UserPredictionStats>>>(json);
                if (loaded != null)
                {
                    _userStats.Clear();
                    foreach (var kvp in loaded)
                        _userStats[kvp.Key] = kvp.Value;
                }
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("LoadStatsAsync", ex, _statsFilePath).ConfigureAwait(false);
            }
            finally
            {
                _statsGate.Release();
            }
        }

        private async Task LoadAchievementsAsync()
        {
            await _achievementsGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(_achievementsFilePath))
                    return;

                var json = await File.ReadAllTextAsync(_achievementsFilePath).ConfigureAwait(false);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<ulong, List<UserAchievement>>>(json);
                if (loaded != null)
                {
                    _userAchievements.Clear();
                    foreach (var kvp in loaded)
                    {
                        _userAchievements[kvp.Key] = kvp.Value
                            .Where(a => AchievementDefinitions.All.ContainsKey(a.AchievementId))
                            .ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("LoadAchievementsAsync", ex, _achievementsFilePath).ConfigureAwait(false);
            }
            finally
            {
                _achievementsGate.Release();
            }
        }

        private async Task SaveStatsAsync()
        {
            await _statsGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var snapshot = _userStats.ToDictionary(kv => kv.Key, kv => kv.Value);
                var json = System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        // ✅ R6 fix: атомарная запись — соответствует SaveStateAsync/SaveHistoryAsync.
                        await SafeJsonIO.WriteAtomicAsync(_statsFilePath, json).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await PredictionErrorLogger.LogAsync("SaveStatsAsync", ex, _statsFilePath).ConfigureAwait(false);
                    }
                    finally
                    {
                        _statsGate.Release();
                    }
                }

        // ✅ НОВОЕ: Обновление статистики пользователя после завершения прогноза
        private async Task UpdateUserStatsAfterResolution(
            ulong guildId,
            ActivePrediction prediction,
            int winningOutcomeId,
            double coef)
        {
            if (!_userStats.TryGetValue(guildId, out var guildStats))
            {
                guildStats = new Dictionary<ulong, UserPredictionStats>();
                _userStats[guildId] = guildStats;
            }

            // Обрабатываем всех участников прогноза
            foreach (var bet in prediction.Bets.Values)
            {
                if (!guildStats.TryGetValue(bet.UserId, out var stats))
                {
                    stats = new UserPredictionStats { UserId = bet.UserId };
                    guildStats[bet.UserId] = stats;
                }

                stats.TotalBets++;
                stats.TotalWagered += bet.Amount;
                stats.TotalParticipation++;

                var betCoefficient = prediction.GetCoefficient(bet.OutcomeId);
                var currentUtcDate = DateTime.UtcNow.Date;
                var currentWeekStart = GetWeekStartUtc(currentUtcDate);
                var currentMonthStart = new DateTime(currentUtcDate.Year, currentUtcDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);

                if (stats.WeekStartUtc != currentWeekStart)
                {
                    stats.WeekStartUtc = currentWeekStart;
                    stats.WeekHighCoeffWins = 0;
                }

                if (stats.MonthStartUtc != currentMonthStart)
                {
                    stats.MonthStartUtc = currentMonthStart;
                    stats.MonthProfit = 0;
                }

                if (bet.Amount > stats.LargestBet)
                    stats.LargestBet = bet.Amount;

                if (betCoefficient < 2)
                    stats.FavoriteBets++;

                if (bet.OutcomeId == winningOutcomeId)
                {
                    // Победитель
                    stats.WonBets++;

                    // Прибыль = (ставка * коэфф) - ставка
                    var totalReturn = (long)Math.Round(bet.Amount * coef, MidpointRounding.AwayFromZero);
                    var profit = totalReturn - bet.Amount;

                    stats.TotalWon += profit;
                    stats.NetProfit += profit;
                    stats.MonthProfit += profit;

                    if (profit > stats.HighestSingleWin)
                        stats.HighestSingleWin = profit;

                    if (betCoefficient > stats.HighestCoeffWin)
                        stats.HighestCoeffWin = betCoefficient;

                    if (betCoefficient > 10)
                    {
                        stats.HighCoeffWins++;
                        stats.WeekHighCoeffWins++;
                    }

                    if (betCoefficient > 5)
                        stats.CurrentHighCoeffStreak++;
                    else
                        stats.CurrentHighCoeffStreak = 0;

                    if (betCoefficient < 2)
                        stats.FavoriteWins++;

                    // Обновление серии
                    if (stats.CurrentStreak >= 0)
                        stats.CurrentStreak++;
                    else
                        stats.CurrentStreak = 1;

                    if (stats.CurrentStreak > stats.BestStreak)
                        stats.BestStreak = stats.CurrentStreak;
                }
                else
                {
                    // Проигравший
                    stats.LostBets++;
                    stats.TotalLost += bet.Amount;
                    stats.NetProfit -= bet.Amount;
                    stats.MonthProfit -= bet.Amount;
                    stats.CurrentHighCoeffStreak = 0;

                    // Обновление серии
                    if (stats.CurrentStreak <= 0)
                        stats.CurrentStreak--;
                    else
                        stats.CurrentStreak = -1;
                }
            }

            await SaveStatsAsync().ConfigureAwait(false);
        }

        private static DateTime GetWeekStartUtc(DateTime utcDate)
        {
            var diff = ((int)utcDate.DayOfWeek + 6) % 7;
            return utcDate.AddDays(-diff);
        }

        // ✅ НОВОЕ: Обновление достижений после завершения прогноза
        private async Task UpdateAchievementsAsync(ulong guildId, ActivePrediction prediction)
        {
            if (!_userStats.TryGetValue(guildId, out var guildStats))
                return;

            if (!_userAchievements.TryGetValue(guildId, out var guildAchievements))
            {
                guildAchievements = new List<UserAchievement>();
                _userAchievements[guildId] = guildAchievements;
            }

            var newAchievements = new List<UserAchievement>();

            // Проверяем достижения для каждого участника
            foreach (var bet in prediction.Bets.Values)
            {
                if (!guildStats.TryGetValue(bet.UserId, out var stats))
                    continue;

                var userAchievementIds = guildAchievements
                    .Where(a => a.UserId == bet.UserId)
                    .Select(a => a.AchievementId)
                    .ToHashSet();

                var wonCurrentBet = bet.OutcomeId == prediction.WinningOutcomeId;
                var totalReturn = wonCurrentBet
                    ? (long)Math.Round(bet.Amount * prediction.GetCoefficient(bet.OutcomeId), MidpointRounding.AwayFromZero)
                    : 0;
                var currentProfit = wonCurrentBet ? totalReturn - bet.Amount : 0;
                var currentWinningOutcomePool = prediction.WinningOutcomeId.HasValue
                    ? prediction.GetOutcomeById(prediction.WinningOutcomeId.Value)?.TotalStake ?? 0
                    : 0;
                var currentWinningBetsCount = prediction.WinningOutcomeId.HasValue
                    ? prediction.Bets.Values.Count(b => b.OutcomeId == prediction.WinningOutcomeId.Value)
                    : 0;
                var isSingleWinnerOnOutcome = wonCurrentBet && currentWinningBetsCount == 1;

                // Проверка каждого достижения
                CheckAchievement("newcomer", stats.TotalBets >= 1);
                CheckAchievement("student", stats.TotalParticipation >= 5);
                CheckAchievement("experienced", stats.TotalParticipation >= 25);
                CheckAchievement("versatile", stats.TotalParticipation >= 50);
                CheckAchievement("veteran", stats.TotalParticipation >= 100);

                CheckAchievement("first_blood", stats.TotalWon > 0);
                CheckAchievement("first_place", stats.TotalBets >= 1 && stats.FirstBets >= 1);
                CheckAchievement("rich", wonCurrentBet && currentProfit > 10000);
                CheckAchievement("millionaire", wonCurrentBet && currentProfit > 50000);
                CheckAchievement("banker", stats.NetProfit > 100000);
                CheckAchievement("highroller", bet.Amount > 1000);
                CheckAchievement("whale", bet.Amount > 10000);

                CheckAchievement("accurate", stats.WinRate > 70 && stats.TotalBets >= 20);
                CheckAchievement("lucky", stats.BestStreak >= 10);
                CheckAchievement("on_fire", stats.BestStreak >= 20);
                CheckAchievement("sniper", stats.HighCoeffWins >= 5);
                CheckAchievement("lightning", stats.CurrentHighCoeffStreak >= 3);

                CheckAchievement("risky", wonCurrentBet && prediction.GetCoefficient(bet.OutcomeId) > 10);
                CheckAchievement("madman", wonCurrentBet && prediction.GetCoefficient(bet.OutcomeId) > 20);
                CheckAchievement("legend", wonCurrentBet && prediction.GetCoefficient(bet.OutcomeId) > 50);
                CheckAchievement("hurricane", stats.WeekHighCoeffWins >= 3);

                CheckAchievement("analyst", stats.FavoriteBets >= 10 && stats.FavoriteWins > 0 && ((double)stats.FavoriteWins / stats.FavoriteBets * 100) > 80);
                CheckAchievement("strategist", stats.MonthProfit > 50000);
                CheckAchievement("mathematician", stats.CreatedPredictions >= 10);
                CheckAchievement("prediction_king", stats.CreatedPredictions >= 50);

                CheckAchievement("early_bird", stats.FirstBets >= 10);
                CheckAchievement("loner", isSingleWinnerOnOutcome);
                CheckAchievement("trickster", wonCurrentBet && prediction.TotalPool > 0 && currentWinningOutcomePool * 10 < prediction.TotalPool);
                CheckAchievement("perfectionist", stats.BestStreak >= 50);

                void CheckAchievement(string achievementId, bool condition)
                {
                    if (condition && AchievementDefinitions.All.ContainsKey(achievementId) && !userAchievementIds.Contains(achievementId))
                    {
                        var newAch = new UserAchievement
                        {
                            UserId = bet.UserId,
                            AchievementId = achievementId,
                            EarnedAt = DateTimeOffset.UtcNow
                        };
                        guildAchievements.Add(newAch);
                        newAchievements.Add(newAch);
                        userAchievementIds.Add(achievementId);
                    }
                }
            }

            if (newAchievements.Count > 0)
            {
                await SaveAchievementsAsync().ConfigureAwait(false);
            }
        }

        private async Task SaveAchievementsAsync()
        {
            await _achievementsGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var snapshot = _userAchievements.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
                var json = System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        // ✅ R6 fix: атомарная запись.
                        await SafeJsonIO.WriteAtomicAsync(_achievementsFilePath, json).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await PredictionErrorLogger.LogAsync("SaveAchievementsAsync", ex, _achievementsFilePath).ConfigureAwait(false);
                    }
                    finally
                    {
                        _achievementsGate.Release();
                    }
                }

        // ✅ НОВОЕ: Создаёт Embed с новыми достижениями участников
        private Embed? BuildAchievementsEmbed(ulong guildId, string predictionTitle)
        {
            if (!_userAchievements.TryGetValue(guildId, out var guildAchievements))
                return null;

            // Получаем достижения за последние 10 секунд
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-10);
            var recentAchievements = guildAchievements
                .Where(a => a.EarnedAt >= cutoff)
                .GroupBy(a => a.UserId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(a => a.AchievementId).ToList()
                );

            if (recentAchievements.Count == 0)
                return null;

            var eb = new EmbedBuilder()
                .WithTitle("🎖️ НОВЫЕ ДОСТИЖЕНИЯ!")
                .WithColor(Color.Gold)
                .WithDescription($"Прогноз: **{predictionTitle}**\n\n");

            var sb = new StringBuilder();

            foreach (var kvp in recentAchievements.Take(10)) // Максимум 10 пользователей
            {
                var userId = kvp.Key;
                var achievementIds = kvp.Value;

                sb.AppendLine($"👤 <@{userId}>:");

                foreach (var achievementId in achievementIds)
                {
                    if (AchievementDefinitions.All.TryGetValue(achievementId, out var def))
                    {
                        sb.AppendLine($"  {def.Icon} **{def.Name}**");
                        sb.AppendLine($"     _{def.Description}_");
                        sb.AppendLine();
                    }
                }
            }

            eb.WithDescription(eb.Description + sb.ToString());
            eb.WithFooter($"Всего участников с новыми достижениями: {recentAchievements.Count}");

            return eb.Build();
        }

        // ✅ НОВОЕ: Публичные методы доступа к статистике
        public UserPredictionStats? GetUserStats(ulong guildId, ulong userId)
        {
            if (!_userStats.TryGetValue(guildId, out var guildStats))
                return null;

            return guildStats.TryGetValue(userId, out var stats) ? stats : null;
        }

        public IEnumerable<UserPredictionStats> GetAllUserStats(ulong guildId)
        {
            if (!_userStats.TryGetValue(guildId, out var guildStats))
                return Enumerable.Empty<UserPredictionStats>();

            return guildStats.Values;
        }

        public List<UserAchievement> GetUserAchievements(ulong guildId, ulong userId)
        {
            if (!_userAchievements.TryGetValue(guildId, out var guildAchievements))
                return new List<UserAchievement>();

            return guildAchievements
                .Where(a => a.UserId == userId && AchievementDefinitions.All.ContainsKey(a.AchievementId))
                .ToList();
        }

        public void Shutdown()
                {
                            // Идемпотентная остановка: отменяем монитор и помечаем disposed,
                            // чтобы повторные вызовы (например, при рестарте) были безопасны.
                            if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
                                return;

                            lock (_shutdownLock)
                            {
                                                if (_disposed)
                                                {
                                                    return;
                                                }
                                                try
                                                {
                                                                                    // ✅ Round 7-C6: подписки на Ready/Connected больше не нужны,
                                                                                    // вся логика восстановления переехала в RunStage3RestoreAsync()
                                                                                    // и зовётся синхронно из ЭТАП 3/4.
                                                                                    _client.Disconnected -= OnClientDisconnected;
                                                                                }
                                                                                catch
                                                                                {
                                                                                    // клиент уже отписан — это норма при перезапуске
                                                                                }

                                                // ✅ Bug 5: фиксируем момент ухода в offline на момент штатного shutdown,
                                                // чтобы при следующем старте LoadStateAsync мог сдвинуть BetsCloseAtUtc.
                                                try
                                                {
                                                    var nowUtc = DateTimeOffset.UtcNow;
                                                    foreach (var p in SnapshotAllActive())
                                                    {
                                                        if (!p.IsResolved && !p.IsLocked && !p.BotOfflineAtUtc.HasValue)
                                                        {
                                                            p.BotOfflineAtUtc = nowUtc;
                                                            p.WasBotOfflineOnShutdown = true;
                                                        }
                                                                                // ✅ Bug 6: фиксируем событие "shutdown" в логе, чтобы
                                                                                // можно было отличить полноценный offline от шумных реконнектов.
                                                                                if (!p.IsResolved)
                                                                                {
                                                                                    p.OfflineEvents = p.OfflineEvents ?? new List<OfflineEvent>();
                                                                                    p.OfflineEvents.Add(new OfflineEvent
                                                                                    {
                                                                                        AtUtc = nowUtc,
                                                                                        Kind = OfflineEventKind.Disconnected,
                                                                                        Severity = "offline",
                                                                                        Note = "Shutdown"
                                                                                    });
                                                                                }
                                                                            }
                                                }
                                                catch (Exception ex)
                        {
                                                    try
                                                    {
                                                        PredictionErrorLogger.LogAsync("Shutdown:markOffline", ex, "Failed to mark predictions offline").GetAwaiter().GetResult();
                                                    }
                                                    catch { /* не блокируем shutdown */ }
                                                }

                                                // ✅ Bug 5: сохраняем состояние, чтобы BotOfflineAtUtc дошёл до файла.
                                                try
                                                {
                                                    SaveStateAsync().GetAwaiter().GetResult();
                                                }
                                                catch (Exception ex)
                                                {
                                                    try
                                                    {
                                                        PredictionErrorLogger.LogAsync("Shutdown:saveState", ex, "Failed to persist predictions state on shutdown").GetAwaiter().GetResult();
                                                    }
                                                    catch { /* не блокируем shutdown */ }
                                                }

                                                try { _cts.Cancel(); } catch { }
                                                try { _cts.Dispose(); } catch { }

                                                _disposed = true;
                            }
                        }
                    }
                }







