using System;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты Шага 4 «Преимущества» визарда VtM V20.
/// Покрывает Дисциплины (пул 3), Факты биографии (пул 5), Добродетели (1/1/1 + пул 7).
/// </summary>
public class VampireAdvantagesTests
{
    private static VampireCharacter NewDraft(string clan = "Вентру") => new()
    {
        CharacterId = Guid.NewGuid(),
        PlayerId = 42,
        PlayerName = "TestUser",
        Generation = 13,
        Hunger = 1,
        Clan = clan,
    };

    // ── Каталог: 3 слота дисциплин по клану ─────────────────────────────

    [Fact]
    public void Catalog_GetDisciplineSlots_Ventrue()
    {
        var slots = VampireAdvantagesCatalog.GetDisciplineSlots("Вентру");
        Assert.Equal(3, slots.Count);
        Assert.Contains("Доминирование", slots);
        Assert.Contains("Стойкость", slots);
        Assert.Contains("Величие", slots);
    }

    [Fact]
    public void Catalog_GetDisciplineSlots_Caitiff_ReturnsThreePlaceholders()
    {
        var slots = VampireAdvantagesCatalog.GetDisciplineSlots("Каитиф");
        Assert.Equal(3, slots.Count);
        Assert.True(VampireAdvantagesCatalog.IsCaitiff("Каитиф"));
    }

    [Fact]
    public void Catalog_GetDisciplineSlots_UnknownClan_ReturnsCaitiffFallback()
    {
        var slots = VampireAdvantagesCatalog.GetDisciplineSlots("НеКлан");
        Assert.Equal(3, slots.Count);
        Assert.False(VampireAdvantagesCatalog.IsCaitiff("НеКлан"));
    }

    [Fact]
    public void Catalog_HasDisciplineSlots_TrueForKnownAndUnknown()
    {
        Assert.True(VampireAdvantagesCatalog.HasDisciplineSlots("Вентру"));
        Assert.True(VampireAdvantagesCatalog.HasDisciplineSlots("Каитиф"));
        Assert.True(VampireAdvantagesCatalog.HasDisciplineSlots("Гангрел"));
        Assert.False(VampireAdvantagesCatalog.HasDisciplineSlots(""));
        Assert.False(VampireAdvantagesCatalog.HasDisciplineSlots(null));
    }

    [Fact]
    public void Catalog_Pools_AreCorrect()
    {
        Assert.Equal(3, VampireAdvantagesCatalog.DisciplinePool);
        Assert.Equal(5, VampireAdvantagesCatalog.BackgroundPool);
        Assert.Equal(7, VampireAdvantagesCatalog.VirtuePool);
        Assert.Equal(5, VampireAdvantagesCatalog.PerFieldCap);
    }

    [Fact]
    public void Catalog_VirtueBase_Is111()
    {
        Assert.Equal(1, VampireAdvantagesCatalog.VirtueBaseConscience);
        Assert.Equal(1, VampireAdvantagesCatalog.VirtueBaseSelfControl);
        Assert.Equal(1, VampireAdvantagesCatalog.VirtueBaseCourage);
    }

    // ── Дисциплины: increment/decrement ──────────────────────────────────

    [Fact]
    public void Discipline_StartAtZero()
    {
        var d = NewDraft();
        Assert.Equal(0, VampireAdvantagesResolver.GetDisciplineValue(d, "Доминирование"));
        Assert.Equal(0, VampireAdvantagesResolver.TotalDisciplineSpent(d));
        Assert.Equal(3, VampireAdvantagesResolver.RemainingDisciplinePool(d));
        Assert.False(VampireAdvantagesResolver.IsDisciplinesComplete(d));
    }

    [Fact]
    public void Discipline_Increment_UpToThreeOfPool()
    {
        var d = NewDraft();
        Assert.True(VampireAdvantagesResolver.IncrementDiscipline(d, "Доминирование").IsSuccess);
        Assert.True(VampireAdvantagesResolver.IncrementDiscipline(d, "Величие").IsSuccess);
        Assert.True(VampireAdvantagesResolver.IncrementDiscipline(d, "Величие").IsSuccess);

        Assert.Equal(1, VampireAdvantagesResolver.GetDisciplineValue(d, "Доминирование"));
        Assert.Equal(2, VampireAdvantagesResolver.GetDisciplineValue(d, "Величие"));
        Assert.Equal(3, VampireAdvantagesResolver.TotalDisciplineSpent(d));
        Assert.True(VampireAdvantagesResolver.IsDisciplinesComplete(d));
    }

    [Fact]
    public void Discipline_PoolExhausted_RejectsFourth()
    {
        var d = NewDraft();
        VampireAdvantagesResolver.IncrementDiscipline(d, "Доминирование");
        VampireAdvantagesResolver.IncrementDiscipline(d, "Стойкость");
        VampireAdvantagesResolver.IncrementDiscipline(d, "Величие");

        var reject = VampireAdvantagesResolver.IncrementDiscipline(d, "Доминирование");
        Assert.False(reject.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.PoolExhausted, reject.Failure);
    }

    [Fact]
    public void Discipline_RejectsUnknownForKnownClan()
    {
        var d = NewDraft("Вентру");
        var reject = VampireAdvantagesResolver.IncrementDiscipline(d, "Тауматургия");
        Assert.False(reject.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.UnknownField, reject.Failure);
    }

    [Fact]
    public void Discipline_CapIsFive_NotReachedWithPool()
    {
        // Пул = 3, кэп = 5 — пул кончится раньше кэпа, поэтому отдельная проверка
        // через SetDisciplineValue (для тестовых сценариев).
        var d = NewDraft();
        var set = VampireAdvantagesResolver.SetDisciplineValue(d, "Доминирование", 5);
        Assert.False(set.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.PoolExhausted, set.Failure);
    }

    [Fact]
    public void Discipline_Decrement_WorksAndRemoves()
    {
        var d = NewDraft();
        VampireAdvantagesResolver.IncrementDiscipline(d, "Доминирование");
        VampireAdvantagesResolver.IncrementDiscipline(d, "Доминирование");
        Assert.True(VampireAdvantagesResolver.DecrementDiscipline(d, "Доминирование").IsSuccess);
        Assert.Equal(1, VampireAdvantagesResolver.GetDisciplineValue(d, "Доминирование"));
        VampireAdvantagesResolver.DecrementDiscipline(d, "Доминирование");
        Assert.Equal(0, VampireAdvantagesResolver.GetDisciplineValue(d, "Доминирование"));
        Assert.False(d.Disciplines.ContainsKey("Доминирование"));
    }

    [Fact]
    public void Discipline_Decrement_BelowZeroRejected()
    {
        var d = NewDraft();
        var rej = VampireAdvantagesResolver.DecrementDiscipline(d, "Доминирование");
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.BelowMin, rej.Failure);
    }

    [Fact]
    public void Discipline_SetValue_RoundsTrip()
    {
        var d = NewDraft();
        var ok = VampireAdvantagesResolver.SetDisciplineValue(d, "Стойкость", 2);
        Assert.True(ok.IsSuccess);
        Assert.Equal(2, VampireAdvantagesResolver.GetDisciplineValue(d, "Стойкость"));

        // Пул уже 2/3, можно ещё 1.
        Assert.True(VampireAdvantagesResolver.SetDisciplineValue(d, "Стойкость", 3).IsSuccess);

        // А дальше — пул исчерпан.
        Assert.False(VampireAdvantagesResolver.SetDisciplineValue(d, "Величие", 1).IsSuccess);
    }

    [Fact]
    public void Discipline_Caitiff_RenameWorks()
    {
        var d = NewDraft("Каитиф");
        VampireAdvantagesResolver.IncrementDiscipline(d, "Дисциплина 1");
        Assert.True(VampireAdvantagesResolver.RenameCaitiffDiscipline(d, "Дисциплина 1", "Анимализм").IsSuccess);
        Assert.Equal(1, VampireAdvantagesResolver.GetDisciplineValue(d, "Анимализм"));
        Assert.False(d.Disciplines.ContainsKey("Дисциплина 1"));
    }

    [Fact]
    public void Discipline_Caitiff_RenameEmptySlot_CreatatesWithZero()
    {
        // Каитиф переименовывает слот ДО траты очка — резолвер должен создать ключ с 0.
        var d = NewDraft("Каитиф");
        var r = VampireAdvantagesResolver.RenameCaitiffDiscipline(d, "Дисциплина 1", "Анимализм");
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(0, VampireAdvantagesResolver.GetDisciplineValue(d, "Анимализм"));
        Assert.False(d.Disciplines.ContainsKey("Дисциплина 1"));
        // Пул остался 3 (0 не тратит очки).
        Assert.Equal(3, VampireAdvantagesResolver.RemainingDisciplinePool(d));
    }

    [Fact]
    public void Discipline_Caitiff_RenameEmptySlot_AllowsLaterIncrement()
    {
        var d = NewDraft("Каитиф");
        VampireAdvantagesResolver.RenameCaitiffDiscipline(d, "Дисциплина 1", "Анимализм");
        Assert.True(VampireAdvantagesResolver.IncrementDiscipline(d, "Анимализм").IsSuccess);
        Assert.Equal(1, VampireAdvantagesResolver.GetDisciplineValue(d, "Анимализм"));
        Assert.Equal(2, VampireAdvantagesResolver.RemainingDisciplinePool(d));
    }

    [Fact]
    public void Discipline_Caitiff_RenameFilled_PreservesValue()
    {
        var d = NewDraft("Каитиф");
        VampireAdvantagesResolver.IncrementDiscipline(d, "Дисциплина 2");
        VampireAdvantagesResolver.IncrementDiscipline(d, "Дисциплина 2");
        var r = VampireAdvantagesResolver.RenameCaitiffDiscipline(d, "Дисциплина 2", "Прорицание");
        Assert.True(r.IsSuccess);
        Assert.Equal(2, VampireAdvantagesResolver.GetDisciplineValue(d, "Прорицание"));
        Assert.False(d.Disciplines.ContainsKey("Дисциплина 2"));
        Assert.Equal(1, VampireAdvantagesResolver.RemainingDisciplinePool(d));
    }

    [Fact]
    public void Discipline_Caitiff_RenameToExistingName_Rejected()
    {
        var d = NewDraft("Каитиф");
        VampireAdvantagesResolver.RenameCaitiffDiscipline(d, "Дисциплина 1", "Анимализм");
        VampireAdvantagesResolver.RenameCaitiffDiscipline(d, "Дисциплина 2", "Прорицание");
        var r = VampireAdvantagesResolver.RenameCaitiffDiscipline(d, "Дисциплина 3", "Анимализм");
        Assert.False(r.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.BackgroundAlreadyExists, r.Failure);
    }

    [Fact]
    public void Discipline_Caitiff_RenameUnknownSlot_Rejected()
    {
        // Имя не в списке клановых слотов и не в draft.Disciplines.
        var d = NewDraft("Каитиф");
        var r = VampireAdvantagesResolver.RenameCaitiffDiscipline(d, "ЧужаяДисциплина", "Чтото");
        Assert.False(r.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.UnknownField, r.Failure);
    }

    [Fact]
    public void Discipline_Rename_RejectsForNonCaitiff()
    {
        var d = NewDraft("Вентру");
        VampireAdvantagesResolver.IncrementDiscipline(d, "Доминирование");
        var rej = VampireAdvantagesResolver.RenameCaitiffDiscipline(d, "Доминирование", "X");
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.CannotRename, rej.Failure);
    }

    // ── Факты биографии: add/remove/increment/decrement ──────────────────

    [Fact]
    public void Background_StartEmpty()
    {
        var d = NewDraft();
        Assert.Empty(d.Backgrounds);
        Assert.Equal(0, VampireAdvantagesResolver.TotalBackgroundSpent(d));
        Assert.Equal(5, VampireAdvantagesResolver.RemainingBackgroundPool(d));
        Assert.False(VampireAdvantagesResolver.IsBackgroundsComplete(d));
    }

    [Fact]
    public void Background_Add_BeginsAtRank1()
    {
        var d = NewDraft();
        var ok = VampireAdvantagesResolver.AddBackground(d, "Стая");
        Assert.True(ok.IsSuccess);
        Assert.Equal(1, VampireAdvantagesResolver.GetBackgroundRank(d, "Стая"));
        Assert.Equal(1, VampireAdvantagesResolver.TotalBackgroundSpent(d));
        Assert.Equal(4, VampireAdvantagesResolver.RemainingBackgroundPool(d));
    }

    [Fact]
    public void Background_AddTwiceSameName_Rejected()
    {
        var d = NewDraft();
        VampireAdvantagesResolver.AddBackground(d, "Стая");
        var dup = VampireAdvantagesResolver.AddBackground(d, "Стая");
        Assert.False(dup.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.BackgroundAlreadyExists, dup.Failure);
    }

    [Fact]
    public void Background_Remove_FreesPool()
    {
        var d = NewDraft();
        VampireAdvantagesResolver.AddBackground(d, "Стая");
        VampireAdvantagesResolver.AddBackground(d, "Ресурсы");
        Assert.True(VampireAdvantagesResolver.RemoveBackground(d, "Стая").IsSuccess);
        Assert.Equal(1, VampireAdvantagesResolver.TotalBackgroundSpent(d));
    }

    [Fact]
    public void Background_Remove_UnknownRejected()
    {
        var d = NewDraft();
        var rej = VampireAdvantagesResolver.RemoveBackground(d, "Несуществующее");
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.BackgroundNotFound, rej.Failure);
    }

    [Fact]
    public void Background_Increment_PoolCap5_BumpsToRank5()
    {
        var d = NewDraft();
        VampireAdvantagesResolver.AddBackground(d, "Союзники");
        VampireAdvantagesResolver.IncrementBackground(d, "Союзники"); // 2
        VampireAdvantagesResolver.IncrementBackground(d, "Союзники"); // 3
        VampireAdvantagesResolver.IncrementBackground(d, "Союзники"); // 4
        Assert.True(VampireAdvantagesResolver.IncrementBackground(d, "Союзники").IsSuccess); // 5
        Assert.Equal(5, VampireAdvantagesResolver.GetBackgroundRank(d, "Союзники"));
        Assert.Equal(5, VampireAdvantagesResolver.TotalBackgroundSpent(d));
        Assert.True(VampireAdvantagesResolver.IsBackgroundsComplete(d));
    }

    [Fact]
    public void Background_Increment_AboveCap5Rejected()
    {
        var d = NewDraft();
        VampireAdvantagesResolver.AddBackground(d, "X");
        // форсируем прямой записью — Add начинает с ранга 1.
        d.Backgrounds["X"] = 5;
        var rej = VampireAdvantagesResolver.IncrementBackground(d, "X");
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.AboveCap, rej.Failure);
    }

    [Fact]
    public void Background_Increment_PoolExhausted()
    {
        var d = NewDraft();
        VampireAdvantagesResolver.AddBackground(d, "A"); // 1
        VampireAdvantagesResolver.AddBackground(d, "B"); // 2
        // Ранги по 1.
        d.Backgrounds["A"] = 3; // A=3
        d.Backgrounds["B"] = 2; // B=2 → итого 5
        var rej = VampireAdvantagesResolver.IncrementBackground(d, "A");
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.PoolExhausted, rej.Failure);
    }

    [Fact]
    public void Background_Decrement_BelowRank1Rejected()
    {
        var d = NewDraft();
        VampireAdvantagesResolver.AddBackground(d, "X");
        var rej = VampireAdvantagesResolver.DecrementBackground(d, "X");
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.BelowMin, rej.Failure);
    }

    [Fact]
    public void Background_Rename_UpdatesDictKey()
    {
        var d = NewDraft();
        VampireAdvantagesResolver.AddBackground(d, "Стая");
        Assert.True(VampireAdvantagesResolver.RenameBackground(d, "Стая", "Ресурсы").IsSuccess);
        Assert.Equal(1, VampireAdvantagesResolver.GetBackgroundRank(d, "Ресурсы"));
        Assert.False(d.Backgrounds.ContainsKey("Стая"));
    }

    [Fact]
    public void Background_Rename_ToExistingRejected()
    {
        var d = NewDraft();
        VampireAdvantagesResolver.AddBackground(d, "A");
        VampireAdvantagesResolver.AddBackground(d, "B");
        var rej = VampireAdvantagesResolver.RenameBackground(d, "A", "B");
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.BackgroundAlreadyExists, rej.Failure);
    }

    // ── Добродетели: base 1/1/1, pool 7, cap 5 ──────────────────────────

    [Fact]
    public void Virtue_GetSeedsBase111()
    {
        var d = NewDraft();
        // Без явного вызова SeedVirtues — GetVirtueValue всё равно вернёт 1.
        Assert.Equal(1, VampireAdvantagesResolver.GetVirtueValue(d, VampireParameterCatalog.VirtueConscience));
        Assert.Equal(1, VampireAdvantagesResolver.GetVirtueValue(d, VampireParameterCatalog.VirtueSelfControl));
        Assert.Equal(1, VampireAdvantagesResolver.GetVirtueValue(d, VampireParameterCatalog.VirtueCourage));
    }

    [Fact]
    public void Virtue_SeedVirtues_Populates111()
    {
        var d = NewDraft();
        d.Virtues.Clear();
        VampireAdvantagesResolver.SeedVirtues(d);
        Assert.Equal(1, d.Virtues[VampireParameterCatalog.VirtueConscience]);
        Assert.Equal(1, d.Virtues[VampireParameterCatalog.VirtueSelfControl]);
        Assert.Equal(1, d.Virtues[VampireParameterCatalog.VirtueCourage]);
    }

    [Fact]
    public void Virtue_StartSpentZero()
    {
        var d = NewDraft();
        Assert.Equal(0, VampireAdvantagesResolver.TotalVirtueSpent(d));
        Assert.Equal(7, VampireAdvantagesResolver.RemainingVirtuePool(d));
    }

    [Fact]
    public void Virtue_Increment_AddsOnTopOfBase()
    {
        var d = NewDraft();
        Assert.True(VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage).IsSuccess);
        Assert.True(VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage).IsSuccess);
        Assert.Equal(3, VampireAdvantagesResolver.GetVirtueValue(d, VampireParameterCatalog.VirtueCourage));
        Assert.Equal(2, VampireAdvantagesResolver.TotalVirtueSpent(d));
    }

    [Fact]
    public void Virtue_Cap5_BumpsBaseToFive()
    {
        var d = NewDraft();
        // старт 1, можно +4 → 5.
        Assert.True(VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage).IsSuccess); // 2
        Assert.True(VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage).IsSuccess); // 3
        Assert.True(VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage).IsSuccess); // 4
        var fifth = VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage);
        Assert.True(fifth.IsSuccess);
        Assert.Equal(5, VampireAdvantagesResolver.GetVirtueValue(d, VampireParameterCatalog.VirtueCourage));

        var rej = VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage);
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.AboveCap, rej.Failure);
    }

    [Fact]
    public void Virtue_PoolOf7_AllocatedAcrossAll()
    {
        var d = NewDraft();
        // Совесть+1, Самок+2, Смелость+4 → итого 7 сверху, все на кэпе или близко.
        VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueConscience);   // 2 (+1)
        VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueSelfControl);  // 2 (+1)
        VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueSelfControl);  // 3 (+2 всего)
        VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage);      // 2 (+1)
        VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage);      // 3 (+2)
        VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage);      // 4 (+3)
        VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage);      // 5 (+4)
        Assert.Equal(7, VampireAdvantagesResolver.TotalVirtueSpent(d));
        Assert.Equal(0, VampireAdvantagesResolver.RemainingVirtuePool(d));
        Assert.True(VampireAdvantagesResolver.IsVirtuesComplete(d));

        // попытка добавить ещё — отказ.
        var rej = VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueConscience);
        Assert.Equal(VampireAdvantagesResolver.Failure.PoolExhausted, rej.Failure);
    }

    [Fact]
    public void Virtue_PoolExhaustedWhenPoolUsedUp()
    {
        var d = NewDraft();
        // Совесть → 5 (4 сверху, потрачено 4).
        for (int i = 0; i < 4; i++)
            VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueConscience);
        // Самоконтроль → 3 (2 сверху, итого потрачено 6).
        for (int i = 0; i < 2; i++)
            VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueSelfControl);
        // Смелость → 2 (1 сверху, итого потрачено 7) — пул исчерпан.
        VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage);
        Assert.Equal(7, VampireAdvantagesResolver.TotalVirtueSpent(d));
        Assert.True(VampireAdvantagesResolver.IsVirtuesComplete(d));

        // Любая следующая попытка — PoolExhausted.
        var rej = VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage);
        Assert.Equal(VampireAdvantagesResolver.Failure.PoolExhausted, rej.Failure);
    }

    [Fact]
    public void Virtue_Decrement_NotBelow1()
    {
        var d = NewDraft();
        var rej = VampireAdvantagesResolver.DecrementVirtue(d, VampireParameterCatalog.VirtueConscience);
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.BelowMin, rej.Failure);
    }

    [Fact]
    public void Virtue_Decrement_WorksAboveBase()
    {
        var d = NewDraft();
        VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage); // 2
        VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage); // 3
        Assert.True(VampireAdvantagesResolver.DecrementVirtue(d, VampireParameterCatalog.VirtueCourage).IsSuccess);
        Assert.Equal(2, VampireAdvantagesResolver.GetVirtueValue(d, VampireParameterCatalog.VirtueCourage));
    }

    [Fact]
    public void Virtue_Increment_UnknownRejected()
    {
        var d = NewDraft();
        var rej = VampireAdvantagesResolver.IncrementVirtue(d, "Решимость"); // Шабашитская замена, у нас не применяется
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireAdvantagesResolver.Failure.UnknownField, rej.Failure);
    }

    // ── Сброс ────────────────────────────────────────────────────────────

    [Fact]
    public void ResetProgress_PreservesClanAndClearsStep4()
    {
        var d = NewDraft("Вентру");
        VampireAdvantagesResolver.IncrementDiscipline(d, "Доминирование");
        VampireAdvantagesResolver.AddBackground(d, "Стая");
        VampireAdvantagesResolver.IncrementVirtue(d, VampireParameterCatalog.VirtueCourage);

        VampireAdvantagesResolver.ResetProgress(d);

        Assert.Equal("Вентру", d.Clan);
        Assert.Equal(13, d.Generation);
        Assert.Empty(d.Disciplines);
        Assert.Empty(d.Backgrounds);
        Assert.Equal(1, d.Virtues[VampireParameterCatalog.VirtueConscience]);
    }

    [Fact]
    public void ResetAll_DoesSame()
    {
        var d = NewDraft();
        VampireAdvantagesResolver.IncrementDiscipline(d, "Скорость");
        VampireAdvantagesResolver.ResetAll(d);
        Assert.Empty(d.Disciplines);
    }

    // ── Статус-сообщения ────────────────────────────────────────────────

    [Fact]
    public void Status_Disciplines_ShowsPool()
    {
        var d = NewDraft("Вентру");
        var msg = VampireAdvantagesResolver.BuildDisciplinesStatusMessage(d);
        Assert.Contains("Шаг 4.1", msg);
        Assert.Contains("Вентру", msg);
        Assert.Contains("Доминирование", msg);
        Assert.Contains("**0** / **3**", msg);
    }

    [Fact]
    public void Status_Backgrounds_ReflectsListAndPool()
    {
        var d = NewDraft();
        VampireAdvantagesResolver.AddBackground(d, "Стая");
        var msg = VampireAdvantagesResolver.BuildBackgroundsStatusMessage(d);
        Assert.Contains("Стая", msg);
        Assert.Contains("**1** / **5**", msg);
    }

    [Fact]
    public void Status_Virtues_ShowsHumanityAndWillpower()
    {
        var d = NewDraft();
        var msg = VampireAdvantagesResolver.BuildVirtuesStatusMessage(d);
        Assert.Contains("Человечность", msg);
        Assert.Contains("Воля", msg);
        Assert.Contains("**0** / **7**", msg);
        Assert.Contains("Совесть", msg);
        Assert.Contains("Самоконтроль", msg);
        Assert.Contains("Смелость", msg);
    }

    [Fact]
    public void Status_Caitiff_ShowsRenamingHint()
    {
        var d = NewDraft("Каитиф");
        var msg = VampireAdvantagesResolver.BuildDisciplinesStatusMessage(d);
        Assert.Contains("Каитиф", msg);
        Assert.Contains("свободный ввод", msg);
    }

    [Fact]
    public void Status_EmptyClan_HintsAtStep1()
    {
        var d = NewDraft("");
        d.Clan = "";
        var msg = VampireAdvantagesResolver.BuildDisciplinesStatusMessage(d);
        Assert.Contains("Шаге 1", msg);
    }
}
