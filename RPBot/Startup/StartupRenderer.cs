using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RPBot.Util;

namespace RPBot.Startup
{
    /// <summary>
    /// Лаконичный рендерер старта. Синглтон. Один путь — никаких дублей.
    ///
    /// Поведение:
    ///   - До EndStartup() — exclusive-режим: SemaphoreSlim(1,1) per-line,
    ///     все строки последовательно проходят через все sinks.
    ///   - BeginStage(title) — пишет Header, EndStage — пишет Footer-разделитель
    ///     (в том же sink-формате, что и заголовок, но с ведущим слэшем).
    ///     WriteLine внутри стадии идёт по одной строке, не блокируя вызов.
    ///   - EndStartup() — снимает эксклюзив (sinks дальше работают параллельно).
    ///   - WriteHeader/WriteFooter (async) — для вложенных подблоков,
    ///     когда нужен свой заголовок/закрывающая линия без стадии.
    ///
    /// Контракт: один вызов WriteLine → ровно одна запись в каждый sink.
    /// Никаких Console.SetOut. Никакого двойного логирования.
    /// </summary>
    public sealed class StartupRenderer : IAsyncDisposable
    {
        private readonly List<IStartupSink> _sinks = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private bool _exclusive = true;
        private bool _disposed;
        private int _stageDepth;
        private string? _lastStageTitle;

        public static StartupRenderer Instance { get; } = new StartupRenderer();

        public void AttachSink(IStartupSink sink)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            if (!_sinks.Contains(sink))
                _sinks.Add(sink);
        }

        /// <summary>
        /// Удаляет все sinks. Используется для очистки накопленных подписок
        /// (например, при горячем рестарте программа несколько раз дёргала
        /// AttachSink, и старый UiSink из прошлой Program-копии продолжал
        /// писать в UI дубли строк).
        /// </summary>
        public void ClearSinks() => _sinks.Clear();

        public StageHandle BeginStage(string title)
        {
            ThrowIfDisposed();
            if (_stageDepth > 0)
                throw new InvalidOperationException("BeginStage вызван повторно до EndStage.");
            _stageDepth++;
            // Заголовок должен появиться в логах ДО любого кода, идущего после BeginStage,
            // поэтому ждём завершения синхронно. Иначе порядок строк ломается.
            WriteHeaderSync(title);
            return new StageHandle(this);
        }

        private void EndStage()
        {
            if (_stageDepth == 0) return;
            _stageDepth--;
            // Закрывающая линия — в том же sink-формате, что и заголовок.
            // Синхронно, чтобы блок гарантированно закрылся до следующего BeginStage.
            WriteFooterSync(_lastStageTitle);
        }

        public void EndStartup() => _exclusive = false;

        public void WriteLine(string text)
        {
            ThrowIfDisposed();
            var record = new StartupLogRecord(DateTime.Now, StartupChannel.Stage, text);
            _ = DispatchAsync(record);
        }

        public void WriteInfo(string text)
        {
            ThrowIfDisposed();
            var record = new StartupLogRecord(DateTime.Now, StartupChannel.Info, text);
            _ = DispatchAsync(record);
        }

        public void WriteWarn(string text)
        {
            ThrowIfDisposed();
            var record = new StartupLogRecord(DateTime.Now, StartupChannel.Warn, text);
            _ = DispatchAsync(record);
        }

        public void WriteError(string text)
        {
            ThrowIfDisposed();
            var record = new StartupLogRecord(DateTime.Now, StartupChannel.Error, text);
            _ = DispatchAsync(record);
        }

        public void WriteHeader(string text)
        {
            ThrowIfDisposed();
            var record = new StartupLogRecord(DateTime.Now, StartupChannel.Header, text);
            _ = DispatchAsync(record);
        }

        public void WriteFooter(string text)
        {
            ThrowIfDisposed();
            var record = new StartupLogRecord(DateTime.Now, StartupChannel.Footer, text);
            _ = DispatchAsync(record);
        }

        private void WriteHeaderSync(string text)
        {
            ThrowIfDisposed();
            _lastStageTitle = text;
            var record = new StartupLogRecord(DateTime.Now, StartupChannel.Header, text);
            DispatchAsync(record).GetAwaiter().GetResult();
        }

        private void WriteFooterSync(string? title)
        {
            if (string.IsNullOrEmpty(title)) return;
            var record = new StartupLogRecord(DateTime.Now, StartupChannel.Footer, title);
            DispatchAsync(record).GetAwaiter().GetResult();
        }

        private async Task DispatchAsync(StartupLogRecord record)
        {
            if (_disposed) return;
            try
            {
                if (_exclusive)
                {
                    var got = _gate.Wait(TimeSpan.FromMilliseconds(500), _cts.Token);
                    if (!got) return; // лучше тихо дропнуть, чем блокировать
                }

                foreach (var sink in _sinks)
                {
                    try { await sink.WriteAsync(record, _cts.Token).ConfigureAwait(false); }
                    catch (Exception ex) { BotLogger.Warn(LogCategory.System, $"[StartupRenderer] sink {sink.GetType().Name} failed: {ex.GetType().Name}: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) { /* shutdown — норма */ }
            catch (ObjectDisposedException) { /* shutdown — норма */ }
            catch (Exception ex)
            {
                // Не валим startup-рендер из-за экзотики, но логируем —
                // раньше было catch { } и при проблемах с самим sinks не было
                // никакого следа в основном логе.
                BotLogger.Warn(LogCategory.System, $"[StartupRenderer] DispatchAsync failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (_exclusive)
                {
                    try { _gate.Release(); } catch { }
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StartupRenderer));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
            _gate.Dispose();
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public readonly struct StageHandle : IDisposable
        {
            private readonly StartupRenderer _renderer;
            internal StageHandle(StartupRenderer renderer) { _renderer = renderer; }
            public void Dispose() => _renderer?.EndStage();
        }
    }
}
