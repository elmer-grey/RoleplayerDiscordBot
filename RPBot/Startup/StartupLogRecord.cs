using System;

namespace RPBot.Startup
{
    // ───── Стартовые каналы ────────────────────────────────────────────
    public enum StartupChannel
    {
        Stage = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
        Header = 4,
        Footer = 5
    }

    public readonly record struct StartupLogRecord(
        DateTime Timestamp,
        StartupChannel Channel,
        string Text);
}
