using System;
using System.Linq;
using Discord;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

public class VampireMoralityComponentsTests
{
    [Fact]
    public void Build_HasFourButtons()
    {
        var c = new VampireCharacter { CharacterId = Guid.NewGuid() };
        var mc = VampireMoralityComponents.Build(c.CharacterId);
        var row = Assert.IsType<ActionRowComponent>(Assert.Single(mc.Components));
        Assert.Equal(4, row.Components.Count);
        Assert.All(row.Components, x => Assert.IsType<ButtonComponent>(x));
    }

    [Fact]
    public void Build_WithMenu_HasButtonsPlusSelect()
    {
        var c = new VampireCharacter { CharacterId = Guid.NewGuid() };
        var mc = VampireMoralityComponents.Build(c.CharacterId, withDerangementMenu: true);
        // Discord.Net при наличии SelectMenu выносит его в отдельный ActionRow.
        Assert.Equal(2, mc.Components.Count);
        var rows = mc.Components.OfType<ActionRowComponent>().ToList();
        Assert.Equal(2, rows.Count);
        // С меню расстройств: 3 кнопки (Conscience / Remove / Close) + SelectMenu.
        Assert.Equal(3, rows[0].Components.OfType<ButtonComponent>().Count());
        var select = Assert.Single(rows[1].Components.OfType<SelectMenuComponent>());
        Assert.Equal(10, select.Options.Count);
    }

    [Fact]
    public void Build_EmptyGuid_Throws()
    {
        Assert.Throws<ArgumentException>(() => VampireMoralityComponents.Build(Guid.Empty));
    }

    [Theory]
    [InlineData(MoralityAction.Close)]
    [InlineData(MoralityAction.StartAddDerangement)]
    [InlineData(MoralityAction.RemoveLastDerangement)]
    [InlineData(MoralityAction.ConscienceCheck)]
    public void BuildId_Roundtrips(MoralityAction action)
    {
        var id = Guid.NewGuid();
        var cid = VampireMoralityComponents.BuildId(action, id);
        Assert.True(VampireMoralityComponents.TryParse(cid, out var parsedAction, out var parsedId));
        Assert.Equal(action, parsedAction);
        Assert.Equal(id, parsedId);
    }

    [Fact]
    public void TryParse_SelectMenu_RejectedByButtonParser()
    {
        var id = Guid.NewGuid();
        var cid = VampireMoralityComponents.BuildSelectId(id);
        Assert.False(VampireMoralityComponents.TryParse(cid, out _, out _));
        Assert.Equal(id, VampireMoralityComponents.TryParseSelectedMenu(cid));
    }

    [Fact]
    public void TryParseSelectedMenu_NonSelect_ReturnsNull()
    {
        var id = Guid.NewGuid();
        var cid = VampireMoralityComponents.BuildId(MoralityAction.Close, id);
        Assert.Null(VampireMoralityComponents.TryParseSelectedMenu(cid));
    }

    [Theory]
    [InlineData("")]
    [InlineData("vtm_btn:close:abc")]
    [InlineData("vtm_moral:notanaction:abc123")]
    [InlineData("vtm_moral:close:")]
    [InlineData("vtm_moral:close:notaguid")]
    [InlineData("vtm_moral:sel:notaguid")]
    public void TryParse_ReturnsFalse_ForForeignInputs(string cid)
    {
        Assert.False(VampireMoralityComponents.TryParse(cid, out _, out _));
        Assert.Null(VampireMoralityComponents.TryParseSelectedMenu(cid));
    }

    [Fact]
    public void IsOurs_OnlyOurPrefix()
    {
        Assert.True(VampireMoralityComponents.IsOurs("vtm_moral:close:abc"));
        Assert.False(VampireMoralityComponents.IsOurs("vtm_btn:close:abc"));
        Assert.False(VampireMoralityComponents.IsOurs(""));
    }
}
