using System;
using System.Collections.Generic;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

public class VampireCombatDamageResolverTests
{
    // ─── ComputePool — базовая формула ────────────────────────────────

    [Fact]
    public void ComputePool_BasePlusStrength_NoNetSuccesses()
    {
        var p = VampireCombatDamageResolver.ComputePool(
            baseManeuver: 1,
            attackerStrength: 3,
            attackerSuccesses: 5,
            defenderSuccesses: 5);

        Assert.Equal(1, p.BaseManeuver);
        Assert.Equal(3, p.AttackerStrength);
        Assert.Equal(0, p.NetAttackSuccesses);
        Assert.Equal(4, p.DamagePoolSize); // 1+3+0
        Assert.Equal(4, p.RawTotal);
    }

    [Fact]
    public void ComputePool_NetSuccessesAddToPool()
    {
        var p = VampireCombatDamageResolver.ComputePool(
            baseManeuver: 1,
            attackerStrength: 3,
            attackerSuccesses: 7,
            defenderSuccesses: 4);

        Assert.Equal(3, p.NetAttackSuccesses);
        Assert.Equal(7, p.DamagePoolSize); // 1+3+3
    }

    [Fact]
    public void ComputePool_DefenseExceedsAttack_NoBonusButPoolStillBuilt()
    {
        // По согласованию: превышение защиты не уменьшает пул урона ниже база+Сила.
        var p = VampireCombatDamageResolver.ComputePool(
            baseManeuver: 1,
            attackerStrength: 3,
            attackerSuccesses: 2,
            defenderSuccesses: 8);

        Assert.Equal(-6, p.NetAttackSuccesses);
        Assert.Equal(4, p.DamagePoolSize); // 1+3+0
    }

    [Fact]
    public void ComputePool_PoolIsCappedAtMaxDamagePool()
    {
        var p = VampireCombatDamageResolver.ComputePool(
            baseManeuver: 10,
            attackerStrength: 5,
            attackerSuccesses: 20,
            defenderSuccesses: 0);

        Assert.Equal(VampireCombatDamageResolver.MaxDamagePool, p.DamagePoolSize);
        Assert.True(p.RawTotal > VampireCombatDamageResolver.MaxDamagePool);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ComputePool_AcceptsValidStrength(int strength)
    {
        var p = VampireCombatDamageResolver.ComputePool(1, strength, 1, 0);
        Assert.Equal(strength, p.AttackerStrength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void ComputePool_RejectsOutOfRangeStrength(int strength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VampireCombatDamageResolver.ComputePool(1, strength, 1, 0));
    }

    [Fact]
    public void ComputePool_RejectsNegativeBase()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VampireCombatDamageResolver.ComputePool(-1, 3, 1, 0));
    }

    [Fact]
    public void ComputePool_RejectsNegativeSuccesses()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VampireCombatDamageResolver.ComputePool(1, 3, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VampireCombatDamageResolver.ComputePool(1, 3, 0, -1));
    }

    // ─── CountDamageSuccesses — оценка кубов ─────────────────────────

    [Fact]
    public void CountDamageSuccesses_AllSuccesses()
    {
        var dice = new[] { 6, 7, 8, 9, 10 };
        Assert.Equal(5, VampireCombatDamageResolver.CountDamageSuccesses(dice));
    }

    [Fact]
    public void CountDamageSuccesses_NoSuccesses()
    {
        var dice = new[] { 1, 2, 3, 4, 5 };
        Assert.Equal(0, VampireCombatDamageResolver.CountDamageSuccesses(dice));
    }

    [Fact]
    public void CountDamageSuccesses_ThresholdIs6()
    {
        var dice = new[] { 5, 6 };
        Assert.Equal(1, VampireCombatDamageResolver.CountDamageSuccesses(dice));
    }

    [Fact]
    public void CountDamageSuccesses_RejectsEmpty()
    {
        Assert.Throws<ArgumentException>(
            () => VampireCombatDamageResolver.CountDamageSuccesses(new int[0]));
    }

    [Fact]
    public void CountDamageSuccesses_RejectsOutOfRangeDie()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VampireCombatDamageResolver.CountDamageSuccesses(new[] { 1, 11 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VampireCombatDamageResolver.CountDamageSuccesses(new[] { 0, 6 }));
    }

    // ─── BuildHelpLines ──────────────────────────────────────────────

    [Fact]
    public void BuildHelpLines_ContainsFormulaSteps()
    {
        var lines = VampireCombatDamageResolver.BuildHelpLines();
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("Атака", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, l => l.Contains("Защита", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, l => l.Contains("Пул урона", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, l => l.Contains("сложность 6", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, l => l.Contains("повреждение", StringComparison.OrdinalIgnoreCase));
    }
}
