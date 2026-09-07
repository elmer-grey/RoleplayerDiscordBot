using RPBot.VtM;
using Xunit;

namespace SmokeTests;

public class VampireConscienceResolverTests
{
    [Fact]
    public void ConscienceDicePool_UsesHumanity_WhenNoPath()
    {
        var c = new VampireCharacter { Path = "", PathRating = 0, Humanity = 7 };
        Assert.Equal(7, VampireConscienceResolver.ConscienceDicePool(c, currentHumanity: 7));
    }

    [Fact]
    public void ConscienceDicePool_UsesPath_WhenPathSet()
    {
        var c = new VampireCharacter { Path = "Путь Каина", PathRating = 6, Humanity = 8 };
        Assert.Equal(6, VampireConscienceResolver.ConscienceDicePool(c, currentHumanity: 8));
    }

    [Fact]
    public void ConscienceDicePool_ClampsTo10()
    {
        var c = new VampireCharacter { Humanity = 15 };
        Assert.Equal(10, VampireConscienceResolver.ConscienceDicePool(c, currentHumanity: 15));
    }

    [Fact]
    public void ConscienceDicePool_NegativeClampsToZero()
    {
        var c = new VampireCharacter { Humanity = -3 };
        Assert.Equal(0, VampireConscienceResolver.ConscienceDicePool(c, currentHumanity: -3));
    }

    [Fact]
    public void ConscienceDicePool_NullCharacterReturnsZero()
    {
        Assert.Equal(0, VampireConscienceResolver.ConscienceDicePool(null!, 5));
    }

    [Fact]
    public void Roll_FiveSuccesses_IsSuccess()
    {
        var r = VampireConscienceResolver.Roll(new[] { 8, 9, 10, 8, 9 });
        Assert.Equal(5, r.Successes);
        Assert.Equal(VampireConscienceResolver.ConscienceRollOutcome.Success, r.Outcome);
    }

    [Fact]
    public void Roll_OneSuccess_IsSuccess()
    {
        var r = VampireConscienceResolver.Roll(new[] { 2, 8, 5 });
        Assert.Equal(1, r.Successes);
        Assert.Equal(VampireConscienceResolver.ConscienceRollOutcome.Success, r.Outcome);
    }

    [Fact]
    public void Roll_NoSuccess_HasMaxDieIsFailure()
    {
        // нет кубиков >= 8, но максимум = 7 (>= 6) → Failure
        var r = VampireConscienceResolver.Roll(new[] { 1, 3, 5, 7, 2 });
        Assert.Equal(0, r.Successes);
        Assert.Equal(VampireConscienceResolver.ConscienceRollOutcome.Failure, r.Outcome);
    }

    [Fact]
    public void Roll_NoSuccess_AllBelowSix_IsBotch()
    {
        // все < 6 → Botch
        var r = VampireConscienceResolver.Roll(new[] { 1, 2, 3, 4, 5 });
        Assert.Equal(0, r.Successes);
        Assert.Equal(VampireConscienceResolver.ConscienceRollOutcome.Botch, r.Outcome);
    }

    [Fact]
    public void Roll_EmptyDice_IsBotch()
    {
        var r = VampireConscienceResolver.Roll(System.Array.Empty<int>());
        Assert.Equal(0, r.Successes);
        Assert.Equal(VampireConscienceResolver.ConscienceRollOutcome.Botch, r.Outcome);
    }

    [Fact]
    public void Roll_NullDice_IsBotch()
    {
        var r = VampireConscienceResolver.Roll(null!);
        Assert.Equal(0, r.Successes);
        Assert.Equal(VampireConscienceResolver.ConscienceRollOutcome.Botch, r.Outcome);
    }

    [Fact]
    public void Roll_DieOutOfRange_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => VampireConscienceResolver.Roll(new[] { 11 }));
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => VampireConscienceResolver.Roll(new[] { 0 }));
    }

    [Fact]
    public void Roll_CustomDifficulty_Ten_DecreasesSuccesses()
    {
        var r = VampireConscienceResolver.Roll(new[] { 8, 9, 10 }, difficulty: 10);
        Assert.Equal(1, r.Successes);
        Assert.Equal(VampireConscienceResolver.ConscienceRollOutcome.Success, r.Outcome);
    }

    [Fact]
    public void Roll_CustomDifficulty_Five_IncreasesSuccesses()
    {
        var r = VampireConscienceResolver.Roll(new[] { 3, 5, 6 }, difficulty: 5);
        Assert.Equal(2, r.Successes);
    }

    [Fact]
    public void Roll_CustomBotchThreshold_Four_MakesEvenHighFailuresBotch()
    {
        // при пороге 4 — кубик 3 = Botch, потому что он < 4
        var r = VampireConscienceResolver.Roll(new[] { 3 }, difficulty: 8, botchThreshold: 4);
        Assert.Equal(VampireConscienceResolver.ConscienceRollOutcome.Botch, r.Outcome);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void Roll_InvalidBotchThreshold_Throws(int threshold)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => VampireConscienceResolver.Roll(new[] { 5 }, botchThreshold: threshold));
    }

    [Fact]
    public void Apply_Success_NoChange()
    {
        var r = VampireConscienceResolver.Apply(VampireConscienceResolver.ConscienceRollOutcome.Success);
        Assert.Equal(0, r.HumanityDelta);
        Assert.Equal(0, r.ConscienceDelta);
        Assert.False(r.AddDerangement);
    }

    [Fact]
    public void Apply_Failure_LosesHumanityOnly()
    {
        var r = VampireConscienceResolver.Apply(VampireConscienceResolver.ConscienceRollOutcome.Failure);
        Assert.Equal(-1, r.HumanityDelta);
        Assert.Equal(0, r.ConscienceDelta);
        Assert.False(r.AddDerangement);
    }

    [Fact]
    public void Apply_Botch_LosesBothAndAddsDerangement()
    {
        var r = VampireConscienceResolver.Apply(VampireConscienceResolver.ConscienceRollOutcome.Botch);
        Assert.Equal(-1, r.HumanityDelta);
        Assert.Equal(-1, r.ConscienceDelta);
        Assert.True(r.AddDerangement);
    }

    [Theory]
    [InlineData(VampireConscienceResolver.ConscienceRollOutcome.Success)]
    [InlineData(VampireConscienceResolver.ConscienceRollOutcome.Failure)]
    [InlineData(VampireConscienceResolver.ConscienceRollOutcome.Botch)]
    public void Describe_NeverEmpty(VampireConscienceResolver.ConscienceRollOutcome outcome)
    {
        var s = VampireConscienceResolver.Describe(outcome);
        Assert.False(string.IsNullOrWhiteSpace(s));
    }

    [Fact]
    public void Describe_BotchMentionsDerangement()
    {
        var s = VampireConscienceResolver.Describe(VampireConscienceResolver.ConscienceRollOutcome.Botch);
        Assert.Contains("расстройство", s, System.StringComparison.OrdinalIgnoreCase);
    }
}
