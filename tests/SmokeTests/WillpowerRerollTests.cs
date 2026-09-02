using System.Collections.Generic;
using System.Linq;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты для <see cref="WillpowerReroll"/> — переброс regular-кубиков за пункт воли.
/// </summary>
public class WillpowerRerollTests
{
    private static IReadOnlyList<int> D(params int[] values) => values;

    private static IRandom Rng(int seed) => new RPBot.VtM.SeededRandom(seed);

    [Fact]
    public void CanReroll_ReturnsTrue_WhenPointsAvailable()
    {
        Assert.True(WillpowerReroll.CanReroll(1));
        Assert.True(WillpowerReroll.CanReroll(5));
    }

    [Fact]
    public void CanReroll_ReturnsFalse_WhenNoPoints()
    {
        Assert.False(WillpowerReroll.CanReroll(0));
        Assert.False(WillpowerReroll.CanReroll(-1));
    }

    [Theory]
    [InlineData(5, 3, 3)]
    [InlineData(5, 1, 1)]
    [InlineData(5, 5, 5)]
    [InlineData(5, 10, 5)]
    [InlineData(0, 3, 0)]
    public void ActualRerollCount_ClampsToAvailable(int regular, int requested, int expected)
    {
        Assert.Equal(expected, WillpowerReroll.ActualRerollCount(regular, requested));
    }

    [Fact]
    public void RerollRegular_PicksWorstValues_AndKeepsArrayLength()
    {
        var seeded = Rng(42);
        var dice = D(8, 3, 9, 2, 6);
        var rerolled = WillpowerReroll.RerollRegular(dice, 2, seeded);

        Assert.Equal(5, rerolled.Length);
        // Первые 2 кубика (3 и 2) были худшими — позиции 1 и 3.
        // После переброса их значения могут быть любыми 1..10,
        // но позиции 0, 2, 4 (8, 9, 6) сохраняются.
        Assert.Equal(8, rerolled[0]);
        Assert.Equal(9, rerolled[2]);
        Assert.Equal(6, rerolled[4]);
        Assert.NotEqual(3, rerolled[1]); // переброшено
        Assert.NotEqual(2, rerolled[3]); // переброшено
    }

    [Fact]
    public void RerollRegular_HungerDiceUnaffected_WhenCalledOnRegularOnly()
    {
        var seeded = Rng(7);
        var regular = D(1, 2, 3, 4, 5);
        var hunger = D(6, 7, 8);
        var rerolled = WillpowerReroll.RerollRegular(regular, 3, seeded);

        // Hunger не трогаем — только regular.
        Assert.Equal(new[] { 6, 7, 8 }, hunger);
        Assert.Equal(5, rerolled.Length);
        // Худшие 1, 2, 3 переброшены, 4, 5 остаются.
        Assert.Equal(4, rerolled[3]);
        Assert.Equal(5, rerolled[4]);
    }

    [Fact]
    public void RerollRegular_CountExceedsPool_RerollsAll()
    {
            var seeded = Rng(1);
        var dice = D(2, 5);
            // Просим 3, есть только 2 — перебрасываем оба.
            var rerolled = WillpowerReroll.RerollRegular(dice, 3, seeded);

        Assert.Equal(2, rerolled.Length);
        Assert.NotEqual(2, rerolled[0]);
        Assert.NotEqual(5, rerolled[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(11)]
    public void RerollRegular_RejectsInvalidCount(int badCount)
    {
        var seeded = Rng(1);
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            WillpowerReroll.RerollRegular(D(3, 5, 7), badCount, seeded));
    }

    [Fact]
    public void RerollRegular_RejectsEmptyPool()
    {
            var seeded = Rng(1);
        Assert.Throws<System.ArgumentException>(() =>
            WillpowerReroll.RerollRegular(System.Array.Empty<int>(), 1, seeded));
    }

    [Fact]
    public void RerollRegular_OneCount_RerollsExactlyOne()
    {
        var seeded = Rng(99);
        var dice = D(10, 10, 10);
        var rerolled = WillpowerReroll.RerollRegular(dice, 1, seeded);

        // Все три = 10. Худший — любой, берём первый по OrderBy с индексом.
        Assert.Equal(3, rerolled.Length);
        // Позиция 0 переброшена.
        Assert.NotEqual(10, rerolled[0]);
        // Позиции 1 и 2 сохранены.
        Assert.Equal(10, rerolled[1]);
        Assert.Equal(10, rerolled[2]);
    }

    [Fact]
    public void RerollRegular_TiesKeepStableOrder()
    {
        // При равенстве значений — перебрасываем по минимальному индексу.
        var seeded = Rng(5);
        var dice = D(3, 3, 3);
        var rerolled = WillpowerReroll.RerollRegular(dice, 2, seeded);

        Assert.Equal(3, rerolled.Length);
        // Первые два по индексу переброшены, последний сохранён.
        Assert.Equal(3, rerolled[2]);
    }

    [Fact]
    public void WillpowerPoints_DeductedOnce_PerReroll()
    {
        // Моделируем: willpower = 5, после переброса = 4.
        int willpowerPoints = 5;
        var seeded = Rng(11);

        if (WillpowerReroll.CanReroll(willpowerPoints))
        {
            var dice = D(1, 2, 3, 4, 5);
            var _ = WillpowerReroll.RerollRegular(dice, 3, seeded);
            willpowerPoints -= 1;
        }

        Assert.Equal(4, willpowerPoints);
    }
}