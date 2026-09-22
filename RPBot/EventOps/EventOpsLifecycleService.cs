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
        private readonly TelegramNotifier? _telegramNotifier;
        private readonly EventNotificationService _eventNotifications;
        private readonly Func<IReadOnlyDictionary<ulong, ServerConfig>> _serverConfigsProvider;

        private readonly ConcurrentDictionary<(ulong guildId, ulong eventId), ScheduledTimers> _timers = new();
        private readonly CancellationTokenSource _shutdownCts = new();

        // Single-flight guard: запрещает двойной запуск RebuildFromStoreAsync при reconnect/Ready.
        private int _running;

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
        /// Запрашивает отмену всех таймеров (для shutdown/OnDisconnected).
        /// Безопасно вызывать многократно.
        /// </summary>
        public void Cancel()
        {
            try { _shutdownCts.Cancel(); } catch { }

                    // ВАЖНО: _shutdownCts сам по себе не связан с per-event CTSs в _timers
                    // (они живут независимыми CancellationTokenSource'ами на каждое
                    // событие). Без явного обхода словаря таймеры продолжали бы
                    // тикать после дисконнекта/рестарта — утечка CTS, дублирующиеся
                    // Task.Delay и потенциально двойные cleanup'ы при повторном Ready
                    // (audit bug #2).
                    foreach (var kv in _timers)
                    {
                        var slot = kv.Value;
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

                int scheduledCleanup = 0, scheduledReminder = 0, skipped = 0;
                foreach (var entry in entries)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        // Нам интересны события, для которых есть анонс в канале
                        // (иначе удалять нечего) и которые НЕ в финальном статусе
                        // (Completed/Cancelled) — для них сразу ставим cleanup24h.
                        var mark = entry.LastUpdatedMark;
                        if (mark == "completed" || mark == "cancelled")
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

                            continue;
                        }

                        // Scheduled/Started: ставим reminder1h (если ещё актуально).
                        if (entry.LastStartTimeUtc.HasValue && ScheduleReminder1h(entry))
                            scheduledReminder++;
                        else
                            skipped++;
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        BotLogger.Error(LogCategory.Discord,
                            $"[EventOpsLifecycle] rebuild entry guild={entry.GuildId} event={entry.EventId}: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                BotLogger.Info(LogCategory.Discord,
                    $"[EventOpsLifecycle] RebuildFromStoreAsync завершено: cleanup={scheduledCleanup}, reminder={scheduledReminder}, skipped={skipped}");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
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
                var entry = _store.TryGet(ev.Guild.Id, ev.Id);
                if (entry == null) return;

                ScheduleReminder1h(entry);
                BotLogger.Info(LogCategory.Discord,
                    $"[EventOpsLifecycle] HandleCreatedAsync guild={ev.Guild.Id} event={ev.Id} (reminder1h поставлен из Created)");
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Discord,
                    $"[EventOpsLifecycle] HandleCreatedAsync error: {ex.Message}");
            }
            await Task.CompletedTask;
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

        private bool ScheduleReminder1h(EventAnnouncementEntry entry)
        {
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

            // Отменяем старый, если был.
            slot.Reminder1hCts?.Cancel();
            slot.Reminder1hCts?.Dispose();

            var cts = new CancellationTokenSource();
            slot.Reminder1hCts = cts;
            slot.Reminder1hAtUtc = reminderAt;

            _ = Task.Run(() => RunReminder1hAsync(entry, cts.Token, delay), cts.Token);

            BotLogger.Info(LogCategory.Discord,
                $"[EventOpsLifecycle] запланирован reminder1h: guild={entry.GuildId} event={entry.EventId} через {(int)delay.TotalMinutes} мин (at {reminderAt:u})");
            return true;
        }

        private void CancelReminder1h(EventAnnouncementEntry entry)
        {
            var key = (entry.GuildId, entry.EventId);
            if (!_timers.TryGetValue(key, out var slot)) return;

            if (slot.Reminder1hCts != null)
            {
                try { slot.Reminder1hCts.Cancel(); } catch { }
                slot.Reminder1hCts.Dispose();
                slot.Reminder1hCts = null;
                slot.Reminder1hAtUtc = null;
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
            if (deleteAt <= nowUtc) return; // Уже прошло.

            var key = (entry.GuildId, entry.EventId);
            var slot = _timers.GetOrAdd(key, _ => new ScheduledTimers());

            slot.DeleteReminder15mCts?.Cancel();
            slot.DeleteReminder15mCts?.Dispose();

            var cts = new CancellationTokenSource();
            slot.DeleteReminder15mCts = cts;

            var delay = deleteAt - nowUtc;
            _ = Task.Run(() => RunDeleteReminder15mAsync(entry, cts.Token, delay), cts.Token);

            BotLogger.Info(LogCategory.Discord,
                $"[EventOpsLifecycle] запланирован deleteReminder15m: guild={entry.GuildId} event={entry.EventId} через {(int)delay.TotalMinutes} мин");
        }

        private void CancelDeleteReminder15m(EventAnnouncementEntry entry)
        {
            var key = (entry.GuildId, entry.EventId);
            if (!_timers.TryGetValue(key, out var slot)) return;

            if (slot.DeleteReminder15mCts != null)
            {
                try { slot.DeleteReminder15mCts.Cancel(); } catch { }
                slot.DeleteReminder15mCts.Dispose();
                slot.DeleteReminder15mCts = null;
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

            slot.Cleanup24hCts?.Cancel();
            slot.Cleanup24hCts?.Dispose();

            var cts = new CancellationTokenSource();
            slot.Cleanup24hCts = cts;

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

        private async Task SendReminderAsync(EventAnnouncementEntry entry)
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

            // Пытаемся достать создателя из REST.
            try
            {
                var restGuild = await client.Rest.GetGuildAsync(entry.GuildId);
                var restEvent = await restGuild.GetEventAsync(entry.EventId);
                if (restEvent?.Creator != null)
                {
                    creatorText = MentionUtils.MentionUser(restEvent.Creator.Id);
                }
            }
            catch { /* REST не ответил — это не критично */ }

            // ── Discord DM: тем, кому дошёл оригинальный анонс ────────────────
            // Шлём только тем, у кого уже есть запись в DmMessageIdsByUserId —
            // иначе мы не знаем, дошла ли им копия.
            var dmSent = new Dictionary<ulong, ulong>();
            var client2 = client;
            var entryRef = entry;
            foreach (var kv in entry.DmMessageIdsByUserId)
            {
                var userId = kv.Key;
                try
                {
                    var user = guild.GetUser(userId) as IUser ?? await TryGetUserAsync(client2, userId);
                    if (user == null) continue;

                    var dm = await user.CreateDMChannelAsync();
                    var dmEmbed = new EmbedBuilder()
                        .WithTitle("⏰ Напоминание о событии")
                        .WithDescription(
                            $"**Напоминание:** через час начнётся событие **{eventName}** — не пропустите!")
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

            // ── Discord: основной канал анонса (рядом с анонсом события) ──────
            // Шлём отдельным сообщением — анонс события не трогаем. Через 15 мин
            // после ActualStartTimeUtc это сообщение удаляется.
            ulong? announceReminderMsgId = null;
            if (entry.AnnounceChannelId != 0)
            {
                try
                {
                    var announceCh = await client.GetChannelAsync(entry.AnnounceChannelId) as ITextChannel;
                    if (announceCh != null)
                    {
                        var announceEmbed = new EmbedBuilder()
                            .WithTitle("⏰ Напоминание о событии")
                            .WithDescription(
                                $"**Напоминание:** через час начнётся событие **{eventName}**, не пропустите!")
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
            if (_telegramNotifier != null && tgChatId != 0)
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
                        whereTextTg = "#" + channelDisplay;
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
                                            $"⏰ <b>Напоминание:</b> через час начнётся событие <b>{eventName}</b>, не пропустите!\n\n" +
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
            _store.UpdateEntry(updated);
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
            if (_telegramNotifier != null
                && entry.TelegramMessageId > 0
                && entry.TelegramChatId != 0)
            {
                try
                {
                    await _telegramNotifier.DeleteMessageAsync(entry.GuildId, entry.TelegramMessageId);
                    BotLogger.Info(LogCategory.Discord,
                        $"[EventOpsLifecycle] cleanup: Telegram анонс удалён guild={entry.GuildId} event={entry.EventId}");
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.Discord,
                        $"[EventOpsLifecycle] cleanup Telegram error: {ex.Message}");
                }
            }

            // Удаляем запись из стора целиком.
            _store.Remove(entry.GuildId, entry.EventId);

            // Удаляем таймеры.
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

        private sealed class ScheduledTimers
        {
            public CancellationTokenSource? Reminder1hCts;
            public DateTimeOffset? Reminder1hAtUtc;
            public CancellationTokenSource? DeleteReminder15mCts;
            public CancellationTokenSource? Cleanup24hCts;
        }
    }
}
