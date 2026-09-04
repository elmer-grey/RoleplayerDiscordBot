using System;
using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM;

/// <summary>
/// Разовая миграция персонажей, сохранённых до разделения Step/freebie
/// для дисциплин и фактов.
///
/// <para>До Шага 6 (финального рефакторинга) freebie на дисциплины/факты
/// писался в те же <see cref="VampireCharacter.Disciplines"/> /
/// <see cref="VampireCharacter.Backgrounds"/> dict'ы, что и Шаг 4.1/4.2.
/// После рефакторинга шаги хранятся отдельно в
/// <see cref="VampireCharacter.FreebieDisciplines"/> и
/// <see cref="VampireCharacter.FreebieBackgrounds"/>, а гейты шага
/// смотрят только Step-словарь.</para>
///
/// <para>Без миграции у старых персонажей
/// <see cref="VampireAdvantagesResolver.IsDisciplinesComplete"/>
/// вернул бы <c>false</c> (т.к. в Step лежит Step+freebie вместе).
/// Чтобы не терять данные, переносим «излишек» сверх пула шага в
/// соответствующий Freebie-словарь, сохраняя полное значение для листа.</para>
///
/// <para>Миграция идемпотентна: если уже всё разделено (или ещё не было
/// freebie), ничего не меняется.</para>
/// </summary>
public static class VampireFreebieSplitMigration
{
    /// <summary>
    /// Применить миграцию ко всем персонажам. Возвращает количество
    /// персонажей, у которых реально что-то поменялось.
    /// </summary>
    public static int Migrate(IDictionary<string, VampireCharacter> characters)
    {
        if (characters == null || characters.Count == 0) return 0;

        int changed = 0;
        foreach (var c in characters.Values)
        {
            if (c == null) continue;
            if (MigrateCharacter(c)) changed++;
        }
        return changed;
    }

    /// <summary>
    /// Миграция одного персонажа. Возвращает true, если были изменения.
    /// </summary>
    public static bool MigrateCharacter(VampireCharacter c)
    {
        if (c == null) return false;

        bool any = false;

        // Discipline: pool = 3 (V20)
        bool discChanged = SplitOverflow(
            c.Disciplines, c.FreebieDisciplines,
            VampireAdvantagesCatalog.DisciplinePool,
            VampireAdvantagesCatalog.PerFieldCap,
            out var newDiscStep, out var newDiscFree);
        if (discChanged) { c.Disciplines = newDiscStep; c.FreebieDisciplines = newDiscFree; }
        any |= discChanged;

        // Background: pool = 5 (V20)
        bool bgChanged = SplitOverflow(
            c.Backgrounds, c.FreebieBackgrounds,
            VampireAdvantagesCatalog.BackgroundPool,
            VampireAdvantagesCatalog.PerFieldCap,
            out var newBgStep, out var newBgFree);
        if (bgChanged) { c.Backgrounds = newBgStep; c.FreebieBackgrounds = newBgFree; }
        any |= bgChanged;

        return any;
    }

    /// <summary>
    /// Если сумма <paramref name="stepDict"/> строго больше <paramref name="pool"/>,
    /// переносим «излишек» в <paramref name="freebieDict"/>, начиная с самых
    /// больших значений. В каждой записи оставляем в Step хотя бы 1 (если
    /// возможно), чтобы не «обнулять» существующие дисциплины/факты.
    ///
    /// <para>После прохода <c>Step.Sum() == pool</c>. Если при переносе
    /// <see cref="VampireAdvantagesCatalog.PerFieldCap"/> превышен — это
    /// означает, что исходные данные были повреждены/завышены; мы не
    /// клампим Free (иначе потеряем данные), лишь фиксируем факт
    /// переполнения.</para>
    /// </summary>
    private static bool SplitOverflow(
        Dictionary<string, int> stepDict,
        Dictionary<string, int> freebieDict,
        int pool,
        int clampTo,
        out Dictionary<string, int> updatedStep,
        out Dictionary<string, int> updatedFree)
    {
        updatedStep = stepDict;
        updatedFree = freebieDict;

        if (stepDict == null || stepDict.Count == 0) return false;

        int sum = stepDict.Values.Sum(v => Math.Max(0, v));
        int overflow = sum - pool;
        if (overflow <= 0) return false;

        var step = new Dictionary<string, int>(stepDict, StringComparer.Ordinal);
        var free = freebieDict != null
            ? new Dictionary<string, int>(freebieDict, StringComparer.Ordinal)
            : new Dictionary<string, int>(StringComparer.Ordinal);

        // Идём от больших к меньшим. В каждой записи оставляем хотя бы 1
        // в Step, если есть выбор (overflow меньше cur и pool это позволяет).
        foreach (var name in step.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList())
        {
            if (overflow <= 0) break;
            int cur = Math.Max(0, step[name]);
            int maxTake = (cur > 0 && overflow < cur && pool >= 1)
                ? Math.Min(cur - 1, overflow)
                : Math.Min(cur, overflow);
            if (maxTake <= 0) continue;

            step[name] = cur - maxTake;
            free[name] = (free.TryGetValue(name, out var fv) ? fv : 0) + maxTake;
            overflow -= maxTake;

            if (step[name] <= 0) step.Remove(name);
        }

        updatedStep = step;
        updatedFree = free;
        return true;
    }
}
