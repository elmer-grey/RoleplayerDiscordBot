using System;
using System.Collections.Generic;
using System.Reflection;

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
public static partial class VampireFinishingResolver
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

    /// <summary>Шкала здоровья загружена и готова к использованию (V20: 7 ячеек).</summary>
    public static void EnsureHealth(VampireCharacter draft)
    {
        if (draft == null) return;
        if (draft.Health == null)
            draft.Health = new HealthState(HealthTrackSize);
    }

    /// <summary>Размер шкалы здоровья по V20 (стр. 263): фиксировано 7 ячеек.</summary>
    public const int HealthTrackSize = 7;

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
}

