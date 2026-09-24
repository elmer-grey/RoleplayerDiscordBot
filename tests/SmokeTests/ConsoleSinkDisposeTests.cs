using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// audit-leaks #7: ConsoleSink — SemaphoreSlim _gate без IDisposable.
/// Класс не использовался в основном пути (после введения BotLoggerSink)
/// но остался в репозитории как legacy/reusable. Сам факт, что у него
/// есть управляемый ресурс без Dispose, делает его ловушкой для будущих
/// пользователей. Тест проверяет идемпотентность Dispose.
/// </summary>
public class ConsoleSinkDisposeTests
{
    [Fact]
    public void Dispose_IsIdempotent()
    {
        var sink = new RPBot.Startup.ConsoleSink();

        sink.Dispose();
        sink.Dispose();
        sink.Dispose();

        // SemaphoreSlim.Dispose идемпотентен — повторный вызов не бросает.
    }

    [Fact]
    public void ImplementsIDisposable()
    {
        var sink = new RPBot.Startup.ConsoleSink();
        Assert.IsAssignableFrom<IDisposable>(sink);
    }

    [Fact]
    public void WriteAsync_AfterDispose_DoesNotThrowUnrecoverable()
    {
        var sink = new RPBot.Startup.ConsoleSink();
        sink.Dispose();

        // WriteAsync перехватывает любое исключение внутри и пишет в Debug.
        // Если _gate.Dispose() уберёт все хэндлы, обращение к нему бросит
        // ObjectDisposedException — это нормально быть проглочено.
        var rec = new RPBot.Startup.StartupLogRecord(
            DateTime.Now,
            RPBot.Startup.StartupChannel.Info,
            "post-dispose line");

        var ex = Record.Exception(() => sink.WriteAsync(rec, default).GetAwaiter().GetResult());
        Assert.Null(ex);
    }
}
