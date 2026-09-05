using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM;

/// <summary>
/// Каталог канонических Достоинств (Merits) и Недостатков (Flaws) VtM V20.
/// </summary>
/// <remarks>
/// <para>Источник: VtM V20 Corebook, стр. 485+ (Merits) и 523+ (Flaws).</para>
/// <para>Цены в диапазоне 1..7. На Шаге 5 игрок распределяет freebie-пул:
/// Merits покупаются за freebie (цена 1..7), Flaws дают бонусные пункты
/// к пулу (тоже 1..7).</para>
/// <para>Перечень не исчерпывающий — игрок может попросить рассказчика
/// добавить произвольное Merit/Flaw через свободный ввод имени, если
/// его нет в каталоге. В таком случае цена выбирается рассказчиком.</para>
/// </remarks>
public static class VampireMeritsFlawsCatalog
{
    /// <summary>Достоинство из каталога.</summary>
    public sealed record MeritEntry(string Name, int Cost, string Category, string Description);

    /// <summary>Недостаток из каталога.</summary>
    public sealed record FlawEntry(string Name, int Cost, string Category, string Description);

    // ─── Merits (V20 стр. 485+) ────────────────────────────────────

    /// <summary>
    /// Канонические Merits V20. Подмножество — основные категории:
    /// Physical, Mental/Social, Supernatural, Status, Influence.
    /// </summary>
    public static readonly IReadOnlyList<MeritEntry> Merits = new[]
    {
        // Physical
        new MeritEntry("Обострённые чувства", 1, "Физические", "+1 к восприятию для одного из чувств (зрение, слух, обоняние, осязание, вкус)."),
        new MeritEntry("Быстрый рефлекс", 1, "Физические", "+1 к инициативе."),
        new MeritEntry("Железная воля", 3, "Физические", "Тратит 1 Willpower, чтобы получить +2 dice pool на действие против ментального контроля."),
        new MeritEntry("Двойное натур.", 5, "Физические", "+2 к Обаянию по отношению к тем, кого вы сексуально привлекаете."),
        new MeritEntry("Скорость", 3, "Физические", "+1 к скорости передвижения и +2 dice на действия, связанные со скоростью."),
        // Mental
        new MeritEntry("Бдительный ум", 1, "Ментальные", "+1 dice на одно связанное действие разума (логика, память и т.п.)."),
        new MeritEntry("Концентрация", 2, "Ментальные", "+1 dice на действие, требующее длительной сосредоточенности."),
        new MeritEntry("Эйдетическая память", 2, "Ментальные", "Автоматический успех на простое воспоминание; +2 dice на сложное."),
        new MeritEntry("Ясный ум", 3, "Ментальные", "Тратит 1 Willpower, чтобы получить +2 dice pool на ментальное действие."),
        new MeritEntry("Полиглот", 2, "Ментальные", "+1 к знанию языка сверх обычного."),
        // Social
        new MeritEntry("Обаяние", 2, "Социальные", "+1 к Обаянию в социальных проверках."),
        new MeritEntry("Красноречие", 2, "Социальные", "+1 к Красноречию (речь, убеждение)."),
        new MeritEntry("Друзья повсюду", 1, "Социальные", "Знает кого-то в любом городе, у кого можно попросить помощь (1 раз за историю)."),
        new MeritEntry("Привилегия", 4, "Социальные", "Рождён в знатной/богатой семье; +1 на реакции людей в официальных ситуациях."),
        // Supernatural (Vampire-only)
        new MeritEntry("Чистая кровь", 4, "Сверхъестественные", "Дисциплины, работающие на крови, на 1 уровень слабее для вашего персонажа."),
        new MeritEntry("Душа коснулась", 7, "Сверхъестественные", "Возможность истинной смерти (Diablerie не поглощает душу)."),
        new MeritEntry("Зов крови (слабый)", 3, "Сверхъестественные", "+1 dice на действие Bond к сородичу."),
        new MeritEntry("Зов крови (сильный)", 5, "Сверхъестественные", "+2 dice на действие Bond к сородичу."),
    };

    // ─── Flaws (V20 стр. 523+) ────────────────────────────────────

    /// <summary>
    /// Канонические Flaws V20. Подмножество — основные категории:
    /// Physical, Mental/Social, Supernatural, Moral.
    /// </summary>
    public static readonly IReadOnlyList<FlawEntry> Flaws = new[]
    {
        // Physical
        new FlawEntry("Глухота на одно ухо", 1, "Физические", "Одно ухо полностью глухое."),
        new FlawEntry("Дальтонизм", 1, "Физические", "Не различает определённые цвета."),
        new FlawEntry("Медленный рефлекс", 1, "Физические", "-1 к инициативе."),
        new FlawEntry("Одноногий", 3, "Физические", "Нет одной ноги (или функциональный аналог)."),
        new FlawEntry("Хромота", 2, "Физические", "Хромает; скорость передвижения уменьшена."),
        // Mental
        new FlawEntry("Мягкий ум", 1, "Ментальные", "-1 dice на действия против ментального контроля."),
        new FlawEntry("Забывчивость", 1, "Ментальные", "-1 к долгосрочной памяти (забывает факты)."),
        new FlawEntry("Ночные кошмары", 2, "Ментальные", "Мучают повторяющиеся кошмары, мешают спать."),
        new FlawEntry("Фобия (конкретная)", 2, "Ментальные", "Сильный страх перед конкретным явлением/объектом."),
        new FlawEntry("Навязчивость", 3, "Ментальные", "Одержим конкретной целью; действия вне её получают -2."),
        // Social
        new FlawEntry("Некрасивый", 1, "Социальные", "-1 к Обаянию."),
        new FlawEntry("Социальная неуклюжесть", 2, "Социальные", "-1 к Красноречию в формальных ситуациях."),
        new FlawEntry("Немой", 4, "Социальные", "Не может говорить (но может писать, жестикулировать)."),
        new FlawEntry("Скверный характер", 2, "Социальные", "Все социальные реакции ухудшены на 1."),
        new FlawEntry("Предатель", 4, "Социальные", "Известен как предатель; социальные реакции сильно ухудшены."),
        // Supernatural (Vampire-only)
        new FlawEntry("Истончающаяся кровь", 4, "Сверхъестественные", "Кровь ослаблена: -1 к Blood Pool."),
        new FlawEntry("Зов крови (слабый)", 2, "Сверхъестественные", "-1 dice на Bond."),
        new FlawEntry("Зов крови (сильный)", 4, "Сверхъестественные", "-2 dice на Bond."),
        new FlawEntry("Несвежесть", 3, "Сверхъестественные", "Запах разложения; +1 к Обаянию от вампиров, -1 от смертных."),
    };

    /// <summary>Получить Merit по имени (null, если нет в каталоге).</summary>
    public static MeritEntry? FindMerit(string name)
        => Merits.FirstOrDefault(m => m.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));

    /// <summary>Получить Flaw по имени (null, если нет в каталоге).</summary>
    public static FlawEntry? FindFlaw(string name)
        => Flaws.FirstOrDefault(f => f.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));

    /// <summary>Разрешённая цена (1..7).</summary>
    public static bool IsValidCost(int cost) => cost >= 1 && cost <= 7;
}
