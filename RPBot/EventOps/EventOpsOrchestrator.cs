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
                await OnCreatedAsync(guildEvent).ConfigureAwait(false);
        }

        public async Task HandleUpdatedAsync(SocketGuildEvent guildEvent, SocketGuildEvent? previous)
        {
            BuildAndTraceContext(EventOperationKind.Updated, guildEvent, previous, null);
            if (OnUpdatedAsync != null)
                await OnUpdatedAsync(guildEvent, previous).ConfigureAwait(false);
        }

        public async Task HandleStartedAsync(SocketGuildEvent guildEvent)
        {
            BuildAndTraceContext(EventOperationKind.Started, guildEvent, null, "started");
            if (OnStartedAsync != null)
                await OnStartedAsync(guildEvent).ConfigureAwait(false);
        }

        public async Task HandleCancelledAsync(SocketGuildEvent guildEvent)
        {
            BuildAndTraceContext(EventOperationKind.Cancelled, guildEvent, null, "cancelled");
            if (OnCancelledAsync != null)
                await OnCancelledAsync(guildEvent).ConfigureAwait(false);
        }

        public async Task HandleCompletedAsync(SocketGuildEvent guildEvent)
        {
            BuildAndTraceContext(EventOperationKind.Completed, guildEvent, null, "completed");
            if (OnCompletedAsync != null)
                await OnCompletedAsync(guildEvent).ConfigureAwait(false);
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
