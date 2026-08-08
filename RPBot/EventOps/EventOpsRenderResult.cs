namespace RPBot.EventOps
{
    public sealed class EventOpsRenderResult
    {
        public required string DiscordTitle { get; init; }
        public required string TelegramTitle { get; init; }
        public string? ShortSummary { get; init; }
    }
}
