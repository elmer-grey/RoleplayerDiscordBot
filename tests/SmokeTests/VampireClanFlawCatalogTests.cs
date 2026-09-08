using System.Linq;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Покрывает файловые операции над словарём клановых изъянов VtM V20.
/// </summary>
[Collection("BotConfig")]
public class VampireClanFlawCatalogTests : IsolatedDataTestBase
{
    public VampireClanFlawCatalogTests() : base("vtm_clan_flaw_catalog")
    {
        VampireClanFlawCatalog.EnsureSeeded();
    }

    [Fact]
    public void All_ContainsExactlyFourteenClans()
    {
        // V20: 13 кланов + Каитиф (безклановый).
        Assert.Equal(14, VampireClanFlawCatalog.All().Count);
    }

    [Fact]
    public void All_HasUniqueIds()
    {
        var ids = VampireClanFlawCatalog.All().Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Theory]
    [InlineData("Ассамит")]
    [InlineData("Бруха")]
    [InlineData("Вентру")]
    [InlineData("Гангрел")]
    [InlineData("Джованни")]
    [InlineData("Каитиф")]
    [InlineData("Ласомбра")]
    [InlineData("Малкавиан")]
    [InlineData("Носферату")]
    [InlineData("Последователь Сета")]
    [InlineData("Равнос")]
    [InlineData("Тореадор")]
    [InlineData("Тремер")]
    [InlineData("Цимисхи")]
    public void FindByName_ReturnsKnownClan(string clan)
    {
        Assert.NotNull(VampireClanFlawCatalog.FindByName(clan));
    }

    [Fact]
    public void FindByName_IsCaseInsensitive()
    {
        Assert.NotNull(VampireClanFlawCatalog.FindByName("ТРЕМЕР"));
        Assert.NotNull(VampireClanFlawCatalog.FindByName("вентру"));
        Assert.Null(VampireClanFlawCatalog.FindByName("nonexistent"));
        Assert.Null(VampireClanFlawCatalog.FindByName(""));
        Assert.Null(VampireClanFlawCatalog.FindByName(null));
    }

    [Fact]
    public void GetClanFlawLong_ReturnsNonEmptyForKnown()
    {
        var desc = VampireClanFlawCatalog.GetClanFlawLong("Тремер");
        Assert.False(string.IsNullOrWhiteSpace(desc));
        // Полный текст должен быть ощутимо длиннее краткого.
        Assert.True(desc.Length > 50);
    }

    [Fact]
    public void GetClanFlawLong_Unknown_ReturnsEmpty()
    {
        Assert.Equal("", VampireClanFlawCatalog.GetClanFlawLong("Неизвестный"));
    }

    [Fact]
    public void EveryEntry_HasFlawLongAndShort()
    {
        foreach (var c in VampireClanFlawCatalog.All())
        {
            Assert.False(string.IsNullOrWhiteSpace(c.NameRu), $"Клан без имени (id={c.Id})");
            Assert.False(string.IsNullOrWhiteSpace(c.FlawShort), $"Клан {c.NameRu}: FlawShort пуст");
            Assert.False(string.IsNullOrWhiteSpace(c.FlawLong), $"Клан {c.NameRu}: FlawLong пуст");
        }
    }
}
