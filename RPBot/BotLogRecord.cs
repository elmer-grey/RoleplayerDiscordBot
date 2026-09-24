using System;

namespace RPBot
{
    /// <summary>
    /// Seq — монотонный идентификатор, проставляемый на стороне WebDashboard
    /// при попадании записи в стрим-буфер. Используется клиентом для
    /// дедупликации (timestamp+message могут совпадать у разных событий).
    /// Для записей, которые WebDashboard никогда не отдавал (например, до
    /// первого запуска дашборда), поле остаётся 0.
    /// </summary>
    public sealed record BotLogRecord(
        DateTimeOffset Timestamp,
        LogLevel Level,
        LogCategory Category,
        string Message,
        string FormattedLine,
        bool IsUser = false,
        long Seq = 0);
}
