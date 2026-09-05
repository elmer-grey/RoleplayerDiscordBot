using System;
using System.Linq;

namespace RPBot.VtM;

/// <summary>
/// Резолвер Достоинств/Недостатков: добавление, удаление, валидация.
/// </summary>
/// <remarks>
/// <para>Правила V20 стр. 485+ и 523+:</para>
/// <list type="bullet">
///   <item>Merits (достоинства) — покупаются за freebie. Цена 1..7.</item>
///   <item>Flaws (недостатки) — дают бонусные freebie при создании. Цена 1..7.</item>
///   <item>На Шаге 5 — выбор из каталога (<see cref="VampireMeritsFlawsCatalog"/>)
///         или свободный ввод (цена выбирается рассказчиком).</item>
///   <item>Имена регистро-независимы, но в словаре хранятся в канонической форме.</item>
/// </list>
/// <para>UI и freebie-пул НЕ интегрированы здесь — это реестр и валидация.
/// Шаг 5 интегрирует их через <see cref="VampireFinishingResolver"/> отдельно.</para>
/// </remarks>
public static class VampireMeritsFlawsResolver
{
    public enum Failure
    {
        None,
        InvalidName,
        InvalidCost,
        DuplicateMerit,
        DuplicateFlaw,
        NotFound,
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

    // ─── Read helpers ───────────────────────────────────────────────

    /// <summary>Сумма цен всех Merits.</summary>
    public static int MeritsCost(VampireCharacter draft)
    {
        if (draft == null) return 0;
        return draft.Merits?.Values.Sum() ?? 0;
    }

    /// <summary>Сумма цен всех Flaws.</summary>
    public static int FlawsCost(VampireCharacter draft)
    {
        if (draft == null) return 0;
        return draft.Flaws?.Values.Sum() ?? 0;
    }

    /// <summary>Количество Merits у персонажа.</summary>
    public static int MeritsCount(VampireCharacter draft)
        => draft?.Merits?.Count ?? 0;

    /// <summary>Количество Flaws у персонажа.</summary>
    public static int FlawsCount(VampireCharacter draft)
        => draft?.Flaws?.Count ?? 0;

    // ─── Add / Remove ──────────────────────────────────────────────

    /// <summary>
    /// Добавить Merit персонажу. Цена обязательно в диапазоне 1..7.
    /// </summary>
    /// <remarks>
    /// Каталог (<see cref="VampireMeritsFlawsCatalog"/>) задаёт рекомендованную цену;
    /// если она нужна — позовите <see cref="VampireMeritsFlawsCatalog.FindMerit"/>(name)!.Cost.
    /// </remarks>
    public static Decision AddMerit(VampireCharacter draft, string name, int cost)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (string.IsNullOrWhiteSpace(name))
            return Decision.Fail(Failure.InvalidName, "Имя достоинства не указано.");

        var canonical = FindCanonical(name, isMerit: true);
        if (canonical == null)
            return Decision.Fail(Failure.InvalidName,
                $"«{name}» нет в каталоге. Используйте каноническое название или добавьте в каталог.");

        if (draft.Merits.ContainsKey(canonical))
            return Decision.Fail(Failure.DuplicateMerit,
                $"«{canonical}» уже добавлено.");

        if (!VampireMeritsFlawsCatalog.IsValidCost(cost))
            return Decision.Fail(Failure.InvalidCost,
                $"Цена {cost} вне диапазона 1..7.");

        var newMerits = new System.Collections.Generic.Dictionary<string, int>(
            draft.Merits ?? new System.Collections.Generic.Dictionary<string, int>(),
            StringComparer.Ordinal);
        newMerits[canonical] = cost;
        draft.Merits = newMerits;
        return Decision.Ok($"«{canonical}» добавлено (цена {cost}).");
    }

    /// <summary>
    /// Добавить Merit с ценой из каталога. Удобный метод для UI без перерасчёта.
    /// </summary>
    public static Decision AddMeritFromCatalog(VampireCharacter draft, string name)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        var canonical = FindCanonical(name, isMerit: true);
        if (canonical == null)
            return Decision.Fail(Failure.InvalidName, $"«{name}» нет в каталоге.");
        var entry = VampireMeritsFlawsCatalog.FindMerit(canonical);
        return AddMerit(draft, canonical, entry!.Cost);
    }

    /// <summary>
    /// Добавить Flaw персонажу. Цена обязательно в диапазоне 1..7.
    /// </summary>
    public static Decision AddFlaw(VampireCharacter draft, string name, int cost)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (string.IsNullOrWhiteSpace(name))
            return Decision.Fail(Failure.InvalidName, "Имя недостатка не указано.");

        var canonical = FindCanonical(name, isMerit: false);
        if (canonical == null)
            return Decision.Fail(Failure.InvalidName,
                $"«{name}» нет в каталоге. Используйте каноническое название или добавьте в каталог.");

        if (draft.Flaws.ContainsKey(canonical))
            return Decision.Fail(Failure.DuplicateFlaw,
                $"«{canonical}» уже добавлено.");

        if (!VampireMeritsFlawsCatalog.IsValidCost(cost))
            return Decision.Fail(Failure.InvalidCost,
                $"Цена {cost} вне диапазона 1..7.");

        var newFlaws = new System.Collections.Generic.Dictionary<string, int>(
            draft.Flaws ?? new System.Collections.Generic.Dictionary<string, int>(),
            StringComparer.Ordinal);
        newFlaws[canonical] = cost;
        draft.Flaws = newFlaws;
        return Decision.Ok($"«{canonical}» добавлено (цена {cost}).");
    }

    /// <summary>
    /// Добавить Flaw с ценой из каталога. Удобный метод для UI без перерасчёта.
    /// </summary>
    public static Decision AddFlawFromCatalog(VampireCharacter draft, string name)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        var canonical = FindCanonical(name, isMerit: false);
        if (canonical == null)
            return Decision.Fail(Failure.InvalidName, $"«{name}» нет в каталоге.");
        var entry = VampireMeritsFlawsCatalog.FindFlaw(canonical);
        return AddFlaw(draft, canonical, entry!.Cost);
    }

    /// <summary>Убрать Merit по имени. Если такого нет — NotFound.</summary>
    public static Decision RemoveMerit(VampireCharacter draft, string name)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (string.IsNullOrWhiteSpace(name))
            return Decision.Fail(Failure.InvalidName, "Имя не указано.");

        var canonical = FindCanonical(name, isMerit: true);
        if (canonical == null || !draft.Merits.ContainsKey(canonical))
            return Decision.Fail(Failure.NotFound, $"«{name}» не найдено среди достоинств.");

        var newMerits = new System.Collections.Generic.Dictionary<string, int>(
            draft.Merits, StringComparer.Ordinal);
        newMerits.Remove(canonical);
        draft.Merits = newMerits;
        return Decision.Ok($"«{canonical}» убрано.");
    }

    /// <summary>Убрать Flaw по имени. Если такого нет — NotFound.</summary>
    public static Decision RemoveFlaw(VampireCharacter draft, string name)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (string.IsNullOrWhiteSpace(name))
            return Decision.Fail(Failure.InvalidName, "Имя не указано.");

        var canonical = FindCanonical(name, isMerit: false);
        if (canonical == null || !draft.Flaws.ContainsKey(canonical))
            return Decision.Fail(Failure.NotFound, $"«{name}» не найдено среди недостатков.");

        var newFlaws = new System.Collections.Generic.Dictionary<string, int>(
            draft.Flaws, StringComparer.Ordinal);
        newFlaws.Remove(canonical);
        draft.Flaws = newFlaws;
        return Decision.Ok($"«{canonical}» убрано.");
    }

    // ─── Helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Найти каноническое имя (с учётом регистра) в каталоге.
    /// Возвращает null, если ничего не найдено.
    /// </summary>
    private static string? FindCanonical(string name, bool isMerit)
    {
        var source = isMerit
            ? (System.Collections.Generic.IEnumerable<string>)VampireMeritsFlawsCatalog.Merits.Select(m => m.Name)
            : VampireMeritsFlawsCatalog.Flaws.Select(f => f.Name);
        foreach (var n in source)
            if (n.Equals(name, StringComparison.OrdinalIgnoreCase))
                return n;
        return null;
    }
}
