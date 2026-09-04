using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace RPBot.VtM;

/// <summary>
/// Бизнес-логика Шага 5 визарда VtM V20 «Последние штрихи».
/// </summary>
/// <remarks>
/// <para>Состав шага (по V20 стр. 86, 92-100):</para>
/// <list type="bullet">
///   <item>Производные (read-only) — Человечность и Воля рассчитываются по формулам
///         <c>Чел = Совесть + Самоконтроль</c> и <c>Воля = Смелость</c> из добродетелей,
///         уже распределённых на Шаге 4.3. Отдельных правок Чел/Воли в Шаге 5 нет.</item>
///   <item>Свободные пункты — пул 15, курс 5/2/7/1/2 на пять параметров.
///         Чел/Воля НЕ входят в курс Шага 5, потому что и так растут
///         автоматически при трате в Добродетель.</item>
///   <item>Голод — V5-Hunger, всегда 1 при создании, в Шаге 5 не редактируется.</item>
///   <item>Здоровье — только показ таблицы <see cref="HealthState"/>, без правки.</item>
/// </list>
/// <para>Запас крови (blood pool d10) и рандомный бросок в V20 нами НЕ используются —
/// соответствующие пункты V20 заменены единым «Голодом» (см. дизайн-док).</para>
/// </remarks>
public static class VampireFinishingResolver
{
    public enum Failure
    {
        None,
        UnknownTarget,
        UnknownField,
        InvalidName,
        PoolExhausted,
        AboveCap,
        NotApplicable,
    }

    public readonly struct Decision
    {
        public bool IsSuccess { get; }
        public Failure Failure { get; }
        public string Message { get; }

        private Decision(bool ok, Failure f, string msg)
        {
            IsSuccess = ok; Failure = f; Message = msg;
        }

        public static Decision Ok(string msg = "OK") => new Decision(true, Failure.None, msg);
        public static Decision Fail(Failure f, string msg) => new Decision(false, f, msg);
    }

    /// <summary>На что можно потратить свободный пункт на Шаге 5.</summary>
    public enum FreebieTarget
    {
        Attribute = 1,
        Ability = 2,
        Discipline = 3,
        Background = 4,
        Virtue = 5,
        /// <summary>Человечность (V20 стр. 86): +1 стоит 2 свободных пункта.</summary>
        Humanity = 6,
        /// <summary>Воля (V20 стр. 86): +1 стоит 1 свободный пункт.</summary>
        Willpower = 7,
    }

    // ═══ Формулы производных ═══════════════════════════════════════

    /// <summary>Человечность = Совесть + Самоконтроль + бонус freebie, кэп 10.</summary>
    public static int ComputeHumanity(VampireCharacter draft)
    {
        if (draft == null) return 0;
        var con = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueConscience);
        var scl = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueSelfControl);
        return Math.Min(10, Math.Max(1, con + scl + draft.HumanityBonus));
    }

    /// <summary>Воля = Смелость + бонус freebie, кэп 10.</summary>
    public static int ComputeWillpower(VampireCharacter draft)
    {
        if (draft == null) return 0;
        var cou = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueCourage);
        return Math.Min(10, Math.Max(1, cou + draft.WillpowerBonus));
    }

    /// <summary>Подробная строка расчёта — для UI (только формула, без бонуса).</summary>
    public static string DescribeHumanity(VampireCharacter draft)
    {
        var con = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueConscience);
        var scl = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueSelfControl);
        return $"{con + scl} (Совесть {con} + Самоконтроль {scl})";
    }

    /// <summary>Подробная строка расчёта для Воли (только формула, без бонуса).</summary>
    public static string DescribeWillpower(VampireCharacter draft)
    {
        var cou = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueCourage);
        return $"{cou} (Смелость {cou})";
    }

    // ═══ Freebie 15 на Шаге 5 ══════════════════════════════════════

    /// <summary>Канонический freebie-курс V20 стр. 86.</summary>
    public const int FreebiePool = 15;

    /// <summary>Стоимость за +1 для каждой из целей.</summary>
    public static int CostOf(FreebieTarget t) => t switch
    {
        FreebieTarget.Attribute   => 5,
        FreebieTarget.Ability     => 2,
        FreebieTarget.Discipline  => 7,
        FreebieTarget.Background  => 1,
        FreebieTarget.Virtue      => 2,
        FreebieTarget.Humanity    => 2,
        FreebieTarget.Willpower   => 1,
        _ => int.MaxValue,
    };

    /// <summary>Список дружелюбных названий целей (для UI).</summary>
    public static readonly IReadOnlyList<FreebieTarget> FreebieTargets = new[]
    {
        FreebieTarget.Attribute,
        FreebieTarget.Ability,
        FreebieTarget.Discipline,
        FreebieTarget.Background,
        FreebieTarget.Virtue,
        FreebieTarget.Humanity,
        FreebieTarget.Willpower,
    };

    public static string TargetName(FreebieTarget t, VampireCharacter draft) => t switch
    {
        FreebieTarget.Attribute   => "Характеристика",
        FreebieTarget.Ability     => "Способность",
        FreebieTarget.Discipline  => "Дисциплина",
        FreebieTarget.Background  => "Факт биографии",
        FreebieTarget.Virtue      => "Добродетель",
        FreebieTarget.Humanity    => "Человечность",
        FreebieTarget.Willpower   => "Воля",
        _ => t.ToString(),
    };

    // ═══ Freebie-реестр по черновику ══════════════════════════════════

    private static string Key(FreebieTarget t, string field)
        => $"{(int)t}:{field}";

    /// <summary>Потрачено на конкретную клетку (0 если нет).</summary>
    public static int FreebieSpentOn(VampireCharacter draft, FreebieTarget target, string field)
    {
        if (draft == null) return 0;
        var key = Key(target, field);
        return draft.FreebieSpent.TryGetValue(key, out var v) ? v : 0;
    }

    /// <summary>Суммарно потрачено пунктов из пула 15.</summary>
    public static int ConsumedFreebies(VampireCharacter draft)
    {
        if (draft == null) return 0;
        var sum = 0;
        foreach (var (k, count) in draft.FreebieSpent)
        {
            if (count <= 0) continue;
            var colon = k.IndexOf(':');
            if (colon <= 0) continue;
            if (!int.TryParse(k.Substring(0, colon), out var targetNum)) continue;
            var t = (FreebieTarget)targetNum;
            sum += CostOf(t) * count;
        }
        return sum;
    }

    /// <summary>Сколько свободных пунктов осталось.</summary>
    public static int RemainingFreebies(VampireCharacter draft)
        => Math.Max(0, FreebiePool - ConsumedFreebies(draft));

    // ═══ Allocate / Deallocate / Reset ══════════════════════════════════

    /// <summary>
    /// Потратить freebie на +1 к указанному полю.
    /// <paramref name="virtueAffectingHumanityOrWillpower"/> = true, если трата трогает добродетель
    /// (Humanity/Willpower в резолвере пересчитываются автоматически).
    /// </summary>
    public static Decision AllocateFreebie(
        VampireCharacter draft,
        FreebieTarget target,
        string field,
        out bool virtueAffectingHumanityOrWillpower)
    {
        virtueAffectingHumanityOrWillpower = false;
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (string.IsNullOrWhiteSpace(field))
            return Decision.Fail(Failure.InvalidName, "Имя поля не указано.");

        var cost = CostOf(target);
        if (ConsumedFreebies(draft) + cost > FreebiePool)
            return Decision.Fail(Failure.PoolExhausted,
                $"Пул свободных пунктов ({FreebiePool}) уже исчерпан. Этот параметр стоит {cost}.");

        var (hardCap, _) = GetCaps(target, draft);
        var cur = ReadFieldValue(draft, target, field);
        if (cur + 1 > hardCap)
            return Decision.Fail(Failure.AboveCap,
                $"«{field}» уже на кэпе {hardCap}.");

        if (!WriteFieldValue(draft, target, field, cur + 1, out var err))
            return Decision.Fail(Failure.UnknownField, err);

        var key = Key(target, field);
        var newSpent = new Dictionary<string, int>(draft.FreebieSpent, StringComparer.Ordinal);
        newSpent[key] = (newSpent.TryGetValue(key, out var v) ? v : 0) + 1;
        draft.FreebieSpent = newSpent;

        if (target == FreebieTarget.Virtue)
            virtueAffectingHumanityOrWillpower = true;

        return Decision.Ok($"«{field}» → {cur + 1} (-{cost} свободных).");
    }

    /// <summary>Откатить freebie с указанного поля (если возможно).</summary>
    public static Decision DeallocateFreebie(VampireCharacter draft, FreebieTarget target, string field)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (string.IsNullOrWhiteSpace(field))
            return Decision.Fail(Failure.InvalidName, "Имя поля не указано.");

        var key = Key(target, field);
        if (!draft.FreebieSpent.TryGetValue(key, out var v) || v <= 0)
            return Decision.Fail(Failure.NotApplicable,
                $"На «{field}» нет потраченных свободных пунктов.");

        var cur = ReadFieldValue(draft, target, field);
        if (cur <= 0)
            return Decision.Fail(Failure.NotApplicable,
                $"«{field}» уже на 0.");

        if (!WriteFieldValue(draft, target, field, cur - 1, out var err))
            return Decision.Fail(Failure.UnknownField, err);

        var newSpent = new Dictionary<string, int>(draft.FreebieSpent, StringComparer.Ordinal);
        newSpent[key] = v - 1;
        if (newSpent[key] <= 0) newSpent.Remove(key);
        draft.FreebieSpent = newSpent;

        var cost = CostOf(target);
        return Decision.Ok($"«{field}» → {cur - 1} (+{cost} свободных).");
    }

    /// <summary>Сбросить все freebie-траты (возвращает все потраченные пункты в пул).</summary>
    public static Decision ResetFreebies(VampireCharacter draft)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (draft.FreebieSpent.Count == 0)
            return Decision.Ok("Свободные пункты уже сброшены.");

        var keysToClear = draft.FreebieSpent.Keys.ToArray();
        foreach (var k in keysToClear)
        {
            var colon = k.IndexOf(':');
            if (colon <= 0) continue;
            if (!int.TryParse(k.Substring(0, colon), out var targetNum)) continue;
            var field = k.Substring(colon + 1);
            var target = (FreebieTarget)targetNum;

            // Откатываем по одной записи; при v=1 — удаляем ключ.
            var currentSpent = draft.FreebieSpent.TryGetValue(k, out var cv) ? cv : 0;
            for (int i = 0; i < currentSpent; i++)
            {
                var cur = ReadFieldValue(draft, target, field);
                if (cur <= 0) break;
                if (!WriteFieldValue(draft, target, field, cur - 1, out _)) break;
            }
        }
        // Очищаем реестр одним махом.
        draft.FreebieSpent = new Dictionary<string, int>(StringComparer.Ordinal);
        return Decision.Ok("Свободные пункты сброшены.");
    }

    // ═══ Хелперы: чтение/запись в любую из пяти моделей ═══════════════

    /// <summary>Кэпы по целям: (hardCap, min). Для Humanity/Willpower hardCap = 10 − формула.</summary>
    public static (int hardCap, int min) GetCaps(FreebieTarget t, VampireCharacter draft) => t switch
    {
        FreebieTarget.Attribute   => (5, 1),
        FreebieTarget.Ability     => (5, 0),
        FreebieTarget.Discipline  => (5, 0),
        FreebieTarget.Background  => (5, 1),
        FreebieTarget.Virtue      => (5, 1),
        FreebieTarget.Humanity    => (Math.Max(0, 10 - FormulaHumanity(draft)), 0),
        FreebieTarget.Willpower   => (Math.Max(0, 10 - FormulaWillpower(draft)), 0),
        _ => (5, 0),
    };

    /// <summary>«Голая» формула Чел без бонуса (Совесть + Самоконтроль, кэп 10).</summary>
    private static int FormulaHumanity(VampireCharacter draft)
    {
        if (draft == null) return 0;
        var con = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueConscience);
        var scl = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueSelfControl);
        return Math.Min(10, Math.Max(1, con + scl));
    }

    /// <summary>«Голая» формула Воли без бонуса (Смелость, кэп 10).</summary>
    private static int FormulaWillpower(VampireCharacter draft)
    {
        if (draft == null) return 0;
        var cou = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueCourage);
        return Math.Min(10, Math.Max(1, cou));
    }

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

    /// <summary>Полный текстовый статус Шага 5 для DM-рендера.</summary>
    public static string BuildStatusMessage(VampireCharacter draft)
    {
        if (draft == null) draft = new VampireCharacter();
        var sb = new StringBuilder();
        sb.AppendLine("**Шаг 5 — последние штрихи**");
        sb.AppendLine("Пул 15 свободных пунктов по ценам V20 (хар 5 / спос 2 / диск 7 / факт 1 / доброд 2 / Чел 2 / Воля 1).");
        sb.AppendLine();
        sb.Append("Человечность: **").Append(ComputeHumanity(draft)).AppendLine("**");
        sb.Append("Воля:         **").Append(ComputeWillpower(draft)).AppendLine("**");
        sb.AppendLine("Голод:        1 (стартовое значение, в Шаге 5 не редактируется)");
        sb.Append("Слабость:     ").AppendLine(
            string.IsNullOrEmpty(draft.Weakness)
                ? "(пусто — задаётся автоматически кланом)"
                : draft.Weakness);

        if (draft.Health != null)
        {
            sb.Append("Здоровье:     ").Append(draft.Health.Render())
              .Append("   (ячейки: ").Append(draft.Health.Size)
              .Append(", штраф: ").Append(draft.Health.Penalty)
              .AppendLine(")");
        }
        else
        {
            sb.AppendLine("Здоровье:     (не настроено)");
        }

        sb.AppendLine();
        var consumed = ConsumedFreebies(draft);
        var remaining = FreebiePool - consumed;
        sb.Append("**Свободные пункты: осталось ").Append(remaining)
          .Append(" / ").Append(FreebiePool)
          .Append("** (потрачено ").Append(consumed).Append(").");
        var spent = DescribeSpent(draft);
        if (spent.Count == 0)
        {
            sb.AppendLine(" Пока ничего не потрачено.");
        }
        else
        {
            sb.AppendLine();
            foreach (var line in spent) sb.Append("  • ").AppendLine(line);
        }

        return sb.ToString();
    }
}
