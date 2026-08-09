using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot.Startup
{
    /// <summary>
    /// Пишет в файл напрямую, минуя BotLogger. Это ключ к устранению дублей:
    /// рендерер ведёт свой лог старта, BotLogger ведёт свой по категориям.
    /// </summary>
    public sealed class FileSink : IStartupSink
    {
        private readonly string _path;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public FileSink(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public Task WriteAsync(StartupLogRecord record, CancellationToken ct)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var line = $"[{record.Timestamp:HH:mm:ss.fff}] {Normalize(record)}";

                _gate.Wait(TimeSpan.FromMilliseconds(250), ct);
                try
                {
                    File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                }
                finally
                {
                    try { _gate.Release(); } catch { }
                }
            }
            catch
            {
                // не валим старт из-за файла
            }
            return Task.CompletedTask;
        }

        private static string Normalize(StartupLogRecord r) => r.Channel switch
        {
                    StartupChannel.Header => $"\n── {r.Text} ──",
                    StartupChannel.Footer => $"── /{r.Text} ──",
                    StartupChannel.Warn   => $"[WARN] {r.Text}",
                    StartupChannel.Error  => $"[ERROR] {r.Text}",
                    _                     => r.Text,
                };
    }
}
