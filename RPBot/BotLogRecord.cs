using System;

namespace RPBot
{
    public sealed record BotLogRecord(
        DateTimeOffset Timestamp,
        LogLevel Level,
        LogCategory Category,
        string Message,
        string FormattedLine);
}
