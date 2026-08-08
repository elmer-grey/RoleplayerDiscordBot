using Discord.WebSocket;

namespace RPBot.EventOps
{
    public sealed class EventOpsContext
    {
        public required EventOperationKind Operation { get; init; }
        public required SocketGuildEvent CurrentEvent { get; init; }
        public SocketGuildEvent? PreviousEvent { get; init; }
        public EventAnnouncementEntry? AnnouncementEntry { get; init; }
        public string? StatusText { get; init; }
    }
}
