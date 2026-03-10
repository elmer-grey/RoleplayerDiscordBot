using Discord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using System.IO;

namespace RPBot
{
    public class BotConfig
    {
		// Папка Settings живёт рядом с Logs в корне каталога приложения
		// (оба пути относительные к AppContext.BaseDirectory).
		public const string SettingsFolderName = "Settings";

		public static string GetSettingsDirectory()
		{
			return ResolvePath(SettingsFolderName);
		}

		// Текущая загруженная конфигурация (удобство для доступа из других классов)
		public static BotConfig? Current { get; private set; }

		// Утилита: если путь относительный — трактуем его относительно каталога приложения (где лежит exe)
		public static string ResolvePath(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return AppContext.BaseDirectory;

			return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
		}

        // === ИДЕНТИФИКАЦИЯ И БАЗОВЫЕ НАСТРОЙКИ ===

        // Токен бота — хранится в config.json или в переменной окружения DISCORD_BOT_TOKEN
        public string? BotToken { get; set; } = null;

        // ID серверов, где бот работает
        public List<ulong> GuildIDs { get; set; } = new List<ulong>
        {
            295189463376855040, // Канал "КнР"
            1288192593137635359  // Канал "Тест"
        };

        // Версия бота (отображается в логах/статусах)
        public string BotVersion { get; set; } = "0.6.0.0";

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

        // Настройки прогнозирования соединения
        public PredictionConfig Prediction { get; set; } = new PredictionConfig();

        // === ЛОГИ И ОТЧЁТЫ ===

        // Директория для логов (по умолчанию отдельная папка Logs рядом с EXE)
		public string LogDirectory { get; set; } = "Logs";

        // Директория для отчётов об ошибках (bug reports)
		public string BugReportDirectory { get; set; } = "Logs";

        // === ПУТИ К ФАЙЛАМ ===

        // Путь к файлу с текстовыми блоками (по умолчанию в Settings/Pastes.txt)
		public string TextBlocksPath { get; set; } = Path.Combine(SettingsFolderName, "Pastes.txt");

        // Директория с картинками для бросков (например, Numbers)
		public string NumbersDirectory { get; set; } = Path.Combine(SettingsFolderName, "Numbers");

        // Путь до скрипта перезапуска (может быть относительным к каталогу приложения)
		public string RestartScriptPath { get; set; } = Path.Combine(SettingsFolderName, "restart_bot.ps1");

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
                    Console.WriteLine($"BotConfig.Load error reading '{resolvedPath}': {ex}");
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
				"// LogDirectory — папка для логов (по умолчанию Logs рядом с exe).\n" +
				"// BugReportDirectory — папка для отчётов об ошибках (bug_report_*.txt).\n" +
				"// TextBlocksPath — файл с текстовыми блоками для команд.\n" +
				"// NumbersDirectory — папка с картинками для бросков кубиков.\n" +
				"// RestartScriptPath — скрипт для внешнего перезапуска бота.\n" +
				"// DailyRestart* — настройки ежедневной перезагрузки (локальное время и время по МСК).\n" +
				"// DefaultSwearWords — базовый список слов для фильтра мата.\n";
			var defaultJsonWithComments = header + Environment.NewLine + jsonBody + Environment.NewLine;
			File.WriteAllText(resolvedPath, defaultJsonWithComments, System.Text.Encoding.UTF8);
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
                File.WriteAllText(resolvedPath, json, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BotConfig.Save error writing '{path}': {ex}");
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
        public bool EnablePredictions { get; set; } = true;
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
}
