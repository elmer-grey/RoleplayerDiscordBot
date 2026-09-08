using System;
using System.Collections.Generic;
using System.Linq;
using RPBot.SlashModules;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты для <see cref="RollContext.BuildPool"/> — чистая логика сборки пула
/// по правилам VtM V20, без Discord-обвязки.
/// </summary>
public class VampireRollSlashModuleTests
{
    // ─── Базовый пул: характеристика + навык ────────────────────────────────

    [Fact]
    public void BuildPool_CharacteristicAndAbility_SumsBoth()
    {
        var c = MakeCharacter(
            attributes: new() { ["Сила"] = 2, ["Драка"] = 3 },
            hunger: 1);

        var r = RollContext.BuildPool(c, "Сила", "Драка", null, 0, 6);

        Assert.Equal(5, r.PoolSize);                       // 2 + 3
        Assert.Equal(1, r.Hunger);
        Assert.Equal(6, r.Difficulty);                     // с навыком +1 НЕ добавляется
        Assert.Equal("Сила", r.CharacteristicName);
        Assert.Equal(2, r.CharacteristicValue);
        Assert.Equal("Драка", r.AbilityName);
        Assert.Equal(3, r.AbilityValue);
        Assert.False(r.AbilityMissingPenalty);
        Assert.Null(r.Specialization);
    }

    [Fact]
    public void BuildPool_NoAbility_UsesCharacteristicOnly_AndIncreasesDifficultyByOne()
    {
        var c = MakeCharacter(
            attributes: new() { ["Ловкость"] = 4 },
            hunger: 2);

        var r = RollContext.BuildPool(c, "Ловкость", null, null, 0, 6);

        Assert.Equal(4, r.PoolSize);                       // только характеристика
        Assert.Equal(7, r.Difficulty);                     // +1 за отсутствие навыка
        Assert.Null(r.AbilityName);
        Assert.True(r.AbilityMissingPenalty);
    }

    [Fact]
    public void BuildPool_NoAbility_DifficultyCappedAt10()
    {
        var c = MakeCharacter(
            attributes: new() { ["Ловкость"] = 3 },
            hunger: 0);

        var r = RollContext.BuildPool(c, "Ловкость", null, null, 0, 10);

        Assert.Equal(10, r.Difficulty);                    // 10 + 1, но clamp на 10
    }

    // ─── Дисциплина увеличивает пул ────────────────────────────────────────

    [Fact]
    public void BuildPool_Discipline_AddsLevelToPool()
    {
        var c = MakeCharacter(
            attributes: new() { ["Сила"] = 2, ["Драка"] = 2 },
            disciplines: new() { ["Стремительность"] = 3 },
            hunger: 1);

        var r = RollContext.BuildPool(c, "Сила", "Драка", "Стремительность", 0, 6);

        Assert.Equal(7, r.PoolSize);                       // 2 + 2 + 3
        Assert.Equal(3, r.DisciplineValue);
        Assert.Equal("Стремительность", r.DisciplineName);
    }

    // ─── Бонусные / штрафные кубы ──────────────────────────────────────────

    [Theory]
    [InlineData( 2, 8)]   // Сила 3 + Драка 3 + 2
    [InlineData(-2, 4)]   // Сила 3 + Драка 3 - 2
    [InlineData( 0, 6)]
    public void BuildPool_BonusDice_Sums(int bonus, int expected)
    {
        var c = MakeCharacter(
            attributes: new() { ["Сила"] = 3, ["Драка"] = 3 },
            hunger: 1);

        var r = RollContext.BuildPool(c, "Сила", "Драка", null, bonus, 6);

        Assert.Equal(expected, r.PoolSize);
        Assert.Equal(bonus, r.BonusDice);
    }

    // ─── Специализация извлекается ─────────────────────────────────────────

    [Fact]
    public void BuildPool_Specialization_ExtractedFromCharacter()
    {
        var c = MakeCharacter(
            attributes: new() { ["Ловкость"] = 3, ["Скрытность"] = 2 },
            specializations: new() { ["Скрытность"] = "тени" },
            hunger: 1);

        var r = RollContext.BuildPool(c, "Ловкость", "Скрытность", null, 0, 6);

        Assert.Equal("тени", r.Specialization);
    }

    [Fact]
    public void BuildPool_NoSpecialization_Null()
    {
        var c = MakeCharacter(
            attributes: new() { ["Ловкость"] = 3, ["Скрытность"] = 2 },
            hunger: 1);

        var r = RollContext.BuildPool(c, "Ловкость", "Скрытность", null, 0, 6);

        Assert.Null(r.Specialization);
    }

    [Fact]
    public void BuildPool_AdHocSpecialization_OverridesCharacterSpec()
    {
        var c = MakeCharacter(
            attributes: new() { ["Ловкость"] = 3, ["Скрытность"] = 2 },
            specializations: new() { ["Скрытность"] = "тени" },
            hunger: 1);

        var r = RollContext.BuildPool(c, "Ловкость", "Скрытность", null, 0, 6,
            adHocSpecialization: "снег");

        Assert.Equal("снег", r.Specialization);
    }

    [Fact]
    public void BuildPool_AdHocSpec_UsedEvenWhenNoCharacterSpec()
    {
        var c = MakeCharacter(
            attributes: new() { ["Ловкость"] = 3, ["Скрытность"] = 2 },
            hunger: 1);

        var r = RollContext.BuildPool(c, "Ловкость", "Скрытность", null, 0, 6,
            adHocSpecialization: "ad-hoc");

        Assert.Equal("ad-hoc", r.Specialization);
    }

    // ─── Голод clamp 0..5 ──────────────────────────────────────────────────

    [Fact]
    public void BuildPool_Hunger_ClampedTo5()
    {
        var c = MakeCharacter(
            attributes: new() { ["Ловкость"] = 1 },
            hunger: 99);

        var r = RollContext.BuildPool(c, "Ловкость", null, null, 0, 6);

        Assert.Equal(5, r.Hunger);
    }

    [Fact]
    public void BuildPool_Hunger_NegativeClampedTo0()
    {
        var c = MakeCharacter(
            attributes: new() { ["Ловкость"] = 1 },
            hunger: -3);

        var r = RollContext.BuildPool(c, "Ловкость", null, null, 0, 6);

        Assert.Equal(0, r.Hunger);
    }

    // ─── Ошибки валидации ──────────────────────────────────────────────────

    [Fact]
    public void BuildPool_NullCharacter_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => RollContext.BuildPool(null!, "Сила", null, null, 0, 6));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildPool_EmptyCharacteristic_Throws(string? input)
    {
        var c = MakeCharacter(attributes: new() { ["Сила"] = 1 });
        Assert.Throws<ArgumentException>(
            () => RollContext.BuildPool(c, input!, null, null, 0, 6));
    }

    [Fact]
    public void BuildPool_UnknownCharacteristic_Throws()
    {
        var c = MakeCharacter(attributes: new() { ["Сила"] = 3 });
        var ex = Assert.Throws<ArgumentException>(
            () => RollContext.BuildPool(c, "НетТакого", null, null, 0, 6));
        Assert.Contains("НетТакого", ex.Message);
    }

    [Fact]
    public void BuildPool_CharacteristicMissingFromCharacter_Throws()
    {
        var c = MakeCharacter(attributes: new());          // пусто
        var ex = Assert.Throws<ArgumentException>(
            () => RollContext.BuildPool(c, "Сила", null, null, 0, 6));
        Assert.Contains("Сила", ex.Message);
    }

    [Fact]
    public void BuildPool_UnknownAbility_Throws()
    {
        var c = MakeCharacter(
            attributes: new() { ["Сила"] = 2, ["ЧтоТо"] = 3 });
        var ex = Assert.Throws<ArgumentException>(
            () => RollContext.BuildPool(c, "Сила", "НетТакого", null, 0, 6));
        Assert.Contains("НетТакого", ex.Message);
    }

    [Fact]
    public void BuildPool_AbilityIsActuallyCharacteristic_Throws()
    {
        // Попытка передать характеристику как «навык» — это логическая ошибка.
        var c = MakeCharacter(
            attributes: new() { ["Сила"] = 2, ["Ловкость"] = 3 });
        var ex = Assert.Throws<ArgumentException>(
            () => RollContext.BuildPool(c, "Сила", "Ловкость", null, 0, 6));
        Assert.Contains("Ловкость", ex.Message);
    }

    [Fact]
    public void BuildPool_AbilityMissingFromCharacter_Throws()
    {
        var c = MakeCharacter(attributes: new() { ["Сила"] = 2 });
        var ex = Assert.Throws<ArgumentException>(
            () => RollContext.BuildPool(c, "Сила", "Скрытность", null, 0, 6));
        Assert.Contains("Скрытность", ex.Message);
    }

    [Fact]
    public void BuildPool_UnknownDiscipline_Throws()
    {
        var c = MakeCharacter(
            attributes: new() { ["Сила"] = 2 },
            disciplines: new() { ["Стремительность"] = 2 });
        var ex = Assert.Throws<ArgumentException>(
            () => RollContext.BuildPool(c, "Сила", null, "НетТакой", 0, 6));
        Assert.Contains("НетТакой", ex.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void BuildPool_InvalidDifficulty_Throws(int diff)
    {
        var c = MakeCharacter(attributes: new() { ["Сила"] = 2 });
        Assert.Throws<ArgumentException>(
            () => RollContext.BuildPool(c, "Сила", null, null, 0, diff));
    }

    [Fact]
    public void BuildPool_PoolLessThanOne_Throws()
    {
        var c = MakeCharacter(
            attributes: new() { ["Сила"] = 1, ["Драка"] = 1 });
        // 1 + 1 + бонус -5 = -3 → ошибка
        var ex = Assert.Throws<ArgumentException>(
            () => RollContext.BuildPool(c, "Сила", "Драка", null, -5, 6));
        Assert.Contains("пул", ex.Message.ToLower());
    }

    // ─── Хелпер ────────────────────────────────────────────────────────────

    private static VampireCharacter MakeCharacter(
        Dictionary<string, int>? attributes = null,
        Dictionary<string, int>? disciplines = null,
        Dictionary<string, string>? specializations = null,
        int hunger = 0)
    {
        var c = new VampireCharacter
        {
            Hunger = hunger,
            Attributes = attributes ?? new(),
            Disciplines = disciplines ?? new(),
            Specializations = specializations ?? new(),
        };
        return c;
    }
}
