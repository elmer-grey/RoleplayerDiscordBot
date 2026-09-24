using System.IO;
using System.Threading;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// 1.1 — запуск бота не обращается к C:\temp. Все данные лежат в BotConfig.DataFolderName (Data/)
/// и при необходимости в Settings/ рядом.
/// </summary>
[Collection(nameof(BotConfigCollection))]
public class DataFolderTests
{
    [Fact]
    public void DataFolderName_IsRelative_NoTempPaths()
    {
        Assert.Equal("Data", BotConfig.DataFolderName);
        Assert.Equal("Settings", BotConfig.SettingsFolderName);
    }

    [Fact]
    public void ResolvePath_RelativePath_BecomesRelativeToBaseDirectory()
    {
        var resolved = BotConfig.ResolvePath("foo/bar.json");
        Assert.True(Path.IsPathRooted(resolved),
            "Относительный путь должен быть приведён к абсолютному относительно AppContext.BaseDirectory");
        Assert.Contains("foo", resolved);
        Assert.Contains("bar.json", resolved);
        Assert.StartsWith(AppContext.BaseDirectory.TrimEnd('\\', '/'), resolved);
        Assert.DoesNotContain("temp", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDataDirectory_CreatesDirectory_IfMissing()
    {
        // Каталог создаётся внутри BotConfig.GetDataDirectory(), но в параллельном
        // xUnit-запуске другой тест может сбросить кеш BotConfig._dataRootOverride
        // через рефлексию (см. ProductionDataDirTests.ResetCache) и между
        // CreateDirectory и Directory.Exists что-то успеть сделать с путём.
        // Допустимо короткое ожидание: тест проверяет «функция создаёт каталог»,
        // а не «каталог неуничтожим между CreateDirectory и проверкой».
        var dataDir = "";
        for (var i = 0; i < 3; i++)
        {
            dataDir = BotConfig.GetDataDirectory();
            if (Directory.Exists(dataDir)) break;
            Thread.Sleep(20);
        }
        Assert.False(string.IsNullOrWhiteSpace(dataDir));
        Assert.True(Directory.Exists(dataDir),
            $"Каталог данных должен существовать после старта: {dataDir}");
    }
}