using System;
using System.Linq;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Smoke-тесты для <see cref="VampireSheetEmbed"/> — структура листа по V20 стр. 92.
/// </summary>
/// <remarks>
/// Покрывают структурные инварианты: наличие 3-колоночных рядов, имена блоков,
/// корректное число точек, специализации при ≥ 4, поведение на пустых полях.
/// Discord-рендеринг (картинки, цвета) — за пределами smoke-тестов.
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
        BloodPool = 12,
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
            ["Атлетика"] = 4,
            ["Драка"] = 3,
            ["Хитрость"] = 1,
            ["Вождение"] = 1,
            ["Скрытность"] = 2,
            ["Медицина"] = 1,
            ["Оккультизм"] = 2,
        },
        Virtues =
        {
            ["Совесть"] = 3,
            ["Самоконтроль"] = 2,
            ["Смелость"] = 4,
        },
        Backgrounds = { ["Старейшина"] = 3, ["Ресурсы"] = 2 },
        Disciplines = { ["Стойкость"] = 2, ["Потенциал"] = 1 },
        Specializations = { ["Атлетика"] = "плавание" },
        Merits = { ["Острые чувства"] = 2, ["Связи"] = 3 },
        Flaws = { ["Заразный укус"] = 2 },
        Health = new HealthState(size: 7),
        Bio = "Виктор из клана Гангрел, 11-е поколение.",
        AvatarUrl = "https://example.com/avatar.png",
        ExperienceCurrent = 12,
        ExperienceTotal = 40,
        Nature = "Судья",
        Demeanor = "Конформист",
        Concept = "Староста поневоле",
        Chronicle = "Киев-1240",
        Clan = "Гангрел",
        Generation = 11,
        Sire = "Элеонора фон Штейн",
        Weakness = "Приступы ярости в ближнем бою",
    };

    // ─── Базовая сборка ──────────────────────────────────────────────────

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
    public void Build_WithBio_DescriptionIsEmptyByDefault()
    {
        // Bio НЕ встраивается в основной embed — для него отдельный
        // VampireDescriptionEmbed, который показывается по кнопке «Описание».
        var embed = VampireSheetEmbed.Build(NewCharacter());
        Assert.Null(embed.Description);
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
    public void Build_NormalBio_DoesNotEmbedIntoMainSheet()
    {
        var c = NewCharacter();
        c.Bio = "Краткое био.";
        var embed = VampireSheetEmbed.Build(c);
        Assert.Null(embed.Description);
    }

    // ─── Шапка V20 ───────────────────────────────────────────────────────

    [Fact]
    public void Build_Header_ContainsAllV20Fields()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var header = embed.Fields.First(f => f.Name.Contains("Идентификация"));
        var body = header.Value.ToString()!;
        Assert.Contains("Хроника", body);
        Assert.Contains("Натура", body);
        Assert.Contains("Маска", body);
        Assert.Contains("Амплуа", body);
        Assert.Contains("Клан", body);
        Assert.Contains("поколение", body);
        Assert.Contains("Сир", body);
    }

    [Fact]
    public void Build_NoHeader_WhenAllFieldsEmpty()
    {
        var c = new VampireCharacter();
        var embed = VampireSheetEmbed.Build(c);
        // Если все поля шапки пусты — блок «Идентификация» отсутствует.
        Assert.DoesNotContain(embed.Fields, f => f.Name.Contains("Идентификация"));
    }

    // ─── Характеристики ─────────────────────────────────────────────────

    [Fact]
    public void Build_Characteristics_ContainsAllThreeGroups()
    {
        // Характеристики разнесены по трём inline-колонкам (Физ/Соц/Мент),
        // как у Способностей. Заголовки групп ищутся во всех полях embed-а.
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var allText = string.Join("\n", embed.Fields.Select(f => f.Value.ToString() ?? ""));
        Assert.Contains("Физические", allText);
        Assert.Contains("Социальные", allText);
        Assert.Contains("Ментальные", allText);
    }

    [Fact]
    public void Build_Characteristics_ContainsAllNine()
    {
        // Проверяем, что все 9 атрибутов присутствуют в embed-е (в любой колонке).
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var allText = string.Join("\n", embed.Fields.Select(f => f.Value.ToString() ?? ""));
        foreach (var cat in VampireParameterCatalog.Physical
            .Concat(VampireParameterCatalog.Social)
            .Concat(VampireParameterCatalog.Mental))
        {
            Assert.Contains(cat, allText);
        }
    }

    // ─── Способности (Таланты / Навыки / Знания) ────────────────────────

    [Fact]
    public void Build_Abilities_ContainAllThreeGroups()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var allText = string.Join("\n", embed.Fields.Select(f => f.Value.ToString() ?? ""));
        Assert.Contains("Таланты", allText);
        Assert.Contains("Навыки", allText);
        Assert.Contains("Знания", allText);
        foreach (var t in VampireParameterCatalog.Talents) Assert.Contains(t, allText);
        foreach (var s in VampireParameterCatalog.Skills) Assert.Contains(s, allText);
        foreach (var k in VampireParameterCatalog.Knowledges) Assert.Contains(k, allText);
    }

    [Fact]
    public void Build_Abilities_SpecializationShown_WhenDotsAtLeast4()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var allText = string.Join("\n", embed.Fields.Select(f => f.Value.ToString() ?? ""));
        Assert.Contains("плавание", allText); // specialization for Атлетика
    }

    [Fact]
    public void Build_Abilities_NoSpecialization_WhenDotsBelow4()
    {
        var c = NewCharacter();
        c.Specializations = new() { ["Атлетика"] = "плавание" };
        c.Attributes["Атлетика"] = 2;
        var embed = VampireSheetEmbed.Build(c);
        var allText = string.Join("\n", embed.Fields.Select(f => f.Value.ToString() ?? ""));
        Assert.DoesNotContain("плавание", allText);
    }

    // ─── Преимущества ────────────────────────────────────────────────────

    [Fact]
    public void Build_Advantages_ContainsThreeSubheaders()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var allText = string.Join("\n", embed.Fields.Select(f => f.Value.ToString() ?? ""));
        Assert.Contains("Дисциплины", allText);
        Assert.Contains("Факты биографии", allText);
        Assert.Contains("Добродетели", allText);
    }

    [Fact]
    public void Build_Disciplines_ListsAll()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var allText = string.Join("\n", embed.Fields.Select(f => f.Value.ToString() ?? ""));
        Assert.Contains("Стойкость", allText);
        Assert.Contains("Потенциал", allText);
    }

    [Fact]
    public void Build_Backgrounds_ListsAll()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var allText = string.Join("\n", embed.Fields.Select(f => f.Value.ToString() ?? ""));
        Assert.Contains("Старейшина", allText);
        Assert.Contains("Ресурсы", allText);
    }

    [Fact]
    public void Build_Virtues_ShowsBothNames()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var allText = string.Join("\n", embed.Fields.Select(f => f.Value.ToString() ?? ""));
        Assert.Contains("Совесть/Решимость", allText);
        Assert.Contains("Самоконтроль/Инстинкты", allText);
        Assert.Contains("Смелость", allText);
    }

    [Fact]
    public void Build_Virtues_DisplayFiveCells()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var allText = string.Join("\n", embed.Fields.Select(f => f.Value.ToString() ?? ""));
        int fiveCells = System.Text.RegularExpressions.Regex.Matches(allText, @"●{1,5}○{1,5}").Count;
        Assert.True(fiveCells >= 3, $"ожидаем минимум 3 строки ●○, получили {fiveCells}");
    }

    // ─── Нижний ряд ──────────────────────────────────────────────────────

    [Fact]
    public void Build_BottomRow_HasThreeColumns()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var bottom = embed.Fields
            .Where(f => f.Name.Contains("Достоинства") || f.Name.Contains("Суть") || f.Name.Contains("Здоровье и опыт"))
            .ToList();
        Assert.Equal(3, bottom.Count);
    }

    [Fact]
    public void Build_MeritsFlaws_ShowsMeritsAndFlaws()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var mf = embed.Fields.First(f => f.Name.Contains("Достоинства и недостатки"));
        var body = mf.Value.ToString()!;
        Assert.Contains("Острые чувства", body);
        Assert.Contains("Связи", body);
        Assert.Contains("Заразный укус", body);
    }

    [Fact]
    public void Build_Essence_ShowsHumanityWillpowerHunger()
    {
        // Кровь (BloodPool) больше не показывается: в V20 её роль играет Голод.
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var e = embed.Fields.First(f => f.Name.Contains("Суть"));
        var body = e.Value.ToString()!;
        Assert.Contains("Человечность", body);
        Assert.Contains("Воля", body);
        Assert.Contains("Голод", body);
        Assert.DoesNotContain("Кровь", body);
    }

    /// <summary>
    /// VtM V20 #42: при Hunger = 5 Зверь в ярости — предупреждение должно появиться в колонке «Суть».
    /// </summary>
    [Fact]
    public void Build_Essence_Hunger5_ShowsBeastWarning()
    {
        var c = NewCharacter();
        c.Hunger = 5;
        var embed = VampireSheetEmbed.Build(c);
        var e = embed.Fields.First(f => f.Name.Contains("Суть"));
        Assert.Contains("⚠ Зверь в ярости", e.Value.ToString());
    }

    /// <summary>
    /// VtM V20 #42: при Hunger &lt; 5 предупреждения быть не должно.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Build_Essence_HungerBelow5_NoBeastWarning(int hunger)
    {
        var c = NewCharacter();
        c.Hunger = hunger;
        var embed = VampireSheetEmbed.Build(c);
        var e = embed.Fields.First(f => f.Name.Contains("Суть"));
        Assert.DoesNotContain("Зверь в ярости", e.Value.ToString());
    }

    [Fact]
    public void Build_HealthExperience_HasAllParts()
    {
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var h = embed.Fields.First(f => f.Name.Contains("Здоровье и опыт"));
        var body = h.Value.ToString()!;
        Assert.Contains("Здоровье", body);
        Assert.Contains("Штраф", body);
        Assert.Contains("Изъян", body);
        Assert.Contains("Приступы ярости", body);
        Assert.Contains("Опыт", body);
        Assert.Contains("12 / 40", body);
    }

    [Fact]
    public void Build_HealthExperience_NullHealth_ShowsPlaceholder()
    {
        var c = NewCharacter();
        c.Health = null;
        var embed = VampireSheetEmbed.Build(c);
        var h = embed.Fields.First(f => f.Name.Contains("Здоровье и опыт"));
        // При Health == null показываем полноценную пустую шкалу из 7 ячеек
        // (а не страшное "не инициализировано", как раньше).
        Assert.Contains("**Здоровье:**", h.Value.ToString());
        Assert.DoesNotContain("не инициализировано", h.Value.ToString());
        // 7 пустых ячеек = 7 букв "S" (CellState.Empty) в Render().
        Assert.Contains("SSSSSSS", h.Value.ToString());
    }

    [Fact]
    public void Build_HealthExperience_ReflectsTablePenalty()
    {
        var c = NewCharacter();
        c.Health!.ApplyNonLethal(1); // одна косая черта — штраф 0 по таблице
        var embed = VampireSheetEmbed.Build(c);
        var h = embed.Fields.First(f => f.Name.Contains("Здоровье и опыт"));
        var body = h.Value.ToString()!;
        Assert.Contains("Здоровье:", body);
        Assert.Contains("Штраф", body);
        // Один нелетальный → ячейка с косой чертой, штраф по таблице = 0.
        Assert.Contains("0", body);
    }

    [Fact]
    public void Build_HealthExperience_ShowsIncapacitatedMark()
    {
        var c = NewCharacter();
        for (int i = 0; i < 7; i++) c.Health!.ApplyLethal(1);
        var embed = VampireSheetEmbed.Build(c);
        var h = embed.Fields.First(f => f.Name.Contains("Здоровье и опыт"));
        Assert.Contains("Небоеспособен", h.Value.ToString());
    }

    // ─── Прочее ──────────────────────────────────────────────────────────

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
    public void Build_MeritsFlaws_Empty_ShowsDash()
    {
        var c = NewCharacter();
        c.Merits.Clear();
        c.Flaws.Clear();
        var embed = VampireSheetEmbed.Build(c);
        var mf = embed.Fields.First(f => f.Name.Contains("Достоинства и недостатки"));
        Assert.Contains("—", mf.Value.ToString());
    }

    [Fact]
    public void Build_EmptyCharacter_NoHeaderButAllRowsPresent()
    {
        var c = new VampireCharacter();
        var embed = VampireSheetEmbed.Build(c);
        Assert.DoesNotContain(embed.Fields, f => f.Name.Contains("Идентификация"));
        Assert.Contains(embed.Fields, f => f.Name.Contains("Характеристики"));
        Assert.Contains(embed.Fields, f => f.Name.Contains("Достоинства и недостатки"));
        Assert.Contains(embed.Fields, f => f.Name.Contains("Суть"));
        Assert.Contains(embed.Fields, f => f.Name.Contains("Здоровье и опыт"));
    }

    // ── Шаг 6: финальный лист = формулы Шага 5 ─────────────────────

    [Fact]
    public void Build_Essence_UsesComputeHumanity_FromVirtues()
    {
        // Человечность = Совесть + Самоконтроль = 3 + 2 = 5 (из NewCharacter).
        // Legacy-поле Humanity=7 игнорируется.
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var e = embed.Fields.First(f => f.Name.Contains("Суть"));
        var body = e.Value.ToString()!;
        Assert.Contains("Человечность", body);
        Assert.Contains("●●●●●○○○○○", body); // 5 закрашенных из 10
    }

    [Fact]
    public void Build_Essence_UsesComputeWillpower_FromCourage()
    {
        // Воля = Смелость = 4 (из NewCharacter). Legacy Willpower=5 игнорируется.
        var embed = VampireSheetEmbed.Build(NewCharacter());
        var e = embed.Fields.First(f => f.Name.Contains("Суть"));
        var body = e.Value.ToString()!;
        Assert.Contains("Воля", body);
        Assert.Contains("●●●●○○○○○○", body); // 4 из 10
    }

    [Fact]
    public void Build_Essence_ReflectsHumanityBonusFromStep5()
    {
        // Если игрок потратил freebie на Чел на Шаге 5 — финальный лист должен это показать.
        var c = NewCharacter();
        c.HumanityBonus = 2; // +2 за свободные пункты
        var embed = VampireSheetEmbed.Build(c);
        var e = embed.Fields.First(f => f.Name.Contains("Суть"));
        var body = e.Value.ToString()!;
        // 3 (Совесть) + 2 (СК) + 2 (бонус) = 7
        Assert.Contains("●●●●●●●○○○", body);
    }
}
