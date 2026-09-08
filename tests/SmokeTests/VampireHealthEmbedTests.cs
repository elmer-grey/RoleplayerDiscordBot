using System;
using System.Linq;
using Discord;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

public class VampireHealthEmbedTests
{
    private static VampireCharacter WithHealth(int size = 7, params Action<HealthState>[] steps)
    {
        var c = new VampireCharacter
        {
            PlayerName = "Андрей",
            CharacterName = "Виктор",
            CharacterId = Guid.NewGuid(),
        };
        c.Health = new HealthState(size);
        foreach (var s in steps) s(c.Health);
        return c;
    }

    private static string AllText(Embed embed) =>
        (embed.Description ?? "") + " " + string.Join("|", embed.Fields.Select(f => (f.Name ?? "") + "=" + (f.Value ?? "")));

    [Fact]
    public void Build_NullHealth_ShowsPlaceholder()
    {
        var c = new VampireCharacter { CharacterId = Guid.NewGuid() };
        var text = AllText(VampireHealthEmbed.Build(c));
        Assert.Contains("шкала не задана", text);
    }

    [Fact]
    public void Build_FullHealth_NoIncapacitatedShown()
    {
        var c = WithHealth();
        var text = AllText(VampireHealthEmbed.Build(c));
        Assert.DoesNotContain("Небоеспособен", text);
    }

    [Fact]
    public void Build_DamagedHealth_ShowsPenalty()
    {
            var c = WithHealth(size: 7, h => h.ApplyAggravated(2));
        var text = AllText(VampireHealthEmbed.Build(c));
        Assert.Contains("Состояние", text);
        Assert.Contains("Штраф", text);
    }

    [Fact]
    public void Build_Incapacitated_OverridesPenalty()
    {
        var c = WithHealth(size: 7, h => h.ApplyAggravated(10));
        var text = AllText(VampireHealthEmbed.Build(c));
        Assert.Contains("Небоеспособен", text);
    }

    [Fact]
    public void Build_AlwaysContainsTrackField()
    {
        var c = WithHealth();
        var embed = VampireHealthEmbed.Build(c);
        Assert.Contains(embed.Fields, f => f.Name == "Шкала");
    }

    [Fact]
    public void Components_HasFourButtons()
    {
        var c = WithHealth();
        var mc = VampireHealthEmbed.Components(c);
        var row = Assert.IsType<ActionRowComponent>(Assert.Single(mc.Components));
        Assert.Equal(4, row.Components.OfType<ButtonComponent>().Count());
    }

    [Fact]
    public void Components_RequiresCharacterId()
    {
        var c = WithHealth();
        c.CharacterId = Guid.Empty;
        Assert.Throws<ArgumentException>(() => VampireHealthEmbed.Components(c));
    }

    [Fact]
    public void Components_ParseRoundtrip()
    {
        var c = WithHealth();
        var mc = VampireHealthEmbed.Components(c);
        foreach (var b in mc.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>())
        {
            Assert.True(VampireHealthComponents.TryParse(b.CustomId!, out _, out var id));
            Assert.Equal(c.CharacterId, id);
        }
    }

    [Fact]
    public void Build_NotExceedDiscordLimit()
    {
        var c = WithHealth(size: 7, h => h.ApplyAggravated(10));
        var embed = VampireHealthEmbed.Build(c);
        Assert.True(embed.Length <= 6000, $"embed слишком длинный: {embed.Length}");
    }

    [Fact]
    public void Build_NullCharacter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => VampireHealthEmbed.Build(null!));
    }
}
