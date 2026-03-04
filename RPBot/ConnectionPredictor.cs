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
        private int _currentHour = DateTime.UtcNow.Hour;

        // Внутреннее состояние для сглаживания и подтверждения сигналов
        private double _smoothedHeartbeatMisses = 0.0;
        private readonly Dictionary<string, int> _factorConfirmCounts = new();

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
            _smoothedHeartbeatMisses = _connectionInfo.HeartbeatMisses;
        }

        /// <summary>
        /// Анализирует текущее состояние и делает прогноз
        /// </summary>
        public async Task<PredictionResult?> AnalyzeAndPredict()
        {
            // Проверяем, включены ли прогнозы
            if (!_config.EnablePredictions)
                return null;

            // Проверяем защиту от спама
            if (!CanMakePrediction())
                return null;

            _connectionInfo.CalculateStabilityScore();
            var now = DateTime.UtcNow;

            // Сглаживаем heartbeat, чтобы учесть кратковременные выбросы
            var alpha = Math.Clamp(_config.HeartbeatSmoothingAlpha, 0.05, 0.95);
            _smoothedHeartbeatMisses = alpha * _connectionInfo.HeartbeatMisses + (1 - alpha) * _smoothedHeartbeatMisses;

            // Подсчёт отключений за окно
            var disconnectsLastHour = _connectionInfo.RecentDisconnects.Count(e => (now - e.Time).TotalMinutes <= 60);
            var disconnectsTotal = _connectionInfo.RecentDisconnects.Count;

            // Если данных совсем мало — не предсказываем
            if (disconnectsTotal < _config.MinDisconnectsForPrediction && _smoothedHeartbeatMisses < 1)
                return null;

            // Оцениваем факторы, но требуем подтверждений (не флапать)
            PredictionResult? candidate = null;

            // Фактор: высокая частота отключений
            if (disconnectsLastHour >= 3)
            {
                var key = "freq";
                _factorConfirmCounts.TryGetValue(key, out var cnt);
                cnt++;
                _factorConfirmCounts[key] = cnt;

                if (cnt >= _config.ConfirmationsRequired)
                {
                    candidate = new PredictionResult
                    {
                        PredictedTime = now.AddMinutes(15),
                        Reason = "Высокая частота отключений",
                        Confidence = 60,
                        Recommendation = "Проверьте интернет-соединение",
                        IsCooldown = false
                    };
                }
            }
            else
            {
                _factorConfirmCounts["freq"] = 0;
            }

            // Фактор: пропущенные heartbeat (используем сглаженное значение)
            if (_smoothedHeartbeatMisses >= 3)
            {
                var key = "heartbeat";
                _factorConfirmCounts.TryGetValue(key, out var cnt);
                cnt++;
                _factorConfirmCounts[key] = cnt;

                if (cnt >= _config.ConfirmationsRequired)
                {
                    var pr = new PredictionResult
                    {
                        PredictedTime = now.AddSeconds(30),
                        Reason = "Пропущены heartbeat пакеты",
                        Confidence = 85,
                        Recommendation = "Скоро будет разрыв соединения",
                        IsCooldown = false
                    };

                    // Если уже есть кандидат — усиливаем уверенность
                    if (candidate != null)
                    {
                        candidate.Confidence = Math.Min(100, (candidate.Confidence + pr.Confidence) / 2 + 5);
                        candidate.Reason += "; + Пропущенные heartbeat";
                    }
                    else
                    {
                        candidate = pr;
                    }
                }
            }
            else
            {
                _factorConfirmCounts["heartbeat"] = 0;
            }

            // Фактор: общая стабильность
            if (_connectionInfo.ConnectionStabilityScore < 50)
            {
                var key = "stability";
                _factorConfirmCounts.TryGetValue(key, out var cnt);
                cnt++;
                _factorConfirmCounts[key] = cnt;

                if (cnt >= _config.ConfirmationsRequired)
                {
                    var pr = new PredictionResult
                    {
                        PredictedTime = now.AddMinutes(10),
                        Reason = "Низкая стабильность соединения",
                        Confidence = 70,
                        Recommendation = "Рекомендуется перезагрузка бота",
                        IsCooldown = false
                    };

                    if (candidate != null)
                    {
                        candidate.Confidence = Math.Min(100, (candidate.Confidence + pr.Confidence) / 2);
                        candidate.Reason += "; + Низкая стабильность";
                    }
                    else
                    {
                        candidate = pr;
                    }
                }
            }
            else
            {
                _factorConfirmCounts["stability"] = 0;
            }

            // Итог: если есть кандидат и уверенность выше порога — уведомляем
            if (candidate != null && candidate.Confidence >= _config.PredictionConfidenceThreshold)
            {
                // Обновляем счетчики для защиты от спама
                UpdatePredictionCounters();

                // Сбрасываем счётчики подтверждений, чтобы не спамить повторно
                _factorConfirmCounts.Clear();

                await OnPredictionMade?.Invoke(candidate);
                return candidate;
            }

            return null;
        }

        private bool CanMakePrediction()
        {
            var now = DateTime.UtcNow;

            // Сброс счетчика в начале нового часа (UTC)
            if (now.Hour != _currentHour)
            {
                _currentHour = now.Hour;
                _predictionsThisHour = 0;
            }

            // Проверка на cooldown
            if ((_lastPredictionTime != DateTime.MinValue) && (now - _lastPredictionTime).TotalMinutes < _config.CooldownMinutes)
                return false;

            // Проверка лимита в час
            if (_predictionsThisHour >= _config.MaxPredictionsPerHour)
                return false;

            return true;
        }

        private void UpdatePredictionCounters()
        {
            _lastPredictionTime = DateTime.UtcNow;
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
