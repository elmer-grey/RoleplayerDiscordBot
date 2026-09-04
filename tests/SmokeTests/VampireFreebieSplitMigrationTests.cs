using System.Collections.Generic;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты для <see cref="VampireFreebieSplitMigration"/>.
/// Проверяют идемпотентность, корректность переноса излишка из Step в Freebie
/// и no-op для уже-разделённых или пустых персонажей.
///
/// <para>Алгоритм миграции: если <c>Step.Sum() &gt; Pool</c>, то излишек
/// переносится в Freebie-словарь, начиная с самых больших записей
/// (OrderByDescending). Это значит, что для тестовых данных
/// <c>{A:2, B:1, C:1}</c> при пуле 3 и overflow 1 запись "A" уменьшится
/// до 1, а в Freebie появится "A:1".</para>
/// </summary>
public class VampireFreebieSplitMigrationTests
{
    [Fact]
    public void Empty_NoOp()
    {
        var chars = new Dictionary<string, VampireCharacter>();
        int changed = VampireFreebieSplitMigration.Migrate(chars);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void OnlyStep_NoOverflow_NoChange()
    {
        var c = new VampireCharacter { PlayerName = "p1" };
        c.Disciplines["Стремительность"] = 2;
        c.Disciplines["Мощь"] = 1;
        // Sum = 3 == DisciplinePool. Нечего переносить.

        bool changed = VampireFreebieSplitMigration.MigrateCharacter(c);

        Assert.False(changed);
        Assert.Equal(2, c.Disciplines["Стремительность"]);
        Assert.Equal(1, c.Disciplines["Мощь"]);
        Assert.Empty(c.FreebieDisciplines);
    }

    [Fact]
    public void StepPlusFreebieOverflow_Discipline_SplitsToFreebie()
    {
        var c = new VampireCharacter { PlayerName = "p1" };
        // Legacy: freebie был записан в тот же dict. Sum = 4 > DisciplinePool=3.
        c.Disciplines["Стремительность"] = 2;
        c.Disciplines["Мощь"] = 1;
        c.Disciplines["Величие"] = 1; // ← это был freebie

        bool changed = VampireFreebieSplitMigration.MigrateCharacter(c);

        Assert.True(changed);
        // Излишек=1 снят с самой большой записи (Стремительность 2 → 1).
        Assert.Equal(1, c.Disciplines["Стремительность"]);
        Assert.Equal(1, c.Disciplines["Мощь"]);
        Assert.Equal(1, c.Disciplines["Величие"]); // не тронут: меньше overflow
        // Freebie получил 1 пункт в Стремительность.
        Assert.Equal(1, c.FreebieDisciplines["Стремительность"]);
        // Лист (через GetDisciplineValue) даёт ту же итоговую сумму 4.
        Assert.Equal(2, VampireAdvantagesResolver.GetDisciplineValue(c, "Стремительность"));
        Assert.Equal(1, VampireAdvantagesResolver.GetDisciplineValue(c, "Мощь"));
        Assert.Equal(1, VampireAdvantagesResolver.GetDisciplineValue(c, "Величие"));
        // Гейт Шага 4.1 снова проходит.
        Assert.True(VampireAdvantagesResolver.IsDisciplinesComplete(c));
    }

    [Fact]
    public void StepPlusFreebieOverflow_Background_SplitsToFreebie()
    {
        var c = new VampireCharacter { PlayerName = "p2" };
        c.Backgrounds["Влияние"] = 3;
        c.Backgrounds["Контакты"] = 2;
        c.Backgrounds["Состояние"] = 2; // ← это был freebie (sum=7 > pool=5)

        bool changed = VampireFreebieSplitMigration.MigrateCharacter(c);

        Assert.True(changed);
        // Излишек=2 снят с самой большой (Влияние 3 → 1).
        Assert.Equal(1, c.Backgrounds["Влияние"]);
        Assert.Equal(2, c.Backgrounds["Контакты"]);
        Assert.Equal(2, c.Backgrounds["Состояние"]); // не тронут
        Assert.Equal(2, c.FreebieBackgrounds["Влияние"]);
        // Лист (сумма Step+Free) даёт ту же итоговую сумму 7.
        Assert.Equal(3, VampireAdvantagesResolver.GetBackgroundRank(c, "Влияние"));
        Assert.Equal(2, VampireAdvantagesResolver.GetBackgroundRank(c, "Контакты"));
        Assert.Equal(2, VampireAdvantagesResolver.GetBackgroundRank(c, "Состояние"));
        Assert.True(VampireAdvantagesResolver.IsBackgroundsComplete(c));
    }

    [Fact]
    public void Idempotency_TwiceIsNoOp()
    {
        var c = new VampireCharacter { PlayerName = "p3" };
        c.Disciplines["Стремительность"] = 2;
        c.Disciplines["Мощь"] = 1;
        c.Disciplines["Величие"] = 1;

        Assert.True(VampireFreebieSplitMigration.MigrateCharacter(c));
        // После первого прохода Step.Sum = 3 == pool, мигрировать нечего.
        Assert.False(VampireFreebieSplitMigration.MigrateCharacter(c));
        Assert.Equal(1, c.Disciplines["Стремительность"]);
        Assert.Equal(1, c.Disciplines["Мощь"]);
        Assert.Equal(1, c.Disciplines["Величие"]);
        Assert.Equal(1, c.FreebieDisciplines["Стремительность"]);
    }

    [Fact]
    public void OverflowBiggerThanCap_ClampedToCap()
    {
        var c = new VampireCharacter { PlayerName = "p4" };
        c.Disciplines["Стремительность"] = 8;
        bool changed = VampireFreebieSplitMigration.MigrateCharacter(c);
        Assert.True(changed);
        Assert.Equal(3, c.Disciplines["Стремительность"]);
        Assert.Equal(5, c.FreebieDisciplines["Стремительность"]);
    }

    [Fact]
    public void OverflowClampedWhenExceedsCap_NoOtherFieldsTouched()
    {
        var c = new VampireCharacter { PlayerName = "p5" };
        // sum=10, pool=3, overflow=7. Алгоритм снимает максимум с самой
        // большой (Стремительность 8 → 1, Free=7), меньшие не трогаются.
        // Step.Sum == pool == 3 (главное условие для гейта).
        c.Disciplines["Стремительность"] = 8;
        c.Disciplines["Мощь"] = 2;

        bool changed = VampireFreebieSplitMigration.MigrateCharacter(c);
        Assert.True(changed);
        Assert.Equal(3, c.Disciplines.Values.Sum());
        Assert.Equal(2, c.Disciplines["Мощь"]);
        // Гейт Шага 4.1 снова проходит.
        Assert.True(VampireAdvantagesResolver.IsDisciplinesComplete(c));
    }

    [Fact]
    public void Split_LargestStaysInStep()
    {
        var c = new VampireCharacter { PlayerName = "p6" };
        c.Disciplines["Стремительность"] = 2;
        c.Disciplines["Мощь"] = 1;
        c.Disciplines["Величие"] = 1; // overflow=1

        VampireFreebieSplitMigration.MigrateCharacter(c);

        // Излишек 1 забрали с самой большой записи — Стремительность (2 → 1).
        // Меньшие записи (Мощь, Величие) не трогаем.
        Assert.Equal(1, c.Disciplines["Стремительность"]);
        Assert.Equal(1, c.Disciplines["Мощь"]);
        Assert.Equal(1, c.Disciplines["Величие"]);
        Assert.Equal(1, c.FreebieDisciplines["Стремительность"]);
    }

    [Fact]
    public void Migrate_Dictionary_AllCharacters()
    {
        var chars = new Dictionary<string, VampireCharacter>();
        var a = new VampireCharacter { PlayerName = "a" };
        a.Disciplines["Стремительность"] = 2;
        a.Disciplines["Мощь"] = 1;
        a.Disciplines["Величие"] = 1; // overflow
        chars["a"] = a;

        var b = new VampireCharacter { PlayerName = "b" };
        b.Disciplines["Пусто"] = 0; // не должно учитываться
        chars["b"] = b;

        int changed = VampireFreebieSplitMigration.Migrate(chars);
        Assert.Equal(1, changed);
    }
}
