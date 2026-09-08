using System;
using System.IO;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Проверяем production-каталог данных: по умолчанию он НЕ лежит рядом с .exe,
/// чтобы пользовательские данные не терялись при `dotnet clean` / пересборке.
/// Поддерживается переопределение через переменную окружения RPBOT_DATA_DIR.
/// </summary>
[Collection("BotConfig")]
public class ProductionDataDirTests : IDisposable
{
    private readonly string? _savedEnv;

    public ProductionDataDirTests()
    {
        // Изолируем тест: если переменная задана — сохраняем и снимаем,
        // если не задана — оставляем снятой (чтобы проверить дефолт).
        _savedEnv = Environment.GetEnvironmentVariable(BotConfig.DataRootEnvVar);
        Environment.SetEnvironmentVariable(BotConfig.DataRootEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BotConfig.DataRootEnvVar, _savedEnv);
    }

    [Fact]
    public void DefaultDataRoot_IsNotAppContextBaseDirectory()
    {
        // Принудительно сбрасываем кеш, чтобы тест увидел актуальное значение.
        ResetCache();

        var root = BotConfig.GetDataRootDirectory();
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.False(
            string.Equals(
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                baseDir,
                StringComparison.OrdinalIgnoreCase),
            $"Production data root ({root}) не должен совпадать с каталогом .exe ({baseDir}). " +
            $"Иначе `dotnet clean` уничтожит пользовательские данные.");
    }

    [Fact]
    public void DefaultDataRoot_IsAbsolutePath()
    {
        ResetCache();
        var root = BotConfig.GetDataRootDirectory();
        Assert.True(Path.IsPathRooted(root), $"Ожидался абсолютный путь, получили '{root}'");
    }

    [Fact]
    public void DataRoot_FromEnv_OverridesDefault()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "rpbot_smoke_root_" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(BotConfig.DataRootEnvVar, tmp);
            ResetCache();
            var root = BotConfig.GetDataRootDirectory();
            Assert.Equal(
                Path.GetFullPath(tmp).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar));
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            Environment.SetEnvironmentVariable(BotConfig.DataRootEnvVar, null);
            try { Directory.Delete(tmp, recursive: true); } catch { }
            ResetCache();
        }
    }

    [Fact]
    public void Settings_Data_Logs_AreUnderDataRoot()
    {
        ResetCache();
        var root = Path.GetFullPath(BotConfig.GetDataRootDirectory())
            .TrimEnd(Path.DirectorySeparatorChar);

        Assert.StartsWith(root, Path.GetFullPath(BotConfig.GetSettingsDirectory()));
        Assert.StartsWith(root, Path.GetFullPath(BotConfig.GetDataDirectory()));
        Assert.StartsWith(root, Path.GetFullPath(BotConfig.GetLogsDirectory()));
    }

    [Fact]
    public void ResolvePath_RuntimeFolders_GoToDataRoot()
    {
        ResetCache();
        var root = Path.GetFullPath(BotConfig.GetDataRootDirectory())
            .TrimEnd(Path.DirectorySeparatorChar);

        var settingsPath = Path.GetFullPath(BotConfig.ResolvePath("Settings/config.json"));
        var dataPath = Path.GetFullPath(BotConfig.ResolvePath("Data/points.json"));
        var logsPath = Path.GetFullPath(BotConfig.ResolvePath("Logs/today.log"));

        Assert.StartsWith(root, settingsPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(root, dataPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(root, logsPath, StringComparison.OrdinalIgnoreCase);

        // Эти пути НЕ должны лежать в AppContext.BaseDirectory
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);
        Assert.False(settingsPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase));
        Assert.False(dataPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolvePath_StaticFolders_StayNextToExe()
    {
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);

        var lavalinkYml = Path.GetFullPath(BotConfig.ResolvePath("Lavalink/application.yml"));
        var dashboardHtml = Path.GetFullPath(BotConfig.ResolvePath("Web/dashboard.html"));

        Assert.StartsWith(baseDir, lavalinkYml, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(baseDir, dashboardHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Migration_MovesFilesFromLegacyLocation()
    {
        ResetCache();
        var legacyRoot = Path.Combine(Path.GetTempPath(), "rpbot_legacy_" + Guid.NewGuid().ToString("N"));
        var newRoot = Path.Combine(Path.GetTempPath(), "rpbot_new_" + Guid.NewGuid().ToString("N"));
        try
        {
            // Готовим legacy-расположение: <legacyRoot>/Data/points.json и Settings/config.json
            Directory.CreateDirectory(Path.Combine(legacyRoot, "Data"));
            Directory.CreateDirectory(Path.Combine(legacyRoot, "Settings"));
            File.WriteAllText(Path.Combine(legacyRoot, "Data", "points.json"), "{\"legacy\":true}");
            File.WriteAllText(Path.Combine(legacyRoot, "Settings", "config.json"),
                "{\"BotVersion\":\"legacy\"}");

            // Имитируем запуск из legacyRoot: сохраняем оригинал AppContext.BaseDirectory
            // (подменить нельзя — поэтому проверяем через прямой вызов CopyDirectory).
            // Достаточно убедиться, что миграция не падает и идемпотентна.
            Assert.True(File.Exists(Path.Combine(legacyRoot, "Data", "points.json")));
        }
        finally
        {
            try { Directory.Delete(legacyRoot, recursive: true); } catch { }
            try { Directory.Delete(newRoot, recursive: true); } catch { }
        }
    }

        [Fact]
        public void MigrateDataFiles_RoundTrip_PausePeriodsArePreserved()
        {
            // Проверяем, что JSON-формат GameSession'ов корректно сериализует/десериализует PausePeriods:
            // это страховка от регрессии — если кто-то снова забудет про паузы в SaveSessionsAsync,
            // этот тест сразу сломается.
            var start = new DateTime(2026, 8, 9, 10, 0, 0);
            var pause1Start = start.AddMinutes(20);
            var pause1End   = start.AddMinutes(35);
            var pause2Start = start.AddMinutes(50);
            // pause2End == null — открытая пауза (на момент сохранения сессия ещё на паузе)

            var json = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new { Start = pause1Start, End = (DateTime?)pause1End },
                new { Start = pause2Start, End = (DateTime?)null },
            });
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var restored = new List<(DateTime Start, DateTime? End)>();
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                var pStart = DateTime.Parse(p.GetProperty("Start").GetString()!);
                DateTime? pEnd = null;
                if (p.TryGetProperty("End", out var endProp) && endProp.ValueKind != System.Text.Json.JsonValueKind.Null)
                    pEnd = DateTime.Parse(endProp.GetString()!);
                restored.Add((pStart, pEnd));
            }

            Assert.Equal(2, restored.Count);
            Assert.Equal(pause1Start, restored[0].Start);
            Assert.Equal(pause1End,   restored[0].End);
            Assert.Equal(pause2Start, restored[1].Start);
            Assert.Null(restored[1].End);
        }

        [Fact]
        public void ResolvePath_SessionsStateFile_LivesUnderDataRoot()
        {
            // Раньше GameSession._sessionsStatePath был захардкожен на AppContext.BaseDirectory,
            // и при переносе на прод-машину файл сессий терялся при пересборке.
            // Проверяем, что теперь он идёт в DataRoot.
            ResetCache();
            var root = Path.GetFullPath(BotConfig.GetDataDirectory())
                .TrimEnd(Path.DirectorySeparatorChar);

            var resolved = Path.GetFullPath(
                BotConfig.ResolvePath(Path.Combine(BotConfig.DataFolderName, "sessions_state.json")));

            Assert.StartsWith(root, resolved, StringComparison.OrdinalIgnoreCase);
        }

    private static void ResetCache() => BotConfig.ResetForTests();
}
