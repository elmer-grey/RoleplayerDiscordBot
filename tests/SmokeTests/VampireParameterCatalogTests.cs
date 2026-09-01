using RPBot.VtM;
using Xunit;

namespace SmokeTests;

public class VampireParameterCatalogTests
{
    [Fact]
    public void All_Has9Characteristics_And30Attributes()
    {
        // 3+3+3 характеристик + 10+10+10 атрибутов = 39.
        Assert.Equal(9, VampireParameterCatalog.Characteristics.Count);
        Assert.Equal(39, VampireParameterCatalog.All.Count);
    }

    [Fact]
    public void Physical_Contains_StrengthDexStamina()
    {
        Assert.Contains("Сила", VampireParameterCatalog.Physical);
        Assert.Contains("Ловкость", VampireParameterCatalog.Physical);
        Assert.Contains("Выносливость", VampireParameterCatalog.Physical);
    }

    [Fact]
    public void Social_Contains_CharismaManipAppearance()
    {
        Assert.Contains("Обаяние", VampireParameterCatalog.Social);
        Assert.Contains("Манипуляция", VampireParameterCatalog.Social);
        Assert.Contains("Привлекательность", VampireParameterCatalog.Social);
    }

    [Fact]
    public void Mental_Contains_PerceptionIntellWits()
    {
        Assert.Contains("Восприятие", VampireParameterCatalog.Mental);
        Assert.Contains("Интеллект", VampireParameterCatalog.Mental);
        Assert.Contains("Смекалка", VampireParameterCatalog.Mental);
    }

    [Fact]
    public void Talents_HasTen()
    {
        Assert.Equal(10, VampireParameterCatalog.Talents.Count);
    }

    [Fact]
    public void Skills_HasTen()
    {
        Assert.Equal(10, VampireParameterCatalog.Skills.Count);
    }

    [Fact]
    public void Knowledges_HasTen()
    {
        Assert.Equal(10, VampireParameterCatalog.Knowledges.Count);
    }

    [Fact]
    public void IsCharacteristic_TrueForStrength()
    {
        Assert.True(VampireParameterCatalog.IsCharacteristic("Сила"));
        Assert.True(VampireParameterCatalog.IsCharacteristic("Обаяние"));
        Assert.True(VampireParameterCatalog.IsCharacteristic("Восприятие"));
    }

    [Fact]
    public void IsCharacteristic_FalseForBrawl()
    {
        Assert.False(VampireParameterCatalog.IsCharacteristic("Драка"));
        Assert.False(VampireParameterCatalog.IsCharacteristic("Вождение"));
        Assert.False(VampireParameterCatalog.IsCharacteristic("Законы"));
    }

    [Fact]
    public void IsValid_AcceptsAllKnownAttributes()
    {
        Assert.True(VampireParameterCatalog.IsValid("Сила"));
        Assert.True(VampireParameterCatalog.IsValid("Драка"));
        Assert.True(VampireParameterCatalog.IsValid("Законы"));
    }

    [Fact]
    public void IsValid_RejectsUnknown()
    {
        Assert.False(VampireParameterCatalog.IsValid("Несуществующий"));
        Assert.False(VampireParameterCatalog.IsValid(""));
    }

    [Fact]
    public void GetCategory_ReturnsCorrectCategory()
    {
        Assert.Equal("Физические", VampireParameterCatalog.GetCategory("Сила"));
        Assert.Equal("Социальные", VampireParameterCatalog.GetCategory("Обаяние"));
        Assert.Equal("Ментальные", VampireParameterCatalog.GetCategory("Восприятие"));
        Assert.Equal("Таланты", VampireParameterCatalog.GetCategory("Драка"));
        Assert.Equal("Навыки", VampireParameterCatalog.GetCategory("Вождение"));
        Assert.Equal("Знания", VampireParameterCatalog.GetCategory("Законы"));
    }

    [Fact]
    public void GetCategory_UnknownReturnsEmpty()
    {
        Assert.Equal("", VampireParameterCatalog.GetCategory("Несуществующий"));
        Assert.Equal("", VampireParameterCatalog.GetCategory(""));
    }

    [Fact]
    public void Categories_CoversAllParameters()
    {
        int totalViaCategories = 0;
        foreach (var c in VampireParameterCatalog.Categories.Values)
            totalViaCategories += c.Count;
        Assert.Equal(VampireParameterCatalog.All.Count, totalViaCategories);
    }

    [Fact]
    public void NoDuplicateParameterNames()
    {
        var all = VampireParameterCatalog.All;
        Assert.Equal(all.Count, new System.Collections.Generic.HashSet<string>(all, System.StringComparer.Ordinal).Count);
    }

    [Fact]
    public void Virtues_ContainsThreeClassicOnes()
    {
        Assert.Contains("Совесть", VampireParameterCatalog.Virtues);
        Assert.Contains("Самоконтроль", VampireParameterCatalog.Virtues);
        Assert.Contains("Смелость", VampireParameterCatalog.Virtues);
    }

    [Fact]
    public void Clans_HasAtLeast13()
    {
        Assert.True(VampireParameterCatalog.Clans.Count >= 13,
            $"Ожидалось ≥13 кланов, найдено {VampireParameterCatalog.Clans.Count}");
    }

    [Fact]
    public void AllowedValues_AreZeroThroughFive()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, VampireParameterCatalog.AllowedValues);
    }
}