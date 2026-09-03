using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM;

/// <summary>
/// Приоритет группы характеристик на Шаге 2 визарда.
/// </summary>
/// <remarks>
/// <para>VtM V20 (стр. 84): одна из групп получает 7 пунктов (первичная),
/// другая — 5 (вторичная), третья — 3 (третичная). Всего 15 пунктов
/// на 9 характеристик (по 3 в группе), каждый шаг распределяется
/// целиком внутри группы.</para>
///
/// <para>В книге только 6 «стандартных» перестановок 7/5/3 — кастомных
/// (вроде 4/5/6) нет. Эти 6 перестановок и перечислены в <see cref="All"/>.</para>
/// </remarks>
public enum VampireAttributePriority
{
    /// <summary>Физические=7, Социальные=5, Ментальные=3.</summary>
    PhysicalPrimary = 0,
    /// <summary>Физические=7, Ментальные=5, Социальные=3.</summary>
    PhysicalSecondary = 1,
    /// <summary>Социальные=7, Физические=5, Ментальные=3.</summary>
    SocialPrimary = 2,
    /// <summary>Социальные=7, Ментальные=5, Физические=3.</summary>
    SocialSecondary = 3,
    /// <summary>Ментальные=7, Физические=5, Социальные=3.</summary>
    MentalPrimary = 4,
    /// <summary>Ментальные=7, Социальные=5, Физические=3.</summary>
    MentalSecondary = 5,
}

public static class VampireAttributePriorityExtensions
{
    /// <summary>
    /// Все 6 стандартных приоритетов (используется для UI SelectMenu).
    /// </summary>
    public static readonly IReadOnlyList<VampireAttributePriority> All = new[]
    {
        VampireAttributePriority.PhysicalPrimary,
        VampireAttributePriority.PhysicalSecondary,
        VampireAttributePriority.SocialPrimary,
        VampireAttributePriority.SocialSecondary,
        VampireAttributePriority.MentalPrimary,
        VampireAttributePriority.MentalSecondary,
    };

    public static int PointsFor(this VampireAttributePriority priority, VampireAttributeGroup group)
    {
        return priority switch
        {
            VampireAttributePriority.PhysicalPrimary => group == VampireAttributeGroup.Physical ? 7
                : group == VampireAttributeGroup.Social ? 5 : 3,
            VampireAttributePriority.PhysicalSecondary => group == VampireAttributeGroup.Physical ? 7
                : group == VampireAttributeGroup.Social ? 3 : 5,
            VampireAttributePriority.SocialPrimary => group == VampireAttributeGroup.Social ? 7
                : group == VampireAttributeGroup.Physical ? 5 : 3,
            VampireAttributePriority.SocialSecondary => group == VampireAttributeGroup.Social ? 7
                : group == VampireAttributeGroup.Physical ? 3 : 5,
            VampireAttributePriority.MentalPrimary => group == VampireAttributeGroup.Mental ? 7
                : group == VampireAttributeGroup.Physical ? 5 : 3,
            VampireAttributePriority.MentalSecondary => group == VampireAttributeGroup.Mental ? 7
                : group == VampireAttributeGroup.Social ? 5 : 3,
            _ => 0,
        };
    }

    public static string HumanName(this VampireAttributePriority priority)
    {
        // Удобное русское название для UI.
        return priority switch
        {
            VampireAttributePriority.PhysicalPrimary  => "Физ (7) / Соц (5) / Мент (3)",
            VampireAttributePriority.PhysicalSecondary => "Физ (7) / Мент (5) / Соц (3)",
            VampireAttributePriority.SocialPrimary    => "Соц (7) / Физ (5) / Мент (3)",
            VampireAttributePriority.SocialSecondary   => "Соц (7) / Мент (5) / Физ (3)",
            VampireAttributePriority.MentalPrimary    => "Мент (7) / Физ (5) / Соц (3)",
            VampireAttributePriority.MentalSecondary   => "Мент (7) / Соц (5) / Физ (3)",
            _ => priority.ToString(),
        };
    }
}

/// <summary>
/// Группа характеристик (Шаг 2).
/// </summary>
public enum VampireAttributeGroup
{
    /// <summary>Физические: Сила, Ловкость, Выносливость.</summary>
    Physical,
    /// <summary>Социальные: Обаяние, Манипуляция, Привлекательность.</summary>
    Social,
    /// <summary>Ментальные: Восприятие, Интеллект, Смекалка.</summary>
    Mental,
}

/// <summary>
/// Каталог 9 характеристик VtM V20. Источник — книга правил, стр. 91.
/// </summary>
public static class VampireAttributeCatalog
{
    public static readonly IReadOnlyList<string> Physical = new[] { "Сила", "Ловкость", "Выносливость" };
    public static readonly IReadOnlyList<string> Social   = new[] { "Обаяние", "Манипуляция", "Привлекательность" };
    public static readonly IReadOnlyList<string> Mental   = new[] { "Восприятие", "Интеллект", "Смекалка" };

    public static IReadOnlyList<string> NamesInGroup(VampireAttributeGroup group) => group switch
    {
        VampireAttributeGroup.Physical => Physical,
        VampireAttributeGroup.Social   => Social,
        VampireAttributeGroup.Mental   => Mental,
        _ => System.Array.Empty<string>(),
    };

    /// <summary>Определить группу по имени характеристики (рус.). null если не нашли.</summary>
    public static VampireAttributeGroup? FindGroup(string attributeName)
    {
        if (string.IsNullOrEmpty(attributeName)) return null;
        if (Physical.Contains(attributeName)) return VampireAttributeGroup.Physical;
        if (Social.Contains(attributeName))   return VampireAttributeGroup.Social;
        if (Mental.Contains(attributeName))   return VampireAttributeGroup.Mental;
        return null;
    }
}
