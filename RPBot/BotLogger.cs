using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

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
    Rolls,
    }

    public static class LogCategoryExtensions
    {
    /// <summary>События, порождённые действиями пользователей (броски, сессии, прогнозы).</summary>
    public static bool IsUser(this LogCategory cat) => cat switch
    {
        LogCategory.Rolls    => true,
        LogCategory.Session  => true,
        LogCategory.Predict  => true,
        LogCategory.Points   => true,
        _                     => false,
    };
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
        // Единый файл терминального лога (logs/run.log) — сюда зеркалируются ВСЕ
        // записи BotLogger + StartupRenderer, в порядке появления. Удобно копировать
        // и пересылать, не собирая по категориям.
        private static string?        _unifiedLogPath;
        private static readonly SemaphoreSlim _unifiedLock = new(1, 1);
        private static readonly ConcurrentDictionary<Guid, Action<BotLogRecord>> _observers = new();
        // Обратный индекс для O(1) удаления observer по ссылке
        private static readonly ConcurrentDictionary<Action<BotLogRecord>, List<Guid>> _observerBackRef = new();

        // ───── Инициализация ──────────────────────────────────────────────

        private static readonly object _initLock = new();

        /// <summary>
        /// Вызывается один раз при запуске/рестарте.
        /// Создаёт имена файлов вида: <Category>_yyyyMMdd_HHmmss.log
        /// Дополнительно открывает единый файл терминального лога logs/run.log
        /// внутри той же сессионной папки — туда зеркалируется всё, что идёт в UI.
        /// </summary>
        public static void Initialize(string logDirectory, DateTime startupTime)
        {
            lock (_initLock)
            {
                _startupStamp = startupTime.ToString("yyyyMMdd_HHmmss");

                // Каждый запуск — своя подпапка: Logs/20250615_143022/
                var sessionDir = Path.Combine(logDirectory, _startupStamp);
                Directory.CreateDirectory(sessionDir);
                _logDirectory = sessionDir;

                // Единый файл-зеркало терминала — единственное место, где собираются
                // ВСЕ сообщения от BotLogger + StartupRenderer. Имя фиксированное,
                // чтобы пользователь всегда знал, куда смотреть.
                _unifiedLogPath = Path.Combine(logDirectory, "run.log");
                try
                {
                    // Перезаписываем run.log при старте, чтобы не путаться со старыми запусками.
                    // Подробные категорийные логи остаются в подпапке.
                    File.WriteAllText(_unifiedLogPath, $"=== Бот запускается: {startupTime:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}", Encoding.UTF8);
                }
                catch { }

                foreach (LogCategory cat in Enum.GetValues<LogCategory>())
                {
                    if (!_locks.TryGetValue(cat, out var existing))
                    {
                        _locks[cat] = new SemaphoreSlim(1, 1);
                    }
                    _paths[cat] = Path.Combine(sessionDir, $"{cat}.log");
                }
            }
        }

        /// <summary>
        /// Публичный путь к единому лог-файлу. Любой sink (в т.ч. StartupRenderer)
        /// может позвать этот метод и писать туда же, куда пишет BotLogger.
        /// </summary>
        public static string? UnifiedLogPath => _unifiedLogPath;

        /// <summary>
        /// Дописывает строку в единый файл-зеркало терминала. Потокобезопасно.
        /// Используется и BotLogger-ом, и внешними sinks (StartupRenderer), чтобы
        /// гарантировать единый порядок строк между источниками.
        /// </summary>
        public static async Task WriteUnifiedLineAsync(string line)
        {
            if (string.IsNullOrEmpty(_unifiedLogPath)) return;
            await _unifiedLock.WaitAsync().ConfigureAwait(false);
            try
            {
                try
                {
                    File.AppendAllText(_unifiedLogPath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
            finally
            {
                try { _unifiedLock.Release(); } catch { }
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

                /// <summary>
                /// Синхронный Info, ожидающий завершения записи на диск.
                /// Используется в тестах, чтобы избежать гонки с fire-and-forget фоновой записью.
                /// </summary>
                public static Task WriteTestSync(LogCategory category, string message)
                    => WriteAsync(LogLevel.Info, category, message);

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

        public static Guid RegisterObserver(Action<BotLogRecord> observer)
        {
            var id = Guid.NewGuid();
            _observers[id] = observer;
            // Добавляем в обратный индекс для быстрого удаления
            _observerBackRef.AddOrUpdate(
                observer,
                _ => new List<Guid> { id },
                (_, list) => { lock (list) { list.Add(id); } return list; });
            return id;
        }

        public static void UnregisterObserver(Guid observerId)
        {
            if (_observers.TryRemove(observerId, out var observer))
            {
                // Удаляем из обратного индекса
                if (_observerBackRef.TryGetValue(observer, out var list))
                {
                    lock (list)
                    {
                        list.Remove(observerId);
                        if (list.Count == 0)
                            _observerBackRef.TryRemove(observer, out _);
                    }
                }
            }
        }

        public static void UnregisterObserver(Action<BotLogRecord> observer)
        {
            if (!_observerBackRef.TryRemove(observer, out var ids))
                return;
            lock (ids)
            {
                foreach (var id in ids)
                    _observers.TryRemove(id, out _);
            }
        }

        // ───── Ядро ──────────────────────────────────────────────────────

        private static void Write(LogLevel level, LogCategory category, string message)
        {
            if (level < _minLevel) return;
            var line = FormatLine(level, message);
#pragma warning disable CS4014
            AppendToFileAsync(category, line);
            // Зеркалим в единый run.log, чтобы всё (старт + рантайм) было в одном месте.
            _ = WriteUnifiedLineAsync(line);
#pragma warning restore CS4014
            NotifyObservers(new BotLogRecord(DateTimeOffset.Now, level, category, message, line, category.IsUser()));
            if (level >= LogLevel.Info)
                _uiSink?.Invoke(UiPrefix(level) + message);
        }

        private static async Task WriteAsync(LogLevel level, LogCategory category, string message)
        {
            if (level < _minLevel) return;
            var line = FormatLine(level, message);
            await AppendToFileAsync(category, line).ConfigureAwait(false);
            await WriteUnifiedLineAsync(line).ConfigureAwait(false);
            NotifyObservers(new BotLogRecord(DateTimeOffset.Now, level, category, message, line, category.IsUser()));
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

        private static ConsoleColor LevelColor(LogLevel level) => level switch
        {
            LogLevel.Debug => ConsoleColor.DarkGray,
            LogLevel.Info  => ConsoleColor.Cyan,
            LogLevel.Warn  => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            _              => ConsoleColor.Gray,
        };

        private static ConsoleColor CategoryColor(LogCategory category) => category switch
        {
            LogCategory.Rolls   => ConsoleColor.Magenta,
            LogCategory.Music   => ConsoleColor.Green,
            LogCategory.Predict => ConsoleColor.DarkCyan,
            LogCategory.Points  => ConsoleColor.DarkMagenta,
            LogCategory.Session => ConsoleColor.DarkGreen,
            LogCategory.System  => ConsoleColor.Gray,
            LogCategory.Boot    => ConsoleColor.White,
            LogCategory.Discord => ConsoleColor.Blue,
            LogCategory.Sheets  => ConsoleColor.DarkYellow,
            _                   => ConsoleColor.Gray,
        };

        private static void NotifyObservers(BotLogRecord record)
        {
            foreach (var observer in _observers.Values)
            {
                try
                {
                    observer(record);
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.System, $"[Observer] Исключение в observer: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

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
