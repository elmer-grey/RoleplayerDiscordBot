using System;
using System.Linq;
using Discord;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

public class VampireSheetComponentsTests
{
    private static VampireCharacter NewCharacter(Guid? id = null) => new VampireCharacter
    {
        PlayerName = "Андрей",
        CharacterName = "Виктор",
        CharacterId = id ?? Guid.NewGuid(),
        Hunger = 2,
        Willpower = 5,
    };

    private static (ActionRowComponent row, ButtonComponent[] buttons) Unwrap(MessageComponent mc)
    {
        var row = Assert.IsType<ActionRowComponent>(Assert.Single(mc.Components));
        var buttons = row.Components.OfType<ButtonComponent>().ToArray();
        return (row, buttons);
    }

    [Fact]
    public void Build_FiveCoreButtonsInOneRow()
    {
        var c = NewCharacter();
        var mc = VampireSheetComponents.Build(c);
        var (_, buttons) = Unwrap(mc);
        Assert.Equal(5, buttons.Length);
    }

    [Fact]
    public void Build_CoreButtonsArePrimaryExceptClanAndMorality()
    {
        var c = NewCharacter();
        var mc = VampireSheetComponents.Build(c);
        var (_, buttons) = Unwrap(mc);
        Assert.All(buttons, b =>
        {
            if (b.Label == "Клан" || b.Label == "Мораль")
                Assert.Equal(ButtonStyle.Secondary, b.Style);
            else
                Assert.Equal(ButtonStyle.Primary, b.Style);
        });
    }

    [Fact]
    public void Build_RequiresCharacterId()
    {
        var c = NewCharacter();
        c.CharacterId = Guid.Empty;
        Assert.Throws<ArgumentException>(() => VampireSheetComponents.Build(c));
    }

    [Fact]
    public void Build_NullCharacter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => VampireSheetComponents.Build(null!));
    }

    [Fact]
    public void Parse_CustomIds_BackToActions()
    {
        var c = NewCharacter();
        var mc = VampireSheetComponents.Build(c);
        var (_, buttons) = Unwrap(mc);
        foreach (var b in buttons)
        {
            Assert.True(VampireSheetComponents.TryParse(b.CustomId!, out var action, out var id));
            Assert.Equal(c.CharacterId, id);
            Assert.True(action is VampireSheetAction.Description
                              or VampireSheetAction.Willpower
                              or VampireSheetAction.Health
                              or VampireSheetAction.Clan
                              or VampireSheetAction.Morality
                              or VampireSheetAction.Experience);
        }
    }

    [Fact]
    public void Parse_Roundtrip_PreservesActionAndId()
    {
        var id = Guid.NewGuid();
        var cid = VampireSheetComponents.BuildCustomId(VampireSheetAction.Willpower, id);
        Assert.True(VampireSheetComponents.TryParse(cid, out var action, out var parsedId));
        Assert.Equal(VampireSheetAction.Willpower, action);
        Assert.Equal(id, parsedId);
    }

    [Fact]
    public void Parse_ForeignCustomId_ReturnsFalse()
    {
        Assert.False(VampireSheetComponents.TryParse("foo:bar:baz", out _, out _));
        Assert.False(VampireSheetComponents.TryParse("", out _, out _));
        Assert.False(VampireSheetComponents.TryParse("vam_reroll:1:123", out _, out _));
    }

    [Fact]
    public void IsOurButton_OnlyTrueForOwnPrefix()
    {
        Assert.True(VampireSheetComponents.IsOurButton("vtm_btn:desc:abc"));
        Assert.False(VampireSheetComponents.IsOurButton("vam_reroll:1:123"));
        Assert.False(VampireSheetComponents.IsOurButton(""));
    }

    [Fact]
    public void Build_WithExperienceButton_AddsSecondRow()
    {
        var c = NewCharacter();
        var mc = VampireSheetComponents.Build(c, showExperienceButton: true);
        var rows = mc.Components.OfType<ActionRowComponent>().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(5, rows[0].Components.OfType<ButtonComponent>().Count());
        var xpButton = Assert.Single(rows[1].Components.OfType<ButtonComponent>());
        Assert.Equal("Опыт", xpButton.Label);
        Assert.Equal(ButtonStyle.Success, xpButton.Style);
    }

    [Fact]
    public void Build_WithoutExperienceButton_HasOneRow()
    {
        var c = NewCharacter();
        var mc = VampireSheetComponents.Build(c);
        Assert.Single(mc.Components);
    }

    [Fact]
    public void Build_XpCustomId_ParsesToExperienceAction()
    {
        var id = Guid.NewGuid();
        var cid = VampireSheetComponents.BuildCustomId(VampireSheetAction.Experience, id);
        Assert.True(VampireSheetComponents.TryParse(cid, out var action, out var parsed));
        Assert.Equal(VampireSheetAction.Experience, action);
        Assert.Equal(id, parsed);
    }
}
