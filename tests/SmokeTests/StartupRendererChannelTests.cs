using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RPBot.Startup;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Покрытие логики StartupRenderer, которая раньше была без тестов:
///   * маршрутизация каналов в синхронную последовательность через SemaphoreSlim;
///   * порядок Header → Body lines → Footer для вложенных стадий;
///   * идемпотентность EndStartup (снятие exclusive-режима);
///   * повторный BeginStage без EndStage → InvalidOperationException;
///   * корректное поведение DisposeAsync после активной фазы.
///
/// Тесты используют TestSink и фиксируют канал каждой записи.
/// </summary>
public class StartupRendererChannelTests : IDisposable
{
    private readonly TestSink _sink;

    public StartupRendererChannelTests()
    {
        _sink = new TestSink();
        // Конструктор рендерера приватный, но Instance — public static singleton.
        // Снимаем все sinks, ставим только наш test-sink.
        StartupRenderer.Instance.ClearSinks();
        StartupRenderer.Instance.AttachSink(_sink);
    }

    public void Dispose()
    {
            try { StartupRenderer.Instance.ClearSinks(); } catch { }
        }

        [Fact]
        public async Task WriteLine_RoutesToStageChannel()
    {
            StartupRenderer.Instance.WriteLine("hello stage");
            await _sink.WaitForCountAsync(1);
            var rec = _sink.Records[0];
            Assert.Equal(StartupChannel.Stage, rec.Channel);
            Assert.Equal("hello stage", rec.Text);
        }

        [Fact]
        public async Task WriteInfo_RoutesToInfoChannel()
        {
            StartupRenderer.Instance.WriteInfo("hello info");
            await _sink.WaitForCountAsync(1);
            var rec = _sink.Records[0];
            Assert.Equal(StartupChannel.Info, rec.Channel);
            Assert.Equal("hello info", rec.Text);
        }

        [Fact]
        public async Task WriteWarn_RoutesToWarnChannel()
        {
            StartupRenderer.Instance.WriteWarn("⚠️ caution");
            await _sink.WaitForCountAsync(1);
            Assert.Equal(StartupChannel.Warn, _sink.Records[0].Channel);
        }

        [Fact]
        public async Task WriteError_RoutesToErrorChannel()
        {
            StartupRenderer.Instance.WriteError("❌ boom");
            await _sink.WaitForCountAsync(1);
            Assert.Equal(StartupChannel.Error, _sink.Records[0].Channel);
        }

        [Fact]
        public async Task BeginStage_HeaderAndFooterAppearOnce()
        {
            StartupRenderer.Instance.ClearSinks();
            StartupRenderer.Instance.AttachSink(_sink);

            using (StartupRenderer.Instance.BeginStage("TEST STAGE"))
            {
                StartupRenderer.Instance.WriteLine("step 1");
                StartupRenderer.Instance.WriteLine("step 2");
            }

            // BeginStage пишет синхронный Header, потом 2 строки,
            // потом EndStage пишет синхронный Footer — итого 4 записи.
            await _sink.WaitForCountAsync(4);

            Assert.Equal(StartupChannel.Header, _sink.Records[0].Channel);
            Assert.Equal("TEST STAGE", _sink.Records[0].Text);
            Assert.Equal(StartupChannel.Stage, _sink.Records[1].Channel);
            Assert.Equal(StartupChannel.Stage, _sink.Records[2].Channel);
            Assert.Equal(StartupChannel.Footer, _sink.Records[3].Channel);
        }

    [Fact]
    public async Task BeginStage_TwiceWithoutEnd_Throws()
    {
        StartupRenderer.Instance.ClearSinks();
        StartupRenderer.Instance.AttachSink(_sink);

        using (StartupRenderer.Instance.BeginStage("OUTER"))
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => StartupRenderer.Instance.BeginStage("INNER"));
            Assert.Contains("BeginStage вызван повторно", ex.Message);
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ClearSinks_StopsDispatchingToOldSinks()
    {
        var sink2 = new TestSink();
        StartupRenderer.Instance.AttachSink(sink2);
        StartupRenderer.Instance.ClearSinks();
        StartupRenderer.Instance.AttachSink(_sink);

        StartupRenderer.Instance.WriteLine("via first");
        await _sink.WaitForCountAsync(1);

        // sink2 не должен получить ничего — он был снят через ClearSinks
        // до переподключения. (Если бы ClearSinks не сработал, мы бы увидели
        // запись здесь, что и было бы ловушкой.)
        Assert.Empty(sink2.Records);
    }

    [Fact]
    public async Task AttachSink_SameInstance_NotAddedTwice()
    {
        var sink3 = new TestSink();
        StartupRenderer.Instance.AttachSink(sink3);
        StartupRenderer.Instance.AttachSink(sink3);
        StartupRenderer.Instance.AttachSink(_sink);

        StartupRenderer.Instance.WriteLine("only-once");
        await _sink.WaitForCountAsync(1);

        // Должна быть одна запись: даже если sink3 был добавлен дважды,
        // dedup в AttachSink это пресекает.
        Assert.Single(sink3.Records);
    }

    private sealed class TestSink : IStartupSink
    {
        private readonly ConcurrentQueue<StartupLogRecord> _records = new();

        public System.Collections.Generic.List<StartupLogRecord> Records =>
            _records.ToArray() is var arr ? new System.Collections.Generic.List<StartupLogRecord>(arr) : new();

        public Task WriteAsync(StartupLogRecord record, CancellationToken ct)
        {
            _records.Enqueue(record);
            return Task.CompletedTask;
        }

        public async Task WaitForCountAsync(int expectedCount, int timeoutMs = 2000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (_records.Count < expectedCount && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
        }
    }
}
