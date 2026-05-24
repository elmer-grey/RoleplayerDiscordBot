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
        // Временные метки отключений для анализа трендов
        public List<DateTime> RecentDisconnectTimes { get; set; } = new();
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
        /// Убирает последнюю запись об отключении из истории, если клиент сам восстановился.
        /// Это предотвращает ухудшение прогноза после кратких WebSocket-сбоев с мгновенным реконнектом.
        /// </summary>
        public void RemoveLastSelfRecoveredDisconnect()
        {
            if (RecentDisconnectTimes.Count > 0)
                RecentDisconnectTimes.RemoveAt(0);
            if (RecentDisconnectReasons.Count > 0)
                RecentDisconnectReasons.RemoveAt(0);
        }

        /// <summary>
        /// Добавляет причину отключения в историю
        /// </summary>
        public void AddDisconnectReason(string reason, string details = "")
        {
            LastDisconnectTime = DateTime.UtcNow;
            LastDisconnectReason = reason;
            LastDisconnectDetails = details;

            // Добавляем в историю
            RecentDisconnectReasons.Insert(0, $"{reason} - {DateTime.UtcNow:HH:mm:ss}");
            RecentDisconnectTimes.Insert(0, DateTime.UtcNow);
            while (RecentDisconnectReasons.Count > 50)
                RecentDisconnectReasons.RemoveAt(RecentDisconnectReasons.Count - 1);
            while (RecentDisconnectTimes.Count > 50)
                RecentDisconnectTimes.RemoveAt(RecentDisconnectTimes.Count - 1);

            // Обновляем статистику
            if (DisconnectStats.ContainsKey(reason))
                DisconnectStats[reason]++;
            else
                DisconnectStats[reason] = 1;
        }

        /// <summary>
        /// Возвращает количество отключений за последние N минут
        /// </summary>
        public int GetDisconnectsInLastMinutes(int minutes)
        {
            if (minutes <= 0) return 0;
            var cutoff = DateTime.UtcNow.AddMinutes(-minutes);
            return RecentDisconnectTimes.Count(t => t >= cutoff);
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
            var totalDisconnects = DisconnectStats.Values.Sum();
            var uptimeMinutes = CurrentUptime.TotalMinutes;

            if (uptimeMinutes > 0)
            {
                var disconnectsPerHour = totalDisconnects / (uptimeMinutes / 60);
                ConnectionStabilityScore = Math.Max(0, 100 - (disconnectsPerHour * 10));
                ConnectionStabilityScore = Math.Min(100, ConnectionStabilityScore);
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
