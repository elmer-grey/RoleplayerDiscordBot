using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>
    /// Хранит всю историю и статистику подключений бота
    /// </summary>
    public class ConnectionStateInfo
    {
        public class DisconnectEntry
        {
            public DateTime Time { get; set; }
            public string Reason { get; set; } = "";
            public string Details { get; set; } = "";
        }

        // Структурированная история отключений (в порядке от нового к старому)
        public List<DisconnectEntry> RecentDisconnects { get; set; } = new();

        // Текущее состояние
        public DateTime LastConnectionTime { get; set; } = DateTime.UtcNow;
        public DateTime LastDisconnectTime { get; set; }
        public string LastDisconnectReason { get; set; } = "Неизвестно";
        public string LastDisconnectDetails { get; set; } = "";
        public string LastConnectReason { get; set; } = "Первичный запуск";

        // Статистика реконнектов
        public int ReconnectAttempts { get; set; }
        public int SuccessfulReconnects { get; set; }
        public int FailedReconnects { get; set; }
        public double AverageReconnectTimeMs { get; set; }

        // История и статистика
        public List<string> RecentDisconnectReasons { get; set; } = new();
        public Dictionary<string, int> DisconnectStats { get; set; } = new();

        // Прогнозирование
        public bool IsExpectedDisconnect { get; set; }
        public DateTime? PredictedDisconnectTime { get; set; }
        public string PredictedReason { get; set; } = "";
        public double ConnectionStabilityScore { get; set; } = 100.0;

        // Heartbeat мониторинг
        public DateTime LastHeartbeatTime { get; set; } = DateTime.UtcNow;
        public int HeartbeatMisses { get; set; }

        // Время работы
        public TimeSpan CurrentUptime =>
            LastConnectionTime == default ? TimeSpan.Zero : DateTime.UtcNow - LastConnectionTime;

        /// <summary>
        /// Добавляет причину отключения в историю
        /// </summary>
        public void AddDisconnectReason(string reason, string details = "")
        {
            LastDisconnectTime = DateTime.UtcNow;
            LastDisconnectReason = reason;
            LastDisconnectDetails = details;

            // Добавляем в структурированную историю
            var entry = new DisconnectEntry { Time = DateTime.UtcNow, Reason = reason, Details = details };
            RecentDisconnects.Insert(0, entry);
            while (RecentDisconnects.Count > 50)
                RecentDisconnects.RemoveAt(RecentDisconnects.Count - 1);

            // Также поддерживаем совместимую строковую историю (короткий формат)
            RecentDisconnectReasons.Insert(0, $"{reason} - {DateTime.UtcNow:HH:mm:ss}");
            while (RecentDisconnectReasons.Count > 10)
                RecentDisconnectReasons.RemoveAt(RecentDisconnectReasons.Count - 1);

            // Обновляем статистику
            if (DisconnectStats.ContainsKey(reason))
                DisconnectStats[reason]++;
            else
                DisconnectStats[reason] = 1;
        }

        /// <summary>
        /// Обновляет информацию об успешном подключении
        /// </summary>
        public void UpdateConnectionInfo(string reason)
        {
            LastConnectionTime = DateTime.UtcNow;
            LastConnectReason = reason;
            ReconnectAttempts = 0;
        }

        /// <summary>
        /// Рассчитывает стабильность соединения на основе истории
        /// </summary>
        public void CalculateStabilityScore()
        {
            // Рассчитываем стабильность на основе числа отключений за последний час и за весь период
            try
            {
                var now = DateTime.UtcNow;
                var lastHourCount = RecentDisconnects.Count(e => (now - e.Time).TotalHours <= 1);
                var last24hCount = RecentDisconnects.Count(e => (now - e.Time).TotalHours <= 24);

                // Базовый скор: 100 - штрафы
                double score = 100.0;

                // Штраф за отключения в последнем часе (сильнее влияет)
                score -= Math.Min(60, lastHourCount * 20);

                // Доп. штраф за накопленные отключения за 24 часа
                score -= Math.Min(30, last24hCount * 2);

                // Нормируем
                ConnectionStabilityScore = Math.Max(0, Math.Min(100, score));
            }
            catch
            {
                // в случае ошибок оставляем прежнее значение
            }
        }

        /// <summary>
        /// Сбрасывает счетчик попыток после успеха
        /// </summary>
        public void ResetAttempts()
        {
            ReconnectAttempts = 0;
            SuccessfulReconnects++;
        }

        /// <summary>
        /// Увеличивает счетчик неудачных попыток
        /// </summary>
        public void IncrementFailedAttempts()
        {
            FailedReconnects++;
        }
    }
}
