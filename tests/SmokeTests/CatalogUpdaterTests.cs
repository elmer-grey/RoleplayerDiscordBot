using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты версионирования JSON-справочников VtM: при изменении поля
/// <c>schema_version</c> в embedded-ресурсе файл на диске должен
/// пересеиваться. Только upgrade (не merge, не downgrade).
/// </summary>
[Collection("BotConfig")]
public class CatalogUpdaterTests : IsolatedDataTestBase
{
    public CatalogUpdaterTests() : base("vtm_catalog_updater") { }

    /// <summary>
    /// Находит assembly, содержащую <see cref="CatalogUpdater"/> — там же
    /// лежат embedded-ресурсы справочников.
    /// </summary>
    private static Assembly RpbotAssembly()
        => typeof(CatalogUpdater).Assembly;

    /// <summary>
    /// Читает schema_version из embedded-ресурса RPBot.
    /// </summary>
    private static int? EmbeddedSchemaVersion(string resourceName)
    {
        using var stream = RpbotAssembly().GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.TryGetProperty("schema_version", out var v)
               && v.TryGetInt32(out var n) ? n : (int?)null;
    }

    /// <summary>
    /// Вызов <see cref="CatalogUpdater.UpgradeIfStaleAsync"/> с явной
    /// передачей RPBot assembly — чтобы тест работал из SmokeTests.dll.
    /// </summary>
    private static CatalogUpdater.UpgradeResult Run(string path, string resource)
        => CatalogUpdater.UpgradeIfStaleAsync(path, resource, RpbotAssembly());

    /// <summary>
    /// Пишет на диск файл с указанным schema_version (или без поля).
    /// </summary>
    private static void WriteDisk(string path, int? schemaVersion, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (schemaVersion.HasValue)
            File.WriteAllText(path, $"{{\"schema_version\": {schemaVersion.Value}, \"_test\": \"{content}\"}}");
        else
            File.WriteAllText(path, $"{{\"_test\": \"{content}\"}}");
    }

    // ─── UpgradeIfStaleAsync: общий сценарий ───────────────────────────

    [Fact]
    public void UpgradeIfStaleAsync_NoFile_SeedsFromEmbedded()
    {
        var path = Path.Combine(TempDir, "vampire", "derangements.json");
        var resource = "RPBot.Resources.derangements.v20.json";

        var result = Run(path, resource);

        Assert.Equal(CatalogUpdater.UpgradeResult.Seeded, result);
        Assert.True(File.Exists(path));
        // Содержимое — копия embedded, а не наш _test.
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("_test", text);
        Assert.Contains("Биполярное расстройство", text);
    }

    [Fact]
    public void UpgradeIfStaleAsync_DiskOlder_Upgrades()
    {
        var path = Path.Combine(TempDir, "vampire", "derangements.json");
        var resource = "RPBot.Resources.derangements.v20.json";
        var embedded = EmbeddedSchemaVersion(resource);
        Assert.NotNull(embedded);

        WriteDisk(path, embedded.Value - 1, "old");

        var result = Run(path, resource);

        Assert.Equal(CatalogUpdater.UpgradeResult.Upgraded, result);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("_test", text);
        Assert.Contains("Биполярное расстройство", text);
    }

    [Fact]
    public void UpgradeIfStaleAsync_DiskEqual_NoOp()
    {
        var path = Path.Combine(TempDir, "vampire", "derangements.json");
        var resource = "RPBot.Resources.derangements.v20.json";
        var embedded = EmbeddedSchemaVersion(resource);
        Assert.NotNull(embedded);

        WriteDisk(path, embedded.Value, "current");

        var result = Run(path, resource);

        Assert.Equal(CatalogUpdater.UpgradeResult.UpToDate, result);
        // Содержимое не трогали.
        var text = File.ReadAllText(path);
        Assert.Contains("_test", text);
    }

    [Fact]
    public void UpgradeIfStaleAsync_DiskNewer_RefusesDowngrade()
    {
        var path = Path.Combine(TempDir, "vampire", "derangements.json");
        var resource = "RPBot.Resources.derangements.v20.json";
        var embedded = EmbeddedSchemaVersion(resource);
        Assert.NotNull(embedded);

        WriteDisk(path, embedded.Value + 5, "future");

        var result = Run(path, resource);

        Assert.Equal(CatalogUpdater.UpgradeResult.DowngradeRefused, result);
        var text = File.ReadAllText(path);
        Assert.Contains("_test", text);
        Assert.Contains("future", text);
    }

    [Fact]
    public void UpgradeIfStaleAsync_DiskMissingVersion_TreatedAsZero_Upgrades()
    {
        // Битый/старый JSON без schema_version — считается как 0.
        // Если embedded > 0, файл должен быть пересеян.
        var path = Path.Combine(TempDir, "vampire", "derangements.json");
        var resource = "RPBot.Resources.derangements.v20.json";
        var embedded = EmbeddedSchemaVersion(resource);
        Assert.NotNull(embedded);
        Assert.True(embedded > 0, "Для теста нужно embedded > 0");

        WriteDisk(path, schemaVersion: null, content: "broken_old_format");

        var result = Run(path, resource);

        Assert.Equal(CatalogUpdater.UpgradeResult.Upgraded, result);
        Assert.DoesNotContain("broken_old_format", File.ReadAllText(path));
    }

    [Fact]
    public void UpgradeIfStaleAsync_DiskWithLegacyVersion_ReadsItAsSchemaVersion()
    {
        // Backward-compat: если на диске лежит старый формат с полем
        // `version` (без префикса), CatalogUpdater должен его прочитать
        // и сравнить с embedded.
        var path = Path.Combine(TempDir, "vampire", "derangements.json");
        var resource = "RPBot.Resources.derangements.v20.json";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"version\": 1, \"_test\": \"legacy\"}");

        var result = Run(path, resource);

        // embedded тоже = 1 → UpToDate.
        Assert.Equal(CatalogUpdater.UpgradeResult.UpToDate, result);
        Assert.Contains("legacy", File.ReadAllText(path));
    }

    [Fact]
    public void UpgradeIfStaleAsync_BrokenJson_TreatedAsZero_Upgrades()
    {
        var path = Path.Combine(TempDir, "vampire", "derangements.json");
        var resource = "RPBot.Resources.derangements.v20.json";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{not valid json");

        var result = Run(path, resource);

        Assert.Equal(CatalogUpdater.UpgradeResult.Upgraded, result);
    }

    [Fact]
    public void UpgradeIfStaleAsync_MissingResource_UpToDateIfDiskExists()
    {
        var path = Path.Combine(TempDir, "vampire");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "nonexistent.json"), "{\"any\": 1}");

        var result = Run(Path.Combine(path, "nonexistent.json"), "RPBot.Resources.does.not.exist.json");

        Assert.Equal(CatalogUpdater.UpgradeResult.UpToDate, result);
    }

    // ─── Интеграция: Vampire*Catalog.UpgradeIfStale() ─────────────────

    [Fact]
    public void VampireDerangementCatalog_UpgradeIfStale_FirstRun_Seeds()
    {
        var result = VampireDerangementCatalog.UpgradeIfStale();
        Assert.True(result is CatalogUpdater.UpgradeResult.Seeded or CatalogUpdater.UpgradeResult.UpToDate);
    }

    [Fact]
    public void VampireClanFlawCatalog_UpgradeIfStale_RepeatedRun_UpToDate()
    {
        // Первый прогон засеет (или UpToDate, если кто-то уже засеял).
        VampireClanFlawCatalog.UpgradeIfStale();
        // Второй прогон должен быть UpToDate.
        var second = VampireClanFlawCatalog.UpgradeIfStale();
        Assert.Equal(CatalogUpdater.UpgradeResult.UpToDate, second);
    }

    [Fact]
    public void VampireWeaponsCatalog_UpgradeIfStale_RepeatedRun_UpToDate()
    {
        VampireWeaponsCatalog.UpgradeIfStale();
        var second = VampireWeaponsCatalog.UpgradeIfStale();
        Assert.Equal(CatalogUpdater.UpgradeResult.UpToDate, second);
    }
}
