using System;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты для <see cref="VampireRepeatReroll"/> — повторная попытка по правилам VtM V20
/// (стр. 286): пул НЕ меняется, сложность возрастает на 1, берётся новый результат
/// (а не лучший из двух).
/// </summary>
public class VampireRepeatRerollTests
{
    private static IRandom Rng(int seed) => new RPBot.VtM.SeededRandom(seed);

    // ─── ComputeRepeatDifficulty (+1) ────────────────────────────────────

    [Theory]
    [InlineData(2, 3)]
    [InlineData(5, 6)]
    [InlineData(6, 7)]
    [InlineData(8, 9)]
    [InlineData(10, 11)]
    public void ComputeRepeatDifficulty_ReturnsOriginalPlusOne(int original, int expected)
    {
        Assert.Equal(expected, VampireRepeatReroll.ComputeRepeatDifficulty(original));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ComputeRepeatDifficulty_BelowMin_Throws(int original)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VampireRepeatReroll.ComputeRepeatDifficulty(original));
    }

    // ─── CanRepeat ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(13, true)]
    public void CanRepeat_ReturnsTrueOnlyForPoolsAtLeastOne(int original, bool expected)
    {
        Assert.Equal(expected, VampireRepeatReroll.CanRepeat(original));
    }

    // ─── RollRepeat (пул не меняется, сложность +1) ────────────────────────

    [Fact]
    public void RollRepeat_Pool5_ProducesFiveDice()
    {
        // V20 стр. 286: повтор = тот же пул (5), не N-1.
        var repeat = VampireRepeatReroll.RollRepeat(
            originalPoolSize: 5,
            originalDifficulty: 6,
            regularCount: 5,
            hungerCount: 0,
            rng: Rng(123));

        Assert.Equal(5, repeat.OriginalPoolSize);
        Assert.Equal(5, repeat.NewRegularDice.Length + repeat.NewHungerDice.Length);
        Assert.Equal(5, repeat.NewRegularDice.Length);
        Assert.Equal(0, repeat.NewHungerDice.Length);
    }

    [Fact]
    public void RollRepeat_Pool2_ProducesTwoDice()
    {
        var repeat = VampireRepeatReroll.RollRepeat(
            originalPoolSize: 2,
            originalDifficulty: 6,
            regularCount: 2,
            hungerCount: 0,
            rng: Rng(42));

        Assert.Equal(2, repeat.OriginalPoolSize);
        Assert.Equal(2, repeat.NewRegularDice.Length);
    }

    [Fact]
    public void RollRepeat_DifficultyIncreasesByOne()
    {
        var repeat = VampireRepeatReroll.RollRepeat(
            originalPoolSize: 5,
            originalDifficulty: 6,
            regularCount: 5,
            hungerCount: 0,
            rng: Rng(1));

        Assert.Equal(6, repeat.OriginalDifficulty);
        Assert.Equal(7, repeat.NewDifficulty);
    }

    [Fact]
    public void RollRepeat_AllDiceInRange_1Through10()
    {
        var repeat = VampireRepeatReroll.RollRepeat(
            originalPoolSize: 13,
            originalDifficulty: 6,
            regularCount: 10,
            hungerCount: 3,
            rng: Rng(2024));

        Assert.Equal(10, repeat.NewRegularDice.Length);
        Assert.Equal(3, repeat.NewHungerDice.Length);
        foreach (var d in repeat.NewRegularDice) Assert.InRange(d, 1, 10);
        foreach (var d in repeat.NewHungerDice) Assert.InRange(d, 1, 10);
    }

    [Fact]
    public void RollRepeat_SplitHungerRegular_ProducesBoth()
    {
        // Гибрид V20+V5: regular 3, hunger 2.
        var repeat = VampireRepeatReroll.RollRepeat(
            originalPoolSize: 5,
            originalDifficulty: 6,
            regularCount: 3,
            hungerCount: 2,
            rng: Rng(7));

        Assert.Equal(3, repeat.NewRegularDice.Length);
        Assert.Equal(2, repeat.NewHungerDice.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RollRepeat_PoolBelowMin_Throws(int pool)
    {
        Assert.Throws<ArgumentException>(
            () => VampireRepeatReroll.RollRepeat(
                originalPoolSize: pool,
                originalDifficulty: 6,
                regularCount: 1,
                hungerCount: 0,
                rng: Rng(1)));
    }

    [Fact]
    public void RollRepeat_PoolSplitMismatch_Throws()
    {
        // regular + hunger ≠ originalPoolSize — ошибка согласованности.
        Assert.Throws<ArgumentException>(
            () => VampireRepeatReroll.RollRepeat(
                originalPoolSize: 5,
                originalDifficulty: 6,
                regularCount: 3,
                hungerCount: 1,
                rng: Rng(1)));
    }

    [Fact]
    public void RollRepeat_NullRng_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => VampireRepeatReroll.RollRepeat(
                originalPoolSize: 5,
                originalDifficulty: 6,
                regularCount: 5,
                hungerCount: 0,
                rng: null!));
    }

    // ─── Repeat.Successes / IsBotch ────────────────────────────────────────

    [Fact]
    public void Repeat_Successes_CountsHybrid()
    {
        // regular 6, 10, hunger 8 → 2 успеха (1 съедает один 6-9 из 1,
        // остаётся 0+1=1 в regular, +1 hunger = 2).
        var repeat = new VampireRepeatReroll.Repeat(
            OriginalPoolSize: 5,
            OriginalDifficulty: 6,
            NewDifficulty: 7,
            NewRegularDice: new[] { 1, 5, 6, 10 },
            NewHungerDice: new[] { 8 });

        Assert.Equal(2, repeat.Successes);
        Assert.False(repeat.IsBotch);
    }

    [Fact]
    public void Repeat_IsBotch_TrueWhenZeroSuccessesAndAtLeastOneOne()
    {
        var repeat = new VampireRepeatReroll.Repeat(
            OriginalPoolSize: 5,
            OriginalDifficulty: 6,
            NewDifficulty: 7,
            NewRegularDice: new[] { 1, 2, 5 },
            NewHungerDice: Array.Empty<int>());

        Assert.Equal(0, repeat.Successes);
        Assert.True(repeat.IsBotch);
    }

    [Fact]
    public void Repeat_IsBotch_FalseWhenNoOnesEvenIfNoSuccesses()
    {
        var repeat = new VampireRepeatReroll.Repeat(
            OriginalPoolSize: 4,
            OriginalDifficulty: 6,
            NewDifficulty: 7,
            NewRegularDice: new[] { 2, 3, 5 },
            NewHungerDice: Array.Empty<int>());

        Assert.Equal(0, repeat.Successes);
        Assert.False(repeat.IsBotch);
    }

    [Fact]
    public void RollRepeat_WithSeededRng_IsDeterministic()
    {
        var a = VampireRepeatReroll.RollRepeat(10, 6, 10, 0, Rng(7777));
        var b = VampireRepeatReroll.RollRepeat(10, 6, 10, 0, Rng(7777));

        Assert.Equal(a.NewRegularDice, b.NewRegularDice);
        Assert.Equal(a.NewHungerDice, b.NewHungerDice);
        Assert.Equal(a.Successes, b.Successes);
    }
}
