using System;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

public class VampireDiablerieResolverTests
{
    // ─── Базовые успешные случаи ────────────────────────────────────────

    [Fact]
    public void Resolve_Success_GapOne_DropsByOne()
    {
        var r = VampireDiablerieResolver.Resolve(
            attackerGeneration: 13,
            victimGeneration: 12,
            attackerHumanity: 7,
            success: true);

        Assert.True(r.Success);
        Assert.Equal(13, r.AttackerGenerationBefore);
        Assert.Equal(12, r.AttackerGenerationAfter);
        Assert.Equal(1, r.GenerationDrop);
    }

    [Fact]
    public void Resolve_Success_GapFive_DropsByThree()
    {
        var r = VampireDiablerieResolver.Resolve(
            attackerGeneration: 12,
            victimGeneration: 7,
            attackerHumanity: 7,
            success: true);

        Assert.Equal(3, r.GenerationDrop);
        Assert.Equal(9, r.AttackerGenerationAfter);
    }

    [Fact]
    public void Resolve_Success_GapFour_DropsByOne()
    {
        var r = VampireDiablerieResolver.Resolve(
            attackerGeneration: 12,
            victimGeneration: 8,
            attackerHumanity: 7,
            success: true);

        Assert.Equal(1, r.GenerationDrop);
        Assert.Equal(11, r.AttackerGenerationAfter);
    }

    [Fact]
    public void Resolve_Success_VictimOlder_DropsGeneration()
    {
        // жертва старше (меньше число), атакующий — неофициальный новообращённый
        var r = VampireDiablerieResolver.Resolve(
            attackerGeneration: 13,
            victimGeneration: 8,
            attackerHumanity: 5,
            success: true);

        Assert.Equal(3, r.GenerationDrop);
        Assert.Equal(10, r.AttackerGenerationAfter);
    }

    [Fact]
    public void Resolve_Success_AttackerYoungerThanVictim_NoDrop()
    {
        // жертва моложе (число больше) — по правилам диаблери не снижает поколение
        var r = VampireDiablerieResolver.Resolve(
            attackerGeneration: 8,
            victimGeneration: 11,
            attackerHumanity: 7,
            success: true);

        Assert.Equal(0, r.GenerationDrop);
        Assert.Equal(8, r.AttackerGenerationAfter);
    }

    [Fact]
    public void Resolve_Success_GenerationFloorAt3()
    {
        // Атакующий и так на нижнем кэпе поколений — не должно уйти ниже 3
        var r = VampireDiablerieResolver.Resolve(
            attackerGeneration: 3,
            victimGeneration: 8,
            attackerHumanity: 7,
            success: true);

        Assert.Equal(3, r.AttackerGenerationAfter);
    }

    // ─── Эйфория и Человечность ─────────────────────────────────────────

    [Fact]
    public void Resolve_EuphoriaDifficulty_10MinusHumanity_ClampedAt2()
    {
        var r = VampireDiablerieResolver.Resolve(13, 12, attackerHumanity: 8, success: true);
        Assert.Equal(2, r.EuphoriaDifficulty); // max(2, 10-8)

        var r2 = VampireDiablerieResolver.Resolve(13, 12, attackerHumanity: 7, success: true);
        Assert.Equal(3, r2.EuphoriaDifficulty);

        var r3 = VampireDiablerieResolver.Resolve(13, 12, attackerHumanity: 10, success: true);
        Assert.Equal(2, r3.EuphoriaDifficulty); // max(2, 0) = 2
    }

    [Fact]
    public void Resolve_HumanityLossFlat_AlwaysOne_OnSuccess()
    {
        var r = VampireDiablerieResolver.Resolve(13, 12, attackerHumanity: 7, success: true);
        Assert.Equal(1, r.HumanityLossFlat);
    }

    // ─── Чёрные полосы в ауре ───────────────────────────────────────────

    [Fact]
    public void Resolve_AuraStainsYears_FloorAtOne()
    {
        // атакующий старше жертвы (gen меньше) — разница может быть отрицательной
        var r = VampireDiablerieResolver.Resolve(
            attackerGeneration: 8,
            victimGeneration: 12,
            attackerHumanity: 7,
            success: true);

        Assert.Equal(1, r.AuraStainsYears); // Math.Max(1, ...)
    }

    [Fact]
    public void Resolve_AuraStainsYears_EqualsGenerationGap()
    {
        var r = VampireDiablerieResolver.Resolve(
            attackerGeneration: 13,
            victimGeneration: 12,
            attackerHumanity: 7,
            success: true);

        // newGen = 12, victimGen = 12 → разница 0 → кэп 1
        Assert.Equal(1, r.AuraStainsYears);

        var r2 = VampireDiablerieResolver.Resolve(
            attackerGeneration: 12,
            victimGeneration: 7,
            attackerHumanity: 7,
            success: true);

        // newGen = 9, victimGen = 7 → разница 2
        Assert.Equal(2, r2.AuraStainsYears);
    }

    // ─── Провал ─────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_Failure_NoEffects()
    {
        var r = VampireDiablerieResolver.Resolve(
            attackerGeneration: 13,
            victimGeneration: 12,
            attackerHumanity: 7,
            success: false);

        Assert.False(r.Success);
        Assert.Equal(0, r.GenerationDrop);
        Assert.Equal(0, r.HumanityLossFlat);
        Assert.Equal(0, r.EuphoriaDifficulty);
        Assert.Equal(0, r.AuraStainsYears);
        Assert.Equal(13, r.AttackerGenerationAfter);
        Assert.NotEmpty(r.Notes);
    }

    [Fact]
    public void Resolve_Success_NotesContainKeyPoints()
    {
        var r = VampireDiablerieResolver.Resolve(
            attackerGeneration: 12,
            victimGeneration: 8,
            attackerHumanity: 7,
            success: true);

        Assert.Contains(r.Notes, n => n.Contains("сложность любых атак", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Notes, n => n.Contains("Понижение поколения", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Notes, n => n.Contains("Чёрные полосы", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Notes, n => n.Contains("эйфория", StringComparison.OrdinalIgnoreCase));
    }

    // ─── Валидация ──────────────────────────────────────────────────────

    [Fact]
    public void Resolve_RejectsOutOfRangeGeneration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VampireDiablerieResolver.Resolve(
                attackerGeneration: 2,
                victimGeneration: 12,
                attackerHumanity: 7,
                success: true));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VampireDiablerieResolver.Resolve(
                attackerGeneration: 13,
                victimGeneration: 16,
                attackerHumanity: 7,
                success: true));
    }

    [Fact]
    public void Resolve_RejectsOutOfRangeHumanity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VampireDiablerieResolver.Resolve(13, 12, attackerHumanity: 0, success: true));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VampireDiablerieResolver.Resolve(13, 12, attackerHumanity: 11, success: true));
    }
}
