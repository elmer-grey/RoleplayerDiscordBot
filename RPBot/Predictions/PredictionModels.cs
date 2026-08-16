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

        // ✅ Bug 6: событие offline/online для лога в прогнозе
        public enum OfflineEventKind
        {
            Disconnected,
            Reconnected,
        }

        public class OfflineEvent
        {
            public DateTimeOffset AtUtc { get; set; }
            public OfflineEventKind Kind { get; set; }
            // "restart" — наш рестарт (.restart_pending), "reconnect" — микроразрыв,
            // "offline" — длительное отключение
            public string Severity { get; set; } = "offline";
            public string? Note { get; set; }
        }

        // ✅ Round 7-C4: ID «online»-сообщения + время, через которое его удалить.
        // При рестарте сериализуется в файл, чтобы при возвращении бота в сеть
        // запланировать удаление оставшихся online-сообщений.
        public class OnlineAnnouncementMessage
        {
            public ulong MessageId { get; set; }
            public DateTimeOffset SentAtUtc { get; set; }
        }

    public class ActivePrediction
    {
        public ulong GuildId { get; set; }
        public ulong CreatorId { get; set; }
        public ulong ChannelId { get; set; }
        public ulong MessageId { get; set; }
        public string Title { get; set; } = string.Empty;
        // ✅ Round 7-C8: связь прогноза с Discord-событием, чтобы при завершении/отмене
        // события (включая stale-cleanup после рестарта бота) прогноз автоматически
        // отменялся через PredictionService.CancelAsync(...).
        public ulong? EventId { get; set; }
        public bool UseCompactOutcomeLabels { get; set; }
        public bool UseInlineOutcomeFields { get; set; } = true;

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
        // ✅ Bug 5: если бот ушёл в offline во время приёма ставок, сохраняем момент.
        // Используется в Shutdown()/OnClientDisconnected, сбрасывается в LoadStateAsync.
        public DateTimeOffset? BotOfflineAtUtc { get; set; }
        public double? LastOfflineDurationMinutes { get; set; }
        public bool WasBotOfflineOnShutdown { get; set; }
        // ✅ Bug 6: лог событий offline/online для embed'а результата и истории
        public List<OfflineEvent> OfflineEvents { get; set; } = new();
        // ✅ Bug 6 (новое): ID сообщений-объявлений offline/online в канале прогноза.
        // AnnounceOfflineAsync добавляет ID, AnnounceOnlineAsync удаляет их и шлёт
        // своё сообщение «бот снова онлайн». Это избавляет канал от накопления
        // мусорных сообщений при каждом реконнекте.
        public List<ulong> OfflineAnnouncementMessageIds { get; set; } = new();
        // ✅ Round 7-C4: ID «online»-сообщений для отложенного удаления через 5 минут.
        // Шлём «Бот снова в сети» и сохраняем ID; фоновый таск удаляет через 5 минут,
        // чтобы канал не копил мусор, но сообщение оставалось видимым достаточно долго.
        public List<OnlineAnnouncementMessage> OnlineAnnouncementMessageIds { get; set; } = new();
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

    // ✅ НОВОЕ: Статистика пользователя по прогнозам
    public class UserPredictionStats
    {
        public ulong UserId { get; set; }
        public int TotalBets { get; set; }              // Всего ставок сделано
        public int WonBets { get; set; }                // Выигранных ставок
        public int LostBets { get; set; }               // Проигранных ставок
        public long TotalWagered { get; set; }          // Всего поставлено
        public long TotalWon { get; set; }              // Всего выиграно (чистая прибыль)
        public long TotalLost { get; set; }             // Всего проиграно
        public long NetProfit { get; set; }             // Чистая прибыль (TotalWon - TotalLost)
        public int CurrentStreak { get; set; }          // Текущая серия (+ выигрыши, - проигрыши)
        public int BestStreak { get; set; }             // Лучшая серия выигрышей
        public int CurrentHighCoeffStreak { get; set; } // Серия побед с коэфф. >5x
        public long HighestSingleWin { get; set; }      // Самый большой выигрыш за раз
        public double HighestCoeffWin { get; set; }     // Самый высокий коэффициент выигравшей ставки
        public long LargestBet { get; set; }            // Самая крупная ставка
        public int TotalParticipation { get; set; }     // Участий в прогнозах
        public int FirstBets { get; set; }              // Сколько раз пользователь был первым, кто поставил в прогнозе
        public int CreatedPredictions { get; set; }     // Сколько прогнозов создал пользователь
        public int HighCoeffWins { get; set; }          // Победы с коэфф. >10x
        public int FavoriteWins { get; set; }           // Победы на фаворитах
        public int FavoriteBets { get; set; }           // Ставки на фаворитов
        public DateTime WeekStartUtc { get; set; }      // Начало календарной недели (понедельник, UTC)
        public int WeekHighCoeffWins { get; set; }      // Победы с коэфф. >10x за текущую неделю
        public DateTime MonthStartUtc { get; set; }     // Начало календарного месяца (UTC)
        public long MonthProfit { get; set; }           // Прибыль за текущий календарный месяц
        public double WinRate => TotalBets > 0 ? (double)WonBets / TotalBets * 100 : 0;
        public double ROI => TotalWagered > 0 ? (double)NetProfit / TotalWagered * 100 : 0;
    }

    // ✅ НОВОЕ: Достижение
    public class Achievement
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Emoji { get; set; } = string.Empty;
    }

    // ✅ НОВОЕ: Полученное достижение пользователя
    public class UserAchievement
    {
        public ulong UserId { get; set; }
        public string AchievementId { get; set; } = string.Empty;
        public DateTimeOffset EarnedAt { get; set; }
    }
}
