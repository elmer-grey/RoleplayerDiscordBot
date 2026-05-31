using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>Уровни логирования.</summary>
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error,
    }

    /// <summary>Категории источников логов.</summary>
    public enum LogCategory
    {
        Boot,
        Session,
        Music,
        Predict,
        Points,
        Discord,
        Sheets,
        Cmd,
        Config,
        System,
    }

    /// <summary>
    /// Центральный логгер бота.
    /// Один файл на жизненный цикл (имя по времени старта).
    /// Пишет в файл всегда; в UI — только Info/Warn/Error.
    /// </summary>
    public static class BotLogger
    {
        private static readonly SemaphoreSlim _fileLock = new(1, 1);

        private static string? _logFilePath;
        private static Action<string>? _uiSink;         // callback → _ui.AddLog
        private static LogLevel _minLevel = LogLevel.Debug;
        private static long _maxFileSizeBytes = 20 * 1024 * 1024; // 20 МБ

        // ───── Инициализация ──────────────────────────────────────────────

        /// <summary>
        /// Вызывается один раз при запуске.
        /// <paramref name="logDirectory"/> — папка Logs/.
        /// <paramref name="startupTime"/> — время старта (используется в имени файла).
        /// </summary>
        public static void Initialize(string logDirectory, DateTime startupTime)
        {
            Directory.CreateDirectory(logDirectory);
            var stamp = startupTime.ToString("yyyyMMdd_HHmmss");
            _logFilePath = Path.Combine(logDirectory, $"Bot_{stamp}.log");
        }

        /// <summary>Устанавливает (или снимает) callback для вывода в терминальный UI.</summary>
        public static void SetUiSink(Action<string>? sink)
        {
            _uiSink = sink;
        }

        /// <summary>Минимальный уровень для записи в файл (по умолчанию Debug).</summary>
        public static void SetMinLevel(LogLevel level)
        {
            _minLevel = level;
        }

        // ───── Публичный API ─────────────────────────────────────────────

        public static void Debug(LogCategory category, string message)
            => Write(LogLevel.Debug, category, message, null);

        public static void Info(LogCategory category, string message)
            => Write(LogLevel.Info, category, message, null);

        public static void Warn(LogCategory category, string message)
            => Write(LogLevel.Warn, category, message, null);

        public static void Error(LogCategory category, string message, Exception? ex = null)
            => Write(LogLevel.Error, category, ex != null ? $"{message}: {ex.Message}" : message, null);

        /// <summary>Асинхронные варианты (для использования в async-методах).</summary>
        public static Task DebugAsync(LogCategory category, string message)
            => WriteAsync(LogLevel.Debug, category, message);

        public static Task InfoAsync(LogCategory category, string message)
            => WriteAsync(LogLevel.Info, category, message);

        public static Task WarnAsync(LogCategory category, string message)
            => WriteAsync(LogLevel.Warn, category, message);

        public static Task ErrorAsync(LogCategory category, string message, Exception? ex = null)
            => WriteAsync(LogLevel.Error, category, ex != null ? $"{message}: {ex.Message}" : message);

        // ───── Ядро ──────────────────────────────────────────────────────

        private static void Write(LogLevel level, LogCategory category, string message, string? unused = null)
        {
            if (level < _minLevel) return;
            var line = FormatLine(level, category, message);
            // Запись в файл — fire-and-forget, ошибки проглатываем
#pragma warning disable CS4014
            AppendToFileAsync(line);
#pragma warning restore CS4014
            // UI — только Info и выше, синхронно (вызывается из UI-потока или фона)
            if (level >= LogLevel.Info)
                _uiSink?.Invoke(UiPrefix(level) + message);
        }

        private static async Task WriteAsync(LogLevel level, LogCategory category, string message)
        {
            if (level < _minLevel) return;
            var line = FormatLine(level, category, message);
            await AppendToFileAsync(line).ConfigureAwait(false);
            if (level >= LogLevel.Info)
                _uiSink?.Invoke(UiPrefix(level) + message);
        }

        // ───── Форматирование ────────────────────────────────────────────

        private static string FormatLine(LogLevel level, LogCategory category, string message)
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var lvl  = LevelLabel(level);
            var cat  = CategoryLabel(category);
            return $"[{time}] [{lvl}] [{cat}] {message}";
        }

        private static string LevelLabel(LogLevel level) => level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info  => "INFO ",
            LogLevel.Warn  => "WARN ",
            LogLevel.Error => "ERROR",
            _              => "?????"
        };

        private static string CategoryLabel(LogCategory cat) => cat switch
        {
            LogCategory.Boot    => "BOOT   ",
            LogCategory.Session => "SESSION",
            LogCategory.Music   => "MUSIC  ",
            LogCategory.Predict => "PREDICT",
            LogCategory.Points  => "POINTS ",
            LogCategory.Discord => "DISCORD",
            LogCategory.Sheets  => "SHEETS ",
            LogCategory.Cmd     => "CMD    ",
            LogCategory.Config  => "CONFIG ",
            LogCategory.System  => "SYSTEM ",
            _                   => "MISC   ",
        };

        private static string UiPrefix(LogLevel level) => level switch
        {
            LogLevel.Warn  => "⚠️ ",
            LogLevel.Error => "❌ ",
            _              => string.Empty,
        };

        // ───── Запись в файл ─────────────────────────────────────────────

        private static async Task AppendToFileAsync(string line)
        {
            if (_logFilePath == null) return;
            await _fileLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Ротация по размеру
                if (File.Exists(_logFilePath))
                {
                    var fi = new FileInfo(_logFilePath);
                    if (fi.Length > _maxFileSizeBytes)
                    {
                        var rotated = _logFilePath.Replace(".log", $"_rotated_{DateTime.Now:HHmmss}.log");
                        File.Move(_logFilePath, rotated);
                    }
                }

                await File.AppendAllTextAsync(_logFilePath, line + Environment.NewLine, Encoding.UTF8)
                          .ConfigureAwait(false);
            }
            catch
            {
                // Игнорируем ошибки файлового вывода — не падаем из-за лога
            }
            finally
            {
                _fileLock.Release();
            }
        }

        // ───── Вспомогательные методы для совместимости ──────────────────

        /// <summary>
        /// Записывает блок строк (для startup-боксов) как Info/Boot.
        /// </summary>
        public static async Task WriteBatchAsync(LogCategory category, System.Collections.Generic.IEnumerable<string> lines)
        {
            foreach (var line in lines)
                await WriteAsync(LogLevel.Info, category, line).ConfigureAwait(false);
        }

        /// <summary>
        /// Закрывает логгер (записывает финальную строку).
        /// </summary>
        public static void Shutdown(string reason)
        {
            _ = AppendToFileAsync(FormatLine(LogLevel.Info, LogCategory.Boot, $"=== Логгер завершён: {reason} ==="));
        }
    }
}
