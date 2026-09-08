using System;
using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM;

/// <summary>
/// Чистая логика расчёта урона VtM V20 (combat).
/// Не делает сетевых вызовов.
/// </summary>
/// <remarks>
/// <para>Источник правил: <c>v20_p280-320.txt</c>, стр. 301-302.</para>
/// <list type="bullet">
/// <item>База урона = база манёвра/оружия (например, нож = Сила+1,
/// меч = Сила+2, револьвер .38 = 4).</item>
/// <item>Каждый доп. успех проверки атаки <em>сверх первого</em>
/// увеличивает пул урона на 1d10
/// («каждый дополнительный успех при проверке атаки увеличивает пул проверки
/// урона от этой атаки на один d10» — V20, стр. 301).</item>
/// <item>Пул урона не может быть меньше базы манёвра — попадание
/// гарантирует хотя бы базовый пул.</item>
/// <item>Сложность проверки урона = 6. Каждый успех = одно повреждение.</item>
/// <item>Проверка урона не может закончиться провалом —
/// только неудачей (= «по касательной», 0 повреждений).</item>
/// </list>
/// </remarks>
public static class VampireCombatDamageResolver
{
    /// <summary>Сложность проверки урона (V20, стр. 301).</summary>
    public const int DamageRollDifficulty = 6;

    /// <summary>Максимальное количество кубов урона (предохранитель).</summary>
    public const int MaxDamagePool = 30;

    /// <summary>
    /// Результат расчёта пула урона по формуле V20.
    /// </summary>
    /// <param name="BaseManeuver">База манёвра/оружия. Уже включает Силу,
    /// если база её требует (например, «Сила + 1» для ножа).</param>
    /// <param name="ExcessSuccesses">Количество успехов атаки сверх первого
    /// (т.е. <c>max(0, attackerSuccesses − 1)</c>). Это именно то, что
    /// добавляется к пулу урона.</param>
    /// <param name="DamagePoolSize">Итоговый пул урона, ограниченный
    /// <see cref="MaxDamagePool"/>. Не бывает меньше <c>BaseManeuver</c>.</param>
    public sealed record Pool(
        int BaseManeuver,
        int ExcessSuccesses,
        int DamagePoolSize)
    {
        /// <summary>Сумма «база + дополнительные успехи атаки» (без кэпа).</summary>
        public int RawTotal => BaseManeuver + ExcessSuccesses;
    }

    /// <summary>
    /// Посчитать пул урона по формуле V20 (стр. 301):
    /// <c>pool = max(BaseManeuver, BaseManeuver + max(0, attackerSuccesses − 1))</c>.
    /// </summary>
    /// <param name="baseManeuver">База манёвра/оружия (≥ 0).</param>
    /// <param name="attackerSuccesses">Успехи проверки атаки (≥ 0).</param>
    public static Pool ComputePool(int baseManeuver, int attackerSuccesses)
    {
        ValidateNonNegative(baseManeuver, nameof(baseManeuver));
        ValidateNonNegative(attackerSuccesses, nameof(attackerSuccesses));

        // Первый успех не прибавляется к пулу; каждый последующий — +1d10.
        var excess = Math.Max(0, attackerSuccesses - 1);
        var raw = baseManeuver + excess;
        // Пул урона не может быть меньше базы манёвра — попадание есть попадание.
        var floored = Math.Max(raw, baseManeuver);
        var capped = Math.Min(floored, MaxDamagePool);

        return new Pool(baseManeuver, excess, capped);
    }

    /// <summary>
    /// Записать результат проверки урона в лог бросков: каждый куб ≥6 даёт 1 успех,
    /// сложность = <see cref="DamageRollDifficulty"/>.
    /// </summary>
    /// <param name="dice">Значения кубов (1..10).</param>
    /// <returns>Количество успехов (0..<c>damagePool</c>).</returns>
    public static int CountDamageSuccesses(IReadOnlyList<int> dice)
    {
        if (dice == null) throw new ArgumentNullException(nameof(dice));
        if (dice.Count == 0) throw new ArgumentException("Пул урона пуст.", nameof(dice));
        if (dice.Any(d => d < 1 || d > 10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(dice),
                "Кубы должны быть в диапазоне 1..10.");
        }
        return dice.Count(d => d >= DamageRollDifficulty);
    }

    /// <summary>
    /// Краткая текстовая справка по боевой формуле V20 для команды <c>/vampire_combat_help</c>.
    /// </summary>
    public static IReadOnlyList<string> BuildHelpLines()
    {
        var lines = new List<string>
        {
            "**Бой в V20 проходит в три фазы:**",
            "• *Инициатива* — Ловкость + Смекалка (или 6 + Лов + Смек).",
            "• *Атака* — характеристика + навык. Сложность 6 (по умолчанию).",
            "• *Результат* — пул урона и проверка на прочность.",
            "",
            "**Формула урона (стр. 301):** `pool = база_манёвра + max(0, успехи_атаки − 1)`.",
            "  Первый успех не прибавляется. Каждый последующий — +1d10.",
            "  Пул не может быть меньше базы манёвра. Проверка урона — только неудача, не провал.",
            "  Сложность проверки урона = 6. Каждый успех = 1 повреждение.",
            "",
            "**Типы повреждений:**",
            "• `/` лёгкое — Выносливость + Стойкость на прочность.",
            "• `Х` тяжёлое — то же; смертным не проходится.",
            "• `Ж` губительное — прочность только Стойкость (огонь/солнце/клыки/когти).",
            "",
            "**Ближний бой — манёвры (стр. 309):**",
        };
        foreach (var m in VampireManeuverCatalog.Melee)
        {
            lines.Add($"  • *{m.Name}* — `{m.Stat}`, точность `{m.Accuracy:+#;-#;0}`, урон `{m.DamageFormula}`" +
                      (m.DamageKind != DamageType.None ? $" ({DamageTag(m.DamageKind)})" : "") +
                      (m.Notes != null ? $". {m.Notes}" : ""));
        }
        lines.Add("");
        lines.Add("**Дистанционный бой — манёвры (стр. 309):**");
        foreach (var m in VampireManeuverCatalog.Ranged)
        {
            lines.Add($"  • *{m.Name}* — `{m.Stat}`, точность `{m.Accuracy:+#;-#;0}`, урон `{m.DamageFormula}`" +
                      (m.Notes != null ? $". {m.Notes}" : ""));
        }
        lines.Add("");
        lines.Add("**Общие модификаторы (стр. 303-304):**");
        foreach (var g in VampireManeuverCatalog.General)
        {
            lines.Add($"  • *{g.Name}* — {g.Effect}");
        }
        lines.Add("");
        lines.Add("**Таблица брони (стр. 310):**");
        foreach (var a in VampireManeuverCatalog.Armor)
        {
            lines.Add($"  • *{a.Name}* — показатель {a.Protection:+#;-#;0}, удобство {a.ComfortModifier:+#;-#;0}");
        }
        lines.Add("");
        lines.Add("**Холодное оружие (стр. 310):**");
        foreach (var w in VampireManeuverCatalog.MeleeWeapons)
        {
            lines.Add($"  • *{w.Name}* — `{w.DamageFormula}` ({DamageTag(w.DamageType)})");
        }
        lines.Add("");
        lines.Add("**Огнестрельное оружие (стр. 311):**");
        foreach (var w in VampireManeuverCatalog.RangedWeapons)
        {
            lines.Add($"  • *{w.Name}* — база {w.DamageBase}, дист. {w.Range}/{w.MaxRange} м, скоростр. {w.RateOfFire}, боезапас {w.Magazine}" +
                      (w.Notes != null ? $". {w.Notes}" : ""));
        }
        lines.Add("");
        lines.Add("**Здоровье (стр. 312) — модификаторы пула:**");
        lines.Add("  • `помят` +0 · `легко ранен` −1 · `ранен` −1 · `серьёзно ранен` −2");
        lines.Add("  • `тяжело ранен` −2 · `едва жив` −5 · `при смерти` — без сознания.");
        return lines;
    }

    private static string DamageTag(DamageType kind) => kind switch
    {
        DamageType.Bashing => "лёгкие (/)",
        DamageType.Lethal => "тяжёлые (Х)",
        DamageType.Aggravated => "губительные (Ж)",
        _ => "без урона",
    };

    private static void ValidateNonNegative(int value, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                "Значение не может быть отрицательным.");
        }
    }
}
