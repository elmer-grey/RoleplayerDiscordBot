using System;
using System.Linq;
using RPBot.VtM;
using Xunit;

namespace RPBot.Tests;

/// <summary>
/// Тесты чистой логики <see cref="VampireFrenzyResolver"/>.
/// </summary>
public class VampireFrenzyResolverTests
{
    private static VampireCharacter Make(string clan = "Гангрел",
        int selfControl = 3, int courage = 3, string? instincts = null)
    {
        var c = new VampireCharacter
        {
            Clan = clan,
            CharacterId = Guid.NewGuid(),
            PlayerId = 1,
        };
        c.Virtues[VampireParameterCatalog.VirtueConscience] = 3;
        c.Virtues[VampireParameterCatalog.VirtueSelfControl] = selfControl;
        c.Virtues[VampireParameterCatalog.VirtueCourage] = courage;
        if (instincts != null)
            c.Virtues["Инстинкты"] = 1;
        return c;
    }

    [Fact]
    public void ComputePoolSize_Frenzy_UsesSelfControl()
    {
        var c = Make(selfControl: 4);
        Assert.Equal(4, VampireFrenzyResolver.ComputePoolSize(c, VampireFrenzyResolver.FrenzyKind.Frenzy));
    }

    [Fact]
    public void ComputePoolSize_Rotschreck_UsesCourage()
    {
        var c = Make(courage: 2);
        Assert.Equal(2, VampireFrenzyResolver.ComputePoolSize(c, VampireFrenzyResolver.FrenzyKind.Rötschreck));
    }

    [Fact]
    public void ComputePoolSize_ClampsToFive()
    {
        var c = Make(selfControl: 99);
        Assert.Equal(5, VampireFrenzyResolver.ComputePoolSize(c, VampireFrenzyResolver.FrenzyKind.Frenzy));
    }

    [Fact]
    public void ComputeDifficulty_AddsBrujahBonus()
    {
        Assert.Equal(8, VampireFrenzyResolver.ComputeDifficulty(VampireFrenzyResolver.FrenzyKind.Frenzy, 6, 3, "Бруха"));
    }

    [Fact]
    public void ComputeDifficulty_BrujahCappedAtTen()
    {
        Assert.Equal(10, VampireFrenzyResolver.ComputeDifficulty(VampireFrenzyResolver.FrenzyKind.Frenzy, 8, 3, "Бруха"));
    }

    [Fact]
    public void ComputeDifficulty_NonBrujah_NoBonus()
    {
        Assert.Equal(6, VampireFrenzyResolver.ComputeDifficulty(VampireFrenzyResolver.FrenzyKind.Frenzy, 6, 3, "Вентру"));
    }

    [Fact]
    public void HorrorActDifficulty_MapsConscienceToDifficulty()
    {
        Assert.Equal(6, VampireFrenzyResolver.HorrorActDifficulty(3)); // 9-3
        Assert.Equal(8, VampireFrenzyResolver.HorrorActDifficulty(1)); // 9-1=8
        Assert.Equal(4, VampireFrenzyResolver.HorrorActDifficulty(5)); // 9-5=4
    }

    [Fact]
    public void IsInstinctDriven_NoSelfControl_NoInstincts_False()
    {
        var c = Make(selfControl: 0);
        Assert.False(VampireFrenzyResolver.IsInstinctDriven(c));
    }

    [Fact]
    public void IsInstinctDriven_HasInstincts_NoSelfControl_True()
    {
        var c = Make(selfControl: 0, instincts: "Инстинкты");
        Assert.True(VampireFrenzyResolver.IsInstinctDriven(c));
    }

    [Fact]
    public void IsInstinctDriven_HasSelfControl_FalseEvenIfInstinctsPresent()
    {
        var c = Make(selfControl: 3, instincts: "Инстинкты");
        Assert.False(VampireFrenzyResolver.IsInstinctDriven(c));
    }

    [Fact]
    public void Roll_InstinctDriven_AlwaysUnleashed_AndPicksAtavism()
    {
        var c = Make(clan: "Гангрел", selfControl: 0, instincts: "Инстинкты");
        var rng = new RPBot.VtM.SeededRandom(42);

        var result = VampireFrenzyResolver.Roll(c, VampireFrenzyResolver.FrenzyKind.Frenzy, 6, 0, rng, rollAtavism: true);

        Assert.Equal(VampireFrenzyResolver.FrenzyRollOutcome.Unleashed, result.Outcome);
        Assert.NotNull(result.ActiveAtavism);
        Assert.Single(c.ActiveAtavisms);
        Assert.Contains(result.ActiveAtavism!, c.ActiveAtavisms);
    }

    [Fact]
    public void Roll_InstinctDriven_NonGangrel_NoAtavism()
    {
        var c = Make(clan: "Вентру", selfControl: 0, instincts: "Инстинкты");
        var rng = new RPBot.VtM.SeededRandom(42);

        var result = VampireFrenzyResolver.Roll(c, VampireFrenzyResolver.FrenzyKind.Frenzy, 6, 0, rng, rollAtavism: true);

        Assert.Equal(VampireFrenzyResolver.FrenzyRollOutcome.Unleashed, result.Outcome);
        Assert.Null(result.ActiveAtavism);
        Assert.Empty(c.ActiveAtavisms);
    }

    [Fact]
    public void Roll_NoSuccesses_Unleashed_WithGangrelAtavism()
    {
        var c = Make(clan: "Гангрел", selfControl: 3);
        // Подсовываем 3 единицы — все < 6, успехов 0. Picker вернёт индекс 1.
        var fake = new FixedDiceRandom(new[] { 1, 1, 1 });

        var result = VampireFrenzyResolver.Roll(c, VampireFrenzyResolver.FrenzyKind.Frenzy, 6, 0, fake, rollAtavism: true);

        Assert.Equal(VampireFrenzyResolver.FrenzyRollOutcome.Unleashed, result.Outcome);
        Assert.Equal(0, result.Successes);
        Assert.NotNull(result.ActiveAtavism);
    }

    [Fact]
    public void Roll_SomeSuccesses_PartiallyContained()
    {
        var c = Make(selfControl: 3);
        // 2 успеха (7, 8), 1 провал (3).
        var fake = new FixedDiceRandom(new[] { 7, 8, 3 });

        var result = VampireFrenzyResolver.Roll(c, VampireFrenzyResolver.FrenzyKind.Frenzy, 6, 0, fake);

        Assert.Equal(VampireFrenzyResolver.FrenzyRollOutcome.PartiallyContained, result.Outcome);
        Assert.Equal(2, result.Successes);
        Assert.Equal(2, result.RoundsContained);
        Assert.Equal(2, result.AccumulatedSuccesses);
        Assert.Null(result.ActiveAtavism); // не Гангрел
    }

    [Fact]
    public void Roll_AccumulatedSuccesses_ReachesFive_FullySuppressed()
    {
        var c = Make(selfControl: 3);
        // 2 успеха — добавим ещё 3 накопленных.
        var fake = new FixedDiceRandom(new[] { 7, 8, 3 });

        var result = VampireFrenzyResolver.Roll(c, VampireFrenzyResolver.FrenzyKind.Frenzy, 6, 3, fake);

        Assert.Equal(VampireFrenzyResolver.FrenzyRollOutcome.FullySuppressed, result.Outcome);
        Assert.Equal(5, result.AccumulatedSuccesses);
    }

    [Fact]
    public void Roll_DeterministicWithSameDice()
    {
        var c1 = Make(selfControl: 3);
        var c2 = Make(selfControl: 3);
        var fake1 = new FixedDiceRandom(new[] { 7, 8, 3 });
        var fake2 = new FixedDiceRandom(new[] { 7, 8, 3 });

        var r1 = VampireFrenzyResolver.Roll(c1, VampireFrenzyResolver.FrenzyKind.Frenzy, 6, 0, fake1);
        var r2 = VampireFrenzyResolver.Roll(c2, VampireFrenzyResolver.FrenzyKind.Frenzy, 6, 0, fake2);

        Assert.Equal(r1.Outcome, r2.Outcome);
        Assert.Equal(r1.Successes, r2.Successes);
        Assert.Equal(r1.PoolSize, r2.PoolSize);
    }

    [Fact]
    public void Roll_AtavismAddedOnce_OnRepeatedRolls()
    {
        var c = Make(clan: "Гангрел", selfControl: 3);
        // Подсовываем 1 — успехов нет, но атавизм добавляется по индексу 1.
        var fake = new FixedDiceRandom(new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 });

        VampireFrenzyResolver.Roll(c, VampireFrenzyResolver.FrenzyKind.Frenzy, 6, 0, fake, rollAtavism: true);
        var firstAtavism = c.ActiveAtavisms.Single();

        // Повторный бросок с тем же рандомом — тот же атавизм, дубликат не добавляется.
        var fake2 = new FixedDiceRandom(new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 });
        VampireFrenzyResolver.Roll(c, VampireFrenzyResolver.FrenzyKind.Frenzy, 6, 0, fake2, rollAtavism: true);

        Assert.Single(c.ActiveAtavisms);
        Assert.Equal(firstAtavism, c.ActiveAtavisms[0]);
    }

    [Fact]
    public void Roll_CustomAtavismPicker_Respected()
    {
        var c = Make(clan: "Гангрел", selfControl: 3);
        var fake = new FixedDiceRandom(new[] { 1, 1, 1 });

        var result = VampireFrenzyResolver.Roll(c, VampireFrenzyResolver.FrenzyKind.Frenzy, 6, 0, fake,
            rollAtavism: true,
            atavismPicker: idx => $"CUSTOM_{idx}");

        Assert.Equal("CUSTOM_1", result.ActiveAtavism);
        Assert.Contains("CUSTOM_1", c.ActiveAtavisms);
    }

    [Fact]
    public void FrenzyRollResult_Describe_HasExpectedSubstrings()
    {
        var r1 = new VampireFrenzyResolver.FrenzyRollResult(
            VampireFrenzyResolver.FrenzyRollOutcome.FullySuppressed, 3, 5, 6, 5, 0, null);
        Assert.Contains("усмирён", r1.Describe());

        var r2 = new VampireFrenzyResolver.FrenzyRollResult(
            VampireFrenzyResolver.FrenzyRollOutcome.PartiallyContained, 3, 2, 6, 2, 2, null);
        Assert.Contains("сдерживается 2", r2.Describe());

        var r3 = new VampireFrenzyResolver.FrenzyRollResult(
            VampireFrenzyResolver.FrenzyRollOutcome.Unleashed, 3, 0, 6, 0, 0, null);
        Assert.Contains("вырвался", r3.Describe());
    }

    /// <summary>Адаптер фиксированных бросков — для детерминизма тестов.
    /// Реализует <see cref="IRandom"/> напрямую, минуя <see cref="SystemRandomAdapter"/>
    /// (он sealed).</summary>
    private sealed class FixedDiceRandom : RPBot.VtM.IRandom
    {
        private readonly System.Collections.Generic.Queue<int> _values;
        public FixedDiceRandom(System.Collections.Generic.IEnumerable<int> values)
        {
            _values = new System.Collections.Generic.Queue<int>(values);
        }
        public int Next(int minValue, int maxValue)
        {
            // Внешний код вызывает Next(min,max) — нам нужно отдать значение из очереди,
            // игнорируя границы.
            if (_values.Count == 0) return minValue;
            return _values.Dequeue();
        }
    }
}
