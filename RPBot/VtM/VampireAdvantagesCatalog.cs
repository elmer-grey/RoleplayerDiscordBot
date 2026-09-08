using System.Collections.Generic;

namespace RPBot.VtM;

/// <summary>
/// Шаг 4 «Преимущества»: пулы, кэпы и стартовые значения для трёх секций.
/// </summary>
/// <remarks>
/// <para>VtM V20 (стр. 99-100): игрок распределяет свободно фиксированные пулы
/// очков в Дисциплины, факты биографии и Добродетели.</para>
/// <list type="bullet">
/// <item>Дисциплины: пул 3, имена — клановые (для Каитифа пусто, свободный ввод).</item>
/// <item>Факты биографии: пул 5, имена свободные, кэп ранга 5.</item>
/// <item>Добродетели: пул 7 сверху, база 1/1/1, имена фиксированные.</item>
/// </list>
/// <para>Старт всех значений = кэп при свободных пунктах (<c>Base</c>) —
/// то что даётся персонажу автоматически до Шага 4. Это не «минимум»,
/// а «значение до распределения».</para>
/// </remarks>
public static class VampireAdvantagesCatalog
{
    // ─── Параметры пулов ───────────────────────────────────────────

    /// <summary>Дисциплины: количество пунктов преимуществ.</summary>
    public const int DisciplinePool = 3;

    /// <summary>Факты биографии: количество пунктов преимуществ.</summary>
    public const int BackgroundPool = 5;

    /// <summary>Добродетели: количество пунктов преимуществ (сверх базы).</summary>
    public const int VirtuePool = 7;

    /// <summary>Кэп на одно поле (дисциплина / факт / добродетель): макс 5.</summary>
    public const int PerFieldCap = 5;

    /// <summary>Максимум разных дисциплин у одного персонажа (V20: канонически не больше 6).</summary>
    public const int MaxDisciplinesPerCharacter = 6;

    /// <summary>Максимум разных фактов биографии у одного персонажа (V20: канонически не больше 6).</summary>
    public const int MaxBackgroundsPerCharacter = 6;

    /// <summary>Минимальное значение любой ячейки: 0 для дисциплин/фактов, 1 для добродетелей.</summary>
    public const int MinDiscipline = 0;
    public const int MinBackground = 1;
    public const int MinVirtue = 1;

    /// <summary>Базовое значение каждой добродетели до распределения 7 пунктов (V20 стр. 100).</summary>
    public const int VirtueBaseConscience = 1;
    public const int VirtueBaseSelfControl = 1;
    public const int VirtueBaseCourage = 1;

    /// <summary>Имена клановых дисциплин для шкалы Каитифа (3 фиксированных имени-заглушки).</summary>
    /// <remarks>
    /// Каитиф в V20 не имеет клановых дисциплин и вкладывает в любые с одобрения рассказчика.
    /// На UI даём пользователю 3 «пустых» слота и свободный ввод имени каждого.
    /// </remarks>
    public static readonly IReadOnlyList<string> CaitiffDisciplineSlots = new[]
    {
        "Дисциплина 1",
        "Дисциплина 2",
        "Дисциплина 3",
    };

    /// <summary>
    /// Получить имена дисциплин, доступные для клана на Шаге 4.
    /// </summary>
    public static IReadOnlyList<string> GetDisciplineSlots(string? clanName)
    {
        if (string.IsNullOrEmpty(clanName)) return System.Array.Empty<string>();
        if (clanName == "Каитиф") return CaitiffDisciplineSlots;
        var fromClan = VampireParameterCatalog.GetClanDisciplines(clanName);
        return fromClan.Count == 0 ? CaitiffDisciplineSlots : fromClan;
    }

    /// <summary>Имеются ли слоты дисциплин для клана.</summary>
    public static bool HasDisciplineSlots(string? clanName)
        => GetDisciplineSlots(clanName).Count > 0;

    /// <summary>Является ли клан Каитифом (свободный ввод имён дисциплин).</summary>
    public static bool IsCaitiff(string? clanName)
        => !string.IsNullOrEmpty(clanName) && clanName == "Каитиф";

    /// <summary>
    /// Стандартные имена фонов VtM V20 (13 шт., см. <see cref="VampireParameterCatalog.Backgrounds"/>).
    /// Вынесено как алиас, чтобы визард Шага 4.2 мог ссылаться на «канон» из каталога параметров.
    /// </summary>
    public static IReadOnlyList<string> StandardBackgrounds => VampireParameterCatalog.Backgrounds;

    /// <summary>Является ли имя стандартным фоном VtM V20.</summary>
    public static bool IsStandardBackground(string? name)
        => VampireParameterCatalog.IsValidBackground(name);
}
