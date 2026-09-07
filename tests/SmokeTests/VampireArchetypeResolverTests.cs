using System;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты для <see cref="VampireArchetypeResolver"/> (Roadmap #35) и каталога архетипов.
/// </summary>
public class VampireArchetypeResolverTests
{
    private static VampireCharacter MakeChar(string archetype) => new()
    {
        Archetype = archetype,
    };

    // ─── Каталог архетипов ──────────────────────────────────────────────

    [Fact]
    public void Archetypes_ContainsTenItems()
    {
        Assert.Equal(10, VampireParameterCatalog.Archetypes.Count);
    }

    [Theory]
    [InlineData("Автократ")]
    [InlineData("Бонвиван")]
    [InlineData("Борец")]
    [InlineData("Конформист")]
    [InlineData("Консерватор")]
    [InlineData("Преступник")]
    [InlineData("Мудрец")]
    [InlineData("Калека")]
    [InlineData("Реформатор")]
    [InlineData("Традиционалист")]
    public void IsValidArchetype_AcceptsAll(string name)
    {
        Assert.True(VampireParameterCatalog.IsValidArchetype(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Воин")]
    [InlineData("автократ")]      // регистр важен
    [InlineData("Автократ ")]     // пробел важен
    public void IsValidArchetype_RejectsOthers(string name)
    {
        Assert.False(VampireParameterCatalog.IsValidArchetype(name));
    }

    [Theory]
    [InlineData("Автократ", "добившись власти")]
    [InlineData("Бонвиван", "«отрываясь по полной»")]
    [InlineData("Борец", "победив в честном поединке")]
    [InlineData("Мудрец", "узнав новое")]
    [InlineData("Традиционалист", "придерживаясь традиций")]
    public void GetArchetypeRestoreCondition_ReturnsDescription(string name, string expectedContains)
    {
        var desc = VampireParameterCatalog.GetArchetypeRestoreCondition(name);
        Assert.Contains(expectedContains, desc);
    }

    [Fact]
    public void GetArchetypeRestoreCondition_Unknown_Empty()
    {
        Assert.Equal("", VampireParameterCatalog.GetArchetypeRestoreCondition("Неизвестный"));
    }

    // ─── CanRestore (без программного лимита) ───────────────────────────

    [Fact]
    public void CanRestore_NoArchetype()
    {
        var c = MakeChar("");
        var result = VampireArchetypeResolver.CanRestore(c, maxWillpower: 5, currentWillpower: 3);
        Assert.Equal(VampireArchetypeResolver.CanRestoreResult.NoArchetype, result);
    }

    [Fact]
    public void CanRestore_NullCharacter_NoArchetype()
    {
        var result = VampireArchetypeResolver.CanRestore(null, maxWillpower: 5, currentWillpower: 3);
        Assert.Equal(VampireArchetypeResolver.CanRestoreResult.NoArchetype, result);
    }

    [Fact]
    public void CanRestore_NoWillpower()
    {
        var c = MakeChar("Мудрец");
        var result = VampireArchetypeResolver.CanRestore(c, maxWillpower: 0, currentWillpower: 0);
        Assert.Equal(VampireArchetypeResolver.CanRestoreResult.NoWillpower, result);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(5, 6)]   // больше максимума — никогда не должно случиться, но на всякий
    [InlineData(10, 10)]
    public void CanRestore_AlreadyFull(int max, int current)
    {
        var c = MakeChar("Мудрец");
        var result = VampireArchetypeResolver.CanRestore(c, max, current);
        Assert.Equal(VampireArchetypeResolver.CanRestoreResult.AlreadyFull, result);
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(5, 3)]
    [InlineData(10, 9)]
    [InlineData(1, 0)]
    public void CanRestore_Allowed_BelowMax(int max, int current)
    {
        var c = MakeChar("Мудрец");
        var result = VampireArchetypeResolver.CanRestore(c, max, current);
        Assert.Equal(VampireArchetypeResolver.CanRestoreResult.Allowed, result);
    }

    [Fact]
        public void CanRestore_AllTenArchetypes_AreAllowed()
        {
            // Без программного лимита: любой заданный архетип даёт Allowed при незаполненной Воле.
            foreach (var name in VampireParameterCatalog.Archetypes)
            {
                var c = MakeChar(name);
                var result = VampireArchetypeResolver.CanRestore(c, maxWillpower: 5, currentWillpower: 3);
                Assert.Equal(VampireArchetypeResolver.CanRestoreResult.Allowed, result);
            }
        }

        // ─── Describe ───────────────────────────────────────────────────────

    [Fact]
    public void Describe_ReturnsNonEmptyForEachResult()
    {
        foreach (VampireArchetypeResolver.CanRestoreResult r in Enum.GetValues(typeof(VampireArchetypeResolver.CanRestoreResult)))
        {
            Assert.False(string.IsNullOrEmpty(VampireArchetypeResolver.Describe(r)),
                $"Describe пуст для {r}");
        }
    }

    [Theory]
    [InlineData(VampireArchetypeResolver.CanRestoreResult.NoArchetype, "архетип")]
    [InlineData(VampireArchetypeResolver.CanRestoreResult.AlreadyFull, "максимум")]
    [InlineData(VampireArchetypeResolver.CanRestoreResult.NoWillpower, "не определена")]
    public void Describe_ContainsMeaningfulWord(VampireArchetypeResolver.CanRestoreResult result, string expectedContains)
    {
        var desc = VampireArchetypeResolver.Describe(result);
        Assert.Contains(expectedContains, desc, StringComparison.OrdinalIgnoreCase);
    }
}
