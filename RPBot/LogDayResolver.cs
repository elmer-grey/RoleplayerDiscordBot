using System;

namespace RPBot;

/// <summary>
/// Разрешает «логический день» для логов.
///
/// День логов начинается в <see cref="CutoffHour"/>:00 локального времени.
/// Запись с timestamp <c>23.09 05:59</c> относится к логу за <c>22.09</c>;
/// запись с timestamp <c>23.09 06:00</c> — уже к логу за <c>23.09</c>.
///
/// Это нужно, чтобы одна суточная папка <c>Logs/yyyyMMdd/</c> покрывала
/// «человеческие» сутки бота: ночные рестарты, утренние смены, остановки
/// на обслуживание — всё попадает в одну папку, пока бот живёт в этот день.
/// </summary>
public static class LogDayResolver
{
    /// <summary>Час локального времени, с которого начинается «новый день логов» (по умолчанию 6).</summary>
    public const int DefaultCutoffHour = 6;

    /// <summary>
    /// Возвращает идентификатор дня логов (формат yyyyMMdd) для заданного момента.
    /// </summary>
    /// <param name="moment">Локальное время (DateTime.Kind игнорируется — берётся .Hour).</param>
    /// <param name="cutoffHour">Час начала нового дня (0..23).</param>
    public static string ResolveDay(DateTime moment, int cutoffHour = DefaultCutoffHour)
    {
        if (cutoffHour < 0 || cutoffHour > 23)
            throw new ArgumentOutOfRangeException(nameof(cutoffHour), cutoffHour, "Cutoff hour must be in [0..23].");

        // Если момент до cutoffHour — это «вчера» в логическом смысле.
        var day = moment.Date;
        if (moment.Hour < cutoffHour)
            day = day.AddDays(-1);
        return day.ToString("yyyyMMdd");
    }

    /// <summary>
    /// Возвращает момент времени начала логического дня, в который попадает <paramref name="moment"/>.
    /// Например, для cutoff=6 и moment=23.09 14:30 → 23.09 06:00;
    /// для moment=23.09 03:15 → 22.09 06:00.
    /// </summary>
    public static DateTime GetDayStart(DateTime moment, int cutoffHour = DefaultCutoffHour)
    {
        if (cutoffHour < 0 || cutoffHour > 23)
            throw new ArgumentOutOfRangeException(nameof(cutoffHour), cutoffHour, "Cutoff hour must be in [0..23].");
        var day = moment.Date;
        if (moment.Hour < cutoffHour)
            day = day.AddDays(-1);
        return day.AddHours(cutoffHour);
    }
}
