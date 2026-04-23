using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace RPBot
{
    public class PredictionOutcome
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public long TotalStake { get; set; }
        public ulong? TopUserId { get; set; }
        public long TopUserStake { get; set; }
    }

    public class PredictionBet
    {
        public ulong UserId { get; set; }
        public int OutcomeId { get; set; }
        public long Amount { get; set; }
    }

    public class ActivePrediction
    {
        public ulong GuildId { get; set; }
        public ulong CreatorId { get; set; }
        public ulong ChannelId { get; set; }
        public ulong MessageId { get; set; }
        public string Title { get; set; } = string.Empty;

        // ✅ НОВОЕ: Список исходов для поддержки N вариантов
        public List<PredictionOutcome> Outcomes { get; set; } = new();

        // ✅ ОБРАТНАЯ СОВМЕСТИМОСТЬ: Свойства для старых данных (2 исхода)
        [JsonIgnore]
        public PredictionOutcome Outcome1
        {
            get => Outcomes.Count > 0 ? Outcomes[0] : new PredictionOutcome { Id = 1 };
            set
            {
                if (Outcomes.Count == 0)
                    Outcomes.Add(value);
                else
                    Outcomes[0] = value;
            }
        }

        [JsonIgnore]
        public PredictionOutcome Outcome2
        {
            get => Outcomes.Count > 1 ? Outcomes[1] : new PredictionOutcome { Id = 2 };
            set
            {
                if (Outcomes.Count == 0)
                    Outcomes.Add(new PredictionOutcome { Id = 1 });
                if (Outcomes.Count == 1)
                    Outcomes.Add(value);
                else
                    Outcomes[1] = value;
            }
        }

        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset BetsCloseAtUtc { get; set; }
        public bool IsLocked { get; set; }
        public bool IsResolved { get; set; }
        public int? WinningOutcomeId { get; set; }
        public Dictionary<ulong, PredictionBet> Bets { get; set; } = new();

        // Synchronization primitive for concurrent operations on this prediction
        [JsonIgnore]
        public System.Threading.SemaphoreSlim Sync { get; } = new System.Threading.SemaphoreSlim(1, 1);

        // ✅ ОБНОВЛЕНО: TotalPool для N исходов
        [JsonIgnore]
        public long TotalPool => Outcomes.Sum(o => o.TotalStake);

        // ✅ ОБРАТНАЯ СОВМЕСТИМОСТЬ: Коэффициенты для первых двух исходов
        [JsonIgnore]
        public double RawOdds1 => Outcome1.TotalStake <= 0 ? 1.0 : (double)TotalPool / Outcome1.TotalStake;

        [JsonIgnore]
        public double RawOdds2 => Outcome2.TotalStake <= 0 ? 1.0 : (double)TotalPool / Outcome2.TotalStake;

        // ✅ ИСПРАВЛЕНО: Coef1 и Coef2 теперь возвращают rawOdds (не вычитают 1)
        [JsonIgnore]
        public double Coef1 => Math.Round(RawOdds1, 2, MidpointRounding.AwayFromZero);

        [JsonIgnore]
        public double Coef2 => Math.Round(RawOdds2, 2, MidpointRounding.AwayFromZero);

        // ✅ НОВОЕ: Методы для работы с N исходами
        public PredictionOutcome? GetOutcomeById(int id) => Outcomes.FirstOrDefault(o => o.Id == id);

        public double GetRawOdds(int outcomeId)
        {
            var outcome = GetOutcomeById(outcomeId);
            if (outcome == null || outcome.TotalStake <= 0) return 1.0;
            return (double)TotalPool / outcome.TotalStake;
        }

        // ✅ ИСПРАВЛЕНО: GetCoefficient теперь возвращает rawOdds (не вычитает 1)
        // Коэффициент показывает сколько вернётся за каждую поставленную костяшку
        public double GetCoefficient(int outcomeId)
        {
            var rawOdds = GetRawOdds(outcomeId);
            return Math.Round(rawOdds, 2, MidpointRounding.AwayFromZero);
        }
    }
}
