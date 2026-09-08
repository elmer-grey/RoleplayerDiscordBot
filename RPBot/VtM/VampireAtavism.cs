using System.Text.Json.Serialization;

namespace RPBot.VtM;

/// <summary>
/// Тип (происхождение) атавизма — помогает отображать последствия правильно.
/// </summary>
/// <remarks>
/// <para>Не путать с самим списком <see cref="VampireCharacter.ActiveAtavisms"/>,
/// где лежит <b>название</b>. Тип описывает <i>откуда</i> атавизм взялся и
/// какие у него последствия для сюжета/механики.</para>
/// </remarks>
public enum AtavismKind
{
    /// <summary>Неизвестно / устаревшие данные без миграции. Показываем как «Гангрел» по умолчанию.</summary>
    Unknown = 0,

    /// <summary>Гангрел: звериные черты при входе в ярость (V20 стр. 95, 268).
    /// Пассивные — пробиваются постепенно, не дают штрафов, но социально мешают.</summary>
    GangrelBeastFeature,

    /// <summary>Людоедство (Diablerie Craving) — все кланы: жажда диаблери при провале
    /// Frenzy с полностью пустой кровью (V20 стр. 322-325). Серьёзный проступок,
    /// падение Humanity + проверка совести 9.</summary>
    DiablerieCraving,

    /// <summary>Берсерк (Rötschreck-induced) — паническая ярость при провале Ротшрека
    /// (V20 стр. 324). Бросает оружие, бьёт союзников, ломает мебель.</summary>
    BerserkPanic,

    /// <summary>Специфический клановый атавизм, не из стандартного списка
    /// (например, Тремер — кровотечение из глаз при Поцелуе). Задаётся рассказчиком.</summary>
    ClanSpecific,
}

/// <summary>
/// Запись активного атавизма с метаданными: имя, тип и когда получен.
/// </summary>
/// <param name="Name">Название атавизма (например, «Пробивающаяся шерсть»).</param>
/// <param name="Kind">Происхождение/тип атавизма.</param>
/// <param name="AcquiredAt">
/// В какой сцене получен. По правилам V20 атавизм живёт до конца истории
/// или снимается рассказчиком. Используем <c>null</c>, если сцена неизвестна
/// (например, для мигрированных записей без метаданных).
/// </param>
public sealed record AtavismEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] AtavismKind Kind,
    [property: JsonPropertyName("at")]   string? AcquiredAt)
{
    /// <summary>Иконка для UI в зависимости от типа.</summary>
    public string Icon => Kind switch
    {
        AtavismKind.GangrelBeastFeature => "🐾",
        AtavismKind.DiablerieCraving    => "🩸",
        AtavismKind.BerserkPanic        => "🌑",
        AtavismKind.ClanSpecific        => "🎭",
        _                                => "❔",
    };

    /// <summary>Краткая подпись типа для UI.</summary>
    public string KindLabel => Kind switch
    {
        AtavismKind.GangrelBeastFeature => "Гангрел",
        AtavismKind.DiablerieCraving    => "Людоедство",
        AtavismKind.BerserkPanic        => "Берсерк (Ротшрек)",
        AtavismKind.ClanSpecific        => "Клановый",
        _                                => "Неизв.",
    };

    /// <summary>Строчное представление для embed: «иконка имя — Тип (сцена)».</summary>
    public string Describe() => AcquiredAt is null
        ? $"{Icon} {Name} — {KindLabel}"
        : $"{Icon} {Name} — {KindLabel} (сцена {AcquiredAt})";
}
