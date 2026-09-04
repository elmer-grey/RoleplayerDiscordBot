using System;
using System.Collections.Generic;
using System.Reflection;

namespace RPBot.VtM;

/// <summary>
/// Низкоуровневые хелперы: чтение/запись поля любой из 7 целей Шага 5
/// (атрибуты, способности, дисциплины, факты, добродетели, Чел, Воля).
/// </summary>
public static partial class VampireFinishingResolver
{
    /// <summary>Текущее значение поля по цели.</summary>
    public static int ReadFieldValue(VampireCharacter draft, FreebieTarget target, string field)
    {
        if (draft == null || string.IsNullOrEmpty(field)) return 0;
        return target switch
        {
            FreebieTarget.Attribute   => GetDictValue(draft.Attributes,         field),
            // Ability: читаем ТОЛЬКО freebie dict (Attributes), без AbilitiesStruct —
            // иначе AllocateFreebie будет перезаписывать struct-значение.
            FreebieTarget.Ability     => GetDictValue(draft.Attributes,         field),
            FreebieTarget.Discipline  => GetDictValue(draft.FreebieDisciplines, field),
            FreebieTarget.Background  => GetDictValue(draft.FreebieBackgrounds, field),
            FreebieTarget.Virtue      => GetDictValue(draft.Virtues,           field),
            FreebieTarget.Humanity    => Math.Max(0, draft.HumanityBonus),
            FreebieTarget.Willpower   => Math.Max(0, draft.WillpowerBonus),
            _ => 0,
        };
    }

    /// <summary>
    /// Прочитать «итоговое» значение атрибута/способности (Шаг 2/3 + freebie-бонус),
    /// если нужен актуальный потолок для проверки капа.
    /// </summary>
    public static int ReadAttributeValue(VampireCharacter draft, string field)
    {
        if (draft == null) return 0;
        int baseValue = draft.AttributesStruct != null
            ? VampireAttributesResolver.GetAttributeValue(draft.AttributesStruct, field)
            : 0;
        int fb = GetDictValue(draft.Attributes, field);
        return baseValue + fb;
    }

    /// <summary>
    /// Прочитать «итоговое» значение способности (Шаг 3 + freebie-бонус).
    /// </summary>
    public static int ReadAbilityValue(VampireCharacter draft, string field)
    {
        if (draft == null) return 0;
        int baseValue = draft.AbilitiesStruct != null
            ? VampireAbilitiesResolver.GetAbilityValue(draft.AbilitiesStruct, field)
            : 0;
        int fb = GetDictValue(draft.Attributes, field);
        return baseValue + fb;
    }

    private static int GetDictValue(Dictionary<string, int> dict, string key)
        => (dict != null && dict.TryGetValue(key, out var v)) ? Math.Max(0, v) : 0;

    private static int GetAbilityValue(VampireCharacter draft, string field)
    {
        if (draft?.AbilitiesStruct == null) return 0;
        var prop = draft.AbilitiesStruct.GetType().GetProperty(
            field,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop == null || prop.PropertyType != typeof(int)) return 0;
        var v = prop.GetValue(draft.AbilitiesStruct);
        return v is int i ? Math.Max(0, i) : 0;
    }

    private static bool SetAbilityValue(VampireCharacter draft, string field, int value)
    {
        if (draft?.AbilitiesStruct == null) return false;
        var prop = draft.AbilitiesStruct.GetType().GetProperty(
            field,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop == null || prop.PropertyType != typeof(int)) return false;
        prop.SetValue(draft.AbilitiesStruct, value);
        return true;
    }

    private static bool WriteFieldValue(
        VampireCharacter draft,
        FreebieTarget target,
        string field,
        int newValue,
        out string error)
    {
        error = null;
        if (draft == null) { error = "draft is null"; return false; }
        if (newValue < 0) newValue = 0;

        switch (target)
        {
            case FreebieTarget.Attribute:
                {
                    // Freebie-бонус пишется ТОЛЬКО в dict, не в AttributesStruct.
                    // Иначе IsAttributesComplete (Шаг 2) сломается: он считает sum(AttributesStruct) - 9 == 15.
                    var cur = ReadAttributeValue(draft, field);
                    int next = Math.Max(0, newValue);
                    if (next <= 0) draft.Attributes.Remove(field);
                    else draft.Attributes[field] = next;
                    return true;
                }
            case FreebieTarget.Ability:
                {
                    // Freebie-бонус пишется ТОЛЬКО в dict, не в AbilitiesStruct.
                    // Иначе IsAbilitiesComplete (Шаг 3) сломается: он считает sum(AbilitiesStruct) == 27.
                    if (VampireAbilitiesCatalog.FindGroup(field) == null)
                    {
                        error = $"Неизвестная способность: {field}";
                        return false;
                    }
                    var cur = GetAbilityValue(draft, field);
                    int next = Math.Max(0, newValue);
                    if (next <= 0) draft.Attributes.Remove(field);
                    else draft.Attributes[field] = next;
                    return true;
                }
            case FreebieTarget.Discipline:
                // Freebie-бонус пишется ТОЛЬКО в FreebieDisciplines, не в Disciplines.
                // Иначе IsDisciplinesComplete (Шаг 4.1) сломается: он считает sum(Disciplines) == 3.
                if (newValue <= 0) draft.FreebieDisciplines.Remove(field);
                else draft.FreebieDisciplines[field] = newValue;
                return true;
            case FreebieTarget.Background:
                // Freebie-бонус пишется ТОЛЬКО в FreebieBackgrounds, не в Backgrounds.
                // Иначе IsBackgroundsComplete (Шаг 4.2) сломается: он считает sum(Backgrounds) == 5.
                if (newValue <= 0) draft.FreebieBackgrounds.Remove(field);
                else draft.FreebieBackgrounds[field] = newValue;
                return true;
            case FreebieTarget.Virtue:
                if (newValue <= 0) draft.Virtues.Remove(field);
                else draft.Virtues[field] = newValue;
                return true;
            case FreebieTarget.Humanity:
                draft.HumanityBonus = newValue;
                return true;
            case FreebieTarget.Willpower:
                draft.WillpowerBonus = newValue;
                return true;
        }
        error = $"Неизвестная цель: {target}";
        return false;
    }

    /// <summary>Полный список строк «уже потрачено» для UI.</summary>
    public static IReadOnlyList<string> DescribeSpent(VampireCharacter draft)
    {
        if (draft == null || draft.FreebieSpent.Count == 0)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var (k, count) in draft.FreebieSpent)
        {
            if (count <= 0) continue;
            var colon = k.IndexOf(':');
            if (colon <= 0) continue;
            int.TryParse(k.Substring(0, colon), out var targetNum);
            var target = (FreebieTarget)targetNum;
            var field = k.Substring(colon + 1);
            list.Add($"{TargetName(target, draft)}: «{field}» — ×{count} (-{CostOf(target) * count})");
        }
        return list;
    }
}
