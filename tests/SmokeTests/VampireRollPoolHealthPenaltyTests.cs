using System.Collections.Generic;
using RPBot.SlashModules;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты на авто-штраф здоровья в пуле кубов из RollContext.BuildPool.
/// V20 стр. 287: штраф за раны вычитается из пула автоматически.
/// </summary>
public class VampireRollPoolHealthPenaltyTests
{
    private static dynamic Build(
        Dictionary<string, int> attributes,
        int hunger,
        int healthNonLethalDamage,
        int bonusDice,
        int difficulty,
        string ability,
        string attribute)
    {
        var character = new VampireCharacter
        {
            CharacterName = "Test",
            Hunger = hunger,
            Attributes = attributes,
            Abilities = new List<string> { ability },
            Disciplines = new Dictionary<string, int>(),
            Specializations = new Dictionary<string, string>(),
        };

        if (healthNonLethalDamage > 0)
        {
            character.Health = new HealthState(size: 7);
            character.Health.ApplyAggravated(healthNonLethalDamage);
        }

        return RollContext.BuildPool(
            character, attribute, ability,
            disciplineName: null,
            bonusDice: bonusDice,
            difficulty: difficulty,
            adHocSpecialization: null);
    }

    [Fact]
    public void PenaltyZero_HealthNotApplied()
    {
        var result = Build(
            attributes: new() { ["Ловкость"] = 3, ["Драка"] = 4 },
            hunger: 1,
            healthNonLethalDamage: 0,
            bonusDice: 0,
            difficulty: 6,
            ability: "Драка",
            attribute: "Ловкость");
        Assert.Equal(7, result.PoolSize); // 4 + 3
        Assert.Equal(0, result.HealthPenalty);
    }

    [Fact]
    public void PenaltyMinusOne_AppliedToPool()
    {
        var result = Build(
            attributes: new() { ["Ловкость"] = 3, ["Драка"] = 4 },
            hunger: 1,
            healthNonLethalDamage: 2, // 2 агравированных = «Легко ранен» = -1
            bonusDice: 0,
            difficulty: 6,
            ability: "Драка",
            attribute: "Ловкость");
        Assert.Equal(6, result.PoolSize); // 7 - 1
        Assert.Equal(-1, result.HealthPenalty);
    }

    [Fact]
    public void PenaltyMinusTwo_AppliedToPool()
    {
        var result = Build(
            attributes: new() { ["Сила"] = 2, ["Драка"] = 3 },
            hunger: 2,
            healthNonLethalDamage: 4, // «Серьёзно» / «Тяжело» = -2
            bonusDice: 1,
            difficulty: 6,
            ability: "Драка",
            attribute: "Сила");
        // 3 (навык) + 2 (хар) + 1 (явный бонус) - 2 (штраф здоровья) = 4
        Assert.Equal(4, result.PoolSize);
        Assert.Equal(-2, result.HealthPenalty);
    }

    [Fact]
    public void Penalty_WithExplicitBonus_BothShown()
    {
        var result = Build(
            attributes: new() { ["Ловкость"] = 3, ["Драка"] = 4 },
            hunger: 1,
            healthNonLethalDamage: 2, // -1
            bonusDice: 2,
            difficulty: 6,
            ability: "Драка",
            attribute: "Ловкость");
        // 4 + 3 + 2 - 1 = 8
        Assert.Equal(8, result.PoolSize);
        Assert.Equal(2, result.BonusDice);
        Assert.Equal(-1, result.HealthPenalty);
    }

    [Fact]
    public void NullHealth_NoPenalty()
    {
        var character = new VampireCharacter
        {
            CharacterName = "Test",
            Hunger = 1,
            Attributes = new Dictionary<string, int> { ["Ловкость"] = 3, ["Драка"] = 4 },
            Abilities = new List<string> { "Драка" },
            Disciplines = new Dictionary<string, int>(),
            Specializations = new Dictionary<string, string>(),
            Health = null,
        };

        var result = RollContext.BuildPool(
            character, "Ловкость", "Драка",
            disciplineName: null,
            bonusDice: 0,
            difficulty: 6,
            adHocSpecialization: null);

        Assert.Equal(7, result.PoolSize);
        Assert.Equal(0, result.HealthPenalty);
    }

    [Fact]
    public void PenaltyMakesPoolZeroOrNegative_Throws()
    {
        // Атрибуты и штраф такие, что пул уходит в минус — должно бросить.
        // При 5 агравированных TablePenalty = -5.
        var character = new VampireCharacter
        {
            CharacterName = "Test",
            Hunger = 1,
            Attributes = new Dictionary<string, int> { ["Ловкость"] = 1, ["Драка"] = 0 },
            Abilities = new List<string>(),
            Disciplines = new Dictionary<string, int>(),
            Specializations = new Dictionary<string, string>(),
            Health = new HealthState(size: 7),
        };
        character.Health.ApplyAggravated(5);

        Assert.Throws<System.ArgumentException>(() =>
            RollContext.BuildPool(
                character, "Ловкость", null,
                disciplineName: null,
                bonusDice: 0,
                difficulty: 6,
                adHocSpecialization: null));
    }
}
