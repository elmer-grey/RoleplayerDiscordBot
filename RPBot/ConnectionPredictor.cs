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
        private readonly object _analyzeLock = new();

        // Для подтверждения предсказаний требуем несколько последовательных срабатываний
        private ConnectionPredictor.PredictionResult? _lastCandidate;
        private int _consecutiveMatches = 0;
        private DateTime _firstCandidateTime = DateTime.MinValue;

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
            public bool IsSuppressed { get; set; } // was suppressed awaiting confirmation
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
        public async Task<PredictionResult?> AnalyzeAndPredict()
        {
            lock (_analyzeLock)
            {
                // ensure single analyzer at a time
            }
            // Проверяем, включены ли прогнозы
            if (!_config.EnablePredictions)
                return null;

            // Проверяем защиту от спама (но не блокируем подтверждение кандидата)
            var canSendNow = CanMakePrediction();

            _connectionInfo.CalculateStabilityScore();
            var now = DateTime.UtcNow;

            // Собираем факторы и их оценки
            int recentDisconnects = _connectionInfo.GetDisconnectsInLastMinutes(_config.TrendWindowMinutes);

            bool freqActive = recentDisconnects >= _config.MinDisconnectsForPrediction;
            double freqScore = 0;
            if (freqActive)
            {
                // линейная шкала уверенности от порога
                freqScore = Math.Min(90, 40 + (recentDisconnects - _config.MinDisconnectsForPrediction) * 10);
            }

            bool hbActive = _connectionInfo.HeartbeatMisses >= _config.HeartbeatMissesForPrediction;
            double hbScore = hbActive ? 85 : 0;

            bool stabilityActive = _connectionInfo.ConnectionStabilityScore < 50;
            double stabilityScore = stabilityActive ? (int)(100 - _connectionInfo.ConnectionStabilityScore) : 0;

            // Взвешивание
            double weighted = (freqScore * _config.FrequencyWeight) + (hbScore * _config.HeartbeatWeight) + (stabilityScore * _config.StabilityWeight);
            double maxPossible = (100 * (_config.FrequencyWeight + _config.HeartbeatWeight + _config.StabilityWeight));
            int combinedConfidence = maxPossible > 0 ? (int)Math.Round((weighted / maxPossible) * 100) : 0;

            int activeFactors = 0;
            if (freqActive) activeFactors++;
            if (hbActive) activeFactors++;
            if (stabilityActive) activeFactors++;

            // Требуем либо несколько факторов, либо достаточную комбинированную уверенность
            if (activeFactors < _config.MinFactorsForPrediction && combinedConfidence < _config.PredictionConfidenceThreshold)
            {
                // Слишком мало сигналов — не делаем прогноз
                ResetCandidate();
                return null;
            }

            // Формируем кандидат-прогноз (консервативные времена)
            var candidate = new PredictionResult
            {
                PredictedTime = hbActive ? now.AddSeconds(30) : (freqActive ? now.AddMinutes(15) : now.AddMinutes(10)),
                Reason = hbActive ? "Пропущены heartbeat пакеты" : (freqActive ? "Высокая частота отключений" : "Низкая стабильность соединения"),
                Confidence = Math.Max(combinedConfidence, Math.Max((int)freqScore, Math.Max((int)hbScore, (int)stabilityScore))),
                Recommendation = hbActive ? "Проверьте связь / ожидается разрыв" : "Проверьте соединение или перезапустите бота",
                IsSuppressed = false
            };

            // Подтверждение кандидата — требуется несколько последовательных срабатываний
            if (_lastCandidate == null || _lastCandidate.Reason != candidate.Reason || Math.Abs((_lastCandidate.PredictedTime - candidate.PredictedTime).TotalSeconds) > _config.ConfirmationWindowSeconds)
            {
                // новый кандидат
                _lastCandidate = candidate;
                _consecutiveMatches = 1;
                _firstCandidateTime = now;
                // не отправляем сообщение первым срабатыванием
                _lastCandidate.IsSuppressed = true;
                return null;
            }

            // Совпадение с предыдущим кандидатом
            _consecutiveMatches++;

            // Если прошло слишком много времени с первого кандидата — сбрасываем
            if ((now - _firstCandidateTime).TotalSeconds > _config.ConfirmationWindowSeconds)
            {
                _lastCandidate = candidate;
                _consecutiveMatches = 1;
                _firstCandidateTime = now;
                _lastCandidate.IsSuppressed = true;
                return null;
            }

            if (_consecutiveMatches < Math.Max(1, _config.RequireConsecutiveEvaluations))
            {
                // ждём подтверждения
                _lastCandidate.IsSuppressed = true;
                return null;
            }

            // Кандидат подтверждён — можно отправлять, но проверяем cooldown/лимиты
            if (!canSendNow)
            {
                // отмечаем, что прогноз был готов, но подавлён из-за cooldown
                candidate.IsCooldown = true;
                candidate.IsSuppressed = true;
                ResetCandidate();
                return candidate; // возвращаем как информация, но не шлём событие
            }

            // Отправляем прогноз
            UpdatePredictionCounters();
            ResetCandidate();
            await OnPredictionMade?.Invoke(candidate);
            return candidate;
        }

        private void ResetCandidate()
        {
            _lastCandidate = null;
            _consecutiveMatches = 0;
            _firstCandidateTime = DateTime.MinValue;
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
