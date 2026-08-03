using System;

namespace RPBot
{
    /// <summary>
    /// Вспомогательные методы для форматирования времени в сообщениях Discord.
    /// Discord автоматически отображает такие теги в часовом поясе пользователя.
    /// </summary>
    public static class DiscordTimeFormatter
    {
        /// <summary>
        /// Возвращает Discord-таймстемп в формате "дата и время" (например, "12 января 2025 г. 18:30").
        /// </summary>
        public static string FullDateTime(DateTime dateTime)
        {
            return $"<t:{ToUnixSeconds(dateTime)}:f>";
        }

        /// <summary>
        /// Возвращает Discord-таймстемп в формате "дата и время" для DateTimeOffset.
        /// </summary>
        public static string FullDateTime(DateTimeOffset dateTime)
        {
            return $"<t:{dateTime.ToUnixTimeSeconds()}:f>";
        }

        /// <summary>
        /// Возвращает Discord-таймстемп в относительном формате (например, "через 2 часа").
        /// </summary>
        public static string Relative(DateTime dateTime)
        {
            return $"<t:{ToUnixSeconds(dateTime)}:R>";
        }

        /// <summary>
        /// Возвращает Discord-таймстемп в относительном формате для DateTimeOffset.
        /// </summary>
        public static string Relative(DateTimeOffset dateTime)
        {
            return $"<t:{dateTime.ToUnixTimeSeconds()}:R>";
        }

        /// <summary>
        /// Возвращает только время (например, "18:30").
        /// </summary>
        public static string TimeOnly(DateTime dateTime)
        {
            return $"<t:{ToUnixSeconds(dateTime)}:t>";
        }

        /// <summary>
        /// Возвращает только время для DateTimeOffset.
        /// </summary>
        public static string TimeOnly(DateTimeOffset dateTime)
        {
            return $"<t:{dateTime.ToUnixTimeSeconds()}:t>";
        }

        private static long ToUnixSeconds(DateTime dateTime)
        {
            var utc = dateTime.Kind == DateTimeKind.Utc ? dateTime : dateTime.ToUniversalTime();
            return new DateTimeOffset(utc).ToUnixTimeSeconds();
        }
    }
}
