using System;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты Шага 2 «Характеристики 7/5/3» визарда создания персонажа VtM V20.
/// </summary>
public class VampireAttributesTests
{
    private static VampireCharacter NewDraft(string clan = "") => new VampireCharacter
    {
        CharacterId = Guid.NewGuid(),
        PlayerId = 42,
        PlayerName = "TestUser",
        Generation = 13,
        Hunger = 1,
        Clan = clan,
    };

    // ── Каталог: 9 атрибутов + группы ──────────────────────────────────

    [Fact]
    public void Catalog_Physical_HasThreeAttributes()
    {
        Assert.Equal(3, VampireAttributeCatalog.Physical.Count);
        Assert.Contains("Сила", VampireAttributeCatalog.Physical);
        Assert.Contains("Ловкость", VampireAttributeCatalog.Physical);
        Assert.Contains("Выносливость", VampireAttributeCatalog.Physical);
    }

    [Fact]
    public void Catalog_Social_HasThreeAttributes()
    {
        Assert.Equal(3, VampireAttributeCatalog.Social.Count);
        Assert.Contains("Обаяние", VampireAttributeCatalog.Social);
        Assert.Contains("Манипуляция", VampireAttributeCatalog.Social);
        Assert.Contains("Привлекательность", VampireAttributeCatalog.Social);
    }

    [Fact]
    public void Catalog_Mental_HasThreeAttributes()
    {
        Assert.Equal(3, VampireAttributeCatalog.Mental.Count);
        Assert.Contains("Восприятие", VampireAttributeCatalog.Mental);
        Assert.Contains("Интеллект", VampireAttributeCatalog.Mental);
        Assert.Contains("Смекалка", VampireAttributeCatalog.Mental);
    }

    [Fact]
    public void Catalog_FindGroup_Works()
    {
        Assert.Equal(VampireAttributeGroup.Physical, VampireAttributeCatalog.FindGroup("Сила"));
        Assert.Equal(VampireAttributeGroup.Physical, VampireAttributeCatalog.FindGroup("Выносливость"));
        Assert.Equal(VampireAttributeGroup.Social,   VampireAttributeCatalog.FindGroup("Обаяние"));
        Assert.Equal(VampireAttributeGroup.Social,   VampireAttributeCatalog.FindGroup("Привлекательность"));
        Assert.Equal(VampireAttributeGroup.Mental,   VampireAttributeCatalog.FindGroup("Восприятие"));
        Assert.Equal(VampireAttributeGroup.Mental,   VampireAttributeCatalog.FindGroup("Смекалка"));
    }

    [Fact]
    public void Catalog_FindGroup_UnknownReturnsNull()
    {
        Assert.Null(VampireAttributeCatalog.FindGroup("НетТакого"));
        Assert.Null(VampireAttributeCatalog.FindGroup(""));
        Assert.Null(VampireAttributeCatalog.FindGroup(null));
    }

    [Fact]
    public void Catalog_AllPrioritiesHaveUniquePoints()
    {
        foreach (var p in VampireAttributePriorityExtensions.All)
        {
            var p1 = p.PointsFor(VampireAttributeGroup.Physical);
            var p2 = p.PointsFor(VampireAttributeGroup.Social);
            var p3 = p.PointsFor(VampireAttributeGroup.Mental);
            // Каждый приоритет выдаёт ровно 7/5/3 в разном порядке.
            Assert.Equal(7, Math.Max(Math.Max(p1, p2), p3));
            Assert.Equal(3, Math.Min(Math.Min(p1, p2), p3));
            Assert.Equal(5, p1 + p2 + p3 - 7 - 3);
        }
    }

    // ── SetPriority ────────────────────────────────────────────────────

    [Fact]
    public void SetPriority_PersistsAsString()
    {
        var draft = NewDraft();
        var dec = VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
        Assert.Equal(VampireAttributesFailure.None, dec.Failure);
        Assert.Equal("PhysicalPrimary", draft.AttributesPriority);
        // Не завершён — атрибуты не распределены.
        Assert.False(dec.StepComplete);
    }

    [Fact]
    public void SetPriority_AfterFullDistribution_MarksComplete()
    {
        var draft = NewDraft();
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
        // Распределить: физ 7 (3+4), соц 5 (3+2), мент 3 (1+1+1).
        // Сила +4 (до 5), Ловк +1 (до 2), Выносл +2 (до 3)
        VampireAttributesResolver.Increment(draft, "Сила");
        VampireAttributesResolver.Increment(draft, "Сила");
        VampireAttributesResolver.Increment(draft, "Сила");
        VampireAttributesResolver.Increment(draft, "Сила");
        VampireAttributesResolver.Increment(draft, "Ловкость");
        VampireAttributesResolver.Increment(draft, "Выносливость");
        VampireAttributesResolver.Increment(draft, "Выносливость");
        // Соц 5: Обаяние +2, Манип +1, Прив +2
        VampireAttributesResolver.Increment(draft, "Обаяние");
        VampireAttributesResolver.Increment(draft, "Обаяние");
        VampireAttributesResolver.Increment(draft, "Манипуляция");
        VampireAttributesResolver.Increment(draft, "Привлекательность");
        VampireAttributesResolver.Increment(draft, "Привлекательность");
        // Мент 3: Воспр +1, Инт +1, Хитр +1
        VampireAttributesResolver.Increment(draft, "Восприятие");
        VampireAttributesResolver.Increment(draft, "Интеллект");
        VampireAttributesResolver.Increment(draft, "Смекалка");

        Assert.True(VampireAttributesResolver.IsAttributesComplete(draft));
    }

    // ── Increment / Decrement ──────────────────────────────────────────

    [Fact]
    public void Increment_WithoutPriority_Fails()
    {
        var draft = NewDraft();
        var dec = VampireAttributesResolver.Increment(draft, "Сила");
        Assert.Equal(VampireAttributesFailure.PriorityRequired, dec.Failure);
    }

    [Fact]
    public void Increment_UnknownAttribute_Fails()
    {
        var draft = NewDraft();
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
        var dec = VampireAttributesResolver.Increment(draft, "НетТакой");
        Assert.Equal(VampireAttributesFailure.UnknownAttribute, dec.Failure);
    }

    [Fact]
    public void Increment_WithinBudget_Succeeds()
    {
        var draft = NewDraft();
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
        // Физ 7: Сила +4 (1→5).
        for (int i = 0; i < 4; i++)
        {
            var dec = VampireAttributesResolver.Increment(draft, "Сила");
            Assert.True(dec.IsSuccess);
        }
        Assert.Equal(5, draft.AttributesStruct.Strength);
    }

    [Fact]
    public void Increment_ExceedsBudget_Fails()
    {
        var draft = NewDraft();
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
        // Сила +7 (1→8) — физ уже исчерпан (7 очков на 3 атр.).
        // Сначала истратим все 7 на физ:
        // Сила +4, Ловк +1, Выносл +2 = 7.
        for (int i = 0; i < 4; i++) VampireAttributesResolver.Increment(draft, "Сила");
        VampireAttributesResolver.Increment(draft, "Ловкость");
        VampireAttributesResolver.Increment(draft, "Выносливость");
        VampireAttributesResolver.Increment(draft, "Выносливость");
        // Попытка ещё одного инкремента в физ — должна провалиться.
        var dec = VampireAttributesResolver.Increment(draft, "Выносливость");
        Assert.Equal(VampireAttributesFailure.GroupBudgetExceeded, dec.Failure);
        Assert.Equal(3, draft.AttributesStruct.Stamina);
    }

    [Fact]
    public void Decrement_BelowBase_Fails()
    {
        var draft = NewDraft();
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
        var dec = VampireAttributesResolver.Decrement(draft, "Сила");
        Assert.Equal(VampireAttributesFailure.BelowBase, dec.Failure);
        Assert.Equal(1, draft.AttributesStruct.Strength);
    }

    [Fact]
    public void Decrement_Nosferatu_AppearanceCanBeZero()
    {
        var draft = NewDraft(clan: "Носферату");
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
        // Привлекательность=0 (Носферату): уменьшить можно, база=0.
        var dec = VampireAttributesResolver.Decrement(draft, "Привлекательность");
        Assert.True(dec.IsSuccess);
        Assert.Equal(0, draft.AttributesStruct.Appearance);
    }

    [Fact]
    public void Decrement_Nosferatu_AppearanceBelowZero_Fails()
    {
        var draft = NewDraft(clan: "Носферату");
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
        VampireAttributesResolver.Decrement(draft, "Привлекательность"); // 1 → 0
        var dec = VampireAttributesResolver.Decrement(draft, "Привлекательность");
        Assert.Equal(VampireAttributesFailure.BelowBase, dec.Failure);
    }

    [Fact]
    public void Decrement_RestoresValue()
    {
        var draft = NewDraft();
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
        VampireAttributesResolver.Increment(draft, "Сила"); // 1 → 2
        var dec = VampireAttributesResolver.Decrement(draft, "Сила"); // 2 → 1
        Assert.True(dec.IsSuccess);
        Assert.Equal(1, draft.AttributesStruct.Strength);
    }

    // ── Reset ──────────────────────────────────────────────────────────

    [Fact]
    public void ResetProgress_KeepsPriority_ClearsAttributes()
    {
        var draft = NewDraft();
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.MentalPrimary);
        VampireAttributesResolver.Increment(draft, "Восприятие");
        VampireAttributesResolver.Increment(draft, "Интеллект");

        VampireAttributesResolver.ResetProgress(draft);

        Assert.Equal("MentalPrimary", draft.AttributesPriority);
        Assert.Equal(1, draft.AttributesStruct.Perception);
        Assert.Equal(1, draft.AttributesStruct.Intelligence);
    }

    [Fact]
    public void ResetAll_ClearsEverything()
    {
        var draft = NewDraft();
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.MentalPrimary);
        VampireAttributesResolver.Increment(draft, "Восприятие");

        VampireAttributesResolver.ResetAll(draft);

        Assert.Equal("", draft.AttributesPriority);
        Assert.Equal(1, draft.AttributesStruct.Perception);
    }

    // ── IsAttributesComplete / status ──────────────────────────────────

    [Fact]
    public void CompleteCheck_FailsWithoutPriority()
    {
        var draft = NewDraft();
        Assert.False(VampireAttributesResolver.IsAttributesComplete(draft));
    }

    [Fact]
    public void CompleteCheck_FailsWithoutFullDistribution()
    {
        var draft = NewDraft();
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
        // Частично потрачено: 4 пункта (не 15).
        for (int i = 0; i < 4; i++) VampireAttributesResolver.Increment(draft, "Сила");
        Assert.False(VampireAttributesResolver.IsAttributesComplete(draft));
    }

    [Fact]
    public void CompleteCheck_FailsWhenWrongDistribution()
    {
        // Если в группе «потрачено» != бюджета, шаг не завершён.
        var draft = NewDraft();
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
        // Потратим только 6 на физ, не 7.
        for (int i = 0; i < 3; i++) VampireAttributesResolver.Increment(draft, "Сила");
        VampireAttributesResolver.Increment(draft, "Ловкость");
        VampireAttributesResolver.Increment(draft, "Выносливость");
        VampireAttributesResolver.Increment(draft, "Выносливость");
        // Соц 5: Обаяние +2, Манип +1, Прив +2
        VampireAttributesResolver.Increment(draft, "Обаяние");
        VampireAttributesResolver.Increment(draft, "Обаяние");
        VampireAttributesResolver.Increment(draft, "Манипуляция");
        VampireAttributesResolver.Increment(draft, "Привлекательность");
        VampireAttributesResolver.Increment(draft, "Привлекательность");
        // Мент 4 (больше бюджета=3) — допустим, потратили.
        VampireAttributesResolver.Increment(draft, "Восприятие");
        VampireAttributesResolver.Increment(draft, "Интеллект");
        VampireAttributesResolver.Increment(draft, "Смекалка");
        Assert.False(VampireAttributesResolver.IsAttributesComplete(draft)); // физ не на 7
    }

    [Fact]
    public void CompleteCheck_Succeeds_WhenAllGroupsMatchBudget()
    {
        var draft = NewDraft();
        // PhysicalPrimary
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
        // Физ=7: 4+2+1
        for (int i = 0; i < 4; i++) VampireAttributesResolver.Increment(draft, "Сила");
        VampireAttributesResolver.Increment(draft, "Ловкость");
        VampireAttributesResolver.Increment(draft, "Ловкость");
        VampireAttributesResolver.Increment(draft, "Выносливость");
        // Соц=5: 3+1+1
        for (int i = 0; i < 3; i++) VampireAttributesResolver.Increment(draft, "Обаяние");
        VampireAttributesResolver.Increment(draft, "Манипуляция");
        VampireAttributesResolver.Increment(draft, "Привлекательность");
        // Мент=3: 1+1+1
        VampireAttributesResolver.Increment(draft, "Восприятие");
        VampireAttributesResolver.Increment(draft, "Интеллект");
        VampireAttributesResolver.Increment(draft, "Смекалка");
        Assert.True(VampireAttributesResolver.IsAttributesComplete(draft));
        Assert.Equal(15, GetTotalSpent(draft));
    }

    [Fact]
    public void CompleteCheck_AllSixPriorities_Work()
    {
        // Для каждого из 6 приоритетов — выдать корректный сценарий 15 очков,
        // и проверить, что IsAttributesComplete=true.
        foreach (var p in VampireAttributePriorityExtensions.All)
        {
            var d = NewDraft();
            VampireAttributesResolver.SetPriority(d, p);
            var phys = p.PointsFor(VampireAttributeGroup.Physical);
            var soc = p.PointsFor(VampireAttributeGroup.Social);
            var ment = p.PointsFor(VampireAttributeGroup.Mental);

            DistributeGroupFully(d, VampireAttributeCatalog.Physical, phys);
            DistributeGroupFully(d, VampireAttributeCatalog.Social, soc);
            DistributeGroupFully(d, VampireAttributeCatalog.Mental, ment);

            Assert.True(VampireAttributesResolver.IsAttributesComplete(d),
                $"Приоритет {p} не должен быть завершён: физ={phys}, соц={soc}, мент={ment}, total={GetTotalSpent(d)}");
        }
    }

    private static void DistributeGroupFully(VampireCharacter d, System.Collections.Generic.IReadOnlyList<string> names, int points)
    {
        // Раскидываем очки по-разному: первый атрибут получает больше.
        var idx = 0;
        while (points > 0)
        {
            var take = System.Math.Min(points, 3);
            for (int i = 0; i < take; i++)
            {
                var r = VampireAttributesResolver.Increment(d, names[idx]);
                Assert.True(r.IsSuccess);
            }
            points -= take;
            idx = (idx + 1) % names.Count;
        }
    }

    private static int GetTotalSpent(VampireCharacter d) =>
        d.AttributesStruct.Strength + d.AttributesStruct.Dexterity + d.AttributesStruct.Stamina
      + d.AttributesStruct.Charisma + d.AttributesStruct.Manipulation + d.AttributesStruct.Appearance
      + d.AttributesStruct.Perception + d.AttributesStruct.Intelligence + d.AttributesStruct.Wits - 9;

    // ── Status message ────────────────────────────────────────────────

    [Fact]
    public void BuildStatus_ContainsAllNineNames()
    {
        var draft = NewDraft();
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
        var msg = VampireAttributesResolver.BuildAttributesStatusMessage(draft);
        foreach (var name in new[] { "Сила", "Ловкость", "Выносливость",
                                    "Обаяние", "Манипуляция", "Привлекательность",
                                    "Восприятие", "Интеллект", "Смекалка" })
        {
            Assert.Contains(name, msg);
        }
    }

    [Fact]
    public void BuildStatus_ShowsTotalAndRemaining()
    {
        var draft = NewDraft();
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
        VampireAttributesResolver.Increment(draft, "Сила");
        var msg = VampireAttributesResolver.BuildAttributesStatusMessage(draft);
        Assert.Contains("1/15", msg);
    }

    [Fact]
    public void BuildStatus_WithoutPriority_HasPlaceholderHint()
    {
        var draft = NewDraft();
        var msg = VampireAttributesResolver.BuildAttributesStatusMessage(draft);
        Assert.Contains("Приоритет не выбран", msg);
    }

    [Fact]
    public void BuildStatus_NosferatuMentionsAppearanceZero()
    {
        var draft = NewDraft(clan: "Носферату");
            VampireAttributesResolver.ApplyClanRules(draft);
            VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
            var msg = VampireAttributesResolver.BuildAttributesStatusMessage(draft);
            // В тексте статус-сообщения Привлекательность идёт со значением 0.
            Assert.Contains("Привлекательность: **0**", msg);
        }

        // ── Специализации характеристик (V20 стр. 101) ────────────────────

        private static VampireCharacter BuildAttrAt4PlusDraft(string clan = "")
        {
            var draft = NewDraft(clan);
            // Значение 1 (база) + 3 шага = 4 — минимальный порог.
            VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
            VampireAttributesResolver.Increment(draft, "Сила");
            VampireAttributesResolver.Increment(draft, "Сила");
            VampireAttributesResolver.Increment(draft, "Сила");
            // Имитация «Шаг 5 завершён, все freebie распределены» — специализации
            // доступны только после этого.
            VampireFinishingResolver.MarkFreebiesExhausted(draft);
            return draft;
        }

        [Fact]
        public void SetAttributeSpecialization_AcceptsAtFour()
        {
            var draft = BuildAttrAt4PlusDraft();
            Assert.Equal(4, draft.GetAttributeValue("Сила"));
            var r = VampireAttributesResolver.SetAttributeSpecialization(draft, "Сила", "поднятие тяжестей");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal("поднятие тяжестей", VampireAttributesResolver.GetAttributeSpecialization(draft, "Сила"));
        }

        [Fact]
        public void SetAttributeSpecialization_RejectsBelowFour()
        {
            var draft = NewDraft();
            VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
            // Сила = 1 (база), без инкрементов → специализация отвергается.
            var r = VampireAttributesResolver.SetAttributeSpecialization(draft, "Сила", "бег");
            Assert.False(r.IsSuccess);
            Assert.Equal(VampireAttributesFailure.BelowBase, r.Failure);
        }

        [Fact]
        public void SetAttributeSpecialization_RejectsUnknownAttribute()
        {
            var draft = NewDraft();
            var r = VampireAttributesResolver.SetAttributeSpecialization(draft, "Убеждение", "риторика");
            Assert.False(r.IsSuccess);
            Assert.Equal(VampireAttributesFailure.UnknownAttribute, r.Failure);
        }

        [Fact]
        public void SetAttributeSpecialization_NosferatuAppearanceAlwaysRejected()
        {
            var draft = NewDraft(clan: "Носферату");
            VampireAttributesResolver.ApplyClanRules(draft);
            VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
            // Привлекательность = 0 у Носферату даже с приоритетом SocialPrimary и попытками инкремента.
            var r = VampireAttributesResolver.SetAttributeSpecialization(draft, "Привлекательность", "обаяние");
            Assert.False(r.IsSuccess);
            Assert.Equal(VampireAttributesFailure.BelowBase, r.Failure);
            Assert.Contains("0", r.Message);
        }

        [Fact]
        public void SetAttributeSpecialization_OverwritesPrevious()
        {
            var draft = BuildAttrAt4PlusDraft();
            VampireAttributesResolver.SetAttributeSpecialization(draft, "Сила", "поднятие тяжестей");
            VampireAttributesResolver.SetAttributeSpecialization(draft, "Сила", "единоборства");
            Assert.Equal("единоборства", VampireAttributesResolver.GetAttributeSpecialization(draft, "Сила"));
        }

        [Fact]
        public void SetAttributeSpecialization_EmptyClearsSpec()
        {
            var draft = BuildAttrAt4PlusDraft();
            VampireAttributesResolver.SetAttributeSpecialization(draft, "Сила", "поднятие тяжестей");
            var r = VampireAttributesResolver.SetAttributeSpecialization(draft, "Сила", "");
            Assert.True(r.IsSuccess);
            Assert.Equal("", VampireAttributesResolver.GetAttributeSpecialization(draft, "Сила"));
        }

        [Fact]
        public void SetAttributeSpecialization_AppearsInStatusMessage()
        {
            var draft = BuildAttrAt4PlusDraft();
            VampireAttributesResolver.SetAttributeSpecialization(draft, "Сила", "поднятие тяжестей");
            var msg = VampireAttributesResolver.BuildAttributesStatusMessage(draft);
            Assert.Contains("поднятие тяжестей", msg);
        }

        [Fact]
        public void SetAttributeSpecialization_RejectsWhenFreebiesRemain()
        {
            var draft = NewDraft();
            VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalPrimary);
            VampireAttributesResolver.Increment(draft, "Сила");
            VampireAttributesResolver.Increment(draft, "Сила");
            VampireAttributesResolver.Increment(draft, "Сила");
            // Сила = 4, но freebie не распределены.
            var r = VampireAttributesResolver.SetAttributeSpecialization(draft, "Сила", "поднятие тяжестей");
            Assert.False(r.IsSuccess);
            Assert.Equal(VampireAttributesFailure.BelowBase, r.Failure);
            Assert.False(draft.Specializations.ContainsKey("Сила"));
        }

        [Fact]
        public void UniversalSetSpecialization_AcceptsAttributesAndAbilities()
        {
            // Тот же метод SetSpecialization теперь работает и для характеристик.
            var draft = BuildAttrAt4PlusDraft();
            var attrRes = VampireAbilitiesResolver.SetSpecialization(draft, "Сила", "поднятие тяжестей");
            Assert.True(attrRes.IsSuccess, attrRes.Message);
            Assert.Equal("поднятие тяжестей", VampireAbilitiesResolver.GetSpecialization(draft, "Сила"));
        }
    }
