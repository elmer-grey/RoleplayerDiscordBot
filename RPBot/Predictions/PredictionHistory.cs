using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RPBot.Predictions
{
    /// <summary>
    /// Запись завершённого прогноза в истории
    /// </summary>
    public class PredictionHistoryEntry
    {
        public ulong GuildId { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public List<PredictionOutcome> Outcomes { get; set; } = new();
        
        /// <summary>
        /// ID победившего исхода (null если отменён)
        /// </summary>
        public int? WinningOutcomeId { get; set; }
        
        /// <summary>
        /// Название победившего исхода
        /// </summary>
        public string? WinningOutcomeName { get; set; }
        
        /// <summary>
        /// Общая сумма выплат победителям
        /// </summary>
        public long TotalPayout { get; set; }
        
        /// <summary>
        /// Общий банк прогноза
        /// </summary>
        public long TotalPool { get; set; }
        
        /// <summary>
        /// Список ставок с результатами
        /// </summary>
        public List<BetResult> Bets { get; set; } = new();
        
        /// <summary>
        /// Был ли прогноз отменён
        /// </summary>
        public bool WasCancelled { get; set; }
        
        /// <summary>
        /// ID создателя прогноза
        /// </summary>
        public ulong CreatorId { get; set; }
    }
    
    /// <summary>
    /// Результат ставки в завершённом прогнозе
    /// </summary>
    public class BetResult
    {
        public ulong UserId { get; set; }
        public int OutcomeId { get; set; }
        public long Amount { get; set; }
        public long Payout { get; set; } // 0 если проиграл, Amount если отменён, Amount * коэфф если выиграл
        public bool Won { get; set; }
        public double Coefficient { get; set; } // Коэффициент на момент завершения
        public bool WasFavorite { get; set; }
        public bool WasUnderdog { get; set; }
    }
    
    /// <summary>
    /// Хранилище истории прогнозов по гильдиям
    /// </summary>
    public class PredictionHistoryStore
    {
        /// <summary>
        /// История по гильдиям (ключ - GuildId)
        /// </summary>
        public Dictionary<ulong, List<PredictionHistoryEntry>> History { get; set; } = new();
        
        /// <summary>
        /// Максимальное количество записей в истории на гильдию
        /// </summary>
        [JsonIgnore]
        public const int MaxHistoryPerGuild = 100;
    }
}
