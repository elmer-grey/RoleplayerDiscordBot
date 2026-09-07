using System.Text.Json.Serialization;

namespace RPBot.VtM;

/// <summary>
/// VtM-персонаж для хранения и последующих бросков.
/// </summary>
/// <remarks>
/// Сериализуется в <c>Data/vtm/characters_{guildId}.json</c> через
/// <see cref="VampireStorage"/>. JSON-формат — с camelCase.
/// </remarks>
public sealed class VampireCharacter
{
    /// <summary>
    /// Стабильный UUID персонажа. Генерируется при первом сохранении и больше не меняется.
    /// Используется как ключ в <see cref="VampireDisplayIndex"/> и
    /// <see cref="VampireDmBlockIndex"/> для глобальной синхронизации сообщений.
    /// </summary>
    /// <remarks>
    /// <para>Для уже существующих персонажей UUID проставляется лениво при
    /// следующей загрузке (<see cref="VampireStorage.RebuildCharacterIds"/>) —
    /// каждая запись при чтении проверяется и при отсутствии Id получает
    /// сгенерированный, а файл сохраняется.</para>
    /// <para>Игрок в одной гильдии — один персонаж (по дизайну на сейчас);
    /// UUID не привязан к игроку, поэтому если когда-нибудь модель поменяется,
    /// старые индексы продолжат работать.</para>
    /// </remarks>
    [JsonPropertyName("characterId")]
    public Guid CharacterId { get; set; } = Guid.Empty;

    /// <summary>Имя игрока (Discord username, не ID).</summary>
    [JsonPropertyName("playerName")]
    public string PlayerName { get; set; } = "";

        /// <summary>
        /// Discord user ID игрока (ulong). Нужен для отправки листа в DM по команде <c>/vampire_show</c>.
        /// </summary>
        /// <remarks>
        /// Опциональное поле — у старых персонажей 0. Заполняется при создании/обновлении.
        /// Используется <see cref="VampireStorage"/> как вторичный индекс наряду с <see cref="PlayerName"/>.
        /// </remarks>
        [JsonPropertyName("playerId")]
        public ulong PlayerId { get; set; }

    /// <summary>Имя персонажа.</summary>
    [JsonPropertyName("characterName")]
    public string CharacterName { get; set; } = "";

    // ─── Шапка листа V20 (стр. 92) ───────────────────────────────────

    /// <summary>
    /// Натура (Archetype / Nature) — глубинная суть персонажа. Свободный текст.
    /// Примеры из V20: «Герой», «Судья», «Отступник», «Чудовище».
    /// </summary>
    [JsonPropertyName("nature")]
    public string Nature { get; set; } = "";

    /// <summary>
    /// Маска / Амплуа (Demeanor) — социальная роль, которую персонаж играет.
    /// Примеры: «Конформист», «Традиционалист», «Ребел», «Бунтарь».
    /// </summary>
    [JsonPropertyName("demeanor")]
    public string Demeanor { get; set; } = "";

    /// <summary>
    /// Концепт / Амплуа (Concept) — краткое описание роли персонажа в хронике.
    /// Пример: «Полицейский под прикрытием, ставший вампиром», «Искательница запретных знаний».
    /// </summary>
    [JsonPropertyName("concept")]
    public string Concept { get; set; } = "";

    /// <summary>
    /// Хроника, в которой участвует персонаж (название сюжета).
    /// </summary>
    [JsonPropertyName("chronicle")]
    public string Chronicle { get; set; } = "";

    /// <summary>
    /// Клан персонажа (название из <see cref="VampireParameterCatalog.Clans"/>).
    /// </summary>
    [JsonPropertyName("clan")]
    public string Clan { get; set; } = "";

    /// <summary>
        /// Архетип / Натура (V20 стр. 92). Один из 10: Автократ, Бонвиван, Борец,
        /// Конформист, Консерватор, Преступник, Мудрец, Калека, Реформатор, Традиционалист.
        /// Используется как триггер для восстановления 1 пункта воли (V20 стр. 280+).
        /// </summary>
        /// <remarks>
        /// Рассказчик сам решает, выполнено ли условие архетипа. Без программного
        /// лимита на частоту восстановления — лимит слишком легко ввести по ошибке
        /// (часовой пояс, долгие/короткие сцены).
        /// </remarks>
        [JsonPropertyName("archetype")]
        public string Archetype { get; set; } = "";

        /// <summary>
        /// Поколение (1..13+). По умолчанию 13 (новорождённый).
        /// 13 = новообращённый, 8 и ниже — очень древние вампиры.
        /// </summary>
        [JsonPropertyName("generation")]
        public int Generation { get; set; } = 13;

    /// <summary>
    /// Сир (имя вампира, обратившего этого персонажа). Свободный текст.
    /// </summary>
    [JsonPropertyName("sire")]
    public string Sire { get; set; } = "";

    // ─── V20 особые поля ─────────────────────────────────────────────

    /// <summary>
    /// Клановый изъян (Clan Flaw, V20 стр. 60-88). Один на клан.
    /// Примеры: «утончённый вкус» (Вентру), «приступы ярости» (Гангрел),
    /// «безумие» (Малкавиан), «уродство» (Носферату), «одержимость прекрасным» (Тореадор).
    /// </summary>
    [JsonPropertyName("weakness")]
    public string Weakness { get; set; } = "";

    /// <summary>
    /// Запас крови (Blood Pool) — максимум равен Стойкости (обычно 4..10).
    /// По умолчанию 10 (стандартное для вампира со Стойкостью 3-4).
    /// </summary>
    [Obsolete("Не используется в листе после Шага 6: Кровь убрана, трекинг ведётся через Hunger. " +
              "Оставлено для обратной совместимости с сохранёнными персонажами.")]
    [JsonPropertyName("bloodPool")]
    public int BloodPool { get; set; } = 10;

    /// <summary>
    /// Специализации: способность → узкая сфера применения.
    /// V20 стр. 101: рекомендуется при значении ≥ 4, но допускается и ниже.
    /// </summary>
    /// <remarks>
    /// Одна специализация на способность. Пример:
    /// { "Ремесло": "кузнечное дело", "Атлетика": "плавание" }.
    /// </remarks>
    [JsonPropertyName("specializations")]
    public Dictionary<string, string> Specializations { get; set; } = new();

    /// <summary>
    /// Достоинства (Merits) — название → цена (1..7).
    /// V20 стр. 485+: покупаются за опыт на этапе создания или позже.
    /// </summary>
    [JsonPropertyName("merits")]
    public Dictionary<string, int> Merits { get; set; } = new();

    /// <summary>
    /// Недостатки (Flaws) — название → цена (1..7).
    /// V20 стр. 523+: дают бонусные очки опыта при создании персонажа.
    /// </summary>
    [JsonPropertyName("flaws")]
    public Dictionary<string, int> Flaws { get; set; } = new();

    /// <summary>
    /// Характеристики и атрибуты по русским названиям из <see cref="VampireParameterCatalog"/>.
    /// Ключ — название, значение — количество точек (0..5).
    /// </summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, int> Attributes { get; set; } = new();

        /// <summary>
        /// Структурированные 9 характеристик VtM V20 (Сила, Ловкость, …, Смекалка).
        /// Используется визардом создания персонажа (Шаг 2 «Характеристики 7/5/3»).
        /// </summary>
        [JsonPropertyName("attributesStruct")]
        public VampireAttributes AttributesStruct { get; set; } = new();

        /// <summary>
        /// Приоритет групп характеристик (Шаг 2): одна из 6 стандартных перестановок 7/5/3.
        /// Хранится строкой имени enum (<see cref="VampireAttributePriority"/>).
        /// </summary>
        [JsonPropertyName("attributesPriority")]
        public string AttributesPriority { get; set; } = "";

                /// <summary>
                /// Структурированные 30 способностей VtM V20 (Таланты/Навыки/Знания).
                /// Используется визардом создания персонажа (Шаг 3 «Способности 13/9/5»).
                /// </summary>
                [JsonPropertyName("abilitiesStruct")]
                public VampireAbilities AbilitiesStruct { get; set; } = new();

                /// <summary>
                /// Приоритет групп способностей (Шаг 3): одна из 6 стандартных перестановок 13/9/5.
                /// Хранится строкой имени enum (<see cref="VampireAbilityPriority"/>).
                /// </summary>
                [JsonPropertyName("abilitiesPriority")]
                public string AbilitiesPriority { get; set; } = "";

        /// <summary>Способности (Disciplines, Способности).</summary>
        [JsonPropertyName("abilities")]
        public List<string> Abilities { get; set; } = new();

    /// <summary>
        /// Факты биографии: имя → ранг (1..5).
        /// </summary>
        /// <remarks>
        /// <para>V20 стр. 99: 5 пунктов на факты биографии. Ранг не может
        /// превышать 5. Пусто = факты не выбраны.</para>
        /// <para>Сериализуется как объект JSON (раньше был список строк,
        /// миграция делается автоматически при чтении устаревших файлов через
        /// <see cref="VampireBackgroundsJsonConverter"/>).</para>
        /// </remarks>
        [JsonPropertyName("backgrounds")]
        [JsonConverter(typeof(VampireBackgroundsJsonConverter))]
        public Dictionary<string, int> Backgrounds { get; set; } = new();

        /// <summary>
        /// Факты, добавленные через freebie (Шаг 5).
        /// Хранится отдельно, чтобы <see cref="VampireAdvantagesResolver.IsBackgroundsComplete"/>
        /// считал только Шаг 4.2 пул (5/5), а не сумму с freebie.
        /// В Embed'е факты рисуются как сумма рангов двух словарей.
        /// </summary>
        [JsonPropertyName("backgrounds_freebie")]
        public Dictionary<string, int> FreebieBackgrounds { get; set; } = new();

    /// <summary>Добродетели (Совесть, Самоконтроль, Смелость). База 1/1/1 проставляется в конструкторе.</summary>
    [JsonPropertyName("virtues")]
    public Dictionary<string, int> Virtues { get; set; } = new()
    {
        [VampireParameterCatalog.VirtueConscience] = VampireAdvantagesCatalog.VirtueBaseConscience,
        [VampireParameterCatalog.VirtueSelfControl] = VampireAdvantagesCatalog.VirtueBaseSelfControl,
        [VampireParameterCatalog.VirtueCourage] = VampireAdvantagesCatalog.VirtueBaseCourage,
    };

    /// <summary>Текущий Голод (1..5). По умолчанию 1.</summary>
    [JsonPropertyName("hunger")]
    public int Hunger { get; set; } = 1;

        /// <summary>
        /// Атрибут «Воля» (1..10). Базовое значение обсуждается отдельно.
        /// </summary>
        [Obsolete("Не используется в листе после Шага 6: значение берётся из VampireFinishingResolver.ComputeWillpower. " +
                  "Оставлено для обратной совместимости с сохранёнными персонажами.")]
        [JsonPropertyName("willpower")]
        public int Willpower { get; set; } = 5;

        /// <summary>
        /// Текущий запас пунктов воли (0..<see cref="Willpower"/>).
        /// Стартовое значение = <see cref="Willpower"/>. Восстанавливается по завершении истории.
        /// </summary>
        [Obsolete("Не используется в листе после Шага 6: значение берётся из VampireFinishingResolver.ComputeWillpower. " +
                  "Оставлено для обратной совместимости с сохранёнными персонажами.")]
        [JsonPropertyName("willpowerPoints")]
        public int WillpowerPoints { get; set; } = 5;

        /// <summary>
        /// Флаг «уже тратил пункт воли в этом ходу». Сбрасывается в начале каждого хода рассказчиком.
        /// </summary>
        [JsonPropertyName("willpowerSpentThisTurn")]
        public bool WillpowerSpentThisTurn { get; set; }

        /// <summary>
        /// Свободное описание персонажа (биография, концепт, клан, поколение и т. п.).
        /// Показывается в embed'е листа персонажа и в личных сообщениях игрока.
        /// </summary>
        /// <remarks>
        /// Опционально. Поддерживает обычный текст; переносы строк сохраняются Discord'ом.
        /// Не сериализуется, если пустое — для обратной совместимости со старыми персонажами.
        /// </remarks>
        [JsonPropertyName("bio")]
        public string Bio { get; set; } = "";

        /// <summary>
        /// URL картинки-аватара персонажа (Discord attachment URL или иной https-адрес).
        /// Используется как <c>thumbnail</c> в embed'е листа.
        /// </summary>
        /// <remarks>
        /// Опционально. Если пусто или невалидно — thumbnail не задаётся.
        /// Discord ограничивает размер thumbnail до 5 МБ и допускает только http/https.
        /// </remarks>
        [JsonPropertyName("avatarUrl")]
        public string AvatarUrl { get; set; } = "";

        /// <summary>
        /// Человечность / Путь совести (1..10). По умолчанию 7.
        /// </summary>
        /// <remarks>
        /// Показывается в embed'е листа персонажа.
        /// </remarks>
        [Obsolete("Не используется в листе после Шага 6: значение берётся из VampireFinishingResolver.ComputeHumanity. " +
                  "Оставлено для обратной совместимости с сохранёнными персонажами.")]
        [JsonPropertyName("humanity")]
        public int Humanity { get; set; } = 7;

        /// <summary>
        /// Шкала здоровья персонажа. Может быть null, если персонаж ещё не создан / не загружен.
        /// </summary>
        [JsonPropertyName("health")]
        public HealthState? Health { get; set; }

        /// <summary>
        /// Дисциплины: имя → уровень (1..5). Опционально.
        /// </summary>
        [JsonPropertyName("disciplines")]
        public Dictionary<string, int> Disciplines { get; set; } = new();

        /// <summary>
        /// Дисциплины, добавленные через freebie (Шаг 5).
        /// Хранится отдельно, чтобы <see cref="VampireAdvantagesResolver.IsDisciplinesComplete"/>
        /// считал только Шаг 4.1 пул (3/3), а не сумму с freebie.
        /// В Embed'е дисциплины рисуются как сумма двух словарей.
        /// </summary>
        [JsonPropertyName("disciplines_freebie")]
        public Dictionary<string, int> FreebieDisciplines { get; set; } = new();

        /// <summary>
        /// Текущий опыт (необязательно). Показывается в embed'е листа.
        /// </summary>
        [JsonPropertyName("experienceCurrent")]
        public int ExperienceCurrent { get; set; }

        /// <summary>
        /// Всего получено опыта за всю историю персонажа (необязательно).
        /// </summary>
        [JsonPropertyName("experienceTotal")]
        public int ExperienceTotal { get; set; }

                /// <summary>
                /// Сколько раз тратился свободный пункт на конкретную клетку Шага 5.
                /// Ключ — "{TargetNum}:{Field}", значение — число применённых +N.
                /// Записывается, чтобы можно было откатить или сбросить все траты.
                /// </summary>
                [JsonPropertyName("freebieSpent")]
                public Dictionary<string, int> FreebieSpent { get; set; } = new();

                /// <summary>
                /// Бонус Человечности сверх формулы (Совесть + Самоконтроль), потраченный
                /// свободными пунктами на Шаге 5. Кэп — 10 за вычетом формулы.
                /// </summary>
                [JsonPropertyName("humanityBonus")]
                public int HumanityBonus { get; set; } = 0;

                /// <summary>
                /// Бонус Воли сверх формулы (Смелость), потраченный свободными пунктами
                /// на Шаге 5. Кэп — 10 за вычетом формулы.
                /// </summary>
                [JsonPropertyName("willpowerBonus")]
                public int WillpowerBonus { get; set; } = 0;

                /// <summary>
                /// Шаг 5 подтверждён пользователем: freebie не добиты до нуля,
                /// но игрок явно согласился заморозить значения листа и перейти
                /// к специализациям. После этого траты freebie больше нельзя менять.
                /// </summary>
                [JsonPropertyName("step5Finalized")]
                public bool Step5Finalized { get; set; }

                /// <summary>
                /// Когда был подтверждён Шаг 5 (UTC). null, если ещё не подтверждён.
                /// </summary>
                [JsonPropertyName("step5FinalizedAt")]
                public DateTime? Step5FinalizedAt { get; set; }

                // ─── Мораль (V20 стр. 92, 313+, 333) ───────────────────────────────

                /// <summary>
                /// Путь Просветления (Path of Enlightenment). Пусто, если персонаж следует
                /// обычной Человечности. Иначе — название из внутреннего списка (V20 стр. 271-307):
                /// «Путь Каина», «Путь Сердца и Духа», «Путь Мудрости», «Путь Сплочённости» и т.д.
                /// </summary>
                /// <remarks>
                /// Если задано — пул проверки совести берётся из <see cref="PathRating"/>,
                /// иначе из текущей Человечности (V20 стр. 269).
                /// </remarks>
                [JsonPropertyName("path")]
                public string Path { get; set; } = "";

                /// <summary>
                /// Значение выбранного Пути (1..5). Если <see cref="Path"/> пусто, поле игнорируется.
                /// </summary>
                [JsonPropertyName("pathRating")]
                public int PathRating { get; set; }

                /// <summary>
                /// Психические расстройства персонажа (V20 стр. 313-317). Имена — из
                /// <see cref="VampireDerangementCatalog"/> или произвольные, заданные рассказчиком.
                /// </summary>
                /// <remarks>
                /// Дубликаты и пустые строки игнорируются. Получает при провале проверки совести (botch)
                /// либо инициируется рассказчиком вручную.
                /// </remarks>
                [JsonPropertyName("derangements")]
                public List<string> Derangements { get; set; } = new();

                /// <summary>
                /// Активные атавизмы (Гангрел, V20 стр. 95). Добавляются при входе в ярость
                /// и становятся постоянными, если рассказчик решит.
                /// </summary>
                /// <remarks>
                /// Хранится на персонаже, чтобы можно было отображать список в листе и стирать
                /// вручную. Сейчас заполняется автоматически в <see cref="VampireFrenzyResolver"/>
                /// при провале проверки самоконтроля для Гангрела.
                /// </remarks>
                [JsonPropertyName("activeAtavisms")]
                public List<string> ActiveAtavisms { get; set; } = new();

    /// <summary>
    /// Сумма значений по всем ключам (для команды "/rollVH параметр1 параметр2 hunger=N").
    /// </summary>
    public int SumAttributes(params string[] names)
    {
        if (names == null || names.Length == 0) return 0;
        int sum = 0;
        foreach (var n in names)
            sum += GetAttributeValue(n);
        return sum;
    }

    /// <summary>
    /// Итоговое значение атрибута для листа/бросков: Шаг 2 + freebie-бонус Шага 5.
    /// Игнорирует базовую +1 характеристики (это уже учтено в <see cref="AttributesStruct"/>).
    /// </summary>
    public int GetAttributeValue(string attributeName)
    {
        int baseValue = AttributesStruct != null
            ? VampireAttributesResolver.GetAttributeValue(AttributesStruct, attributeName)
            : 0;
        int freebieBonus = 0;
        if (Attributes != null && Attributes.TryGetValue(attributeName, out var fb))
            freebieBonus = Math.Max(0, fb);
        return baseValue + freebieBonus;
    }

    /// <summary>
    /// Итоговое значение способности для листа: Шаг 3 + freebie-бонус Шага 5.
    /// </summary>
    public int GetAbilityValue(string abilityName)
    {
        int baseValue = AbilitiesStruct != null
            ? VampireAbilitiesResolver.GetAbilityValue(AbilitiesStruct, abilityName)
            : 0;
        int freebieBonus = 0;
        if (Attributes != null && Attributes.TryGetValue(abilityName, out var fb))
            freebieBonus = Math.Max(0, fb);
        return baseValue + freebieBonus;
    }

    /// <summary>Сколько «закрашенных точек» нужно показать в листе персонажа.</summary>
    /// <remarks>
    /// Характеристики (Физические/Социальные/Ментальные) имеют +1 «изначальную» точку,
    /// атрибуты — нет. Максимум — 5 точек.
    /// Источник значения: <see cref="AttributesStruct"/> (Шаг 2) + freebie-бонус из
    /// <see cref="Attributes"/> dict (Шаг 5). Лист **обязан** читать оба источника, иначе
    /// данные Шага 2 или Шага 5 будут потеряны.
    /// </remarks>
    public int DisplayDots(string attributeName, bool isCharacteristic)
    {
        int baseValue = AttributesStruct != null
            ? VampireAttributesResolver.GetAttributeValue(AttributesStruct, attributeName)
            : 0;
        int freebieBonus = 0;
        if (Attributes != null && Attributes.TryGetValue(attributeName, out var fb))
            freebieBonus = Math.Max(0, fb);
        int total = baseValue + freebieBonus;
        int dots = Math.Min(total, 5);
        return Math.Max(dots, 0);
    }

        /// <summary>
        /// Подтянуть запас пунктов воли к потолку после повышения атрибута «Воля».
        /// Вызывать при изменении <see cref="Willpower"/> (создание персонажа, покупка за опыт).
        /// </summary>
#pragma warning disable CS0618 // Willpower/WillpowerPoints устарели для листа, но используются runtime-кнопкой «Воля».
        public void EnsureWillpowerPointsValid()
        {
            if (Willpower < 0) Willpower = 0;
            if (WillpowerPoints < 0) WillpowerPoints = 0;
            if (WillpowerPoints > Willpower) WillpowerPoints = Willpower;
        }
#pragma warning restore CS0618
}