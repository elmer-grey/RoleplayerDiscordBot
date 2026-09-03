using System;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты для <see cref="VampireClanEmbed"/>.
/// </summary>
public class VampireClanEmbedTests
{
    [Fact]
    public void Build_NullCharacter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => VampireClanEmbed.Build(null!));
    }

    [Fact]
    public void Build_EmptyClan_ReturnsNull()
    {
        var c = new VampireCharacter { CharacterName = "X", Clan = "" };
        Assert.Null(VampireClanEmbed.Build(c));
    }

    [Fact]
    public void Build_WithClan_TitleContainsClanName()
    {
        var c = new VampireCharacter { CharacterName = "X", Clan = "Бруха" };
        var embed = VampireClanEmbed.Build(c);
        Assert.NotNull(embed);
        Assert.Contains("Бруха", embed!.Title);
    }

    [Fact]
    public void Build_WithClan_HasDisciplinesField()
    {
        var c = new VampireCharacter { CharacterName = "X", Clan = "Бруха" };
        var embed = VampireClanEmbed.Build(c);
        Assert.NotNull(embed);
        var field = Assert.Single(embed!.Fields, f => f.Name.Contains("дисциплин"));
        // Название поля содержит список дисциплин (Value — поясняющий текст).
        Assert.Contains("Стремительность", field.Name);
        Assert.Contains("Мощь", field.Name);
        Assert.Contains("Величие", field.Name);
    }

    [Fact]
    public void Build_Caitiff_HasNoDisciplinesFieldButHasFlaw()
    {
        var c = new VampireCharacter { CharacterName = "X", Clan = "Каитиф" };
        var embed = VampireClanEmbed.Build(c);
        Assert.NotNull(embed);
        // У Каитифа нет клановых дисциплин — поле всё равно должно быть (с пояснением).
        Assert.Contains(embed!.Fields, f => f.Name.Contains("дисциплин"));
    }

    [Fact]
    public void Build_ClanFlaw_TruncatesToMaxFieldValue()
    {
        var c = new VampireCharacter { CharacterName = "X", Clan = "Бруха" };
        var embed = VampireClanEmbed.Build(c);
        Assert.NotNull(embed);
        var flaw = Assert.Single(embed!.Fields, f => f.Name.Contains("полностью"));
        Assert.True(flaw.Value.Length <= VampireClanEmbed.MaxFieldValue);
    }
}
