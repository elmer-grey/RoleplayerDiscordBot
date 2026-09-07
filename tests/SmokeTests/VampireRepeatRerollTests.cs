using System;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты для <see cref="VampireRepeatReroll"/> — повторный бросок по правилам VtM V20
/// (стр. 286 со ссылкой на 267): пул уменьшается на 1 кубик, берётся новый результат
/// (а не лучший из двух).
/// </summary>
public class VampireRepeatRerollTests
{
    private static IRandom Rng(int seed) => new RPBot.VtM.SeededRandom(seed);

    // ─── ComputeRepeatPoolSize ────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(5, 4)]
    [InlineData(13, 12)]
    public void ComputeRepeatPoolSize_Normal_ReturnsOriginalMinusOne(int original, int expected)
    {
        Assert.Equal(expected, VampireRepeatReroll.ComputeRepeatPoolSize(original));
    }

    [Fact]
    public void ComputeRepeatPoolSize_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VampireRepeatReroll.ComputeRepeatPoolSize(-1));
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

    // ─── RollRepeat ────────────────────────────────────────────────────────

    [Fact]
    public void RollRepeat_Pool5_ProducesFourDice()
    {
        var repeat = VampireRepeatReroll.RollRepeat(5, Rng(123));

        Assert.Equal(5, repeat.OriginalPoolSize);
        Assert.Equal(4, repeat.RepeatPoolSize);
        Assert.Equal(4, repeat.RepeatDice.Length);
    }

    [Fact]
    public void RollRepeat_Pool2_ProducesOneDie()
    {
        var repeat = VampireRepeatReroll.RollRepeat(2, Rng(42));

        Assert.Equal(2, repeat.OriginalPoolSize);
        Assert.Equal(1, repeat.RepeatPoolSize);
        Assert.Single(repeat.RepeatDice);
    }

    [Fact]
    public void RollRepeat_AllDiceInRange_1Through10()
    {
        var repeat = VampireRepeatReroll.RollRepeat(13, Rng(2024));

        Assert.Equal(12, repeat.RepeatPoolSize);
        Assert.Equal(12, repeat.RepeatDice.Length);
        foreach (var d in repeat.RepeatDice)
        {
            Assert.InRange(d, 1, 10);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RollRepeat_PoolBelowMin_Throws(int pool)
    {
        Assert.Throws<ArgumentException>(
            () => VampireRepeatReroll.RollRepeat(pool, Rng(1)));
    }

    [Fact]
    public void RollRepeat_NullRng_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => VampireRepeatReroll.RollRepeat(5, null!));
    }

    // ─── Repeat.Successes / IsBotch ────────────────────────────────────────

    [Fact]
    public void Repeat_Successes_CountsValuesAtOrAboveThreshold()
    {
        // Порог V20 = 6 (по VampireDicePool.SuccessThreshold).
        var repeat = new VampireRepeatReroll.Repeat(
            OriginalPoolSize: 5,
            RepeatPoolSize: 4,
            RepeatDice: new[] { 1, 5, 6, 10 });

        Assert.Equal(2, repeat.Successes); // 6 и 10
        Assert.False(repeat.IsBotch);
    }

    [Fact]
    public void Repeat_IsBotch_TrueWhenZeroSuccessesAndAtLeastOneOne()
    {
        var repeat = new VampireRepeatReroll.Repeat(
            OriginalPoolSize: 5,
            RepeatPoolSize: 3,
            RepeatDice: new[] { 1, 2, 5 });

        Assert.Equal(0, repeat.Successes);
        Assert.True(repeat.IsBotch);
    }

    [Fact]
    public void Repeat_IsBotch_FalseWhenNoOnesEvenIfNoSuccesses()
    {
        var repeat = new VampireRepeatReroll.Repeat(
            OriginalPoolSize: 4,
            RepeatPoolSize: 3,
            RepeatDice: new[] { 2, 3, 5 });

        Assert.Equal(0, repeat.Successes);
        Assert.False(repeat.IsBotch);
    }

    [Fact]
    public void RollRepeat_WithSeededRng_IsDeterministic()
    {
        var a = VampireRepeatReroll.RollRepeat(10, Rng(7777));
        var b = VampireRepeatReroll.RollRepeat(10, Rng(7777));

        Assert.Equal(a.RepeatDice, b.RepeatDice);
        Assert.Equal(a.Successes, b.Successes);
    }
}
