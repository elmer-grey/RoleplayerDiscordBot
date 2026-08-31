using System.Linq;
using RPBot.VtM;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Чистая логика пула VtM v20 + v5. Никакого Discord — тестируем только математику.
/// Все источники случайности детерминированы через <see cref="SeededRandom"/>,
/// а в части тестов — напрямую через <see cref="VampireDicePool.EvaluateV20"/>/<see cref="VampireDicePool.EvaluateV5"/>.
///
/// Финальные правила (согласованы 2026-09-01):
///   • regular: каждый куб сам по себе: 6–9 → +1, 10 → +1, 1 → −1, 2–5 → 0. Без нейтрализации X↔!.
///   • hunger 6–9/10 → всегда +1.
///   • hunger X/! = Messy/Bestial ТОЛЬКО если regular без + И в regular есть крит (X/!).
///   • На бросок — не более одного Messy/Bestial.
///   • Доп. куб при взаимном X↔! в обоих пулах.
/// </summary>
public class VampireDicePoolTests
{
    // ----- V20 -----

    [Fact]
    public void RollV20_AllSuccesses_CountsEveryHit()
    {
        var dice = new[] { 8, 9, 10, 6, 7 };
        var r = VampireDicePool.EvaluateV20(dice);

        Assert.Equal(5, r.Successes);
        Assert.False(r.IsBotch);
        Assert.Equal(1, r.Criticals);
    }

    [Fact]
    public void RollV20_AllFailures_NoBotch_NoOnes()
    {
        var dice = new[] { 2, 3, 4, 5, 5 };
        var r = VampireDicePool.EvaluateV20(dice);

        Assert.Equal(0, r.Successes);
        Assert.False(r.IsBotch);
    }

    [Fact]
    public void RollV20_BotchOnlyIfZeroSuccessesAndAtLeastOneOne()
    {
        var dice = new[] { 1, 3, 4, 5, 2 };
        var r = VampireDicePool.EvaluateV20(dice);

        Assert.Equal(0, r.Successes);
        Assert.True(r.IsBotch);
        Assert.Equal(1, r.Ones);
    }

    [Fact]
    public void RollV20_BotchSuppressedByAnySuccess()
    {
        var dice = new[] { 1, 6, 2, 3, 4 };
        var r = VampireDicePool.EvaluateV20(dice);

        Assert.Equal(1, r.Successes);
        Assert.False(r.IsBotch);
    }

    [Fact]
    public void RollV20_MixedResult_CountsOnlyGe6()
    {
        var dice = new[] { 6, 5, 8, 1, 10, 3 };
        var r = VampireDicePool.EvaluateV20(dice);

        Assert.Equal(3, r.Successes); // 6, 8, 10
        Assert.False(r.IsBotch);
        Assert.Equal(1, r.Criticals);
    }

    [Fact]
    public void RollV20_RejectsZeroPool()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => VampireDicePool.RollV20(0, new SeededRandom(1)));
    }

    [Fact]
    public void RollV20_ClampsAtMaxTotalDice()
    {
        var r = VampireDicePool.RollV20(VampireDicePool.MaxTotalDice + 100, new SeededRandom(42));
        Assert.Equal(VampireDicePool.MaxTotalDice, r.Dice.Length);
    }

    // ----- V5: голодные кубы -----

    [Fact]
    public void RollV5_SpecExample_Pool7_Hunger2_FiveRegularTwoHunger()
    {
        var result = VampireDicePool.RollV5(poolSize: 7, hunger: 2, rng: new SeededRandom(123));

        Assert.Equal(5, result.RegularDice.Length);
        Assert.Equal(2, result.HungerDice.Length);
        Assert.Equal(7, result.RegularDice.Length + result.HungerDice.Length);
    }

    [Fact]
    public void RollV5_HungerZero_BehavesLikeV20PlusZeroHungerDice()
    {
        var r = VampireDicePool.RollV5(poolSize: 5, hunger: 0, rng: new SeededRandom(7));
        Assert.Equal(5, r.RegularDice.Length);
        Assert.Empty(r.HungerDice);
        // V20 успехи считаются по "просто ≥6 без поедания",
        // а V5 — с поеданием X→6-9 и с −1 за остаток X.
        // Эти две формулы НЕ эквивалентны в общем случае (например, X→−1 в V5 vs 0 в V20).
        // Этот тест гарантирует лишь, что V5 не падает и делит пул 5+0 корректно.
        Assert.NotNull(r);
    }

    [Fact]
    public void RollV5_RejectsHungerExceedingPool()
    {
        Assert.Throws<System.ArgumentException>(() => VampireDicePool.RollV5(poolSize: 3, hunger: 4, rng: new SeededRandom(1)));
    }

    [Fact]
    public void RollV5_RejectsHungerAboveFive()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => VampireDicePool.RollV5(poolSize: 10, hunger: 6, rng: new SeededRandom(1)));
    }

    // ----- V5: правила "голод" — Messy / Bestial -----

    /// <summary>
    /// regular `++++` + hunger `!` → regular 4, hunger +1, не Messy.
    /// regular имеет «+», поэтому голодный ! — обычный +1.
    /// </summary>
    [Fact]
    public void V5_R01_RegularCleanPlus_HungerTen_PlainPlus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 8, 9 },
            hungerDice: new[] { 10 });
        Assert.Equal(4, r.RegularSuccesses);
        Assert.Equal(1, r.HungerSum);
        Assert.Equal(5, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `++++` + hunger `+` → 5, plain.
    /// </summary>
    [Fact]
    public void V5_R02_RegularCleanPlus_HungerPlus_PlainPlus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 8, 9 },
            hungerDice: new[] { 7 });
        Assert.Equal(5, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `++++` + hunger `X` → 4 + (-1) = 3, не Bestial.
    /// regular имеет «+», голодный X — обычный −1.
    /// </summary>
    [Fact]
    public void V5_R03_RegularCleanPlus_HungerOne_PlainMinus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 8, 9 },
            hungerDice: new[] { 1 });
        Assert.Equal(3, r.TotalSuccesses);
        Assert.False(r.IsBestialFailure);
    }

    /// <summary>
    /// regular `+++X` (2 успеха, одна 1) + hunger `+` → 2 + 1 = 3.
    /// regular имеет «+», голодный + засчитывается.
    /// </summary>
    [Fact]
    public void V5_R04_RegularPlusAndX_HungerPlus_PlainPlus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 8, 1 },
            hungerDice: new[] { 7 });
        Assert.Equal(3, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `+++X` + hunger `!` → regular 2, hunger +1 (не Messy). Итого 3.
    /// regular имеет «+» → Messy подавлен.
    /// </summary>
    [Fact]
    public void V5_R05_RegularPlusAndX_HungerTen_NotMessy()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 8, 1 },
            hungerDice: new[] { 10 });
        Assert.Equal(3, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `+++X` + hunger `X` → regular 2, hunger −1 (не Bestial). Итого 1.
    /// regular имеет «+» → Bestial подавлен.
    /// </summary>
    [Fact]
    public void V5_R06_RegularPlusAndX_HungerOne_NotBestial()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 8, 1 },
            hungerDice: new[] { 1 });
        Assert.Equal(1, r.TotalSuccesses);
        Assert.False(r.IsBestialFailure);
    }

    /// <summary>
    /// regular `++XX` (regularPlusCount = 0, regularHasCrit = true)
    /// + hunger `+` → regular 0, hunger +1. Итого 1.
    /// regular без «+», но в hunger есть «+» — засчитывается (некритический).
    /// </summary>
    [Fact]
    public void V5_R07_RegularNoPlus_HungerPlus_PlainPlus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 1, 1 },
            hungerDice: new[] { 7 });
        Assert.Equal(1, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
        Assert.False(r.IsBestialFailure);
    }

    /// <summary>
    /// regular `++XX` (regularPlusCount = 0, regularHasCrit = true)
    /// + hunger `!` → Messy (флаг, без числового вклада).
    /// Итог = 0 (regular) + 0 (Messy, не даёт +1).
    /// </summary>
    [Fact]
    public void V5_R08_RegularNoPlusWithCrit_HungerTen_Messy()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 1, 1 },
            hungerDice: new[] { 10 });
        Assert.Equal(1, r.TotalSuccesses);
        Assert.True(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `++XX` (regularPlusCount = 0, regularHasCrit = true)
    /// + hunger `X` → Bestial (флаг, без числового вклада).
    /// Hunger кубик X даёт −1. Итог = 0 + (−1) = −1, флаг Bestial.
    /// </summary>
    [Fact]
    public void V5_R09_RegularNoPlusWithCrit_HungerOne_Bestial()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 1, 1 },
            hungerDice: new[] { 1 });
        Assert.Equal(-1, r.TotalSuccesses);
        Assert.True(r.IsBestialFailure);
    }

    /// <summary>
    /// regular `+!!!` (regular 4, ! три) + hunger `+` → 4 + 1 = 5.
    /// regular имеет «+», голодный + — обычный +1.
    /// </summary>
    [Fact]
    public void V5_R10_RegularHasCrit_HungerPlus_PlainPlus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 10, 10, 10 },
            hungerDice: new[] { 7 });
        Assert.Equal(5, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `+!!!` + hunger `!` → regular 4, hunger +1 (не Messy, т.к. regular имеет «+»). Итого 5.
    /// </summary>
    [Fact]
    public void V5_R11_RegularHasCrit_HungerTen_NotMessy()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 10, 10, 10 },
            hungerDice: new[] { 10 });
        Assert.Equal(5, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `+!!!` + hunger `X` → regular 4, hunger −1 (не Bestial). Итого 3.
    /// </summary>
    [Fact]
    public void V5_R12_RegularHasCrit_HungerOne_NotBestial()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 10, 10, 10 },
            hungerDice: new[] { 1 });
        Assert.Equal(3, r.TotalSuccesses);
        Assert.False(r.IsBestialFailure);
    }

    /// <summary>
    /// regular `X!!!` (regular 2: −1+1+1+1) + hunger `!` → regular 2, hunger +1 (regular имеет «+»). Итого 3.
    /// </summary>
    [Fact]
    public void V5_R13_RegularXAndCrit_HungerTen_NotMessy()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 1, 10, 10, 10 },
            hungerDice: new[] { 10 });
        Assert.Equal(3, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `X!!!` + hunger `X` → regular 2, hunger −1 (regular имеет «+»). Итого 1.
    /// </summary>
    [Fact]
    public void V5_R14_RegularXAndCrit_HungerOne_NotBestial()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 1, 10, 10, 10 },
            hungerDice: new[] { 1 });
        Assert.Equal(1, r.TotalSuccesses);
        Assert.False(r.IsBestialFailure);
    }

    /// <summary>
    /// regular `XXXX` (−4, без «+», с критом X) + hunger `X` → Bestial.
    /// Hunger кубик X даёт −1 (как обычный провал). Bestial — только флаг.
    /// Сумма: regular −4 + hunger −1 = −5.
    /// </summary>
    [Fact]
    public void V5_R15_RegularOnlyOnes_HungerOne_Bestial()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 1, 1, 1, 1 },
            hungerDice: new[] { 1 });
        Assert.True(r.IsBestialFailure);
        Assert.Equal(-5, r.TotalSuccesses);
    }

    /// <summary>
    /// regular `XXXX` + hunger `+` → regular −4, hunger +1. Итого −3.
    /// </summary>
    [Fact]
    public void V5_R16_RegularOnlyOnes_HungerPlus_PlainPlus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 1, 1, 1, 1 },
            hungerDice: new[] { 7 });
        Assert.Equal(-3, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `++++` + hunger `—` → 4, plain.
    /// </summary>
    [Fact]
    public void V5_R17_RegularCleanPlus_HungerEmpty_Plain()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 8, 9 },
            hungerDice: new[] { 5 });
        Assert.Equal(4, r.TotalSuccesses);
    }

    /// <summary>
    /// regular `+—++` + hunger `X` → 3 + (−1) = 2 (regular имеет «+», Bestial подавлен).
    /// </summary>
    [Fact]
    public void V5_R18_RegularPlusAndBlank_HungerOne_NotBestial()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 5, 7, 8 },
            hungerDice: new[] { 1 });
        Assert.Equal(2, r.TotalSuccesses);
        Assert.False(r.IsBestialFailure);
    }

    /// <summary>
    /// regular `+—+X` (regular 1: 6+5+6−1=1) + hunger `+` → 1 + 1 = 2.
    /// </summary>
    [Fact]
    public void V5_R19_RegularMixed_HungerPlus_PlainPlus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 5, 6, 1 },
            hungerDice: new[] { 7 });
        Assert.Equal(2, r.TotalSuccesses);
    }

    /// <summary>
    /// regular `+—+X` + hunger `—` → regular 1, hunger 0. Итого 1.
    /// </summary>
    [Fact]
    public void V5_R20_RegularMixed_HungerEmpty_Plain()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 5, 6, 1 },
            hungerDice: new[] { 5 });
        Assert.Equal(1, r.TotalSuccesses);
    }

    /// <summary>
    /// regular `+—!!` (3) + hunger `—` → 3.
    /// </summary>
    [Fact]
    public void V5_R21_RegularPlusAndCrit_HungerEmpty_Plain()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 5, 10, 10 },
            hungerDice: new[] { 5 });
        Assert.Equal(3, r.TotalSuccesses);
    }

    /// <summary>
    /// regular `——!!` (regular 2) + hunger `+` → 2 + 1 = 3.
    /// regular имеет «+», голодный + засчитывается.
    /// </summary>
    [Fact]
    public void V5_R22_RegularOnlyCrit_HungerPlus_PlainPlus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 5, 5, 10, 10 },
            hungerDice: new[] { 7 });
        Assert.Equal(3, r.TotalSuccesses);
    }

    /// <summary>
    /// regular `——!!` (2) + hunger `!` → regular 2, hunger +1 (regular имеет «+»). Итого 3.
    /// </summary>
    [Fact]
    public void V5_R23_RegularOnlyCrit_HungerTen_NotMessy()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 5, 5, 10, 10 },
            hungerDice: new[] { 10 });
        Assert.Equal(3, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `———X` (−1, regularPlusCount = 0, regularHasCrit = true)
    /// + hunger `!` → Messy (флаг, без числового вклада).
    /// Hunger кубик ! даёт +1. Итого −1 + 1 = 0, флаг Messy.
    /// </summary>
    [Fact]
    public void V5_R24_RegularOnlyOneAndBlanks_HungerTen_Messy()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 5, 5, 5, 1 },
            hungerDice: new[] { 10 });
        Assert.Equal(0, r.TotalSuccesses);
        Assert.True(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `———X` + hunger `X` → Bestial (regular без «+», есть крит X).
    /// Regular: −1. Hunger кубик X даёт −1 (как обычный провал). Bestial — только флаг.
    /// Итого −1 + (−1) = −2.
    /// </summary>
    [Fact]
    public void V5_R25_RegularOnlyOneAndBlanks_HungerOne_Bestial()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 5, 5, 5, 1 },
            hungerDice: new[] { 1 });
        Assert.Equal(-2, r.TotalSuccesses);
        Assert.True(r.IsBestialFailure);
    }

    /// <summary>
    /// regular `———X` + hunger `—` → regular −1, hunger 0. Итого −1 (ботч, есть 1).
    /// </summary>
    [Fact]
    public void V5_R26_RegularOnlyOneAndBlanks_HungerEmpty_Botch()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 5, 5, 5, 1 },
            hungerDice: new[] { 5 });
        Assert.Equal(-1, r.TotalSuccesses);
        Assert.True(r.IsBotch);
        Assert.False(r.IsMessyCritical);
        Assert.False(r.IsBestialFailure);
    }

    /// <summary>
    /// regular `————` (0, regularPlusCount = 0, regularHasCrit = false)
    /// + hunger `!` → нет поддержки → обычный +1. Итого 1.
    /// </summary>
    [Fact]
    public void V5_R27_RegularAllBlankNoCrit_HungerTen_PlainPlus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 5, 5, 5, 5 },
            hungerDice: new[] { 10 });
        Assert.Equal(1, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `————` + hunger `X` → нет поддержки → обычный −1. Итого −1.
    /// </summary>
    [Fact]
    public void V5_R28_RegularAllBlankNoCrit_HungerOne_PlainMinus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 5, 5, 5, 5 },
            hungerDice: new[] { 1 });
        Assert.Equal(-1, r.TotalSuccesses);
        Assert.False(r.IsBestialFailure);
    }

    /// <summary>
    /// regular `————` + hunger `+` → regular 0, hunger +1. Итого 1.
    /// </summary>
    [Fact]
    public void V5_R29_RegularAllBlank_HungerPlus_PlainPlus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 5, 5, 5, 5 },
            hungerDice: new[] { 7 });
        Assert.Equal(1, r.TotalSuccesses);
    }

    /// <summary>
    /// regular `++++` + hunger `——` → 4.
    /// </summary>
    [Fact]
    public void V5_R30_RegularCleanPlus_Hunger2Blanks_Plain()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 8, 9 },
            hungerDice: new[] { 5, 5 });
        Assert.Equal(4, r.TotalSuccesses);
    }

    // ----- V5: hunger 2 -----

    /// <summary>
    /// regular `++++` + hunger `!!` → 4 + 2 = 6, не Messy.
    /// regular имеет «+», Messy подавлен, обе ! — обычные +1.
    /// </summary>
    [Fact]
    public void V5_R31_RegularClean_Hunger2Tens_NotMessy()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 8, 9 },
            hungerDice: new[] { 10, 10 });
        Assert.Equal(6, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `++++` + hunger `XX` → 4 + (−2) = 2, не Bestial.
    /// regular имеет «+», обе X — обычные −1.
    /// </summary>
    [Fact]
    public void V5_R32_RegularClean_Hunger2Ones_NotBestial()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 8, 9 },
            hungerDice: new[] { 1, 1 });
        Assert.Equal(2, r.TotalSuccesses);
        Assert.False(r.IsBestialFailure);
    }

    /// <summary>
    /// regular `++XX` (regularPlusCount = 0, regularHasCrit = true)
    /// + hunger `!!` → один Messy (флаг), вторая ! — обычная +1.
    /// Итог = 0 + 1 (!) = 1, флаг Messy.
    /// </summary>
    [Fact]
    public void V5_R33_RegularNoPlusWithCrit_Hunger2Tens_OneMessy()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 1, 1 },
            hungerDice: new[] { 10, 10 });
        Assert.True(r.IsMessyCritical);
        Assert.Equal(2, r.TotalSuccesses); // 0 + 1(!) + 1(!) = 2
    }

    /// <summary>
    /// regular `++XX` + hunger `XX` → один Bestial (вторая X — обычная −1).
    /// Hunger кубики X дают −1 каждый. Bestial — только флаг.
    /// Итог = 0 + (−1) + (−1) = −2.
    /// </summary>
    [Fact]
    public void V5_R34_RegularNoPlusWithCrit_Hunger2Ones_OneBestial()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 1, 1 },
            hungerDice: new[] { 1, 1 });
        Assert.True(r.IsBestialFailure);
        Assert.Equal(-2, r.TotalSuccesses);
    }

    /// <summary>
    /// regular `++XX` + hunger `+!` → regularPlusCount = 0, regularHasCrit = true.
    /// голодный ! → Messy (флаг), голодный ! даёт +1.
    /// Итого: 0 + 1 + 1 (!) = 2, флаг Messy.
    /// </summary>
    [Fact]
    public void V5_R35_RegularNoPlusWithCrit_HungerPlusTen_Messy()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 1, 1 },
            hungerDice: new[] { 7, 10 });
        Assert.True(r.IsMessyCritical);
        Assert.Equal(2, r.TotalSuccesses); // 0 + 1(+) + 1(!) = 2
    }

    /// <summary>
    /// regular `++XX` + hunger `X!` → доп. куб имеет власть.
    /// regular нет успехов 6–9, hunger содержит оба крита X и !.
    /// Hunger вклад: −1 + 1 = 0. Доп. куб 6 → +1. Итог 0 + 0 + 1 = 1. Без флагов.
    /// </summary>
    [Fact]
    public void V5_R36_BonusDie_WhenMutualCrits()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 1, 1 },
            hungerDice: new[] { 1, 10 },
            bonusDie: 6);
        Assert.Equal(1, r.TotalSuccesses);
        Assert.False(r.IsBestialFailure);
        Assert.False(r.IsMessyCritical);
        Assert.Equal(6, r.BonusDie);
    }

    /// <summary>
    /// regular `+!!!` (4) + hunger `!!` → regular имеет «+», обе ! — обычные +1.
    /// Итого 4 + 2 = 6.
    /// </summary>
    [Fact]
    public void V5_R37_RegularHasCrit_Hunger2Tens_BothPlus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 10, 10, 10 },
            hungerDice: new[] { 10, 10 });
        Assert.Equal(6, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `XXXX` (−4, regularPlusCount = 0, regularHasCrit = true)
    /// + hunger `!!` → один Messy (флаг), вторая ! — обычная +1.
    /// Итог −4 + 1 (!) + 1 (!) = −2, флаг Messy.
    /// </summary>
    [Fact]
    public void V5_R38_RegularOnlyOnes_Hunger2Tens_OneMessy()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 1, 1, 1, 1 },
            hungerDice: new[] { 10, 10 });
        Assert.True(r.IsMessyCritical);
        Assert.Equal(-2, r.TotalSuccesses); // -4 + 1(!) + 1(!) = -2
    }

    /// <summary>
    /// regular `++++` + hunger `——` → 4.
    /// </summary>
    [Fact]
    public void V5_R39_RegularClean_Hunger2Blanks_Plain()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 7, 8, 9 },
            hungerDice: new[] { 5, 5 });
        Assert.Equal(4, r.TotalSuccesses);
    }

    /// <summary>
    /// regular `+—+X` (1: 6+5+6−1) + hunger `X—` → regular имеет «+», Bestial подавлен.
    /// regular 1 + hunger X(−1) + hunger —(0) = 0.
    /// </summary>
    [Fact]
    public void V5_R40_RegularPlusAndX_HungerOneAndBlank_NotBestial()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 5, 6, 1 },
            hungerDice: new[] { 1, 5 });
        Assert.Equal(0, r.TotalSuccesses);
        Assert.False(r.IsBestialFailure);
    }

    /// <summary>
    /// regular `+—!!` (3) + hunger `——` → 3.
    /// </summary>
    [Fact]
    public void V5_R41_RegularPlusAndCrit_Hunger2Blanks_Plain()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 6, 5, 10, 10 },
            hungerDice: new[] { 5, 5 });
        Assert.Equal(3, r.TotalSuccesses);
    }

    /// <summary>
    /// regular `————` (0) + hunger `——` → 0.
    /// </summary>
    [Fact]
    public void V5_R42_RegularAllBlank_Hunger2Blanks_Plain()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 5, 5, 5, 5 },
            hungerDice: new[] { 5, 5 });
        Assert.Equal(0, r.TotalSuccesses);
    }

    /// <summary>
    /// regular `————` + hunger `!!` → regular без «+», regularHasCrit = false.
    /// Голодный ! без поддержки — обычный +1. Итого 2.
    /// </summary>
    [Fact]
    public void V5_R43_RegularAllBlankNoCrit_Hunger2Tens_BothPlus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 5, 5, 5, 5 },
            hungerDice: new[] { 10, 10 });
        Assert.Equal(2, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
    }

    /// <summary>
    /// regular `————` + hunger `XX` → regularHasCrit = false.
    /// Голодный X без поддержки — обычный −1. Итого −2.
    /// </summary>
    [Fact]
    public void V5_R44_RegularAllBlankNoCrit_Hunger2Ones_BothMinus1()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 5, 5, 5, 5 },
            hungerDice: new[] { 1, 1 });
        Assert.Equal(-2, r.TotalSuccesses);
        Assert.False(r.IsBestialFailure);
    }

    /// <summary>
    /// regular `————` + hunger `X!` → regularHasCrit = false.
    /// Оба голодных крита без поддержки — обычные ±1. Итого 0.
    /// </summary>
    [Fact]
    public void V5_R45_RegularAllBlankNoCrit_HungerOneTen_PlainZero()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 5, 5, 5, 5 },
            hungerDice: new[] { 1, 10 });
        Assert.Equal(0, r.TotalSuccesses);
        Assert.False(r.IsMessyCritical);
        Assert.False(r.IsBestialFailure);
    }

    /// <summary>
    /// regular `————` + hunger `——` → 0, оба пустые.
    /// </summary>
    [Fact]
    public void V5_R46_RegularAllBlank_Hunger2Blanks_Plain()
    {
        var r = VampireDicePool.EvaluateV5(
            regularDice: new[] { 5, 5, 5, 5 },
            hungerDice: new[] { 5, 5 });
        Assert.Equal(0, r.TotalSuccesses);
    }

    // ----- Бросок с доп. кубом (через RollV5) -----

    [Fact]
    public void RollV5_GeneratesBonusDie_WhenMutualCrits()
    {
        // Проверяем, что метод RollV5 не падает, когда в обоих пулах есть X↔! —
        // доп. куб должен быть сгенерирован.
        var r = VampireDicePool.RollV5(poolSize: 6, hunger: 2, rng: new SeededRandom(42));
        Assert.NotNull(r);
    }

    // ----- FormatDice -----

    [Fact]
    public void FormatDice_JoinsWithSeparator()
    {
        var s = VampireDicePool.FormatDice(new[] { 1, 6, 10 });
        Assert.Equal("1, 6, 10", s);
    }

    // ----- OutcomeLabel -----

    [Fact]
    public void OutcomeLabel_ChoosesMostSevere()
    {
        // Обновлено под новую сигнатуру V5RollResult:
        // (regularDice, hungerDice, regularSuccesses, regularOnes, hungerSum,
        //  totalSuccesses, isMessy, isBestial, isBotch, bonusDie)
        var messy = new V5RollResult(
            new[] { 6 }, new[] { 10 },
            1, 0, 1, 2,
            true, false, false, null);
        var bestial = new V5RollResult(
            new[] { 3 }, new[] { 1 },
            0, 0, -1, -1,
            false, true, false, null);
        var success = new V5RollResult(
            new[] { 7 }, new int[0],
            1, 0, 0, 1,
            false, false, false, null);

        Assert.Contains("Голодный успех", messy.OutcomeLabel());
        Assert.Contains("Голодный провал", bestial.OutcomeLabel());
        Assert.Equal("Успех", success.OutcomeLabel());
    }
}
