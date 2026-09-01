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
}