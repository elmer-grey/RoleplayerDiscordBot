using RPBot.VtM;
using Xunit;

namespace SmokeTests;

public class VampireCharacterTests
{
    [Fact]
    public void SumAttributes_AddsExistingValues()
    {
        var c = new VampireCharacter
        {
            PlayerName = "tester",
            CharacterName = "Test",
            Attributes = new()
            {
                ["Сила"] = 3,
                ["Ловкость"] = 2,
                ["Драка"] = 2,
            }
        };
        Assert.Equal(5, c.SumAttributes("Сила", "Ловкость"));
        Assert.Equal(2, c.SumAttributes("Драка"));
    }

    [Fact]
    public void SumAttributes_IgnoresMissing()
    {
        var c = new VampireCharacter();
        Assert.Equal(0, c.SumAttributes("Сила"));
        Assert.Equal(0, c.SumAttributes());
    }

    [Fact]
    public void DisplayDots_Characteristic_HasPlusOne()
    {
        var c = new VampireCharacter { Attributes = new() { ["Сила"] = 3 } };
        // Характеристика: 3+1 = 4
        Assert.Equal(4, c.DisplayDots("Сила", isCharacteristic: true));
    }

    [Fact]
    public void DisplayDots_Attribute_NoBonus()
    {
        var c = new VampireCharacter { Attributes = new() { ["Драка"] = 3 } };
        // Атрибут: 3 без бонуса
        Assert.Equal(3, c.DisplayDots("Драка", isCharacteristic: false));
    }

    [Fact]
    public void DisplayDots_CappedAt5()
    {
        var c = new VampireCharacter { Attributes = new() { ["Сила"] = 5 } };
        // 5+1=6 → cap at 5
        Assert.Equal(5, c.DisplayDots("Сила", isCharacteristic: true));
    }

    [Fact]
    public void DisplayDots_MissingReturnsZero()
    {
        var c = new VampireCharacter();
        Assert.Equal(0, c.DisplayDots("Сила", isCharacteristic: true));
    }

    [Fact]
    public void Json_Roundtrip_PreservesFields()
    {
        var c = new VampireCharacter
        {
            PlayerName = "domen_",
            CharacterName = "Александр",
            Hunger = 3,
            Attributes = new() { ["Сила"] = 3, ["Драка"] = 2 },
            Abilities = new() { "Стремительность", "Запугивание" },
            Backgrounds = new() { "Стая" },
            Virtues = new() { ["Совесть"] = 3 }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(c);
        var restored = System.Text.Json.JsonSerializer.Deserialize<VampireCharacter>(json);

        Assert.NotNull(restored);
        Assert.Equal("domen_", restored!.PlayerName);
        Assert.Equal("Александр", restored.CharacterName);
        Assert.Equal(3, restored.Hunger);
        Assert.Equal(3, restored.Attributes["Сила"]);
        Assert.Equal(2, restored.Attributes["Драка"]);
        Assert.Equal(2, restored.Abilities.Count);
        Assert.Single(restored.Backgrounds);
        Assert.Equal(3, restored.Virtues["Совесть"]);
    }
}