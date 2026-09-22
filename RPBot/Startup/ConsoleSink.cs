using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot.Startup
{
    /// <summary>
    /// Пишет в консоль. Один экземпляр на процесс. Внутри — SemaphoreSlim(1,1),
    /// чтобы цвет-ломающие Console.WriteLine(ы) из других потоков не разрывали блок.
    /// </summary>
    public sealed class ConsoleSink : IStartupSink
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public Task WriteAsync(StartupLogRecord record, CancellationToken ct)
        {
            try
            {
                _gate.Wait(TimeSpan.FromMilliseconds(250), ct);
                try
                {
                    var prev = Console.ForegroundColor;
                    Console.ForegroundColor = PickColor(record.Channel);
                    Console.WriteLine(FormatLine(record));
                    Console.ForegroundColor = prev;
                }
                finally
                {
                    try { _gate.Release(); } catch { }
                }
            }
            catch (Exception ex)
            {
                // не валим старт из-за консоли, но оставляем след в Debug —
                // Console.Write может бросить, если stdout перенаправлен и
                // пайп закрыт (например, бот запущен как Windows-сервис).
                Debug.WriteLine($"[ConsoleSink] write failed: {ex.GetType().Name}: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        private static ConsoleColor PickColor(StartupChannel channel) => channel switch
        {
            StartupChannel.Header => ConsoleColor.Cyan,
                    StartupChannel.Footer => ConsoleColor.DarkCyan,
                    StartupChannel.Warn   => ConsoleColor.Yellow,
                    StartupChannel.Error  => ConsoleColor.Red,
                    _                     => ConsoleColor.Gray,
                };

                private static string FormatLine(StartupLogRecord r)
                {
                    // Без префикса времени — файл пишет тайм-штамп, консоль остаётся чистой.
                    // Пустая строка перед заголовком помогает разделять этапы визуально.
                    return r.Channel switch
                    {
                        StartupChannel.Header => $"\n═══ {r.Text} ═══",
                        StartupChannel.Footer => $"═══ /{r.Text} ═══",
                        StartupChannel.Warn   => $"  ⚠ {r.Text}",
                        StartupChannel.Error  => $"  ❌ {r.Text}",
                        _                     => $"    {r.Text}",
                    };
                }
    }
}
