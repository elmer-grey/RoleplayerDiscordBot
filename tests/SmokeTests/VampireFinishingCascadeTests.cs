using System;
using System.Linq;
using Discord;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты UI каскада Шага 5 «Последние штрихи» визарда VtM V20.
/// Покрывает 3-шаговый каскад: (1) категория → (2) подгруппа → (3) поле.
/// </summary>
public class VampireFinishingCascadeTests
{
    private static VampireCharacter NewDraft(string clan = "Вентру") => new()
    {
        CharacterId = Guid.NewGuid(),
        PlayerId = 42,
        PlayerName = "TestUser",
        Generation = 13,
        Hunger = 1,
        Clan = clan,
    };

    private static void SeedVirtues(VampireCharacter draft, int conscience = 1, int selfControl = 1, int courage = 1)
    {
        draft.Virtues[VampireParameterCatalog.VirtueConscience] = conscience;
        draft.Virtues[VampireParameterCatalog.VirtueSelfControl] = selfControl;
        draft.Virtues[VampireParameterCatalog.VirtueCourage] = courage;
    }

    /// <summary>Извлечь все SelectMenu из MessageComponent.</summary>
    private static System.Collections.Generic.List<SelectMenuComponent> SelectMenus(MessageComponent mc)
        => mc.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components.OfType<SelectMenuComponent>()).ToList();

    /// <summary>Извлечь все кнопки из MessageComponent.</summary>
    private static System.Collections.Generic.List<ButtonComponent> Buttons(MessageComponent mc)
        => mc.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components.OfType<ButtonComponent>()).ToList();

    // ── Step 1: Target ────────────────────────────────────────────────

    [Fact]
    public void Step_Target_ShowsSevenCategories()
    {
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(
            d, VampireFreebieCascadeStep.Target, target: null, subgroup: null);

        var menus = SelectMenus(comp);
        var targetMenu = Assert.Single(menus);

        // 7 категорий freebie: Attribute, Ability, Discipline, Background, Virtue, Humanity, Willpower.
        Assert.Equal(7, targetMenu.Options.Count);

        // Все опции имеют числовое value = (int)FreebieTarget.
        Assert.All(targetMenu.Options, o => Assert.True(int.TryParse(o.Value, out _), $"value={o.Value}"));
    }

    [Fact]
    public void Step_Target_AllCategoriesHaveValueAndLabel()
    {
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(
            d, VampireFreebieCascadeStep.Target);

        var menu = SelectMenus(comp).Single();
        Assert.All(menu.Options, o =>
        {
            Assert.False(string.IsNullOrWhiteSpace(o.Value));
            Assert.False(string.IsNullOrWhiteSpace(o.Label));
        });
    }

    // ── Step 2: Subgroup (только для Attribute/Ability) ──────────────

    [Fact]
    public void Step_Subgroup_Attribute_ShowsThreeSubgroups()
    {
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(
            d,
            VampireFreebieCascadeStep.Subgroup,
            VampireFinishingResolver.FreebieTarget.Attribute,
            subgroup: null);

        var menus = SelectMenus(comp);
        var subgroupMenu = Assert.Single(menus);

        // 3 подгруппы: Physical, Social, Mental.
        Assert.Equal(3, subgroupMenu.Options.Count);
        Assert.Contains(subgroupMenu.Options, o => o.Value == "Physical");
        Assert.Contains(subgroupMenu.Options, o => o.Value == "Social");
        Assert.Contains(subgroupMenu.Options, o => o.Value == "Mental");
    }

    [Fact]
    public void Step_Subgroup_Ability_ShowsThreeSubgroups()
    {
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(
            d,
            VampireFreebieCascadeStep.Subgroup,
            VampireFinishingResolver.FreebieTarget.Ability,
            subgroup: null);

        var menus = SelectMenus(comp);
        var subgroupMenu = Assert.Single(menus);

        // 3 подгруппы: Talents, Skills, Knowledges.
        Assert.Equal(3, subgroupMenu.Options.Count);
        Assert.Contains(subgroupMenu.Options, o => o.Value == "Talents");
        Assert.Contains(subgroupMenu.Options, o => o.Value == "Skills");
        Assert.Contains(subgroupMenu.Options, o => o.Value == "Knowledges");
    }

    [Fact]
    public void Step_Subgroup_Discipline_FallsBackToTarget()
    {
        // Для Discipline нет подгрупп — каскад должен показывать Step 1.
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(
            d,
            VampireFreebieCascadeStep.Subgroup,
            VampireFinishingResolver.FreebieTarget.Discipline,
            subgroup: null);

        var menu = SelectMenus(comp).Single();
        // 7 категорий — fallback на Step 1.
        Assert.Equal(7, menu.Options.Count);
    }

    [Fact]
    public void Step_Subgroup_WithoutTarget_FallsBackToTarget()
    {
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(
            d,
            VampireFreebieCascadeStep.Subgroup,
            target: null,
            subgroup: null);

        var menu = SelectMenus(comp).Single();
        Assert.Equal(7, menu.Options.Count);
    }

    // ── Step 3: Field ─────────────────────────────────────────────────

    [Fact]
    public void Step_Field_Attribute_Physical_ShowsThreeAttributes()
    {
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(
            d,
            VampireFreebieCascadeStep.Field,
            VampireFinishingResolver.FreebieTarget.Attribute,
            subgroup: "Physical");

        var menus = SelectMenus(comp);
        var fieldMenu = Assert.Single(menus);

        // 3 атрибута × только «+» (cur=0 → «−» не показывается) = 3 опции.
        Assert.Equal(3, fieldMenu.Options.Count);
        Assert.Contains(fieldMenu.Options, o => o.Value.StartsWith("+:") && o.Value.Contains("Сила"));
        Assert.Contains(fieldMenu.Options, o => o.Value.StartsWith("+:") && o.Value.Contains("Ловкость"));
        Assert.Contains(fieldMenu.Options, o => o.Value.StartsWith("+:") && o.Value.Contains("Выносливость"));
    }

    [Fact]
    public void Step_Field_Attribute_Physical_AfterIncrement_ShowsBothSigns()
    {
        // После +1 к атрибуту — у этого атрибута появляется «−»,
        // а у остальных (cur=0) — только «+».
        var d = NewDraft();
        SeedVirtues(d);
        d.Attributes["Сила"] = 1;

        var comp = VampireWizardComponents.BuildForFinishingStep(
            d,
            VampireFreebieCascadeStep.Field,
            VampireFinishingResolver.FreebieTarget.Attribute,
            subgroup: "Physical");

        var menu = SelectMenus(comp).Single();
        // Сила: + и − (cur=1) = 2.
        // Ловкость: только + (cur=0) = 1.
        // Выносливость: только + (cur=0) = 1.
        // Итого 4.
        Assert.Equal(4, menu.Options.Count);
        Assert.Contains(menu.Options, o => o.Value.StartsWith("+:") && o.Value.Contains("Сила"));
        Assert.Contains(menu.Options, o => o.Value.StartsWith("−:") && o.Value.Contains("Сила"));
        Assert.Contains(menu.Options, o => o.Value.StartsWith("+:") && o.Value.Contains("Ловкость"));
        Assert.Contains(menu.Options, o => o.Value.StartsWith("+:") && o.Value.Contains("Выносливость"));
    }

    [Fact]
    public void Step_Field_Ability_Talents_ShowsTenAbilities()
    {
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(
            d,
            VampireFreebieCascadeStep.Field,
            VampireFinishingResolver.FreebieTarget.Ability,
            subgroup: "Talents");

        var menus = SelectMenus(comp);
        var fieldMenu = Assert.Single(menus);

        // 10 талантов × только «+» (cur=0) = 10 опций.
        Assert.Equal(10, fieldMenu.Options.Count);
        Assert.Contains(fieldMenu.Options, o => o.Value.StartsWith("+:") && o.Value.Contains("Атлетика"));
        Assert.Contains(fieldMenu.Options, o => o.Value.StartsWith("+:") && o.Value.Contains("Эмпатия"));
    }

    [Fact]
    public void Step_Field_Discipline_ShowsAllSlots()
    {
        var d = NewDraft(); // Вентру: Доминирование, Стойкость, Величие.
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(
            d,
            VampireFreebieCascadeStep.Field,
            VampireFinishingResolver.FreebieTarget.Discipline,
            subgroup: null);

        var menus = SelectMenus(comp);
        var fieldMenu = Assert.Single(menus);

        // 3 слота × только «+» (cur=0) = 3 опции.
        Assert.Equal(3, fieldMenu.Options.Count);
        Assert.Contains(fieldMenu.Options, o => o.Value.Contains("Доминирование"));
    }

    [Theory]
    [InlineData(VampireFinishingResolver.FreebieTarget.Humanity, "Человечность")]
    [InlineData(VampireFinishingResolver.FreebieTarget.Willpower, "Воля")]
    public void Step_Field_HumanityOrWillpower_ShowsOnlySelected(
        VampireFinishingResolver.FreebieTarget target,
        string expectedLabel)
    {
        // Регрессия ревью: раньше AppendHumanityWillpowerOptions итерировал оба варианта
        // независимо от выбранного target. Теперь — только выбранный.
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(
            d,
            VampireFreebieCascadeStep.Field,
            target,
            subgroup: null);

        var fieldMenu = Assert.Single(SelectMenus(comp));
        var labels = fieldMenu.Options.Select(o => o.Label).ToList();
        Assert.Contains(labels, l => l.Contains(expectedLabel));
        // Другая категория не должна присутствовать.
        var other = target == VampireFinishingResolver.FreebieTarget.Humanity ? "Воля" : "Человечность";
        Assert.DoesNotContain(labels, l => l.Contains(other));
    }

    [Fact]
    public void Step_Target_HumanityAndWillpowerAreSeparate()
    {
        // Humanity и Willpower — две отдельные категории на Step 1.
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(d, VampireFreebieCascadeStep.Target);
        var menu = SelectMenus(comp).Single();
        Assert.Contains(menu.Options, o => o.Label.Contains("Человечность"));
        Assert.Contains(menu.Options, o => o.Label.Contains("Воля"));
    }

    // ── Кнопки навигации ─────────────────────────────────────────────

    [Fact]
    public void Step_Target_HasNoBackButton()
    {
        // На Step 1 кнопка «⬅ Назад к подгруппам/категориям» не нужна.
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(
            d, VampireFreebieCascadeStep.Target);

        var buttons = Buttons(comp);
        Assert.DoesNotContain(buttons, b => b.CustomId.Contains("finishing_back"));
    }

    [Fact]
    public void Step_Subgroup_HasBackToCategoriesButton()
    {
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(
            d,
            VampireFreebieCascadeStep.Subgroup,
            VampireFinishingResolver.FreebieTarget.Attribute);

        var buttons = Buttons(comp);
        Assert.Contains(buttons, b => b.CustomId.Contains("finishing_back_from_subgroup"));
    }

    [Fact]
    public void Step_Field_HasBackToSubgroupsButton()
    {
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(
            d,
            VampireFreebieCascadeStep.Field,
            VampireFinishingResolver.FreebieTarget.Attribute,
            "Physical");

        var buttons = Buttons(comp);
        Assert.Contains(buttons, b => b.CustomId.Contains("finishing_back_from_field"));
    }

    [Fact]
    public void AllSteps_HaveCommonNavigationButtons()
    {
        var d = NewDraft();
        SeedVirtues(d);

        foreach (var step in new[] {
            VampireFreebieCascadeStep.Target,
            VampireFreebieCascadeStep.Subgroup,
            VampireFreebieCascadeStep.Field,
        })
        {
            var comp = VampireWizardComponents.BuildForFinishingStep(
                d, step, VampireFinishingResolver.FreebieTarget.Attribute, "Physical");

            var buttons = Buttons(comp);
            Assert.Contains(buttons, b => b.CustomId.Contains("back_to_advantages"));
            Assert.Contains(buttons, b => b.CustomId.Contains("finishing_reset"));
        }
    }

    // ── Step 1: Target — все 7 категорий имеют разный лейбл ──────────

    [Fact]
    public void Step_Target_LabelsAreUnique()
    {
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(d, VampireFreebieCascadeStep.Target);
        var menu = SelectMenus(comp).Single();
        var labels = menu.Options.Select(o => o.Label).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    // ── Защита UI: «Готово → лист» disabled пока Шаг 5 не завершён ───

    [Fact]
    public void Step_Target_DoneButtonDisabled_WhenPoolNotEmpty()
    {
        // B4: кнопка «Готово → лист» должна быть disabled, пока пул не пуст.
        var d = NewDraft();
        SeedVirtues(d);
        // Пул полный (15/15), ни одна трата не сделана → done button disabled.

        var comp = VampireWizardComponents.BuildForFinishingStep(d, VampireFreebieCascadeStep.Target);
        var buttons = Buttons(comp);
        var done = Assert.Single(buttons, b => b.CustomId.Contains("finishing_done"));
        Assert.True(done.IsDisabled, "«Готово → лист» должен быть disabled при ост. 15 свободных.");
    }

    [Fact]
    public void Step_Target_DoneButtonDisabled_AfterPartialSpend()
    {
        // Потратили 5 из 15 → кнопка всё ещё disabled.
        var d = NewDraft();
        SeedVirtues(d);
        Assert.True(VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Сила", out _).IsSuccess);

        var comp = VampireWizardComponents.BuildForFinishingStep(d, VampireFreebieCascadeStep.Target);
        var done = Buttons(comp).Single(b => b.CustomId.Contains("finishing_done"));
        Assert.True(done.IsDisabled);
    }

    [Fact]
    public void Step_Target_DoneButtonEnabled_AfterPoolExhausted()
    {
        // Пул исчерпан → кнопка активна.
        var d = NewDraft();
        SeedVirtues(d);
        // 3 × Attribute по 5 = 15, пул = 0.
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Сила", out _);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Ловкость", out _);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Выносливость", out _);

        var comp = VampireWizardComponents.BuildForFinishingStep(d, VampireFreebieCascadeStep.Target);
        var done = Buttons(comp).Single(b => b.CustomId.Contains("finishing_done"));
        Assert.False(done.IsDisabled);
    }

    [Fact]
    public void Step_Target_DoneButtonEnabled_AfterUserConfirm()
    {
        // Игрок явно подтвердил, не потратив всё → кнопка активна.
        var d = NewDraft();
        SeedVirtues(d);
        VampireFinishingResolver.ConfirmStep5(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(d, VampireFreebieCascadeStep.Target);
        var done = Buttons(comp).Single(b => b.CustomId.Contains("finishing_done"));
        Assert.False(done.IsDisabled);
    }

    [Fact]
    public void Step_Target_FinalizeButtonVisible_WhenPoolNotEmptyAndNotFinalized()
    {
        // Кнопка «⚠ Подтвердить и заморозить» появляется только когда пул не пуст и Шаг 5 не подтверждён.
        var d = NewDraft();
        SeedVirtues(d);

        var comp = VampireWizardComponents.BuildForFinishingStep(d, VampireFreebieCascadeStep.Target);
        var buttons = Buttons(comp);
        Assert.Contains(buttons, b => b.CustomId.Contains("finishing_finalize") && !b.IsDisabled);
    }

    [Fact]
    public void Step_Target_FinalizeButtonHidden_WhenPoolExhausted()
    {
        // Пул исчерпан — кнопка «Подтвердить и заморозить» не нужна.
        var d = NewDraft();
        SeedVirtues(d);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Сила", out _);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Ловкость", out _);
        VampireFinishingResolver.AllocateFreebie(d, VampireFinishingResolver.FreebieTarget.Attribute, "Выносливость", out _);

        var comp = VampireWizardComponents.BuildForFinishingStep(d, VampireFreebieCascadeStep.Target);
        var buttons = Buttons(comp);
        Assert.DoesNotContain(buttons, b => b.CustomId.Contains("finishing_finalize"));
    }
}
