using System;
using System.Collections.Generic;
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
    /// Каждая категория пишется в отдельный файл с одинаковым суффиксом времени запуска:
    ///   Boot_20250615_143022.log, Discord_20250615_143022.log, Music_20250615_143022.log …
    /// Новые файлы создаются только при запуске/рестарте.
    /// </summary>
    public static class BotLogger
    {
        // Один семафор на категорию — файлы не блокируют друг друга
        private static readonly Dictionary<LogCategory, SemaphoreSlim> _locks  = new();
        private static readonly Dictionary<LogCategory, string>        _paths  = new();

        private static string?        _logDirectory;
        private static string?        _startupStamp;
        private static Action<string>? _uiSink;
        private static LogLevel        _minLevel         = LogLevel.Debug;
        private static long            _maxFileSizeBytes = 20 * 1024 * 1024; // 20 МБ

        // ───── Инициализация ──────────────────────────────────────────────

        /// <summary>
        /// Вызывается один раз при запуске/рестарте.
        /// Создаёт имена файлов вида: <Category>_yyyyMMdd_HHmmss.log
        /// </summary>
        public static void Initialize(string logDirectory, DateTime startupTime)
        {
            _startupStamp = startupTime.ToString("yyyyMMdd_HHmmss");

            // Каждый запуск — своя подпапка: Logs/20250615_143022/
            var sessionDir = Path.Combine(logDirectory, _startupStamp);
            Directory.CreateDirectory(sessionDir);
            _logDirectory = sessionDir;

            _locks.Clear();
            _paths.Clear();

            foreach (LogCategory cat in Enum.GetValues<LogCategory>())
            {
                _locks[cat] = new SemaphoreSlim(1, 1);
                _paths[cat] = Path.Combine(sessionDir, $"{cat}.log");
            }
        }

        /// <summary>Устанавливает (или снимает) callback для вывода в терминальный UI.</summary>
        public static void SetUiSink(Action<string>? sink) => _uiSink = sink;

        /// <summary>Минимальный уровень для записи в файл (по умолчанию Debug).</summary>
        public static void SetMinLevel(LogLevel level) => _minLevel = level;

        // ───── Публичный API ─────────────────────────────────────────────

        public static void Debug(LogCategory category, string message)
            => Write(LogLevel.Debug, category, message);

        public static void Info(LogCategory category, string message)
            => Write(LogLevel.Info, category, message);

        public static void Warn(LogCategory category, string message)
            => Write(LogLevel.Warn, category, message);

        public static void Error(LogCategory category, string message, Exception? ex = null)
            => Write(LogLevel.Error, category, ex != null ? $"{message}: {ex.Message}" : message);

        public static Task DebugAsync(LogCategory category, string message)
            => WriteAsync(LogLevel.Debug, category, message);

        public static Task InfoAsync(LogCategory category, string message)
            => WriteAsync(LogLevel.Info, category, message);

        public static Task WarnAsync(LogCategory category, string message)
            => WriteAsync(LogLevel.Warn, category, message);

        public static Task ErrorAsync(LogCategory category, string message, Exception? ex = null)
            => WriteAsync(LogLevel.Error, category, ex != null ? $"{message}: {ex.Message}" : message);

        // ───── Ядро ──────────────────────────────────────────────────────

        private static void Write(LogLevel level, LogCategory category, string message)
        {
            if (level < _minLevel) return;
            var line = FormatLine(level, message);
#pragma warning disable CS4014
            AppendToFileAsync(category, line);
#pragma warning restore CS4014
            if (level >= LogLevel.Info)
                _uiSink?.Invoke(UiPrefix(level) + message);
        }

        private static async Task WriteAsync(LogLevel level, LogCategory category, string message)
        {
            if (level < _minLevel) return;
            var line = FormatLine(level, message);
            await AppendToFileAsync(category, line).ConfigureAwait(false);
            if (level >= LogLevel.Info)
                _uiSink?.Invoke(UiPrefix(level) + message);
        }

        // ───── Форматирование ────────────────────────────────────────────

        private static string FormatLine(LogLevel level, string message)
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var lvl  = LevelLabel(level);
            return $"[{time}] [{lvl}] {message}";
        }

        private static string LevelLabel(LogLevel level) => level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info  => "INFO ",
            LogLevel.Warn  => "WARN ",
            LogLevel.Error => "ERROR",
            _              => "?????",
        };

        private static string UiPrefix(LogLevel level) => level switch
        {
            LogLevel.Warn  => "⚠️ ",
            LogLevel.Error => "❌ ",
            _              => string.Empty,
        };

        // ───── Запись в файл ─────────────────────────────────────────────

        private static async Task AppendToFileAsync(LogCategory category, string line)
        {
            if (!_paths.TryGetValue(category, out var path)) return;
            var sem = _locks[category];

            await sem.WaitAsync().ConfigureAwait(false);
            try
            {
                // Ротация по размеру — переименовываем, продолжаем в том же имени
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    if (fi.Length > _maxFileSizeBytes)
                    {
                        var rotated = path.Replace(".log", $"_rot_{DateTime.Now:HHmmss}.log");
                        File.Move(path, rotated);
                    }
                }

                await File.AppendAllTextAsync(path, line + Environment.NewLine, Encoding.UTF8)
                          .ConfigureAwait(false);
            }
            catch
            {
                // Не падаем из-за ошибок файлового вывода
            }
            finally
            {
                sem.Release();
            }
        }

        // ───── Вспомогательное ───────────────────────────────────────────

        public static async Task WriteBatchAsync(LogCategory category, IEnumerable<string> lines)
        {
            foreach (var line in lines)
                await WriteAsync(LogLevel.Info, category, line).ConfigureAwait(false);
        }

        public static void Shutdown(string reason)
        {
            _ = AppendToFileAsync(LogCategory.Boot,
                FormatLine(LogLevel.Info, $"=== Логгер завершён: {reason} ==="));
        }
    }
}
