using System;
using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM;

/// <summary>
/// Логика freebie-пула Шага 5: цены, цели, реестр, allocate/deallocate/reset.
/// </summary>
public static partial class VampireFinishingResolver
{
    /// <summary>Канонический freebie-курс V20 стр. 86.</summary>
    public const int FreebiePool = 15;

    /// <summary>Базовый freebie-пул (V20 стр. 86). Всегда 15.</summary>
    /// <remarks>
    /// Используйте <see cref="EffectiveFreebiePool"/> для учёта Merits/Flaws.
    /// Эта константа оставлена для обратной совместимости и прямого расчёта
    /// там, где Merits/Flaws не нужны (например, в миграции).
    /// </remarks>
    public const int BaseFreebiePool = 15;

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

    /// <summary>Имя цели на русском (для UI).</summary>
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

    // ─── Freebie-реестр по черновику ─────────────────────────────────

    private static string Key(FreebieTarget t, string field)
        => $"{(int)t}:{field}";

    /// <summary>Потрачено на конкретную клетку (0 если нет).</summary>
    public static int FreebieSpentOn(VampireCharacter draft, FreebieTarget target, string field)
    {
        if (draft == null) return 0;
        var key = Key(target, field);
        return draft.FreebieSpent.TryGetValue(key, out var v) ? v : 0;
    }

    /// <summary>Суммарно потрачено пунктов из пула.</summary>
    /// <remarks>
    /// Считает ТОЛЬКО траты freebie (по FreebieSpent). Стоимость Merits уже учтена
    /// в <see cref="EffectiveFreebiePool"/> как вычет из потолка, поэтому здесь
    /// не должна учитываться повторно — иначе Merits вычитаются дважды
    /// (V20 стр. 86: Merits покупаются из того же пула, что и freebie).
    /// </remarks>
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

    /// <summary>Эффективный freebie-пул на текущий момент (V20 стр. 86, 92).</summary>
    /// <remarks>
    /// Формула: 15 (базовый пул) + сумма Flaws − сумма Merits.
    /// Flaws дают бонусные пункты, Merits тратят freebie.
    /// </remarks>
    public static int EffectiveFreebiePool(VampireCharacter draft)
        => BaseFreebiePool
           + VampireMeritsFlawsResolver.FlawsCost(draft)
           - VampireMeritsFlawsResolver.MeritsCost(draft);

    /// <summary>Сколько свободных пунктов осталось (с учётом Merits и Flaws).</summary>
    public static int RemainingFreebies(VampireCharacter draft)
        => Math.Max(0, EffectiveFreebiePool(draft) - ConsumedFreebies(draft));

        /// <summary>Все freebie потрачены?</summary>
        public static bool FreebiesExhausted(VampireCharacter draft)
            => RemainingFreebies(draft) == 0;

        /// <summary>Шаг 5 подтверждён пользователем (лист заморожен для специализаций)?</summary>
        /// <remarks>
        /// Устанавливается через <see cref="ConfirmStep5"/>. После этого специализации
        /// разрешены, даже если в пуле ещё остались пункты — игрок явно согласился
        /// их «заморозить» и больше не менять.
        /// </remarks>
        public static bool IsStep5Finalized(VampireCharacter draft)
            => draft != null && draft.Step5Finalized;

        /// <summary>
        /// Подтвердить завершение Шага 5: выставить флаг заморозки.
        /// Используется визардом после диалога «Вы уверены?».
        /// </summary>
        public static void ConfirmStep5(VampireCharacter draft)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            draft.Step5Finalized = true;
            draft.Step5FinalizedAt = DateTime.UtcNow;
        }

        /// <summary>Откатить подтверждение Шага 5 (для отмены игроком).</summary>
        public static void UnconfirmStep5(VampireCharacter draft)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            draft.Step5Finalized = false;
            draft.Step5FinalizedAt = null;
        }

        /// <summary>Специализации разрешены?</summary>
        /// <remarks>
        /// Условие разрешения: либо все freebie потрачены (пул = 0),
        /// либо игрок явно подтвердил завершение Шага 5 через диалог.
        /// </remarks>
        public static bool SpecializationsAllowed(VampireCharacter draft)
            => draft != null && (FreebiesExhausted(draft) || IsStep5Finalized(draft));

        /// <summary>
        /// Пометить все freebie как потраченные (без фактической траты на параметры).
        /// Используется юнит-тестами для перевода черновика в состояние «пост-Шаг-5»,
        /// когда специализации уже разрешены. В production не вызывается.
        /// </summary>
        public static void MarkFreebiesExhausted(VampireCharacter draft)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            // Записываем «+1 Background Факт» столько раз, сколько нужно, чтобы добить до эффективного пула.
            var cost = CostOf(FreebieTarget.Background); // = 1
            var need = Math.Max(0, EffectiveFreebiePool(draft) - ConsumedFreebies(draft)) / cost;
            for (int i = 0; i < need; i++)
            {
                draft.FreebieSpent[Key(FreebieTarget.Background, $"_sentinel_{i}")] = 1;
            }
        }

    // ─── Allocate / Deallocate / Reset ──────────────────────────────

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
        // Лимит на количество разных дисциплин / фактов: при попытке добавить +1 к новому
        // полю (которого ещё нет ни в основном, ни в freebie-словаре) — проверяем суммарный
        // размер обоих словарей.
        if (target == FreebieTarget.Background
            && draft.FreebieBackgrounds != null
            && !draft.FreebieBackgrounds.ContainsKey(field)
            && (draft.Backgrounds?.ContainsKey(field) != true))
        {
            int existing = (draft.Backgrounds?.Count ?? 0) + draft.FreebieBackgrounds.Count;
            if (existing + 1 > VampireAdvantagesCatalog.MaxBackgroundsPerCharacter)
                return Decision.Fail(Failure.AboveCap,
                    $"Достигнут лимит разных фактов биографии: {VampireAdvantagesCatalog.MaxBackgroundsPerCharacter}.");
        }
        if (target == FreebieTarget.Discipline
            && draft.FreebieDisciplines != null
            && !draft.FreebieDisciplines.ContainsKey(field)
            && (draft.Disciplines?.ContainsKey(field) != true))
        {
            int existing = (draft.Disciplines?.Count ?? 0) + draft.FreebieDisciplines.Count;
            if (existing + 1 > VampireAdvantagesCatalog.MaxDisciplinesPerCharacter)
                return Decision.Fail(Failure.AboveCap,
                    $"Достигнут лимит разных дисциплин: {VampireAdvantagesCatalog.MaxDisciplinesPerCharacter}.");
        }
        if (ConsumedFreebies(draft) + cost > EffectiveFreebiePool(draft))
            return Decision.Fail(Failure.PoolExhausted,
                $"Эффективный пул ({EffectiveFreebiePool(draft)}) уже исчерпан. Этот параметр стоит {cost}.");

        var (hardCap, _) = GetCaps(target, draft);
        // curTotal — итоговое значение (Шаг 2/3/4 + freebie), нужно для проверки hardCap.
        var curTotal = target switch
        {
            FreebieTarget.Attribute   => ReadAttributeValue(draft, field),
            FreebieTarget.Ability     => ReadAbilityValue(draft, field),
            FreebieTarget.Discipline  => VampireAdvantagesResolver.GetDisciplineValue(draft, field),
            FreebieTarget.Background  => VampireAdvantagesResolver.GetBackgroundRank(draft, field),
            _ => ReadFieldValue(draft, target, field),
        };
        if (curTotal + 1 > hardCap)
            return Decision.Fail(Failure.AboveCap,
                $"«{field}» уже на кэпе {hardCap}.");

        // В dict пишем ТОЛЬКО freebie-вклад, не итог. Для Attribute/Ability/Discipline/Background
        // ReadFieldValue читает соответствующий *freebie* dict, поэтому WriteFieldValue
        // запишет корректную дельту (+1).
        var nextFreebie = ReadFieldValue(draft, target, field) + 1;
        if (!WriteFieldValue(draft, target, field, nextFreebie, out var err))
            return Decision.Fail(Failure.UnknownField, err);

        var key = Key(target, field);
        var newSpent = new Dictionary<string, int>(draft.FreebieSpent, StringComparer.Ordinal);
        newSpent[key] = (newSpent.TryGetValue(key, out var v) ? v : 0) + 1;
        draft.FreebieSpent = newSpent;

        if (target == FreebieTarget.Virtue)
            virtueAffectingHumanityOrWillpower = true;

        return Decision.Ok($"«{field}» → {curTotal + 1} (-{cost} свободных).");
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

        var curTotal = target switch
        {
            FreebieTarget.Attribute   => ReadAttributeValue(draft, field),
            FreebieTarget.Ability     => ReadAbilityValue(draft, field),
            FreebieTarget.Discipline  => VampireAdvantagesResolver.GetDisciplineValue(draft, field),
            FreebieTarget.Background  => VampireAdvantagesResolver.GetBackgroundRank(draft, field),
            _ => ReadFieldValue(draft, target, field),
        };
        if (curTotal <= 0)
            return Decision.Fail(Failure.NotApplicable,
                $"«{field}» уже на 0.");

        var nextFreebie = ReadFieldValue(draft, target, field) - 1;
        if (!WriteFieldValue(draft, target, field, nextFreebie, out var err))
            return Decision.Fail(Failure.UnknownField, err);

        var newSpent = new Dictionary<string, int>(draft.FreebieSpent, StringComparer.Ordinal);
        newSpent[key] = v - 1;
        if (newSpent[key] <= 0) newSpent.Remove(key);
        draft.FreebieSpent = newSpent;

        var cost = CostOf(target);
        return Decision.Ok($"«{field}» → {curTotal - 1} (+{cost} свободных).");
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

            var currentSpent = draft.FreebieSpent.TryGetValue(k, out var cv) ? cv : 0;
            for (int i = 0; i < currentSpent; i++)
            {
                var cur = ReadFieldValue(draft, target, field);
                if (cur <= 0) break;
                if (!WriteFieldValue(draft, target, field, cur - 1, out _)) break;
            }
        }
        draft.FreebieSpent = new Dictionary<string, int>(StringComparer.Ordinal);
        return Decision.Ok("Свободные пункты сброшены.");
    }
}
