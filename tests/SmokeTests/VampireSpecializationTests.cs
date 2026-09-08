using System.Collections.Generic;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты на гибридный подсчёт успехов V20+V5:
///   • специализация действует ТОЛЬКО на regular-кубы (десятки ×2);
///   • hunger-кубы считаются обычным образом без специализации.
/// </summary>
public class VampireSpecializationTests
{
    private static int[] A(params int[] xs) => xs;

    // ---------- CountSuccessesFor (один пул, V20): legacy-проверки ----------

    [Fact]
    public void CountSuccessesFor_NoSpecialization_TensCountAsOne()
    {
        var dice = A(6, 7, 8, 9, 10);
        Assert.Equal(5, VampireDicePool.CountSuccessesFor(dice, specialization: null));
    }

    [Fact]
    public void CountSuccessesFor_WithSpecialization_TensDoubled()
    {
        var dice = A(6, 7, 8, 9, 10, 10);
        Assert.Equal(6, VampireDicePool.CountSuccessesFor(dice, null));
        Assert.Equal(8, VampireDicePool.CountSuccessesFor(dice, "тени"));
    }

    [Fact]
    public void CountSuccessesFor_SpecializationOnlyMattersForTens()
    {
        var dice = A(6, 7, 8, 9);
        Assert.Equal(
            VampireDicePool.CountSuccessesFor(dice, null),
            VampireDicePool.CountSuccessesFor(dice, "тени"));
    }

    [Fact]
    public void CountSuccessesFor_EmptySpecialization_TreatedAsNoSpec()
    {
        var dice = A(6, 7, 10);
        int noSpec = VampireDicePool.CountSuccessesFor(dice, "");
        int nullSpec = VampireDicePool.CountSuccessesFor(dice, null);
        int whitespaceSpec = VampireDicePool.CountSuccessesFor(dice, "   ");
        Assert.Equal(noSpec, nullSpec);
        Assert.Equal(noSpec, whitespaceSpec);
        Assert.Equal(3, noSpec);
    }

    [Fact]
    public void CountSuccessesFor_NoSuccesses_ReturnsZero()
    {
        var dice = A(1, 2, 3, 4, 5);
        Assert.Equal(0, VampireDicePool.CountSuccessesFor(dice, null));
        Assert.Equal(0, VampireDicePool.CountSuccessesFor(dice, "тени"));
    }

    [Fact]
    public void CountSuccessesFor_OnlyOnes_NotAffectedBySpecialization()
    {
        var dice = A(1, 1, 1);
        Assert.Equal(0, VampireDicePool.CountSuccessesFor(dice, "тени"));
    }

    [Fact]
    public void CountSuccessesFor_NullDice_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => VampireDicePool.CountSuccessesFor(null!, "тени"));
    }

    [Fact]
    public void CountSuccessesFor_FiveDice_OneTen_ShowsDifference()
    {
        var dice = A(6, 7, 8, 9, 10);
        Assert.Equal(5, VampireDicePool.CountSuccessesFor(dice, null));
        Assert.Equal(6, VampireDicePool.CountSuccessesFor(dice, "тени"));
    }

    // ---------- CountSuccessesHybrid (regular + hunger, V20+V5) ----------

    [Fact]
    public void CountSuccessesHybrid_NoSpec_RegularAndHungerSummed()
    {
        // regular: 6, 7, 10 (3); hunger: 8, 9 (2) → 5 успехов.
        var regular = A(6, 7, 10);
        var hunger  = A(8, 9);
        Assert.Equal(5, VampireDicePool.CountSuccessesHybrid(regular, hunger, specialization: null));
    }

    [Fact]
    public void CountSuccessesHybrid_WithSpec_RegularTensDoubled_HungerUntouched()
    {
        // regular: 6, 10, 10 → без спец: 1+1+1 = 3, со спец: 1+2+2 = 5.
        // hunger: 8, 10, 10 → всегда 1+1+1 = 3 (спец. НЕ действует на hunger).
        // Итого: без спец 6, со спец 8.
        var regular = A(6, 10, 10);
        var hunger  = A(8, 10, 10);
        Assert.Equal(6, VampireDicePool.CountSuccessesHybrid(regular, hunger, null));
        Assert.Equal(8, VampireDicePool.CountSuccessesHybrid(regular, hunger, "тени"));
    }

    [Fact]
    public void CountSuccessesHybrid_OnlyHunger_SpecIgnored()
    {
        // regular пуст: голодные 6,7,8,9,10 → 5; спец. не действует.
        var regular = System.Array.Empty<int>();
        var hunger  = A(6, 7, 8, 9, 10);
        Assert.Equal(5, VampireDicePool.CountSuccessesHybrid(regular, hunger, null));
        Assert.Equal(5, VampireDicePool.CountSuccessesHybrid(regular, hunger, "тени"));
    }

    [Fact]
    public void CountSuccessesHybrid_OnlyRegular_SpecDoublesTens()
    {
        var regular = A(6, 7, 10);
        var hunger  = System.Array.Empty<int>();
        Assert.Equal(3, VampireDicePool.CountSuccessesHybrid(regular, hunger, null));
        Assert.Equal(4, VampireDicePool.CountSuccessesHybrid(regular, hunger, "тени"));
    }

    [Fact]
    public void CountSuccessesHybrid_BothEmpty_Zero()
    {
        var empty = System.Array.Empty<int>();
        Assert.Equal(0, VampireDicePool.CountSuccessesHybrid(empty, empty, null));
        Assert.Equal(0, VampireDicePool.CountSuccessesHybrid(empty, empty, "тени"));
    }

    [Fact]
    public void CountSuccessesHybrid_WhitespaceSpec_TreatedAsNoSpec()
    {
        var regular = A(6, 10);
        var hunger  = A(10);
        Assert.Equal(
            VampireDicePool.CountSuccessesHybrid(regular, hunger, null),
            VampireDicePool.CountSuccessesHybrid(regular, hunger, "   "));
    }

    [Fact]
    public void CountSuccessesHybrid_NullArguments_Throw()
    {
        var empty = System.Array.Empty<int>();
        Assert.Throws<System.ArgumentNullException>(
            () => VampireDicePool.CountSuccessesHybrid(null!, empty, "тени"));
        Assert.Throws<System.ArgumentNullException>(
            () => VampireDicePool.CountSuccessesHybrid(empty, null!, "тени"));
    }

    [Fact]
    public void CountSuccessesHybrid_OnesDoNotContribute()
    {
        // regular: 1, 6, 10 → V5: 1-ка съедает один успех.
        //   без спец: 1+1 = 2 успеха, минус 1 за 1-ку → 1.
        //   со спец: 1+2 = 3 успеха, минус 1 за 1-ку → 2.
        // hunger: 1, 1 — голодные 1-цы НЕ съедают успехи → 0.
        // Итого: без спец 1, со спец 2.
        var regular = A(1, 6, 10);
        var hunger  = A(1, 1);
        Assert.Equal(1, VampireDicePool.CountSuccessesHybrid(regular, hunger, null));
        Assert.Equal(2, VampireDicePool.CountSuccessesHybrid(regular, hunger, "тени"));
    }

    [Fact]
    public void CountSuccessesHybrid_RegularOnesEatSuccesses()
    {
        // regular: 6, 7, 8 — три успеха, две 1-ки съедают два → 1 успех.
        // hunger: 10 — один успех.
        // Итого: 2.
        var regular = A(1, 1, 6, 7, 8);
        var hunger  = A(10);
        Assert.Equal(2, VampireDicePool.CountSuccessesHybrid(regular, hunger, null));
        Assert.Equal(2, VampireDicePool.CountSuccessesHybrid(regular, hunger, "тени"));
    }
}
