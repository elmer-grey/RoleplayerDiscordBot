using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>
    /// Сервис записи статистики игровых сессий в Google Таблицу.
    ///
    /// Структура листа (столбцы A–J):
    ///   A – Дата           (дата старта, формат dd.MM.yyyy)
    ///   B – Мастер         (имя мастера, раскрывающийся список)
    ///   C – Название       (название игры, раскрывающийся список)
    ///   D – Время старта   (HH:mm)
    ///   E – Время конца    (HH:mm)
    ///   F – Время перерывов (строка вида "15 мин 30 сек" или "-")
    ///   G – Статус         (формула таблицы, не трогаем)
    ///   H – Продолжительность (формула таблицы, не трогаем)
    ///   I – Место проведения (ручное, не трогаем)
    ///   J – Примечание     (ручное, не трогаем)
    ///
    /// Стратегия поиска строки для записи:
    ///   Ищем первую строку начиная с GoogleSheetDataStartRow, у которой
    ///   в колонке A стоит "-" (заглушка незаполненной строки).
    /// </summary>
    public class GoogleSheetsService
    {
        // Индексы столбцов (0-based)
        private const int ColDate        = 0; // A
        private const int ColMaster      = 1; // B
        private const int ColTitle       = 2; // C
        private const int ColTimeStart   = 3; // D
        private const int ColTimeEnd     = 4; // E
        private const int ColPause       = 5; // F
        // G (6) и H (7) — формулы таблицы, не пишем
        // I (8) — место проведения, не пишем
        // J (9) — примечание, не пишем

        private readonly SpreadsheetsResource _sheets;
        private readonly string _spreadsheetId;
        private readonly string _sheetName;
        private readonly int _dataStartRow; // 1-based (обычно 2)
        private readonly SemaphoreSlim _lock = new(1, 1);

        public Action<string>? LogSink { get; set; }

        private GoogleSheetsService(
            SpreadsheetsResource sheets,
            string spreadsheetId,
            string sheetName,
            int dataStartRow)
        {
            _sheets = sheets;
            _spreadsheetId = spreadsheetId;
            _sheetName = sheetName;
            _dataStartRow = dataStartRow;
        }

        /// <summary>
        /// Создаёт экземпляр сервиса по данным из BotConfig.
        /// Возвращает null если Google Sheets не настроен или отключён.
        /// </summary>
        public static GoogleSheetsService? TryCreate(BotConfig cfg)
        {
            try
            {
                if (cfg == null || !cfg.GoogleSheetsEnabled)
                    return null;

                if (string.IsNullOrWhiteSpace(cfg.GoogleSpreadsheetId))
                {
                    Console.WriteLine("[GoogleSheets] GoogleSpreadsheetId не задан в config.json — интеграция отключена.");
                    return null;
                }

                var credPath = BotConfig.ResolvePath(
                    string.IsNullOrWhiteSpace(cfg.GoogleSheetsCredentialsPath)
                        ? "google_credentials.json"
                        : cfg.GoogleSheetsCredentialsPath);

                if (!File.Exists(credPath))
                {
                    Console.WriteLine($"[GoogleSheets] Файл credentials не найден: {credPath} — интеграция отключена.");
                    return null;
                }

                GoogleCredential credential;
                using (var stream = new FileStream(credPath, FileMode.Open, FileAccess.Read))
                {
                    credential = GoogleCredential
                        .FromStream(stream)
                        .CreateScoped(SheetsService.Scope.Spreadsheets);
                }

                var service = new SheetsService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "RPBot"
                });

                return new GoogleSheetsService(
                    service.Spreadsheets,
                    cfg.GoogleSpreadsheetId,
                    string.IsNullOrWhiteSpace(cfg.GoogleSheetName) ? "2026 год" : cfg.GoogleSheetName,
                    cfg.GoogleSheetDataStartRow > 0 ? cfg.GoogleSheetDataStartRow : 2);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GoogleSheets] Ошибка инициализации: {ex.Message}");
                return null;
            }
        }

        // ─── Публичный API ────────────────────────────────────────────────

        /// <summary>
        /// Записывает завершённую сессию в таблицу.
        /// Возвращает 1-based номер строки, куда была сделана запись,
        /// или -1 при ошибке / если строка-заглушка не найдена.
        /// </summary>
        public async Task<int> AppendSessionAsync(GameSession session)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                Log($"Поиск свободной строки для сессии \"{session.GameName}\"...");

                int row = await FindPlaceholderRowAsync().ConfigureAwait(false);
                if (row < 0)
                {
                    Log("Свободная строка-заглушка не найдена. Добавляю новую строку в конец таблицы.");
                    row = await FindFirstEmptyRowAsync().ConfigureAwait(false);
                    if (row < 0)
                    {
                        Log("Не удалось определить строку для записи.");
                        return -1;
                    }
                }

                var values = BuildRowValues(session);
                await WriteRowAsync(row, values).ConfigureAwait(false);

                Log($"Сессия \"{session.GameName}\" записана в строку {row}.");
                return row;
            }
            catch (Exception ex)
            {
                Log($"Ошибка при записи сессии: {ex.Message}");
                return -1;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Обновляет поля Мастер и Название в уже записанной строке.
        /// Вызывается когда мастер меняет параметры через кнопку "Изменить".
        /// </summary>
        public async Task UpdateSessionRowAsync(GameSession session)
        {
            if (session.SheetRowIndex <= 0)
                return;

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                Log($"Обновление строки {session.SheetRowIndex} для сессии \"{session.GameName}\"...");

                // Обновляем только колонки B (Мастер) и C (Название)
                var rangeB = $"'{_sheetName}'!B{session.SheetRowIndex}";
                var rangeC = $"'{_sheetName}'!C{session.SheetRowIndex}";

                var data = new List<ValueRange>
                {
                    new ValueRange
                    {
                        Range = rangeB,
                        Values = new List<IList<object>> { new List<object> { session.MasterName } }
                    },
                    new ValueRange
                    {
                        Range = rangeC,
                        Values = new List<IList<object>> { new List<object> { session.GameName } }
                    }
                };

                var batchRequest = _sheets.Values.BatchUpdate(
                    new BatchUpdateValuesRequest
                    {
                        ValueInputOption = "USER_ENTERED",
                        Data = data
                    },
                    _spreadsheetId);

                await batchRequest.ExecuteAsync().ConfigureAwait(false);
                Log($"Строка {session.SheetRowIndex} обновлена (мастер: {session.MasterName}, название: {session.GameName}).");
            }
            catch (Exception ex)
            {
                Log($"Ошибка при обновлении строки {session.SheetRowIndex}: {ex.Message}");
            }
            finally
            {
                _lock.Release();
            }
        }

        // ─── Приватные методы ─────────────────────────────────────────────

        /// <summary>
        /// Ищет первую строку начиная с _dataStartRow, где в колонке A стоит "-".
        /// Возвращает 1-based номер строки или -1 если не найдена.
        /// </summary>
        private async Task<int> FindPlaceholderRowAsync()
        {
            try
            {
                // Читаем весь столбец A начиная со строки данных
                var range = $"'{_sheetName}'!A{_dataStartRow}:A";
                var request = _sheets.Values.Get(_spreadsheetId, range);
                request.ValueRenderOption = SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.UNFORMATTEDVALUE;
                var response = await request.ExecuteAsync().ConfigureAwait(false);

                if (response?.Values == null)
                    return _dataStartRow; // лист пустой — пишем с первой строки данных

                for (int i = 0; i < response.Values.Count; i++)
                {
                    var cell = response.Values[i].Count > 0
                        ? (response.Values[i][0]?.ToString() ?? "")
                        : "";

                    if (cell.Trim() == "-")
                        return _dataStartRow + i; // 1-based
                }

                return -1; // заглушки кончились
            }
            catch (Exception ex)
            {
                Log($"Ошибка при поиске строки-заглушки: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Находит первую строку после всех данных (для дозаписи если заглушки кончились).
        /// </summary>
        private async Task<int> FindFirstEmptyRowAsync()
        {
            try
            {
                var range = $"'{_sheetName}'!A{_dataStartRow}:A";
                var request = _sheets.Values.Get(_spreadsheetId, range);
                request.ValueRenderOption = SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.UNFORMATTEDVALUE;
                var response = await request.ExecuteAsync().ConfigureAwait(false);

                if (response?.Values == null)
                    return _dataStartRow;

                // Первая строка, где ячейка пустая или отсутствует
                for (int i = 0; i < response.Values.Count; i++)
                {
                    var cell = response.Values[i].Count > 0
                        ? (response.Values[i][0]?.ToString() ?? "")
                        : "";
                    if (string.IsNullOrWhiteSpace(cell))
                        return _dataStartRow + i;
                }

                // Все строки заняты — добавляем в конец
                return _dataStartRow + response.Values.Count;
            }
            catch (Exception ex)
            {
                Log($"Ошибка при поиске пустой строки: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Формирует список значений строки из данных сессии.
        /// Пишем только колонки A–F (индексы 0–5), G–J не трогаем.
        /// </summary>
        private static IList<object> BuildRowValues(GameSession session)
        {
            var pauseText = BuildPauseText(session);

            return new List<object>
            {
                session.StartTime.ToString("dd.MM.yyyy"),          // A – Дата
                session.MasterName ?? "",                           // B – Мастер
                session.GameName ?? "",                             // C – Название
                session.StartTime.ToString("HH:mm"),               // D – Время старта
                session.EndTime.HasValue
                    ? session.EndTime.Value.ToString("HH:mm")
                    : "",                                           // E – Время конца
                pauseText                                           // F – Время перерывов
            };
        }

        /// <summary>
        /// Форматирует суммарное время перерывов.
        /// Возвращает "-" если перерывов не было.
        /// </summary>
        private static string BuildPauseText(GameSession session)
        {
            var completedPauses = session.PausePeriods
                .Where(p => p.End.HasValue)
                .ToList();

            if (completedPauses.Count == 0)
                return "-";

            var totalSeconds = completedPauses
                .Sum(p => (p.End!.Value - p.Start).TotalSeconds);

            var ts = TimeSpan.FromSeconds(totalSeconds);
            var hours   = (int)ts.TotalHours;
            var minutes = ts.Minutes;
            var seconds = ts.Seconds;

            var parts = new List<string>();
            if (hours > 0)   parts.Add($"{hours} ч");
            if (minutes > 0) parts.Add($"{minutes} мин");
            if (seconds > 0 && hours == 0) parts.Add($"{seconds} сек"); // секунды только если меньше часа

            return parts.Count > 0 ? string.Join(" ", parts) : "< 1 мин";
        }

        /// <summary>
        /// Записывает значения в строку row (1-based), колонки A–F.
        /// </summary>
        private async Task WriteRowAsync(int row, IList<object> values)
        {
            var range = $"'{_sheetName}'!A{row}:F{row}";
            var body = new ValueRange
            {
                Range = range,
                Values = new List<IList<object>> { values }
            };

            var request = _sheets.Values.Update(body, _spreadsheetId, range);
            request.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            await request.ExecuteAsync().ConfigureAwait(false);
        }

        private void Log(string message)
        {
            LogSink?.Invoke($"[GoogleSheets] {message}");
            Console.WriteLine($"[GoogleSheets] {message}");
        }
    }
}
