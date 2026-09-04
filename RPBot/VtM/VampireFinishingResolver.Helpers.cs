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
            FreebieTarget.Attribute   => GetDictValue(draft.Attributes,  field),
            FreebieTarget.Ability     => GetAbilityValue(draft,         field),
            FreebieTarget.Discipline  => GetDictValue(draft.Disciplines, field),
            FreebieTarget.Background  => GetDictValue(draft.Backgrounds, field),
            FreebieTarget.Virtue      => GetDictValue(draft.Virtues,     field),
            FreebieTarget.Humanity    => Math.Max(0, draft.HumanityBonus),
            FreebieTarget.Willpower   => Math.Max(0, draft.WillpowerBonus),
            _ => 0,
        };
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
                if (newValue <= 0) draft.Attributes.Remove(field);
                else draft.Attributes[field] = newValue;
                return true;
            case FreebieTarget.Ability:
                if (!SetAbilityValue(draft, field, newValue))
                {
                    error = $"Неизвестная способность: {field}";
                    return false;
                }
                return true;
            case FreebieTarget.Discipline:
                if (newValue <= 0) draft.Disciplines.Remove(field);
                else draft.Disciplines[field] = newValue;
                return true;
            case FreebieTarget.Background:
                if (newValue <= 0) draft.Backgrounds.Remove(field);
                else draft.Backgrounds[field] = newValue;
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
