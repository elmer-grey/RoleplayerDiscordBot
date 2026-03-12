using System;
using System.IO;
using System.Threading;
using System.Text;

namespace RPBot
{
    internal static class Logger
    {
        private static readonly SemaphoreSlim _semaphore = new(1,1);
        private static string _logDir = Path.Combine(AppContext.BaseDirectory, "Logs");
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTime First)> _recent = new();
        private static readonly TimeSpan _suppressWindow = TimeSpan.FromSeconds(30);
        private const int _summaryEvery = 5;

        public static void Initialize(string? logDirectory)
        {
            if (!string.IsNullOrWhiteSpace(logDirectory))
                _logDir = logDirectory!;
            try { Directory.CreateDirectory(_logDir); } catch { }
        }

        public static void RedirectConsoleOutputs()
        {
            try
            {
                Console.SetOut(new LoggerTextWriter());
                Console.SetError(new LoggerTextWriter());
            }
            catch { }
        }

        public static void LogInfo(string message)
        {
            _ = WriteAsync("InfoLog.txt", message);
        }

        public static void LogError(string message)
        {
            _ = WriteAsync("ErrorLog.txt", message);
        }

        public static void LogWarning(string message)
        {
            _ = WriteAsync("WarningLog.txt", message);
        }

        public static void LogDebug(string message)
        {
            _ = WriteAsync("DebugLog.txt", message);
        }

        private static async System.Threading.Tasks.Task WriteAsync(string fileName, string message)
        {
            var path = Path.Combine(_logDir, fileName);

            // dedupe/suppression logic
            var key = fileName + "|" + message;
            var now = DateTime.UtcNow;

            var entry = _recent.GetOrAdd(key, _ => (1, now));
            if (entry.Count > 0)
            {
                // attempt to update
                var updated = _recent.AddOrUpdate(key,
                    k => (1, now),
                    (k, old) =>
                    {
                        if ((now - old.First) <= _suppressWindow)
                        {
                            return (old.Count + 1, old.First);
                        }
                        else
                        {
                            // window expired - reset
                            return (1, now);
                        }
                    });

                // if within window and count not hitting summary threshold, suppress detailed logs
                if ((now - updated.First) <= _suppressWindow && updated.Count > 1 && (updated.Count % _summaryEvery) != 0)
                {
                    // suppressed
                    return;
                }

                // if we are at summary point, replace message with summary
                if (updated.Count > 1 && (now - updated.First) <= _suppressWindow && (updated.Count % _summaryEvery) == 0)
                {
                    message = $"[SUPPRESSED] Message repeated {updated.Count} times since {updated.First:HH:mm:ss}: {message}";
                }
            }

            await _semaphore.WaitAsync();
            try
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var fileInfo = new FileInfo(path);
                        if (fileInfo.Length > 10 * 1024 * 1024)
                        {
                            var archived = Path.Combine(_logDir, $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                            File.Move(path, archived);
                        }
                    }
                    await File.AppendAllTextAsync(path, $"[{DateTime.Now:dd-MM-yyyy HH:mm:ss}] {message}\n");
                }
                catch
                {
                    // best-effort
                }
            }
            finally
            {
                _semaphore.Release();
            }

            try { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"); } catch { }
        }
    }

    internal sealed class LoggerTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (value == null) return;
            try { Logger.LogInfo(value); } catch { }
        }

        public override void Write(char value)
        {
            // collect or ignore single chars
        }
    }
}
