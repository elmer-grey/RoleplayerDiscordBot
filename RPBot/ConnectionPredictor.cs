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
        private readonly PredictionConfig _config;

        // Защита от спама
        private DateTime _lastPredictionTime = DateTime.MinValue;
        private int _predictionsThisHour = 0;
        private int _currentHour = DateTime.Now.Hour;

        public event Func<PredictionResult, Task> OnPredictionMade;

        public class PredictionResult
        {
            public DateTime PredictedTime { get; set; }
            public string Reason { get; set; }
            public int Confidence { get; set; } // 0-100%
            public string Recommendation { get; set; }
            public bool IsCooldown { get; set; }
        }

        public ConnectionPredictor(ReconnectionService reconnectionService, PredictionConfig config = null)
        {
            _reconnectionService = reconnectionService;
            _connectionInfo = reconnectionService.ConnectionInfo;
            _config = config ?? new PredictionConfig();
        }

        /// <summary>
        /// Анализирует текущее состояние и делает прогноз
        /// </summary>
        public async Task<PredictionResult> AnalyzeAndPredict()
        {
            // Проверяем, включены ли прогнозы
            if (!_config.EnablePredictions)
                return null;

            // Проверяем защиту от спама
            if (!CanMakePrediction())
                return null;

            _connectionInfo.CalculateStabilityScore();
            var now = DateTime.UtcNow;
            var recentCount = _connectionInfo.RecentDisconnectReasons.Count;

            // Не делаем прогнозы, если мало данных
            if (recentCount < _config.MinDisconnectsForPrediction)
                return null;

            PredictionResult result = null;

            // ФАКТОР 1: Частота отключений
            if (recentCount >= _config.MinDisconnectsForPrediction)
            {
                var lastHour = _connectionInfo.RecentDisconnectReasons
                    .Count(r => r.Contains(now.AddHours(-1).ToString("HH")));

                if (lastHour >= 3)
                {
                    result = new PredictionResult
                    {
                        PredictedTime = now.AddMinutes(15),
                        Reason = "Высокая частота отключений",
                        Confidence = 65,
                        Recommendation = "Проверьте интернет-соединение"
                    };
                }
            }

            // ФАКТОР 2: Пропущенные heartbeat
            if (result == null && _connectionInfo.HeartbeatMisses > 2)
            {
                result = new PredictionResult
                {
                    PredictedTime = now.AddSeconds(30),
                    Reason = "Пропущены heartbeat пакеты",
                    Confidence = 85,
                    Recommendation = "Скоро будет разрыв соединения"
                };
            }

            // ФАКТОР 3: Стабильность соединения
            if (result == null && _connectionInfo.ConnectionStabilityScore < 50)
            {
                result = new PredictionResult
                {
                    PredictedTime = now.AddMinutes(10),
                    Reason = "Низкая стабильность соединения",
                    Confidence = 70,
                    Recommendation = "Рекомендуется перезагрузка бота"
                };
            }

            // Если прогноз сделан и он достаточно уверенный
            if (result != null && result.Confidence >= _config.PredictionConfidenceThreshold)
            {
                // Обновляем счетчики для защиты от спама
                UpdatePredictionCounters();

                // Оповещаем подписчиков
                await OnPredictionMade?.Invoke(result);
                return result;
            }

            return null;
        }

        private bool CanMakePrediction()
        {
            var now = DateTime.Now;

            // Сброс счетчика в начале нового часа
            if (now.Hour != _currentHour)
            {
                _currentHour = now.Hour;
                _predictionsThisHour = 0;
            }

            // Проверка на cooldown
            if ((now - _lastPredictionTime).TotalMinutes < _config.CooldownMinutes)
                return false;

            // Проверка лимита в час
            if (_predictionsThisHour >= _config.MaxPredictionsPerHour)
                return false;

            return true;
        }

        private void UpdatePredictionCounters()
        {
            _lastPredictionTime = DateTime.Now;
            _predictionsThisHour++;
        }

        /// <summary>
        /// Возвращает текстовый статус соединения
        /// </summary>
        public string GetConnectionHealthStatus()
        {
            var score = _connectionInfo.ConnectionStabilityScore;

            if (score >= 90) return "Отличное";
            if (score >= 70) return "Хорошее";
            if (score >= 50) return "Среднее";
            if (score >= 30) return "Плохое";
            return "Критическое";
        }
    }
}
