using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты применения потерь после проверки совести (Roadmap #37, V20 стр. 333).
/// Применяются границы, чтобы персонаж не становился NPC за одну проверку.
/// </summary>
public class VampireMoralityResolverTests
{
    private static VampireCharacter MakeCharacter(int conscience, int selfControl, int humanityBonus)
    {
        var c = new VampireCharacter();
        c.Virtues[VampireParameterCatalog.VirtueConscience] = conscience;
        c.Virtues[VampireParameterCatalog.VirtueSelfControl] = selfControl;
        c.HumanityBonus = humanityBonus;
        return c;
    }

    private static ConscienceApplyResult Apply(VampireConscienceResolver.ConscienceRollOutcome outcome)
        => VampireConscienceResolver.Apply(outcome);

    // ─── Success — ничего не меняется ───────────────────────────────

    [Fact]
    public void Success_NoChanges()
    {
        var c = MakeCharacter(conscience: 4, selfControl: 2, humanityBonus: 1);
        var before = VampireFinishingResolver.ComputeHumanity(c);

        var applied = VampireMoralityResolver.ApplyConscience(c, Apply(VampireConscienceResolver.ConscienceRollOutcome.Success));

        Assert.Equal(0, applied.AppliedHumanityLoss);
        Assert.Equal(0, applied.AppliedConscienceLoss);
        Assert.Null(applied.AddedDerangement);
        Assert.Equal(before, applied.NewHumanity);
        Assert.Equal(4, applied.NewConscience);
        Assert.Equal(1, c.HumanityBonus); // не тронут
    }

    // ─── Failure — минус один к Чел. ────────────────────────────────

    [Fact]
    public void Failure_DecrementsHumanityBonus()
    {
        // Con=4 + SC=2 + Bonus=2 = 8. После Failure должно стать 7.
        var c = MakeCharacter(conscience: 4, selfControl: 2, humanityBonus: 2);
        var before = VampireFinishingResolver.ComputeHumanity(c);

        var applied = VampireMoralityResolver.ApplyConscience(c, Apply(VampireConscienceResolver.ConscienceRollOutcome.Failure));

        Assert.Equal(1, applied.AppliedHumanityLoss);
        Assert.Equal(0, applied.AppliedConscienceLoss);
        Assert.Null(applied.AddedDerangement);
        Assert.Equal(before - 1, applied.NewHumanity);
        Assert.Equal(1, c.HumanityBonus);
        // Совесть не трогается.
        Assert.Equal(4, c.Virtues[VampireParameterCatalog.VirtueConscience]);
    }

    [Fact]
    public void Failure_DoesNotTouchConscience()
    {
        var c = MakeCharacter(conscience: 5, selfControl: 3, humanityBonus: 0);
        VampireMoralityResolver.ApplyConscience(c, Apply(VampireConscienceResolver.ConscienceRollOutcome.Failure));
        Assert.Equal(5, c.Virtues[VampireParameterCatalog.VirtueConscience]);
    }

    // ─── Botch — минус Чел. + минус Совесть + расстройство ────────

    [Fact]
    public void Botch_DecrementsBothAndAddsDerangement()
    {
        // Con=4, SC=2, Bonus=2 → Чел.=8.
        // Ботч по согласованной семантике:
        // 1) HumanityBonus -= 1 (Failure-часть) → Bonus=1.
        // 2) Conscience -= 1 → Con=3.
        // 3) Компенсация формулы: Bonus += 1 → Bonus=2.
        // Итого: Чел.=3+2+2=7 (минус 1 от 8, не минус 2).
        var c = MakeCharacter(conscience: 4, selfControl: 2, humanityBonus: 2);
        var beforeHumanity = VampireFinishingResolver.ComputeHumanity(c);

        var applied = VampireMoralityResolver.ApplyConscience(c, Apply(VampireConscienceResolver.ConscienceRollOutcome.Botch));

        Assert.Equal(1, applied.AppliedHumanityLoss);
        Assert.Equal(1, applied.AppliedConscienceLoss);
        Assert.NotNull(applied.AddedDerangement);
        // Чел. упала ровно на 1 (не на 2 — компенсация формулы работает).
        Assert.Equal(beforeHumanity - 1, applied.NewHumanity);
        Assert.Equal(3, applied.NewConscience);
        // HumanityBonus: −1 от Failure, +1 от компенсации = 0 нетто, остался 2.
        Assert.Equal(2, c.HumanityBonus);
        Assert.Single(c.Derangements!);
    }

    [Fact]
    public void Botch_AddsNewDerangementFromCatalog()
    {
        var c = MakeCharacter(conscience: 4, selfControl: 2, humanityBonus: 0);
        // Уже есть одно расстройство — должно подобраться другое.
        c.Derangements.Add("Биполярное расстройство");

        var applied = VampireMoralityResolver.ApplyConscience(c, Apply(VampireConscienceResolver.ConscienceRollOutcome.Botch));

        Assert.NotNull(applied.AddedDerangement);
        Assert.NotEqual("Биполярное расстройство", applied.AddedDerangement);
        Assert.True(VampireDerangementCatalog.IsKnown(applied.AddedDerangement!));
        Assert.Equal(2, c.Derangements.Count);
    }

    // ─── Границы ──────────────────────────────────────────────────────

    [Fact]
    public void Failure_AtHumanityOne_IsNoop()
    {
        // Con=1 + SC=1 + Bonus=0 → Чел.=2. Потеря −1 → Чел.=1.
        // Проверяем именно NewHumanity — она не должна уйти ниже 1.
        var c = MakeCharacter(conscience: 1, selfControl: 1, humanityBonus: 0);
        // GetVirtueValue не позволяет Con/SC < 1, поэтому единственный способ
        // получить Чел.=1 — выставить HumanityBonus = -1 (Con=1, SC=1, Bonus=-1 = 1).
        c.HumanityBonus = -1;
        var before = VampireFinishingResolver.ComputeHumanity(c);
        Assert.Equal(1, before); // sanity-check

        var applied = VampireMoralityResolver.ApplyConscience(c, Apply(VampireConscienceResolver.ConscienceRollOutcome.Failure));

        // HumanityBonus уже на нижней границе (−(Con+SC)+MinHumanity = −1),
        // снимать нечего → applied.AppliedHumanityLoss = 0, NewHumanity остаётся 1.
        Assert.Equal(0, applied.AppliedHumanityLoss);
        Assert.Equal(1, applied.NewHumanity);
    }

    [Fact]
    public void Failure_BonusAlreadyAtFloor_DoesNotDropBelow1()
    {
        // Con=1, SC=1, Bonus=-1 → Чел.=1. Failure должен оставить Чел.=1.
        var c = MakeCharacter(conscience: 1, selfControl: 1, humanityBonus: -1);

        var applied = VampireMoralityResolver.ApplyConscience(c, Apply(VampireConscienceResolver.ConscienceRollOutcome.Failure));

        Assert.Equal(1, applied.NewHumanity);
        // HumanityBonus не должен уйти ниже minBonus = -1.
        Assert.Equal(-1, c.HumanityBonus);
    }

    [Fact]
    public void Botch_ConscienceNeverDropsBelowOne()
    {
        var c = MakeCharacter(conscience: 1, selfControl: 2, humanityBonus: 1);
        var applied = VampireMoralityResolver.ApplyConscience(c, Apply(VampireConscienceResolver.ConscienceRollOutcome.Botch));

        // Conscience не должна уйти ниже 1 (хоть дельта и предлагает −1).
        Assert.Equal(1, applied.NewConscience);
        // HumanityBonus всё равно теряем (формула позволяет).
        Assert.Equal(1, applied.AppliedHumanityLoss);
        // Расстройство всё равно добавляется.
        Assert.NotNull(applied.AddedDerangement);
    }

    [Fact]
    public void Botch_NullCharacter_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => VampireMoralityResolver.ApplyConscience(null!, Apply(VampireConscienceResolver.ConscienceRollOutcome.Botch)));
    }

    [Fact]
    public void Botch_NullApply_Throws()
    {
        var c = MakeCharacter(conscience: 4, selfControl: 2, humanityBonus: 0);
        Assert.Throws<System.ArgumentNullException>(
            () => VampireMoralityResolver.ApplyConscience(c, null!));
    }
}
