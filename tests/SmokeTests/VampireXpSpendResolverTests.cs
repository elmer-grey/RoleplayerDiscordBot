using System;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты для <see cref="VampireXpSpendResolver"/> — таблица XP-курсов V20 (стр. 141)
/// + Каитиф-правило 6 × текущее (V20 стр. 131).
/// </summary>
public class VampireXpSpendResolverTests
{
    // ─── Характеристика (текущее + 1) × 4 ────────────────────────────────

    [Theory]
    [InlineData(0, 4)]   // 1 × 4 = 4
    [InlineData(1, 8)]   // 2 × 4 = 8
    [InlineData(2, 12)]  // 3 × 4 = 12
    [InlineData(3, 16)]  // 4 × 4 = 16
    [InlineData(4, 20)]  // 5 × 4 = 20
    public void AttributeXp_ValidLevels(int current, int expected)
    {
        Assert.Equal(expected, VampireXpSpendResolver.AttributeXp(current));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void AttributeXp_OutOfRange_ReturnsMinusOne(int current)
    {
        Assert.Equal(-1, VampireXpSpendResolver.AttributeXp(current));
    }

    // ─── Способность (текущее + 1) × 2 ───────────────────────────────────

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 6)]
    [InlineData(3, 8)]
    [InlineData(4, 10)]
    public void AbilityXp_ValidLevels(int current, int expected)
    {
        Assert.Equal(expected, VampireXpSpendResolver.AbilityXp(current));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void AbilityXp_OutOfRange_ReturnsMinusOne(int current)
    {
        Assert.Equal(-1, VampireXpSpendResolver.AbilityXp(current));
    }

    // ─── Дисциплина: клановые × 5, сторонние × 7, Каитиф × 6 ────────────

    [Theory]
    [InlineData(0, false, false, 5)]   // клан, 1 × 5
    [InlineData(1, false, false, 10)]  // клан, 2 × 5
    [InlineData(4, false, false, 25)]  // клан, 5 × 5
    [InlineData(0, false, true, 7)]    // сторонняя, 1 × 7
    [InlineData(2, false, true, 21)]   // сторонняя, 3 × 7
    public void DisciplineXp_ClanVsOutside(int current, bool isCaitiff, bool isOutside, int expected)
    {
        // isOutside == true → !isClanDiscipline
        Assert.Equal(expected, VampireXpSpendResolver.DisciplineXp(current, isCaitiff, !isOutside));
    }

    [Theory]
    [InlineData(0, 6)]   // 1 × 6
    [InlineData(1, 12)]  // 2 × 6
    [InlineData(3, 24)]  // 4 × 6
    [InlineData(4, 30)]  // 5 × 6
    public void DisciplineXp_Caitiff_AnyIsSix(int current, int expected)
    {
        // Каитиф: и для «клановой», и для «сторонней» — 6 × текущее+1
        Assert.Equal(expected, VampireXpSpendResolver.DisciplineXp(current, isCaitiff: true, isClanDiscipline: true));
        Assert.Equal(expected, VampireXpSpendResolver.DisciplineXp(current, isCaitiff: true, isClanDiscipline: false));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void DisciplineXp_OutOfRange(int current)
    {
        Assert.Equal(-1, VampireXpSpendResolver.DisciplineXp(current, false, false));
    }

    // ─── Вторичный путь (Некромантия/Тауматургия) (текущее + 1) × 4 ─────

    [Theory]
    [InlineData(0, 4)]
    [InlineData(2, 12)]
    [InlineData(4, 20)]
    public void SecondaryPathXp_Valid(int current, int expected)
    {
        Assert.Equal(expected, VampireXpSpendResolver.SecondaryPathXp(current));
    }

    // ─── Добродетель (текущее + 1) × 2 ───────────────────────────────────

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 10)]
    public void VirtueXp_Valid(int current, int expected)
    {
        Assert.Equal(expected, VampireXpSpendResolver.VirtueXp(current));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(99)]
    public void VirtueXp_OutOfRange(int current)
    {
        Assert.Equal(-1, VampireXpSpendResolver.VirtueXp(current));
    }

    // ─── Человечность/Путь (текущее + 1) × 2 ─────────────────────────────

    [Theory]
    [InlineData(1, 4)]   // 2 × 2
    [InlineData(5, 12)]  // 6 × 2
    [InlineData(9, 20)]  // 10 × 2
    public void HumanityXp_Valid(int current, int expected)
    {
        Assert.Equal(expected, VampireXpSpendResolver.HumanityXp(current));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void HumanityXp_OutOfRange(int current)
    {
        Assert.Equal(-1, VampireXpSpendResolver.HumanityXp(current));
    }

    // ─── Воля (текущее + 1) × 1 ──────────────────────────────────────────

    [Theory]
    [InlineData(1, 2)]
    [InlineData(5, 6)]
    [InlineData(9, 10)]
    public void WillpowerXp_Valid(int current, int expected)
    {
        Assert.Equal(expected, VampireXpSpendResolver.WillpowerXp(current));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void WillpowerXp_OutOfRange(int current)
    {
        Assert.Equal(-1, VampireXpSpendResolver.WillpowerXp(current));
    }

    // ─── Фикс-стоимости ──────────────────────────────────────────────────

    [Fact]
    public void NewAbilityXp_FixedCost()
    {
        Assert.Equal(3, VampireXpSpendResolver.NewAbilityXp());
    }

    [Fact]
    public void NewDisciplineXp_FixedCost()
    {
        Assert.Equal(10, VampireXpSpendResolver.NewDisciplineXp());
    }

    [Fact]
    public void NewSecondaryPathXp_FixedCost()
    {
        Assert.Equal(7, VampireXpSpendResolver.NewSecondaryPathXp());
    }

    // ─── CanBuy проверки ─────────────────────────────────────────────────

    [Fact]
    public void CanBuy_ReturnsFalseAtMax()
    {
        Assert.False(VampireXpSpendResolver.CanBuyAttribute(5));
        Assert.False(VampireXpSpendResolver.CanBuyAbility(5));
        Assert.False(VampireXpSpendResolver.CanBuyDiscipline(5));
        Assert.False(VampireXpSpendResolver.CanBuyVirtue(5));
        Assert.False(VampireXpSpendResolver.CanBuyHumanity(10));
        Assert.False(VampireXpSpendResolver.CanBuyWillpower(10));
    }

    [Fact]
    public void CanBuy_ReturnsTrueBelowMax()
    {
        Assert.True(VampireXpSpendResolver.CanBuyAttribute(0));
        Assert.True(VampireXpSpendResolver.CanBuyAbility(4));
        Assert.True(VampireXpSpendResolver.CanBuyDiscipline(0));
        Assert.True(VampireXpSpendResolver.CanBuyVirtue(0));
        Assert.True(VampireXpSpendResolver.CanBuyHumanity(1));
        Assert.True(VampireXpSpendResolver.CanBuyWillpower(9));
    }

    // ─── Высокоуровневый API по персонажу ─────────────────────────────────

    private static VampireCharacter MakeChar(string clan, int baseLvl, int freebieLvl)
    {
        var c = new VampireCharacter { Clan = clan };
        c.Disciplines["Стремительность"] = baseLvl;
        c.FreebieDisciplines["Стремительность"] = freebieLvl;
        return c;
    }

    [Fact]
    public void GetCurrentDisciplineLevel_SumsBaseAndFreebie()
    {
        var c = MakeChar("Бруха", baseLvl: 1, freebieLvl: 2);
        Assert.Equal(3, VampireXpSpendResolver.GetCurrentDisciplineLevel(c, "Стремительность"));
    }

    [Fact]
    public void GetCurrentDisciplineLevel_Missing_Zero()
    {
        var c = new VampireCharacter { Clan = "Вентру" };
        Assert.Equal(0, VampireXpSpendResolver.GetCurrentDisciplineLevel(c, "Доминирование"));
    }

    [Fact]
    public void IsClanDisciplineFor_NonCaitiff_TrueForClan()
    {
        var c = new VampireCharacter { Clan = "Бруха" };
        Assert.True(VampireXpSpendResolver.IsClanDisciplineFor(c, "Стремительность"));   // клан Бруха
        Assert.False(VampireXpSpendResolver.IsClanDisciplineFor(c, "Анимализм"));       // не клан
    }

    [Fact]
    public void IsClanDisciplineFor_Caitiff_AlwaysTrue()
    {
        var c = new VampireCharacter { Clan = "Каитиф" };
        Assert.True(VampireXpSpendResolver.IsClanDisciplineFor(c, "Стремительность"));
        Assert.True(VampireXpSpendResolver.IsClanDisciplineFor(c, "Анимализм"));
        Assert.True(VampireXpSpendResolver.IsClanDisciplineFor(c, "Некромантия"));
    }

    [Fact]
    public void XpCostForDiscipline_Clan_5x()
    {
        var c = MakeChar("Бруха", baseLvl: 1, freebieLvl: 0); // current = 1
        // (1 + 1) × 5 = 10
        Assert.Equal(10, VampireXpSpendResolver.XpCostForDiscipline(c, "Стремительность"));
    }

    [Fact]
    public void XpCostForDiscipline_Outside_7x()
    {
        var c = MakeChar("Бруха", baseLvl: 0, freebieLvl: 0); // current = 0
        c.Disciplines["Анимализм"] = 0;
        // (0 + 1) × 7 = 7
        Assert.Equal(7, VampireXpSpendResolver.XpCostForDiscipline(c, "Анимализм"));
    }

    [Fact]
    public void XpCostForDiscipline_Caitiff_6x_AnyDiscipline()
    {
        var c = MakeChar("Каитиф", baseLvl: 0, freebieLvl: 0);
        // Каитиф: и «Стремительность» (теоретически не клановая, но Каитифу — всё равно)
        // и «Анимализм» — обе × 6.
        c.Disciplines["Анимализм"] = 0;
        Assert.Equal(6, VampireXpSpendResolver.XpCostForDiscipline(c, "Анимализм"));
        Assert.Equal(6, VampireXpSpendResolver.XpCostForDiscipline(c, "Стремительность"));
    }

    [Fact]
    public void XpCostForDiscipline_Caitiff_WithCurrentLevel_Still6x()
    {
        var c = MakeChar("Каитиф", baseLvl: 2, freebieLvl: 0); // current = 2
        c.Disciplines["Анимализм"] = 2;
        // (2 + 1) × 6 = 18
        Assert.Equal(18, VampireXpSpendResolver.XpCostForDiscipline(c, "Анимализм"));
    }

    [Fact]
    public void FormatCost_Valid()
    {
        Assert.Equal("5 XP", VampireXpSpendResolver.FormatCost(5));
        Assert.Equal("20 XP", VampireXpSpendResolver.FormatCost(20));
    }

    [Fact]
    public void FormatCost_Invalid()
    {
        Assert.Equal("—", VampireXpSpendResolver.FormatCost(0));
        Assert.Equal("—", VampireXpSpendResolver.FormatCost(-1));
    }
}
