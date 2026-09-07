using System;
using System.Linq;
using Discord;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

public class VampireExperienceComponentsTests
{
    [Fact]
    public void Build_HasTwoButtons()
    {
        var mc = VampireExperienceComponents.Build(Guid.NewGuid());
        var row = Assert.IsType<ActionRowComponent>(Assert.Single(mc.Components));
        var buttons = row.Components.OfType<ButtonComponent>().ToList();
        Assert.Equal(2, buttons.Count);
        Assert.Contains(buttons, b => b.Label == "Начислить" && b.Style == ButtonStyle.Success);
        Assert.Contains(buttons, b => b.Label == "Потратить" && b.Style == ButtonStyle.Danger);
    }

    [Fact]
    public void Build_EmptyGuid_Throws()
    {
        Assert.Throws<ArgumentException>(() => VampireExperienceComponents.Build(Guid.Empty));
    }

    [Theory]
    [InlineData(ExperienceModalAction.Grant)]
    [InlineData(ExperienceModalAction.Spend)]
    public void TryParse_Roundtrips(ExperienceModalAction action)
    {
        var id = Guid.NewGuid();
        var cid = action == ExperienceModalAction.Grant
            ? VampireExperienceComponents.BuildGrantId(id)
            : VampireExperienceComponents.BuildSpendId(id);
        Assert.True(VampireExperienceComponents.TryParse(cid, out var parsed, out var parsedId));
        Assert.Equal(action, parsed);
        Assert.Equal(id, parsedId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("vtm_xp_modal:grant:abc")] // modal prefix не кнопка
    [InlineData("vtm_xp:notanaction:abc123")]
    [InlineData("vtm_xp:grant:notaguid")]
    public void TryParse_ReturnsFalse_ForInvalid(string cid)
    {
        Assert.False(VampireExperienceComponents.TryParse(cid, out _, out _));
    }

    [Fact]
    public void IsOurs_OnlyOurPrefix()
    {
        Assert.True(VampireExperienceComponents.IsOurs("vtm_xp:grant:abc"));
        Assert.False(VampireExperienceComponents.IsOurs("vtm_btn:close:abc"));
        Assert.False(VampireExperienceComponents.IsOurs(""));
    }
}
