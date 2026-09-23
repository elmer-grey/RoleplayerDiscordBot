using System;
using System.Threading.Tasks;
using Discord.WebSocket;

namespace RPBot.EventOps
{
    public sealed class EventOpsOrchestrator
    {
        private readonly EventOpsRenderer _renderer;
        private readonly EventAnnouncementStore _store;

        public EventOpsOrchestrator(EventOpsRenderer renderer, EventAnnouncementStore store)
        {
            _renderer = renderer;
            _store = store;
        }

        public Func<SocketGuildEvent, Task>? OnCreatedAsync { get; set; }
        public Func<SocketGuildEvent, SocketGuildEvent?, Task>? OnUpdatedAsync { get; set; }
        public Func<SocketGuildEvent, Task>? OnStartedAsync { get; set; }
        public Func<SocketGuildEvent, Task>? OnCancelledAsync { get; set; }
        public Func<SocketGuildEvent, Task>? OnCompletedAsync { get; set; }

        public async Task HandleCreatedAsync(SocketGuildEvent guildEvent)
        {
            BuildAndTraceContext(EventOperationKind.Created, guildEvent, null, null);
            if (OnCreatedAsync != null)
                await SafeInvokeAsync(OnCreatedAsync, guildEvent, "Created").ConfigureAwait(false);
        }

        public async Task HandleUpdatedAsync(SocketGuildEvent guildEvent, SocketGuildEvent? previous)
        {
            BuildAndTraceContext(EventOperationKind.Updated, guildEvent, previous, null);
            if (OnUpdatedAsync != null)
                await SafeInvokeUpdatedAsync(OnUpdatedAsync, guildEvent, previous, "Updated").ConfigureAwait(false);
        }

        public async Task HandleStartedAsync(SocketGuildEvent guildEvent)
        {
            BuildAndTraceContext(EventOperationKind.Started, guildEvent, null, "started");
            if (OnStartedAsync != null)
                await SafeInvokeAsync(OnStartedAsync, guildEvent, "Started").ConfigureAwait(false);
        }

        public async Task HandleCancelledAsync(SocketGuildEvent guildEvent)
        {
            BuildAndTraceContext(EventOperationKind.Cancelled, guildEvent, null, "cancelled");
            if (OnCancelledAsync != null)
                await SafeInvokeAsync(OnCancelledAsync, guildEvent, "Cancelled").ConfigureAwait(false);
        }

        public async Task HandleCompletedAsync(SocketGuildEvent guildEvent)
        {
            BuildAndTraceContext(EventOperationKind.Completed, guildEvent, null, "completed");
            if (OnCompletedAsync != null)
                await SafeInvokeAsync(OnCompletedAsync, guildEvent, "Completed").ConfigureAwait(false);
        }

        /// <summary>
        /// Вызывает каждый подписчик multicast-делегата по очереди. Если один из них
        /// бросит исключение (синхронно или через Task) — оно логируется с понятным
        /// сообщением, но остальные подписчики всё равно будут вызваны.
        /// Multicast delegate сам по себе для Func&lt;T, Task&gt; не «протягивает»
        /// исключения дальше первого обработчика: CLR после первого await в Announcer
        /// уже запускает следующего подписчика, и если тот упал, возвращённый Task
        /// становится faulted — orchestrator просто увидит его и выйдет, остальные
        /// подписчики не вызовутся. Мы итерируем GetInvocationList() явно.
        /// </summary>
        private async Task SafeInvokeAsync(Func<SocketGuildEvent, Task>? handler, SocketGuildEvent ev, string opName)
        {
            if (handler == null) return;
            var list = handler.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                var single = (Func<SocketGuildEvent, Task>)list[i];
                try
                {
                    await single(ev).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    BotLogger.Error(LogCategory.Discord,
                                            $"[EventOps] {opName} handler #{i} ({(single.Method.DeclaringType?.Name is { Length: >0 } d ? d + "." + single.Method.Name : single.Method.Name)}) error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private async Task SafeInvokeUpdatedAsync(Func<SocketGuildEvent, SocketGuildEvent?, Task>? handler, SocketGuildEvent ev, SocketGuildEvent? previous, string opName)
        {
            if (handler == null) return;
            var list = handler.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                var single = (Func<SocketGuildEvent, SocketGuildEvent?, Task>)list[i];
                try
                {
                    await single(ev, previous).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    BotLogger.Error(LogCategory.Discord,
                                            $"[EventOps] {opName} handler #{i} ({(single.Method.DeclaringType?.Name is { Length: >0 } d ? d + "." + single.Method.Name : single.Method.Name)}) error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private EventOpsContext BuildAndTraceContext(EventOperationKind kind, SocketGuildEvent current, SocketGuildEvent? previous, string? status)
        {
            EventAnnouncementEntry? entry = null;
            if (current.Guild != null)
            {
                try { entry = _store.TryGet(current.Guild.Id, current.Id); }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.Discord, $"[EventOps] Не удалось получить announcement: {ex.Message}");
                }
            }

            var context = new EventOpsContext
            {
                Operation = kind,
                CurrentEvent = current,
                PreviousEvent = previous,
                AnnouncementEntry = entry,
                StatusText = status,
            };

            var rendered = _renderer.Render(context);
            BotLogger.Debug(LogCategory.Discord, $"[EventOps] {rendered.ShortSummary}");
            return context;
        }
    }
}
