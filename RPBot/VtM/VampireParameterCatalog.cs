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
    /// Списки талантов/навыков/знаний верифицированы по VtM V20 Anniversary (юбилейное издание),
    /// лист персонажа стр. 92-93, PDF-дамп от 2026-09-01.
    /// Не путать с V20 Dark Ages или V5 — у них другие списки.
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

        // V20 Anniversary (стр. 92-93): 10 талантов — черты врождённые, инстинктивные.
        public static readonly IReadOnlyList<string> Talents = new[]
        {
            "Атлетика",
            "Бдительность",
            "Драка",
            "Запугивание",
            "Красноречие",
            "Лидерство",
            "Уличное чутьё",
            "Хитрость",
            "Шестое чувство",
            "Эмпатия"
        };

        // V20 Anniversary (стр. 92-93): 10 навыков — требуют обучения или практики.
        public static readonly IReadOnlyList<string> Skills = new[]
        {
            "Вождение",
            "Воровство",
            "Выживание",
            "Исполнение",
            "Обращение с животными",
            "Ремесло",
            "Скрытность",
            "Стрельба",
            "Фехтование",
            "Этикет"
        };

        // V20 Anniversary (стр. 92-93): 10 знаний — академические и прикладные области.
        public static readonly IReadOnlyList<string> Knowledges = new[]
        {
            "Гуманитарные науки",
            "Естественные науки",
            "Информатика",
            "Медицина",
            "Оккультизм",
            "Политика",
            "Расследование",
            "Финансы",
            "Электроника",
            "Юриспруденция"
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

        // ─── Кланы VtM V20 ────────────────────────────────────────────────

        /// <summary>
        /// 13 канонических кланов VtM V20 + Каитиф (без клановых дисциплин).
        /// Источник: V20 PDF стр. 39-87 (PDF-дамп от 2026-09-03).
        /// </summary>
        public static readonly IReadOnlyList<string> Clans = new[]
        {
            "Ассамит",
            "Бруха",
            "Вентру",
            "Гангрел",
            "Джованни",
            "Каитиф",
            "Ласомбра",
            "Малкавиан",
            "Носферату",
            "Последователь Сета",
            "Равнос",
            "Тореадор",
            "Тремер",
            "Цимисхи"
        };

        /// <summary>Является ли имя валидным кланом.</summary>
        public static bool IsValidClan(string clanName)
            => !string.IsNullOrEmpty(clanName) && Clans.Contains(clanName);

        // ─── Архетипы (V20 стр. 92) ────────────────────────────────────────

        /// <summary>
        /// 10 архетипов VtM V20 (стр. 92). Используются как триггер для восстановления Воли
        /// (V20 стр. 280+, подробности — на стр. 92 в описании каждого архетипа).
        /// </summary>
        /// <remarks>
        /// В V20 «архетип» — это пара Натура + Маска (Nature / Demeanor). В нашем листе
        /// хранится одно поле <see cref="VampireCharacter.Archetype"/>, соответствующее Натуре.
        /// Маска — отдельное поле <see cref="VampireCharacter.Demeanor"/>.
        /// </remarks>
        public static readonly IReadOnlyList<string> Archetypes = new[]
        {
            "Автократ",         // добившись власти
            "Бонвиван",         // «отрываясь по полной»
            "Борец",            // победив в честном поединке
            "Конформист",       // следуя правилам
            "Консерватор",      // привычно действуя по укладу
            "Преступник",       // пойдя на преступление ради выгоды
            "Мудрец",           // узнав новое
            "Калека",           // выжив в трудной ситуации
            "Реформатор",       // изменив систему
            "Традиционалист",   // придерживаясь традиций
        };

        /// <summary>Является ли имя валидным архетипом.</summary>
        public static bool IsValidArchetype(string name)
            => !string.IsNullOrEmpty(name) && Archetypes.Contains(name);

        /// <summary>
        /// Короткое описание условия восстановления Воли для архетипа.
        /// Используется в подсказках UI и логах.
        /// </summary>
        public static string GetArchetypeRestoreCondition(string name) => name switch
        {
            "Автократ"         => "добившись власти",
            "Бонвиван"         => "«отрываясь по полной»",
            "Борец"            => "победив в честном поединке",
            "Конформист"       => "следуя правилам",
            "Консерватор"      => "привычно действуя по укладу",
            "Преступник"       => "пойдя на преступление ради выгоды",
            "Мудрец"           => "узнав новое",
            "Калека"           => "выжив в трудной ситуации",
            "Реформатор"       => "изменив систему",
            "Традиционалист"   => "придерживаясь традиций",
            _ => "",
        };

        /// <summary>
        /// Клановые дисциплины по VtM V20 (книга, стр. 50-87).
        /// Возвращает массив из трёх дисциплин (пустой для Каитифа).
        /// </summary>
        public static IReadOnlyList<string> GetClanDisciplines(string clanName)
        {
            if (string.IsNullOrEmpty(clanName)) return System.Array.Empty<string>();
            return clanName switch
            {
                "Ассамит"           => new[] { "Стремительность", "Сокрытие", "Упокоение" },
                "Бруха"             => new[] { "Стремительность", "Мощь", "Величие" },
                "Вентру"            => new[] { "Доминирование", "Стойкость", "Величие" },
                "Гангрел"           => new[] { "Анимализм", "Стойкость", "Метаморфозы" },
                "Джованни"          => new[] { "Доминирование", "Некромантия", "Мощь" },
                "Каитиф"            => System.Array.Empty<string>(), // любые с одобрения рассказчика
                "Ласомбра"          => new[] { "Доминирование", "Затемнение", "Мощь" },
                "Малкавиан"         => new[] { "Ясновидение", "Помешательство", "Сокрытие" },
                "Носферату"         => new[] { "Анимализм", "Сокрытие", "Мощь" },
                "Последователь Сета" => new[] { "Сокрытие", "Величие", "Серпентис" },
                "Равнос"            => new[] { "Анимализм", "Фантасмагория", "Стойкость" },
                "Тореадор"          => new[] { "Ясновидение", "Стремительность", "Величие" },
                "Тремер"            => new[] { "Ясновидение", "Доминирование", "Тауматургия" },
                "Цимисхи"           => new[] { "Анимализм", "Ясновидение", "Преображение" },
                _ => System.Array.Empty<string>()
            };
        }

        /// <summary>
        /// Стандартные фоны (Backgrounds) VtM V20 (стр. 119-125, 13 шт.).
        /// Используются в Шаге 4.2 визарда (5 пунктов суммарно по рангам 1..5).
        /// </summary>
        public static readonly IReadOnlyList<string> Backgrounds = new[]
        {
            "Поколение",
            "Спутники",
            "Связи",
            "Влияние",
            "Союзники",
            "Наставник",
            "Ресурсы",
            "Стадо",
            "Наследие",
            "Слухи",
            "Оккультное",
            "Известность",
            "Секта",
        };

        /// <summary>
        /// Является ли переданное имя стандартным фоном VtM V20.
        /// </summary>
        public static bool IsValidBackground(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var b in Backgrounds)
                if (string.Equals(b, name, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// Полный список дисциплин VtM V20 (все клановые, без Каитифа).
        /// Используется для автокомплита в /vampire_roll.
        /// </summary>
        public static readonly IReadOnlyList<string> AllDisciplines = new[]
        {
            "Анимализм",
            "Величие",
            "Доминирование",
            "Затемнение",
            "Метаморфозы",
            "Мощь",
            "Некромантия",
            "Помешательство",
            "Преображение",
            "Серпентис",
            "Сокрытие",
            "Стойкость",
            "Стремительность",
            "Тауматургия",
            "Упокоение",
            "Фантасмагория",
            "Ясновидение",
        };

        /// <summary>
        /// Краткое описание изъяна (для выжимки в embed'е листа персонажа).
        /// VtM V20 стр. 50-87.
        /// </summary>
        public static string GetClanFlawShort(string clanName)
        {
            if (string.IsNullOrEmpty(clanName)) return "";
            return clanName switch
            {
                "Ассамит"           => "Из-за проклятия Тремер каждый выпитый пункт крови другого Сородича — неотвратимое тяжёлое повреждение.",
                "Бруха"             => "В приступах ярости (ВНЕШНЯЯ ЯРОСТЬ) сложность самоконтроля +2 (до 10), тратить волю для предотвращения нельзя.",
                "Вентру"            => "Утончённый вкус: питается кровью только одной категории смертных (выбирается при создании, изменить нельзя).",
                "Гангрел"           => "При каждом приступе ярости получает временный звериный признак (атавизм).",
                "Джованни"          => "Поцелуи причиняют смертным мучительную боль: при питье крови смертного — удвоенный урон.",
                "Каитиф"            => "Нет кланового изъяна, но нет и клановых дисциплин.",
                "Ласомбра"          => "Не отражаются ни в каких полированных поверхностях и зеркалах.",
                "Малкавиан"         => "Перманентное психическое расстройство, которое нельзя исцелить (можно временно нейтрализовать волей).",
                "Носферату"         => "Привлекательность всегда 0 (зачеркнуть в листе); проверки с привлекательностью крайне сложны.",
                "Последователь Сета" => "Солнце наносит на 2 повреждения больше; яркий свет уменьшает пул проверок на 1d10.",
                "Равнос"            => "Рабство пороку: при возможности поддаться выбранному пороку — проверка самоконтроля/инстинктов (сложность 6).",
                "Тореадор"          => "При виде прекрасного — проверка самоконтроля/инстинктов (сложность 6); неудача = застывание до конца сцены.",
                "Тремер"            => "Зависимость от крови сильнее: узы крови 2-й ступени с первого глотка, 3-й — со второго.",
                "Цимисхи"           => "Привязка к родной земле: без двух пригоршней почвы из места Становления — удвоение штрафа здоровья каждый день.",
                _ => ""
            };
        }

        /// <summary>
        /// Полное описание изъяна (для блока «Клан» под листом персонажа)
        /// вынесено в <see cref="VampireClanFlawCatalog"/>. VtM V20 стр. 50-87.
        /// </summary>
    }
}