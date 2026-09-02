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

    /// <summary>
    /// Характеристики и атрибуты по русским названиям из <see cref="VampireParameterCatalog"/>.
    /// Ключ — название, значение — количество точек (0..5).
    /// </summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, int> Attributes { get; set; } = new();

    /// <summary>Способности (Disciplines, Способности).</summary>
    [JsonPropertyName("abilities")]
    public List<string> Abilities { get; set; } = new();

    /// <summary>Факты биографии.</summary>
    [JsonPropertyName("backgrounds")]
    public List<string> Backgrounds { get; set; } = new();

    /// <summary>Добродетели (Совесть, Самоконтроль, Смелость).</summary>
    [JsonPropertyName("virtues")]
    public Dictionary<string, int> Virtues { get; set; } = new();

    /// <summary>Текущий Голод (1..5). По умолчанию 1.</summary>
    [JsonPropertyName("hunger")]
    public int Hunger { get; set; } = 1;

        /// <summary>
        /// Атрибут «Воля» (1..10). Базовое значение обсуждается отдельно.
        /// </summary>
        [JsonPropertyName("willpower")]
        public int Willpower { get; set; } = 5;

        /// <summary>
        /// Текущий запас пунктов воли (0..<see cref="Willpower"/>).
        /// Стартовое значение = <see cref="Willpower"/>. Восстанавливается по завершении истории.
        /// </summary>
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
    /// Сумма значений по всем ключам (для команды "/rollVH параметр1 параметр2 hunger=N").
    /// </summary>
    public int SumAttributes(params string[] names)
    {
        if (names == null || names.Length == 0) return 0;
        int sum = 0;
        foreach (var n in names)
        {
            if (Attributes.TryGetValue(n, out var v)) sum += v;
        }
        return sum;
    }

    /// <summary>Сколько «закрашенных точек» нужно показать в листе персонажа.</summary>
    /// <remarks>
    /// Характеристики (Физические/Социальные/Ментальные) имеют +1 «изначальную» точку,
    /// атрибуты — нет. Максимум — 5 точек.
    /// </remarks>
    public int DisplayDots(string attributeName, bool isCharacteristic)
    {
        if (!Attributes.TryGetValue(attributeName, out var v)) return 0;
        int dots = isCharacteristic ? Math.Min(v + 1, 5) : Math.Min(v, 5);
        return Math.Max(dots, 0);
    }

        /// <summary>
        /// Подтянуть запас пунктов воли к потолку после повышения атрибута «Воля».
        /// Вызывать при изменении <see cref="Willpower"/> (создание персонажа, покупка за опыт).
        /// </summary>
        public void EnsureWillpowerPointsValid()
        {
            if (Willpower < 0) Willpower = 0;
            if (WillpowerPoints < 0) WillpowerPoints = 0;
            if (WillpowerPoints > Willpower) WillpowerPoints = Willpower;
        }
}