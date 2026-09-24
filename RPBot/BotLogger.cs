using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

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

                // ✅ Round 7-C10: двойная схема логов.
                //
                //   1) Общий непрерывный run.log в корне Logs/
                //      — дописывается с маркером `=== Restart #N: ... ===` между сессиями.
                //      — удобно, когда нужно увидеть «полный хвост» за дни/недели.
                //
                //   2) Копия run.log внутри каждой сессионной папки Logs/<yyyyMMdd_HHmmss>/
                //      — туда зеркалируется всё содержимое ОБЩЕГО run.log за время этой сессии.
                //      — удобно, когда нужен только конкретный запуск без хвоста от соседей.
                //
                // Пользователь знает оба файла: «общий с маркерами» и «внутри папки запуска».
                // Оба пишутся строго в порядке записи (через общий _unifiedLock).
                _unifiedLogPath = Path.Combine(logDirectory, "run.log");
                try
                {
                    if (File.Exists(_unifiedLogPath))
                    {
                        // Считаем, какой по счёту это рестарт — по числу уже записанных маркеров.
                        // Это грубая эвристика (считаем все вхождения "=== Restart #"), но для
                        // пользовательского лога этого достаточно.
                        int restartNumber = 1;
                        try
                        {
                            var existing = File.ReadAllText(_unifiedLogPath);
                            int idx = 0;
                            while ((idx = existing.IndexOf("=== Restart #", idx, StringComparison.Ordinal)) >= 0)
                            {
                                restartNumber++;
                                idx++;
                            }
                        }
                        catch { /* если не смогли прочитать — оставляем 1 */ }
                        var marker = $"=== Restart #{restartNumber}: {startupTime:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}";
                        File.AppendAllText(_unifiedLogPath, marker, Encoding.UTF8);
                        // ✅ Round 7-C10: продублировать тот же маркер в сессионную копию.
                        // Это первый лайн per-session файла — пользователь сразу видит,
                        // с какого момента начинается запись внутри Logs/<stamp>/run.log.
                        try
                        {
                            File.AppendAllText(Path.Combine(sessionDir, "run.log"), marker, Encoding.UTF8);
                        }
                        catch { }
                    }
                    else
                    {
                        var header = $"=== Бот запускается: {startupTime:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}";
                        File.WriteAllText(_unifiedLogPath, header, Encoding.UTF8);
                        // ✅ Round 7-C10: первая сессия — общий run.log и per-session run.log
                        // стартуют с одного и того же заголовка.
                        try
                        {
                            File.WriteAllText(Path.Combine(sessionDir, "run.log"), header, Encoding.UTF8);
                        }
                        catch { }
                    }
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
        /// ✅ Round 7-C10: путь к per-session копии run.log внутри сессионной папки.
        /// Если сессионная папка ещё не создана (Initialize не вызывался) — вернёт null.
        /// </summary>
        public static string? SessionRunLogPath
        {
            get
            {
                if (string.IsNullOrEmpty(_logDirectory)) return null;
                return Path.Combine(_logDirectory, "run.log");
            }
        }

        /// <summary>
        /// Дописывает строку в единый файл-зеркало терминала. Потокобезопасно.
        /// Используется и BotLogger-ом, и внешними sinks (StartupRenderer), чтобы
        /// гарантировать единый порядок строк между источниками.
        /// ✅ Round 7-C10: пишет и в общий run.log (с маркерами), и в per-session run.log.
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
                    // ✅ Round 7-C10: параллельная запись в per-session копию.
                    // Тот же лайн в том же порядке. Делаем best-effort: если per-session
                    // файл недоступен — общий run.log остаётся источником правды.
                    var sessionPath = SessionRunLogPath;
                    if (!string.IsNullOrEmpty(sessionPath) && !string.Equals(sessionPath, _unifiedLogPath, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            File.AppendAllText(sessionPath, line + Environment.NewLine, Encoding.UTF8);
                        }
                        catch { /* per-session файл необязательный */ }
                    }
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

        // ───── Единый sink для StartupRenderer ─────────────────────────────
        // Дёргается из BotLoggerSink. Делает то же, что и Write: пишет в run.log,
        // в категорийный файл, отдаёт в observer-канал и UI-sink. Один путь для
        // всех каналов → никаких дублей. До этого StartupRenderer-у подключались
        // ConsoleSink + FileSink + UiSink + BotLoggerSink, и BotLoggerSink тоже
        // писал в UI — получалось две копии в терминале.
        internal static void WriteStartupFull(LogLevel level, string message)
        {
            if (level < _minLevel) return;
            var category = LogCategory.Boot;
            var line = FormatLine(level, category, message);
#pragma warning disable CS4014
            AppendToFileAsync(category, line);
            // Зеркалим в единый run.log, чтобы было в одном месте рядом с
            // обычными runtime-записями BotLogger. Это и было целью переноса
            // стартап-логов в observer — пользователь видит их и в дашборде,
            // и в одном файле run.log.
            _ = WriteUnifiedLineAsync(line);
#pragma warning restore CS4014
            var record = new BotLogRecord(DateTimeOffset.Now, level, category, message, line, category.IsUser());
            NotifyObservers(record);
            if (level >= LogLevel.Info)
                _uiSink?.Invoke(UiPrefix(level) + message);
        }

        // ───── Ядро ──────────────────────────────────────────────────────

        private static void Write(LogLevel level, LogCategory category, string message)
                {
                    if (level < _minLevel) return;
                    var line = FormatLine(level, category, message);
        #pragma warning disable CS4014
                    AppendToFileAsync(category, line);
                    // Зеркалим в единый run.log, чтобы всё (старт + рантайм) было в одном месте.
                    _ = WriteUnifiedLineAsync(line);
        #pragma warning restore CS4014
                    var record = new BotLogRecord(DateTimeOffset.Now, level, category, message, line, category.IsUser());
                    // Подписчики (дашборд, тесты) получают запись напрямую — без хвоста из файла,
                    // без парсера. Это единая точка правды: всё, что попало в Write,
                    // попадает и в observer-канал, и в run.log.
                    NotifyObservers(record);
                    if (level >= LogLevel.Info)
                        _uiSink?.Invoke(UiPrefix(level) + message);
                }

                private static async Task WriteAsync(LogLevel level, LogCategory category, string message)
                {
                    if (level < _minLevel) return;
                    var line = FormatLine(level, category, message);
                    await AppendToFileAsync(category, line).ConfigureAwait(false);
                    await WriteUnifiedLineAsync(line).ConfigureAwait(false);
                    var record = new BotLogRecord(DateTimeOffset.Now, level, category, message, line, category.IsUser());
                    NotifyObservers(record);
                    if (level >= LogLevel.Info)
                        _uiSink?.Invoke(UiPrefix(level) + message);
                }

        // ───── Форматирование ────────────────────────────────────────────

        private static string FormatLine(LogLevel level, LogCategory category, string message)
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var lvl  = LevelLabel(level);
            var cat  = category.ToString();
            // После [LEVEL] всегда пробел — парсер TryParseFormattedLine опирается на это,
            // чтобы отличить лог от произвольных строк с квадратными скобками. Сразу за ним
            // стоит [Category] (или сразу сообщение, если категория — System по умолчанию).
            return $"[{time}] [{lvl}] [{cat}] {message}";
        }

        // Все метки ровно 5 символов без хвостовых пробелов — парсер TryParseFormattedLine
        // сравнивает их напрямую со switch ("INFO"/"WARN"/"DEBUG"/"ERROR").
        private static string LevelLabel(LogLevel level) => level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info  => "INFO",
            LogLevel.Warn  => "WARN",
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

                    try
                    {
                        await sem.WaitAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) { return; /* логгер уже погашен */ }

                    try
                    {
                        // Ротация по размеру — переименовываем, продолжаем в том же имени
                        if (File.Exists(path))
                        {
                            var fi = new FileInfo(path);
                            if (fi.Length > _maxFileSizeBytes)
                            {
                                var rotated = path.Replace(".log", $"_rot_{DateTime.Now:HHmmss}.log");
                                try { File.Move(path, rotated); } catch { /* ротация best-effort */ }
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
                        try { sem.Release(); } catch (ObjectDisposedException) { }
                    }
                }

        // ───── Вспомогательное ───────────────────────────────────────────

        public static async Task WriteBatchAsync(LogCategory category, IEnumerable<string> lines)
        {
            foreach (var line in lines)
                await WriteAsync(LogLevel.Info, category, line).ConfigureAwait(false);
        }

        /// <summary>
        /// Финальная остановка логгера: помечает завершение сессии и обнуляет пути,
        /// чтобы фоновые записи после Shutdown не восстанавливали категорийные файлы.
        /// Идемпотентен.
        /// </summary>
        public static void Shutdown(string reason)
                {
                    lock (_initLock)
                    {
                        _ = AppendToFileAsync(LogCategory.Boot,
                            FormatLine(LogLevel.Info, LogCategory.Boot, $"=== Логгер завершён: {reason} ==="));
                        _paths.Clear();
                        _logDirectory = null;
                        _unifiedLogPath = null;
                        // ✅ Audit-leaks #6: Dispose семафоров категорийных локов.
                        // Без этого при горячем рестарте _locks накапливает SemaphoreSlim-ы
                        // (на каждый Foreach(Enum.GetValues<LogCategory>()) — новый набор).
                        // AppendToFileAsync уже корректно обрабатывает ObjectDisposedException.
                        foreach (var kv in _locks)
                        {
                            try { kv.Value.Dispose(); } catch { /* идемпотентно */ }
                        }
                        _locks.Clear();
                        // ✅ Round 7-C10: per-session run.log продолжает жить внутри своей папки
                        // и после Shutdown — пользователь всё ещё может его открыть. Ничего
                        // дополнительно не делаем: WriteUnifiedLineAsync смотрит на _logDirectory,
                        // и когда он null, запись просто игнорируется.
                    }
                }

        // ───── Хвост из run.log удалён. Дашборд подписан напрямую на observer
        // BotLogger (см. RegisterObserver) — это надёжнее, чем парсить файл.
        // Что попало в Write/WriteAsync, попадает и в observer-канал, и в run.log.
    }
}
