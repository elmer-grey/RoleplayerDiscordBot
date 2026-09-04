using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM;

/// <summary>
/// Категория способностей (Шаг 3 визарда).
/// </summary>
/// <remarks>
/// <para>VtM V20 (стр. 81, 89): способности разделены на три категории —
/// таланты, навыки и знания. В каждой категории по 10 конкретных
/// способностей (всего 30).</para>
/// <para>Распределение очков между категориями: 13/9/5 (по аналогии с
/// характеристиками 7/5/3).</para>
/// </remarks>
public enum VampireAbilityGroup
{
    /// <summary>Таланты — интуитивные способности (10 шт.).</summary>
    Talents = 0,
    /// <summary>Навыки — приобретённые тренировкой (10 шт.).</summary>
    Skills = 1,
    /// <summary>Знания — теоретические, из книг/учёбы (10 шт.).</summary>
    Knowledges = 2,
}

/// <summary>
/// Приоритет группы способностей на Шаге 3.
/// </summary>
/// <remarks>
/// <para>VtM V20 (стр. 89): внутри первичной группы нужно распределить
/// 13 пунктов, внутри вторичной — 9, внутри третичной — 5.</para>
/// <para>В книге только 6 стандартных перестановок 13/9/5.</para>
/// </remarks>
public enum VampireAbilityPriority
{
    /// <summary>Таланты=13, Навыки=9, Знания=5.</summary>
    TalentsPrimary = 0,
    /// <summary>Таланты=13, Знания=9, Навыки=5.</summary>
    TalentsSecondary = 1,
    /// <summary>Навыки=13, Таланты=9, Знания=5.</summary>
    SkillsPrimary = 2,
    /// <summary>Навыки=13, Знания=9, Таланты=5.</summary>
    SkillsSecondary = 3,
    /// <summary>Знания=13, Таланты=9, Навыки=5.</summary>
    KnowledgesPrimary = 4,
    /// <summary>Знания=13, Навыки=9, Таланты=5.</summary>
    KnowledgesSecondary = 5,
}

public static class VampireAbilityPriorityExtensions
{
    public static readonly IReadOnlyList<VampireAbilityPriority> All = new[]
    {
        VampireAbilityPriority.TalentsPrimary,
        VampireAbilityPriority.TalentsSecondary,
        VampireAbilityPriority.SkillsPrimary,
        VampireAbilityPriority.SkillsSecondary,
        VampireAbilityPriority.KnowledgesPrimary,
        VampireAbilityPriority.KnowledgesSecondary,
    };

    public static int PointsFor(this VampireAbilityPriority priority, VampireAbilityGroup group)
    {
        return priority switch
        {
            VampireAbilityPriority.TalentsPrimary => group == VampireAbilityGroup.Talents ? 13
                : group == VampireAbilityGroup.Skills ? 9 : 5,
            VampireAbilityPriority.TalentsSecondary => group == VampireAbilityGroup.Talents ? 13
                : group == VampireAbilityGroup.Skills ? 5 : 9,
            VampireAbilityPriority.SkillsPrimary => group == VampireAbilityGroup.Skills ? 13
                : group == VampireAbilityGroup.Talents ? 9 : 5,
            VampireAbilityPriority.SkillsSecondary => group == VampireAbilityGroup.Skills ? 13
                : group == VampireAbilityGroup.Talents ? 5 : 9,
            VampireAbilityPriority.KnowledgesPrimary => group == VampireAbilityGroup.Knowledges ? 13
                : group == VampireAbilityGroup.Talents ? 9 : 5,
            VampireAbilityPriority.KnowledgesSecondary => group == VampireAbilityGroup.Knowledges ? 13
                : group == VampireAbilityGroup.Skills ? 9 : 5,
            _ => 0,
        };
    }

    public static string HumanName(this VampireAbilityPriority priority)
    {
        return priority switch
        {
            VampireAbilityPriority.TalentsPrimary     => "Таланты (13) / Навыки (9) / Знания (5)",
            VampireAbilityPriority.TalentsSecondary   => "Таланты (13) / Знания (9) / Навыки (5)",
            VampireAbilityPriority.SkillsPrimary      => "Навыки (13) / Таланты (9) / Знания (5)",
            VampireAbilityPriority.SkillsSecondary    => "Навыки (13) / Знания (9) / Таланты (5)",
            VampireAbilityPriority.KnowledgesPrimary  => "Знания (13) / Таланты (9) / Навыки (5)",
            VampireAbilityPriority.KnowledgesSecondary => "Знания (13) / Навыки (9) / Таланты (5)",
            _ => priority.ToString(),
        };
    }
}

/// <summary>
/// Справочник 30 канонических способностей V20 (по 10 в каждой категории),
/// плюс по 3 примера специализаций для каждой способности.
/// </summary>
public static class VampireAbilitiesCatalog
{
    /// <summary>Таланты (10 шт., стр. 81 V20).</summary>
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
        "Эмпатия",
    };

    /// <summary>Навыки (10 шт., стр. 81 V20).</summary>
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
        "Этикет",
    };

    /// <summary>Знания (10 шт., стр. 81 V20).</summary>
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
        "Юриспруденция",
    };

    /// <summary>
    /// 3 примера специализации для каждой способности (V20, стр. 101, 207 и т.д.).
    /// Это **подсказки**: пользователь может выбрать одну из них или вписать свою.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> SpecializationSuggestions =
        new Dictionary<string, IReadOnlyList<string>>
        {
            // ── Таланты ───────────────────────────────────────────────────
            ["Атлетика"]            = new[] { "Бег", "Прыжки", "Плавание" },
            ["Бдительность"]        = new[] { "Визуальный контакт", "Слух", "Обоняние" },
            ["Драка"]               = new[] { "Кулачный бой", "Борьба", "Грязные приёмы" },
            ["Запугивание"]         = new[] { "Словесное", "Физическое", "Молчаливое присутствие" },
            ["Красноречие"]         = new[] { "Убеждение", "Ораторство", "Переговоры" },
            ["Лидерство"]           = new[] { "Вдохновение", "Тактика", "Прямое командование" },
            ["Уличное чутьё"]       = new[] { "Городские джунгли", "Криминальный мир", "Уличные банды" },
            ["Хитрость"]            = new[] { "Обман", "Отвлечение", "Манипуляция фактами" },
            ["Шестое чувство"]      = new[] { "Предчувствие опасности", "Чтение намерений", "Предвидение" },
            ["Эмпатия"]             = new[] { "Чтение эмоций", "Сочувствие", "Понимание мотивации" },

            // ── Навыки ────────────────────────────────────────────────────
            ["Вождение"]            = new[] { "Легковой автомобиль", "Грузовик", "Мотоцикл" },
            ["Воровство"]           = new[] { "Карманная кража", "Взлом замков", "Проникновение" },
            ["Выживание"]           = new[] { "Дикая природа", "Городские руины", "Пустыня" },
            ["Исполнение"]          = new[] { "Актёрская игра", "Музыка", "Танец" },
            ["Обращение с животными"] = new[] { "Домашние животные", "Хищники", "Птицы" },
            ["Ремесло"]             = new[] { "Столярное дело", "Кузнечное дело", "Ювелирное дело" },
            ["Скрытность"]          = new[] { "Бесшумное движение", "Маскировка", "Уход от слежки" },
            ["Стрельба"]            = new[] { "Пистолет", "Винтовка", "Дробовик" },
            ["Фехтование"]          = new[] { "Рапира", "Сабля", "Катана" },
            ["Этикет"]              = new[] { "Высший свет", "Криминальный мир", "Дипломатический корпус" },

            // ── Знания ────────────────────────────────────────────────────
            ["Гуманитарные науки"]  = new[] { "История", "Философия", "Лингвистика" },
            ["Естественные науки"]  = new[] { "Физика", "Биология", "Химия" },
            ["Информатика"]         = new[] { "Программирование", "Сетевая безопасность", "Базы данных" },
            ["Медицина"]            = new[] { "Телесные повреждения", "Токсикология", "Хирургия" },
            ["Оккультизм"]          = new[] { "Тёмные ритуалы", "Демонология", "Магия крови" },
            ["Политика"]            = new[] { "Городская политика", "Международные отношения", "Камарилья" },
            ["Расследование"]       = new[] { "Допросы", "Анализ улик", "Криминалистика" },
            ["Финансы"]             = new[] { "Бухгалтерия", "Торговля акциями", "Отмывание денег" },
            ["Электроника"]         = new[] { "Бытовая техника", "Системы связи", "Электронные замки" },
            ["Юриспруденция"]       = new[] { "Уголовное право", "Договорное право", "Международное право" },
        };

    public static IReadOnlyList<string> NamesInGroup(VampireAbilityGroup group) => group switch
    {
        VampireAbilityGroup.Talents => Talents,
        VampireAbilityGroup.Skills => Skills,
        VampireAbilityGroup.Knowledges => Knowledges,
        _ => new List<string>(),
    };

    public static VampireAbilityGroup? FindGroup(string abilityName)
    {
        if (Talents.Contains(abilityName)) return VampireAbilityGroup.Talents;
        if (Skills.Contains(abilityName)) return VampireAbilityGroup.Skills;
        if (Knowledges.Contains(abilityName)) return VampireAbilityGroup.Knowledges;
        return null;
    }

    /// <summary>
    /// Возвращает 3 примера специализаций для данной способности.
    /// Если способность не найдена — пустой массив.
    /// </summary>
    public static IReadOnlyList<string> GetSpecializationSuggestions(string abilityName)
    {
        return SpecializationSuggestions.TryGetValue(abilityName, out var list)
            ? list
            : System.Array.Empty<string>();
    }
}
