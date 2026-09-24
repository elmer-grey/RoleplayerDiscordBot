using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// 1.5 — LogStartup пишет в правильную папку.
/// </summary>
[Collection(nameof(BotLoggerCollection))]
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
        public async Task Initialize_CreatesSubfolder_WithDayKey()
        {
            // 21:30 — это уже после cutoffHour=6, значит день = 20260808.
            var startup = new DateTime(2026, 8, 8, 21, 30, 0);
            BotLogger.Initialize(_tmpDir, startup);

            var expected = Path.Combine(_tmpDir, "20260808");
            Assert.True(Directory.Exists(expected),
                $"Ожидался каталог {expected}");

            // Info — fire-and-forget, ждём, пока фоновой Write завершится, чтобы не гонять горутины.
            await BotLogger.WriteTestSync(LogCategory.System, "test message");

            var systemLog = Path.Combine(expected, "System.log");
            Assert.True(File.Exists(systemLog), $"Ожидался файл {systemLog}");
            var content = await File.ReadAllTextAsync(systemLog);
            Assert.Contains("test message", content);
        }

        [Fact]
        public void TwoStarts_SameDay_ReuseSubfolder()
    {
        // Два рестарта в один логический день должны писать в ОДНУ папку.
        // Это поведение суточных папок: рестарты не плодят новые директории.
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 8, 8, 10, 0, 0));
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 8, 8, 11, 0, 0));

        var day = Path.Combine(_tmpDir, "20260808");
        Assert.True(Directory.Exists(day),
            $"Ожидался один каталог дня {day}, а не две подпапки на каждый рестарт");
    }

        [Fact]
        public void TwoStarts_DifferentDays_CreateTwoSubfolders()
    {
        // 23.09 23:00 — день 23.09.
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 9, 23, 23, 0, 0));
        // 24.09 10:00 — уже новый день.
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 9, 24, 10, 0, 0));

        Assert.True(Directory.Exists(Path.Combine(_tmpDir, "20260923")));
        Assert.True(Directory.Exists(Path.Combine(_tmpDir, "20260924")));
    }
}