using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Обложка события → столбец K таблицы (формула =IMAGE), описание события →
/// примечание этой же ячейки. Проверяются чистые функции GoogleSheetsService:
/// сборка формулы и санитизация текста примечания.
/// </summary>
public class GoogleSheetsEventDetailsTests
{
    private static string BuildFormula(string url) =>
        (string?)typeof(RPBot.GoogleSheetsService).GetMethod("BuildImageFormula",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public)!
            .Invoke(null, new object[] { url ?? "" }) ?? "";

    private static string SanitizeNote(string? text) =>
        (string?)typeof(RPBot.GoogleSheetsService).GetMethod("SanitizeSessionNote",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public)!
            .Invoke(null, new object[] { text }) ?? "";

    [Fact]
    public void ImageFormula_RegularUrl_Wrapped()
    {
        var result = BuildFormula("https://cdn.discordapp.com/guild-events/123/abc.png");

        Assert.Equal("=IMAGE(\"https://cdn.discordapp.com/guild-events/123/abc.png\")", result);
    }

    [Fact]
    public void ImageFormula_UrlWithQuotesAndLineBreaks_Cleaned()
    {
        // Кавычки/переводы строк в URL сломали бы формулу — вырезаются.
        var result = BuildFormula("https://cdn.example/ab\"c.png\nextra");

        Assert.Equal("=IMAGE(\"https://cdn.example/abc.pngextra\")", result);
    }

    [Fact]
    public void Note_NormalText_Trims()
    {
        var result = SanitizeNote("  Чёрная паутина затянет мир.\r\nВторая строка. ");

        Assert.Equal("Чёрная паутина затянет мир.\nВторая строка.", result);
    }

    [Fact]
    public void Note_LongText_TruncatedWithEllipsis()
    {
        var result = SanitizeNote(new string('а', 4001));

        Assert.Equal(4001, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void Note_NullOrEmpty_Empty()
    {
        Assert.Equal("", SanitizeNote(null));
        Assert.Equal("", SanitizeNote("   "));
    }
}