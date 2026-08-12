using System;
using System.Threading;
using System.Threading.Tasks;
using RPBot;

namespace RPBot.Startup
{
    /// <summary>
    /// Единственный sink для StartupRenderer на этапе после привязки.
    /// Отвечает сразу за три канала:
    ///   • дашборд — через observer-канал BotLogger (см. BotLogger.WriteStartup);
    ///   • терминал — _uiSink BotLogger-а (Console/UI-окно бота);
    ///   • run.log — тот же путь, что у обычных BotLogger-записей
    ///     (через WriteStartupAppendToFile, см. BotLogger).
    ///
    /// Раньше StartupRenderer-у подключались отдельные ConsoleSink/FileSink/UiSink,
    /// и это создавало дубли в терминале и run.log, потому что BotLoggerSink
    /// тоже писал в observer и UI. Теперь у нас ровно один путь наружу —
    /// через этот BotLoggerSink.
    /// </summary>
    public sealed class BotLoggerSink : IStartupSink
    {
        public Task WriteAsync(StartupLogRecord record, CancellationToken ct)
        {
            try
            {
                switch (record.Channel)
                {
                    case StartupChannel.Warn:
                        BotLogger.WriteStartupFull(LogLevel.Warn, record.Text);
                        break;
                    case StartupChannel.Error:
                        BotLogger.WriteStartupFull(LogLevel.Error, record.Text);
                        break;
                    case StartupChannel.Header:
                    case StartupChannel.Footer:
                    case StartupChannel.Info:
                    case StartupChannel.Stage:
                    default:
                        BotLogger.WriteStartupFull(LogLevel.Info, record.Text);
                        break;
                }
            }
            catch
            {
                // не валим старт из-за проблем дашборда/файла/UI
            }
            return Task.CompletedTask;
        }
    }
}
