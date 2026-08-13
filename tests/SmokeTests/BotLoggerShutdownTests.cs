using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// 4.x — BotLogger:
///   * Shutdown() идемпотентен;
///   * пост-shutdown записи не падают на disposed semaphores;
///   * Shutdown обнуляет _paths / _logDirectory так, что следующий Initialize чист.
/// </summary>
public class BotLoggerShutdownTests : IDisposable
{
    private readonly string _tmpDir;

    public BotLoggerShutdownTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_logger_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
        // Вернуть логгер в чистое состояние для следующих тестов.
        try { BotLogger.Initialize(_tmpDir, DateTime.UtcNow); } catch { }
    }

    [Fact]
    public void Shutdown_CalledOnce_DoesNotThrow()
    {
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 1, 1));
        BotLogger.Shutdown("test");
    }

    [Fact]
    public void Shutdown_CalledTwice_DoesNotThrow()
    {
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 1, 1));
        BotLogger.Shutdown("test");
        BotLogger.Shutdown("test again");
    }

    [Fact]
    public async Task Info_AfterShutdown_DoesNotThrow()
    {
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 1, 1));
        BotLogger.Shutdown("test");

        // Не должно кидать ObjectDisposedException на disposed semaphore.
        BotLogger.Info(LogCategory.System, "post-shutdown message");

        // Даём фоновой записи шанс завершиться (должна стать no-op).
        await Task.Delay(50);
    }

    [Fact]
    public async Task WriteTestSync_AfterShutdown_DoesNotThrow()
    {
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 1, 1));
        BotLogger.Shutdown("test");

        // WriteTestSync должен либо записать, либо вернуться, но не кидать.
        await BotLogger.WriteTestSync(LogCategory.System, "post-shutdown test sync");
    }

    [Fact]
    public void Initialize_AfterShutdown_RecreatesState()
    {
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 1, 1));
        BotLogger.Shutdown("test");

        // Должна успешно переинициализировать — старые пути обнулены.
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 1, 2));

        var expected = Path.Combine(_tmpDir, "20260102_000000");
        Assert.True(Directory.Exists(expected),
            $"После Initialize после Shutdown должен быть создан каталог {expected}");
    }
}
