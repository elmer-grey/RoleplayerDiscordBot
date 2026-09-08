using System;
using System.Collections.Generic;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

public class VampireCombatDamageResolverTests
{
    // ─── ComputePool — формула V20 стр. 301 ───────────────────────

    [Fact]
    public void ComputePool_FirstSuccessAddsNothing()
    {
        var p = VampireCombatDamageResolver.ComputePool(
            baseManeuver: 4,    // напр. револьвер .38
            attackerSuccesses: 1); // первый успех — без надбавки

        Assert.Equal(0, p.ExcessSuccesses);
        Assert.Equal(4, p.DamagePoolSize);
    }

    [Fact]
    public void ComputePool_EachExtraSuccessAddsOne()
    {
        // Револьвер .38, база 4. 4 успеха → +3d10 (1-й бесплатный).
        var p = VampireCombatDamageResolver.ComputePool(baseManeuver: 4, attackerSuccesses: 4);

        Assert.Equal(3, p.ExcessSuccesses);
        Assert.Equal(7, p.DamagePoolSize);
    }

    [Fact]
    public void ComputePool_PoolNeverBelowBase()
    {
        // Даже при провале — попадание есть попадание, минимум = база.
        var p = VampireCombatDamageResolver.ComputePool(baseManeuver: 5, attackerSuccesses: 1);

        Assert.Equal(5, p.DamagePoolSize);
    }

    [Fact]
    public void ComputePool_StrengthAlreadyInBase()
    {
        // Нож = «Сила + 1». Если Сила=3, база = 4, не 3+1+Сила.
        var p = VampireCombatDamageResolver.ComputePool(baseManeuver: 4, attackerSuccesses: 3);

        Assert.Equal(2, p.ExcessSuccesses);
        Assert.Equal(6, p.DamagePoolSize);
    }

    [Fact]
    public void ComputePool_RejectsNegativeBase()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VampireCombatDamageResolver.ComputePool(-1, 1));
    }

    [Fact]
    public void ComputePool_RejectsNegativeSuccesses()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VampireCombatDamageResolver.ComputePool(1, -1));
    }

    [Fact]
    public void ComputePool_PoolIsCappedAtMaxDamagePool()
    {
        var p = VampireCombatDamageResolver.ComputePool(baseManeuver: 25, attackerSuccesses: 30);

        Assert.Equal(VampireCombatDamageResolver.MaxDamagePool, p.DamagePoolSize);
        Assert.True(p.RawTotal > VampireCombatDamageResolver.MaxDamagePool);
    }

    [Fact]
    public void ComputePool_ZeroBaseStillValid()
    {
        var p = VampireCombatDamageResolver.ComputePool(baseManeuver: 0, attackerSuccesses: 3);

        Assert.Equal(2, p.ExcessSuccesses);
        Assert.Equal(2, p.DamagePoolSize);
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
        var joined = string.Join("\n", lines);
        // Формула пула.
        Assert.Contains("pool", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Пул урона", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("сложность 6", joined, StringComparison.OrdinalIgnoreCase);
        // Все ключевые манёвры должны упоминаться.
        Assert.Contains("Укус", joined, StringComparison.Ordinal);
        Assert.Contains("Парирование", joined, StringComparison.Ordinal);
        Assert.Contains("Длинная очередь", joined, StringComparison.Ordinal);
        // Типы повреждений и оружие.
        Assert.Contains("губительные", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Револьвер", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("брони", joined, StringComparison.OrdinalIgnoreCase);
    }
}
