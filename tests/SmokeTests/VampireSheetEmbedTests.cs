using System;
using System.Linq;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Smoke-тесты для <see cref="VampireSheetEmbed"/>.
/// </summary>
/// <remarks>
/// Покрывают только структурные инварианты embed'а: количество блоков, видимые поля,
/// корректное число точек, безопасное поведение на пустых/неполных персонажах.
/// Discord-рендеринг самих блоков (картинки, цвета) — за пределами smoke-тестов.
/// </remarks>
public class VampireSheetEmbedTests
{
    private static VampireCharacter NewCharacter() => new VampireCharacter
    {
        PlayerName = "Андрей",
        CharacterName = "Виктор",
        Hunger = 2,
        Willpower = 5,
        WillpowerPoints = 4,
        Humanity = 7,
        Attributes =
        {
            ["Сила"] = 3,
            ["Ловкость"] = 2,
            ["Выносливость"] = 3,
            ["Обаяние"] = 1,
            ["Манипуляция"] = 2,
            ["Привлекательность"] = 2,
            ["Восприятие"] = 3,
            ["Интеллект"] = 2,
            ["Смекалка"] = 2,
            ["Атлетика"] = 2,
            ["Драка"] = 3,
            ["Хитрость"] = 1,
            ["Вождение"] = 1,
            ["Скрытность"] = 2,
            ["Медицина"] = 1,
            ["Оккультизм"] = 2
        },
        Virtues =
        {
            ["Совесть"] = 3,
            ["Самоконтроль"] = 2,
            ["Смелость"] = 4
        },
        Backgrounds = { "Старейшина 3", "Ресурсы 2" },
        Disciplines = { ["Стойкость"] = 2, ["Потенциал"] = 1 },
        Health = new HealthState(size: 7),
        Bio = "Виктор из клана Гангрел, 11-е поколение.",
        AvatarUrl = "https://example.com/avatar.png",
        ExperienceCurrent = 12,
        ExperienceTotal = 40
    };

    [Fact]
    public void Build_NullCharacter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => VampireSheetEmbed.Build(null!));
    }

    [Fact]
    public void Build_EmptyCharacter_DoesNotThrow()
    {
        var embed = VampireSheetEmbed.Build(new VampireCharacter());
        Assert.NotNull(embed);
        Assert.NotNull(embed.Title);
        Assert.NotEmpty(embed.Fields);
    }

    [Fact]
    public void Build_DefaultCharacter_TitleIsCharacterName()
    {
        var embed = VampireSheetEmbed.Build(new VampireCharacter { CharacterName = "Зоя" });
        Assert.Equal("Зоя", embed.Title);
    }

    [Fact]
    public void Build_EmptyCharacterName_FallsBackToPlaceholder()
    {
        var embed = VampireSheetEmbed.Build(new VampireCharacter { CharacterName = "" });
        Assert.Equal("Безымянный вампир", embed.Title);
    }

    [Fact]
    public void Build_WithBio_DescriptionContainsBio()
    {
        var c = NewCharacter();
        var embed = VampireSheetEmbed.Build(c);
        Assert.NotNull(embed.Description);
        Assert.Contains("Гангрел", embed.Description);
    }

    [Fact]
    public void Build_WithAvatar_ThumbnailIsSet()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        Assert.Equal("https://example.com/avatar.png", embed.Thumbnail?.Url);
    }

    [Fact]
    public void Build_WithEmptyAvatar_NoThumbnail()
    {
        var c = NewCharacter();
        c.AvatarUrl = "";
        var embed = VampireSheetEmbed.Build(c);
        Assert.Null(embed.Thumbnail);
    }

    [Fact]
    public void Build_WithInvalidAvatar_NoThumbnail()
    {
        var c = NewCharacter();
        c.AvatarUrl = "not-a-url";
        var embed = VampireSheetEmbed.Build(c);
        Assert.Null(embed.Thumbnail);
    }

    [Fact]
    public void Build_FtpAvatar_Rejected()
    {
        var c = NewCharacter();
        c.AvatarUrl = "ftp://example.com/x.png";
        var embed = VampireSheetEmbed.Build(c);
        Assert.Null(embed.Thumbnail);
    }

    [Fact]
    public void Build_HasNineCharacteristicRows()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var charsField = embed.Fields.First(f => f.Name.Contains("Характеристики"));
        // Каждая характеристика в отдельной строке, плюс 3 заголовка категорий.
        var lines = charsField.Value.ToString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int attrRows = lines.Count(l => l.Contains('`'));
        Assert.Equal(9, attrRows);
    }

    [Fact]
    public void Build_HasTalentsSkillsKnowledgesBlocks()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        Assert.Contains(embed.Fields, f => f.Name.Contains("Таланты"));
        Assert.Contains(embed.Fields, f => f.Name.Contains("Навыки"));
        Assert.Contains(embed.Fields, f => f.Name.Contains("Знания"));
    }

    [Fact]
    public void Build_TalentsBlock_ContainsAllTenTalents()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var talentsField = embed.Fields.First(f => f.Name.Contains("Таланты"));
        var body = talentsField.Value.ToString()!;
        foreach (var talent in VampireParameterCatalog.Talents)
            Assert.Contains(talent, body);
    }

    [Fact]
    public void Build_SkillsBlock_ContainsAllTenSkills()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var skillsField = embed.Fields.First(f => f.Name.Contains("Навыки"));
        var body = skillsField.Value.ToString()!;
        foreach (var skill in VampireParameterCatalog.Skills)
            Assert.Contains(skill, body);
    }

    [Fact]
    public void Build_KnowledgesBlock_ContainsAllTenKnowledges()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var kField = embed.Fields.First(f => f.Name.Contains("Знания"));
        var body = kField.Value.ToString()!;
        foreach (var k in VampireParameterCatalog.Knowledges)
            Assert.Contains(k, body);
    }

    [Fact]
    public void Build_HasVirtuesBlock()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        Assert.Contains(embed.Fields, f => f.Name.Contains("Добродетели"));
    }

    [Fact]
    public void Build_HasMiscBlockWithHumanityWillpowerHunger()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var misc = embed.Fields.First(f => f.Name.Contains("Прочее"));
        var body = misc.Value.ToString()!;
        Assert.Contains("Человечность", body);
        Assert.Contains("Воля", body);
        Assert.Contains("Голод", body);
        Assert.Contains("Опыт", body);
    }

    [Fact]
    public void Build_HasHealthBlock()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var h = embed.Fields.First(f => f.Name.Contains("Здоровье"));
        Assert.Contains("Шкала", h.Value.ToString());
    }

    [Fact]
    public void Build_NullHealth_OmitsHealthField()
    {
        var c = NewCharacter();
        c.Health = null;
        var embed = VampireSheetEmbed.Build(c);
        Assert.DoesNotContain(embed.Fields, f => f.Name.Contains("Здоровье"));
    }

    [Fact]
    public void Build_HasDisciplinesBlock()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var d = embed.Fields.First(f => f.Name.Contains("Дисциплины"));
        Assert.Contains("Стойкость", d.Value.ToString());
        Assert.Contains("Потенциал", d.Value.ToString());
    }

    [Fact]
    public void Build_EmptyDisciplines_OmitsDisciplinesField()
    {
        var c = NewCharacter();
        c.Disciplines.Clear();
        var embed = VampireSheetEmbed.Build(c);
        Assert.DoesNotContain(embed.Fields, f => f.Name.Contains("Дисциплины"));
    }

    [Fact]
    public void Build_HasBackgroundsBlock()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var b = embed.Fields.First(f => f.Name.Contains("Фон"));
        Assert.Contains("Старейшина", b.Value.ToString());
    }

    [Fact]
    public void Build_EmptyBackgrounds_OmitsBackgroundsField()
    {
        var c = NewCharacter();
        c.Backgrounds.Clear();
        var embed = VampireSheetEmbed.Build(c);
        Assert.DoesNotContain(embed.Fields, f => f.Name.Contains("Фон"));
    }

    [Fact]
    public void Build_HasAuthorPlayerName()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        Assert.NotNull(embed.Author);
        Assert.Equal("Андрей", embed.Author?.Name);
    }

    [Fact]
    public void Build_HasFooter()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        Assert.NotNull(embed.Footer);
        Assert.Contains("/vampire_show", embed.Footer?.Text);
    }

    [Fact]
    public void Build_HasDefaultColor()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        Assert.Equal(VampireSheetEmbed.DefaultColor, embed.Color);
    }

    [Fact]
    public void Build_LongBio_TruncatedToDiscordLimit()
    {
        var c = NewCharacter();
        c.Bio = new string('ы', 5000);
        var embed = VampireSheetEmbed.Build(c);
        Assert.NotNull(embed.Description);
        // Discord EmbedBuilder.MaxDescriptionLength = 4096; наш Truncate оставляет +1 символ «…».
        Assert.True(embed.Description!.Length <= 4097);
    }

    [Fact]
    public void DotsString_NormalCase()
    {
        Assert.Equal("●●●○○", VampireSheetEmbed.DotsString(3, 5));
    }

    [Fact]
    public void DotsString_Overflow_ClampsToTotal()
    {
        Assert.Equal("●●●●●", VampireSheetEmbed.DotsString(7, 5));
    }

    [Fact]
    public void DotsString_Negative_TreatedAsZero()
    {
        Assert.Equal("○○○○○", VampireSheetEmbed.DotsString(-1, 5));
    }

    [Fact]
    public void DotsString_ZeroTotal_EmptyString()
    {
        Assert.Equal("", VampireSheetEmbed.DotsString(3, 0));
    }

    [Fact]
    public void Build_HighHunger_Displayed()
    {
        var c = NewCharacter();
        c.Hunger = 5;
        var embed = VampireSheetEmbed.Build(c);
        var misc = embed.Fields.First(f => f.Name.Contains("Прочее"));
        Assert.Contains("●●●●●", misc.Value.ToString());
    }

    [Fact]
    public void Build_HealthWithNonLethal_ReflectedInRender()
    {
        var c = NewCharacter();
        c.Health!.ApplyNonLethal(2);
        var embed = VampireSheetEmbed.Build(c);
        var h = embed.Fields.First(f => f.Name.Contains("Здоровье"));
        Assert.Contains("//", h.Value.ToString());
    }
}
