using System.Collections.Generic;

namespace RPBot.Predictions
{
    /// <summary>
    /// Тип достижения
    /// </summary>
    public enum AchievementType
    {
        Beginner,     // Новичковые
        Financial,    // Финансовые
        Accuracy,     // Точность
        Risk,         // Риск
        Strategy,     // Стратегия
        Special       // Специальные
    }
    
    /// <summary>
    /// Определение достижения
    /// </summary>
    public class AchievementDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public AchievementType Type { get; set; }
        public int Rarity { get; set; } // 1=обычное, 2=редкое, 3=эпик, 4=легендарное
        
        /// <summary>
        /// Может быть получено многократно
        /// </summary>
        public bool Repeatable { get; set; }
    }
    
    /// <summary>
    /// Список всех достижений в системе
    /// </summary>
    public static class AchievementDefinitions
    {
        public static readonly Dictionary<string, AchievementDefinition> All = new()
        {
            // ==================== НОВИЧКОВЫЕ (5) ====================
            ["newcomer"] = new()
            {
                Id = "newcomer",
                Icon = "🌟",
                Name = "Новичок",
                Description = "Сделал первую ставку",
                Type = AchievementType.Beginner,
                Rarity = 1,
                Repeatable = false
            },
            ["student"] = new()
            {
                Id = "student",
                Icon = "🎓",
                Name = "Ученик",
                Description = "Участвовал в 5 прогнозах",
                Type = AchievementType.Beginner,
                Rarity = 1,
                Repeatable = false
            },
            ["experienced"] = new()
            {
                Id = "experienced",
                Icon = "🔰",
                Name = "Бывалый",
                Description = "Участвовал в 25 прогнозах",
                Type = AchievementType.Beginner,
                Rarity = 2,
                Repeatable = false
            },
            ["versatile"] = new()
            {
                Id = "versatile",
                Icon = "🌈",
                Name = "Разносторонний",
                Description = "Участвовал в 50 прогнозах",
                Type = AchievementType.Beginner,
                Rarity = 2,
                Repeatable = false
            },
            ["veteran"] = new()
            {
                Id = "veteran",
                Icon = "⭐",
                Name = "Ветеран",
                Description = "Участвовал в 100 прогнозах",
                Type = AchievementType.Beginner,
                Rarity = 3,
                Repeatable = false
            },
            
            // ==================== ФИНАНСОВЫЕ (6) ====================
            ["first_blood"] = new()
            {
                Id = "first_blood",
                Icon = "💵",
                Name = "Первая кровь",
                Description = "Первый выигрыш",
                Type = AchievementType.Financial,
                Rarity = 1,
                Repeatable = false
            },
            ["rich"] = new()
            {
                Id = "rich",
                Icon = "💰",
                Name = "Богач",
                Description = "Выиграл >10,000 за раз",
                Type = AchievementType.Financial,
                Rarity = 2,
                Repeatable = true
            },
            ["millionaire"] = new()
            {
                Id = "millionaire",
                Icon = "💎",
                Name = "Миллионер",
                Description = "Выиграл >50,000 за раз",
                Type = AchievementType.Financial,
                Rarity = 3,
                Repeatable = true
            },
            ["banker"] = new()
            {
                Id = "banker",
                Icon = "🏦",
                Name = "Банкир",
                Description = "Общая прибыль >100,000",
                Type = AchievementType.Financial,
                Rarity = 3,
                Repeatable = false
            },
            ["highroller"] = new()
            {
                Id = "highroller",
                Icon = "🔥",
                Name = "Хайроллер",
                Description = "Поставил >1,000 за раз",
                Type = AchievementType.Financial,
                Rarity = 2,
                Repeatable = true
            },
            ["whale"] = new()
            {
                Id = "whale",
                Icon = "🐋",
                Name = "Кит",
                Description = "Поставил >10,000 за раз",
                Type = AchievementType.Financial,
                Rarity = 3,
                Repeatable = true
            },
            
            // ==================== ТОЧНОСТЬ (5) ====================
            ["sniper"] = new()
            {
                Id = "sniper",
                Icon = "🎯",
                Name = "Снайпер",
                Description = "5 ставок с коэфф. >10x",
                Type = AchievementType.Accuracy,
                Rarity = 2,
                Repeatable = false
            },
            ["accurate"] = new()
            {
                Id = "accurate",
                Icon = "🏹",
                Name = "Меткий",
                Description = "Процент побед >70% (мин. 20 ставок)",
                Type = AchievementType.Accuracy,
                Rarity = 3,
                Repeatable = false
            },
            ["lucky"] = new()
            {
                Id = "lucky",
                Icon = "🎲",
                Name = "Удачливый",
                Description = "10 выигрышей подряд",
                Type = AchievementType.Accuracy,
                Rarity = 2,
                Repeatable = false
            },
            ["on_fire"] = new()
            {
                Id = "on_fire",
                Icon = "🔥",
                Name = "Огонь",
                Description = "20 выигрышей подряд",
                Type = AchievementType.Accuracy,
                Rarity = 4,
                Repeatable = false
            },
            ["lightning"] = new()
            {
                Id = "lightning",
                Icon = "⚡",
                Name = "Молния",
                Description = "3 победы подряд с коэфф. >5x",
                Type = AchievementType.Accuracy,
                Rarity = 3,
                Repeatable = false
            },
            
            // ==================== РИСК (5) ====================
            ["risky"] = new()
            {
                Id = "risky",
                Icon = "🎰",
                Name = "Рисковый",
                Description = "Выиграл с коэфф. >10x",
                Type = AchievementType.Risk,
                Rarity = 2,
                Repeatable = true
            },
            ["madman"] = new()
            {
                Id = "madman",
                Icon = "🚀",
                Name = "Безумец",
                Description = "Выиграл с коэфф. >20x",
                Type = AchievementType.Risk,
                Rarity = 3,
                Repeatable = true
            },
            ["legend"] = new()
            {
                Id = "legend",
                Icon = "💫",
                Name = "Легенда",
                Description = "Выиграл с коэфф. >50x",
                Type = AchievementType.Risk,
                Rarity = 4,
                Repeatable = true
            },
            ["hurricane"] = new()
            {
                Id = "hurricane",
                Icon = "🌪️",
                Name = "Ураган",
                Description = "5 побед на аутсайдерах (>10x) за день",
                Type = AchievementType.Risk,
                Rarity = 3,
                Repeatable = false
            },
            ["casino"] = new()
            {
                Id = "casino",
                Icon = "🎪",
                Name = "Казино",
                Description = "Поставил на все исходы одного прогноза",
                Type = AchievementType.Risk,
                Rarity = 2,
                Repeatable = true
            },
            
            // ==================== СТРАТЕГИЯ (4) ====================
            ["analyst"] = new()
            {
                Id = "analyst",
                Icon = "📊",
                Name = "Аналитик",
                Description = "Процент побед на фаворитах >80%",
                Type = AchievementType.Strategy,
                Rarity = 3,
                Repeatable = false
            },
            ["strategist"] = new()
            {
                Id = "strategist",
                Icon = "📈",
                Name = "Стратег",
                Description = "Прибыль >10,000 за месяц",
                Type = AchievementType.Strategy,
                Rarity = 2,
                Repeatable = false
            },
            ["mathematician"] = new()
            {
                Id = "mathematician",
                Icon = "🧮",
                Name = "Математик",
                Description = "Создал 10 прогнозов",
                Type = AchievementType.Strategy,
                Rarity = 2,
                Repeatable = false
            },
            ["prediction_king"] = new()
            {
                Id = "prediction_king",
                Icon = "👑",
                Name = "Король прогнозов",
                Description = "Создал 50 прогнозов",
                Type = AchievementType.Strategy,
                Rarity = 3,
                Repeatable = false
            },
            
            // ==================== СПЕЦИАЛЬНЫЕ (5) ====================
            ["first_place"] = new()
            {
                Id = "first_place",
                Icon = "🥇",
                Name = "Первый!",
                Description = "Первым поставил в прогнозе",
                Type = AchievementType.Special,
                Rarity = 1,
                Repeatable = true
            },
            ["early_bird"] = new()
            {
                Id = "early_bird",
                Icon = "🦅",
                Name = "Ранняя пташка",
                Description = "10 раз ставил первым",
                Type = AchievementType.Special,
                Rarity = 2,
                Repeatable = false
            },
            ["loner"] = new()
            {
                Id = "loner",
                Icon = "😎",
                Name = "Одиночка",
                Description = "Выиграл прогноз будучи единственным на исходе",
                Type = AchievementType.Special,
                Rarity = 3,
                Repeatable = true
            },
            ["trickster"] = new()
            {
                Id = "trickster",
                Icon = "🎭",
                Name = "Трикстер",
                Description = "Выиграл на исходе где было <10% банка",
                Type = AchievementType.Special,
                Rarity = 2,
                Repeatable = true
            },
            ["perfectionist"] = new()
            {
                Id = "perfectionist",
                Icon = "💯",
                Name = "Перфекционист",
                Description = "50 ставок подряд без проигрышей",
                Type = AchievementType.Special,
                Rarity = 4,
                Repeatable = false
            }
        };
        
        /// <summary>
        /// Получить иконку редкости
        /// </summary>
        public static string GetRarityIcon(int rarity)
        {
            return rarity switch
            {
                1 => "⚪", // Обычное
                2 => "🟢", // Редкое
                3 => "🔵", // Эпик
                4 => "🟣", // Легендарное
                _ => "⚪"
            };
        }
    }
}
