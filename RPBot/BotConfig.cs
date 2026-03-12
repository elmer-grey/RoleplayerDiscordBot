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
        // Токен бота — временно хранится в коде (для рабочей версии). В тестовом merge это будет убрано.
        public string? BotToken { get; set; } = "MTMzMTYyODkxMDE1MjEyMjM4OA.GJutjl.wTDw8Tp1wI8ZNehE4TnFNkNKTFRJQ2Q0DPG4JI";

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

        // Загрузить конфигурацию из файла
        public static BotConfig Load(string path = "config.json")
        {
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<BotConfig>(json) ?? new BotConfig();
                }
                catch
                {
                    return new BotConfig();
                }
            }

            // Создать конфиг по умолчанию и сохранить
            var config = new BotConfig();
            config.Save(path);
            return config;
        }

        // Сохранить конфигурацию в файл
        public void Save(string path = "config.json")
        {
            try
            {
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
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
    }
}
