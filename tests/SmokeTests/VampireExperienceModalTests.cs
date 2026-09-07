using System;
using System.Linq;
using Discord;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

public class VampireExperienceModalTests
{
    [Fact]
    public void BuildCustomId_HasCorrectShape()
    {
        var id = Guid.NewGuid();
        var cid = VampireExperienceModal.BuildCustomId(ExperienceModalAction.Grant, id);
        Assert.Equal($"vtm_xp_modal:grant:{id:N}", cid);
    }

    [Fact]
    public void BuildCustomId_EmptyId_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            VampireExperienceModal.BuildCustomId(ExperienceModalAction.Spend, Guid.Empty));
    }

    [Fact]
    public void Build_ReturnsModalWithCorrectTitle()
    {
        var id = Guid.NewGuid();
        var grant = VampireExperienceModal.Build(ExperienceModalAction.Grant, id);
        Assert.Equal("Начислить опыт", grant.Title);
        var spend = VampireExperienceModal.Build(ExperienceModalAction.Spend, id);
        Assert.Equal("Потратить опыт", spend.Title);
    }

    [Fact]
    public void Build_HasAmountField()
    {
        var modal = VampireExperienceModal.Build(ExperienceModalAction.Grant, Guid.NewGuid());
        Assert.Contains(modal.Components.OfType<Discord.TextInputComponent>(),
            c => c.CustomId == VampireExperienceModal.AmountFieldId);
    }

    [Theory]
    [InlineData(ExperienceModalAction.Grant)]
    [InlineData(ExperienceModalAction.Spend)]
    public void TryParse_Roundtrips(ExperienceModalAction action)
    {
        var id = Guid.NewGuid();
        var cid = VampireExperienceModal.BuildCustomId(action, id);
        Assert.True(VampireExperienceModal.TryParse(cid, out var parsed, out var parsedId));
        Assert.Equal(action, parsed);
        Assert.Equal(id, parsedId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("foo:grant:abc")]
    [InlineData("vtm_xp_modal:unknown:abc")]
    [InlineData("vtm_xp_modal:grant:notaguid")]
    [InlineData("vtm_xp_modal:grant:00000000000000000000000000000000")]
    public void TryParse_ReturnsFalse_ForInvalid(string cid)
    {
        Assert.False(VampireExperienceModal.TryParse(cid, out _, out _));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    [InlineData("  42  ", 42)]
    [InlineData("999999", 999999)]
    public void TryParseAmount_AcceptsPositiveIntegers(string raw, int expected)
    {
        Assert.True(VampireExperienceModal.TryParseAmount(raw, out var amount));
        Assert.Equal(expected, amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData(null)]
    public void TryParseAmount_RejectsInvalid(string? raw)
    {
        Assert.False(VampireExperienceModal.TryParseAmount(raw, out var amount));
        Assert.Equal(0, amount);
    }

    [Fact]
    public void IsOurs_OnlyOurPrefix()
    {
        Assert.True(VampireExperienceModal.IsOurs("vtm_xp_modal:grant:abc"));
        Assert.False(VampireExperienceModal.IsOurs("vtm_btn:close:abc"));
        Assert.False(VampireExperienceModal.IsOurs(""));
    }
}
