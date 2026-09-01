using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM
{
    /// <summary>
    /// Каталог предопределённых VtM-параметров на русском.
    /// </summary>
    /// <remarks>
    /// Источник истины для автодополнения в slash-командах, валидации
    /// <c>/vampire_set</c> и отрисовки листа персонажа в <c>/vampire_view</c>.
    /// </remarks>
    public static class VampireParameterCatalog
    {
        // ─── Категории ─────────────────────────────────────────────────────

        public const string CategoryPhysical = "Физические";
        public const string CategorySocial = "Социальные";
        public const string CategoryMental = "Ментальные";
        public const string CategoryTalents = "Таланты";
        public const string CategorySkills = "Навыки";
        public const string CategoryKnowledges = "Знания";

        // ─── Характеристики (показывают +1 точку) ──────────────────────────

        public static readonly IReadOnlyList<string> Physical = new[]
        {
            "Сила",
            "Ловкость",
            "Выносливость"
        };

        public static readonly IReadOnlyList<string> Social = new[]
        {
            "Обаяние",
            "Манипуляция",
            "Привлекательность"
        };

        public static readonly IReadOnlyList<string> Mental = new[]
        {
            "Восприятие",
            "Интеллект",
            "Смекалка"
        };

        // ─── Атрибуты (без +1) ────────────────────────────────────────────

        public static readonly IReadOnlyList<string> Talents = new[]
        {
            "Атлетика",
            "Бдительность",
            "Драка",
            "Избегание",
            "Запугивание",
            "Оценка",
            "Скрытность",
            "Хитрость",
            "Эмпатия",
            "Языки"
        };

        public static readonly IReadOnlyList<string> Skills = new[]
        {
            "Вождение",
            "Воровство",
            "Выживание",
            "Животноводство",
            "Изготовление",
            "Лучник",
            "Маскировка",
            "Огнестрел",
            "Фехтование",
            "Холодное оружие"
        };

        public static readonly IReadOnlyList<string> Knowledges = new[]
        {
            "Гуманитарные науки",
            "Естественные науки",
            "Законы",
            "Академические знания",
            "Бюрократия",
            "Компьютеры",
            "Медицина",
            "Оккультизм",
            "Политика",
            "Технология"
        };

        /// <summary>Все категории по порядку (для листа персонажа).</summary>
        public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Categories =
            new Dictionary<string, IReadOnlyList<string>>
            {
                [CategoryPhysical] = Physical,
                [CategorySocial] = Social,
                [CategoryMental] = Mental,
                [CategoryTalents] = Talents,
                [CategorySkills] = Skills,
                [CategoryKnowledges] = Knowledges
            };

        /// <summary>Множество всех известных имён для быстрой проверки.</summary>
        public static readonly IReadOnlySet<string> All =
            new HashSet<string>(
                Categories.Values.SelectMany(c => c),
                System.StringComparer.Ordinal);

        /// <summary>Множество только характеристик (для правила +1).</summary>
        public static readonly IReadOnlySet<string> Characteristics =
            new HashSet<string>(
                Physical.Concat(Social).Concat(Mental),
                System.StringComparer.Ordinal);

        /// <summary>Категории, в которых лежит параметр. Пусто, если не найден.</summary>
        public static string GetCategory(string attributeName)
        {
            if (string.IsNullOrEmpty(attributeName)) return "";
            foreach (var kv in Categories)
            {
                if (kv.Value.Contains(attributeName)) return kv.Key;
            }
            return "";
        }

        /// <summary>Является ли параметр характеристикой (требует +1 точку).</summary>
        public static bool IsCharacteristic(string attributeName)
        {
            return Characteristics.Contains(attributeName);
        }

        /// <summary>Является ли имя параметра валидным (из любой категории).</summary>
        public static bool IsValid(string attributeName)
        {
            return All.Contains(attributeName);
        }

        /// <summary>Возможные значения параметра: 0..5.</summary>
        public static readonly IReadOnlyList<int> AllowedValues = new[] { 0, 1, 2, 3, 4, 5 };

        // ─── Добродетели ───────────────────────────────────────────────────

        public const string VirtueConscience = "Совесть";
        public const string VirtueSelfControl = "Самоконтроль";
        public const string VirtueCourage = "Смелость";

        public static readonly IReadOnlyList<string> Virtues = new[]
        {
            VirtueConscience,
            VirtueSelfControl,
            VirtueCourage
        };

        // ─── Кланы VtM (для подсказок в /vampire_create) ──────────────────

        public static readonly IReadOnlyList<string> Clans = new[]
        {
            "Бруха",
            "Гангрел",
            "Малкавиан",
            "Носферату",
            "Тореадор",
            "Тремер",
            "Вентру",
            "Ласомбра",
            "Цимих",
            "Равенна",
            "Салюбри",
            "Анциллы",
            "Баярон",
            "Дахат",
            "Джихад"
        };
    }
}