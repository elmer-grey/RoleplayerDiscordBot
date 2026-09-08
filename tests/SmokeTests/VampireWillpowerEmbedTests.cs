using System;
using System.Linq;
using Discord;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

public class VampireWillpowerEmbedTests
{
    private static VampireCharacter NewCharacter(int willpower = 5, int points = 5, bool spent = false) =>
        new VampireCharacter
        {
            PlayerName = "Андрей",
            CharacterName = "Виктор",
            CharacterId = Guid.NewGuid(),
            Willpower = willpower,
            WillpowerPoints = points,
            WillpowerSpentThisTurn = spent,
        };

    private static string AllText(Embed embed) =>
        (embed.Description ?? "") + " " + string.Join("|", embed.Fields.Select(f => (f.Name ?? "") + "=" + (f.Value ?? "")));

    [Fact]
    public void Build_FullWillpower_ShowsAllFilledDots()
    {
        var c = NewCharacter(5, 5);
        var text = AllText(VampireWillpowerEmbed.Build(c));
        Assert.Contains("●●●●●", text);
        Assert.DoesNotContain("○", text);
    }

    [Fact]
    public void Build_EmptyWillpower_ShowsAllEmptyDots()
    {
        var c = NewCharacter(5, 0);
        var text = AllText(VampireWillpowerEmbed.Build(c));
        Assert.Contains("○○○○○", text);
        Assert.DoesNotContain("●", text);
    }

    [Fact]
    public void Build_PartialWillpower_FillsFromLeft()
    {
        var c = NewCharacter(7, 3);
        var text = AllText(VampireWillpowerEmbed.Build(c));
        Assert.Contains("●●●○○○○", text);
    }

    [Fact]
    public void Build_OvercapPoints_AreClampedToCeiling()
    {
        var c = NewCharacter(5, 8);
        var text = AllText(VampireWillpowerEmbed.Build(c));
        Assert.Contains("●●●●●", text);
    }

    [Fact]
    public void Build_SpentFlag_TriggersNote()
    {
        var c = NewCharacter(5, 3, spent: true);
        var text = AllText(VampireWillpowerEmbed.Build(c));
        Assert.Contains("уже использовался", text);
        Assert.Contains("Сопротивление", text);
    }

    [Fact]
    public void Components_HasTwoButtons()
    {
        var c = NewCharacter();
        var mc = VampireWillpowerEmbed.Components(c);
        var row = Assert.IsType<ActionRowComponent>(Assert.Single(mc.Components));
            Assert.Equal(2, row.Components.OfType<ButtonComponent>().Count());
    }

    [Fact]
    public void Components_ParseRoundtrip()
    {
        var id = Guid.NewGuid();
        var mc = VampireWillpowerEmbed.Components(new VampireCharacter
        {
            CharacterName = "X",
            CharacterId = id,
            Willpower = 5,
            WillpowerPoints = 5,
        });
        foreach (var comp in mc.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>())
        {
            Assert.True(VampireWillpowerComponents.TryParse(comp.CustomId!, out _, out var parsedId));
            Assert.Equal(id, parsedId);
        }
    }

    [Fact]
    public void Components_RequiresCharacterId()
    {
        var c = NewCharacter();
        c.CharacterId = Guid.Empty;
        Assert.Throws<ArgumentException>(() => VampireWillpowerEmbed.Components(c));
    }

    [Fact]
    public void Build_NullCharacter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => VampireWillpowerEmbed.Build(null!));
    }

    [Fact]
    public void Build_NotExceedDiscordLimit()
    {
        var c = NewCharacter(10, 10);
        var embed = VampireWillpowerEmbed.Build(c);
        Assert.True(embed.Length <= 6000, $"embed слишком длинный: {embed.Length}");
    }
}
