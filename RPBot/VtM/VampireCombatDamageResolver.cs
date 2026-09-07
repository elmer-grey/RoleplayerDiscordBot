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
/// <item>Каждый доп. успех проверки атаки увеличивает пул урона на 1d10.</item>
/// <item>У каждой атаки есть параметр «урон» — зависит от силы атакующего
/// либо заранее определённое число (для оружия).</item>
/// <item>Сложность проверки урона = 6. Каждый успех = одно повреждение.</item>
/// <item>Каждое превышение успехов атаки над успехами защиты добавляет +1 к пулу урона
/// (по согласованию с пользователем — формула 3 из roadmap).</item>
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
    /// <param name="BaseManeuver">База манёвра (например, 1 для кулака/когтей,
    /// значение из таблицы оружия для конкретного оружия).</param>
    /// <param name="AttackerStrength">Сила атакующего (1..5).</param>
    /// <param name="NetAttackSuccesses">Превышение успехов атаки над успехами защиты.
    /// Может быть ≤ 0 — тогда пул урона всё равно равен базе+сила
    /// (без надбавки за «лишние» успехи).</param>
    /// <param name="DamagePoolSize">Итоговый пул урона, ограниченный <see cref="MaxDamagePool"/>.</param>
    public sealed record Pool(
        int BaseManeuver,
        int AttackerStrength,
        int NetAttackSuccesses,
        int DamagePoolSize)
    {
        /// <summary>Сумма «база + Сила + дополнительные успехи атаки» (без кэпа).</summary>
        public int RawTotal => BaseManeuver + AttackerStrength + Math.Max(0, NetAttackSuccesses);
    }

    /// <summary>
    /// Посчитать пул урона по формуле V20:
    /// <c>база манёвра + Сила атакующего + max(0, успехи_атаки − успехи_защиты)</c>.
    /// </summary>
    /// <param name="baseManeuver">База манёвра (≥ 0).</param>
    /// <param name="attackerStrength">Сила атакующего (1..5).</param>
    /// <param name="attackerSuccesses">Успехи проверки атаки (≥ 0).</param>
    /// <param name="defenderSuccesses">Успехи проверки защиты (≥ 0).</param>
    public static Pool ComputePool(
        int baseManeuver,
        int attackerStrength,
        int attackerSuccesses,
        int defenderSuccesses)
    {
        ValidateNonNegative(baseManeuver, nameof(baseManeuver));
        ValidateNonNegative(attackerSuccesses, nameof(attackerSuccesses));
        ValidateNonNegative(defenderSuccesses, nameof(defenderSuccesses));
        if (attackerStrength < 1 || attackerStrength > 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attackerStrength),
                attackerStrength,
                "Сила атакующего должна быть в диапазоне 1..5.");
        }

        var net = attackerSuccesses - defenderSuccesses;
        var raw = baseManeuver + attackerStrength + Math.Max(0, net);
        var capped = Math.Min(raw, MaxDamagePool);

        return new Pool(baseManeuver, attackerStrength, net, capped);
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
    public static IReadOnlyList<string> BuildHelpLines() => new[]
    {
        "**1. Атака.** /vampire_damage бросает характеристика + навык (или дисциплина) → N успехов.",
        "**2. Защита.** Атакуемый бросает Ловкость + Защита (или Дисциплина, или Сил. волю) → M успехов.",
        "**3. Пул урона.** База манёвра + Сила атакующего + (N − M) при N > M.",
        "**4. Проверка урона.** Бросок пула d10, сложность 6, каждый успех = 1 повреждение.",
        "Типы повреждений: лёгкое (/), тяжёлое (Х), губительное (Ж — огонь/солнце/клыки/когти вампиров).",
        "Броня снижает базовый пул урона, см. таблицу оружия и примечания по защите.",
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
