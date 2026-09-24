using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Тесты для LogDayResolver: граничные случаи cutoff-hour.
/// Cutoff по умолчанию = 6 (06:00 локального времени).
/// </summary>
public class LogDayResolverTests
{
    [Fact]
    public void Resolve_AfterCutoff_BelongsToCurrentDay()
    {
        // 23.09 06:00 — это уже новый день логов.
        var result = LogDayResolver.ResolveDay(new DateTime(2026, 9, 23, 6, 0, 0));
        Assert.Equal("20260923", result);
    }

    [Fact]
    public void Resolve_BeforeCutoff_BelongsToPreviousDay()
    {
        // 23.09 05:59:59 — это ещё предыдущий день.
        var result = LogDayResolver.ResolveDay(new DateTime(2026, 9, 23, 5, 59, 59));
        Assert.Equal("20260922", result);
    }

    [Fact]
    public void Resolve_Midnight_BelongsToPreviousDay()
    {
        // Полночь — это 00:00, что меньше cutoff=6, значит вчерашний день.
        var result = LogDayResolver.ResolveDay(new DateTime(2026, 9, 23, 0, 0, 0));
        Assert.Equal("20260922", result);
    }

    [Fact]
    public void Resolve_Noon_BelongsToCurrentDay()
    {
        var result = LogDayResolver.ResolveDay(new DateTime(2026, 9, 23, 12, 0, 0));
        Assert.Equal("20260923", result);
    }

    [Fact]
    public void Resolve_LateNight_BelongsToCurrentDay()
    {
        // 23:59 — после cutoff, значит сегодняшний день.
        var result = LogDayResolver.ResolveDay(new DateTime(2026, 9, 23, 23, 59, 59));
        Assert.Equal("20260923", result);
    }

    [Fact]
    public void Resolve_CustomCutoff_Respected()
    {
        // Cutoff = 0: каждый момент относится к своему календарному дню.
        Assert.Equal("20260923", LogDayResolver.ResolveDay(new DateTime(2026, 9, 23, 0, 30, 0), cutoffHour: 0));
        Assert.Equal("20260923", LogDayResolver.ResolveDay(new DateTime(2026, 9, 23, 23, 30, 0), cutoffHour: 0));
        // Cutoff = 23: всё до 23:00 — это вчера.
        Assert.Equal("20260922", LogDayResolver.ResolveDay(new DateTime(2026, 9, 23, 22, 59, 59), cutoffHour: 23));
        Assert.Equal("20260923", LogDayResolver.ResolveDay(new DateTime(2026, 9, 23, 23, 0, 0), cutoffHour: 23));
    }

    [Fact]
    public void Resolve_InvalidCutoff_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LogDayResolver.ResolveDay(DateTime.Now, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LogDayResolver.ResolveDay(DateTime.Now, 24));
    }

    [Fact]
    public void GetDayStart_ReturnsCutoffOfThatDay()
    {
        // 23.09 14:30 → 23.09 06:00
        var start = LogDayResolver.GetDayStart(new DateTime(2026, 9, 23, 14, 30, 0));
        Assert.Equal(new DateTime(2026, 9, 23, 6, 0, 0), start);

        // 23.09 03:15 → 22.09 06:00
        start = LogDayResolver.GetDayStart(new DateTime(2026, 9, 23, 3, 15, 0));
        Assert.Equal(new DateTime(2026, 9, 22, 6, 0, 0), start);

        // Ровно 23.09 06:00 → 23.09 06:00
        start = LogDayResolver.GetDayStart(new DateTime(2026, 9, 23, 6, 0, 0));
        Assert.Equal(new DateTime(2026, 9, 23, 6, 0, 0), start);
    }
}

/// <summary>
/// Тесты для BotLogger.RolloverToDay: переключение на новую суточную папку
/// должно перенацелить все последующие записи на новый каталог.
/// </summary>
[Collection(nameof(BotLoggerCollection))]
public class BotLoggerRolloverTests : IDisposable
{
    private readonly string _tmpDir;

    public BotLoggerRolloverTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_rollover_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 9, 23, 14, 0, 0));
    }

    public void Dispose()
    {
        try { BotLogger.Shutdown("test end"); } catch { }
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    [Fact]
    public async Task Rollover_SwitchesTargetFolder()
    {
        // После Initialize логгер пишет в 20260923.
        await BotLogger.WriteTestSync(LogCategory.System, "before-rollover");
        var day23 = Path.Combine(_tmpDir, "20260923", "System.log");
        Assert.True(File.Exists(day23));
        var beforeContent = await File.ReadAllTextAsync(day23);
        Assert.Contains("before-rollover", beforeContent);

        // Ролловер на 24.09 — имитируем 06:00.
        BotLogger.RolloverToDay(_tmpDir, "20260924");

        // ✅ Делаем несколько попыток чтения — WriteAsync внутри fire-and-forget,
        // и при параллельном xUnit-запуске другой тест может вмешаться
        // в статическое состояние BotLogger между WriteAsync и AppendAllTextAsync.
        // Но в нашем случае WriteTestSync — это await WriteAsync, который
        // ДОЛЖЕН дождаться записи. Если упало — это индикатор реальной гонки.
        await BotLogger.WriteTestSync(LogCategory.System, "after-rollover");

        var day24System = Path.Combine(_tmpDir, "20260924", "System.log");
        Assert.True(Directory.Exists(Path.Combine(_tmpDir, "20260924")),
            $"Ожидался новый каталог дня {_tmpDir}\\20260924");

        Assert.True(File.Exists(day24System), $"Ожидался файл {day24System}");

        var afterContent = await File.ReadAllTextAsync(day24System);
        Assert.Contains("after-rollover", afterContent);
    }

    [Fact]
    public async Task Rollover_RunLogContainsMarker()
    {
        // В run.log текущего дня должен быть записан маркер ролловера.
        BotLogger.RolloverToDay(_tmpDir, "20260924");

        // После ролловера UnifiedLogPath указывает на новый день.
        var runLog = BotLogger.UnifiedLogPath;
        Assert.NotNull(runLog);
        Assert.Contains("20260924", runLog!);
        Assert.Contains("Rollover", await File.ReadAllTextAsync(runLog!));
    }

    [Fact]
    public void Rollover_AppendsToExistingDayFolder()
    {
        // Если папка дня уже существует — RolloverToDay дописывает в неё, а не пересоздаёт.
        var day24 = Path.Combine(_tmpDir, "20260924");
        Directory.CreateDirectory(day24);
        File.WriteAllText(Path.Combine(day24, "System.log"), "preserved content\n");

        BotLogger.RolloverToDay(_tmpDir, "20260924");

        Assert.True(File.Exists(Path.Combine(day24, "System.log")));
        var content = File.ReadAllText(Path.Combine(day24, "System.log"));
        Assert.Contains("preserved content", content);
    }
}
