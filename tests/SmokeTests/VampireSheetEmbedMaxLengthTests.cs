using System;
using System.Collections.Generic;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Страховка: при полностью заполненном персонаже embed остаётся в лимитах Discord.
/// Если эти тесты провалятся — лист не доедет до пользователя, бот будет падать.
/// </summary>
public class VampireSheetEmbedMaxLengthTests
{
    private static string Repeat(int n) => new string('*', Math.Max(0, n));

    private static VampireCharacter NewMaxed() => new VampireCharacter
    {
        PlayerName = "Андрей",
        CharacterName = "Виктор",
        Clan = "Toreador",
        Nature = "Бонвиван",
        Demeanor = "Славный малый",
        Concept = "Утончённый художник",
        Sire = "Граф Дракула",
        Generation = 7,
        Chronicle = "Хроники Тьмы",
        Bio = Repeat(4000),
        AvatarUrl = "https://example.com/avatar.png",
        Hunger = 5,
        Willpower = 10,
        WillpowerPoints = 10,
        Humanity = 10,
        BloodPool = 30,
        Attributes = new Dictionary<string, int>
        {
            ["Сила"] = 5, ["Ловкость"] = 5, ["Выносливость"] = 5,
            ["Обаяние"] = 5, ["Манипуляция"] = 5, ["Привлекательность"] = 5,
            ["Восприятие"] = 5, ["Интеллект"] = 5, ["Смекалка"] = 5,
        },
        Abilities = new List<string>
        {
            "Атлетика", "Драка", "Хитрость", "Вождение", "Скрытность",
            "Медицина", "Оккультизм", "Знание", "Финансы", "Ремесло",
            "Этикет", "Запугивание", "Убеждение", "Чувство", "Языки",
        },
        Backgrounds = new Dictionary<string, int>
                {
                    ["Влияние"] = 2, ["Состояние"] = 2, ["Секреты"] = 1, ["Слуги"] = 2, ["Враги"] = 3,
                },
        Disciplines = new Dictionary<string, int>
        {
            ["Скорость"] = 5, ["Сила"] = 5, ["Стойкость"] = 5,
            ["Присутствие"] = 5, ["Обаяние.д"] = 5, ["Забвение"] = 5,
            ["Тауматургия"] = 5, ["Некромантия"] = 5,
        },
        Specializations = new Dictionary<string, string>
        {
            ["Атлетика"] = "паркур",
            ["Драка"] = "клинки",
            ["Оккультизм"] = "ритуалы",
        },
        Merits = new Dictionary<string, int>
        {
            ["Острый слух"] = 3, ["Быстрые рефлексы"] = 2,
        },
        Flaws = new Dictionary<string, int>
        {
            ["Одержимость"] = 2,
        },
        Virtues = new Dictionary<string, int>
        {
            ["Совесть"] = 5, ["Самоконтроль"] = 5, ["Смелость"] = 5,
        },
        ExperienceCurrent = 50,
        ExperienceTotal = 75,
    };

    [Fact]
    public void Sheet_AtMaxFill_StaysUnderDiscordLimit()
    {
        var c = NewMaxed();
        var embed = VampireSheetEmbed.Build(c);
        Assert.True(embed.Length <= 6000,
            $"embed слишком длинный: {embed.Length} символов (лимит 6000)");
    }

    [Fact]
    public void Sheet_DoesNotCrash_OnMinimalCharacter()
    {
        var c = new VampireCharacter { CharacterId = Guid.NewGuid() };
        var embed = VampireSheetEmbed.Build(c);
        Assert.True(embed.Length <= 6000);
    }

    [Fact]
    public void Sheet_HasNoEmptyTitlesInFields()
    {
        var c = NewMaxed();
        var embed = VampireSheetEmbed.Build(c);
        foreach (var f in embed.Fields)
            Assert.False(string.IsNullOrWhiteSpace(f.Name),
                $"Поле с пустым именем: value={f.Value}");
    }
}
