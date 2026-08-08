using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// 1.5 — LogStartup пишет в правильную папку.
/// </summary>
public class LoggerTests : IDisposable
{
    private readonly string _tmpDir;

    public LoggerTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    [Fact]
    public async Task Initialize_CreatesSubfolder_WithTimestamp()
    {
        var startup = new DateTime(2026, 8, 8, 21, 30, 0);
        BotLogger.Initialize(_tmpDir, startup);

        var expected = Path.Combine(_tmpDir, "20260808_213000");
        Assert.True(Directory.Exists(expected),
            $"Ожидался каталог {expected}");

        // Прямой sync-Write, чтобы избежать гонки с фоновой записью
        BotLogger.Info(LogCategory.System, "test message");

        var systemLog = Path.Combine(expected, "System.log");
        Assert.True(File.Exists(systemLog), $"Ожидался файл {systemLog}");
        var content = await File.ReadAllTextAsync(systemLog);
        Assert.Contains("test message", content);
        await Task.CompletedTask;
    }

    [Fact]
    public void LogStartup_TwoStarts_CreatesTwoSubfolders()
    {
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 8, 8, 10, 0, 0));
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 8, 8, 11, 0, 0));

        var first = Path.Combine(_tmpDir, "20260808_100000");
        var second = Path.Combine(_tmpDir, "20260808_110000");
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
    }
}