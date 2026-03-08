using Discord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;

namespace RPBot
{
    public class BotConfig
    {
		public const string SettingsFolderName = "Settings";

		public static string GetSettingsDirectory()
		{
			return ResolvePath(SettingsFolderName);
		}

		/// <summary>
		/// Переносит файлы настроек/локальных данных из старых путей (корень каталога запуска)
		/// в новую папку Settings. Безопасно: не перетирает файлы, если целевой уже существует.
		/// </summary>
		public static void MigrateLegacySettingsFiles()
		{
			try
			{
				var baseDir = AppContext.BaseDirectory;
				var settingsDir = GetSettingsDirectory();
				Directory.CreateDirectory(settingsDir);

				void MoveIfExists(string sourcePath, string targetPath)
				{
					try
					{
						if (!File.Exists(sourcePath))
							return;

						Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? settingsDir);
						if (!File.Exists(targetPath))
						{
							File.Move(sourcePath, targetPath);
							return;
						}

						var dir = Path.GetDirectoryName(targetPath) ?? settingsDir;
						var name = Path.GetFileNameWithoutExtension(targetPath);
						var ext = Path.GetExtension(targetPath);
						var legacyTarget = Path.Combine(dir, $"{name}.legacy-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
						File.Move(sourcePath, legacyTarget);
					}
					catch { }
				}

				MoveIfExists(
					sourcePath: Path.Combine(baseDir, "config.json"),
					targetPath: Path.Combine(settingsDir, "config.json"));

				MoveIfExists(
					sourcePath: Path.Combine(baseDir, "restart_bot.ps1"),
					targetPath: Path.Combine(settingsDir, "restart_bot.ps1"));

				MoveIfExists(
					sourcePath: Path.Combine(baseDir, "serverconfigs.json"),
					targetPath: Path.Combine(settingsDir, "serverconfigs.json"));

				MoveIfExists(
					sourcePath: Path.Combine(baseDir, "bwonks.json"),
					targetPath: Path.Combine(settingsDir, "bwonks.json"));

				MoveIfExists(
					sourcePath: Path.Combine(baseDir, "Pastes.txt"),
					targetPath: Path.Combine(settingsDir, "Pastes.txt"));
			}
			catch { }
		}

        // Текущая загруженная конфигурация (удобство для доступа из других классов)
        public static BotConfig? Current { get; private set; }

        // Утилита: если путь относительный — трактуем его относительно каталога приложения
        public static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return AppContext.BaseDirectory;

            return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        }

        // Токен бота — хранится в config.json или в переменной окружения DISCORD_BOT_TOKEN
        public string? BotToken { get; set; } = null;

        // ID серверов, где бот работает
        public List<ulong> GuildIDs { get; set; } = new List<ulong>
        {
            295189463376855040, // Канал "КнР"
            1288192593137635359  // Канал "Тест"
        };

        // Настройки подключения
        public ConnectionConfig Connection { get; set; } = new ConnectionConfig();

        // Настройки UI
        public UIConfig UI { get; set; } = new UIConfig();

        // Настройки прогнозирования
        public PredictionConfig Prediction { get; set; } = new PredictionConfig();

        // Путь к файлу с текстовыми блоками (по умолчанию рядом с исполняемым файлом)
		public string TextBlocksPath { get; set; } = Path.Combine(SettingsFolderName, "Pastes.txt");

        // Директория для логов
		public string LogDirectory { get; set; } = "Logs";

        // Директория с картинками для бросков (например Numbers)
		public string NumbersDirectory { get; set; } = "Numbers";

        // Путь до скрипта перезапуска (может быть относительным к каталогу приложения)
		public string RestartScriptPath { get; set; } = Path.Combine(SettingsFolderName, "restart_bot.ps1");

		// Ежедневная плановая перезагрузка
		public bool DailyRestartEnabled { get; set; } = false;
		// Время плановой перезагрузки по локальному времени (формат: HH:mm или HH:mm:ss)
		public string? DailyRestartLocalTime { get; set; } = null;
		// Время плановой перезагрузки по Москве, если локальная таймзона = Москва (формат: HH:mm или HH:mm:ss)
		public string? DailyRestartMoscowTime { get; set; } = null;
		// Если true и локальная таймзона = Москва — использовать DailyRestartMoscowTime, иначе DailyRestartLocalTime
		public bool DailyRestartPreferMoscowTimeWhenLocalIsMoscow { get; set; } = true;

        // Директория для отчетов об ошибках (bug reports)
		public string BugReportDirectory { get; set; } = "Logs";

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

					// Автодополнение конфига новыми полями: если их не было в json,
					// пересохраняем, чтобы они появились в файле.
					var needsResave =
						!json.Contains("\"DailyRestartEnabled\"", StringComparison.Ordinal) ||
						!json.Contains("\"DailyRestartLocalTime\"", StringComparison.Ordinal) ||
						!json.Contains("\"DailyRestartMoscowTime\"", StringComparison.Ordinal) ||
						!json.Contains("\"DailyRestartPreferMoscowTimeWhenLocalIsMoscow\"", StringComparison.Ordinal);

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
            config.Save(resolvedPath);
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
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(resolvedPath, json);
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
