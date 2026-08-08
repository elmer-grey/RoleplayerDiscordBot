using System;

namespace RPBot
{
    /// <summary>
    /// Утилиты для работы с московским временем (UTC+3).
    /// Используется в Telegram-сообщениях, Google Sheets и прогнозах —
    /// там, где нельзя показать локальное время пользователя.
    /// Discord-сообщения используют DiscordTimeFormatter (<t:…:f/R/t>),
    /// чтобы каждый пользователь видел время в своём часовом поясе.
    /// </summary>
    public static class MoscowTime
    {
        // Кандидаты для идентификатора TZ: IANA (Linux/macOS) и Windows (Windows).
        private static readonly string[] TimeZoneCandidates =
            { "Europe/Moscow", "Russian Standard Time" };

        /// <summary>
        /// Возвращает true, если на системе удалось найти TZ Москвы.
        /// Используется в Program.cs при планировании ежедневного рестарта.
        /// </summary>
        public static bool TryGetTimeZone(out TimeZoneInfo? timeZone)
        {
            foreach (var id in TimeZoneCandidates)
            {
                try
                {
                    timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
                    return true;
                }
                catch
                {
                }
            }

            timeZone = null;
            return false;
        }

        /// <summary>
        /// Преобразует UTC-время в московское. Если TZ не найдена — возвращает UTC.
        /// </summary>
        public static bool TryConvertFromUtc(DateTime utc, out DateTime msk)
        {
            if (TryGetTimeZone(out var tz) && tz != null)
            {
                msk = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
                return true;
            }

            msk = utc;
            return false;
        }

        /// <summary>
        /// Приводит произвольный DateTime (UTC/Local/Unspecified) к московскому времени.
        /// Если TZ не найдена — возвращает входное значение как UTC-конвертированное.
        /// Используется в GoogleSheetsService.BuildRowValues.
        /// </summary>
        public static DateTime Convert(DateTime dt)
        {
            var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            return TryConvertFromUtc(utc, out var msk) ? msk : utc;
        }

        /// <summary>
        /// Возвращает московское время как DateTimeOffset с TZ Москвы.
        /// Если TZ не найдена — возвращает UTC. Удобно для эмбедов и логов.
        /// </summary>
        public static DateTimeOffset ToMoscowOffset(DateTimeOffset utc)
        {
            if (TryGetTimeZone(out var tz) && tz != null)
                return TimeZoneInfo.ConvertTime(utc, tz);
            return utc;
        }
    }
}