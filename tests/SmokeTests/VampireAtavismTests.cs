using System;
using System.Linq;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты для <see cref="AtavismEntry"/> + отображение атавизмов в embed.
/// </summary>
public class VampireAtavismTests
{
    private static VampireCharacter NewCharacter(string clan = "Гангрел") => new()
    {
        CharacterId = Guid.NewGuid(),
        PlayerId = 42,
        PlayerName = "TestUser",
        Clan = clan,
        Generation = 13,
        Hunger = 1,
    };

    /// <summary>Генерирует заранее заданные числа при вызове Next.</summary>
    private sealed class FixedDiceRandom : RPBot.VtM.IRandom
    {
        private readonly System.Collections.Generic.Queue<int> _values;
        public FixedDiceRandom(params int[] values)
        {
            _values = new System.Collections.Generic.Queue<int>(values);
        }
        public int Next(int minValue, int maxValue) => _values.Count > 0 ? _values.Dequeue() : minValue;
    }

    // ── AtavismKind: метки и иконки ────────────────────────────────────

    [Fact]
    public void AtavismKind_LabelsAreLocalized()
    {
        Assert.Equal("Гангрел", KindLabelOf(AtavismKind.GangrelBeastFeature));
        Assert.Equal("Людоедство", KindLabelOf(AtavismKind.DiablerieCraving));
        Assert.Equal("Берсерк (Ротшрек)", KindLabelOf(AtavismKind.BerserkPanic));
        Assert.Equal("Клановый", KindLabelOf(AtavismKind.ClanSpecific));
        Assert.Equal("Неизв.", KindLabelOf(AtavismKind.Unknown));
    }

    // ── AtavismEntry.Describe ──────────────────────────────────────────

    [Fact]
    public void Entry_Describe_WithoutScene_OmitsScene()
    {
        var e = new AtavismEntry("Пробивающаяся шерсть", AtavismKind.GangrelBeastFeature, null);
        Assert.Contains("🐾", e.Describe());
        Assert.Contains("Пробивающаяся шерсть", e.Describe());
        Assert.Contains("Гангрел", e.Describe());
        Assert.DoesNotContain("сцена", e.Describe());
    }

    [Fact]
    public void Entry_Describe_WithScene_IncludesScene()
    {
        var e = new AtavismEntry("Звериный рык", AtavismKind.GangrelBeastFeature, "Сцена 3");
        Assert.Contains("сцена Сцена 3", e.Describe());
    }

    [Fact]
    public void Entry_Icon_DiffersPerKind()
    {
        Assert.Equal("🐾", new AtavismEntry("a", AtavismKind.GangrelBeastFeature, null).Icon);
        Assert.Equal("🩸", new AtavismEntry("a", AtavismKind.DiablerieCraving, null).Icon);
        Assert.Equal("🌑", new AtavismEntry("a", AtavismKind.BerserkPanic, null).Icon);
        Assert.Equal("🎭", new AtavismEntry("a", AtavismKind.ClanSpecific, null).Icon);
        Assert.Equal("❔", new AtavismEntry("a", AtavismKind.Unknown, null).Icon);
    }

    // ── VampireCharacter: новое поле, миграция из строк ───────────────

    [Fact]
    public void Character_NewEntriesField_IsInitializedEmpty()
    {
        var c = NewCharacter();
        Assert.NotNull(c.ActiveAtavismEntries);
        Assert.Empty(c.ActiveAtavismEntries);
    }

    [Fact]
    public void Character_LegacyList_StillInitializedEmpty()
    {
        var c = NewCharacter();
        Assert.NotNull(c.ActiveAtavisms);
        Assert.Empty(c.ActiveAtavisms);
    }

    // ── FrenzyResolver: пишет в оба списка ────────────────────────────

    [Fact]
    public void Resolver_GangrelUnleashed_PopulatesBothLists()
    {
        var c = NewCharacter("Гангрел");
        // Самоконтроль=3, Инстинкты пусто. Подаём все 1-цы → 0 успехов → Unleashed → атавизм.
        c.Virtues ??= new System.Collections.Generic.Dictionary<string, int>();
        c.Virtues[VampireParameterCatalog.VirtueSelfControl] = 3;
        var fake = new FixedDiceRandom(System.Linq.Enumerable.Repeat(1, 20).ToArray());
        VampireFrenzyResolver.Roll(c, VampireFrenzyResolver.FrenzyKind.Frenzy, 6, 0, fake, rollAtavism: true);

        Assert.Single(c.ActiveAtavisms);
        Assert.Single(c.ActiveAtavismEntries);
        Assert.Equal(c.ActiveAtavisms[0], c.ActiveAtavismEntries[0].Name);
        Assert.Equal(AtavismKind.GangrelBeastFeature, c.ActiveAtavismEntries[0].Kind);
    }

    [Fact]
    public void Resolver_Rotschreck_SetsBerserkKind()
    {
        var c = NewCharacter("Гангрел");
        c.Virtues ??= new System.Collections.Generic.Dictionary<string, int>();
        c.Virtues[VampireParameterCatalog.VirtueCourage] = 3;
        var fake = new FixedDiceRandom(System.Linq.Enumerable.Repeat(1, 20).ToArray());
        // Подсовываем все 1 → нет успехов, провал, переход в ярость с атавизмом.
        VampireFrenzyResolver.Roll(c, VampireFrenzyResolver.FrenzyKind.Rötschreck, 6, 0, fake, rollAtavism: true);

        if (c.ActiveAtavismEntries.Count > 0)
            Assert.Equal(AtavismKind.BerserkPanic, c.ActiveAtavismEntries[0].Kind);
    }

    [Fact]
    public void Resolver_NonGangrel_DoesNotPopulate()
    {
        var c = NewCharacter("Вентру");
        c.Virtues ??= new System.Collections.Generic.Dictionary<string, int>();
        c.Virtues[VampireParameterCatalog.VirtueSelfControl] = 3;
        var fake = new FixedDiceRandom(System.Linq.Enumerable.Repeat(1, 20).ToArray());
        VampireFrenzyResolver.Roll(c, VampireFrenzyResolver.FrenzyKind.Frenzy, 6, 0, fake, rollAtavism: true);
        Assert.Empty(c.ActiveAtavisms);
        Assert.Empty(c.ActiveAtavismEntries);
    }

    // ── FrenzyEmbed: отображение ──────────────────────────────────────

    [Fact]
    public void Embed_NoAtavisms_ShowsEmptyPlaceholder()
    {
        var c = NewCharacter();
        var text = AllText(VampireFrenzyEmbed.Build(c));
        Assert.Contains("Нет активных атавизмов", text);
    }

    [Fact]
    public void Embed_LegacyList_ShownAsUnknown()
    {
        var c = NewCharacter();
        c.ActiveAtavisms.Add("Какая-то черта");
        var text = AllText(VampireFrenzyEmbed.Build(c));
        Assert.Contains("Какая-то черта", text);
        Assert.Contains("Неизв.", text);
    }

    [Fact]
    public void Embed_EntriesList_ShownWithKind()
    {
        var c = NewCharacter();
        c.ActiveAtavismEntries.Add(new AtavismEntry("Удлинённые клыки", AtavismKind.GangrelBeastFeature, "Сцена 7"));
        var text = AllText(VampireFrenzyEmbed.Build(c));
        Assert.Contains("Удлинённые клыки", text);
        Assert.Contains("Гангрел", text);
        Assert.Contains("сцена Сцена 7", text);
    }

    [Fact]
    public void Embed_DiablerieCraving_ShowsBloodyIcon()
    {
        var c = NewCharacter("Вентру");
        c.ActiveAtavismEntries.Add(new AtavismEntry("Жажда диаблери", AtavismKind.DiablerieCraving, null));
        var text = AllText(VampireFrenzyEmbed.Build(c));
        Assert.Contains("🩸", text);
        Assert.Contains("Людоедство", text);
    }

    // ── FrenzyComponents: кнопка очистки ──────────────────────────────

    [Fact]
    public void Components_HasClearAtavismButton()
    {
        var c = NewCharacter();
        var mc = VampireFrenzyComponents.Build(c);
        var row = Assert.IsType<Discord.ActionRowComponent>(Assert.Single(mc.Components));
        var buttons = row.Components.OfType<Discord.ButtonComponent>().ToList();
        Assert.Contains(buttons, b => b.CustomId!.Contains(":clearatav:"));
        Assert.Equal(3, buttons.Count); // RollFrenzy / RollRotschreck / ClearAtavism
    }

    // ── Хелперы ────────────────────────────────────────────────────────

    private static string AllText(Discord.Embed embed) =>
        (embed.Description ?? "") + " "
        + string.Join("|", embed.Fields.Select(f => (f.Name ?? "") + "=" + (f.Value ?? "")));

    private static string KindLabelOf(AtavismKind kind) => kind switch
    {
        AtavismKind.GangrelBeastFeature => "Гангрел",
        AtavismKind.DiablerieCraving    => "Людоедство",
        AtavismKind.BerserkPanic        => "Берсерк (Ротшрек)",
        AtavismKind.ClanSpecific        => "Клановый",
        _                                => "Неизв.",
    };
}
