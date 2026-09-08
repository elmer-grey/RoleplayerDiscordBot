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

        // Merit за 4: уменьшает EffectivePool до 11. ConsumedFreebies НЕ включает
        // стоимость Merit (она уже вычтена из EffectivePool — иначе двойной учёт).
        // Remaining = 15 − 4 = 11.
        VampireMeritsFlawsResolver.AddMerit(d, "Привилегия", cost: 4);

        Assert.Equal(4, VampireMeritsFlawsResolver.MeritsCost(d));
        Assert.Equal(11, VampireFinishingResolver.EffectiveFreebiePool(d)); // 15 − 4
        Assert.Equal(11, VampireFinishingResolver.RemainingFreebies(d));    // 11 − 0 consumed (Merit уже в потолке)

        _out.WriteLine($"EffectivePool={VampireFinishingResolver.EffectiveFreebiePool(d)}, " +
                       $"Remaining={VampireFinishingResolver.RemainingFreebies(d)}");
    }

    [Fact]
    public void MeritInsufficientFreebies_Rejected()
    {
        var d = new VampireCharacter { Clan = "Носферату" };

        // Сначала берём Merit за 4. Effective = 15 − 4 = 11.
        Assert.True(VampireMeritsFlawsResolver.AddMerit(d, "Привилегия", cost: 4).IsSuccess);
        Assert.Equal(11, VampireFinishingResolver.EffectiveFreebiePool(d));
        Assert.Equal(11, VampireFinishingResolver.RemainingFreebies(d));

        // Пытаемся купить «Душа коснулась» (cost 7) — Effective станет 4,
        // но consumed уже 0, и будущая проверка должна отклонить, так как
        // иначе игрок получает Merit бесплатно.
        // Корректная логика: AddMerit проверяет, что Effective − Consumed >= cost,
        // где Effective уже учитывает ВСЕ будущие Merits.
        // Будущая Effective = 15 − 4 − 7 = 4, consumed = 0, итого 4 < 7 → отказ.
        var r = VampireMeritsFlawsResolver.AddMerit(d, "Душа коснулась", cost: 7);
        Assert.False(r.IsSuccess);
        Assert.Equal(VampireMeritsFlawsResolver.Failure.InsufficientFreebies, r.Failure);

        _out.WriteLine($"После отказа пул остался: {VampireFinishingResolver.RemainingFreebies(d)}");
    }

    [Fact]
    public void MaxFlawsBonus_LimitIsSeven_NotTwo()
    {
        // V20 стр. 86: «количество недостатков ограничено семью свободными пунктами».
        // Это лимит на сумму (≤ 7), а не на количество. Можно взять 7 Flaw по 1
        // или 1 Flaw за 7.
        var d = new VampireCharacter { Clan = "Носферату" };

        // Берём 5 Flaw по 1 + 1 Flaw за 2 = 7 пунктов ровно (правило V20 стр. 86).
        // Каталог содержит только 6 Flaw с ценой 1, поэтому «Хромота» (2) добивает до 7.
        string[] oneDotFlaws =
        {
            "Некрасивый",
            "Забывчивость",
            "Глухота на одно ухо",
            "Дальтонизм",
            "Медленный рефлекс",
        };
        foreach (var f in oneDotFlaws)
        {
            var r = VampireMeritsFlawsResolver.AddFlawFromCatalog(d, f);
            Assert.True(r.IsSuccess, $"Flaw «{f}» не прошёл: {r.Message}");
        }
        // 5 + 2 = 7 ровно.
        Assert.True(VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Хромота").IsSuccess);
        Assert.Equal(7, VampireMeritsFlawsResolver.FlawsCost(d));
        Assert.Equal(6, VampireMeritsFlawsResolver.FlawsCount(d));
        Assert.Equal(22, VampireFinishingResolver.EffectiveFreebiePool(d)); // 15 + 7

        // Любой следующий Flaw — отказ.
        var over = VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Мягкий ум");
        Assert.False(over.IsSuccess);
        Assert.Equal(VampireMeritsFlawsResolver.Failure.MaxFlawsReached, over.Failure);
        _out.WriteLine($"После отказа 7-го Flaw (дополнительного): {over.Message}");
    }

    [Fact]
    public void MaxFlawsBonus_OneFlawOfSeven()
    {
        // Один Flaw с ценой 7 — должен пройти.
        var d = new VampireCharacter { Clan = "Носферату" };
        var r = VampireMeritsFlawsResolver.AddFlaw(d, "Одноногий", cost: 3);
        Assert.True(r.IsSuccess);

        var r2 = VampireMeritsFlawsResolver.AddFlaw(d, "Немой", cost: 4);
        Assert.True(r2.IsSuccess);
        Assert.Equal(7, VampireMeritsFlawsResolver.FlawsCost(d));

        // Третий на 1 — перебор (7 + 1 > 7).
        var over = VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Некрасивый");
        Assert.False(over.IsSuccess);
        Assert.Equal(VampireMeritsFlawsResolver.Failure.MaxFlawsReached, over.Failure);
    }

    [Fact]
    public void MaxFlawsBonus_MaxPoolIs22()
    {
        // V20: «при желании можно увеличить до 22».
        var d = new VampireCharacter { Clan = "Носферату" };
        string[] oneDotFlaws =
        {
            "Некрасивый", "Забывчивость", "Глухота на одно ухо", "Дальтонизм", "Медленный рефлекс",
        };
        foreach (var f in oneDotFlaws)
            VampireMeritsFlawsResolver.AddFlawFromCatalog(d, f);
        VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Хромота"); // cost 2

        Assert.Equal(7, VampireMeritsFlawsResolver.FlawsCost(d));
        Assert.Equal(22, VampireFinishingResolver.EffectiveFreebiePool(d));
        _out.WriteLine($"Максимальный эффективный пул: {VampireFinishingResolver.EffectiveFreebiePool(d)}");
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
        // Effective=11, Remaining=11 (Merit вычтен из потолка, Consumed=0).
        Assert.Equal(11, VampireFinishingResolver.EffectiveFreebiePool(d));
        Assert.Equal(11, VampireFinishingResolver.RemainingFreebies(d));

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
        // Consumed = 0 (Merit учтён в потолке, AllocateFreebie ещё не вызывался).
        // Remaining = 15 − 0 = 15.
        Assert.Equal(15, VampireFinishingResolver.RemainingFreebies(d));
    }

    [Fact]
    public void AllocateFreebie_AfterMeritsAndFlaws_UsesEffectivePool()
    {
        var d = new VampireCharacter { Clan = "Носферату" };
        // Берём 1 Flaw (+1) и 1 Merit (−1) → EffectivePool остаётся 15.
        VampireMeritsFlawsResolver.AddFlawFromCatalog(d, "Некрасивый");
        VampireMeritsFlawsResolver.AddMerit(d, "Бдительный ум", cost: 1);

        // Тратим: Атрибут 5 + Способность 2 + Дисциплина 7 = 14.
        // Consumed = 14 (Merit учтён в потолке, не здесь).
        // Remaining = 15 − 14 = 1.
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Сила", out _).IsSuccess);
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Ability, "Атлетика", out _).IsSuccess);
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Discipline, "Мощь", out _).IsSuccess);
        Assert.Equal(1, VampireFinishingResolver.RemainingFreebies(d));

        // Тратим последний 1 пункт (Background = 1) → 0.
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Background, "Богатство", out _).IsSuccess);

        // Попытка взять ещё — отказ.
        var r = VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Ловкость", out _);
        Assert.False(r.IsSuccess);
        Assert.Equal(VampireFinishingResolver.Failure.PoolExhausted, r.Failure);

        _out.WriteLine($"После всех spent: Effective={VampireFinishingResolver.EffectiveFreebiePool(d)}, " +
                       $"Remaining={VampireFinishingResolver.RemainingFreebies(d)}");
        Assert.Equal(0, VampireFinishingResolver.RemainingFreebies(d));
        Assert.True(VampireFinishingResolver.FreebiesExhausted(d));
    }
}

