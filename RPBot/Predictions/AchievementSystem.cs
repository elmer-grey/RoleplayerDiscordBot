using System;
using System.Collections.Generic;
using System.Linq;

namespace RPBot.Predictions
{
    /// <summary>
    /// Система проверки и присвоения достижений
    /// </summary>
    public class AchievementSystem
    {
        /// <summary>
        /// Проверяет и присваивает достижения пользователю после завершения прогноза
        /// </summary>
        /// <returns>Список новых достижений (ID достижения, количество раз получено)</returns>
        public static List<(string achievementId, int count)> CheckAndAwardAchievements(
            UserBettingStats stats,
            PredictionHistoryEntry? latestPrediction = null,
            BetResult? latestBet = null)
        {
            var newAchievements = new List<(string, int)>();
            
            // ==================== НОВИЧКОВЫЕ ====================
            
            // 🌟 Новичок - первая ставка
            if (stats.TotalBets == 1 && !HasAchievement(stats, "newcomer"))
            {
                AddAchievement(stats, "newcomer");
                newAchievements.Add(("newcomer", 1));
            }
            
            // 🎓 Ученик - 5 прогнозов
            if (stats.TotalPredictions >= 5 && !HasAchievement(stats, "student"))
            {
                AddAchievement(stats, "student");
                newAchievements.Add(("student", 1));
            }
            
            // 🔰 Бывалый - 25 прогнозов
            if (stats.TotalPredictions >= 25 && !HasAchievement(stats, "experienced"))
            {
                AddAchievement(stats, "experienced");
                newAchievements.Add(("experienced", 1));
            }
            
            // 🌈 Разносторонний - 50 прогнозов
            if (stats.TotalPredictions >= 50 && !HasAchievement(stats, "versatile"))
            {
                AddAchievement(stats, "versatile");
                newAchievements.Add(("versatile", 1));
            }
            
            // ⭐ Ветеран - 100 прогнозов
            if (stats.TotalPredictions >= 100 && !HasAchievement(stats, "veteran"))
            {
                AddAchievement(stats, "veteran");
                newAchievements.Add(("veteran", 1));
            }
            
            // ==================== ФИНАНСОВЫЕ ====================
            
            // 💵 Первая кровь - первый выигрыш
            if (stats.Wins == 1 && !HasAchievement(stats, "first_blood"))
            {
                AddAchievement(stats, "first_blood");
                newAchievements.Add(("first_blood", 1));
            }
            
            // 💰 Богач - выиграл >10,000 за раз (повторяемое)
            if (latestBet != null && latestBet.Won && latestBet.Payout - latestBet.Amount > 10000)
            {
                var count = IncrementAchievement(stats, "rich");
                newAchievements.Add(("rich", count));
            }
            
            // 💎 Миллионер - выиграл >50,000 за раз (повторяемое)
            if (latestBet != null && latestBet.Won && latestBet.Payout - latestBet.Amount > 50000)
            {
                var count = IncrementAchievement(stats, "millionaire");
                newAchievements.Add(("millionaire", count));
            }
            
            // 🏦 Банкир - общая прибыль >100,000
            if (stats.NetProfit > 100000 && !HasAchievement(stats, "banker"))
            {
                AddAchievement(stats, "banker");
                newAchievements.Add(("banker", 1));
            }
            
            // 🔥 Хайроллер - поставил >1,000 за раз (повторяемое)
            if (latestBet != null && latestBet.Amount > 1000)
            {
                var count = IncrementAchievement(stats, "highroller");
                newAchievements.Add(("highroller", count));
            }
            
            // 🐋 Кит - поставил >10,000 за раз (повторяемое)
            if (latestBet != null && latestBet.Amount > 10000)
            {
                var count = IncrementAchievement(stats, "whale");
                newAchievements.Add(("whale", count));
            }
            
            // ==================== ТОЧНОСТЬ ====================
            
            // 🎯 Снайпер - 5 ставок с коэфф. >10x
            if (stats.HighCoeffWins >= 5 && !HasAchievement(stats, "sniper"))
            {
                AddAchievement(stats, "sniper");
                newAchievements.Add(("sniper", 1));
            }
            
            // 🏹 Меткий - винрейт >70% (мин. 20 ставок)
            if (stats.TotalBets >= 20 && stats.WinRate > 70 && !HasAchievement(stats, "accurate"))
            {
                AddAchievement(stats, "accurate");
                newAchievements.Add(("accurate", 1));
            }
            
            // 🎲 Удачливый - 10 выигрышей подряд
            if (stats.BestWinStreak >= 10 && !HasAchievement(stats, "lucky"))
            {
                AddAchievement(stats, "lucky");
                newAchievements.Add(("lucky", 1));
            }
            
            // 🔥 Огонь - 20 выигрышей подряд
            if (stats.BestWinStreak >= 20 && !HasAchievement(stats, "on_fire"))
            {
                AddAchievement(stats, "on_fire");
                newAchievements.Add(("on_fire", 1));
            }
            
            // ⚡ Молния - 3 победы подряд с коэфф. >5x
            if (stats.CurrentHighCoeffStreak >= 3 && !HasAchievement(stats, "lightning"))
            {
                AddAchievement(stats, "lightning");
                newAchievements.Add(("lightning", 1));
            }
            
            // ==================== РИСК ====================
            
            // 🎰 Рисковый - выиграл с коэфф. >10x (повторяемое)
            if (latestBet != null && latestBet.Won && latestBet.Coefficient > 10)
            {
                var count = IncrementAchievement(stats, "risky");
                newAchievements.Add(("risky", count));
            }
            
            // 🚀 Безумец - выиграл с коэфф. >20x (повторяемое)
            if (latestBet != null && latestBet.Won && latestBet.Coefficient > 20)
            {
                var count = IncrementAchievement(stats, "madman");
                newAchievements.Add(("madman", count));
            }
            
            // 💫 Легенда - выиграл с коэфф. >50x (повторяемое)
            if (latestBet != null && latestBet.Won && latestBet.Coefficient > 50)
            {
                var count = IncrementAchievement(stats, "legend");
                newAchievements.Add(("legend", count));
            }
            
            // 🌪️ Ураган - 3 побед на аутсайдерах за неделю
            if (stats.WeekHighCoeffWins >= 3 && !HasAchievement(stats, "hurricane"))
            {
                AddAchievement(stats, "hurricane");
                newAchievements.Add(("hurricane", 1));
            }
            
            // 🎪 Казино - поставил на все исходы одного прогноза (проверяется в другом месте)
            // Этот флаг нужно ставить при анализе ставок на конкретный прогноз
            
            // ==================== СТРАТЕГИЯ ====================
            
            // 📊 Аналитик - процент побед на фаворитах >80%
            if (stats.FavoriteBets >= 10 && stats.FavoriteBets > 0)
            {
                var favoriteWinRate = (double)stats.FavoriteWins / stats.FavoriteBets * 100;
                if (favoriteWinRate > 80 && !HasAchievement(stats, "analyst"))
                {
                    AddAchievement(stats, "analyst");
                    newAchievements.Add(("analyst", 1));
                }
            }
            
            // 📈 Стратег - прибыль >50,000 за месяц
            if (stats.MonthProfit > 50000 && !HasAchievement(stats, "strategist"))
            {
                AddAchievement(stats, "strategist");
                newAchievements.Add(("strategist", 1));
            }
            
            // 🧮 Математик - создал 10 прогнозов
            if (stats.PredictionsCreated >= 10 && !HasAchievement(stats, "mathematician"))
            {
                AddAchievement(stats, "mathematician");
                newAchievements.Add(("mathematician", 1));
            }
            
            // 👑 Король прогнозов - создал 50 прогнозов
            if (stats.PredictionsCreated >= 50 && !HasAchievement(stats, "prediction_king"))
            {
                AddAchievement(stats, "prediction_king");
                newAchievements.Add(("prediction_king", 1));
            }
            
            // ==================== СПЕЦИАЛЬНЫЕ ====================
            
            // 🥇 Первый! - первым поставил в прогнозе (повторяемое)
            // Проверяется при создании ставки
            
            // 🦅 Ранняя пташка - 10 раз ставил первым
            if (stats.FirstBets >= 10 && !HasAchievement(stats, "early_bird"))
            {
                AddAchievement(stats, "early_bird");
                newAchievements.Add(("early_bird", 1));
            }
            
            // 😎 Одиночка - выиграл будучи единственным на исходе (повторяемое)
            // Проверяется в UpdateStatsAfterResolve
            
            // 🎭 Трикстер - выиграл на исходе где было <10% банка (повторяемое)
            // Проверяется в UpdateStatsAfterResolve
            
            // 💯 Перфекционист - 50 ставок подряд без проигрышей
            if (stats.CurrentStreak >= 50 && !HasAchievement(stats, "perfectionist"))
            {
                AddAchievement(stats, "perfectionist");
                newAchievements.Add(("perfectionist", 1));
            }
            
            return newAchievements;
        }
        
        /// <summary>
        /// Обновляет статистику пользователя после завершения прогноза
        /// </summary>
        public static void UpdateStatsAfterResolve(
            UserBettingStats stats,
            PredictionHistoryEntry prediction,
            BetResult userBet)
        {
            // Обновляем базовую статистику
            stats.TotalPredictions++;
            stats.TotalBets++;
            stats.TotalWagered += userBet.Amount;
            
            if (userBet.Won)
            {
                stats.Wins++;
                var profit = userBet.Payout - userBet.Amount;
                stats.TotalWon += userBet.Payout;
                stats.NetProfit += profit;
                
                // Серии
                if (stats.CurrentStreak > 0)
                    stats.CurrentStreak++;
                else
                    stats.CurrentStreak = 1;
                
                if (stats.CurrentStreak > stats.BestWinStreak)
                    stats.BestWinStreak = stats.CurrentStreak;
                
                // Рекорды
                if (profit > stats.BiggestWin)
                    stats.BiggestWin = profit;
                
                if (userBet.Coefficient > stats.HighestCoeffWin)
                    stats.HighestCoeffWin = userBet.Coefficient;
                
                // Высокий коэффициент
                if (userBet.Coefficient > 10)
                {
                    stats.HighCoeffWins++;

                    // За день
                    if (DateTime.UtcNow.Date == stats.DayStart.Date)
                    {
                        stats.DayHighCoeffWins++;
                    }
                    else
                    {
                        stats.DayStart = DateTime.UtcNow.Date;
                        stats.DayHighCoeffWins = 1;
                    }

                    // За неделю
                    var weekStart = DateTime.UtcNow.Date.AddDays(-(int)DateTime.UtcNow.DayOfWeek);
                    if (stats.WeekStart == default || weekStart == stats.WeekStart)
                    {
                        stats.WeekStart = weekStart;
                        stats.WeekHighCoeffWins++;
                    }
                    else if (weekStart > stats.WeekStart)
                    {
                        stats.WeekStart = weekStart;
                        stats.WeekHighCoeffWins = 1;
                    }
                }
                
                // Серия высоких коэффициентов
                if (userBet.Coefficient > 5)
                {
                    stats.CurrentHighCoeffStreak++;
                }
                else
                {
                    stats.CurrentHighCoeffStreak = 0;
                }
                
                // Фавориты (коэфф <2x)
                if (userBet.Coefficient < 2)
                {
                    stats.FavoriteWins++;
                    stats.FavoriteBets++;
                }
                
                // 😎 Одиночка - единственный на исходе
                var winnersOnOutcome = prediction.Bets.Count(b => b.OutcomeId == userBet.OutcomeId);
                if (winnersOnOutcome == 1)
                {
                    IncrementAchievement(stats, "loner");
                }
                
                // 🎭 Трикстер - исход <10% банка
                var outcomePool = prediction.Bets.Where(b => b.OutcomeId == userBet.OutcomeId).Sum(b => b.Amount);
                var percentage = prediction.TotalPool > 0 ? (double)outcomePool / prediction.TotalPool * 100 : 0;
                if (percentage < 10 && percentage > 0)
                {
                    IncrementAchievement(stats, "trickster");
                }
            }
            else
            {
                stats.Losses++;
                stats.NetProfit -= userBet.Amount;
                
                // Серии
                if (stats.CurrentStreak < 0)
                    stats.CurrentStreak--;
                else
                    stats.CurrentStreak = -1;
                
                stats.CurrentHighCoeffStreak = 0;
                
                // Фавориты
                if (userBet.Coefficient < 2)
                {
                    stats.FavoriteBets++;
                }
            }
            
            // Рекорд ставки
            if (userBet.Amount > stats.LargestBet)
                stats.LargestBet = userBet.Amount;
            
            // За месяц
            if (DateTime.UtcNow.Year == stats.MonthStart.Year && DateTime.UtcNow.Month == stats.MonthStart.Month)
            {
                stats.MonthProfit += (userBet.Won ? userBet.Payout - userBet.Amount : -userBet.Amount);
            }
            else
            {
                stats.MonthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
                stats.MonthProfit = userBet.Won ? userBet.Payout - userBet.Amount : -userBet.Amount;
            }
        }
        
        /// <summary>
        /// Обновляет счётчик созданных прогнозов
        /// </summary>
        public static void UpdateStatsAfterCreate(UserBettingStats stats)
        {
            stats.PredictionsCreated++;
        }
        
        /// <summary>
        /// Отмечает первую ставку в прогнозе
        /// </summary>
        public static void MarkFirstBet(UserBettingStats stats)
        {
            stats.FirstBets++;
            IncrementAchievement(stats, "first_place");
        }
        
        /// <summary>
        /// Проверяет наличие достижения
        /// </summary>
        private static bool HasAchievement(UserBettingStats stats, string achievementId)
        {
            return stats.Achievements.Any(a => a.AchievementId == achievementId);
        }
        
        /// <summary>
        /// Добавляет достижение (для неповторяемых)
        /// </summary>
        private static void AddAchievement(UserBettingStats stats, string achievementId)
        {
            if (!HasAchievement(stats, achievementId))
            {
                stats.Achievements.Add(new UserAchievement
                {
                    AchievementId = achievementId,
                    UnlockedAt = DateTime.UtcNow,
                    Count = 1
                });
            }
        }
        
        /// <summary>
        /// Увеличивает счётчик достижения (для повторяемых)
        /// </summary>
        /// <returns>Новое количество</returns>
        private static int IncrementAchievement(UserBettingStats stats, string achievementId)
        {
            var existing = stats.Achievements.FirstOrDefault(a => a.AchievementId == achievementId);
            if (existing != null)
            {
                existing.Count++;
                return existing.Count;
            }
            else
            {
                stats.Achievements.Add(new UserAchievement
                {
                    AchievementId = achievementId,
                    UnlockedAt = DateTime.UtcNow,
                    Count = 1
                });
                return 1;
            }
        }
    }
}
