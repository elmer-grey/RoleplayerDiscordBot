using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace RPBot.EventOps
{
    /// <summary>
    /// Фоновый сервис отложенных действий для анонсов Discord-событий (EventOps).
    ///
    /// <para>На каждое событие (по <c>(GuildId, EventId)</c>) держит до трёх таймеров:</para>
    ///
    /// <list type="bullet">
    ///   <item><b>Cleanup24h</b> — через 24 часа после фактического завершения
    ///   (<c>Completed</c>) или отмены (<c>Cancelled</c>) удаляет анонс из канала
    ///   Discord, все DM-копии подписчикам и Telegram-сообщение, чтобы канал не
    ///   пух старыми анонсами. (задача <c>master-event-announce-cleanup</c>)</item>
    ///
    ///   <item><b>Reminder1h</b> — за 1 час до <c>LastStartTimeUtc</c> рассылает
    ///   подписчикам напоминание: «Через час начнётся событие &lt;название&gt;, не
    ///   пропустите!» — в Discord (DM) и Telegram. (задача
    ///   <c>master-event-reminder-1h</c>)</item>
    ///
    ///   <item><b>DeleteReminder15m</b> — через 15 минут после
    ///   <c>ActualStartTimeUtc</c> удаляет DM-сообщения с напоминанием
    ///   (чтобы не висели вечно). Сам анонс события в канале НЕ трогается.</item>
    /// </list>
    ///
    /// <para>При обновлении события (<c>OnUpdatedAsync</c>) таймеры
    /// перепланируются — например, если мастер перенёс событие на час позже,
    /// reminder1h сдвигается и cleanup24h тоже.</para>
    ///
    /// <para>При старте бота (<see cref="RebuildFromStoreAsync"/>) проходит по всем
    /// записям стора и восстанавливает таймеры. Это значит, что события,
    /// анонсированные ДО деплоя новой логики, автоматически получают отложенное
    /// удаление — ничего делать руками не нужно.</para>
    ///
    /// <para>Время хранится в UTC; все расчёты от <c>DateTimeOffset.UtcNow</c>.</para>
    /// </summary>
    public sealed class EventOpsLifecycleService
    {
        private readonly EventAnnouncementStore _store;
        private readonly Func<DiscordSocketClient> _clientProvider;
                private readonly EventNotificationService _eventNotifications;
                private readonly Func<IReadOnlyDictionary<ulong, ServerConfig>> _serverConfigsProvider;
                private TelegramNotifier? _telegramNotifier;
                private readonly object _telegramNotifierLock = new();

        private readonly ConcurrentDictionary<(ulong guildId, ulong eventId), ScheduledTimers> _timers = new();
        private CancellationTokenSource _shutdownCts = new();

        // 🩹 reminder-send-gate: семафор на (guild,event) — сериализует ВСЕ пути
        // отправки напоминаний (RunReminder1h, rehydrate-past, rebuild catch-up,
        // watchdog-свайп). Исключает параллельный дубликат сообщения, когда
        // reconnect-реридрат стартует ровно в момент отправки свипа/таймера:
        // в сторе записался бы id только одной из двух отправок, вторая осталась
        // бы «виртуальной» — не удалённой через 15 мин после старта события.
        private readonly ConcurrentDictionary<(ulong guildId, ulong eventId), SemaphoreSlim> _sendGates = new();

        // Single-flight guard: запрещает двойной запуск RebuildFromStoreAsync при reconnect/Ready.
        private int _running;

        // 🐕 reminder-watchdog-sweep: периодическая страховка таймеров EventOps (запрос
        // владельца 2026-10-20 «каждые ~30 минут проверять, ушло ли напоминание»).
        // Раз в этот интервал стор проверяется независимо от rehydrate/rebuild:
        // просроченное неотправленное напоминание — досылается, таймеры на будущее —
        // ставятся заново, если слот пуст (Cancel() чистит слоты, живой таймер держит CTS).
        private static readonly TimeSpan ReminderSweepInterval = TimeSpan.FromMinutes(30);

        // Single-flight guard свипа: не более одного живого фонового цикла одновременно.
        private int _sweepRunning;

        public EventOpsLifecycleService(
            EventAnnouncementStore store,
            Func<DiscordSocketClient> clientProvider,
            TelegramNotifier? telegramNotifier,
            EventNotificationService eventNotifications,
            Func<IReadOnlyDictionary<ulong, ServerConfig>> serverConfigsProvider)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
            _telegramNotifier = telegramNotifier;
            _eventNotifications = eventNotifications ?? throw new ArgumentNullException(nameof(eventNotifications));
            _serverConfigsProvider = serverConfigsProvider ?? throw new ArgumentNullException(nameof(serverConfigsProvider));
        }

                    /// <summary>
                    /// Сеттер для TelegramNotifier — синхронно меняет ссылку после реконнекта,
                    /// чтобы reminder/cleanup-публикации не падали на disposed notifier.
                    /// </summary>
                    public void SetTelegramNotifier(TelegramNotifier? notifier)
                    {
                        lock (_telegramNotifierLock)
                        {
                            _telegramNotifier = notifier;
                        }
                    }

        /// <summary>
        /// Запрашивает отмену всех таймеров (для shutdown/OnDisconnected).
        /// Безопасно вызывать многократно.
        /// </summary>
        public void Cancel()
        {
            try { _shutdownCts.Cancel(); } catch { }

            // 🩹 reminder-lost-after-reconnect: Cancel() вызывается не только при
            // shutdown, но и на КАЖДОМ OnDisconnected (плановые gateway-реконнекты
            // Discord — каждые 1–3 часа). Отменённый _shutdownCts без пересоздания
            // навсегда рвал linkedCts у последующих RehydrateTimersAsync/
            // RebuildFromStoreAsync: их цикл разрывался на первой итерации
            // (ct.IsCancellationRequested), сводки выходили со всеми нулями, а
            // reminder1h/deleteReminder15m/cleanup24h после реконнекта никогда
            // не восстанавливались (потеря напоминания 2026-10-20 12:00Z).
            // Пересоздаём чистый токен — будущие вызовы снова рабочие.
            _shutdownCts = new CancellationTokenSource();

                    // ВАЖНО: _shutdownCts сам по себе не связан с per-event CTSs в _timers
                    // (они живут независимыми CancellationTokenSource'ами на каждое
                    // событие). Без явного обхода словаря таймеры продолжали бы
                    // тикать после дисконнекта/рестарта — утечка CTS, дублирующиеся
                    // Task.Delay и потенциально двойные cleanup'ы при повторном Ready
                    // (audit bug #2).
                    foreach (var kv in _timers)
                    {
                        var slot = kv.Value;
                                            // Bug #7: lock на слот — иначе параллельный ScheduleXxx может
                                            // перезаписать наш null после cancel/dispose, оставив «живой» CTS.
                                            lock (slot.Lock)
                                            {
                                                try { slot.Reminder1hCts?.Cancel(); } catch { }
                                                try { slot.Reminder1hCts?.Dispose(); } catch { }
                                                slot.Reminder1hCts = null;
                                                slot.Reminder1hAtUtc = null;

                                                try { slot.DeleteReminder15mCts?.Cancel(); } catch { }
                                                try { slot.DeleteReminder15mCts?.Dispose(); } catch { }
                                                slot.DeleteReminder15mCts = null;

                                                try { slot.Cleanup24hCts?.Cancel(); } catch { }
                                                try { slot.Cleanup24hCts?.Dispose(); } catch { }
                                                slot.Cleanup24hCts = null;
                                            }
                                        }
                                    }

        /// <summary>
        /// Привязка к <see cref="EventOpsOrchestrator"/>. Вызывать один раз после создания.
        /// </summary>
        public void Attach(EventOpsOrchestrator orchestrator)
        {
            if (orchestrator == null) throw new ArgumentNullException(nameof(orchestrator));

            orchestrator.OnCreatedAsync += HandleCreatedAsync;
            orchestrator.OnUpdatedAsync += HandleUpdatedAsync;
            orchestrator.OnStartedAsync += HandleStartedAsync;
            orchestrator.OnCompletedAsync += HandleCompletedAsync;
            orchestrator.OnCancelledAsync += HandleCancelledAsync;
        }

        /// <summary>
        /// Одноразовый backfill CompletedAtUtc/CancelledAtUtc для записей, у которых
        /// это поле null, но LastUpdatedMark == completed/cancelled. Вызывается из
        /// RebuildFromStoreAsync при старте бота.
        ///
        /// Для CancelledAtUtc: если LastUpdatedMark == cancelled, а CancelledAtUtc
        /// пуст — заполняем LastUpdatedAt (он обновляется в AnnounceStatusChangedInternalAsync
        /// в момент observed cancelled).
        ///
        /// Для CompletedAtUtc: если LastUpdatedMark == completed, а CompletedAtUtc
        /// пуст — заполняем LastUpdatedAt. Это «лучшее приближение» момента
        /// завершения: AnnounceStatusChangedInternalAsync пишет LastUpdatedAt
        /// в момент observed completed.
        ///
        /// Если LastUpdatedAt по какой-то причине пуст — fallback на LastUpdatedAtUtc
        /// или DateTimeOffset.UtcNow.
        ///
        /// Backfill идемпотентен: если поле уже заполнено — не трогаем.
        /// </summary>
        private void BackfillMissingTimestamps()
        {
            try
            {
                var entries = _store.GetEntriesSnapshot();
                int backfilledCancelled = 0, backfilledCompleted = 0;
                foreach (var e in entries)
                {
                    var mark = e.LastUpdatedMark;
                    if (string.IsNullOrEmpty(mark)) continue;

                    DateTimeOffset? approx = null;
                    if (e.LastUpdatedAt.HasValue)
                    {
                        approx = new DateTimeOffset(DateTime.SpecifyKind(e.LastUpdatedAt.Value, DateTimeKind.Utc));
                    }
                    else if (e.LastUpdatedAtUtc != default)
                    {
                        approx = e.LastUpdatedAtUtc;
                    }

                    if (!approx.HasValue) continue;

                    if (mark == "cancelled" && !e.CancelledAtUtc.HasValue)
                    {
                        var updated = _store.TryGet(e.GuildId, e.EventId);
                        if (updated != null)
                        {
                            updated.CancelledAtUtc = approx;
                            try { _store.UpdateEntry(updated); backfilledCancelled++; } catch { }
                        }
                    }
                    else if (mark == "completed" && !e.CompletedAtUtc.HasValue)
                    {
                        var updated = _store.TryGet(e.GuildId, e.EventId);
                        if (updated != null)
                        {
                            updated.CompletedAtUtc = approx;
                            try { _store.UpdateEntry(updated); backfilledCompleted++; } catch { }
                        }
                    }
                }

                if (backfilledCancelled > 0 || backfilledCompleted > 0)
                {
                    BotLogger.Info(LogCategory.Discord,
                        $"[EventOpsLifecycle] BackfillMissingTimestamps: cancelled={backfilledCancelled}, completed={backfilledCompleted}");
                }
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Discord,
                    $"[EventOpsLifecycle] BackfillMissingTimestamps error: {ex.GetType().Name}: {ex.Message}");
            }
        }

                            /// <summary>
                            /// Безопасный проход по стору для реконнекта: только установка таймеров.
                            /// Не делает полный resync (для этого — RebuildFromStoreAsync); запускается
                            /// из обработчиков реконнекта в Program.cs и страхует Catch-up после
                            /// OnDisconnected→Cancel(), который убил все per-event CTS.
                            ///
                            /// Сценарий использования (run.log 23.09.2026):
                            /// 21:03:39 Started, поставлен deleteReminder15m через Task.Delay(14min);
                            /// 21:10:24 OnDisconnected → _eventOpsLifecycle.Cancel() → DeleteReminder15mCts
                            ///        отменён, Task.Delay проглотил OperationCanceledException;
                            /// 21:10:30 Клиент восстановился сам — никто не переставляет CTS, потому что
                            ///        HandleStartedAsync уже отработал, Discord OnStarted повторно не придёт.
                            ///
                            /// Поведение по Reminder1hAtUtc:
                            /// • null → ничего не делаем (напоминание уже отправлено или не планировалось);
                            /// • в прошлом — отправляем reminder немедленно через SendReminderAsync;
                            /// • в будущем — ставим Task.Run с Task.Delay на остаток.
                            ///
                            /// Поведение по DeleteReminder15mAtUtc:
                            /// • null → ничего (напоминание уже удалено или не было);
                            /// • в прошлом — DeleteReminderMessagesAsync напрямую;
                            /// • в будущем — Task.Run с Task.Delay на остаток.
                            ///
                            /// Не вызывает RECONCILE/catch-up для completed/cancelled/pending_announce —
                            /// этим занимается RebuildFromStoreAsync. Сейчас метод сфокусирован только
                            /// на двух «живых» таймерах, которые наиболее уязвимы к OnDisconnected.
                            /// </summary>
                            public async Task RehydrateTimersAsync(CancellationToken externalCt = default)
                            {
                                if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                                {
                                    BotLogger.Warn(LogCategory.Discord,
                                        "[EventOpsLifecycle] RehydrateTimersAsync уже выполняется или RebuildFromStoreAsync в процессе — повторный вызов игнорирован");
                                    return;
                                }

                                try
                                {
                                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _shutdownCts.Token);
                                    var ct = linkedCts.Token;
                                    var entries = _store.GetEntriesSnapshot();

                                    int rehydratedReminder = 0, rehydratedDelete = 0, rehydratedCleanup = 0,
                                        sentNowReminder = 0, sentNowDelete = 0, sentNowCleanup = 0;

                                    foreach (var entry in entries)
                                    {
                                        if (ct.IsCancellationRequested) break;

                                        // (1) Reminder1h: если момент в сторе — решаем немедленно/Task.Delay.
                                        if (entry.Reminder1hAtUtc.HasValue)
                                        {
                                            var reminderAt = entry.Reminder1hAtUtc.Value;
                                            var nowUtc = DateTimeOffset.UtcNow;
                                            if (nowUtc >= reminderAt)
                                            {
                                                // Окно уже прошло (бот был оффлайн всю часовую пометку).
                                                // Если reminder ещё не отправлен (Dm ids пуст ИЛИ Announce-id=0) — шлём сразу.
                                                // 🩹 reminder-lead-text: но только если событие ещё НЕ началось —
                                                // напоминание «уже началось» после старта рассылать нельзя
                                                // (кейс 2026-10-20: напоминание 12:00Z не ушло, событие 13:00Z
                                                // стартовало, следующий реконнект шлал бы пост-стартовый reminder).
                                                var eventAlreadyStarted = entry.LastStartTimeUtc.HasValue
                                                                          && nowUtc >= entry.LastStartTimeUtc.Value;
                                                var dmIds = entry.ReminderDmMessageIdsByUserId;
                                                var alreadySent = (dmIds != null && dmIds.Count > 0)
                                                                  || entry.ReminderAnnounceMessageId != 0
                                                                  || entry.ReminderTelegramMessageId != 0;
                                                if (!alreadySent && !eventAlreadyStarted)
                                                {
                                                    BotLogger.Info(LogCategory.Discord,
                                                        $"[EventOpsLifecycle] RehydrateTimers: шлю reminder1h (окно прошло в оффлайне) guild={entry.GuildId} event={entry.EventId} reminderAt={reminderAt:o} now={nowUtc:o}");
                                                    try { await SendReminderAsync(entry); sentNowReminder++; }
                                                    catch (Exception exR)
                                                    {
                                                        BotLogger.Warn(LogCategory.Discord,
                                                            $"[EventOpsLifecycle] RehydrateTimers: SendReminderAsync failed: {exR.Message}");
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                // В будущем — ставим Task.Delay на остаток.
                                                var delay = reminderAt - nowUtc;
                                                var key = (entry.GuildId, entry.EventId);
                                                var slot = _timers.GetOrAdd(key, _ => new ScheduledTimers());
                                                CancellationTokenSource cts;
                                                lock (slot.Lock)
                                                {
                                                    try { slot.Reminder1hCts?.Cancel(); } catch { }
                                                    try { slot.Reminder1hCts?.Dispose(); } catch { }
                                                    cts = new CancellationTokenSource();
                                                    slot.Reminder1hCts = cts;
                                                    slot.Reminder1hAtUtc = reminderAt;
                                                }
                                                _ = Task.Run(() => RunReminder1hAsync(entry, cts.Token, delay), cts.Token);
                                                rehydratedReminder++;
                                                BotLogger.Info(LogCategory.Discord,
                                                    $"[EventOpsLifecycle] RehydrateTimers: переставлен reminder1h guild={entry.GuildId} event={entry.EventId} через {(int)delay.TotalMinutes} мин (at {reminderAt:u})");
                                            }
                                        }

                                        // (2) DeleteReminder15m: тоже — если момент в сторе, решаем немедленно/Task.Delay.
                                        if (entry.DeleteReminder15mAtUtc.HasValue)
                                        {
                                            var deleteAt = entry.DeleteReminder15mAtUtc.Value;
                                            var nowUtc = DateTimeOffset.UtcNow;
                                            if (nowUtc >= deleteAt)
                                            {
                                                // Окно прошло. Если reminder-месседжи ещё есть — дропаем.
                                                var dmIds = entry.ReminderDmMessageIdsByUserId;
                                                var anyReminder = (dmIds != null && dmIds.Count > 0)
                                                                  || entry.ReminderAnnounceMessageId != 0
                                                                  || entry.ReminderTelegramMessageId != 0;
                                                if (anyReminder)
                                                {
                                                    BotLogger.Info(LogCategory.Discord,
                                                        $"[EventOpsLifecycle] RehydrateTimers: чищу reminder-месседжи (окно 15мин прошло в оффлайне) guild={entry.GuildId} event={entry.EventId} deleteAt={deleteAt:o} now={nowUtc:o}");
                                                    try { await DeleteReminderMessagesAsync(entry); sentNowDelete++; }
                                                    catch (Exception exD)
                                                    {
                                                        BotLogger.Warn(LogCategory.Discord,
                                                            $"[EventOpsLifecycle] RehydrateTimers: DeleteReminderMessagesAsync failed: {exD.Message}");
                                                    }
                                                }
                                                else
                                                {
                                                    // Просто зачищаем теперь уже ненужный момент в сторе.
                                                    entry.DeleteReminder15mAtUtc = null;
                                                    try { _store.UpdateEntry(entry); } catch { }
                                                }
                                            }
                                            else
                                            {
                                                var delay = deleteAt - nowUtc;
                                                var key = (entry.GuildId, entry.EventId);
                                                var slot = _timers.GetOrAdd(key, _ => new ScheduledTimers());
                                                CancellationTokenSource cts;
                                                lock (slot.Lock)
                                                {
                                                    try { slot.DeleteReminder15mCts?.Cancel(); } catch { }
                                                    try { slot.DeleteReminder15mCts?.Dispose(); } catch { }
                                                    cts = new CancellationTokenSource();
                                                    slot.DeleteReminder15mCts = cts;
                                                }
                                                _ = Task.Run(() => RunDeleteReminder15mAsync(entry, cts.Token, delay), cts.Token);
                                                rehydratedDelete++;
                                                BotLogger.Info(LogCategory.Discord,
                                                    $"[EventOpsLifecycle] RehydrateTimers: переставлен deleteReminder15m guild={entry.GuildId} event={entry.EventId} через {(int)delay.TotalMinutes} мин (at {deleteAt:u})");
                                            }
                                        }

                                        // (3) Cleanup24h: восстанавливаем ТОЖЕ. До этого фикса
                                        // (Bug evt-cleanup-lost-on-reconnect) блок отсутствовал
                                        // целиком, и при любом gateway-реконнекте cleanup24h-CTS
                                        // отменялся в OnDisconnected, а RehydrateTimers его не
                                        // пересоздавал — cleanup для completed/cancelled/abandoned
                                        // событий тихо умирал, и анонсы висели в сторе и в дашборде
                                        // «вечно», пока не приходил следующий полный рестарт бота.
                                        // Сценарий: cleanup для Берсеша (event=1558114473787523173)
                                        // был запланирован на 16:47:16Z, реконнект в 16:51:19
                                        // убил CTS, RehydrateTimers не восстановил — запись так и
                                        // не очистилась до следующего ежедневного рестарта.
                                        if (entry.CleanupAtUtc.HasValue)
                                        {
                                            var cleanupAt = entry.CleanupAtUtc.Value;
                                            var nowUtc2 = DateTimeOffset.UtcNow;
                                            if (nowUtc2 >= cleanupAt)
                                            {
                                                // Cleanup уже в прошлом (типичный случай: реконнект
                                                // случился ПОСЛЕ момента cleanup). Делаем его сейчас —
                                                // иначе запись будет болтаться в сторе «вечно» до
                                                // следующего полного рестарта, в дашборде будет
                                                // показываться как «зависший» completed-анонс.
                                                BotLogger.Info(LogCategory.Discord,
                                                    $"[EventOpsLifecycle] RehydrateTimers: шлю cleanup24h (момент прошёл в оффлайне) guild={entry.GuildId} event={entry.EventId} cleanupAt={cleanupAt:o} now={nowUtc2:o}");
                                                try { await RunCleanup24hAsync(entry, ct, TimeSpan.Zero); sentNowCleanup++; }
                                                catch (Exception exC)
                                                {
                                                    BotLogger.Warn(LogCategory.Discord,
                                                        $"[EventOpsLifecycle] RehydrateTimers: RunCleanup24hAsync failed: {exC.Message}");
                                                }
                                            }
                                            else
                                            {
                                                // В будущем — ставим Task.Delay на остаток. Тот же
                                                // слот, что и в ScheduleCleanup24h, чтобы
                                                // cancellation был согласованным.
                                                var delay = cleanupAt - nowUtc2;
                                                var key = (entry.GuildId, entry.EventId);
                                                var slot = _timers.GetOrAdd(key, _ => new ScheduledTimers());
                                                CancellationTokenSource cts;
                                                lock (slot.Lock)
                                                {
                                                    try { slot.Cleanup24hCts?.Cancel(); } catch { }
                                                    try { slot.Cleanup24hCts?.Dispose(); } catch { }
                                                    cts = new CancellationTokenSource();
                                                    slot.Cleanup24hCts = cts;
                                                }
                                                _ = Task.Run(() => RunCleanup24hAsync(entry, cts.Token, delay), cts.Token);
                                                rehydratedCleanup++;
                                                BotLogger.Info(LogCategory.Discord,
                                                    $"[EventOpsLifecycle] RehydrateTimers: переставлен cleanup24h guild={entry.GuildId} event={entry.EventId} через {(int)delay.TotalMinutes} мин (at {cleanupAt:u})");
                                            }
                                        }
                                    }

                                    BotLogger.Info(LogCategory.Discord,
                                        $"[EventOpsLifecycle] RehydrateTimers завершено: reminderDelay={rehydratedReminder}, deleteDelay={rehydratedDelete}, cleanupDelay={rehydratedCleanup}, reminderNow={sentNowReminder}, deleteNow={sentNowDelete}, cleanupNow={sentNowCleanup}");

                                    // 🐕 reminder-watchdog-sweep: после rehydrate поддерживаем
                                    // постоянный периодический контроль (умирает при Cancel(),
                                    // перезапускается на следующем RehydrateTimersAsync).
                                    StartReminderSweep();
                                }
                                catch (Exception ex)
                                {
                                    BotLogger.Error(LogCategory.Discord,
                                        $"[EventOpsLifecycle] RehydrateTimers error: {ex.GetType().Name}: {ex.Message}");
                                }
                                finally
                                {
                                    Interlocked.Exchange(ref _running, 0);
                                }
                            }

        /// <summary>
        /// Одноразовый проход по всем записям стора после старта бота.
        /// Восстанавливает таймеры для активных/запланированных событий.
        /// Повторные вызовы (reconnect/Ready) — single-flight, защита от дублей.
        /// </summary>
        public async Task RebuildFromStoreAsync(CancellationToken externalCt = default)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                BotLogger.Warn(LogCategory.Discord,
                    "[EventOpsLifecycle] RebuildFromStoreAsync уже выполняется — повторный вызов игнорирован");
                return;
            }

            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _shutdownCts.Token);
                var ct = linkedCts.Token;

                // Backfill исторических cancelled/completed записей без CompletedAtUtc.
                // До фикса evt-bug3 таких полей не было в схеме, и при status-from-rest
                // для старых completed-событий поле «Завершение» в анонсе рендерилось
                // моментом рестарта бота. Backfill заполняет поле «правдоподобным»
                // значением: для cancelled → CancelledAtUtc ?? LastUpdatedAt,
                // для completed → CompletedAtUtc из LastUpdatedAt (≈ момент,
                // когда бот последний раз обновлял запись — для completed-событий
                // это и есть момент завершения, потому что AnnounceStatusChangedInternalAsync
                // пишет LastUpdatedAt = DateTime.UtcNow).
                BackfillMissingTimestamps();

                var entries = _store.GetEntriesSnapshot();
                BotLogger.Info(LogCategory.Discord,
                    $"[EventOpsLifecycle] RebuildFromStoreAsync старт, записей: {entries.Count}");

                int scheduledCleanup = 0, scheduledReminder = 0, scheduledBackfillCompleted = 0, sentCatchupReminder = 0, sentCatchupReminderDelete = 0, skipped = 0;
                foreach (var entry in entries)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                                            // Финальные/устаревшие статусы — сразу ставим cleanup24h
                                            // и подчищаем reminder-сообщения с прошлой сессии.
                                            //
                                            // pending_announce — наш новый маркер (v2 announcer-fix): Created
                                            // поступил, но Discord-announce не ушёл. На рестарте попробуем
                                            // дослать через ReconcilePendingAnnounce ниже.
                                            // abandoned — пометка v2 prod-cleanup-stuck: бот не видел Started/
                                            // Completed, но LastStartTimeUtc далеко в прошлом. Через 24ч после
                                            // старта запись можно удалять.
                                            var mark = entry.LastUpdatedMark;
                                            if (mark == "completed" || mark == "cancelled" || mark == "abandoned" || mark == "pending_announce")
                                            {
                                                if (ScheduleCleanup24h(entry))
                                                    scheduledCleanup++;

                                                // Подчищаем reminder-сообщения (канал/DM/Telegram), если
                                                // они остались с предыдущей сессии. Типичный сценарий:
                                                // reminder1h был отправлен, потом событие отменили до
                                                // того, как отработал DeleteReminder15m — после рестарта
                                                // reminder-сообщение висит в канале/DM, а timer уже умер.
                                                // DeleteReminderMessagesAsync идемпотентен: если сообщений
                                                // нет или уже удалены — молча выходит.
                                                await DeleteReminderMessagesAsync(entry);

                                                // pending_announce — отдельно пытаемся дослать анонс и
                                                // переотправить reminder/TG/DM с нуля. abandoned —
                                                // очищаем сообщения и уходим.
                                                if (mark == "pending_announce")
                                                {
                                                    await ReconcilePendingAnnounceAsync(entry);
                                                }

                                                continue;
                                            }

                        // Scheduled/Started: ставим reminder1h (если ещё актуально).
                        if (entry.LastStartTimeUtc.HasValue && ScheduleReminder1h(entry))
                            scheduledReminder++;
                        else
                            skipped++;

                                                // 🩹 disconnect-no-rehydrate: catch-up reminder для событий, чьё
                                                // окно reminder (LastStartTimeUtc - 1ч) попало в даунтайм бота.
                                                //
                                                // Сценарий: событие создано (reminder запланирован на T-1ч).
                                                // Бот реконнект/рестартился через 30мин после reminder-окна;
                                                // reminder-таймер отменился в OnDisconnected, новый на рестарте
                                                // уже видит LastStartTimeUtc в прошлом → ScheduleReminder1h
                                                // возвращает false (вы ниже не попадаете), reminder не уходит.
                                                //
                                                // Правило: если now внутри окна [Start-1ч, Start+30мин] и
                                                                                                // reminder ещё не отправлен во ВСЕ каналы — досылаем.
                                                                                                // Раньше использовался WasReminderAlreadySent (OR по трём
                                                                                                // каналам), и если хоть один Reminder*-ID в сторе заполнен —
                                                                                                // catch-up не слал. Это ломало DM-сценарий, когда reminder
                                                                                                // уже ушёл в канал/TG, но DM-подписчикам так и не пришёл
                                                                                                // (ReminderDmMessageIdsByUserId оставался null).
                                                                                                //
                                                                                                // 🔁 per-channel-already-sent-skip (Bug fix): теперь
                                                                                                // catch-up срабатывает, пока reminder не разослан во ВСЕ
                                                                                                // каналы. SendReminderAsync с per-channel-skip доставит
                                                                                                // reminder только в каналы, где его ещё не было — дублей
                                                                                                // не будет.
                                                //
                                                // 🩹 Bug: double-reminder-on-catchup. ScheduleReminder1h выше по коду
                                                // при «опоздали» ставит Task.Run(delay=0) через RunReminder1hAsync,
                                                // и эта ветка ВСЕГДА пишет entry.Reminder1hAtUtc = reminderAt в стор
                                                // (см. строку entry.Reminder1hAtUtc = reminderAt после Task.Run).
                                                // Значит, если мы уже внутри ScheduleReminder1h — в сторе
                                                // Reminder1hAtUtc != null, и catch-up НЕ должен слать. Иначе
                                                // reminder уходит дважды: один раз из Task.Run, второй из catch-up
                                                // (в Telegram и в канал анонса — дубли в обоих каналах). Проверяем
                                                // !entry.Reminder1hAtUtc.HasValue, чтобы catch-up ловил только
                                                // «timer пропал после рестарта/реконнекта» (наш сценарий 23:28).
                                                if (entry.LastStartTimeUtc.HasValue)
                                                {
                                                    var dmAlready = entry.ReminderDmMessageIdsByUserId != null && entry.ReminderDmMessageIdsByUserId.Count > 0;
                                                    var announceAlready = entry.ReminderAnnounceMessageId != 0;
                                                    var tgAlready = entry.ReminderTelegramMessageId != 0 && entry.ReminderTelegramChatId != 0;
                                                    var allChannelsSent = dmAlready && announceAlready && tgAlready;
                                                    var startUtc = entry.LastStartTimeUtc.Value;
                                                    var reminderAt = startUtc.AddHours(-1);
                                                    var nowUtc = DateTimeOffset.UtcNow;
                                                    if (!allChannelsSent
                                                        && !entry.Reminder1hAtUtc.HasValue
                                                        && nowUtc >= reminderAt
                                                        && nowUtc <= startUtc.AddMinutes(30))
                                                    {
                                                            BotLogger.Info(LogCategory.Discord,
                                                                $"[EventOpsLifecycle] RebuildFromStoreAsync: catch-up reminder1h (disconnect-no-rehydrate) guild={entry.GuildId} event={entry.EventId} start={startUtc:o} now={nowUtc:o}");
                                                            try { await SendReminderAsync(entry); sentCatchupReminder++; }
                                                            catch (Exception exR)
                                                            {
                                                                BotLogger.Warn(LogCategory.Discord,
                                                                    $"[EventOpsLifecycle] catch-up reminder failed: {exR.Message}");
                                                            }
                                                        }
                                                }

                                                                                                        // 🩹 disconnect-no-rehydrate (deleteReminder15m): если reminder
                                                                                                        // в сторе ещё висит (DM/канал-announce/Telegram), а момент
                                                                                                        // «start+15мин» уже прошёл — чистим в канале/DM/Telegram напрямую.
                                                                                                        //
                                                                                                        // Сценарий (23.09.2026, run.log):
                                                                                                        // 1) бот онлайн, событие Running, идёт HandleStartedAsync →
                                                                                                        //    удалось зашедулить deleteReminder15m через Task.Delay(14min);
                                                                                                        // 2) Discord WebSocket отвалился (run.log: «Отключение: WebSocket
                                                                                                        //    ошибка» в 21:10:24). Program.OnDisconnected вызвал
                                                                                                        //    _eventOpsLifecycle.Cancel() — ВСЕ per-event CTS, включая
                                                                                                        //    DeleteReminder15mCts, отменены. Task.Delay бросает
                                                                                                        //    OperationCanceledException, который проглатывается штатным
                                                                                                        //    catch;
                                                                                                        // 3) бот реконнектился через 6 секунд (Discord.NET recovered),
                                                                                                        //    но HandleStartedAsync уже отработал и в Discord больше
                                                                                                        //    не придёт — таймер никто не переставит;
                                                                                                        // 4) reminder-сообщение в канале анонса висит до того момента,
                                                                                                        //    пока пользователь сам не завершит событие (а тогда
                                                                                                        //    HandleCompletedAsync → DeleteReminderMessagesAsync).
                                                                                                        //
                                                                                                        // Правило: если в сторе есть хоть один reminder-ID (DM, канал
                                                                                                        // анонса, Telegram), и ActualStartTimeUtc ?? LastStartTimeUtc
                                                                                                        // + 15мин ≤ now — пора дропать, независимо от того, дожил ли
                                                                                                        // in-memory Task.Delay до своего хвоста.
                                                                                                        {
                                                                                                            var actual = entry.ActualStartTimeUtc ?? entry.LastStartTimeUtc;
                                                                                                            if (actual.HasValue)
                                                                                                            {
                                                                                                                var deleteAt = actual.Value.AddMinutes(15);
                                                                                                                var nowUtc = DateTimeOffset.UtcNow;
                                                                                                                var dmIds = entry.ReminderDmMessageIdsByUserId;
                                                                                                                bool hasReminder =
                                                                                                                    (dmIds != null && dmIds.Count > 0)
                                                                                                                    || entry.ReminderAnnounceMessageId != 0
                                                                                                                    || (entry.ReminderTelegramMessageId != 0
                                                                                                                        && entry.ReminderTelegramChatId != 0);
                                                                                                                if (hasReminder && nowUtc >= deleteAt)
                                                                                                                {
                                                                                                                    BotLogger.Info(LogCategory.Discord,
                                                                                                                        $"[EventOpsLifecycle] RebuildFromStoreAsync: catch-up deleteReminder15m (disconnect-no-rehydrate) guild={entry.GuildId} event={entry.EventId} start={actual:o} deleteAt={deleteAt:o} now={nowUtc:o}");
                                                                                                                    try
                                                                                                                    {
                                                                                                                        await DeleteReminderMessagesAsync(entry);
                                                                                                                        sentCatchupReminderDelete++;
                                                                                                                    }
                                                                                                                    catch (Exception exD)
                                                                                                                    {
                                                                                                                        BotLogger.Warn(LogCategory.Discord,
                                                                                                                            $"[EventOpsLifecycle] catch-up deleteReminder failed: {exD.Message}");
                                                                                                                    }
                                                                                                                }
                                                                                                            }
                                                                                                        }

                        // 🩹 prod-cleanup-stuck v2: если событие Scheduled/Started (нет mark),
                        // но LastStartTimeUtc уже давно в прошлом (>= 4ч назад) — значит
                        // бот пропустил и Started, и Completed (либо был выключен, либо
                        // был offline). Без нашего вмешательства событие навсегда зависнет
                                                // в сторе как «активное».
                                                //
                                                // v1 переводило запись в completed с эвристикой LastStartTimeUtc+2ч,
                                                // и cleanup24h считался от этого «фейкового» момента. Проблема:
                                                // длительность события неизвестна — могло быть 30 мин, могло 6ч.
                                                // Эвристика обрезала длительные события и чистила анонс раньше времени.
                                                //
                                                // v2 (от 23.09.2026 по решению пользователя): событие, которое бот
                                                // реально не видел в Started/Completed, для нас «потерянное».
                                                // Помечаем его как abandoned — НЕ как completed (Completed-маркер
                                                // нужен только для случая, когда Completed поступил от Discord через
                                                // Started+Completed-пару). cleanup24h считается от
                                                // LastStartTimeUtc + 24ч: событие уже прошло, через сутки никто не
                                                // вспомнит — запись можно удалять. Если LastEndTimeUtc есть и попадает
                                                // в окно [LastStartTimeUtc, +24ч], cleanup считается от end+24ч для
                                                // единообразия (на случай, если событие шло меньше суток).
                                                // Дополнительно: «pending_announce» — наш новый маркер записей, у
                                                // которых Created пришёл, но Discord-announce не ушёл (giveup). Их
                                                // тоже считаем зависшими и помечаем abandoned, если старт далеко
                                                // в прошлом или pending висит дольше часа (ReconcilePendingAnnounce
                                                // ниже попробует доотправить).
                                                if (entry.LastUpdatedMark is null or "started" or "scheduled" or "pending_announce"
                                                    && entry.LastStartTimeUtc.HasValue
                                                    && entry.CompletedAtUtc is null
                                                    && entry.CancelledAtUtc is null
                                                    && entry.LastStartTimeUtc.Value <= DateTimeOffset.UtcNow.AddHours(-4))
                                                {
                                                    // Берём LastEndTimeUtc, только если он >= LastStartTimeUtc
                                                    // (битые данные типа EndTime раньше StartTime игнорируем).
                                                    DateTimeOffset? cleanupSeed = null;
                                                    if (entry.LastEndTimeUtc.HasValue
                                                        && entry.LastEndTimeUtc.Value >= entry.LastStartTimeUtc.Value
                                                        && entry.LastEndTimeUtc.Value <= entry.LastStartTimeUtc.Value.AddHours(24))
                                                    {
                                                        // cleanup = end + 24ч, в пределах 24ч от старта
                                                        cleanupSeed = entry.LastEndTimeUtc.Value;
                                                    }
                                                    else
                                                    {
                                                        // cleanup = start + 24ч (если событие было «без конца»
                                                        // или длилось дольше суток — берём максимальный разумный
                                                        // интервал и считаем от старта; событие уже в прошлом).
                                                        cleanupSeed = entry.LastStartTimeUtc.Value.AddHours(24);
                                                    }

                                                    entry.CompletedAtUtc = entry.LastEndTimeUtc
                                                        ?? entry.LastStartTimeUtc.Value;
                                                    entry.LastUpdatedMark = "abandoned";
                                                    entry.LastUpdatedAt = DateTime.UtcNow;
                                                    entry.CleanupAtUtc = cleanupSeed;
                                                    try { _store.UpdateEntry(entry); } catch { }

                                                    BotLogger.Warn(LogCategory.Discord,
                                                        $"[EventOpsLifecycle] RebuildFromStoreAsync: пометил застрявшее событие как abandoned (prod-cleanup-stuck v2) guild={entry.GuildId} event={entry.EventId} start={entry.LastStartTimeUtc:o} end={(entry.LastEndTimeUtc?.ToString("o") ?? "null")} cleanupAt={cleanupSeed:o}");

                                                    if (ScheduleCleanup24h(entry))
                                                        scheduledBackfillCompleted++;
                                                }
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        BotLogger.Error(LogCategory.Discord,
                            $"[EventOpsLifecycle] rebuild entry guild={entry.GuildId} event={entry.EventId}: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                BotLogger.Info(LogCategory.Discord,
                    $"[EventOpsLifecycle] RebuildFromStoreAsync завершено: cleanup={scheduledCleanup}, reminder={scheduledReminder}, backfillCompleted={scheduledBackfillCompleted}, catchup={sentCatchupReminder}, catchupDel={sentCatchupReminderDelete}, skipped={skipped}");

                // 🐕 reminder-watchdog-sweep: страховка запускается и на старте бота,
                // не только после реконнекта (single-flight исключит дубль).
                StartReminderSweep();
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  Watchdog-свайп напоминаний
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Пер-канальная проверка «напоминание уже уходило» — та же логика, что в
        /// <see cref="RehydrateTimersAsync"/> и <see cref="SendReminderAsync"/>.
        /// </summary>
        internal static bool ReminderWasPerChannelSent(EventAnnouncementEntry entry)
            => (entry.ReminderDmMessageIdsByUserId != null && entry.ReminderDmMessageIdsByUserId.Count > 0)
               || entry.ReminderAnnounceMessageId != 0
               || entry.ReminderTelegramMessageId != 0;

        /// <summary>Событие уже началось (по <c>LastStartTimeUtc</c>)?</summary>
        internal static bool EventAlreadyStarted(EventAnnouncementEntry entry, DateTimeOffset nowUtc)
            => entry.LastStartTimeUtc.HasValue && nowUtc >= entry.LastStartTimeUtc.Value;

        /// <summary>
        /// Запускает фоновый watchdog-цикл: раз в <see cref="ReminderSweepInterval"/>
        /// проходит по свежему снапшоту стора и восстанавливает напоминания:
        /// <list type="bullet">
        ///   <item>момент reminder1h в прошлом, факт отправки не зафиксирован, событие
        ///   ещё не началось — напоминание досылается (с защитой от дублей: живой
        ///   таймер держит CTS в слоте; Cancel() чистит слоты — пустой слот = можно шлать);</item>
        ///   <item>момент в прошлом, но событие уже началось — напоминание рассылать нельзя,
        ///   заброшенный таймер снимается (кейс 2026-10-20 12:00Z);</item>
        ///   <item>момент в будущем — таймер ставится, если слот пуст (потерялся
        ///   из-за Cancel/ошибки планирования);</item>
        ///   <item>то же для deleteReminder15m, но удаление — только после старта события.</item>
        /// </list>
        /// Цикл связан с <c>_shutdownCts</c>: на Cancel() умирает вместе с rehydrate-таймерами
        /// и перезапускается при следующем RehydrateTimersAsync/RebuildFromStoreAsync.
        /// </summary>
        private void StartReminderSweep()
        {
            if (Interlocked.CompareExchange(ref _sweepRunning, 1, 0) != 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var rootCt = _shutdownCts.Token;
                    BotLogger.Info(LogCategory.Discord,
                        $"[EventOpsLifecycle] Watchdog-свайп запущен: интервал={(int)ReminderSweepInterval.TotalMinutes} мин");
                    while (!rootCt.IsCancellationRequested)
                    {
                        try { await SweepOnceAsync(rootCt); }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            BotLogger.Warn(LogCategory.Discord,
                                $"[EventOpsLifecycle] Watchdog-свайп: ошибка прохода: {ex.GetType().Name}: {ex.Message}");
                        }

                        try { await Task.Delay(ReminderSweepInterval, rootCt); }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.Discord,
                        $"[EventOpsLifecycle] Watchdog-свайп цикл прерван: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _sweepRunning, 0);
                }
            });
        }

        /// <summary>Одиночный проход watchdog-свайпа по свежему снапшоту стора.</summary>
        private async Task SweepOnceAsync(CancellationToken ct)
        {
            var entries = _store.GetEntriesSnapshot();
            var nowUtc = DateTimeOffset.UtcNow;

            int sentReminder = 0, scheduledReminder = 0, skippedStarted = 0,
                sentDelete = 0, scheduledDelete = 0;

            bool HasLiveTimer(ScheduledTimers slot, bool forDelete)
            {
                lock (slot.Lock)
                {
                    return forDelete ? slot.DeleteReminder15mCts != null : slot.Reminder1hCts != null;
                }
            }

            foreach (var entry in entries)
            {
                if (ct.IsCancellationRequested) break;

                // ── (1) reminder1h ────────────────────────────────────────────
                if (entry.Reminder1hAtUtc.HasValue && !ReminderWasPerChannelSent(entry))
                {
                    var reminderAt = entry.Reminder1hAtUtc.Value;
                    var started = EventAlreadyStarted(entry, nowUtc);

                    if (nowUtc >= reminderAt)
                    {
                        // Момент прошёл. Если живой таймер существует — он сам отправляет;
                        // голый просроченный кейс (слот пуст, поле в сторе есть) — досылаем.
                        var slot = _timers.GetOrAdd((entry.GuildId, entry.EventId), _ => new ScheduledTimers());
                        if (!HasLiveTimer(slot, forDelete: false))
                        {
                            var fresh = _store.TryGet(entry.GuildId, entry.EventId);
                            if (fresh == null) continue;

                            if (EventAlreadyStarted(fresh, nowUtc))
                            {
                                // 🩹 reminder-lead-text: пост-стартовые напоминания запрещены
                                // (кейс 2026-10-20). Снимаем заброшенный план и чистим поле.
                                CancelReminder1h(fresh);
                                skippedStarted++;
                            }
                            else
                            {
                                BotLogger.Info(LogCategory.Discord,
                                    $"[EventOpsLifecycle] Watchdog-свайп: досылаю reminder1h guild={fresh.GuildId} event={fresh.EventId} reminderAt={reminderAt:o} now={nowUtc:o}");
                                try { await SendReminderAsync(fresh); sentReminder++; }
                                catch (Exception exR)
                                {
                                    BotLogger.Warn(LogCategory.Discord,
                                        $"[EventOpsLifecycle] Watchdog-свайп: SendReminderAsync failed: {exR.Message}");
                                }
                            }
                        }
                    }
                    else if (!started)
                    {
                        // Будущее: таймер должен стоять; пустой слот (после Cancel())
                        // — ставим заново. Живой таймер не трогаем (сбивать delay нельзя).
                        var slot = _timers.GetOrAdd((entry.GuildId, entry.EventId), _ => new ScheduledTimers());
                        if (!HasLiveTimer(slot, forDelete: false))
                        {
                            var delay = reminderAt - nowUtc;
                            var cts = new CancellationTokenSource();
                            bool placed;
                            lock (slot.Lock)
                            {
                                if (slot.Reminder1hCts == null)
                                {
                                    slot.Reminder1hCts = cts;
                                    slot.Reminder1hAtUtc = reminderAt;
                                    placed = true;
                                }
                                else
                                {
                                    placed = false;
                                }
                            }

                            if (placed)
                            {
                                _ = Task.Run(() => RunReminder1hAsync(entry, cts.Token, delay), cts.Token);
                                scheduledReminder++;
                                BotLogger.Info(LogCategory.Discord,
                                    $"[EventOpsLifecycle] Watchdog-свайп: восстановлен таймер reminder1h guild={entry.GuildId} event={entry.EventId} через {(int)delay.TotalMinutes} мин (at {reminderAt:u})");
                            }
                            else
                            {
                                try { cts.Dispose(); } catch { }
                            }
                        }
                    }
                }

                // ── (2) deleteReminder15m ─────────────────────────────────────
                if (entry.DeleteReminder15mAtUtc.HasValue)
                {
                    var deleteAt = entry.DeleteReminder15mAtUtc.Value;
                    var slot = _timers.GetOrAdd((entry.GuildId, entry.EventId), _ => new ScheduledTimers());

                    if (nowUtc >= deleteAt)
                    {
                        // Удаление reminder-сообщений имеет смысл только после старта
                        // события (иначе можно снести напоминание, которое ещё уйдёт).
                        // DeleteReminderMessagesAsync идемпотентен и сам чистит поле в сторе.
                        if (EventAlreadyStarted(entry, nowUtc) && !HasLiveTimer(slot, forDelete: true))
                        {
                            var fresh = _store.TryGet(entry.GuildId, entry.EventId);
                            if (fresh == null) continue;

                            BotLogger.Info(LogCategory.Discord,
                                $"[EventOpsLifecycle] Watchdog-свайп: досылаю deleteReminder15m guild={fresh.GuildId} event={fresh.EventId} deleteAt={deleteAt:o} now={nowUtc:o}");
                            try { await DeleteReminderMessagesAsync(fresh); sentDelete++; }
                            catch (Exception exD)
                            {
                                BotLogger.Warn(LogCategory.Discord,
                                    $"[EventOpsLifecycle] Watchdog-свайп: DeleteReminderMessagesAsync failed: {exD.Message}");
                            }
                        }
                    }
                    else if (!HasLiveTimer(slot, forDelete: true))
                    {
                        var delay = deleteAt - nowUtc;
                        var cts = new CancellationTokenSource();
                        bool placed;
                        lock (slot.Lock)
                        {
                            if (slot.DeleteReminder15mCts == null)
                            {
                                slot.DeleteReminder15mCts = cts;
                                placed = true;
                            }
                            else
                            {
                                placed = false;
                            }
                        }

                        if (placed)
                        {
                            _ = Task.Run(() => RunDeleteReminder15mAsync(entry, cts.Token, delay), cts.Token);
                            scheduledDelete++;
                            BotLogger.Info(LogCategory.Discord,
                                $"[EventOpsLifecycle] Watchdog-свайп: восстановлен таймер deleteReminder15m guild={entry.GuildId} event={entry.EventId} через {(int)delay.TotalMinutes} мин (at {deleteAt:u})");
                        }
                        else
                        {
                            try { cts.Dispose(); } catch { }
                        }
                    }
                }
            }

            BotLogger.Info(LogCategory.Discord,
                $"[EventOpsLifecycle] Watchdog-свайп завершён: checked={entries.Count}, reminderSent={sentReminder}, reminderScheduled={scheduledReminder}, reminderSkipStarted={skippedStarted}, deleteSent={sentDelete}, deleteScheduled={scheduledDelete}");
        }

        // ════════════════════════════════════════════════════════════════════
        //  Обработчики событий EventOpsOrchestrator
        // ════════════════════════════════════════════════════════════════════

        private async Task HandleUpdatedAsync(SocketGuildEvent current, SocketGuildEvent? previous)
        {
            try
            {
                var entry = _store.TryGet(current.Guild.Id, current.Id);
                if (entry == null) return;

                // Если событие уже в финальном статусе — reminder не нужен,
                // и мы не должны его перепланировать. Cleanup24h для cancelled/completed
                // уже стоит (см. HandleCancelledAsync/HandleCompletedAsync).
                // Раньше здесь всегда вызывался ScheduleReminder1h — это могло
                // привести к повторной рассылке reminder для cancelled-события,
                // если Discord прислал обновление после нашей обработки cancelled.
                var status = current.Status.ToString().ToLowerInvariant();
                if (status == "cancelled" || status == "completed")
                {
                    BotLogger.Info(LogCategory.Discord,
                        $"[EventOpsLifecycle] HandleUpdatedAsync guild={current.Guild.Id} event={current.Id} status={status} (reminder пропущен, финальный статус)");
                    return;
                }

                // Перепланируем reminder1h: время могло сдвинуться.
                ScheduleReminder1h(entry);

                BotLogger.Info(LogCategory.Discord,
                    $"[EventOpsLifecycle] HandleUpdatedAsync guild={current.Guild.Id} event={current.Id} (reminder1h перепланирован)");
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Discord,
                    $"[EventOpsLifecycle] HandleUpdatedAsync error: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        private async Task HandleCreatedAsync(SocketGuildEvent ev)
        {
            try
            {
                // Created — самый первый момент, когда у нас появляется событие
                // и есть валидный LastStartTimeUtc из REST. До этого момента reminder1h
                // поставить невозможно. Раньше lifecycle подписывался только на Updated/Started/
                // Completed/Cancelled, и если Discord не присылал ни одного Update между
                // Created и Started (типичный случай: событие создаётся и висит долго без
                // правок) — reminder1h вообще никогда не планировался, и игроки не получали
                // напоминалку. Теперь ставим reminder сразу в Created.
                //
                // Retry TryGet: HandleCreatedAsync вызывается из multicast OnCreatedAsync,
                // где Announcer стоит первым. CLR запускает обоих подписчиков
                // последовательно, но Announcer — async, и CLR переходит к следующему
                // подписчику сразу после первого await в Announcer (не дожидаясь
                // _store.Upsert). Без retry Lifecycle читает entry == null и молча
                // выходит (audit bug #1). Несколько коротких попыток по 200мс — дешевле,
                // чем рефакторинг multicast на явный список обработчиков, и
                // гарантированно ловит созданный Announcer'ом entry.
                EventAnnouncementEntry? entry = null;
                for (int i = 0; i < 10 && entry == null; i++)
                {
                    entry = _store.TryGet(ev.Guild.Id, ev.Id);
                    if (entry == null)
                        await Task.Delay(200).ConfigureAwait(false);
                }
                if (entry == null)
                {
                    BotLogger.Warn(LogCategory.Discord,
                        $"[EventOpsLifecycle] HandleCreatedAsync: entry не появился в сторе за 2с: guild={ev.Guild.Id} event={ev.Id} (Announcer, вероятно, упал до Upsert)");
                    return;
                }

                ScheduleReminder1h(entry);
                BotLogger.Info(LogCategory.Discord,
                    $"[EventOpsLifecycle] HandleCreatedAsync guild={ev.Guild.Id} event={ev.Id} (reminder1h поставлен из Created)");
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Discord,
                    $"[EventOpsLifecycle] HandleCreatedAsync error: {ex.Message}");
            }
        }

        private async Task HandleStartedAsync(SocketGuildEvent ev)
        {
            try
            {
                var entry = _store.TryGet(ev.Guild.Id, ev.Id);
                if (entry == null) return;

                // Reminder1h больше не нужен (событие уже началось).
                CancelReminder1h(entry);

                // Ставим удаление напоминания через 15 мин после ActualStartTimeUtc.
                ScheduleDeleteReminder15m(entry);
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Discord,
                    $"[EventOpsLifecycle] HandleStartedAsync error: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        private async Task HandleCompletedAsync(SocketGuildEvent ev)
        {
            try
            {
                var entry = _store.TryGet(ev.Guild.Id, ev.Id);
                if (entry == null) return;

                // Reminder больше не актуален.
                CancelReminder1h(entry);
                CancelDeleteReminder15m(entry);

                // Сразу подчищаем reminder-сообщения из канала/DM/Telegram —
                // даже если reminder1h уже отправлен, оно не должно висеть
                // после завершения события. Делаем ДО ScheduleCleanup24h,
                // чтобы cleanup не сработал раньше (cleanup24h ждёт 24ч, тут
                // удаление мгновенное — порядок не критичен, но логически
                // cleanup для анонса, а reminder для служебного сообщения).
                await DeleteReminderMessagesAsync(entry);

                // Вычисляем и сохраняем абсолютное время cleanup в стор —
                // это поле переживёт рестарт бота (см. RebuildFromStoreAsync).
                //
                // Приоритет: CompletedAtUtc (фактическое завершение) → CancelledAtUtc
                // (теоретически, если бот увидел cancelled сразу после completed) →
                // ActualStartTimeUtc (фактический старт) → LastStartTimeUtc (план).
                // Раньше fallback начинался с CancelledAtUtc — для completed-событий
                // без ActualStartTimeUtc это давало неверный момент.
                var at = entry.CompletedAtUtc
                      ?? entry.CancelledAtUtc
                      ?? entry.ActualStartTimeUtc
                      ?? entry.LastStartTimeUtc;
                if (at.HasValue)
                {
                    entry.CleanupAtUtc = at.Value.AddHours(24);
                    try { _store.UpdateEntry(entry); } catch { }
                }

                // Через 24ч удаляем анонс.
                ScheduleCleanup24h(entry);
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Discord,
                    $"[EventOpsLifecycle] HandleCompletedAsync error: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        private async Task HandleCancelledAsync(SocketGuildEvent ev)
        {
            try
            {
                var entry = _store.TryGet(ev.Guild.Id, ev.Id);
                if (entry == null) return;

                CancelReminder1h(entry);
                CancelDeleteReminder15m(entry);

                // Если reminder1h уже был отправлен в канал анонса / DM / Telegram —
                // удаляем эти сообщения немедленно, чтобы они не висели после
                // отмены события. Idempotent: если сообщений нет (не отправлялись
                // или уже удалены), DeleteReminderMessagesAsync молча выходит.
                await DeleteReminderMessagesAsync(entry);

                // Для cancelled момент отмены — это уже сейчас; если
                // EventAnnouncer не успел проставить CancelledAtUtc, сделаем
                // это сами и сохраним в стор.
                if (!entry.CancelledAtUtc.HasValue)
                {
                    entry.CancelledAtUtc = DateTimeOffset.UtcNow;
                }
                entry.CleanupAtUtc = entry.CancelledAtUtc.Value.AddHours(24);
                try { _store.UpdateEntry(entry); } catch { }

                ScheduleCleanup24h(entry);
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Discord,
                    $"[EventOpsLifecycle] HandleCancelledAsync error: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Планирование
        // ════════════════════════════════════════════════════════════════════

                /// <summary>
                /// Возвращает true, если reminder1h для этого события уже отправлялся в любой
                /// из трёх каналов (DM подписчикам / Discord-канал анонса / Telegram-чат).
                ///
                /// Используется в <see cref="RebuildFromStoreAsync"/> и <see cref="ScheduleReminder1h"/>
                /// чтобы не слать reminder повторно после рестарта бота. До этого фикса (Bug evt-rb-dup)
                /// проверка смотрела только на DM, и если подписчиков с DM не было — catch-up слал
                /// reminder повторно при каждом рестарте (даже если он уже ушёл в канал/TG).
                /// </summary>
                private static bool WasReminderAlreadySent(EventAnnouncementEntry entry)
                {
                    var dmIds = entry.ReminderDmMessageIdsByUserId;
                    return (dmIds != null && dmIds.Count > 0)
                        || entry.ReminderAnnounceMessageId != 0
                        || (entry.ReminderTelegramMessageId != 0 && entry.ReminderTelegramChatId != 0);
                }

                private bool ScheduleReminder1h(EventAnnouncementEntry entry)
                {
                    // 🩹 Bug evt-rb-dup: если reminder уже отправлялся во ВСЕ каналы (DM / канал анонса /
                    // Telegram) — не планировать новый. До этого guard RebuildFromStoreAsync после рестарта
                    // перепланировал reminder, который уже был отослан в прошлой сессии, и тот
                    // срабатывал снова. Теперь стор — единый источник истины: если все Reminder*-ID
                    // заполнены, считаем, что reminder уже ушёл везде.
                    //
                    // 🔁 per-channel-already-sent-skip: guard остался, но стал «per-channel».
                    // SendReminderAsync сам пропустит каналы, по которым reminder уже был,
                    // поэтому даже если стор показывает «уже отправлялся» для announce/TG,
                    // но DM ещё не было (ReminderDmMessageIdsByUserId == null) — мы всё равно
                    // должны доставить reminder в DM, иначе подписчики без уведомления.
                    var dmAlready = entry.ReminderDmMessageIdsByUserId != null && entry.ReminderDmMessageIdsByUserId.Count > 0;
                    var announceAlready = entry.ReminderAnnounceMessageId != 0;
                    var tgAlready = entry.ReminderTelegramMessageId != 0 && entry.ReminderTelegramChatId != 0;
                    if (dmAlready && announceAlready && tgAlready)
                    {
                        BotLogger.Info(LogCategory.Discord,
                            $"[EventOpsLifecycle] ScheduleReminder1h: skip, reminder уже отправлялся ранее во все каналы (dm={entry.ReminderDmMessageIdsByUserId?.Count ?? 0} announce={entry.ReminderAnnounceMessageId} tg={entry.ReminderTelegramMessageId}) guild={entry.GuildId} event={entry.EventId}");
                        return false;
                    }

                    if (!entry.LastStartTimeUtc.HasValue)
                    {
                        BotLogger.Warn(LogCategory.Discord,
                            $"[EventOpsLifecycle] ScheduleReminder1h: skip, нет LastStartTimeUtc guild={entry.GuildId} event={entry.EventId}");
                return false;
            }

            var reminderAt = entry.LastStartTimeUtc.Value.AddHours(-1);
            var nowUtc = DateTimeOffset.UtcNow;
            var startUtc = entry.LastStartTimeUtc.Value;

            // Окно для reminder1h: [start - 1h, start).
            //   • reminderAt > nowUtc → планируем на будущее (норма).
            //   • reminderAt <= nowUtc < start → опоздали, но событие ещё не началось:
            //     шлём reminder прямо сейчас (всё равно полезнее, чем не слать).
            //   • nowUtc >= start → событие уже стартовало/завершилось — reminder не нужен,
            //     удаляем, если он был, и выходим.
            if (nowUtc >= startUtc)
            {
                BotLogger.Info(LogCategory.Discord,
                    $"[EventOpsLifecycle] ScheduleReminder1h: skip, событие уже стартовало/завершилось now={nowUtc:o} start={startUtc:o} guild={entry.GuildId} event={entry.EventId}");
                // На всякий случай — если reminder-сообщения остались с предыдущей сессии,
                // попробуем их удалить. Идемпотентно.
                _ = Task.Run(() => DeleteReminderMessagesAsync(entry));
                return false;
            }

            TimeSpan delay;
            if (reminderAt > nowUtc)
            {
                delay = reminderAt - nowUtc;
            }
            else
            {
                // Опоздали — шлём сейчас, delay=0.
                delay = TimeSpan.Zero;
                BotLogger.Warn(LogCategory.Discord,
                    $"[EventOpsLifecycle] ScheduleReminder1h: опоздали с планированием, шлём reminder немедленно now={nowUtc:o} reminderAt={reminderAt:o} guild={entry.GuildId} event={entry.EventId}");
            }

            var key = (entry.GuildId, entry.EventId);
            var slot = _timers.GetOrAdd(key, _ => new ScheduledTimers());

                        // ВАЖНО: меняем CTS атомарно через lock на слоте. Иначе при
                        // одновременном ScheduleReminder1h (например, duplicate resync плюс
                        // OnUpdatedAsync) две нитки могут cancel+Dispose одну и ту же ссылку
                        // и затереть только что созданный CTS другой ниткой — reminder не
                        // уйдёт вообще (Bug #7: duplicate resync race).
                        CancellationTokenSource cts;
                        lock (slot.Lock)
                        {
                            try { slot.Reminder1hCts?.Cancel(); } catch { }
                            try { slot.Reminder1hCts?.Dispose(); } catch { }

                            cts = new CancellationTokenSource();
                            slot.Reminder1hCts = cts;
                            slot.Reminder1hAtUtc = reminderAt;
                        }

            _ = Task.Run(() => RunReminder1hAsync(entry, cts.Token, delay), cts.Token);

                        // 🩹 reminder-survives-restart: пишем абсолютный момент напоминания в стор,
                        // чтобы он пережил рестарт бота/дисконнект/exception в RunReminder1h.
                        // RebuildFromStoreAsync читает это поле на старте и досылает reminder, если
                        // окно уже прошло, либо пере-регистрирует Task.Delay на остаток.
                        entry.Reminder1hAtUtc = reminderAt;
                        try { _store.UpdateEntry(entry); } catch { }

                        BotLogger.Info(LogCategory.Discord,
                            $"[EventOpsLifecycle] запланирован reminder1h: guild={entry.GuildId} event={entry.EventId} через {(int)delay.TotalMinutes} мин (at {reminderAt:u})");
                        return true;
                    }

        private void CancelReminder1h(EventAnnouncementEntry entry)
                {
                    var key = (entry.GuildId, entry.EventId);
                    if (_timers.TryGetValue(key, out var slot))
                    {
                                // Bug #7: lock — атомарная отмена Reminder1hCts под блокировкой слота,
                                // чтобы параллельный ScheduleReminder1h не отменил только что созданный CTS.
                                lock (slot.Lock)
                                {
                                    if (slot.Reminder1hCts != null)
                                    {
                                        try { slot.Reminder1hCts.Cancel(); } catch { }
                                        try { slot.Reminder1hCts.Dispose(); } catch { }
                                        slot.Reminder1hCts = null;
                                        slot.Reminder1hAtUtc = null;
                                    }
                                }
                    }

                    // 🩹 reminder-survives-restart: если event был в стор-записи, но слот ушёл
                    // (например, после полного Cancel() в OnDisconnected) — всё равно зачищаем
                    // абсолютный момент в сторе, чтобы RebuildFromStoreAsync не подобрал
                    // «фантомный» reminder для уже стартовавшего события.
                    if (entry.Reminder1hAtUtc.HasValue)
                    {
                        entry.Reminder1hAtUtc = null;
                        try { _store.UpdateEntry(entry); } catch { }
                    }
                }

        private void ScheduleDeleteReminder15m(EventAnnouncementEntry entry)
        {
            // ActualStartTimeUtc может быть null если бот пропустил OnStartedAsync
            // (был оффлайн). В этом случае берём LastStartTimeUtc как fallback.
            var at = entry.ActualStartTimeUtc ?? entry.LastStartTimeUtc;
            if (!at.HasValue) return;

            var deleteAt = at.Value.AddMinutes(15);
            var nowUtc = DateTimeOffset.UtcNow;
                    if (deleteAt <= nowUtc)
                    {
                        // Уже прошло — но всё равно фиксируем момент в стор, чтобы повторные
                        // вызовы (например, при duplicate resync) шли через DeleteReminderMessagesAsync
                        // в RebuildFromStoreAsync, а не полагались на Task.Delay (он бы не сработал).
                        entry.DeleteReminder15mAtUtc = deleteAt;
                        try { _store.UpdateEntry(entry); } catch { }
                        return;
                    }

                    var key = (entry.GuildId, entry.EventId);
                    var slot = _timers.GetOrAdd(key, _ => new ScheduledTimers());

                                // См. Bug #7 в ScheduleReminder1h — атомарная перестановка CTS под lock.
                                CancellationTokenSource cts;
                                lock (slot.Lock)
                                {
                                    try { slot.DeleteReminder15mCts?.Cancel(); } catch { }
                                    try { slot.DeleteReminder15mCts?.Dispose(); } catch { }

                                    cts = new CancellationTokenSource();
                                    slot.DeleteReminder15mCts = cts;
                                }

                    var delay = deleteAt - nowUtc;
                    _ = Task.Run(() => RunDeleteReminder15mAsync(entry, cts.Token, delay), cts.Token);

                    // 🩹 reminder-survives-restart: сохраняем абсолютный момент в стор. Если
                    // Task.Delay потеряется (OnDisconnected/Cancel, kill -9, исключение) — при
                    // следующем рестарте RebuildFromStoreAsync проверит поле и либо поставит
                    // новый Task.Delay на остаток, либо досрочно вызовет DeleteReminderMessagesAsync.
                    entry.DeleteReminder15mAtUtc = deleteAt;
                    try { _store.UpdateEntry(entry); } catch { }

                    BotLogger.Info(LogCategory.Discord,
                        $"[EventOpsLifecycle] запланирован deleteReminder15m: guild={entry.GuildId} event={entry.EventId} через {(int)delay.TotalMinutes} мин (at {deleteAt:u})");
                }

                private void CancelDeleteReminder15m(EventAnnouncementEntry entry)
                {
                    var key = (entry.GuildId, entry.EventId);
                    if (_timers.TryGetValue(key, out var slot))
                    {
                            // Bug #7: lock — иначе может прийти параллельный ScheduleDeleteReminder15m
                            // и либо прочитать старый отменённый CTS, либо наоборот — отменить наш.
                            lock (slot.Lock)
                            {
                                if (slot.DeleteReminder15mCts != null)
                                {
                                    try { slot.DeleteReminder15mCts.Cancel(); } catch { }
                                    try { slot.DeleteReminder15mCts.Dispose(); } catch { }
                                    slot.DeleteReminder15mCts = null;
                                }
                            }
                    }

                    // 🩹 reminder-survives-restart: зачищаем абсолютный момент в сторе даже если
                    // слот пропал (OnDisconnected → Cancel()). Без этого RebuildFromStoreAsync
                    // мог бы повторно запланировать удаление для уже завершённого события.
                    if (entry.DeleteReminder15mAtUtc.HasValue)
                    {
                        entry.DeleteReminder15mAtUtc = null;
                        try { _store.UpdateEntry(entry); } catch { }
                    }
                }

        private bool ScheduleCleanup24h(EventAnnouncementEntry entry)
        {
            // Приоритет: entry.CleanupAtUtc (абсолютное время, записанное в JSON).
            // Если null — вычисляем на лету и сохраняем в стор, чтобы при рестарте
            // бота RebuildFromStoreAsync использовал тот же момент.
            //
            // Порядок fallback'ов — от «самого позднего» к «самому раннему»:
            //   1) CompletedAtUtc  — фактический момент завершения (если событие было completed)
            //   2) CancelledAtUtc  — фактический момент отмены      (если cancelled)
            //   3) ActualStartTimeUtc — фактический момент старта  (бот был онлайн на started)
            //   4) LastStartTimeUtc   — плановое время начала       (rare fallback)
            // Раньше fallback начинался с CancelledAtUtc — это давало 1372 мин вместо 1440 мин
            // для completed-событий, у которых ActualStartTimeUtc был null: cleanup считался от
            // планового LastStartTimeUtc, а не от фактического завершения.
            DateTimeOffset cleanupAt;
            var nowUtc = DateTimeOffset.UtcNow;
            if (entry.CleanupAtUtc.HasValue)
            {
                cleanupAt = entry.CleanupAtUtc.Value;
            }
            else
            {
                var at = entry.CompletedAtUtc
                      ?? entry.CancelledAtUtc
                      ?? entry.ActualStartTimeUtc
                      ?? entry.LastStartTimeUtc;
                if (!at.HasValue)
                {
                    // Запись в сторе без единой временной метки — такого быть не должно
                    // (AnnounceCreatedInternalAsync всегда проставляет LastStartTimeUtc),
                    // но если очень старый анонс без дат или данные повредились — лучше
                    // удалить сейчас, чем держать анонс вечно. Это совпадает с веткой
                    // «cleanupAt <= nowUtc» ниже, но тут мы ещё и логируем, что случай
                    // аномальный.
                    BotLogger.Warn(LogCategory.Discord,
                        $"[EventOpsLifecycle] ScheduleCleanup24h: нет временных меток для cleanup guild={entry.GuildId} event={entry.EventId}, ставлю немедленное удаление");
                    cleanupAt = nowUtc.AddSeconds(2);
                }
                else
                {
                    cleanupAt = at.Value.AddHours(24);
                }
                entry.CleanupAtUtc = cleanupAt;
                try { _store.UpdateEntry(entry); } catch { }
            }

            if (cleanupAt <= nowUtc)
            {
                // Момент уже в прошлом — выполним через 2 секунды (фон).
                cleanupAt = nowUtc.AddSeconds(2);
                entry.CleanupAtUtc = cleanupAt;
                try { _store.UpdateEntry(entry); } catch { }
            }

            var key = (entry.GuildId, entry.EventId);
            var slot = _timers.GetOrAdd(key, _ => new ScheduledTimers());

                        // Bug #7: lock — атомарная перестановка Cleanup24hCts под блокировкой
                        // слота, иначе параллельные ScheduleCleanup24h (например, из HandleCompletedAsync
                        // и из RebuildFromStoreAsync при рестарте) могут cancel+Dispose одну ссылку и
                        // затереть только что созданную другой ниткой — cleanup не выполнится.
                        CancellationTokenSource cts;
                        lock (slot.Lock)
                        {
                            try { slot.Cleanup24hCts?.Cancel(); } catch { }
                            try { slot.Cleanup24hCts?.Dispose(); } catch { }

                            cts = new CancellationTokenSource();
                            slot.Cleanup24hCts = cts;
                        }

                        var delay = cleanupAt - nowUtc;
                        _ = Task.Run(() => RunCleanup24hAsync(entry, cts.Token, delay), cts.Token);

            BotLogger.Info(LogCategory.Discord,
                $"[EventOpsLifecycle] запланирован cleanup24h: guild={entry.GuildId} event={entry.EventId} через {(int)delay.TotalMinutes} мин (at {cleanupAt:u})");
            return true;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Реализация таймеров
        // ════════════════════════════════════════════════════════════════════

        private async Task RunReminder1hAsync(EventAnnouncementEntry entry, CancellationToken ct, TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay, ct);
                if (ct.IsCancellationRequested)
                {
                    BotLogger.Info(LogCategory.Discord,
                        $"[EventOpsLifecycle] RunReminder1h: cancelled до срабатывания guild={entry.GuildId} event={entry.EventId}");
                    return;
                }

                BotLogger.Info(LogCategory.Discord,
                    $"[EventOpsLifecycle] RunReminder1h: сработал, шлём reminder guild={entry.GuildId} event={entry.EventId}");
                await SendReminderAsync(entry);
            }
            catch (OperationCanceledException) { /* штатно */ }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Discord,
                    $"[EventOpsLifecycle] RunReminder1h error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task RunDeleteReminder15mAsync(EventAnnouncementEntry entry, CancellationToken ct, TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay, ct);
                if (ct.IsCancellationRequested)
                {
                    BotLogger.Info(LogCategory.Discord,
                        $"[EventOpsLifecycle] RunDeleteReminder15m: cancelled до срабатывания guild={entry.GuildId} event={entry.EventId}");
                    return;
                }

                BotLogger.Info(LogCategory.Discord,
                    $"[EventOpsLifecycle] RunDeleteReminder15m: сработал, удаляем reminder-сообщения guild={entry.GuildId} event={entry.EventId}");
                await DeleteReminderMessagesAsync(entry);
            }
            catch (OperationCanceledException) { /* штатно */ }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Discord,
                    $"[EventOpsLifecycle] RunDeleteReminder15m error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task RunCleanup24hAsync(EventAnnouncementEntry entry, CancellationToken ct, TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay, ct);
                if (ct.IsCancellationRequested) return;

                await CleanupAnnouncementAsync(entry);
            }
            catch (OperationCanceledException) { /* штатно */ }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Discord,
                    $"[EventOpsLifecycle] RunCleanup24h error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  Действия
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 🩹 reminder-lead-text: честный остаток до события вместо жёсткого «через час».
        /// Плановая отправка идёт на start−1ч (остаток ~60 мин → «через час»), но
        /// catch-up/восстановление после оффлайна шлют позже — тогда пишем реальный
        /// остаток. Если событие уже началось — об этом прямо и пишем.
        /// </summary>
        private static string BuildReminderLeadText(EventAnnouncementEntry entry)
        {
            if (entry.LastStartTimeUtc is not { } start)
                return "через час";

            var remaining = start - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return "событие уже началось";
            if (remaining >= TimeSpan.FromMinutes(50))
                return "через час"; // плановая отправка (запас на джиттер планировщика)

            var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
            return minutes <= 1 ? "прямо сейчас" : $"через {minutes} мин";
        }

        /// <summary>
        /// Собирает тело напоминания. <paramref name="formattedName"/> уже размечен
        /// под конкретный формат (Markdown **…** или Telegram &lt;b&gt;…&lt;/b&gt;).
        /// </summary>
        private static string BuildReminderPhrase(EventAnnouncementEntry entry, string formattedName)
        {
            var lead = BuildReminderLeadText(entry);
            if (lead == "событие уже началось")
                return $"событие {formattedName} уже началось";
            return $"{lead} начнётся событие {formattedName}";
        }

        private async Task SendReminderAsync(EventAnnouncementEntry entry)
        {
            // 🩹 reminder-send-gate: каждый путь отправки дожидается своей очереди
            // по событию; внутри — перечитываем мастер-запись из стора, потому что
            // снапшотные копии (GetEntriesSnapshot) могли не увидеть per-channel ids,
            // записанные предыдущей отправкой прямо перед нами (per-channel skip ниже).
            var gate = _sendGates.GetOrAdd((entry.GuildId, entry.EventId), _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var fresh = _store.TryGet(entry.GuildId, entry.EventId);
                await SendReminderAsyncCore(fresh ?? entry).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task SendReminderAsyncCore(EventAnnouncementEntry entry)
        {
            var client = SafeGetClient();
            if (client == null)
            {
                BotLogger.Warn(LogCategory.Discord,
                    $"[EventOpsLifecycle] SendReminderAsync: client not connected, guild={entry.GuildId}");
                return;
            }

            var guild = client.GetGuild(entry.GuildId);
            if (guild == null)
            {
                BotLogger.Warn(LogCategory.Discord,
                    $"[EventOpsLifecycle] SendReminderAsync: guild {entry.GuildId} not in cache");
                return;
            }

            var eventName = entry.LastName ?? "(без названия)";
            var eventUrl = $"https://discord.com/events/{entry.GuildId}/{entry.EventId}";
            var startUnix = entry.LastStartTimeUtc.HasValue
                ? entry.LastStartTimeUtc.Value.ToUnixTimeSeconds()
                : 0;

            // Откуда и кто — берём из LastLocation / LastChannelId (для Telegram
            // и Discord они разные, см. ниже).
            var whereText = entry.LastChannelId is ulong chId && chId != 0
                ? $"в <#{chId}>"
                : (!string.IsNullOrWhiteSpace(entry.LastLocation) ? entry.LastLocation : "локация не указана");
            var creatorText = "(автор неизвестен)";

                        // 🩹 rest-per-reminder: приоритет — LastCreatorId из стора (запомнили на Created
                        // или LastUpdatedAnnounceAsync). Это убирает на каждом reminder блокирующий
                        // REST-запрос GetGuildAsync + GetEventAsync. Раньше они шли всегда, и при
                        // медленном REST reminders скапливались на dispatcher'е. Если LastCreatorId
                        // неизвестен (старые записи до этого изменения) — fallback на REST, но с
                        // таймаутом 2с (Task.WhenAny), чтобы не блокировать DM/TG-рассылку.
                        if (entry.LastCreatorId is ulong storedCreatorId && storedCreatorId != 0)
                        {
                            creatorText = MentionUtils.MentionUser(storedCreatorId);
            }
                        else
                        {
                            var restTask = TryFetchCreatorViaRestAsync(client, entry);
                            var timeoutTask = Task.Delay(2000);
                            var winner = await Task.WhenAny(restTask, timeoutTask);
                            if (winner == restTask)
                            {
                                var fetched = await restTask;
                                if (fetched is ulong fetchedId && fetchedId != 0)
                                {
                                    creatorText = MentionUtils.MentionUser(fetchedId);
                                    // Запомним на будущее (чтобы следующие reminders этого же
                                    // события уже не дёргали REST).
                                    entry.LastCreatorId = fetchedId;
                                }
                            }
                            else
                            {
                                BotLogger.Warn(LogCategory.Discord,
                                    $"[EventOpsLifecycle] SendReminderAsync: REST creator-lookup таймаут 2с guild={entry.GuildId} event={entry.EventId}, шлём без создателя");
                            }
                        }

            // ── Discord DM: подписчикам события (event-notify.json) ────────────
            // Раньше брали только тех, у кого уже есть запись в DmMessageIdsByUserId
            // (кэш «кому дошёл оригинальный анонс в DM»). Это работало только если
            // оригинальный анонс в DM дошёл. Если он упал (бот рестартовался между
            // отправкой в канал и рассылкой DM, или DM не доставились), то
            // DmMessageIdsByUserId оставался пустым и reminder в DM не уходил
            // никому, хотя подписчики были (Bug: reminder-dm-skipped-when-original-dm-failed).
            //
            // Правильно: reminder в DM — это персональное уведомление подписчикам
            // события, а не follow-up уже разосланного анонса. Список берём из
            // EventNotificationService.GetActiveSubscribers(guildId) — это
            // /var/lib/rpbot/Data/event-notify.json. Fallback на DmMessageIdsByUserId
            // оставлен для обратной совместимости (если в сторе reminder-а нет
            // записи о подписчиках — например, для старых событий до миграции).
            //
            // 🩹 Bug: per-channel-already-sent-skip. Каналы reminder'а независимы:
            // раньше использовался общий WasReminderAlreadySent, и если хоть один
            // из трёх (DM / канал анонса / Telegram) уже отправлял — skip по всем.
            // Это приводило к ситуации, когда после рестарта бот уже отправил
            // reminder в канал анонса и TG (без DM), и DM не уходил никому, хотя
            // подписчики были. Теперь: каждый канал проверяется отдельно, и если
            // в нём ещё не отправляли — отправляем только туда.
            var dmSent = new Dictionary<ulong, ulong>();
            var dmAlreadySent = entry.ReminderDmMessageIdsByUserId != null
                                && entry.ReminderDmMessageIdsByUserId.Count > 0;
            var client2 = client;
            var entryRef = entry;

            if (dmAlreadySent)
            {
                BotLogger.Info(LogCategory.Discord,
                    $"[EventOpsLifecycle] SendReminderAsync: skip DM (уже отправлялся ранее, ids={entry.ReminderDmMessageIdsByUserId!.Count}) guild={entry.GuildId} event={entry.EventId}");
            }
            else
            {
                var dmRecipients = new HashSet<ulong>();
                try
                {
                    foreach (var uid in _eventNotifications.GetActiveSubscribers(guild.Id))
                        dmRecipients.Add(uid);
                }
                catch (Exception exSub)
                {
                    BotLogger.Warn(LogCategory.Discord,
                        $"[EventOpsLifecycle] SendReminderAsync: GetActiveSubscribers failed: {exSub.Message}");
                }
                // Fallback: добавим тех, кому дошёл оригинальный DM-анонс (на случай,
                // если у них была подписка, но по какой-то причине пропала из
                // event-notify.json — чтобы reminder хотя бы дублировал канал/ТГ).
                if (entry.DmMessageIdsByUserId != null)
                {
                    foreach (var kv in entry.DmMessageIdsByUserId)
                        dmRecipients.Add(kv.Key);
                }

                if (dmRecipients.Count == 0)
                {
                    BotLogger.Info(LogCategory.Discord,
                        $"[EventOpsLifecycle] SendReminderAsync: нет подписчиков на DM-напоминания guild={entry.GuildId} event={entry.EventId} (event-notify.json пуст и оригинальный DM-анонс никому не уходил)");
                }

                foreach (var userId in dmRecipients)
                {
                    try
                    {
                        var user = guild.GetUser(userId) as IUser ?? await TryGetUserAsync(client2, userId);
                        if (user == null) continue;

                        var dm = await user.CreateDMChannelAsync();
                        var dmEmbed = new EmbedBuilder()
                            .WithTitle("⏰ Напоминание о событии")
                            .WithDescription(
                                $"**Напоминание:** {BuildReminderPhrase(entry, $"**{eventName}**")} — не пропустите!")
                            .WithColor(Color.Orange)
                            .AddField("🕒 Когда", startUnix > 0 ? $"<t:{startUnix}:F>" : "—", true)
                            .AddField("📍 Где", whereText, true)
                            .AddField("👤 Создал", creatorText, true)
                            .WithUrl(eventUrl)
                            .WithFooter("Через 15 минут после начала события это сообщение будет автоматически удалено")
                            .WithCurrentTimestamp()
                            .Build();

                        var dmMsg = await dm.SendMessageAsync(embed: dmEmbed);
                        dmSent[userId] = dmMsg.Id;

                        BotLogger.Info(LogCategory.Discord,
                            $"[EventOpsLifecycle] reminder DM отправлен user={userId} guild={entryRef.GuildId} event={entryRef.EventId}");
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Warn(LogCategory.Discord,
                            $"[EventOpsLifecycle] reminder DM error user={userId}: {ex.Message}");
                    }
                }
            }

            // ── Discord: основной канал анонса (рядом с анонсом события) ──────
            // Шлём отдельным сообщением — анонс события не трогаем. Через 15 мин
            // после ActualStartTimeUtc это сообщение удаляется.
            //
            // 🩹 per-channel-already-sent-skip: если ReminderAnnounceMessageId != 0
            // (уже слали в этот канал в этой сессии или предыдущей) — skip,
            // чтобы при ручном сбросе одного канала (например, только DM) не
            // дублировать в канале анонса. Аналогично для Telegram-блока ниже.
            ulong? announceReminderMsgId = null;
            if (entry.ReminderAnnounceMessageId != 0)
            {
                BotLogger.Info(LogCategory.Discord,
                    $"[EventOpsLifecycle] SendReminderAsync: skip канал анонса (уже отправлялся ранее, msg={entry.ReminderAnnounceMessageId}) guild={entry.GuildId} event={entry.EventId}");
            }
            else if (entry.AnnounceChannelId != 0)
            {
                try
                {
                    var announceCh = await client.GetChannelAsync(entry.AnnounceChannelId) as ITextChannel;
                    if (announceCh != null)
                    {
                        var announceEmbed = new EmbedBuilder()
                            .WithTitle("⏰ Напоминание о событии")
                            .WithDescription(
                                $"**Напоминание:** {BuildReminderPhrase(entry, $"**{eventName}**")}, не пропустите!")
                            .WithColor(Color.Orange)
                            .AddField("🕒 Когда", startUnix > 0 ? $"<t:{startUnix}:F>" : "—", true)
                            .AddField("📍 Где", whereText, true)
                            .AddField("👤 Создал", creatorText, true)
                            .WithUrl(eventUrl)
                            .WithFooter("Через 15 минут после начала события это сообщение будет удалено автоматически")
                            .WithCurrentTimestamp()
                            .Build();

                        var announceMsg = await announceCh.SendMessageAsync(embed: announceEmbed);
                        announceReminderMsgId = announceMsg.Id;

                        BotLogger.Info(LogCategory.Discord,
                            $"[EventOpsLifecycle] reminder в канал анонса отправлен channel={entry.AnnounceChannelId} guild={entry.GuildId} event={entry.EventId} msg={announceMsg.Id}");
                    }
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.Discord,
                        $"[EventOpsLifecycle] reminder в канал анонса error: {ex.Message}");
                }
            }

            // Сохраняем все ID напоминаний в запись стора (reminder DM, reminder Telegram,
            // reminder в канале), чтобы потом через 15 мин после старта всё корректно удалить.
            {
                var updated = _store.TryGet(entry.GuildId, entry.EventId) ?? entry;
                if (dmSent.Count > 0)
                    updated.ReminderDmMessageIdsByUserId = new Dictionary<ulong, ulong>(dmSent);
                if (announceReminderMsgId.HasValue)
                    updated.ReminderAnnounceMessageId = announceReminderMsgId.Value;
                            // 🩹 rest-per-reminder: подхватить LastCreatorId, если только что
                            // узнали его через REST-fallback. Следующие reminders этого же
                            // события (если такие будут) уже не дёрнут REST.
                            if (entry.LastCreatorId is null && updated.LastCreatorId is null
                                && creatorText != "(автор неизвестен)"
                                && creatorText.StartsWith("<@") && creatorText.EndsWith(">"))
                            {
                                var idText = creatorText.Substring(2, creatorText.Length - 3);
                                if (ulong.TryParse(idText, out var cid))
                                    updated.LastCreatorId = cid;
                            }
                                                // reminder уже отправлен — чистим Reminder1hAtUtc, чтобы
                                                // RebuildFromStoreAsync не прислал его повторно после рестарта.
                                                updated.Reminder1hAtUtc = null;
                                                _store.UpdateEntry(updated);
                                            }

            // ── Telegram: в канал/топик из ServerConfig ───────────────────────
            // Telegram reminder: время в МСК-формате (как в основном анонсе),
            // «Где» — plain text в стиле основного анонса (BuildStatusTelegramText).
            //
            // Берём liveCfg из ServerConfig, а не снимок из entry.TelegramChatId.
            // ServerConfig может меняться в рантайме (мастер перенастроил топик),
            // и если бы мы слали в снимок — reminder уходил бы в старый чат/топик.
            // По аналогии с тем, как уже сделано в EventAnnouncer.
            var liveCfg = _serverConfigsProvider()?.TryGetValue(entry.GuildId, out var lc) == true ? lc : null;
            var tgChatId = liveCfg?.TelegramChatId ?? entry.TelegramChatId;
            var tgThreadId = liveCfg?.TelegramMessageThreadId ?? entry.TelegramMessageThreadId;
            if (entry.ReminderTelegramMessageId != 0 && entry.ReminderTelegramChatId != 0)
            {
                BotLogger.Info(LogCategory.Discord,
                    $"[EventOpsLifecycle] SendReminderAsync: skip Telegram (уже отправлялся ранее, msg={entry.ReminderTelegramMessageId}) guild={entry.GuildId} event={entry.EventId}");
            }
            else if (_telegramNotifier != null && tgChatId != 0)
            {
                try
                {
                    // «Когда»: если есть UTC-старт — конвертируем в МСК; формат
                    // совпадает с EventAnnouncer.BuildStatusTelegramText (dd.MM.yyyy HH-mm по МСК).
                    string whenText = "—";
                    if (entry.LastStartTimeUtc.HasValue)
                    {
                        var utc = entry.LastStartTimeUtc.Value.UtcDateTime;
                        if (MoscowTime.TryConvertFromUtc(utc, out var msk))
                            whenText = msk.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " (по МСК)";
                        else
                            whenText = entry.LastStartTimeUtc.Value.LocalDateTime.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
                    }

                    // «Где»: в стиле основного анонса (BuildStatusTelegramText) —
                    // plain text «Guild → #channel-name» либо текстовая локация,
                    // без HTML-обёрток. TelegramNotifier.BuildSendMessagePayload
                    // экранирует все <, >, & для parse_mode=HTML, поэтому
                    // передавать сюда HTML-теги бессмысленно — придут как
                    // текст «<b>...</b>». Делаем единый стиль с основным анонсом.
                    string whereTextTg;
                    if (entry.LastChannelId is ulong tgChId && tgChId != 0)
                    {
                        string channelDisplay = tgChId.ToString();
                        try
                        {
                            var sockCh = await client.GetChannelAsync(tgChId) as SocketChannel;
                            if (sockCh is IGuildChannel gc && !string.IsNullOrWhiteSpace(gc.Name))
                                channelDisplay = gc.Name;
                        }
                        catch { /* имя канала недоступно — оставляем id */ }
                                                whereTextTg = channelDisplay;
                    }
                    else if (!string.IsNullOrWhiteSpace(entry.LastLocation))
                    {
                        whereTextTg = entry.LastLocation!;
                    }
                    else
                    {
                        whereTextTg = "(локация не указана)";
                    }

                    // Стиль — единый с другими TG-анонсами (см. EventAnnouncer.BuildEventTelegramText):
                                        // жирные метки «Когда»/«Где», кликабельная ссылка. parse_mode=HTML.
                                        // Экранирование &, <, > внутри пользовательских данных выполняет
                                        // сам TelegramNotifier.BuildSendMessagePayload — поэтому здесь
                                        // подставляем eventName/whenText/whereTextTg/eventUrl без
                                        // предварительного EscapeHtml (двойное экранирование ломает вывод).
                                        var tgText =
                                                            $"⏰ <b>Напоминание:</b> {BuildReminderPhrase(entry, $"<b>{eventName}</b>")}, не пропустите!\n\n" +
                                            $"🕒 <b>Когда:</b> {whenText}\n" +
                                            $"📍 <b>Где:</b> {whereTextTg}\n\n" +
                                            $"🔗 <a href=\"{eventUrl}\">Ссылка на событие в Discord</a>";

                    // SendMessageReturningMessageIdAsync: вернёт messageId,
                    // кладём его в ReminderTelegramMessageId чтобы потом удалить.
                    // TelegramNotifier сам возьмёт chatId/threadId из текущего
                    // ServerConfig — entry.TelegramChatId/Thread мы лишь фиксируем
                    // для последующего удаления и аудита.
                    var tgMsgId = await _telegramNotifier.SendMessageReturningMessageIdAsync(
                        entry.GuildId, tgText);

                    if (tgMsgId.HasValue)
                    {
                        var updated = _store.TryGet(entry.GuildId, entry.EventId) ?? entry;
                        updated.ReminderTelegramMessageId = tgMsgId.Value;
                        // Фиксируем chat/thread, в который реально ушёл reminder
                        // (liveCfg или fallback к entry). При последующем удалении
                        // через DeleteReminderMessagesAsync нужно знать, откуда
                        // удалять — без этого мы можем попытаться удалить сообщение
                        // не из того чата.
                        updated.ReminderTelegramChatId = tgChatId;
                        updated.ReminderTelegramThreadId = tgThreadId;
                        _store.UpdateEntry(updated);

                        BotLogger.Info(LogCategory.Discord,
                            $"[EventOpsLifecycle] reminder Telegram отправлен guild={entry.GuildId} event={entry.EventId} chat={tgChatId} thread={tgThreadId} msg={tgMsgId.Value}");
                    }
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.Discord,
                        $"[EventOpsLifecycle] reminder Telegram error: {ex.Message}");
                }
            }
        }

        private async Task DeleteReminderMessagesAsync(EventAnnouncementEntry entry)
        {
            var client = SafeGetClient();
            if (client == null) return;

            // ── Discord: канал анонса (удаляем именно reminder-сообщение) ──────
            if (entry.ReminderAnnounceMessageId != 0 && entry.AnnounceChannelId != 0)
            {
                try
                {
                    var ch = await client.GetChannelAsync(entry.AnnounceChannelId) as ITextChannel;
                    if (ch != null)
                    {
                        var msg = await ch.GetMessageAsync(entry.ReminderAnnounceMessageId) as IUserMessage;
                        if (msg != null)
                        {
                            await msg.DeleteAsync();
                            BotLogger.Info(LogCategory.Discord,
                                $"[EventOpsLifecycle] reminder в канале анонса удалён channel={entry.AnnounceChannelId} msg={entry.ReminderAnnounceMessageId} guild={entry.GuildId} event={entry.EventId}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ex.Message?.Contains("10008", StringComparison.OrdinalIgnoreCase) == true)
                        goto SkipAnnounceDelete;
                    BotLogger.Warn(LogCategory.Discord,
                        $"[EventOpsLifecycle] reminder announce channel delete error: {ex.Message}");
                }
            }
            SkipAnnounceDelete:;

            // ── Discord DM: удаляем именно DM-напоминания ────────────────────
            if (entry.ReminderDmMessageIdsByUserId != null && entry.ReminderDmMessageIdsByUserId.Count > 0)
            {
                foreach (var kv in entry.ReminderDmMessageIdsByUserId)
                {
                    try
                    {
                        var user = client.GetUser(kv.Key) ?? await TryGetUserAsync(client, kv.Key);
                        if (user == null) continue;

                        var dm = await user.CreateDMChannelAsync();
                        await dm.DeleteMessageAsync(kv.Value);

                        BotLogger.Info(LogCategory.Discord,
                            $"[EventOpsLifecycle] reminder DM удалён user={kv.Key} guild={entry.GuildId} event={entry.EventId}");
                    }
                    catch (Exception ex)
                    {
                        // 10008 (Unknown Message) — уже удалено, это ок.
                        if (ex.Message?.Contains("10008", StringComparison.OrdinalIgnoreCase) == true)
                            continue;
                        BotLogger.Warn(LogCategory.Discord,
                            $"[EventOpsLifecycle] reminder DM delete error user={kv.Key}: {ex.Message}");
                    }
                }
            }

            // ── Telegram: удаляем reminder-сообщение ──────────────────────────
            if (_telegramNotifier != null
                && entry.ReminderTelegramMessageId > 0
                && entry.ReminderTelegramChatId != 0)
            {
                try
                {
                    await _telegramNotifier.DeleteMessageAsync(entry.GuildId, entry.ReminderTelegramMessageId);
                    BotLogger.Info(LogCategory.Discord,
                        $"[EventOpsLifecycle] reminder Telegram удалён guild={entry.GuildId} event={entry.EventId}");
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.Discord,
                        $"[EventOpsLifecycle] reminder Telegram delete error: {ex.Message}");
                }
            }

            // Зачищаем поля.
            var updated = _store.TryGet(entry.GuildId, entry.EventId) ?? entry;
            updated.ReminderDmMessageIdsByUserId = null;
            updated.ReminderTelegramMessageId = 0;
            updated.ReminderTelegramChatId = 0;
            updated.ReminderTelegramThreadId = 0;
            updated.ReminderAnnounceMessageId = 0;
                        // 🩹 reminder-survives-restart: reminder-месседжи удалены — фиксируем факт
                        // удаления, обнулив абсолютный момент в сторе. Без этого RebuildFromStoreAsync
                        // мог бы повторно «удалить» уже удалённые месседжи на следующем рестарте.
                        updated.DeleteReminder15mAtUtc = null;
                        _store.UpdateEntry(updated);
                    }

                    /// <summary>
                    /// Дослать анонс для записи со статусом "pending_announce" (Created пришёл,
                    /// но в прошлую сессию Discord-announce не ушёл — giveup из-за DNS-fail /
                    /// реконнекта). Используется только из RebuildFromStoreAsync.
                    ///
                    /// Стратегия: минимальная, без обращений к приватным методам Announcer.
                    /// 1) Снимаем "pending_announce" — entry считается «обычной Scheduled».
                    /// 2) Lifecycle уже поставил reminder1h в HandleCreatedAsync при Created —
                    ///    он не зависит от наличия embed в канале (см. SendReminderAsync).
                    /// 3) Discord пришлёт следующее Updated / Started / Completed для события —
                    ///    тогда Announcer отредактирует или отправит embed заново.
                    ///
                    /// Если Discord Updated/Started не придёт (событие отменено без апдейта)
                    /// — запись будет удалена через cleanup24h по ветке abandoned ниже.
                    /// </summary>
                    private async Task ReconcilePendingAnnounceAsync(EventAnnouncementEntry entry)
                    {
                        try
                        {
                            entry.LastUpdatedMark = null;
                            try { _store.UpdateEntry(entry); } catch { }
                            BotLogger.Info(LogCategory.Discord,
                                $"[EventOpsLifecycle] ReconcilePendingAnnounceAsync: pending_announce снят (reminder уже запланирован), ждём следующего Updated/Started/Completed от Discord guild={entry.GuildId} event={entry.EventId}");
                            await Task.CompletedTask;
                        }
                        catch (Exception ex)
                        {
                            BotLogger.Warn(LogCategory.Discord,
                                $"[EventOpsLifecycle] ReconcilePendingAnnounceAsync: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

        private async Task CleanupAnnouncementAsync(EventAnnouncementEntry entry)
        {
            var client = SafeGetClient();
            if (client == null)
            {
                BotLogger.Warn(LogCategory.Discord,
                    $"[EventOpsLifecycle] CleanupAnnouncementAsync: client not connected, отложим");
                // Если клиент не подключён — оставляем таймер не активным.
                // При следующем Ready цикл RebuildFromStoreAsync поднимет запись снова.
                return;
            }

            // ── Discord: удаляем анонс в канале ───────────────────────────────
            if (entry.AnnounceChannelId != 0 && entry.AnnounceMessageId != 0)
            {
                try
                {
                    var ch = await client.GetChannelAsync(entry.AnnounceChannelId) as ITextChannel;
                    if (ch != null)
                    {
                        var msg = await ch.GetMessageAsync(entry.AnnounceMessageId) as IUserMessage;
                        if (msg != null)
                        {
                            await msg.DeleteAsync();
                            BotLogger.Info(LogCategory.Discord,
                                $"[EventOpsLifecycle] cleanup: анонс удалён channel={entry.AnnounceChannelId} msg={entry.AnnounceMessageId} guild={entry.GuildId} event={entry.EventId}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.Discord,
                        $"[EventOpsLifecycle] cleanup Discord channel error: {ex.Message}");
                }
            }

            // ── Discord DM: удаляем оригинальные DM-копии анонса ──────────────
            if (entry.DmMessageIdsByUserId != null && entry.DmMessageIdsByUserId.Count > 0)
            {
                foreach (var kv in entry.DmMessageIdsByUserId)
                {
                    try
                    {
                        var user = client.GetUser(kv.Key) ?? await TryGetUserAsync(client, kv.Key);
                        if (user == null) continue;

                        var dm = await user.CreateDMChannelAsync();
                        await dm.DeleteMessageAsync(kv.Value);
                    }
                    catch (Exception ex)
                    {
                        // 10008 — уже удалено, ок.
                        if (ex.Message?.Contains("10008", StringComparison.OrdinalIgnoreCase) == true)
                            continue;
                        BotLogger.Warn(LogCategory.Discord,
                            $"[EventOpsLifecycle] cleanup DM error user={kv.Key}: {ex.Message}");
                    }
                }
            }

            // ── Telegram: удаляем анонс ───────────────────────────────────────
            // ⏸ ВРЕМЕННАЯ ПРАВКА (2026-10-19, по решению владельца): удаление ТГ-анонса
            // при cleanup24h отключено — анонс остаётся в Telegram навсегда после
            // статуса «событие завершено». Для отката вернуть tgCleanup24hEnabled=true.
            const bool tgCleanup24hEnabled = false;
            if (_telegramNotifier != null
                && tgCleanup24hEnabled
                && entry.TelegramMessageId > 0
                && entry.TelegramChatId != 0)
            {
                // 🩹 Bug cleanup-tg-silent-fail: в Telegram Bot API
                // deleteMessage возвращает «Bad Request: message can't
                // be deleted» в двух случаях:
                //   1) сообщение опубликовано НЕ от имени бота
                //      (sender_chat_id или forward);
                //   2) сообщению в канале больше 48 часов
                //      (ограничение Telegram API: в каналах бот может
                //      удалять только сообщения не старше 48ч).
                // Раньше результат DeleteMessageAsync игнорировался, и в
                // логе неизменно писалось «Telegram анонс удалён» даже
                // при ok=false — в дашборде анонс пропадал, а в
                // Telegram-канале продолжал висеть без следов cleanup.
                //
                // Стратегия (v4): основной сценарий — deleteMessage
                // (как было задумано изначально: анонс должен
                // удалиться, а не превращаться в «❌ Анонс удалён…» в
                // ленте). Только если Telegram Bot API отказал в
                // удалении (ok=false) — например, истёк 48ч лимит
                // для каналов или сообщение опубликовано не от бота —
                // пробуем editMessageText → editMessageCaption как
                // fallback, чтобы подписчики видели явный след
                // (а не фантомный «живой» анонс события, которое уже
                // давно закончилось). В каждой ветке — детальное
                // логирование для разбора инцидентов.
                var markerText = "❌ Анонс удалён (cleanup 24ч; " +
                                 $"event={entry.EventId}, было опубликовано " +
                                 "и закрыто автоматически)";
                var cleanupDone = false;
                try
                {
                    var ok = await _telegramNotifier.DeleteMessageAsync(entry.GuildId, entry.TelegramMessageId);
                    if (ok)
                    {
                        BotLogger.Info(LogCategory.Discord,
                            $"[EventOpsLifecycle] cleanup: Telegram анонс удалён guild={entry.GuildId} event={entry.EventId} msg={entry.TelegramMessageId}");
                        cleanupDone = true;
                    }
                    else
                    {
                        BotLogger.Warn(LogCategory.Discord,
                            $"[EventOpsLifecycle] cleanup: Telegram delete вернул ok=false, пробуем edit-fallback (вероятно, истёк 48ч лимит) guild={entry.GuildId} event={entry.EventId} msg={entry.TelegramMessageId} chat={entry.TelegramChatId} thread={entry.TelegramMessageThreadId}");
                    }
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.Discord,
                        $"[EventOpsLifecycle] cleanup Telegram delete error: {ex.Message}");
                }

                if (!cleanupDone)
                {
                    // delete не сработал. Пробуем editMessageText
                    // (для текстовых сообщений). Если и он вернёт
                    // ok=false (например, фото-сообщение без
                    // подписи — «there is no text in the message to
                    // edit») — пробуем editMessageCaption: для
                    // фото-сообщений caption — это «текст подписи»,
                    // и метод работает даже за пределами 48ч лимита
                    // на удаление.
                    var editSucceeded = false;
                    try
                    {
                        editSucceeded = await _telegramNotifier.EditMessageTextAsync(
                            entry.GuildId, entry.TelegramMessageId, markerText);
                        if (editSucceeded)
                        {
                            BotLogger.Info(LogCategory.Discord,
                                $"[EventOpsLifecycle] cleanup: Telegram анонс отредактирован (text, fallback после delete fail) guild={entry.GuildId} event={entry.EventId} msg={entry.TelegramMessageId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Warn(LogCategory.Discord,
                            $"[EventOpsLifecycle] cleanup Telegram editText error: {ex.Message}");
                    }

                    if (!editSucceeded)
                    {
                        try
                        {
                            editSucceeded = await _telegramNotifier.EditMessageCaptionAsync(
                                entry.GuildId, entry.TelegramMessageId, markerText);
                            if (editSucceeded)
                            {
                                BotLogger.Info(LogCategory.Discord,
                                    $"[EventOpsLifecycle] cleanup: Telegram анонс отредактирован (caption, fallback после delete fail) guild={entry.GuildId} event={entry.EventId} msg={entry.TelegramMessageId}");
                            }
                        }
                        catch (Exception ex)
                        {
                            BotLogger.Warn(LogCategory.Discord,
                                $"[EventOpsLifecycle] cleanup Telegram editCaption error: {ex.Message}");
                        }
                    }

                    if (!editSucceeded)
                    {
                        // Самый плохой сценарий: ни delete, ни edit
                        // не сработали. Анонс в канале, бот не может
                        // его ни удалить (>48ч), ни отредактировать
                        // (не свой sender_chat). Логируем
                        // максимально подробно, чтобы при разборе
                        // инцидентов было сразу видно, что нужно
                        // сделать руками.
                        BotLogger.Warn(LogCategory.Discord,
                            $"[EventOpsLifecycle] cleanup: Telegram анонс НЕ удалён и НЕ отредактирован (проверьте руками) guild={entry.GuildId} event={entry.EventId} msg={entry.TelegramMessageId} chat={entry.TelegramChatId} thread={entry.TelegramMessageThreadId} tip=вероятно истёк 48ч лимит или сообщение опубликовано не от бота");
                    }
                }
            }

                        // ── Bug #12: финал — стор и таймеры удаляем ПОСЛЕ всех Discord/TG/DM
                        // удалений. Если что-то из вышестоящего упало (например, нет сети при
                        // удалении из TG) — стейт остаётся в сторе, и при следующем рестарте
                        // RebuildFromStoreAsync снова выполнит cleanup24h для этой записи.
                        // Раньше _store.Remove стоял безусловно, и при рестарте мы теряли
                        // информацию о необходимости повторить удаление. ───────────────────
                        _store.Remove(entry.GuildId, entry.EventId);

                        if (_timers.TryRemove((entry.GuildId, entry.EventId), out var slot))
                        {
                            slot.Cleanup24hCts?.Dispose();
                            slot.Reminder1hCts?.Dispose();
                            slot.DeleteReminder15mCts?.Dispose();
                        }
                    }

        // ════════════════════════════════════════════════════════════════════
        //  Хелперы
        // ════════════════════════════════════════════════════════════════════

        private DiscordSocketClient? SafeGetClient()
        {
            try
            {
                var c = _clientProvider();
                if (c == null) return null;
                if (c.ConnectionState != ConnectionState.Connected) return null;
                return c;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<IUser?> TryGetUserAsync(DiscordSocketClient client, ulong userId)
        {
            try { return await client.Rest.GetUserAsync(userId); }
            catch { return null; }
        }

                /// <summary>
                /// Получить ID создателя Discord-события через REST.
                /// Возвращает ulong? — null при любом сбое, 0 если Creator пуст.
                /// Используется как fallback в <c>SendReminderAsync</c>, если в сторе
                /// ещё нет LastCreatorId (записи, сохранённые до этого изменения).
                /// </summary>
                private static async Task<ulong?> TryFetchCreatorViaRestAsync(DiscordSocketClient client, EventAnnouncementEntry entry)
                {
                    try
                    {
                        var restGuild = await client.Rest.GetGuildAsync(entry.GuildId);
                        var restEvent = await restGuild.GetEventAsync(entry.EventId);
                        if (restEvent?.Creator != null)
                            return restEvent.Creator.Id;
                    }
                    catch { /* REST не ответил — это не критично */ }
                    return null;
                }

        private sealed class ScheduledTimers
        {
                    // Lock-объект для безопасной перестановки CTS. Нужен потому, что
                    // ScheduleReminder1h/ScheduleCleanup24h/ScheduleDeleteReminder15m
                    // читают текущий CTS, делают Cancel+Dispose, и пишут новый — три
                    // независимые операции, между которыми может вклиниться параллельный
                    // вызов из duplicate resync / OnUpdatedAsync. Без lock второй
                    // вызов отменит только что созданный CTS первого, и reminder не
                    // уйдёт (race-condition #7).
                    public readonly object Lock = new();
                    public CancellationTokenSource? Reminder1hCts;
                    public DateTimeOffset? Reminder1hAtUtc;
                    public CancellationTokenSource? DeleteReminder15mCts;
                    public CancellationTokenSource? Cleanup24hCts;
                }
    }
}
