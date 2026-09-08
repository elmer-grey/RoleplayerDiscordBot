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

    /// <summary>Человечность = Совесть + Самоконтроль + бонус freebie, кэп 1..10.
    /// (Минимальный порог 1 — наше защитное ограничение: в чарнике пока нет NPC-режима
    /// при Чел.=0, см. <see cref="VampireFinishingResolver.ComputeRawHumanity"/>.)</summary>
    public static int ComputeHumanity(VampireCharacter draft)
    {
        if (draft == null) return 0;
        var con = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueConscience);
        var scl = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueSelfControl);
        return ClampHumanity(con + scl + draft.HumanityBonus);
    }

    /// <summary>
        /// Сырая Человечность БЕЗ искусственного floor=1 (V20 стр. 333-334: Чел.
        /// может быть 0 → окоченение). Минимум 0, а не 1 — это и есть смысл метода:
        /// возвращать нижнюю реальную границу V20, без нашей защитной обёртки.
        /// </summary>
        public static int ComputeRawHumanity(VampireCharacter draft)
        {
            if (draft == null) return 0;
            var con = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueConscience);
            var scl = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueSelfControl);
            return ClampHumanityUpper(con + scl + draft.HumanityBonus);
        }

    /// <summary>Воля = Смелость + бонус freebie, кэп 1..10.</summary>
    public static int ComputeWillpower(VampireCharacter draft)
    {
        if (draft == null) return 0;
        var cou = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueCourage);
        return ClampWillpower(cou + draft.WillpowerBonus);
    }

    /// <summary>Сырая Воля без искусственного кэпа снизу (В20: Воля может быть 0).</summary>
    public static int ComputeRawWillpower(VampireCharacter draft)
    {
        if (draft == null) return 0;
        var cou = VampireAdvantagesResolver.GetVirtueValue(draft, VampireParameterCatalog.VirtueCourage);
        return ClampWillpowerUpper(cou + draft.WillpowerBonus);
    }

    /// <summary>Кэп Чел. сверху (1..10).</summary>
    private static int ClampHumanity(int v)
        => v < 1 ? 1 : (v > 10 ? 10 : v);

    /// <summary>Кэп Чел. только сверху (0..10). Используется для статуса.</summary>
    private static int ClampHumanityUpper(int v)
        => v < 0 ? 0 : (v > 10 ? 10 : v);

    /// <summary>Кэп Воли сверху (1..10).</summary>
    private static int ClampWillpower(int v)
        => v < 1 ? 1 : (v > 10 ? 10 : v);

    /// <summary>Кэп Воли только сверху (0..10). Используется для статуса.</summary>
    private static int ClampWillpowerUpper(int v)
        => v < 0 ? 0 : (v > 10 ? 10 : v);

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

    /// <summary>
    /// Состояние персонажа, зависящее от сырых значений Чел. и Воли
    /// (без искусственного кэпа 1). V20 стр. 333-334: при Чел.=0 персонаж
    /// перестаёт быть собой (NPC); при Воле=0 — безвольный.
    /// </summary>
    public enum CharacterStatus
    {
        /// <summary>Обычное состояние.</summary>
        Normal,

        /// <summary>Чел.=0 — окоченение (V20 стр. 334).</summary>
        Withered,

        /// <summary>Воля=0 — безвольный (по нашему решению, см. design).</summary>
        Enervated,

        /// <summary>Оба значения = 0 — финальное состояние.</summary>
        Shattered,
    }

    /// <summary>
    /// Вычислить статус персонажа на основе сырых значений Чел. и Воли
    /// (без искусственного кэпа 1).
    /// </summary>
    public static CharacterStatus ComputeStatus(VampireCharacter draft)
    {
        if (draft == null) return CharacterStatus.Normal;
        var h = ComputeRawHumanity(draft);
        var w = ComputeRawWillpower(draft);
        if (h <= 0 && w <= 0) return CharacterStatus.Shattered;
        if (h <= 0) return CharacterStatus.Withered;
        if (w <= 0) return CharacterStatus.Enervated;
        return CharacterStatus.Normal;
    }

    /// <summary>
    /// Текстовое описание статуса (для UI/embed).
    /// </summary>
    public static string DescribeStatus(CharacterStatus status) => status switch
    {
        CharacterStatus.Normal => "Норма",
        CharacterStatus.Withered => "⚠ Окоченение (Чел.=0)",
        CharacterStatus.Enervated => "⚠ Безвольный (Воля=0)",
        CharacterStatus.Shattered => "🕱 Полное опустошение (Чел.=0 и Воля=0)",
        _ => "",
    };
}

