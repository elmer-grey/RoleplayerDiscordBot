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
                    BotLogger.Warn(LogCategory.Sheets, "GoogleSpreadsheetId не задан в config.json — интеграция отключена.");
                    return null;
                }

                var credPath = BotConfig.ResolvePath(
                    string.IsNullOrWhiteSpace(cfg.GoogleSheetsCredentialsPath)
                        ? "google_credentials.json"
                        : cfg.GoogleSheetsCredentialsPath);

                if (!File.Exists(credPath))
                {
                    BotLogger.Warn(LogCategory.Sheets, $"Файл credentials не найден: {credPath} — интеграция отключена.");
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
                BotLogger.Error(LogCategory.Sheets, $"Ошибка инициализации: {ex.Message}");
                return null;
            }
        }

        // ─── Публичный API ────────────────────────────────────────────────

        /// <summary>
        /// Проверяет доступность таблицы: авторизация, наличие листа, права на запись.
        /// Используется в чек-листе при запуске бота.
        /// </summary>
        public async Task<(bool Success, string Message)> ProbeAsync()
        {
            try
            {
                // Шаг 1: читаем заголовок листа (1 ячейка) — проверяем авторизацию и доступ
                var readRange = $"'{_sheetName}'!A1";
                var readRequest = _sheets.Values.Get(_spreadsheetId, readRange);
                readRequest.ValueRenderOption = SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.FORMATTEDVALUE;
                var readResponse = await readRequest.ExecuteAsync().ConfigureAwait(false);

                // Шаг 2: пробная запись — проверяем права на запись
                // Пишем в ячейку-«ping» вне диапазона данных, например Z1
                var pingRange = $"'{_sheetName}'!Z1";
                var pingBody = new ValueRange
                {
                    Range = pingRange,
                    Values = new List<IList<object>> { new List<object> { "" } }
                };
                var writeRequest = _sheets.Values.Update(pingBody, _spreadsheetId, pingRange);
                writeRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
                await writeRequest.ExecuteAsync().ConfigureAwait(false);

                var sheetId = string.IsNullOrWhiteSpace(_sheetName) ? "(default)" : _sheetName;
                return (true, $"OK — лист «{sheetId}», чтение и запись доступны");
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (false, $"Таблица не найдена (SpreadsheetId={_spreadsheetId})");
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return (false, "Нет доступа — выдай права Редактора сервисному аккаунту");
            }
            catch (Google.GoogleApiException ex)
            {
                return (false, $"Google API: {ex.HttpStatusCode} — {ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, $"Ошибка: {ex.Message}");
            }
        }

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
                Log($"[Sheets] Поиск строки-заглушки для сессии \"{session.GameName}\"...");

                int row = await FindPlaceholderRowAsync().ConfigureAwait(false);
                if (row < 0)
                {
                    Log("[Sheets] Заглушка не найдена — ищу первую пустую строку.");
                    row = await FindFirstEmptyRowAsync().ConfigureAwait(false);
                    if (row < 0)
                    {
                        Log("[Sheets] Не удалось определить строку для записи.");
                        return -1;
                    }
                }

                var values = BuildRowValues(session);
                Log($"[Sheets] Запись в строку {row}: {string.Join(" | ", values.Select(v => v?.ToString() ?? "-"))}");
                await WriteRowAsync(row, values).ConfigureAwait(false);

                Log($"[Sheets] Запись завершена, строка {row}.");
                return row;
            }
            catch (Exception ex)
            {
                Log($"[Sheets] Ошибка при записи сессии: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
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
                Log($"[Sheets] Обновление строки {session.SheetRowIndex} (мастер: {session.MasterName}, название: {session.GameName})...");

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
                Log($"[Sheets] Строка {session.SheetRowIndex} обновлена.");
            }
            catch (Exception ex)
            {
                Log($"[Sheets] Ошибка при обновлении строки {session.SheetRowIndex}: {ex.Message}");
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

                Log($"[Sheets] Прочитано {response.Values.Count} строк в столбце A.");

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
                Log($"[Sheets] Ошибка при поиске строки-заглушки: {ex.GetType().Name}: {ex.Message}");
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
                Log($"[Sheets] Ошибка при поиске пустой строки: {ex.Message}");
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
            var startMsk = ToMoscowTime(session.StartTime);
            var endMsk = session.EndTime.HasValue ? ToMoscowTime(session.EndTime.Value) : (DateTime?)null;

            return new List<object>
            {
                startMsk.ToString("dd/MM"),                        // A – Дата (МСК)
                session.MasterName ?? "",                          // B – Мастер
                session.GameName ?? "",                            // C – Название
                startMsk.ToString("HH:mm"),                        // D – Время старта (МСК)
                endMsk.HasValue
                    ? endMsk.Value.ToString("HH:mm")
                    : "",                                          // E – Время конца (МСК)
                pauseText                                            // F – Время перерывов
            };
        }

        private static DateTime ToMoscowTime(DateTime dt)
        {
            var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            var candidates = new[] { "Europe/Moscow", "Russian Standard Time" };
            foreach (var id in candidates)
            {
                try
                {
                    var tz = TimeZoneInfo.FindSystemTimeZoneById(id);
                    return TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
                }
                catch
                {
                }
            }

            return utc;
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
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}";
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
        }
    }
}
