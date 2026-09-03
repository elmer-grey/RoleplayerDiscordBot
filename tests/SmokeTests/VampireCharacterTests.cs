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

    // ─── V20-поля (стр. 92) — round-trip и обратная совместимость ────────

    [Fact]
    public void Json_Roundtrip_PreservesV20HeaderFields()
    {
        var c = new VampireCharacter
        {
            CharacterName = "Радуля",
            Nature = "Судья",
            Demeanor = "Конформист",
            Concept = "Староста, ставший вампиром поневоле",
            Chronicle = "Киев-1240",
            Clan = "Вентру",
            Generation = 11,
            Sire = "Элеонора фон Штейн",
        };

        var json = System.Text.Json.JsonSerializer.Serialize(c);
        var restored = System.Text.Json.JsonSerializer.Deserialize<VampireCharacter>(json);

        Assert.NotNull(restored);
        Assert.Equal("Судья", restored!.Nature);
        Assert.Equal("Конформист", restored.Demeanor);
        Assert.Equal("Староста, ставший вампиром поневоле", restored.Concept);
        Assert.Equal("Киев-1240", restored.Chronicle);
        Assert.Equal("Вентру", restored.Clan);
        Assert.Equal(11, restored.Generation);
        Assert.Equal("Элеонора фон Штейн", restored.Sire);
    }

    [Fact]
    public void Json_Roundtrip_PreservesWeaknessAndBlood()
    {
        var c = new VampireCharacter
        {
            Clan = "Гангрел",
            Weakness = "Приступы ярости в ближнем бою",
            BloodPool = 12,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(c);
        var restored = System.Text.Json.JsonSerializer.Deserialize<VampireCharacter>(json);
        Assert.NotNull(restored);
        Assert.Equal("Приступы ярости в ближнем бою", restored!.Weakness);
        Assert.Equal(12, restored.BloodPool);
    }

    [Fact]
    public void Json_Roundtrip_PreservesSpecializationsMeritsFlaws()
    {
        var c = new VampireCharacter
        {
            Specializations = new()
            {
                ["Атлетика"] = "плавание",
                ["Ремесло"] = "кузнечное дело",
            },
            Merits = new()
            {
                ["Острые чувства"] = 2,
                ["Связи"] = 3,
            },
            Flaws = new()
            {
                ["Заразный укус"] = 2,
                ["Кровоточащая рана"] = 2,
            },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(c);
        var restored = System.Text.Json.JsonSerializer.Deserialize<VampireCharacter>(json);
        Assert.NotNull(restored);
        Assert.Equal("плавание", restored!.Specializations["Атлетика"]);
        Assert.Equal("кузнечное дело", restored.Specializations["Ремесло"]);
        Assert.Equal(2, restored.Merits["Острые чувства"]);
        Assert.Equal(3, restored.Merits["Связи"]);
        Assert.Equal(2, restored.Flaws["Заразный укус"]);
    }

    [Fact]
    public void Json_Legacy_WithoutV20Fields_DeserializesWithDefaults()
    {
        // JSON без новых полей (старый формат) → все V20-поля = default.
        const string legacy = """
            {
              "playerName": "oldplayer",
              "playerId": 123456789,
              "characterName": "Legacy",
              "attributes": { "Сила": 2 },
              "abilities": [],
              "backgrounds": [],
              "virtues": {},
              "hunger": 1,
              "willpower": 5,
              "willpowerPoints": 5,
              "willpowerSpentThisTurn": false,
              "bio": "",
              "avatarUrl": "",
              "humanity": 7,
              "disciplines": {},
              "experienceCurrent": 0,
              "experienceTotal": 0
            }
            """;
        var c = System.Text.Json.JsonSerializer.Deserialize<VampireCharacter>(legacy);
        Assert.NotNull(c);
        Assert.Equal("oldplayer", c!.PlayerName);
        Assert.Equal("Legacy", c.CharacterName);
        Assert.Equal("", c.Nature); // default
        Assert.Equal("", c.Demeanor);
        Assert.Equal("", c.Concept);
        Assert.Equal("", c.Chronicle);
        Assert.Equal("", c.Clan);
        Assert.Equal(13, c.Generation); // default
        Assert.Equal("", c.Sire);
        Assert.Equal("", c.Weakness);
        Assert.Equal(10, c.BloodPool); // default
        Assert.Empty(c.Specializations);
        Assert.Empty(c.Merits);
        Assert.Empty(c.Flaws);
    }
}