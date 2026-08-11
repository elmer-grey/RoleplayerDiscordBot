using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot.Startup
{
    /// <summary>
    /// Пишет в единый файл терминального лога BotLogger.UnifiedLogPath.
    /// Если путь ещё не задан (Initialize ещё не звался), использует fallback
    /// logs/startup.log — чтобы ранние стартовые сообщения тоже не пропали.
    ///
    /// Для устранения гонки порядка строк между BotLogger-ом и StartupRenderer-ом
    /// оба источника сериализуются одной блокировкой BotLogger._unifiedLock через
    /// await WriteUnifiedLineAsync. Это даёт честный единый порядок в run.log.
    /// </summary>
    public sealed class FileSink : IStartupSink
    {
        private readonly string _fallbackPath;

        public FileSink(string fallbackPath)
        {
            _fallbackPath = fallbackPath ?? throw new ArgumentNullException(nameof(fallbackPath));
        }

        public async Task WriteAsync(StartupLogRecord record, CancellationToken ct)
        {
            try
            {
                var line = $"[{record.Timestamp:HH:mm:ss.fff}] {Normalize(record)}";

                // Если BotLogger уже инициализирован, зеркалим в его единый файл
                // и сериализуемся через общий с BotLogger лок, чтобы порядок строк
                // (старт + рантайм) был ровно тем, что видит пользователь в GUI.
                if (!string.IsNullOrEmpty(BotLogger.UnifiedLogPath))
                {
                    await BotLogger.WriteUnifiedLineAsync(line).ConfigureAwait(false);
                    return;
                }

                // До Initialize пишем напрямую в fallback-путь. Это ранние
                // стартовые сообщения, которые пригодятся при диагностике.
                var dir = Path.GetDirectoryName(_fallbackPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(_fallbackPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // не валим старт из-за файла
            }
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
