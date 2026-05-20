using System;
using System.Collections.Generic;

namespace RPBot.Predictions
{
    /// <summary>
    /// Прогресс достижения для пользователя
    /// </summary>
    public class UserAchievement
    {
        public string AchievementId { get; set; } = string.Empty;
        public DateTime UnlockedAt { get; set; }
        public int Count { get; set; } = 1; // Сколько раз получено (для repeatable)
    }
    
    /// <summary>
    /// Статистика ставок пользователя для расчёта достижений
    /// </summary>
    public class UserBettingStats
    {
        public ulong UserId { get; set; }
        public ulong GuildId { get; set; }
        
        // Общая статистика
        public int TotalPredictions { get; set; }  // Участвовал в прогнозах
        public int TotalBets { get; set; }         // Всего ставок
        public int Wins { get; set; }              // Выигрышей
        public int Losses { get; set; }            // Проигрышей
        public long TotalWagered { get; set; }     // Всего поставлено
        public long TotalWon { get; set; }         // Всего выиграно
        public long NetProfit { get; set; }        // Чистая прибыль
        
        // Серии
        public int CurrentStreak { get; set; }     // Текущая серия побед/поражений (+ или -)
        public int BestWinStreak { get; set; }     // Лучшая серия побед
        public int CurrentHighCoeffStreak { get; set; } // Текущая серия побед с коэфф >5x
        
        // Рекорды
        public long BiggestWin { get; set; }       // Самый большой выигрыш
        public double HighestCoeffWin { get; set; } // Самый высокий коэффициент выигрыша
        public long LargestBet { get; set; }       // Самая большая ставка
        
        // Специальные счётчики
        public int HighCoeffWins { get; set; }     // Выигрыши с коэфф >10x
        public int FirstBets { get; set; }         // Сколько раз ставил первым
        public int PredictionsCreated { get; set; } // Созданных прогнозов
        
        // За месяц (для стратега)
        public DateTime MonthStart { get; set; }
        public long MonthProfit { get; set; }
        
        // За день (для урагана)
        public DateTime DayStart { get; set; }
        public int DayHighCoeffWins { get; set; }  // Побед на аутсайдерах за день

        // За неделю (для урагана)
        public DateTime WeekStart { get; set; }
        public int WeekHighCoeffWins { get; set; }  // Побед на аутсайдерах за неделю

        // Для аналитика
        public int FavoriteWins { get; set; }      // Побед на фаворитах (коэфф <2x)
        public int FavoriteBets { get; set; }      // Ставок на фаворитов
        
        // Список достижений
        public List<UserAchievement> Achievements { get; set; } = new();
        
        /// <summary>
        /// Процент побед
        /// </summary>
        public double WinRate => TotalBets > 0 ? (double)Wins / TotalBets * 100 : 0;
        
        /// <summary>
        /// Средняя ставка
        /// </summary>
        public long AverageBet => TotalBets > 0 ? TotalWagered / TotalBets : 0;
        
        /// <summary>
        /// ROI (Return on Investment)
        /// </summary>
        public double ROI => TotalWagered > 0 ? (double)NetProfit / TotalWagered * 100 : 0;
    }
    
    /// <summary>
    /// Хранилище статистики и достижений всех пользователей
    /// </summary>
    public class UserAchievementsStore
    {
        /// <summary>
        /// Статистика по пользователям (ключ - "guildId:userId")
        /// </summary>
        public Dictionary<string, UserBettingStats> Users { get; set; } = new();
    }
}
