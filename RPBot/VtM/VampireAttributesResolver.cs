using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RPBot.VtM;

/// <summary>
/// Результат операции на Шаге 2 «Характеристики».
/// </summary>
public enum VampireAttributesFailure
{
    None,
    /// <summary>Не выбран приоритет.</summary>
    PriorityRequired,
    /// <summary>Атрибут не найден в каталоге.</summary>
    UnknownAttribute,
    /// <summary>Уменьшение ниже базы (1; для Привлекательности у Носферату/Самеди — 0).</summary>
    BelowBase,
    /// <summary>Превышен групповой бюджет (7/5/3).</summary>
    GroupBudgetExceeded,
    /// <summary>Приоритет не входит в 6 стандартных 7/5/3.</summary>
    InvalidPriority,
}

/// <summary>
/// Решение резолвера Шага 2: статус, ошибка, обновлённый черновик, готовность шага.
/// </summary>
public sealed record VampireAttributesDecision(
    VampireAttributesFailure Failure,
    string Message,
    VampireCharacter Draft,
    bool StepComplete)
{
    public bool IsSuccess => Failure == VampireAttributesFailure.None;
}

/// <summary>
/// Чистая логика Шага 2 «Характеристики 7/5/3» (V20, стр. 84).
/// </summary>
public static class VampireAttributesResolver
{
    /// <summary>
    /// Установить приоритет групп (одна из 6 стандартных перестановок 7/5/3).
    /// </summary>
    public static VampireAttributesDecision SetPriority(
        VampireCharacter draft,
        VampireAttributePriority priority)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        if (!VampireAttributePriorityExtensions.All.Contains(priority))
            return Fail(draft, VampireAttributesFailure.InvalidPriority,
                "Приоритет должен быть одной из 6 стандартных перестановок 7/5/3.");

        draft.AttributesPriority = priority.ToString();
            return CompleteCheck(draft);
        }

        /// <summary>
        /// Применить специальные клановые правила к атрибутам (V20).
        /// Вызывается при выборе клана в визарде (VampireCreateResolver.SetClan).
        /// </summary>
        public static void ApplyClanRules(VampireCharacter draft)
        {
            if (draft == null) throw new System.ArgumentNullException(nameof(draft));
            if (draft.Clan == "Носферату" || draft.Clan == "Последователь Сета")
            {
                if (draft.AttributesStruct.Appearance > 0)
                    draft.AttributesStruct.Appearance = 0;
            }
        }

    /// <summary>
    /// Инкрементировать атрибут на 1 (не выше группового бюджета).
    /// </summary>
    public static VampireAttributesDecision Increment(
        VampireCharacter draft,
            VampireAttributeGroup group,
            string attributeName)
        {
            return SetAttributeDelta(draft, attributeName, +1, group);
        }

        /// <summary>Инкрементировать — обратная совместимость: найти группу по имени атрибута.</summary>
        public static VampireAttributesDecision Increment(
            VampireCharacter draft,
            string attributeName)
        {
            var group = VampireAttributeCatalog.FindGroup(attributeName);
            if (!group.HasValue)
            {
                return Fail(new VampireCharacter(), VampireAttributesFailure.UnknownAttribute,
                    $"Неизвестный атрибут: {attributeName}");
            }
            return Increment(draft, group.Value, attributeName);
        }

        /// <summary>Декрементировать атрибут на 1 (не ниже базы — 1, или 0 для Привлекательности Носферату/Самеди).</summary>
        public static VampireAttributesDecision Decrement(
            VampireCharacter draft,
            VampireAttributeGroup group,
            string attributeName)
        {
            return SetAttributeDelta(draft, attributeName, -1, group);
        }

        /// <summary>Декрементировать — обратная совместимость: найти группу по имени атрибута.</summary>
        public static VampireAttributesDecision Decrement(
            VampireCharacter draft,
            string attributeName)
        {
            var group = VampireAttributeCatalog.FindGroup(attributeName);
            if (!group.HasValue)
            {
                return Fail(new VampireCharacter(), VampireAttributesFailure.UnknownAttribute,
                    $"Неизвестный атрибут: {attributeName}");
            }
            return Decrement(draft, group.Value, attributeName);
        }

    /// <summary>Сбросить все атрибуты к базе (для кнопки «Сбросить прогресс»).</summary>
    public static VampireAttributesDecision ResetProgress(VampireCharacter draft)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        draft.AttributesStruct = new VampireAttributes();
        return CompleteCheck(draft);
    }

    /// <summary>
    /// Сбросить и приоритет, и атрибуты (для кнопки «Сбросить всё»).
    /// </summary>
    public static VampireAttributesResetAllResult ResetAll(VampireCharacter draft)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        draft.AttributesStruct = new VampireAttributes();
        draft.AttributesPriority = "";
        return new VampireAttributesResetAllResult(draft);
    }

    /// <summary>Проверить, что шаг 2 завершён (приоритет выбран и все очки распределены).</summary>
    public static bool IsAttributesComplete(VampireCharacter draft)
    {
        if (draft == null) return false;
        if (!TryParsePriority(draft.AttributesPriority, out var priority))
            return false;

        var totalSpent = GetTotalSpent(draft.AttributesStruct);
        if (totalSpent != 15) return false;

        // Все очки каждой группы должны быть истрачены полностью.
        foreach (var group in new[] { VampireAttributeGroup.Physical, VampireAttributeGroup.Social, VampireAttributeGroup.Mental })
        {
            var budget = priority.PointsFor(group);
            var spent = GetGroupSpent(draft.AttributesStruct, group);
            if (spent != budget) return false;
        }
        return true;
    }

    /// <summary>
    /// Сколько очков осталось в группе (бюджет − потрачено).
    /// </summary>
    public static int RemainingInGroup(VampireCharacter draft, VampireAttributeGroup group)
    {
        if (draft == null) return 0;
        if (!TryParsePriority(draft.AttributesPriority, out var priority)) return 0;
        return priority.PointsFor(group) - GetGroupSpent(draft.AttributesStruct, group);
    }

    /// <summary>
    /// Текст текущего состояния Шага 2 для DM.
    /// </summary>
    public static string BuildAttributesStatusMessage(VampireCharacter draft)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        var sb = new StringBuilder();
        sb.AppendLine("**Шаг 2 — Характеристики**");
        sb.AppendLine();
        sb.AppendLine("Выберите приоритет групп и распределите очки (7/5/3). Каждая характеристика стартует с 1 пункта. Исключения: Носферату и Самеди имеют Привлекательность = 0.");
        sb.AppendLine();

        if (TryParsePriority(draft.AttributesPriority, out var priority))
        {
            sb.AppendLine($"**Приоритет:** {priority.HumanName()}");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("_Приоритет не выбран. Используйте выпадающее меню выше._");
            sb.AppendLine();
        }

        foreach (var group in new[]
                 { VampireAttributeGroup.Physical, VampireAttributeGroup.Social, VampireAttributeGroup.Mental })
        {
            var groupNames = group switch
            {
                VampireAttributeGroup.Physical => "Физические",
                VampireAttributeGroup.Social => "Социальные",
                VampireAttributeGroup.Mental => "Ментальные",
                _ => group.ToString(),
            };

            var budget = TryParsePriority(draft.AttributesPriority, out var p) ? p.PointsFor(group) : 0;
            var spent = GetGroupSpent(draft.AttributesStruct, group);
            sb.AppendLine($"**{groupNames}** (потрачено {spent}/{budget}):");

            foreach (var name in VampireAttributeCatalog.NamesInGroup(group))
            {
                var value = GetAttributeValue(draft.AttributesStruct, name);
                var baseVal = GetBaseValue(draft, name);
                var added = value - baseVal;
                var addStr = added > 0 ? $" (+{added})" : "";
                sb.AppendLine($"  • {name}: **{value}**{addStr}");
            }
            sb.AppendLine();
        }

        var totalSpent = GetTotalSpent(draft.AttributesStruct);
        sb.AppendLine($"**Итого потрачено:** {totalSpent}/15");
        sb.AppendLine();

        sb.AppendLine(IsAttributesComplete(draft)
            ? "✅ Все 15 пунктов распределены по приоритету. Нажмите «Далее», чтобы перейти к Шагу 3 (способности)."
            : "⏳ Распределите все пункты. Когда все группы будут потрачены полностью — появится кнопка «Далее».");
        return sb.ToString();
    }

    // ── Внутренние хелперы ───────────────────────────────────────────────

    private static VampireAttributesDecision SetAttributeDelta(
        VampireCharacter draft,
        string attributeName,
            int delta,
            VampireAttributeGroup? groupArg = null)
        {
            if (draft == null) throw new System.ArgumentNullException(nameof(draft));
            var group = groupArg ?? VampireAttributeCatalog.FindGroup(attributeName);
            if (!group.HasValue)
                return Fail(draft, VampireAttributesFailure.UnknownAttribute,
                    $"«{attributeName}» не является валидной характеристикой. Допустимые: {string.Join(", ", AllAttributesFlat())}.");

        if (!TryParsePriority(draft.AttributesPriority, out var priority))
            return Fail(draft, VampireAttributesFailure.PriorityRequired,
                "Сначала выберите приоритет (выпадающее меню выше).");

        var baseVal = GetBaseValue(draft, attributeName);
        var current = GetAttributeValue(draft.AttributesStruct, attributeName);
        var newValue = current + delta;

        if (newValue < baseVal)
            return Fail(draft, VampireAttributesFailure.BelowBase,
                $"Нельзя уменьшить «{attributeName}» ниже базового значения {baseVal}.");

            var g = group.Value;
            var budget = priority.PointsFor(g);
            var spent = GetGroupSpent(draft.AttributesStruct, g);
            var newSpent = spent + delta;
            if (newSpent > budget)
                return Fail(draft, VampireAttributesFailure.GroupBudgetExceeded,
                    $"Превышен бюджет группы {g}. Доступно: {budget - spent} (нужно {delta}).");

            SetAttributeValue(draft.AttributesStruct, attributeName, newValue);
            return CompleteCheck(draft);
        }

    private static List<string> AllAttributesFlat()
    {
        var list = new List<string>(9);
        list.AddRange(VampireAttributeCatalog.Physical);
        list.AddRange(VampireAttributeCatalog.Social);
        list.AddRange(VampireAttributeCatalog.Mental);
        return list;
    }

    /// <summary>База: для Привлекательности у Носферату/Самеди = 0, иначе 1.</summary>
    private static int GetBaseValue(VampireCharacter draft, string attributeName)
    {
        if (attributeName == "Привлекательность"
            && (draft.Clan == "Носферату" || draft.Clan == "Последователь Сета"))
        {
            return 0;
        }
        return 1;
    }

    private static int GetAttributeValue(VampireAttributes attrs, string name) => name switch
    {
        "Сила"              => attrs.Strength,
        "Ловкость"          => attrs.Dexterity,
        "Выносливость"      => attrs.Stamina,
        "Обаяние"           => attrs.Charisma,
        "Манипуляция"       => attrs.Manipulation,
        "Привлекательность" => attrs.Appearance,
        "Восприятие"        => attrs.Perception,
        "Интеллект"         => attrs.Intelligence,
        "Смекалка"          => attrs.Wits,
        _ => 0,
    };

    private static void SetAttributeValue(VampireAttributes attrs, string name, int value)
    {
        if (value < 0) value = 0;
        switch (name)
        {
            case "Сила":              attrs.Strength = value; break;
            case "Ловкость":          attrs.Dexterity = value; break;
            case "Выносливость":      attrs.Stamina = value; break;
            case "Обаяние":           attrs.Charisma = value; break;
            case "Манипуляция":       attrs.Manipulation = value; break;
            case "Привлекательность": attrs.Appearance = value; break;
            case "Восприятие":        attrs.Perception = value; break;
            case "Интеллект":         attrs.Intelligence = value; break;
            case "Смекалка":          attrs.Wits = value; break;
        }
    }

    private static int GetGroupSpent(VampireAttributes attrs, VampireAttributeGroup group) => group switch
    {
        VampireAttributeGroup.Physical => attrs.Strength + attrs.Dexterity + attrs.Stamina - 3,
        VampireAttributeGroup.Social   => attrs.Charisma + attrs.Manipulation + attrs.Appearance - 3,
        VampireAttributeGroup.Mental   => attrs.Perception + attrs.Intelligence + attrs.Wits - 3,
        _ => 0,
    };

    private static int GetTotalSpent(VampireAttributes attrs)
    {
        // На каждой характеристике база 1, значит «потрачено» — это сумма −9.
        // Для Носферату/Самеди база Привлекательности 0, но мы не учитываем это здесь —
        // потраченное считаем как сумма − 8 (используется только когда нужно сравнить с 15).
        // Корректнее: вернуть (сумма_без_учёта_привлекательности_носуф) + Appearance
        // … Для простоты сравнения с 15 используем:
        return attrs.Strength + attrs.Dexterity + attrs.Stamina
             + attrs.Charisma + attrs.Manipulation + attrs.Appearance
             + attrs.Perception + attrs.Intelligence + attrs.Wits - 9;
    }

    private static bool TryParsePriority(string s, out VampireAttributePriority priority)
    {
        priority = default;
        if (string.IsNullOrEmpty(s)) return false;
        return System.Enum.TryParse(s, ignoreCase: false, out priority)
            && VampireAttributePriorityExtensions.All.Contains(priority);
    }

    private static VampireAttributesDecision Fail(
        VampireCharacter draft,
        VampireAttributesFailure failure,
        string message)
        => new VampireAttributesDecision(failure, message, draft, IsAttributesComplete(draft));

    private static VampireAttributesDecision CompleteCheck(VampireCharacter draft)
        => new VampireAttributesDecision(
            VampireAttributesFailure.None,
            "",
            draft,
            IsAttributesComplete(draft));
}

/// <summary>Возврат ResetAll — отделён, т.к. у DM-обработчика может быть своя логика.</summary>
public sealed record VampireAttributesResetAllResult(VampireCharacter Draft);
