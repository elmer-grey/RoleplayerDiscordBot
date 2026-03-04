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
        public string TextBlocksPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Pastes.txt");

        // Директория для логов
        public string LogDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Logs");

        // Директория с картинками для бросков (например Numbers)
        public string NumbersDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Numbers");

        // Путь до скрипта перезапуска (может быть относительным к каталогу приложения)
        public string RestartScriptPath { get; set; } = "restart_bot.ps1";

        // Директория для отчетов об ошибках (bug reports)
        public string BugReportDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Logs");

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
                    var cfg = JsonSerializer.Deserialize<BotConfig>(json) ?? new BotConfig();
                    Current = cfg;
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
        // Количество последовательных срабатываний фактора, требуемое для подтверждения прогнозa
        public int ConfirmationsRequired { get; set; } = 2;
        // Параметр экспоненциального сглаживания для heartbeat (0..1). Больше -> быстрее реагирует
        public double HeartbeatSmoothingAlpha { get; set; } = 0.4;
    }
}
