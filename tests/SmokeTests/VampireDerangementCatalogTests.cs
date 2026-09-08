using System.Linq;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

[Collection("BotConfig")]
public class VampireDerangementCatalogTests : IsolatedDataTestBase
{
    public VampireDerangementCatalogTests() : base("vtm_derangements_catalog")
    {
        // Гарантируем, что файл словаря расстройств скопирован из
        // embedded-ресурса при первом запуске тестов в чистом окружении.
        VampireDerangementCatalog.EnsureSeeded();
    }

    [Theory]
    [InlineData("Биполярное расстройство")]
    [InlineData("Булимия")]
    [InlineData("Диссоциативная фуга")]
    [InlineData("Истерия")]
    [InlineData("Кровавый анимизм")]
    [InlineData("Мегаломания")]
    [InlineData("Обсессивно-компульсивное расстройство")]
    [InlineData("Паранойя")]
    [InlineData("Расстройство идентичности")]
    [InlineData("Шизофрения")]
    public void IsKnown_AcceptsAllTen(string name)
    {
        Assert.True(VampireDerangementCatalog.IsKnown(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Нарциссизм")]
    [InlineData("неизвестно")]
    [InlineData("Bipolar Disorder")]
    public void IsKnown_RejectsUnknown(string name)
    {
        Assert.False(VampireDerangementCatalog.IsKnown(name));
    }

    [Fact]
    public void All_ContainsExactlyTenEntries()
    {
        Assert.Equal(10, VampireDerangementCatalog.All().Count);
    }

    [Fact]
    public void All_HasUniqueNames()
    {
        var names = VampireDerangementCatalog.All().Select(d => d.NameRu).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Describe_KnownDerangement_ReturnsNonEmpty()
    {
        var desc = VampireDerangementCatalog.Describe("Паранойя");
        Assert.False(string.IsNullOrWhiteSpace(desc));
    }

    [Fact]
    public void Describe_UnknownDerangement_ReturnsEmpty()
    {
        Assert.Equal("", VampireDerangementCatalog.Describe("Неизвестное"));
    }

    [Fact]
    public void EveryKnown_DerangementHasSummaryAndEffect()
    {
        foreach (var d in VampireDerangementCatalog.All())
        {
            Assert.False(string.IsNullOrWhiteSpace(d.NameRu), "Имя расстройства не должно быть пустым");
            Assert.False(string.IsNullOrWhiteSpace(d.Summary), $"Расстройство {d.NameRu}: Summary пустое");
            Assert.False(string.IsNullOrWhiteSpace(d.Effect), $"Расстройство {d.NameRu}: Effect пустое");
        }
    }
}
