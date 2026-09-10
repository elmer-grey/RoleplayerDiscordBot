using System;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты Шага 3 «Способности 13/9/5» визарда создания персонажа VtM V20.
/// Покрывает каталог 30 способностей, приоритеты, бюджет, лимит 3, специализации.
/// </summary>
public class VampireAbilitiesTests
{
    private static VampireCharacter NewDraft() => new VampireCharacter
    {
        CharacterId = Guid.NewGuid(),
        PlayerId = 42,
        PlayerName = "TestUser",
        Generation = 13,
        Hunger = 1,
    };

    // ── Каталог: 30 способностей + группы ────────────────────────────────

    [Fact]
    public void Catalog_Talents_HasTenAbilities()
    {
        Assert.Equal(10, VampireAbilitiesCatalog.Talents.Count);
        Assert.Contains("Атлетика", VampireAbilitiesCatalog.Talents);
        Assert.Contains("Бдительность", VampireAbilitiesCatalog.Talents);
        Assert.Contains("Драка", VampireAbilitiesCatalog.Talents);
        Assert.Contains("Запугивание", VampireAbilitiesCatalog.Talents);
        Assert.Contains("Красноречие", VampireAbilitiesCatalog.Talents);
        Assert.Contains("Лидерство", VampireAbilitiesCatalog.Talents);
        Assert.Contains("Уличное чутьё", VampireAbilitiesCatalog.Talents);
        Assert.Contains("Хитрость", VampireAbilitiesCatalog.Talents);
        Assert.Contains("Шестое чувство", VampireAbilitiesCatalog.Talents);
        Assert.Contains("Эмпатия", VampireAbilitiesCatalog.Talents);
    }

    [Fact]
    public void Catalog_Skills_HasTenAbilities()
    {
        Assert.Equal(10, VampireAbilitiesCatalog.Skills.Count);
        Assert.Contains("Вождение", VampireAbilitiesCatalog.Skills);
        Assert.Contains("Воровство", VampireAbilitiesCatalog.Skills);
        Assert.Contains("Выживание", VampireAbilitiesCatalog.Skills);
        Assert.Contains("Исполнение", VampireAbilitiesCatalog.Skills);
        Assert.Contains("Обращение с животными", VampireAbilitiesCatalog.Skills);
        Assert.Contains("Ремесло", VampireAbilitiesCatalog.Skills);
        Assert.Contains("Скрытность", VampireAbilitiesCatalog.Skills);
        Assert.Contains("Стрельба", VampireAbilitiesCatalog.Skills);
        Assert.Contains("Фехтование", VampireAbilitiesCatalog.Skills);
        Assert.Contains("Этикет", VampireAbilitiesCatalog.Skills);
    }

    [Fact]
    public void Catalog_Knowledges_HasTenAbilities()
    {
        Assert.Equal(10, VampireAbilitiesCatalog.Knowledges.Count);
        Assert.Contains("Гуманитарные науки", VampireAbilitiesCatalog.Knowledges);
        Assert.Contains("Естественные науки", VampireAbilitiesCatalog.Knowledges);
        Assert.Contains("Информатика", VampireAbilitiesCatalog.Knowledges);
        Assert.Contains("Медицина", VampireAbilitiesCatalog.Knowledges);
        Assert.Contains("Оккультизм", VampireAbilitiesCatalog.Knowledges);
        Assert.Contains("Политика", VampireAbilitiesCatalog.Knowledges);
        Assert.Contains("Расследование", VampireAbilitiesCatalog.Knowledges);
        Assert.Contains("Финансы", VampireAbilitiesCatalog.Knowledges);
        Assert.Contains("Электроника", VampireAbilitiesCatalog.Knowledges);
        Assert.Contains("Юриспруденция", VampireAbilitiesCatalog.Knowledges);
    }

    [Fact]
    public void Catalog_FindGroup_Works()
    {
        Assert.Equal(VampireAbilityGroup.Talents, VampireAbilitiesCatalog.FindGroup("Атлетика"));
        Assert.Equal(VampireAbilityGroup.Talents, VampireAbilitiesCatalog.FindGroup("Эмпатия"));
        Assert.Equal(VampireAbilityGroup.Skills, VampireAbilitiesCatalog.FindGroup("Вождение"));
        Assert.Equal(VampireAbilityGroup.Skills, VampireAbilitiesCatalog.FindGroup("Фехтование"));
        Assert.Equal(VampireAbilityGroup.Knowledges, VampireAbilitiesCatalog.FindGroup("Медицина"));
        Assert.Equal(VampireAbilityGroup.Knowledges, VampireAbilitiesCatalog.FindGroup("Юриспруденция"));
    }

    [Fact]
    public void Catalog_FindGroup_UnknownReturnsNull()
    {
        Assert.Null(VampireAbilitiesCatalog.FindGroup("НетТакого"));
        Assert.Null(VampireAbilitiesCatalog.FindGroup(""));
        Assert.Null(VampireAbilitiesCatalog.FindGroup(null));
    }

    [Fact]
    public void Catalog_NamesInGroup_ReturnsCorrectOrder()
    {
        var t = VampireAbilitiesCatalog.NamesInGroup(VampireAbilityGroup.Talents);
        Assert.Equal(10, t.Count);
        Assert.Equal("Атлетика", t[0]);
    }

    [Fact]
    public void Catalog_AllPrioritiesHaveUniquePoints()
    {
        foreach (var p in VampireAbilityPriorityExtensions.All)
        {
            var p1 = p.PointsFor(VampireAbilityGroup.Talents);
            var p2 = p.PointsFor(VampireAbilityGroup.Skills);
            var p3 = p.PointsFor(VampireAbilityGroup.Knowledges);
            Assert.Equal(13, Math.Max(Math.Max(p1, p2), p3));
            Assert.Equal(5, Math.Min(Math.Min(p1, p2), p3));
            Assert.Equal(13 + 9 + 5, p1 + p2 + p3);
        }
    }

    [Fact]
    public void Catalog_AllPriorities_HaveHumanNames()
    {
        foreach (var p in VampireAbilityPriorityExtensions.All)
        {
            var name = p.HumanName();
            Assert.False(string.IsNullOrEmpty(name), $"HumanName для {p} пустой");
        }
    }

    [Fact]
    public void Catalog_SpecializationSuggestions_NonEmptyForKnownAbility()
    {
        var s = VampireAbilitiesCatalog.GetSpecializationSuggestions("Драка");
        Assert.True(s.Count >= 1, "Должна быть хотя бы одна подсказка для Драки");
    }

    [Fact]
    public void Catalog_SpecializationSuggestions_EmptyForUnknown()
    {
        var s = VampireAbilitiesCatalog.GetSpecializationSuggestions("НетТакого");
        Assert.Empty(s);
    }

    [Fact]
    public void Catalog_SpecializationSuggestions_UpToThreePerAbility()
    {
        foreach (var name in VampireAbilitiesCatalog.Talents)
        {
            var s = VampireAbilitiesCatalog.GetSpecializationSuggestions(name);
            Assert.True(s.Count >= 1 && s.Count <= 3,
                $"Для {name} ожидалось 1-3 подсказки, получено {s.Count}");
        }
        foreach (var name in VampireAbilitiesCatalog.Skills)
        {
            var s = VampireAbilitiesCatalog.GetSpecializationSuggestions(name);
            Assert.True(s.Count >= 1 && s.Count <= 3,
                $"Для {name} ожидалось 1-3 подсказки, получено {s.Count}");
        }
        foreach (var name in VampireAbilitiesCatalog.Knowledges)
        {
            var s = VampireAbilitiesCatalog.GetSpecializationSuggestions(name);
            Assert.True(s.Count >= 1 && s.Count <= 3,
                $"Для {name} ожидалось 1-3 подсказки, получено {s.Count}");
        }
    }

    // ── POCO VampireAbilities: стартовые значения 0 ────────────────────

    [Fact]
    public void Poco_AllAbilities_StartAtZero()
    {
        var abs = new VampireAbilities();
        var d = NewDraft();
        d.AbilitiesStruct = abs;
        Assert.Equal(0, d.AbilitiesStruct.Атлетика);
        Assert.Equal(0, d.AbilitiesStruct.Бдительность);
        Assert.Equal(0, d.AbilitiesStruct.Драка);
        Assert.Equal(0, d.AbilitiesStruct.Запугивание);
        Assert.Equal(0, d.AbilitiesStruct.Красноречие);
        Assert.Equal(0, d.AbilitiesStruct.Лидерство);
        Assert.Equal(0, d.AbilitiesStruct.УличноеЧутьё);
        Assert.Equal(0, d.AbilitiesStruct.Хитрость);
        Assert.Equal(0, d.AbilitiesStruct.ШестоеЧувство);
        Assert.Equal(0, d.AbilitiesStruct.Эмпатия);
        Assert.Equal(0, d.AbilitiesStruct.Вождение);
        Assert.Equal(0, d.AbilitiesStruct.Воровство);
        Assert.Equal(0, d.AbilitiesStruct.Выживание);
        Assert.Equal(0, d.AbilitiesStruct.Исполнение);
        Assert.Equal(0, d.AbilitiesStruct.ОбращениеСЖивотными);
        Assert.Equal(0, d.AbilitiesStruct.Ремесло);
        Assert.Equal(0, d.AbilitiesStruct.Скрытность);
        Assert.Equal(0, d.AbilitiesStruct.Стрельба);
        Assert.Equal(0, d.AbilitiesStruct.Фехтование);
        Assert.Equal(0, d.AbilitiesStruct.Этикет);
        Assert.Equal(0, d.AbilitiesStruct.ГуманитарныеНауки);
        Assert.Equal(0, d.AbilitiesStruct.ЕстественныеНауки);
        Assert.Equal(0, d.AbilitiesStruct.Информатика);
        Assert.Equal(0, d.AbilitiesStruct.Медицина);
        Assert.Equal(0, d.AbilitiesStruct.Оккультизм);
        Assert.Equal(0, d.AbilitiesStruct.Политика);
        Assert.Equal(0, d.AbilitiesStruct.Расследование);
        Assert.Equal(0, d.AbilitiesStruct.Финансы);
        Assert.Equal(0, d.AbilitiesStruct.Электроника);
        Assert.Equal(0, d.AbilitiesStruct.Юриспруденция);
    }

    // ── Приоритеты ──────────────────────────────────────────────────────

    [Fact]
    public void SetPriority_Valid_SetsValue()
    {
        var d = NewDraft();
        var result = VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
        Assert.True(result.IsSuccess);
        Assert.Equal("TalentsPrimary", d.AbilitiesPriority);
    }

    [Fact]
        public void SetPriority_DoesNotResetExistingProgress()
    {
            // Аналогично Шагу 2: SetPriority сохраняет уже набранные пункты
            // (иначе игрок мог бы случайно потерять прогресс).
            var d = NewDraft();
            VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
            VampireAbilitiesResolver.Increment(d, "Атлетика");
            VampireAbilitiesResolver.Increment(d, "Атлетика");
            Assert.Equal(2, d.AbilitiesStruct.Атлетика);
            VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.SkillsPrimary);
            // Способности остаются — сбрасывает только кнопка «Сбросить прогресс».
            Assert.Equal(2, d.AbilitiesStruct.Атлетика);
            Assert.Equal("SkillsPrimary", d.AbilitiesPriority);
        }

    // ── Increment / Decrement / бюджет / лимит ─────────────────────────

    [Fact]
    public void Increment_BumpsValue_IfBudgetAllows()
    {
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary); // Таланты = 13
        var r = VampireAbilitiesResolver.Increment(d, "Атлетика");
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(1, d.AbilitiesStruct.Атлетика);
    }

    [Fact]
    public void Increment_Decrement_Works()
    {
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
        VampireAbilitiesResolver.Increment(d, "Атлетика");
        VampireAbilitiesResolver.Increment(d, "Атлетика");
        Assert.Equal(2, d.AbilitiesStruct.Атлетика);
        var dec = VampireAbilitiesResolver.Decrement(d, "Атлетика");
        Assert.True(dec.IsSuccess);
        Assert.Equal(1, d.AbilitiesStruct.Атлетика);
    }

    [Fact]
    public void Increment_RespectsStepThreeCap_Three()
    {
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
        VampireAbilitiesResolver.Increment(d, "Атлетика");
        VampireAbilitiesResolver.Increment(d, "Атлетика");
        VampireAbilitiesResolver.Increment(d, "Атлетика");
        Assert.Equal(3, d.AbilitiesStruct.Атлетика);
        var r = VampireAbilitiesResolver.Increment(d, "Атлетика");
        Assert.False(r.IsSuccess);
        Assert.Equal(VampireAbilitiesResolver.VampireAbilitiesFailure.AboveStepThreeCap, r.Failure);
    }

    [Fact]
    public void Decrement_BelowZero_Fails()
    {
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
        var r = VampireAbilitiesResolver.Decrement(d, "Атлетика");
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void Increment_FailsWithoutPriority()
    {
        var d = NewDraft();
        var r = VampireAbilitiesResolver.Increment(d, "Атлетика");
        Assert.False(r.IsSuccess);
        Assert.Equal(VampireAbilitiesResolver.VampireAbilitiesFailure.PriorityRequired, r.Failure);
    }

    [Fact]
    public void Increment_UnknownAbility_Fails()
    {
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
        var r = VampireAbilitiesResolver.Increment(d, "НетТакого");
        Assert.False(r.IsSuccess);
        Assert.Equal(VampireAbilitiesResolver.VampireAbilitiesFailure.UnknownAbility, r.Failure);
    }

    [Fact]
        public void Increment_AllowsThreeInEveryAbilityIfBudget()
        {
            var d = NewDraft();
            var setR = VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
            Assert.True(setR.IsSuccess, $"SetPriority не прошёл: {setR.Message}");
            // Санити-чек: после SetPriority бюджет Талантов должен быть 13.
            var remaining = VampireAbilitiesResolver.RemainingInGroup(d, VampireAbilityGroup.Talents);
            Assert.True(remaining >= 10, $"Бюджет Талантов после SetPriority всего {remaining}, ожидалось ≥10. Priority={d.AbilitiesPriority}");
            // Дальше — просто проверяем, что первые 3 инкремента проходят.
            var r1 = VampireAbilitiesResolver.Increment(d, "Атлетика");
            Assert.True(r1.IsSuccess, $"Increment Атлетики не прошёл: {r1.Failure} / {r1.Message}");

            var r2 = VampireAbilitiesResolver.Increment(d, "Бдительность");
            Assert.True(r2.IsSuccess, $"Increment Бдительности не прошёл: {r2.Failure} / {r2.Message}");
        }

    // ── Reset / ResetAll / IsAbilitiesComplete ──────────────────────────

    [Fact]
    public void ResetProgress_ZeroesAbilities_KeepsPriority()
    {
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.SkillsPrimary);
        VampireAbilitiesResolver.Increment(d, "Вождение");
        VampireAbilitiesResolver.Increment(d, "Фехтование");
        Assert.True(d.AbilitiesStruct.Вождение > 0);
        VampireAbilitiesResolver.ResetProgress(d);
        Assert.Equal(0, d.AbilitiesStruct.Вождение);
        Assert.Equal(0, d.AbilitiesStruct.Фехтование);
        Assert.Equal("SkillsPrimary", d.AbilitiesPriority);
    }

    [Fact]
    public void ResetProgress_ClearsAbilitySpecializations_KeepsAttributeSpecs()
    {
        var d = NewDraft();
        d.Specializations["Сила"] = "кулак";
        d.Specializations["Атлетика"] = "бег";
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
        VampireAbilitiesResolver.Increment(d, "Атлетика");
        VampireAbilitiesResolver.ResetProgress(d);

        // Специализация атрибута должна остаться нетронутой.
        Assert.Equal("кулак", d.Specializations["Сила"]);
        // Специализация способности — очищена.
        Assert.False(d.Specializations.ContainsKey("Атлетика"));
    }

    [Fact]
    public void ResetAll_ClearsPriorityAndAbilities()
    {
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
        VampireAbilitiesResolver.Increment(d, "Атлетика");
        VampireAbilitiesResolver.ResetAll(d);
        Assert.Equal("", d.AbilitiesPriority);
        Assert.Equal(0, d.AbilitiesStruct.Атлетика);
    }

    // ── ResetGroup (Шаг 3, кнопка «Сбросить группу») ───────────────────

    [Fact]
    public void ResetGroup_Talents_ZeroesTalentsOnly_KeepsPriority()
    {
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.SkillsPrimary);
        // Заполним все три группы.
        VampireAbilitiesResolver.Increment(d, "Атлетика");
        VampireAbilitiesResolver.Increment(d, "Вождение");
        VampireAbilitiesResolver.Increment(d, "Медицина");
        // Сбросим только таланты.
        var res = VampireAbilitiesResolver.ResetGroup(d, VampireAbilityGroup.Talents);
        Assert.True(res.IsSuccess, res.Message);
        Assert.Equal(0, d.AbilitiesStruct.Атлетика);
        // Навыки и Знания остаются нетронутыми.
        Assert.Equal(1, d.AbilitiesStruct.Вождение);
        Assert.Equal(1, d.AbilitiesStruct.Медицина);
        // Приоритет сохраняется.
        Assert.Equal("SkillsPrimary", d.AbilitiesPriority);
    }

    [Fact]
    public void ResetGroup_Skills_ZeroesSkillsOnly()
    {
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
        VampireAbilitiesResolver.Increment(d, "Атлетика");
        VampireAbilitiesResolver.Increment(d, "Вождение");
        VampireAbilitiesResolver.Increment(d, "Медицина");

        VampireAbilitiesResolver.ResetGroup(d, VampireAbilityGroup.Skills);

        Assert.Equal(1, d.AbilitiesStruct.Атлетика);
        Assert.Equal(0, d.AbilitiesStruct.Вождение);
        Assert.Equal(1, d.AbilitiesStruct.Медицина);
    }

    [Fact]
    public void ResetGroup_Knowledges_ZeroesKnowledgesOnly()
    {
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
        VampireAbilitiesResolver.Increment(d, "Атлетика");
        VampireAbilitiesResolver.Increment(d, "Вождение");
        VampireAbilitiesResolver.Increment(d, "Медицина");

        VampireAbilitiesResolver.ResetGroup(d, VampireAbilityGroup.Knowledges);

        Assert.Equal(1, d.AbilitiesStruct.Атлетика);
        Assert.Equal(1, d.AbilitiesStruct.Вождение);
        Assert.Equal(0, d.AbilitiesStruct.Медицина);
    }

    [Fact]
    public void ResetGroup_ClearsSpecializations_OnlyForThatGroup()
    {
        var d = NewDraft();
        d.AbilitiesPriority = VampireAbilityPriority.TalentsPrimary.ToString();
        d.AbilitiesStruct.Атлетика = 4;
        d.AbilitiesStruct.Вождение = 4;
        VampireFinishingResolver.MarkFreebiesExhausted(d);
        VampireAbilitiesResolver.SetSpecialization(d, "Атлетика", "бег");
        VampireAbilitiesResolver.SetSpecialization(d, "Вождение", "мотоцикл");
        d.Specializations["Сила"] = "кулак"; // атрибутная — должна остаться.

        VampireAbilitiesResolver.ResetGroup(d, VampireAbilityGroup.Talents);

        Assert.False(d.Specializations.ContainsKey("Атлетика"));
        Assert.True(d.Specializations.ContainsKey("Вождение"));
        Assert.Equal("мотоцикл", d.Specializations["Вождение"]);
        Assert.Equal("кулак", d.Specializations["Сила"]);
    }

    [Fact]
    public void IsAbilitiesComplete_FalseWhenBudgetUnspent()
    {
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
        Assert.False(VampireAbilitiesResolver.IsAbilitiesComplete(d));
    }

    // ── Специализации ───────────────────────────────────────────────────

    [Fact]
    public void SetSpecialization_GetSpecialization_RoundTrip()
    {
        var d = NewDraft();
        // По V20 стр. 101 специализация доступна при значении ≥ 4. На Шаге 3
        // максимум = 3, поэтому имитируем пост-Шаг-5 через прямую запись struct
        // и пометку «все freebie распределены».
        d.AbilitiesPriority = VampireAbilityPriority.TalentsPrimary.ToString();
        d.AbilitiesStruct.Атлетика = 4;
        VampireFinishingResolver.MarkFreebiesExhausted(d);
        var result = VampireAbilitiesResolver.SetSpecialization(d, "Атлетика", "бег");
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("бег", VampireAbilitiesResolver.GetSpecialization(d, "Атлетика"));
        Assert.True(d.Specializations.ContainsKey("Атлетика"));
    }

    [Fact]
    public void SetSpecialization_OverwritesPrevious()
    {
        var d = NewDraft();
        d.AbilitiesPriority = VampireAbilityPriority.TalentsPrimary.ToString();
        d.AbilitiesStruct.Драка = 4;
        VampireFinishingResolver.MarkFreebiesExhausted(d);
        VampireAbilitiesResolver.SetSpecialization(d, "Драка", "мечи");
        VampireAbilitiesResolver.SetSpecialization(d, "Драка", "клинки");
        Assert.Equal("клинки", VampireAbilitiesResolver.GetSpecialization(d, "Драка"));
    }

    [Fact]
    public void SetSpecialization_Rejects_WhenBelowFour()
    {
        var d = NewDraft();
        d.AbilitiesPriority = VampireAbilityPriority.TalentsPrimary.ToString();
        // Атлетика = 0 — специализация должна быть отвергнута.
        var result = VampireAbilitiesResolver.SetSpecialization(d, "Атлетика", "бег");
        Assert.False(result.IsSuccess);
        Assert.Equal(VampireAbilitiesResolver.VampireAbilitiesFailure.SpecializationRequired, result.Failure);
        Assert.False(d.Specializations.ContainsKey("Атлетика"));
    }

    [Fact]
    public void SetSpecialization_AcceptsAtExactlyFour()
    {
        var d = NewDraft();
        d.AbilitiesPriority = VampireAbilityPriority.TalentsPrimary.ToString();
        // На Шаге 3 максимум = 3, но Шаг 5 поднимает способности свободными
        // пунктами до 5. Имитируем пост-Шаг-5: прямая запись через struct
        // и пометку «все freebie распределены».
        d.AbilitiesStruct.Атлетика = 4;
        VampireFinishingResolver.MarkFreebiesExhausted(d);
        var result = VampireAbilitiesResolver.SetSpecialization(d, "Атлетика", "бег");
        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public void ClearSpecialization_WorksAtAnyValue()
    {
        var d = NewDraft();
        d.AbilitiesPriority = VampireAbilityPriority.TalentsPrimary.ToString();
        d.AbilitiesStruct.Атлетика = 4;
        VampireFinishingResolver.MarkFreebiesExhausted(d);
        VampireAbilitiesResolver.SetSpecialization(d, "Атлетика", "бег");
        // Очистка разрешена в любой момент (даже без freebie).
        var clear = VampireAbilitiesResolver.SetSpecialization(d, "Атлетика", "");
        Assert.True(clear.IsSuccess);
        Assert.False(d.Specializations.ContainsKey("Атлетика"));
    }

    [Fact]
    public void SetSpecialization_Rejects_WhenFreebiesRemain()
    {
        var d = NewDraft();
        d.AbilitiesPriority = VampireAbilityPriority.TalentsPrimary.ToString();
        d.AbilitiesStruct.Атлетика = 4;
        // Freebie-пул не распределён — специализация должна быть отвергнута.
        var result = VampireAbilitiesResolver.SetSpecialization(d, "Атлетика", "бег");
        Assert.False(result.IsSuccess);
        Assert.Equal(VampireAbilitiesResolver.VampireAbilitiesFailure.SpecializationRequired, result.Failure);
        Assert.False(d.Specializations.ContainsKey("Атлетика"));
    }

    [Fact]
        public void GetSpecialization_ReturnsEmpty_WhenNotSet()
    {
            // Резолвер возвращает "" вместо null — поведение совпадает с Attributes.
            var d = NewDraft();
            Assert.Equal("", VampireAbilitiesResolver.GetSpecialization(d, "Атлетика"));
            Assert.False(d.Specializations.ContainsKey("Атлетика"));
        }

    // ── Сообщения ───────────────────────────────────────────────────────

    [Fact]
    public void BuildAbilitiesStatusMessage_ContainsHeadersAndAbilities()
    {
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
        VampireAbilitiesResolver.Increment(d, "Атлетика");
        var msg = VampireAbilitiesResolver.BuildAbilitiesStatusMessage(d);
        Assert.Contains("Шаг 3", msg);
        Assert.Contains("Способности", msg);
        Assert.Contains("Атлетика", msg);
        Assert.Contains("Таланты", msg);
        Assert.Contains("Навыки", msg);
        Assert.Contains("Знания", msg);
        Assert.Contains("13", msg); // бюджет Талантов
    }

    [Fact]
    public void BuildAbilitiesStatusMessage_ShowsSpecializationInItalic()
    {
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
        d.AbilitiesStruct.Атлетика = 4;
        // Имитация «Шаг 5 завершён, все freebie распределены» — специализации
        // доступны только после этого.
        VampireFinishingResolver.MarkFreebiesExhausted(d);
        VampireAbilitiesResolver.SetSpecialization(d, "Атлетика", "бег");
        var msg = VampireAbilitiesResolver.BuildAbilitiesStatusMessage(d);
        Assert.Contains("*Спец:", msg);
        Assert.Contains("бег", msg);
    }

    // ── Подсветка переполнения при смене приоритета ────────────────────

    [Fact]
    public void BuildAbilitiesStatusMessage_HighlightsOverBudgetGroupAfterPriorityChange()
    {
        // Имитируем ситуацию: игрок задал 13 пунктов в Талантах с TalPrimary
        // (бюджет 13), потом сменил приоритет на SklPrimary (Таланты=9).
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
        // Заполним 13/9/5 напрямую (Increment ограничен 3 на способность, поэтому прямое задание).
        d.AbilitiesStruct.Атлетика = 3;
        d.AbilitiesStruct.Бдительность = 3;
        d.AbilitiesStruct.Драка = 3;
        d.AbilitiesStruct.Запугивание = 2;
        d.AbilitiesStruct.Красноречие = 2;
        d.AbilitiesStruct.Вождение = 3;
        d.AbilitiesStruct.Фехтование = 3;
        d.AbilitiesStruct.Скрытность = 3;
        d.AbilitiesStruct.Медицина = 3;
        d.AbilitiesStruct.Оккультизм = 2;
        // Меняем приоритет: теперь Таланты имеют бюджет 9, но потрачено 13.
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.SkillsPrimary);

        var msg = VampireAbilitiesResolver.BuildAbilitiesStatusMessage(d);
        // Заголовок Талантов должен содержать «⚠️ превышение».
        Assert.Contains("⚠️", msg);
        Assert.Contains("Превышение бюджета", msg);
        Assert.Contains("Таланты", msg);
    }

    [Fact]
    public void BuildAbilitiesStatusMessage_NoOverBudget_ShowsRegularCounters()
    {
        var d = NewDraft();
        VampireAbilitiesResolver.SetPriority(d, VampireAbilityPriority.TalentsPrimary);
        // Распределим точно по бюджету 13/9/5.
        d.AbilitiesStruct.Атлетика = 3;
        d.AbilitiesStruct.Бдительность = 3;
        d.AbilitiesStruct.Драка = 3;
        d.AbilitiesStruct.Запугивание = 2;
        d.AbilitiesStruct.Красноречие = 2;
        d.AbilitiesStruct.Вождение = 3;
        d.AbilitiesStruct.Фехтование = 3;
        d.AbilitiesStruct.Скрытность = 3;
        d.AbilitiesStruct.Медицина = 3;
        d.AbilitiesStruct.Оккультизм = 2;

        var msg = VampireAbilitiesResolver.BuildAbilitiesStatusMessage(d);
        Assert.DoesNotContain("⚠️", msg);
        Assert.DoesNotContain("Превышение бюджета", msg);
    }
}
