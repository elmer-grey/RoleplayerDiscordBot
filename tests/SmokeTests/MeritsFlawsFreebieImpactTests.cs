using Xunit;
using Xunit.Abstractions;
using RPBot.VtM;

namespace SmokeTests;

/// <summary>
/// Проверяем связь Merits/Flaws с freebie-пулом Шага 5.
/// V20 стр. 86, 92, 485+, 523+: Пул = 15 + sum(Flaws) − sum(Merits) − sum(Allocate).
/// </summary>
public class MeritsFlawsFreebieImpactTests
{
    private readonly ITestOutputHelper _out;
    public MeritsFlawsFreebieImpactTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void FlawsEnlargeFreebiePool()
    {
        var d = new VampireCharacter { Clan = "Носферату" };

        var poolBefore = VampireFinishingResolver.RemainingFreebies(d);
        _out.WriteLine($"Пул до Flaw: {poolBefore}");

        VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Некрасивый");   // cost 1
        VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Забывчивость"); // cost 1

        var poolAfter = VampireFinishingResolver.RemainingFreebies(d);
        var flawsCost = VampireMeritsFlawsResolver.FlawsCost(d);
        _out.WriteLine($"Пул после 2 Flaw (cost {flawsCost}): {poolAfter}");

        Assert.Equal(15, poolBefore);
        Assert.Equal(2, flawsCost);
        Assert.Equal(17, poolAfter); // 15 + 2
    }

    [Fact]
    public void MeritsConsumeFreebiePool()
    {
        var d = new VampireCharacter { Clan = "Носферату" };

        Assert.Equal(15, VampireFinishingResolver.RemainingFreebies(d));
        Assert.Equal(15, VampireFinishingResolver.EffectiveFreebiePool(d));

        // Merit за 4: пул уменьшается и EffectivePool, и Remaining.
        VampireMeritsFlawsResolver.AddMerit(d, "Привилегия", cost: 4);

        Assert.Equal(4, VampireMeritsFlawsResolver.MeritsCost(d));
        Assert.Equal(11, VampireFinishingResolver.EffectiveFreebiePool(d)); // 15 − 4
        Assert.Equal(7,  VampireFinishingResolver.RemainingFreebies(d));    // 11 − 4 consumed

        _out.WriteLine($"EffectivePool={VampireFinishingResolver.EffectiveFreebiePool(d)}, " +
                       $"Remaining={VampireFinishingResolver.RemainingFreebies(d)}");
    }

    [Fact]
    public void MeritInsufficientFreebies_Rejected()
    {
        var d = new VampireCharacter { Clan = "Носферату" };

        // Сначала берём Merit за 4. Осталось: 15−4=11 effective, 7 remaining.
        Assert.True(VampireMeritsFlawsResolver.AddMerit(d, "Привилегия", cost: 4).IsSuccess);
        Assert.Equal(11, VampireFinishingResolver.EffectiveFreebiePool(d));
        Assert.Equal(7,  VampireFinishingResolver.RemainingFreebies(d));

        // Пытаемся купить «Душа коснулась» (cost 7) — должно провалиться.
        var r = VampireMeritsFlawsResolver.AddMerit(d, "Душа коснулась", cost: 7);
        Assert.False(r.IsSuccess);
        Assert.Equal(VampireMeritsFlawsResolver.Failure.InsufficientFreebies, r.Failure);

        _out.WriteLine($"После отказа пул остался: {VampireFinishingResolver.RemainingFreebies(d)}");
    }

    [Fact]
    public void MaxTwoFlaws_LimitEnforced()
    {
        var d = new VampireCharacter { Clan = "Носферату" };

        Assert.True(VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Некрасивый").IsSuccess);
        Assert.True(VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Забывчивость").IsSuccess);

        var r = VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Дальтонизм");
        Assert.False(r.IsSuccess);
        Assert.Equal(VampireMeritsFlawsResolver.Failure.MaxFlawsReached, r.Failure);
        _out.WriteLine($"Третий Flaw отклонён: {r.Message}");
    }

    [Fact]
    public void RemovingFlawShrinksPool()
    {
        var d = new VampireCharacter { Clan = "Носферату" };
        VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Некрасивый");
        VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Забывчивость");
        Assert.Equal(17, VampireFinishingResolver.RemainingFreebies(d));

        VampireMeritsFlawsResolver.RemoveFlaw(d, "Некрасивый");
        Assert.Equal(16, VampireFinishingResolver.RemainingFreebies(d));
        _out.WriteLine($"После RemoveFlaw пул = {VampireFinishingResolver.RemainingFreebies(d)}");
    }

    [Fact]
    public void RemovingMeritRestoresPool()
    {
        var d = new VampireCharacter { Clan = "Носферату" };
        VampireMeritsFlawsResolver.AddMerit(d, "Привилегия", cost: 4);
        // Effective=11, Consumed=4, Remaining=7.
        Assert.Equal(11, VampireFinishingResolver.EffectiveFreebiePool(d));
        Assert.Equal(7,  VampireFinishingResolver.RemainingFreebies(d));

        VampireMeritsFlawsResolver.RemoveMerit(d, "Привилегия");
        // После удаления Merit: Effective=15, Consumed=0, Remaining=15.
        Assert.Equal(15, VampireFinishingResolver.EffectiveFreebiePool(d));
        Assert.Equal(15, VampireFinishingResolver.RemainingFreebies(d));
        _out.WriteLine($"После RemoveMerit: Effective={VampireFinishingResolver.EffectiveFreebiePool(d)}, " +
                       $"Remaining={VampireFinishingResolver.RemainingFreebies(d)}");
    }

    [Fact]
    public void EffectiveFreebiePool_IncludesAll()
    {
        var d = new VampireCharacter { Clan = "Носферату" };
        VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Некрасивый");                // +1
        VampireMeritsFlawsResolver.AddMerit(d, "Бдительный ум", cost: 1);              // −1
        // Эффективный пул: 15 + 1 − 1 = 15.
        Assert.Equal(15, VampireFinishingResolver.EffectiveFreebiePool(d));
        // Consumed = MeritsCost(1) + 0 = 1.
        // Remaining = 15 − 1 = 14.
        Assert.Equal(14, VampireFinishingResolver.RemainingFreebies(d));
    }

    [Fact]
    public void AllocateFreebie_AfterMeritsAndFlaws_UsesEffectivePool()
    {
        var d = new VampireCharacter { Clan = "Носферату" };
        // Берём 1 Flaw (+1) и 1 Merit (−1) → EffectivePool остаётся 15.
        VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Некрасивый");
        VampireMeritsFlawsResolver.AddMerit(d, "Бдительный ум", cost: 1);

        // Тратим: Атрибут 5 + Способность 2 + Дисциплина 7 = 14. И consumed = 1+14 = 15. OK.
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Сила", out _).IsSuccess);
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Ability, "Атлетика", out _).IsSuccess);
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Discipline, "Мощь", out _).IsSuccess);

        // Осталось 0. Попытка взять ещё — отказ.
        var r = VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Ловкость", out _);
        Assert.False(r.IsSuccess);
        Assert.Equal(VampireFinishingResolver.Failure.PoolExhausted, r.Failure);

        _out.WriteLine($"После всех spent: Effective={VampireFinishingResolver.EffectiveFreebiePool(d)}, " +
                       $"Remaining={VampireFinishingResolver.RemainingFreebies(d)}");
        Assert.Equal(0, VampireFinishingResolver.RemainingFreebies(d));
        Assert.True(VampireFinishingResolver.FreebiesExhausted(d));
    }
}

