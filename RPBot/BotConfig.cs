using Discord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.IO;
using RPBot.Util;

namespace RPBot
{
    public class BotConfig
    {
		// Папка Settings — содержит конфиги (config.json, serverconfigs.json, master_guide_*.txt, Pastes.txt).
		public const string SettingsFolderName = "Settings";

		// Папка Data — файлы состояния бота (scores, queues, sessions, плейлисты и т.д.)
		public const string DataFolderName = "Data";

		// Папка Logs — логи бота (по умолчанию).
		public const string LogsFolderName = "Logs";

		// Имя переменной окружения для принудительного переопределения корня данных.
		// Если задана — её значение используется как корневой каталог для Settings/, Data/, Logs/.
		// Если не задана — на Windows используется %LOCALAPPDATA%\RPBot,
		// на Linux/macOS — ~/.local/share/rpbot.
		public const string DataRootEnvVar = "RPBOT_DATA_DIR";

		// Имя приложения внутри LocalAppData / $XDG_DATA_HOME.
		public const string AppDataSubdirectory = "RPBot";

		// Кэш для вычисленного корня данных (один раз на процесс).
		private static string? _dataRootOverride;
		private static string? _dataRootDefault;

		/// <summary>
		/// Корневой каталог для runtime-данных бота (Settings/, Data/, Logs/).
		/// Приоритет: переменная окружения RPBOT_DATA_DIR → %LOCALAPPDATA%\RPBot (Windows)
		/// или ~/.local/share/rpbot (Linux/macOS).
		/// Каталог создаётся при первом обращении.
		/// </summary>
		public static string GetDataRootDirectory()
		{
			if (_dataRootOverride != null) return _dataRootOverride;

			var fromEnv = Environment.GetEnvironmentVariable(DataRootEnvVar);
			if (!string.IsNullOrWhiteSpace(fromEnv))
			{
				_dataRootOverride = fromEnv;
			}
			else
			{
				_dataRootDefault ??= ComputeDefaultDataRoot();
				_dataRootOverride = _dataRootDefault;
			}

			Directory.CreateDirectory(_dataRootOverride!);
			return _dataRootOverride!;
		}

		private static string ComputeDefaultDataRoot()
		{
			if (OperatingSystem.IsWindows())
			{
				var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
					Environment.SpecialFolderOption.Create);
				return Path.Combine(localAppData, AppDataSubdirectory);
			}
			// Linux / macOS — XDG_DATA_HOME или ~/.local/share
			var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
			if (!string.IsNullOrWhiteSpace(xdg))
				return Path.Combine(xdg, "rpbot");
			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			return Path.Combine(home, ".local", "share", "rpbot");
		}

		/// <summary>
		/// Каталог для статических ресурсов, идущих рядом с .exe
		/// (Lavalink/application.yml, Web/dashboard.html).
		/// </summary>
		public static string GetStaticDirectory() => AppContext.BaseDirectory;

		public static string GetSettingsDirectory()
		{
			var dir = Path.Combine(GetDataRootDirectory(), SettingsFolderName);
			Directory.CreateDirectory(dir);
			return dir;
		}

		public static string GetDataDirectory()
		{
			var dir = Path.Combine(GetDataRootDirectory(), DataFolderName);
			Directory.CreateDirectory(dir);
			return dir;
		}

		public static string GetLogsDirectory()
		{
			var dir = Path.Combine(GetDataRootDirectory(), LogsFolderName);
			Directory.CreateDirectory(dir);
			return dir;
		}

		/// <summary>
		/// Преобразует путь в абсолютный.
		/// Относительные пути, которые выглядят как runtime-данные (Settings/…, Data/…, Logs/…)
		/// резолвятся относительно корня данных; всё остальное — относительно каталога .exe
		/// (статические ресурсы типа Lavalink/, Web/).
		/// </summary>
		public static string ResolvePath(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return AppContext.BaseDirectory;

			if (Path.IsPathRooted(path)) return path;

			// Runtime-данные — относительно корня данных.
			var trimmed = path.Replace('\\', '/').TrimStart('/');
			if (trimmed.StartsWith(SettingsFolderName + "/", StringComparison.OrdinalIgnoreCase) ||
			    trimmed.StartsWith(DataFolderName + "/", StringComparison.OrdinalIgnoreCase) ||
			    trimmed.StartsWith(LogsFolderName + "/", StringComparison.OrdinalIgnoreCase) ||
			    string.Equals(trimmed, SettingsFolderName, StringComparison.OrdinalIgnoreCase) ||
			    string.Equals(trimmed, DataFolderName, StringComparison.OrdinalIgnoreCase) ||
			    string.Equals(trimmed, LogsFolderName, StringComparison.OrdinalIgnoreCase))
			{
				return Path.Combine(GetDataRootDirectory(), path.Replace('/', Path.DirectorySeparatorChar));
			}

			// Статические ресурсы — относительно .exe.
			return Path.Combine(AppContext.BaseDirectory, path);
		}

		/// <summary>
		/// Одноразовая миграция: переносит файлы данных из старого расположения
		/// (рядом с .exe: bin\Debug\net8.0\Settings, bin\…\Data, bin\…\Logs)
		/// в новое (корневой каталог данных: %LOCALAPPDATA%\RPBot на Windows).
		/// Идемпотентна — если файл уже на новом месте, не трогает.
		/// </summary>
		public static void MigrateDataFiles()
		{
			var oldRoot = AppContext.BaseDirectory;
			var newRoot = GetDataRootDirectory();

			// Если корень данных совпадает с каталогом .exe — мигрировать некуда.
			if (string.Equals(
				Path.GetFullPath(oldRoot).TrimEnd(Path.DirectorySeparatorChar),
				Path.GetFullPath(newRoot).TrimEnd(Path.DirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase))
			{
				return;
			}

			var oldSettingsDir = Path.Combine(oldRoot, SettingsFolderName);
			var oldDataDir = Path.Combine(oldRoot, DataFolderName);
			var oldLogsDir = Path.Combine(oldRoot, LogsFolderName);

			var newSettingsDir = GetSettingsDirectory();
			var newDataDir = GetDataDirectory();

			// Список JSON-файлов данных, которые переезжают из Settings/ в Data/
			var filesToMigrate = new[]
			{
				"bwonks.json",
				"event-notify.json",
				"event_announcements.json",
				"points.json",
				"points_users.json",
				"music_playlists.json",
				"music_queues.json",
				"music_stats.json",
				"predictions_state.json",
				"predictions_history.json",
				"predictions_stats.json",
				"predictions_achievements.json",
							"sessions_state.json",
						};

			foreach (var file in filesToMigrate)
			{
				var src = Path.Combine(oldDataDir, file);
				var dst = Path.Combine(newDataDir, file);
				if (File.Exists(src) && !File.Exists(dst))
				{
					try
					{
						File.Move(src, dst);
						BotLogger.Info(LogCategory.Boot, $"[Migration] Перемещён {file}: Data/ → {newDataDir}");
					}
					catch (Exception ex)
					{
						BotLogger.Error(LogCategory.Boot, $"[Migration] Ошибка перемещения {file}: {ex.Message}");
					}
				}
			}

			// Конфиги (config.json, serverconfigs.json, master_guide_*.txt, Pastes.txt)
			// переезжают из Settings/ → Settings/.
			var settingsFiles = new[]
			{
				"config.json",
				"serverconfigs.json",
				"Pastes.txt",
							"google_credentials.json", // мигрируем из bin\...\google_credentials.json → Settings\
						};
						foreach (var file in settingsFiles)
						{
							var src = Path.Combine(oldSettingsDir, file);
							var dst = Path.Combine(newSettingsDir, file);
							if (File.Exists(src) && !File.Exists(dst))
							{
								try
								{
									File.Move(src, dst);
									BotLogger.Info(LogCategory.Boot, $"[Migration] Перемещён {file}: Settings/ → {newSettingsDir}");
								}
								catch (Exception ex)
								{
									BotLogger.Error(LogCategory.Boot, $"[Migration] Ошибка перемещения {file}: {ex.Message}");
								}
							}
						}

						// google_credentials.json — дополнительно: если лежит рядом с .exe
						// (по старому дефолту из BotConfig.GoogleSheetsCredentialsPath),
						// переносим в Settings/google_credentials.json.
						var legacyCreds = Path.Combine(oldRoot, "google_credentials.json");
						var newCreds = Path.Combine(newSettingsDir, "google_credentials.json");
						if (File.Exists(legacyCreds) && !File.Exists(newCreds))
						{
							try
							{
								File.Move(legacyCreds, newCreds);
								BotLogger.Info(LogCategory.Boot, $"[Migration] Перемещён google_credentials.json: {legacyCreds} → {newCreds}");
							}
							catch (Exception ex)
							{
								BotLogger.Error(LogCategory.Boot, $"[Migration] Ошибка перемещения google_credentials.json: {ex.Message}");
							}
						}

			// master_guide_<guildId>.txt — переносим по glob-шаблону.
			if (Directory.Exists(oldSettingsDir))
			{
				foreach (var file in Directory.GetFiles(oldSettingsDir, "master_guide_*.txt"))
				{
					var dst = Path.Combine(newSettingsDir, Path.GetFileName(file));
					if (!File.Exists(dst))
					{
						try
						{
							File.Move(file, dst);
							BotLogger.Info(LogCategory.Boot, $"[Migration] Перемещён {Path.GetFileName(file)}: Settings/ → {newSettingsDir}");
						}
						catch (Exception ex)
						{
							BotLogger.Error(LogCategory.Boot, $"[Migration] Ошибка перемещения {Path.GetFileName(file)}: {ex.Message}");
						}
					}
				}
			}

			// Numbers/ (папка с картинками кубиков) — из Data/ → Data/.
			var oldNumbers = Path.Combine(oldDataDir, "Numbers");
			var newNumbers = Path.Combine(newDataDir, "Numbers");
			if (Directory.Exists(oldNumbers) && !Directory.Exists(newNumbers))
			{
				try
				{
					CopyDirectory(oldNumbers, newNumbers);
					BotLogger.Info(LogCategory.Boot, $"[Migration] Скопирована папка Numbers/: → {newNumbers}");
				}
				catch (Exception ex)
				{
					BotLogger.Error(LogCategory.Boot, $"[Migration] Ошибка копирования Numbers/: {ex.Message}");
				}
			}

			// Logs/ — копируем подпапки со старыми логами (не перемещаем, чтобы можно было сравнить).
			if (Directory.Exists(oldLogsDir))
			{
				var newLogsDir = Path.Combine(newRoot, LogsFolderName);
				Directory.CreateDirectory(newLogsDir);
				foreach (var sub in Directory.GetDirectories(oldLogsDir))
				{
					var dst = Path.Combine(newLogsDir, Path.GetFileName(sub));
					if (!Directory.Exists(dst))
					{
						try
						{
							CopyDirectory(sub, dst);
							BotLogger.Info(LogCategory.Boot, $"[Migration] Скопированы логи {Path.GetFileName(sub)}: → {newLogsDir}");
						}
						catch (Exception ex)
						{
							BotLogger.Error(LogCategory.Boot, $"[Migration] Ошибка копирования логов: {ex.Message}");
						}
					}
				}
			}

			// Чистим legacy-путь LogDirectory из конфига, если он указывал на Settings/Logs.
			if (Current != null)
			{
				var legacyLogs = Path.Combine(SettingsFolderName, LogsFolderName);
				if (string.Equals(Current.LogDirectory, legacyLogs, StringComparison.OrdinalIgnoreCase) ||
				    string.Equals(Current.LogDirectory, LogsFolderName, StringComparison.OrdinalIgnoreCase))
				{
					Current.LogDirectory = LogsFolderName;
					try
					{
						var cfgPath = Path.Combine(newSettingsDir, "config.json");
						if (File.Exists(cfgPath)) Current.Save(cfgPath);
					}
					catch { /* не критично */ }
				}
			}
		}

		private static void CopyDirectory(string src, string dst)
		{
			Directory.CreateDirectory(dst);
			foreach (var file in Directory.GetFiles(src))
				File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: false);
			foreach (var dir in Directory.GetDirectories(src))
				CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
		}

		// Текущая загруженная конфигурация (удобство для доступа из других классов)
		public static BotConfig? Current { get; private set; }

        // === ИДЕНТИФИКАЦИЯ И БАЗОВЫЕ НАСТРОЙКИ ===

        // Токен бота — хранится в config.json или в переменной окружения DISCORD_BOT_TOKEN
        public string? BotToken { get; set; } = null;

        // ID серверов, где бот работает (задаются в Settings/config.json)
        public List<ulong> GuildIDs { get; set; } = new List<ulong>();

        // Версия бота (отображается в логах/статусах)
        public string BotVersion { get; set; } = "1.0.0.0";

        // Представление версии для UI (например "v1.0.0")
        public string GetDisplayVersion()
        {
            var v = string.IsNullOrWhiteSpace(BotVersion) ? "0.0.0.0" : BotVersion.Trim();
            var parts = v.Split('.');
            if (parts.Length >= 3)
                return $"v{parts[0]}.{parts[1]}.{parts[2]}";
            if (parts.Length == 2)
                return $"v{parts[0]}.{parts[1]}";
            return $"v{v}";
        }


        // Список слов, используемых по умолчанию в фильтре мата (нижний регистр лучше)
        public List<string> DefaultSwearWords { get; set; } = new List<string>
        {
            "badword1",
            "badword2",
            "бляд",
            "сука"
        };

        // === ПОДКЛЮЧЕНИЕ И UI ===

        // Настройки подключения
        public ConnectionConfig Connection { get; set; } = new ConnectionConfig();

        // Настройки UI
        public UIConfig UI { get; set; } = new UIConfig();

        // Настройки прогнозирования отключений соединения (отдельно от игровых прогнозов/ставок)
        public PredictionConfig Prediction { get; set; } = new PredictionConfig();

        // === МУЗЫКА (LAVALINK) ===
        public MusicConfig Music { get; set; } = new MusicConfig();

        // === ЛОГИ И ОТЧЁТЫ ===

        // Директория для логов. По умолчанию относительный путь "Logs", который
        // BotConfig.ResolvePath преобразует в <DataRoot>/Logs.
        public string LogDirectory { get; set; } = "Logs";

        // Директория для отчётов об ошибках (bug reports). Аналогично.
        public string BugReportDirectory { get; set; } = "Logs";

        // === ПУТИ К ФАЙЛАМ ===

        // Путь к файлу с текстовыми блоками (по умолчанию в Settings/Pastes.txt,
        // резолвится BotConfig.ResolvePath в <DataRoot>/Settings/Pastes.txt).
        public string TextBlocksPath { get; set; } = Path.Combine(SettingsFolderName, "Pastes.txt");

        // Директория с картинками для бросков (Numbers/). По умолчанию Data/Numbers,
        // резолвится в <DataRoot>/Data/Numbers.
        public string NumbersDirectory { get; set; } = Path.Combine(DataFolderName, "Numbers");

		// === GOOGLE SHEETS ===

		// Путь к файлу credentials (сервисный аккаунт JSON), относительный или абсолютный.
				// По умолчанию лежит в Settings/ — рядом с config.json и serverconfigs.json.
				// Абсолютный путь тоже допустим (если credentials хранятся вне каталога данных).
				public string? GoogleSheetsCredentialsPath { get; set; } = Path.Combine(SettingsFolderName, "google_credentials.json");

		// ID таблицы (из URL: .../spreadsheets/d/{ID}/edit)
		public string? GoogleSpreadsheetId { get; set; } = null;

		// Название листа, куда пишется статистика
		public string GoogleSheetName { get; set; } = "2026 год";

		// Первая строка с данными (строка 1 = заголовки, данные с 2)
		public int GoogleSheetDataStartRow { get; set; } = 2;

		// Включить запись статистики сессий в Google Sheets
		public bool GoogleSheetsEnabled { get; set; } = false;

		// === ЕЖЕДНЕВНЫЙ РЕСТАРТ ===

		// Включить/выключить ежедневную плановую перезагрузку
		public bool DailyRestartEnabled { get; set; } = false;
		// Время плановой перезагрузки по локальному времени (формат: HH:mm или HH:mm:ss)
		public string? DailyRestartLocalTime { get; set; } = null;
		// Время плановой перезагрузки по Москве, если локальная таймзона = Москва (формат: HH:mm или HH:mm:ss)
		public string? DailyRestartMoscowTime { get; set; } = null;
		// Если true и локальная таймзона = Москва — использовать DailyRestartMoscowTime, иначе DailyRestartLocalTime
		public bool DailyRestartPreferMoscowTimeWhenLocalIsMoscow { get; set; } = true;

        // Загрузить конфигурацию из файла
        public static BotConfig Load(string path = "config.json")
        {
            var resolvedPath = ResolvePath(path);

            if (File.Exists(resolvedPath))
            {
                try
                {
					string json = File.ReadAllText(resolvedPath);
					var options = new JsonSerializerOptions
					{
						PropertyNameCaseInsensitive = true,
						ReadCommentHandling = JsonCommentHandling.Skip,
						AllowTrailingCommas = true
					};
                    var cfg = JsonSerializer.Deserialize<BotConfig>(json, options) ?? new BotConfig();
                    Current = cfg;

					// Автодополнение/миграция конфига новыми полями: если их не было в json,
					// пересохраняем, чтобы они появились в файле.
					var needsResave = false;
					if (!json.Contains("\"DailyRestartEnabled\"", StringComparison.Ordinal) ||
							!json.Contains("\"DailyRestartLocalTime\"", StringComparison.Ordinal) ||
							!json.Contains("\"DailyRestartMoscowTime\"", StringComparison.Ordinal) ||
							!json.Contains("\"DailyRestartPreferMoscowTimeWhenLocalIsMoscow\"", StringComparison.Ordinal))
						{
							needsResave = true;
						}

						if (!json.Contains("\"GoogleSheetsEnabled\"", StringComparison.Ordinal) ||
							!json.Contains("\"GoogleSpreadsheetId\"", StringComparison.Ordinal) ||
							!json.Contains("\"GoogleSheetName\"", StringComparison.Ordinal) ||
							!json.Contains("\"GoogleSheetsCredentialsPath\"", StringComparison.Ordinal) ||
							!json.Contains("\"GoogleSheetDataStartRow\"", StringComparison.Ordinal))
						{
							needsResave = true;
						}

               // Telegram settings moved to serverconfigs.json (ServerConfig)
                if (json.Contains("\"TelegramEnabled\"", StringComparison.Ordinal) ||
                    json.Contains("\"TelegramBotToken\"", StringComparison.Ordinal) ||
                    json.Contains("\"TelegramChatId\"", StringComparison.Ordinal))
                {
                    needsResave = true;
                }

				// Если в старом config.json есть устаревшее поле RestartScriptPath,
				// пересохраняем файл, чтобы удалить его из структуры.
				if (json.Contains("\"RestartScriptPath\"", StringComparison.Ordinal))
				{
					needsResave = true;
				}

				// Миграция старого значения LogDirectory из "Settings/Logs" в "Logs",
					// чтобы не создавать папку Settings/Logs рядом с EXE.
					var legacyLogs = Path.Combine(SettingsFolderName, "Logs");
					if (string.Equals(cfg.LogDirectory, legacyLogs, StringComparison.OrdinalIgnoreCase))
					{
						cfg.LogDirectory = "Logs";
						needsResave = true;
					}

					if (needsResave)
					{
						cfg.Save(resolvedPath);
					}

					return cfg;
                }
                catch (Exception ex)
                {
                    BotLogger.Error(LogCategory.Boot, $"BotConfig.Load: ошибка чтения '{resolvedPath}': {ex.Message}");
                    var cfg = new BotConfig();
                    Current = cfg;
                    return cfg;
                }
            }

			// Создать конфиг по умолчанию и сохранить
			var config = new BotConfig();
			Current = config;

			// Генерируем config.json с комментариями и полной структурой всех разделов,
			// используя обычную сериализацию BotConfig, чтобы не терять поля.
			var defaultDir = Path.GetDirectoryName(resolvedPath) ?? AppContext.BaseDirectory;
			Directory.CreateDirectory(defaultDir);

			var serializerOptions = new JsonSerializerOptions
			{
				WriteIndented = true,
				Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
			};
			var jsonBody = JsonSerializer.Serialize(config, serializerOptions);
			var header =
				"// Основные настройки бота (config.json)\n" +
				"// BotToken можно оставить пустым и задать через переменную окружения DISCORD_BOT_TOKEN.\n" +
				"// GuildIDs — список ID серверов, на которых бот работает.\n" +
				"// LogDirectory — папка для логов. По умолчанию 'Logs', резолвится в <DataRoot>/Logs.\n" +
				"// BugReportDirectory — папка для отчётов об ошибках (bug_report_*.txt).\n" +
				"// TextBlocksPath — файл с текстовыми блоками для команд.\n" +
				"// NumbersDirectory — папка с картинками для бросков кубиков.\n" +
				"// DailyRestart* — настройки ежедневной перезагрузки (локальное время и время по МСК).\n" +
				"// DefaultSwearWords — базовый список слов для фильтра мата.\n";
			var defaultJsonWithComments = header + Environment.NewLine + jsonBody + Environment.NewLine;
			FileStream? defaultLockHandle = SafeJsonIO.AcquireLock(resolvedPath, retries: 5, retryDelayMs: 50);
						try
						{
							SafeJsonIO.WriteAtomic(resolvedPath, defaultJsonWithComments);
						}
						finally
						{
							defaultLockHandle?.Dispose();
						}
						return config;
			        }

        // Сохранить конфигурацию в файл
        public void Save(string path = "config.json")
        {
            try
            {
                var resolvedPath = ResolvePath(path);
                var dir = Path.GetDirectoryName(resolvedPath) ?? AppContext.BaseDirectory;
                Directory.CreateDirectory(dir);

                // При сохранении конфига используем UnsafeRelaxedJsonEscaping,
                // чтобы кириллица и другие символы писались напрямую, а не как \uXXXX.
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string json = JsonSerializer.Serialize(this, options);
                FileStream? lockHandle = SafeJsonIO.AcquireLock(resolvedPath, retries: 5, retryDelayMs: 50);
                try
                {
                    SafeJsonIO.WriteAtomic(resolvedPath, json);
                }
                finally
                {
                    lockHandle?.Dispose();
                }
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Boot, $"BotConfig.Save: ошибка записи '{path}': {ex.Message}");
            }
        }
    }

    public class ConnectionConfig
    {
        public int ConnectionTimeout { get; set; } = 30000;
        public int MessageCacheSize { get; set; } = 50;
        public int HandlerTimeout { get; set; } = 15000;
        public int LargeThreshold { get; set; } = 250;
        public bool AlwaysDownloadUsers { get; set; } = true;
        public RetryMode DefaultRetryMode { get; set; } = RetryMode.AlwaysRetry;
    }

    public class UIConfig
    {
        public int ConsoleWidth { get; set; } = 120;
        public int ConsoleHeight { get; set; } = 35;
        public int LogPanelWidthPercent { get; set; } = 70;
        public bool ShowTimestamps { get; set; } = true;
    }

    public class PredictionConfig
    {
        public int MinDisconnectsForPrediction { get; set; } = 3;
        public int PredictionConfidenceThreshold { get; set; } = 50;
        // Включить/выключить прогнозы отключений соединения (не игровые прогнозы)
        public bool EnablePredictions { get; set; } = true;

        // Алиас для кода, чтобы явно отличать от игровых прогнозов.
        // JSON-ключ остаётся прежним: EnablePredictions.
        [JsonIgnore]
        public bool EnableConnectionPredictions
        {
            get => EnablePredictions;
            set => EnablePredictions = value;
        }
        public int MaxPredictionsPerHour { get; set; } = 2; // Ограничение на количество прогнозов
        public int CooldownMinutes { get; set; } = 30; // Задержка между прогнозами
        // Новые настройки для более стабильной и консервативной работы предсказателя
        public int HeartbeatMissesForPrediction { get; set; } = 3;
        public int RequireConsecutiveEvaluations { get; set; } = 2; // сколько последовательных подтверждений нужно
        public int ConfirmationWindowSeconds { get; set; } = 45; // окно подтверждения
        public int TrendWindowMinutes { get; set; } = 60; // окно для подсчёта частоты отключений
        public int MinFactorsForPrediction { get; set; } = 2; // сколько факторов должно совпасть
        // Весовые коэффициенты для факторов (можно тонко настраивать)
        public double FrequencyWeight { get; set; } = 1.0;
        public double HeartbeatWeight { get; set; } = 1.5;
        public double StabilityWeight { get; set; } = 1.0;
    }
    public class MusicConfig
    {
        /// <summary>Включить музыкальный функционал.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Адрес Lavalink-сервера.</summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>Порт Lavalink-сервера.</summary>
        public int Port { get; set; } = 2333;

        /// <summary>Пароль Lavalink-сервера (должен совпадать с application.yml).</summary>
        public string Password { get; set; } = "rpbot_lavalink_password";

        /// <summary>Автоматически запускать Lavalink.jar при старте бота.</summary>
        public bool AutoStart { get; set; } = true;

        /// <summary>Путь к Lavalink.jar (относительный или абсолютный).</summary>
        public string JarPath { get; set; } = "Lavalink/Lavalink.jar";

        /// <summary>Путь к application.yml для Lavalink (относительный или абсолютный).</summary>
        public string ConfigPath { get; set; } = "Lavalink/application.yml";

        /// <summary>Секунд ожидания готовности Lavalink после запуска.</summary>
        public int StartupTimeoutSeconds { get; set; } = 30;

        /// <summary>Отключить бота от голосового канала если очередь пуста N секунд. 0 = не отключать.</summary>
        public int InactivityTimeoutSeconds { get; set; } = 300;

        /// <summary>Автоматически запускать локальный yt-cipher сервер перед Lavalink.</summary>
        public bool YtCipherAutoStart { get; set; } = false;

        /// <summary>Путь к директории с yt-cipher (там должен лежать server.ts).</summary>
        public string YtCipherPath { get; set; } = "yt-cipher";

        /// <summary>Порт для локального yt-cipher сервера.</summary>
        public int YtCipherPort { get; set; } = 8001;
    }
}
