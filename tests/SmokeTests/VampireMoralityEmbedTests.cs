using RPBot.VtM;
using Xunit;

namespace SmokeTests;

public class VampireMoralityEmbedTests
{
    [Fact]
    public void BuildBlock_HumanityOnly_NoPathNoDerangements()
    {
        var c = new VampireCharacter { Path = "", Humanity = 7 };
        var s = VampireMoralityEmbed.BuildBlock(c);
        Assert.Contains("Человечность", s);
        Assert.DoesNotContain("**Значение:**", s);
        Assert.Contains("Расстройства", s);
        Assert.Contains("нет", s);
    }

    [Fact]
    public void BuildBlock_WithPath_ShowsPathAndRating()
    {
        var c = new VampireCharacter { Path = "Путь Каина", PathRating = 6, Humanity = 8 };
        var s = VampireMoralityEmbed.BuildBlock(c);
        Assert.Contains("Путь", s);
        Assert.Contains("Путь Каина", s);
        Assert.Contains("6", s);
    }

    [Fact]
    public void BuildBlock_WithDerangements_ListsAll()
    {
        var c = new VampireCharacter
        {
            Humanity = 5,
            Derangements = new System.Collections.Generic.List<string>
            {
                "Паранойя",
                "Мегаломания",
            },
        };
        var s = VampireMoralityEmbed.BuildBlock(c);
        Assert.Contains("Паранойя", s);
        Assert.Contains("Мегаломания", s);
    }

    [Fact]
    public void BuildBlock_WithDerangement_ListsIt()
    {
        var c = new VampireCharacter
        {
            Humanity = 5,
            Derangements = new System.Collections.Generic.List<string> { "Истерия" },
        };
        var s = VampireMoralityEmbed.BuildBlock(c);
        Assert.Contains("Истерия", s);
    }
}
