using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>
    /// Анализирует историю и предсказывает будущие отключения
    /// </summary>
    public class ConnectionPredictor
    {
        private readonly ConnectionStateInfo _connectionInfo;
        private readonly ReconnectionService _reconnectionService;

        public event Func<PredictionResult, Task> OnPredictionMade;

        public class PredictionResult
        {
            public DateTime PredictedTime { get; set; }
            public string Reason { get; set; }
            public int Confidence { get; set; } // 0-100%
            public string Recommendation { get; set; }
        }

        public ConnectionPredictor(ReconnectionService reconnectionService)
        {
            _reconnectionService = reconnectionService;
            _connectionInfo = reconnectionService.ConnectionInfo;
        }

        /// <summary>
        /// Анализирует текущее состояние и делает прогноз
        /// </summary>
        public async Task<PredictionResult> AnalyzeAndPredict()
        {
            _connectionInfo.CalculateStabilityScore();

            var now = DateTime.UtcNow;
            var recentCount = _connectionInfo.RecentDisconnectReasons.Count;

            // ФАКТОР 1: Частота отключений
            if (recentCount >= 3)
            {
                var lastHour = _connectionInfo.RecentDisconnectReasons
                    .Count(r => r.Contains(now.AddHours(-1).ToString("HH")));

                if (lastHour >= 3)
                {
                    var result = new PredictionResult
                    {
                        PredictedTime = now.AddMinutes(15),
                        Reason = "Высокая частота отключений",
                        Confidence = 65,
                        Recommendation = "Проверьте интернет-соединение"
                    };

                    await OnPredictionMade?.Invoke(result);
                    return result;
                }
            }

            // ФАКТОР 2: Пропущенные heartbeat
            if (_connectionInfo.HeartbeatMisses > 2)
            {
                var result = new PredictionResult
                {
                    PredictedTime = now.AddSeconds(30),
                    Reason = "Пропущены heartbeat пакеты",
                    Confidence = 85,
                    Recommendation = "Скоро будет разрыв соединения"
                };

                await OnPredictionMade?.Invoke(result);
                return result;
            }

            // ФАКТОР 3: Стабильность соединения
            if (_connectionInfo.ConnectionStabilityScore < 50)
            {
                var result = new PredictionResult
                {
                    PredictedTime = now.AddMinutes(10),
                    Reason = "Низкая стабильность соединения",
                    Confidence = 70,
                    Recommendation = "Рекомендуется перезагрузка бота"
                };

                await OnPredictionMade?.Invoke(result);
                return result;
            }

            // ФАКТОР 4: Время жизни соединения
            if (_connectionInfo.DisconnectStats.Count > 0)
            {
                var avgLifetime = EstimateAverageLifetime();
                var currentUptime = _connectionInfo.CurrentUptime.TotalMinutes;

                if (avgLifetime > 0 && currentUptime > avgLifetime * 0.8)
                {
                    var result = new PredictionResult
                    {
                        PredictedTime = now.AddMinutes(5),
                        Reason = "Приближение к среднему времени отключения",
                        Confidence = 60,
                        Recommendation = "Ожидайте возможного реконнекта"
                    };

                    await OnPredictionMade?.Invoke(result);
                    return result;
                }
            }

            return null; // Прогнозов нет
        }

        /// <summary>
        /// Оценивает среднее время жизни соединения
        /// </summary>
        private double EstimateAverageLifetime()
        {
            // Упрощенная оценка - в реальности нужно хранить историю времен жизни
            return 45; // 45 минут
        }

        /// <summary>
        /// Возвращает текстовый статус соединения
        /// </summary>
        public string GetConnectionHealthStatus()
        {
            var score = _connectionInfo.ConnectionStabilityScore;

            if (score >= 90) return "🟢 Отличное";
            if (score >= 70) return "🟡 Хорошее";
            if (score >= 50) return "🟠 Среднее";
            if (score >= 30) return "🔴 Плохое";
            return "⚫ Критическое";
        }
    }
}
