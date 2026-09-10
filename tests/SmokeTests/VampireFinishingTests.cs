using System;
using System.Linq;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты Шага 5 «Последние штрихи» визарда VtM V20.
/// Покрывает: формулы Чел/Воли (read-only), канонический freebie-курс
/// 5/2/7/1/2 на пять параметров, пул 15, кэпы, undo/reset.
/// </summary>
public class VampireFinishingTests
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

    /// <summary>Распределить базово добродетели (как сделал бы Шаг 4.3) — 1/1/1 + N пунктов сверху.</summary>
    private static void SeedVirtues(VampireCharacter draft, int conscience = 1, int selfControl = 1, int courage = 1)
    {
        draft.Virtues[VampireParameterCatalog.VirtueConscience] = conscience;
        draft.Virtues[VampireParameterCatalog.VirtueSelfControl] = selfControl;
        draft.Virtues[VampireParameterCatalog.VirtueCourage] = courage;
    }

    // ── Каталог: курс и пул freebie ───────────────────────────────────

    [Fact]
    public void Catalog_FreebiePoolIs15()
    {
        Assert.Equal(15, VampireFinishingResolver.FreebiePool);
    }

    [Fact]
    public void Catalog_CostPerTarget_MatchesV20p86()
    {
        Assert.Equal(5, VampireFinishingResolver.CostOf(VampireFinishingResolver.FreebieTarget.Attribute));
        Assert.Equal(2, VampireFinishingResolver.CostOf(VampireFinishingResolver.FreebieTarget.Ability));
        Assert.Equal(7, VampireFinishingResolver.CostOf(VampireFinishingResolver.FreebieTarget.Discipline));
        Assert.Equal(1, VampireFinishingResolver.CostOf(VampireFinishingResolver.FreebieTarget.Background));
        Assert.Equal(2, VampireFinishingResolver.CostOf(VampireFinishingResolver.FreebieTarget.Virtue));
    }

    [Fact]
    public void Catalog_TargetName_IsReadable()
    {
        var d = NewDraft();
        Assert.Equal("Характеристика",  VampireFinishingResolver.TargetName(VampireFinishingResolver.FreebieTarget.Attribute, d));
        Assert.Equal("Способность",     VampireFinishingResolver.TargetName(VampireFinishingResolver.FreebieTarget.Ability, d));
        Assert.Equal("Дисциплина",      VampireFinishingResolver.TargetName(VampireFinishingResolver.FreebieTarget.Discipline, d));
        Assert.Equal("Факт биографии",  VampireFinishingResolver.TargetName(VampireFinishingResolver.FreebieTarget.Background, d));
        Assert.Equal("Добродетель",     VampireFinishingResolver.TargetName(VampireFinishingResolver.FreebieTarget.Virtue, d));
    }

    [Fact]
    public void Catalog_FreebieTargets_ContainsSeven()
    {
        Assert.Equal(7, VampireFinishingResolver.FreebieTargets.Count);
        Assert.Contains(VampireFinishingResolver.FreebieTarget.Attribute, VampireFinishingResolver.FreebieTargets);
        Assert.Contains(VampireFinishingResolver.FreebieTarget.Ability, VampireFinishingResolver.FreebieTargets);
        Assert.Contains(VampireFinishingResolver.FreebieTarget.Discipline, VampireFinishingResolver.FreebieTargets);
        Assert.Contains(VampireFinishingResolver.FreebieTarget.Background, VampireFinishingResolver.FreebieTargets);
        Assert.Contains(VampireFinishingResolver.FreebieTarget.Virtue, VampireFinishingResolver.FreebieTargets);
        Assert.Contains(VampireFinishingResolver.FreebieTarget.Humanity, VampireFinishingResolver.FreebieTargets);
        Assert.Contains(VampireFinishingResolver.FreebieTarget.Willpower, VampireFinishingResolver.FreebieTargets);
    }

    // ── Формулы производных ────────────────────────────────────────────

    [Fact]
    public void Humanity_DefaultBase_Is2()
    {
        // База 1/1/1 — Чел = 2
        var d = NewDraft();
        SeedVirtues(d);
        Assert.Equal(2, VampireFinishingResolver.ComputeHumanity(d));
        Assert.Equal(1, VampireFinishingResolver.ComputeWillpower(d));
    }

    [Fact]
    public void Humanity_MatchesConsciencePlusSelfControl()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 3, selfControl: 4, courage: 3);
        Assert.Equal(7, VampireFinishingResolver.ComputeHumanity(d));
        Assert.Equal(3, VampireFinishingResolver.ComputeWillpower(d));
    }

    [Fact]
    public void Humanity_IsCappedAt10()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 6, selfControl: 6, courage: 8);
        Assert.Equal(10, VampireFinishingResolver.ComputeHumanity(d));
        Assert.Equal(8,  VampireFinishingResolver.ComputeWillpower(d));
    }

    [Fact]
    public void DescribeHumanity_ShowsBreakdown()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 4, selfControl: 3, courage: 3);
        Assert.Contains("7",  VampireFinishingResolver.DescribeHumanity(d));
        Assert.Contains("Совесть 4",     VampireFinishingResolver.DescribeHumanity(d));
        Assert.Contains("Самоконтроль 3", VampireFinishingResolver.DescribeHumanity(d));
    }

    [Fact]
    public void DescribeWillpower_EqualsCourage()
    {
        var d = NewDraft();
        SeedVirtues(d, courage: 4);
        Assert.Equal("4 (Смелость 4)", VampireFinishingResolver.DescribeWillpower(d));
    }

    // ── Freebie pool tracking ──────────────────────────────────────────

    [Fact]
    public void RemainingFreebies_FullOnEmptyDraft()
    {
        var d = NewDraft();
        Assert.Equal(15, VampireFinishingResolver.RemainingFreebies(d));
        Assert.Equal(0,  VampireFinishingResolver.ConsumedFreebies(d));
    }

    [Fact]
    public void IsFinishingComplete_EmptyDraft_IsFalse()
    {
        var d = NewDraft();
        // Пул полный, ничего не потрачено, не подтверждено — Шаг 5 не завершён.
        Assert.False(VampireFinishingResolver.IsFinishingComplete(d));
    }

    [Fact]
    public void IsFinishingComplete_AfterExhaustedPool_IsTrue()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 2, selfControl: 2, courage: 2);
        // 3 раза тратим 5 очков на 3 разных атрибута → пул = 15/15 → 0
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Сила", out _).IsSuccess);
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Ловкость", out _).IsSuccess);
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Выносливость", out _).IsSuccess);
        Assert.Equal(0, VampireFinishingResolver.RemainingFreebies(d));
        Assert.True(VampireFinishingResolver.IsFinishingComplete(d));
    }

    [Fact]
    public void IsFinishingComplete_AfterUserConfirm_IsTrue()
    {
        var d = NewDraft();
        // Пул полный, ничего не потрачено, но игрок явно подтвердил.
        VampireFinishingResolver.ConfirmStep5(d);
        Assert.True(VampireFinishingResolver.IsFinishingComplete(d));
    }

    [Fact]
    public void Allocations_DecreaseRemainingPool()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 2, selfControl: 2, courage: 2);
        // 7 freebie: −1 +1 к способности (cost 2), и −1 +1 к факту (cost 1).
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Background, "Состояние", out _).IsSuccess);
        Assert.Equal(14, VampireFinishingResolver.RemainingFreebies(d));
        Assert.Equal(1, VampireFinishingResolver.ConsumedFreebies(d));
    }

    [Fact]
    public void PoolExhausted_RejectsFurther()
    {
        var d = NewDraft();
        SeedVirtues(d, courage: 4);
        // 15 = 1 × 7 + 1 × 5 + 1 × 2 + 1 × 1 — все заняты.
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Discipline, "Доминирование", out _).IsSuccess); // -7
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute,  "Сила", out _).IsSuccess);              // -5
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Ability,    "Атлетика", out _).IsSuccess);          // -2
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Background, "Состояние", out _).IsSuccess);          // -1
        Assert.Equal(0, VampireFinishingResolver.RemainingFreebies(d));

        var rej = VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Virtue, VampireParameterCatalog.VirtueCourage, out _);
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireFinishingResolver.Failure.PoolExhausted, rej.Failure);
    }

    // ── Spend / Undo на полях ─────────────────────────────────────────

    [Fact]
    public void Allocate_OnAttribute_WritesAndRecords()
    {
        var d = NewDraft();
        SeedVirtues(d);
        var r = VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Сила", out _);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(1, d.Attributes["Сила"]);
        Assert.Equal(1, VampireFinishingResolver.FreebieSpentOn(d, VampireFinishingResolver.FreebieTarget.Attribute, "Сила"));
    }

    [Fact]
    public void Allocate_OnAbility_WritesAndRecords_ThroughReflection()
    {
        var d = NewDraft();
        SeedVirtues(d);
        var r = VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Ability, "Атлетика", out _);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(1, VampireFinishingResolver.ReadFieldValue(d, VampireFinishingResolver.FreebieTarget.Ability, "Атлетика"));
        Assert.Equal(1, VampireFinishingResolver.FreebieSpentOn(d, VampireFinishingResolver.FreebieTarget.Ability, "Атлетика"));
    }

    [Fact]
    public void Allocate_OnDiscipline_AddsToFreebiePool()
    {
        var d = NewDraft();
        SeedVirtues(d);
        var r = VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Discipline, "Доминирование", out _);
        Assert.True(r.IsSuccess, r.Message);
        // Freebie-вклад пишется в FreebieDisciplines (Шаг 4.1 использует draft.Disciplines).
        Assert.Equal(1, d.FreebieDisciplines["Доминирование"]);
    }

    [Fact]
    public void Allocate_OnBackground_CreatesKey()
    {
        var d = NewDraft();
        SeedVirtues(d);
        var r = VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Background, "Состояние", out _);
        Assert.True(r.IsSuccess, r.Message);
        // Freebie-вклад пишется в FreebieBackgrounds (Шаг 4.2 использует draft.Backgrounds).
        Assert.Equal(1, d.FreebieBackgrounds["Состояние"]);
    }

    [Fact]
    public void Allocate_OnVirtue_GrowsHumanityAndWillpower()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 2, selfControl: 3, courage: 3);
        Assert.Equal(5, VampireFinishingResolver.ComputeHumanity(d));
        Assert.Equal(3, VampireFinishingResolver.ComputeWillpower(d));

        var r1 = VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Virtue, VampireParameterCatalog.VirtueConscience, out _);
        Assert.True(r1.IsSuccess);
        var r2 = VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Virtue, VampireParameterCatalog.VirtueCourage, out _);
        Assert.True(r2.IsSuccess);

            // Чел = (2+1) + 3 = 6; Воля = (3+1) = 4.
            Assert.Equal(6, VampireFinishingResolver.ComputeHumanity(d));
        Assert.Equal(4, VampireFinishingResolver.ComputeWillpower(d));
    }

    [Fact]
    public void Deallocate_ReturnsPointToPool()
    {
        var d = NewDraft();
        SeedVirtues(d);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Discipline, "Доминирование", out _); // -7
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Background, "Состояние", out _); // -1
            // Всего потрачено 7 (дисциплина) + 1 (фон) = 8.
            Assert.Equal(8, VampireFinishingResolver.ConsumedFreebies(d));
            Assert.Equal(7, VampireFinishingResolver.RemainingFreebies(d));

            var undo = VampireFinishingResolver.DeallocateFreebie(d, VampireFinishingResolver.FreebieTarget.Discipline, "Доминирование");
            Assert.True(undo.IsSuccess);
            Assert.False(d.Disciplines.ContainsKey("Доминирование"));
            Assert.Equal(1, VampireFinishingResolver.ConsumedFreebies(d));
            Assert.Equal(14, VampireFinishingResolver.RemainingFreebies(d));
        }

    [Fact]
    public void Deallocate_OnUnknownFails()
    {
        var d = NewDraft();
        SeedVirtues(d);
        var rej = VampireFinishingResolver.DeallocateFreebie(d, VampireFinishingResolver.FreebieTarget.Discipline, "Доминирование");
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireFinishingResolver.Failure.NotApplicable, rej.Failure);
    }

    [Fact]
    public void Allocate_BeyondHardCap_Fails()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 5, selfControl: 5, courage: 5);
        // Совесть уже 5, попытка поднять — кэп.
        var rej = VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Virtue, VampireParameterCatalog.VirtueConscience, out _);
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireFinishingResolver.Failure.AboveCap, rej.Failure);
    }

    [Fact]
    public void Allocate_InvalidName_Fails()
    {
        var d = NewDraft();
        SeedVirtues(d);
        var rej = VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "", out _);
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireFinishingResolver.Failure.InvalidName, rej.Failure);
    }

    [Fact]
    public void Allocate_OnUnknownAbilityFails()
    {
        var d = NewDraft();
        SeedVirtues(d);
        var rej = VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Ability, "NotARealAbility", out _);
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireFinishingResolver.Failure.UnknownField, rej.Failure);
    }

    // ── Reset ──────────────────────────────────────────────────────────

    [Fact]
    public void ResetFreebies_RestoresAllSpentPoints()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 2, selfControl: 3, courage: 4);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Discipline, "Доминирование", out _); // -7
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Сила", out _);              // -5
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Ability, "Атлетика", out _);            // -2
        Assert.Equal(14, VampireFinishingResolver.ConsumedFreebies(d));

        var r = VampireFinishingResolver.ResetFreebies(d);
        Assert.True(r.IsSuccess);

        Assert.Equal(0,  VampireFinishingResolver.ConsumedFreebies(d));
        Assert.Equal(15, VampireFinishingResolver.RemainingFreebies(d));
        Assert.Empty(d.FreebieSpent);
        Assert.False(d.Disciplines.ContainsKey("Доминирование"));
        Assert.False(d.Attributes.ContainsKey("Сила"));
        Assert.Equal(0, VampireFinishingResolver.ReadFieldValue(d, VampireFinishingResolver.FreebieTarget.Ability, "Атлетика"));
    }

    [Fact]
    public void ResetFreebies_OnEmpty_IsOk()
    {
        var d = NewDraft();
        SeedVirtues(d);
        var r = VampireFinishingResolver.ResetFreebies(d);
        Assert.True(r.IsSuccess);
    }

    // ── Status message ────────────────────────────────────────────────

    [Fact]
    public void BuildStatusMessage_IncludesAllSections()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 3, selfControl: 4, courage: 4);
        d.Weakness = "Проклятие Бруха";
        d.Health = new HealthState(7);

        var msg = VampireFinishingResolver.BuildStatusMessage(d);
        Assert.Contains("Шаг 5", msg);
        Assert.Contains("Человечность", msg);
        Assert.Contains("Воля", msg);
        Assert.Contains("Голод", msg);
        Assert.Contains("Слабость", msg);
        Assert.Contains("Проклятие Бруха", msg);
        Assert.Contains("Свободные пункты", msg);
        Assert.Contains("осталось 15", msg);
        Assert.Contains("потрачено 0", msg);
    }

    [Fact]
    public void BuildStatusMessage_AfterSpend_ShowsRemainingAndConsumed()
    {
        var d = NewDraft();
        SeedVirtues(d);
        // -5 + -2 = 7 потрачено, 8 осталось.
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Сила", out _);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Ability, "Атлетика", out _);

        var msg = VampireFinishingResolver.BuildStatusMessage(d);
        Assert.Contains("осталось 8", msg);
        Assert.Contains("потрачено 7", msg);
        Assert.Contains("Характеристика", msg);
        Assert.Contains("Способность", msg);
    }

    [Fact]
    public void DescribeSpent_ListsEachEntry()
    {
        var d = NewDraft();
        SeedVirtues(d, courage: 3);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Discipline, "Доминирование", out _);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Сила", out _);

        var list = VampireFinishingResolver.DescribeSpent(d);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, x => x.Contains("Дисциплина") && x.Contains("Доминирование") && x.Contains("-7"));
        Assert.Contains(list, x => x.Contains("Характеристика") && x.Contains("Сила") && x.Contains("-5"));
    }

    // ── Humanity / Willpower как цели freebie ────────────────────────

    [Fact]
    public void AllocateHumanity_GrowsBonus_AndRaisesCompute()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 2, selfControl: 2, courage: 2);
        // Базовая формула: Чел = 4, Воля = 2.
        Assert.Equal(4, VampireFinishingResolver.ComputeHumanity(d));
        Assert.Equal(2, VampireFinishingResolver.ComputeWillpower(d));

        // -2 на Чел → бонус +1, итог 5.
        var r = VampireFinishingResolver.AllocateFreebie(
            d, VampireFinishingResolver.FreebieTarget.Humanity, "Человечность", out _);
        Assert.True(r.IsSuccess);
        Assert.Equal(1, d.HumanityBonus);
        Assert.Equal(5, VampireFinishingResolver.ComputeHumanity(d));
        // Воля не должна меняться.
        Assert.Equal(2, VampireFinishingResolver.ComputeWillpower(d));
    }

    [Fact]
    public void AllocateWillpower_GrowsBonus_AndRaisesCompute()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 2, selfControl: 2, courage: 2);
        // -1 на Волю → бонус +1, итог 3.
        var r = VampireFinishingResolver.AllocateFreebie(
            d, VampireFinishingResolver.FreebieTarget.Willpower, "Воля", out _);
        Assert.True(r.IsSuccess);
        Assert.Equal(1, d.WillpowerBonus);
        Assert.Equal(3, VampireFinishingResolver.ComputeWillpower(d));
        Assert.Equal(4, VampireFinishingResolver.ComputeHumanity(d));
    }

        // ─── Raw / Status (аудит п. 3) ───────────────────────────────────────────

        [Fact]
        public void ComputeHumanity_HasFloorOf1_BackwardsCompatibility()
        {
            // Защитный кэп остаётся (для обратной совместимости с моральным кодексом).
            var d = NewDraft();
            SeedVirtues(d, conscience: 1, selfControl: 1, courage: 1);
            d.HumanityBonus = -10;
            Assert.Equal(1, VampireFinishingResolver.ComputeHumanity(d));
        }

        [Fact]
        public void ComputeRawHumanity_NoFloor_AllowsZero()
        {
            // Virtue не может быть меньше MinVirtue=1, но HumanityBonus может свести к 0.
            var d = NewDraft();
            SeedVirtues(d, conscience: 1, selfControl: 1, courage: 1);
            d.HumanityBonus = -2; // 1+1-2 = 0 — минимальная Чел. по V20.
            Assert.Equal(0, VampireFinishingResolver.ComputeRawHumanity(d));

            // Отрицательный HumanityBonus не приводит к отрицательной Чел.:
            // V20 не определяет значений ниже 0 (это уже полное небытие по лору).
            d.HumanityBonus = -10;
            Assert.Equal(0, VampireFinishingResolver.ComputeRawHumanity(d));
        }

        [Fact]
        public void ComputeRawWillpower_NoFloor_AllowsZero()
        {
            var d = NewDraft();
            SeedVirtues(d, conscience: 1, selfControl: 1, courage: 1);
            d.WillpowerBonus = -1; // 1-1=0
            Assert.Equal(0, VampireFinishingResolver.ComputeRawWillpower(d));

            d.WillpowerBonus = -7;
            Assert.Equal(0, VampireFinishingResolver.ComputeRawWillpower(d));
        }

        [Fact]
        public void ComputeStatus_Normal_WhenBothPositive()
        {
            var d = NewDraft();
            SeedVirtues(d, conscience: 3, selfControl: 3, courage: 3);
            Assert.Equal(VampireFinishingResolver.CharacterStatus.Normal,
                VampireFinishingResolver.ComputeStatus(d));
        }

        [Fact]
        public void ComputeStatus_Withered_WhenHumanityZeroOrBelow()
        {
            var d = NewDraft();
            SeedVirtues(d, conscience: 1, selfControl: 1, courage: 5);
            d.HumanityBonus = -2; // 1+1-2 = 0
            Assert.Equal(VampireFinishingResolver.CharacterStatus.Withered,
                VampireFinishingResolver.ComputeStatus(d));
        }

        [Fact]
        public void ComputeStatus_Enervated_WhenWillpowerZeroOrBelow()
        {
            var d = NewDraft();
            SeedVirtues(d, conscience: 5, selfControl: 5, courage: 1);
            d.WillpowerBonus = -1; // 1-1 = 0
            Assert.Equal(VampireFinishingResolver.CharacterStatus.Enervated,
                VampireFinishingResolver.ComputeStatus(d));
        }

        [Fact]
        public void ComputeStatus_Shattered_WhenBothZero()
        {
            var d = NewDraft();
            SeedVirtues(d, conscience: 1, selfControl: 1, courage: 1);
            d.HumanityBonus = -2;  // 1+1-2=0
            d.WillpowerBonus = -1; // 1-1=0
            Assert.Equal(VampireFinishingResolver.CharacterStatus.Shattered,
                VampireFinishingResolver.ComputeStatus(d));
        }

        [Fact]
        public void DescribeStatus_IncludesWarningGlyph()
        {
            var n = VampireFinishingResolver.DescribeStatus(VampireFinishingResolver.CharacterStatus.Normal);
            Assert.Equal("Норма", n);
            Assert.Contains("Окоченение", VampireFinishingResolver.DescribeStatus(VampireFinishingResolver.CharacterStatus.Withered));
            Assert.Contains("Безвольный", VampireFinishingResolver.DescribeStatus(VampireFinishingResolver.CharacterStatus.Enervated));
        }

    [Fact]
    public void AllocateHumanity_AfterVirtueSpend_BothCount()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 3, selfControl: 3, courage: 3);
        // Формула: Чел 6, Воля 3.
        Assert.Equal(6, VampireFinishingResolver.ComputeHumanity(d));
        Assert.Equal(3, VampireFinishingResolver.ComputeWillpower(d));

        // +2 в Смелость → Воля 5, Чел не меняется.
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Virtue,
            VampireParameterCatalog.VirtueCourage, out _);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Virtue,
            VampireParameterCatalog.VirtueCourage, out _);
        Assert.Equal(6, VampireFinishingResolver.ComputeHumanity(d));
        Assert.Equal(5, VampireFinishingResolver.ComputeWillpower(d));

        // +1 на Чел freebie → итог 7.
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Humanity,
            "Человечность", out _);
        Assert.Equal(1, d.HumanityBonus);
        Assert.Equal(7, VampireFinishingResolver.ComputeHumanity(d));
        // Воля по-прежнему 5.
        Assert.Equal(5, VampireFinishingResolver.ComputeWillpower(d));
    }

    [Fact]
    public void HumanityCap_RespectsFormulaLimit()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 4, selfControl: 4, courage: 1);
        // Формула Чел = 8, бонус можно докинуть только 2 (кэп 10).
        var (cap, _) = VampireFinishingResolver.GetCaps(
            VampireFinishingResolver.FreebieTarget.Humanity, d);
        Assert.Equal(2, cap);

        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Humanity, "Человечность", out _);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Humanity, "Человечность", out _);
        // Третья попытка должна быть отвергнута — кэп.
        var rej = VampireFinishingResolver.AllocateFreebie(
            d, VampireFinishingResolver.FreebieTarget.Humanity, "Человечность", out _);
        Assert.False(rej.IsSuccess);
        Assert.Equal(VampireFinishingResolver.Failure.AboveCap, rej.Failure);
        Assert.Equal(10, VampireFinishingResolver.ComputeHumanity(d));
    }

    [Fact]
    public void WillpowerCap_RespectsFormulaLimit()
    {
        var d = NewDraft();
        SeedVirtues(d, courage: 3);
        // Формула Воля = 3, бонус можно докинуть 7.
        var (cap, _) = VampireFinishingResolver.GetCaps(
            VampireFinishingResolver.FreebieTarget.Willpower, d);
        Assert.Equal(7, cap);
        // -1×7 = 7 пунктов.
        for (int i = 0; i < 7; i++)
            VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Willpower, "Воля", out _);
        // 8-я попытка — кэп.
        var rej = VampireFinishingResolver.AllocateFreebie(
            d, VampireFinishingResolver.FreebieTarget.Willpower, "Воля", out _);
        Assert.False(rej.IsSuccess);
        Assert.Equal(10, VampireFinishingResolver.ComputeWillpower(d));
    }

    [Fact]
    public void DeallocateHumanity_ReturnsPointToPool()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 2, selfControl: 2, courage: 2);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Humanity, "Человечность", out _);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Willpower, "Воля", out _);
        // Потрачено: 2 + 1 = 3, осталось 12.
        Assert.Equal(12, VampireFinishingResolver.RemainingFreebies(d));

        var r = VampireFinishingResolver.DeallocateFreebie(d, VampireFinishingResolver.FreebieTarget.Humanity, "Человечность");
        Assert.True(r.IsSuccess);
        Assert.Equal(0, d.HumanityBonus);
        Assert.Equal(14, VampireFinishingResolver.RemainingFreebies(d));
    }

    [Fact]
    public void ResetFreebies_AlsoClearsHumanityAndWillpowerBonuses()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 2, selfControl: 2, courage: 2);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Humanity, "Человечность", out _);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Willpower, "Воля", out _);
        Assert.Equal(1, d.HumanityBonus);
        Assert.Equal(1, d.WillpowerBonus);

        VampireFinishingResolver.ResetFreebies(d);
        Assert.Equal(0, d.HumanityBonus);
        Assert.Equal(0, d.WillpowerBonus);
        Assert.Equal(15, VampireFinishingResolver.RemainingFreebies(d));
        // Формула возвращается к чистой.
        Assert.Equal(4, VampireFinishingResolver.ComputeHumanity(d));
        Assert.Equal(2, VampireFinishingResolver.ComputeWillpower(d));
    }

    [Fact]
    public void HumanityFreebie_PlusVirtue_StacksInCompute()
    {
        var d = NewDraft();
        SeedVirtues(d, conscience: 1, selfControl: 1, courage: 1);
        // Старт: Чел=2, Воля=1.
        // +2 в Совесть (Шаг 4.3 → формула Чел=4), +1 в Чел freebie → итого 5.
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Virtue,
            VampireParameterCatalog.VirtueConscience, out _);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Virtue,
            VampireParameterCatalog.VirtueConscience, out _);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Humanity,
            "Человечность", out _);
        Assert.Equal(5, VampireFinishingResolver.ComputeHumanity(d));
        Assert.Equal(1, VampireFinishingResolver.ComputeWillpower(d));
    }

    // ── Здоровье (V20: фиксировано 7 ячеек) ─────────────────────────

    [Fact]
    public void HealthTrackSize_IsSeven_ByV20()
    {
        // V20 стр. 263: шкала фиксирована для всех персонажей.
        Assert.Equal(7, VampireFinishingResolver.HealthTrackSize);
    }

    [Fact]
    public void EnsureHealth_FromNull_CreatesV20Scale()
    {
        var d = NewDraft();
        Assert.Null(d.Health);
        VampireFinishingResolver.EnsureHealth(d);
        Assert.NotNull(d.Health);
        Assert.Equal(7, d.Health!.Size);
        Assert.Equal("SSSSSSS", d.Health!.Render());
        Assert.Equal(0, d.Health!.Penalty);
    }

    [Fact]
    public void EnsureHealth_Idempotent_DoesNotOverwrite()
    {
        var d = NewDraft();
        d.Health = new HealthState(7);
        d.Health.ApplyLethal(1); // //SSSSSS
        VampireFinishingResolver.EnsureHealth(d);
        Assert.Equal("//SSSSS", d.Health!.Render());
    }

    [Fact]
    public void BuildStatusMessage_NullHealth_AutoCreatesV20Scale()
    {
        var d = NewDraft();
        SeedVirtues(d);
        Assert.Null(d.Health);
        var msg = VampireFinishingResolver.BuildStatusMessage(d);
        Assert.Contains("Здоровье:", msg);
        Assert.Contains("SSSSSSS", msg);
        Assert.Contains("7 ячеек", msg);
        Assert.DoesNotContain("не настроено", msg);
    }

    [Fact]
    public void BuildStatusMessage_DamagedHealth_ShowsV20Penalty()
    {
        // Аграва на 0..2 → "AASS" на шкале 7 ячеек.
        var d = NewDraft();
        SeedVirtues(d);
        d.Health = new HealthState(7);
        d.Health.ApplyAggravated(2);
        var msg = VampireFinishingResolver.BuildStatusMessage(d);
        Assert.Contains("AASS", msg);
        Assert.Contains("штраф:", msg);
    }

    // ── Голод (read-only) ────────────────────────────────────────────

    [Fact]
    public void BuildStatusMessage_ShowsCurrentHunger()
    {
        var d = NewDraft();
        SeedVirtues(d);
        d.Hunger = 1; // канон V20
        var msg = VampireFinishingResolver.BuildStatusMessage(d);
        Assert.Contains("Голод:        1", msg);
    }

    [Fact]
    public void BuildStatusMessage_HungerNotHardcoded()
    {
        // Если голод повысился в ходе сцены — UI должен показать реальное значение,
        // а не захардкоженную 1.
        var d = NewDraft();
        SeedVirtues(d);
        d.Hunger = 4;
        var msg = VampireFinishingResolver.BuildStatusMessage(d);
        Assert.Contains("Голод:        4", msg);
    }
}

