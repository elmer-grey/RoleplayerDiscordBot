using System;
using System.Collections.Generic;

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
        public PredictionOutcome Outcome1 { get; set; } = new PredictionOutcome { Id = 1 };
        public PredictionOutcome Outcome2 { get; set; } = new PredictionOutcome { Id = 2 };
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset BetsCloseAtUtc { get; set; }
        public bool IsLocked { get; set; }
        public bool IsResolved { get; set; }
        public int? WinningOutcomeId { get; set; }
        public Dictionary<ulong, PredictionBet> Bets { get; set; } = new();

        // Synchronization primitive for concurrent operations on this prediction
        public System.Threading.SemaphoreSlim Sync { get; } = new System.Threading.SemaphoreSlim(1, 1);

        public long TotalPool => Outcome1.TotalStake + Outcome2.TotalStake;

        public double RawOdds1 => Outcome1.TotalStake <= 0 ? 1.0 : (double)TotalPool / Outcome1.TotalStake;
        public double RawOdds2 => Outcome2.TotalStake <= 0 ? 1.0 : (double)TotalPool / Outcome2.TotalStake;

        public double Coef1 => RawOdds1 - 1.0; // надбавка к ставке
        public double Coef2 => RawOdds2 - 1.0;
    }
}
