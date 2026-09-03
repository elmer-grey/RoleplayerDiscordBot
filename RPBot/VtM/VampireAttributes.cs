namespace RPBot.VtM;

/// <summary>
/// 9 характеристик персонажа VtM V20 (книга, стр. 91).
/// Базовое значение каждой — 1 (исключение: Привлекательность у Носферату и Самеди = 0).
/// </summary>
public sealed class VampireAttributes
{
    /// <summary>Физические.</summary>
    public int Strength { get; set; } = 1;
    public int Dexterity { get; set; } = 1;
    public int Stamina { get; set; } = 1;

    /// <summary>Социальные.</summary>
    public int Charisma { get; set; } = 1;
    public int Manipulation { get; set; } = 1;
    public int Appearance { get; set; } = 1;

    /// <summary>Ментальные.</summary>
    public int Perception { get; set; } = 1;
    public int Intelligence { get; set; } = 1;
    public int Wits { get; set; } = 1;
}
