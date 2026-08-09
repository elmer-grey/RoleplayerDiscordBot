using System;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot.Startup
{
    /// <summary>
    /// Делегирует в AddLog UI. _ui может быть null на pre-startup — это нормально.
    /// </summary>
    public sealed class UiSink : IStartupSink
    {
        private readonly Action<string> _addLog;

        public UiSink(Action<string> addLog)
        {
            _addLog = addLog ?? throw new ArgumentNullException(nameof(addLog));
        }

        public Task WriteAsync(StartupLogRecord record, CancellationToken ct)
        {
            try
            {
                _addLog?.Invoke(Normalize(record));
            }
            catch
            {
                // UI может быть в невалидном состоянии — игнор
            }
            return Task.CompletedTask;
        }

        private static string Normalize(StartupLogRecord r) => r.Channel switch
        {
                    StartupChannel.Header => $"\n── {r.Text} ──",
                    StartupChannel.Footer => $"── /{r.Text} ──",
                    StartupChannel.Warn   => $"  ⚠ {r.Text}",
                    StartupChannel.Error  => $"  ❌ {r.Text}",
                    _                     => $"    {r.Text}",
                };
    }
}
