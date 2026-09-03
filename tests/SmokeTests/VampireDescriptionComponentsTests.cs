using System;
using System.Linq;
using Discord;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

public class VampireDescriptionComponentsTests
{
    private static VampireCharacter NewCharacter() => new VampireCharacter
    {
        CharacterName = "Виктор",
        CharacterId = Guid.NewGuid(),
    };

    private static (ActionRowComponent row, ButtonComponent[] buttons) Unwrap(MessageComponent mc)
    {
        var row = Assert.IsType<ActionRowComponent>(Assert.Single(mc.Components));
        var buttons = row.Components.OfType<ButtonComponent>().ToArray();
        return (row, buttons);
    }

    [Fact]
    public void Build_TwoButtonsInOneRow()
    {
        var c = NewCharacter();
        var (_, buttons) = Unwrap(VampireDescriptionComponents.Build(c));
        Assert.Equal(2, buttons.Length);
    }

    [Fact]
    public void Build_EditIsPrimary_CloseIsSecondary()
    {
        var c = NewCharacter();
        var (_, buttons) = Unwrap(VampireDescriptionComponents.Build(c));
        Assert.Equal(ButtonStyle.Primary, buttons[0].Style);
        Assert.Equal(ButtonStyle.Secondary, buttons[1].Style);
    }

    [Fact]
    public void Build_RequiresCharacterId()
    {
        var c = NewCharacter();
        c.CharacterId = Guid.Empty;
        Assert.Throws<ArgumentException>(() => VampireDescriptionComponents.Build(c));
    }

    [Fact]
    public void Build_NullCharacter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => VampireDescriptionComponents.Build(null!));
    }

    [Fact]
    public void Parse_Roundtrip_PreservesActionAndId()
    {
        var id = Guid.NewGuid();
        var cid = VampireDescriptionComponents.BuildId(VampireDescriptionComponents.DescriptionAction.Edit, id);
        Assert.True(VampireDescriptionComponents.TryParse(cid, out var action, out var parsedId));
        Assert.Equal(VampireDescriptionComponents.DescriptionAction.Edit, action);
        Assert.Equal(id, parsedId);
    }

    [Fact]
    public void Parse_Invalid_ReturnsFalse()
    {
        Assert.False(VampireDescriptionComponents.TryParse("foo", out _, out _));
        Assert.False(VampireDescriptionComponents.TryParse("vtm_desc:notanumber:abc", out _, out _));
        Assert.False(VampireDescriptionComponents.TryParse("vtm_desc:edit:00000000-0000-0000-0000-000000000000", out _, out _));
    }

    [Fact]
    public void IsOurButton_OnlyTrueForOwnPrefix()
    {
        Assert.True(VampireDescriptionComponents.IsOurButton("vtm_desc:edit:abc"));
        Assert.False(VampireDescriptionComponents.IsOurButton("vam_reroll:1:2"));
        Assert.False(VampireDescriptionComponents.IsOurButton(""));
    }
}
