using System;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты для <see cref="VampireDescriptionEmbed"/> — отдельный embed «Описание»
/// по кнопке под основным листом персонажа.
/// </summary>
public class VampireDescriptionEmbedTests
{
    private static VampireCharacter NewCharacter() => new VampireCharacter
    {
        PlayerName = "Андрей",
        CharacterName = "Виктор",
        Clan = "Гангрел",
        Generation = 11,
        Nature = "Судья",
        Bio = "Виктор из клана Гангрел, 11-е поколение. Одичавший охотник.",
        AvatarUrl = "https://example.com/avatar.png",
    };

    [Fact]
    public void Build_NullCharacter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => VampireDescriptionEmbed.Build(null!));
    }

    [Fact]
    public void Build_WithBioAndAvatar_ReturnsEmbed()
    {
        var embed = VampireDescriptionEmbed.Build(NewCharacter());
        Assert.NotNull(embed);
        Assert.Equal("Виктор", embed!.Title);
        Assert.Contains("Виктор из клана Гангрел", embed.Description);
        Assert.Equal("https://example.com/avatar.png", embed.Thumbnail?.Url);
    }

    [Fact]
    public void Build_ContainsIdentificationHeader()
    {
        var embed = VampireDescriptionEmbed.Build(NewCharacter());
        Assert.NotNull(embed);
        var header = embed!.Fields[0];
        Assert.Equal("════════ Идентификация ════════", header.Name);
        Assert.Contains("Андрей", header.Value.ToString()!);
        Assert.Contains("Гангрел", header.Value.ToString()!);
        Assert.Contains("11-е поколение", header.Value.ToString()!);
    }

    [Fact]
    public void Build_NoBioAndNoAvatar_ReturnsNull()
    {
        var c = new VampireCharacter { CharacterName = "Пустой" };
        Assert.Null(VampireDescriptionEmbed.Build(c));
    }

    [Fact]
    public void Build_BioOnly_NoAvatar_StillReturnsEmbed()
    {
        var c = NewCharacter();
        c.AvatarUrl = "";
        var embed = VampireDescriptionEmbed.Build(c);
        Assert.NotNull(embed);
        Assert.Null(embed!.Thumbnail);
        Assert.Contains("Виктор из клана Гангрел", embed.Description);
    }

    [Fact]
    public void Build_AvatarOnly_NoBio_StillReturnsEmbed()
    {
        var c = NewCharacter();
        c.Bio = "";
        var embed = VampireDescriptionEmbed.Build(c);
        Assert.NotNull(embed);
        Assert.Null(embed!.Description);
        Assert.Equal("https://example.com/avatar.png", embed.Thumbnail?.Url);
    }

    [Fact]
    public void Build_InvalidAvatar_TreatedAsNoAvatar()
    {
        var c = NewCharacter();
        c.AvatarUrl = "not-a-url";
        // Bio есть — embed всё равно вернётся.
        var embed = VampireDescriptionEmbed.Build(c);
        Assert.NotNull(embed);
        Assert.Null(embed!.Thumbnail);
    }

    [Fact]
    public void Build_Header_OmitsGenerationWhenDefault()
    {
        var c = NewCharacter();
        c.Generation = 13; // default новообращённого — не показываем
        var embed = VampireDescriptionEmbed.Build(c);
        var header = embed!.Fields[0];
        Assert.DoesNotContain("13-е поколение", header.Value.ToString()!);
    }

    [Fact]
    public void Build_Header_NoNature_StillValid()
    {
        var c = NewCharacter();
        c.Nature = "";
        var embed = VampireDescriptionEmbed.Build(c);
        Assert.NotNull(embed);
    }

    [Fact]
    public void Build_EmptyCharacterName_UsesPlaceholder()
    {
        var c = NewCharacter();
        c.CharacterName = "";
        c.PlayerName = "";
        var embed = VampireDescriptionEmbed.Build(c);
        Assert.NotNull(embed);
        Assert.Equal("Безымянный вампир", embed!.Title);
    }

    [Fact]
    public void Build_LongBio_TruncatedToLimit()
    {
        var c = NewCharacter();
        c.Bio = new string('ы', 5000);
        var embed = VampireDescriptionEmbed.Build(c);
        Assert.NotNull(embed);
        Assert.True(embed!.Description!.Length <= VampireDescriptionEmbed.MaxBioLength + 1);
    }

    [Fact]
    public void Build_HasFooter()
    {
        var embed = VampireDescriptionEmbed.Build(NewCharacter());
        Assert.NotNull(embed!.Footer);
        Assert.Contains("Описание", embed.Footer!.ToString());
    }
}
