using System.Text;
using System.Linq;
using RPBot.VtM;
using Xunit;
using Xunit.Abstractions;

namespace SmokeTests;

/// <summary>
/// Один интеграционный тест, который проходит весь путь создания
/// персонажа VtM V20 — Шаг 1 → Шаг 5 — со всеми правилами V20:
/// клановые изъяны, приоритеты, дисциплины, фоны, добродетели,
/// Merits/Flaws с интеграцией в freebie-пул (15 base + Flaws − Merits,
/// ограничение Flaws на 7 пунктов, пул до 22), специализации.
/// Проверяет, что вся текущая реализация работает как единое целое.
/// </summary>
/// <remarks>
/// Три сценария — три разных клана с разными концепциями:
/// <list type="bullet">
/// <item>Сц. A — Каитиф «Детектив-одиночка», MentalPrimary, пул добит до 0.</item>
/// <item>Сц. B — Носферату «Шпион подземелий», PhysicalPrimary, выход через ConfirmStep5.</item>
/// <item>Сц. C — Малкавиан «Пророк безумия», MentalPrimary, пул добит до 0.</item>
/// </list>
/// Каждый персонаж имеет собственные дисциплины, фоны, добродетели и
/// наборы Merits/Flaws — мы сознательно НЕ копируем шаблон сценариев
/// из старого теста (Тремер + Вентру), чтобы покрыть больше кода.
/// </remarks>
public class VtMFullCreationTests
{
    private readonly ITestOutputHelper _out;
    public VtMFullCreationTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void FullCreation_ThreeScenarios_AllSteps_WithMeritsAndFlaws()
    {
        try { System.IO.File.Delete(DumpFilePath); } catch { }

        var log = new StringBuilder();
        void Section(string t) { log.AppendLine().AppendLine("═══ " + t + " ═══"); }
        void Step(string a, string r) { log.AppendLine("▶ " + a + " → " + r); }
        void Info(string s) { log.AppendLine(s); _out.WriteLine(s); }

        BuildCaitiffDetective(log, Section, Step, Info);
        BuildNosferatuSpy(log, Section, Step, Info);
        BuildMalkavianOracle(log, Section, Step, Info);

        Section("СВОДКА ТЕСТА");
        Info(log.ToString());
    }

    // ════════════════════════════════════════════════════════════════════
    // СЦЕНАРИЙ A — Каитиф «Детектив-одиночка» (MentalPrimary, пул добит)
    // ════════════════════════════════════════════════════════════════════

    private void BuildCaitiffDetective(
        StringBuilder log, System.Action<string> Section, System.Action<string, string> Step,
        System.Action<string> Info)
    {
        Section("СЦЕНАРИЙ A — КАИТИФ, «Детектив-одиночка» (пул добит)");
        var draft = new VampireCharacter { CharacterId = Guid.NewGuid() };

        VampireCreateResolver.ApplyConceptField(draft, "concept", "Частный детектив, расследующий пропажу смертных");
        VampireCreateResolver.ApplyConceptField(draft, "clan", "Каитиф");
        VampireCreateResolver.ApplyConceptField(draft, "nature", "Скептик");
        VampireCreateResolver.ApplyConceptField(draft, "demeanor", "Бывалый полицейский");
        draft.PlayerName = "Антон";
        draft.CharacterName = "Кейн Марлоу";
        draft.Generation = 12;
        draft.Sire = "неизвестный";
        Step("Шаг 1",
             $"Concept='{draft.Concept}', Clan='{draft.Clan}', Generation={draft.Generation}, Weakness='{draft.Weakness}'");
        Assert.True(VampireCreateResolver.IsConceptComplete(draft));

        // ШАГ 2 — MentalPrimary (Мент 7 / Физ 5 / Соц 3). Каждая хар-ка стартует с 1.
        // Итог:
        //   Mental 7:  Восприятие 4 (1+3), Интеллект 3 (1+2), Смекалка 3 (1+2).
        //   Physical 5: Сила 3 (1+2), Ловкость 3 (1+2), Выносливость 2 (1+1).
        //   Social 3:   Обаяние 2 (1+1), Манипуляция 2 (1+1), Привлекательность 2 (1+1).
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.MentalPrimary);
        VampireAttributesResolver.ApplyClanRules(draft);
        Enumerable.Range(0, 3).ToList().ForEach(_ => VampireAttributesResolver.Increment(draft, "Восприятие"));
        Enumerable.Range(0, 2).ToList().ForEach(_ => VampireAttributesResolver.Increment(draft, "Интеллект"));
        Enumerable.Range(0, 2).ToList().ForEach(_ => VampireAttributesResolver.Increment(draft, "Смекалка"));
        Enumerable.Range(0, 2).ToList().ForEach(_ => VampireAttributesResolver.Increment(draft, "Сила"));
        Enumerable.Range(0, 2).ToList().ForEach(_ => VampireAttributesResolver.Increment(draft, "Ловкость"));
        VampireAttributesResolver.Increment(draft, "Выносливость");
        VampireAttributesResolver.Increment(draft, "Обаяние");
        VampireAttributesResolver.Increment(draft, "Манипуляция");
        VampireAttributesResolver.Increment(draft, "Привлекательность");
        Step("Шаг 2", $"IsComplete={VampireAttributesResolver.IsAttributesComplete(draft)}");
        Assert.True(VampireAttributesResolver.IsAttributesComplete(draft));

        // ШАГ 3 — TalentsPrimary (Таланты 13 / Навыки 9 / Знания 5).
        // Talents (13):   Бдительность 3, Хитрость 3, Запугивание 3,
        //                 Красноречие 2, Уличное чутьё 2.
        // Skills (9):     Скрытность 3, Стрельба 3, Вождение 2, Ремесло 1.
        // Knowledges (5): Расследование 2, Медицина 2, Юриспруденция 1.
        VampireAbilitiesResolver.SetPriority(draft, VampireAbilityPriority.TalentsPrimary);
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Бдительность").IsSuccess));
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Хитрость").IsSuccess));
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Запугивание").IsSuccess));
        Enumerable.Range(0, 2).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Красноречие").IsSuccess));
        Enumerable.Range(0, 2).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Уличное чутьё").IsSuccess));
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Скрытность").IsSuccess));
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Стрельба").IsSuccess));
        Enumerable.Range(0, 2).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Вождение").IsSuccess));
        Assert.True(VampireAbilitiesResolver.Increment(draft, "Ремесло").IsSuccess);
        Enumerable.Range(0, 2).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Расследование").IsSuccess));
        Enumerable.Range(0, 2).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Медицина").IsSuccess));
        Assert.True(VampireAbilitiesResolver.Increment(draft, "Юриспруденция").IsSuccess);
        Step("Шаг 3", $"IsComplete={VampireAbilitiesResolver.IsAbilitiesComplete(draft)}");
        Assert.True(VampireAbilitiesResolver.IsAbilitiesComplete(draft));

        var earlySpec = VampireAbilitiesResolver.SetSpecialization(draft, "Бдительность", "опасность");
        Assert.False(earlySpec.IsSuccess, "До Шага 5 специализация должна быть отклонена.");

        Section("ШАГ 4 — ПРЕИМУЩЕСТВА (сц. A, Каитиф)");

        // 4.1 — У Каитифа нет клановых дисциплин, выбираем свободно:
        //   Бдительность 1, Стойкость 1, Прорицание 1.
        // Сначала именуем 3 пустых слота «Дисциплина N», затем инкрементируем.
        Assert.True(VampireAdvantagesResolver.RenameCaitiffDiscipline(draft, "Дисциплина 1", "Бдительность").IsSuccess);
        Assert.True(VampireAdvantagesResolver.RenameCaitiffDiscipline(draft, "Дисциплина 2", "Стойкость").IsSuccess);
        Assert.True(VampireAdvantagesResolver.RenameCaitiffDiscipline(draft, "Дисциплина 3", "Прорицание").IsSuccess);
        Assert.True(VampireAdvantagesResolver.IncrementDiscipline(draft, "Бдительность").IsSuccess);
        Assert.True(VampireAdvantagesResolver.IncrementDiscipline(draft, "Стойкость").IsSuccess);
        Assert.True(VampireAdvantagesResolver.IncrementDiscipline(draft, "Прорицание").IsSuccess);
        Step("Шаг 4.1", $"spent={VampireAdvantagesResolver.TotalDisciplineSpent(draft)}/3");
        Assert.True(VampireAdvantagesResolver.IsDisciplinesComplete(draft));

        // 4.2 — ФАКТЫ БИОГРАФИИ (пул 5).
        //   Связи 2, Наставник 1, Архив 1, Влияние 1.
        VampireAdvantagesResolver.AddBackground(draft, "Связи");
        VampireAdvantagesResolver.AddBackground(draft, "Наставник");
        VampireAdvantagesResolver.AddBackground(draft, "Архив");
        VampireAdvantagesResolver.AddBackground(draft, "Влияние");
        VampireAdvantagesResolver.IncrementBackground(draft, "Связи");   // 1→2
        Step("Шаг 4.2", $"spent={VampireAdvantagesResolver.TotalBackgroundSpent(draft)}/5");
        Assert.True(VampireAdvantagesResolver.IsBackgroundsComplete(draft));

        // 4.3 — ДОБРОДЕТЕЛИ (база 1/1/1 + пул 7).
        // Conscience 3 (1+2), SelfControl 4 (1+3), Courage 3 (1+2) — потрачено 7.
        VampireAdvantagesResolver.SeedVirtues(draft);
        Enumerable.Range(0, 2).ToList().ForEach(_ =>
            VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueConscience));
        Enumerable.Range(0, 3).ToList().ForEach(_ =>
            VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueSelfControl));
        Enumerable.Range(0, 2).ToList().ForEach(_ =>
            VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueCourage));
        Step("Шаг 4.3", $"spent={VampireAdvantagesResolver.TotalVirtueSpent(draft)}/7");
        Assert.True(VampireAdvantagesResolver.IsVirtuesComplete(draft));

        Section("ШАГ 5 — ДОВОДКА (сценарий A)");
        draft.Hunger = 1;
        VampireFinishingResolver.EnsureHealth(draft);

        // 5.1 — Flaws 4: Мягкий ум (1) + Забывчивость (1) + Ночные кошмары (2) = 4.
        Assert.True(VampireMeritsFlawsResolver.AddFlawFromCatalog(draft, "Мягкий ум").IsSuccess);
        Assert.True(VampireMeritsFlawsResolver.AddFlawFromCatalog(draft, "Забывчивость").IsSuccess);
        Assert.True(VampireMeritsFlawsResolver.AddFlawFromCatalog(draft, "Ночные кошмары").IsSuccess);
        Step("5.1 Flaws", $"cost={VampireMeritsFlawsResolver.FlawsCost(draft)}, " +
                            $"EffectivePool={VampireFinishingResolver.EffectiveFreebiePool(draft)}");

        // 5.2 — Merits 5: Концентрация (2) + Бдительный ум (1) + Эйдетическая память (2).
        Assert.True(VampireMeritsFlawsResolver.AddMeritFromCatalog(draft, "Концентрация").IsSuccess);
        Assert.True(VampireMeritsFlawsResolver.AddMeritFromCatalog(draft, "Бдительный ум").IsSuccess);
        Assert.True(VampireMeritsFlawsResolver.AddMeritFromCatalog(draft, "Эйдетическая память").IsSuccess);
        Step("5.2 Merits", $"MeritsCost={VampireMeritsFlawsResolver.MeritsCost(draft)}, " +
                            $"EffectivePool={VampireFinishingResolver.EffectiveFreebiePool(draft)}");
        Assert.Equal(5, VampireMeritsFlawsResolver.MeritsCost(draft));

        var pool = VampireFinishingResolver.EffectiveFreebiePool(draft);
        Step("5.3 Freebie старт (сц. A)", $"EffectivePool={pool}");

        Assert.Equal(4, draft.Backgrounds.Count);
        Assert.Equal(0, draft.FreebieBackgrounds.Count);

        // Связи 2→3 (+1), Наставник 1→2 (+1), Архив 1→2 (+1) = 3 BG-пункта.
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Background, "Связи", out _);   // 2→3
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Background, "Наставник", out _); // 1→2
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Background, "Архив", out _);    // 1→2

        // Расследование 2→4 (+2) для специализации «улики».
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Ability, "Расследование", out _);
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Ability, "Расследование", out _);

        // Восприятие 4→5 (cost 5, атрибут) для специализации «слух».
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Attribute, "Восприятие", out _);

        // Промежуточный итог: 1+1+1+2+2+5 = 12. Осталось добить ещё 2.
        var remaining = VampireFinishingResolver.RemainingFreebies(draft);
        Step("5.3 Freebie промежуточно (сц. A)",
             $"Consumed={VampireFinishingResolver.ConsumedFreebies(draft)}, Remaining={remaining}");

        // Добиваем Бдительность 3→4 (cost 1) для специализации «опасность»
        // и Самоконтроль 4→5 (cost 2). Итого +3 — пул добит.
        if (remaining >= 1)
        {
            VampireFinishingResolver.AllocateFreebie(draft,
                VampireFinishingResolver.FreebieTarget.Ability, "Бдительность", out _);
        }
        remaining = VampireFinishingResolver.RemainingFreebies(draft);
        if (remaining >= 2)
        {
            VampireFinishingResolver.AllocateFreebie(draft,
                VampireFinishingResolver.FreebieTarget.Virtue,
                VampireParameterCatalog.VirtueSelfControl, out _);
        }

        Step("5.3 Freebie итог (сц. A)",
             $"Consumed={VampireFinishingResolver.ConsumedFreebies(draft)}, " +
             $"Remaining={VampireFinishingResolver.RemainingFreebies(draft)}, " +
             $"FreebiesExhausted={VampireFinishingResolver.FreebiesExhausted(draft)}, " +
             $"SpecializationsAllowed={VampireFinishingResolver.SpecializationsAllowed(draft)}");
        Assert.True(VampireFinishingResolver.FreebiesExhausted(draft),
            "Сценарий A должен добить freebie до 0.");
        Assert.True(VampireFinishingResolver.SpecializationsAllowed(draft));
        Assert.Equal(0, VampireFinishingResolver.RemainingFreebies(draft));

        var totalBgs = draft.Backgrounds.Keys.Union(draft.FreebieBackgrounds.Keys).Count();
        Assert.True(totalBgs <= 6, $"BG превысили лимит 6: {totalBgs}");

        // Специализации.
        Assert.True(VampireAbilitiesResolver.SetSpecialization(draft, "Расследование", "улики").IsSuccess);
        Assert.True(VampireAbilitiesResolver.SetSpecialization(draft, "Восприятие", "слух").IsSuccess);
        Assert.True(VampireAbilitiesResolver.SetSpecialization(draft, "Бдительность", "опасность").IsSuccess);

        Section("ПРОИЗВОДНЫЕ + ЛИСТ (сценарий A)");
        Info($"Humanity={VampireFinishingResolver.ComputeHumanity(draft)}, " +
             $"Willpower={VampireFinishingResolver.ComputeWillpower(draft)}, " +
             $"Hunger={draft.Hunger}, Weakness='{draft.Weakness}'");

        var embed = VampireSheetEmbed.Build(draft);
        Assert.NotNull(embed);
        Assert.True(embed.Length <= 6000, $"embed слишком длинный: {embed.Length}");
        DumpSheet("ЛИСТ A — Каитиф «Детектив-одиночка» (пул добит)", embed);
    }

    // ════════════════════════════════════════════════════════════════════
    // СЦЕНАРИЙ B — Носферату «Шпион подземелий» (PhysicalPrimary, ConfirmStep5)
    // ════════════════════════════════════════════════════════════════════

    private void BuildNosferatuSpy(
        StringBuilder log, System.Action<string> Section, System.Action<string, string> Step,
        System.Action<string> Info)
    {
        Section("СЦЕНАРИЙ B — НОСФЕРАТУ, «Шпион подземелий» (выход через подтверждение)");
        var draft = new VampireCharacter { CharacterId = Guid.NewGuid() };
        VampireCreateResolver.ApplyConceptField(draft, "concept", "Информатор-крыса, живёт в канализации");
        VampireCreateResolver.ApplyConceptField(draft, "clan", "Носферату");
        VampireCreateResolver.ApplyConceptField(draft, "nature", "Животное");
        VampireCreateResolver.ApplyConceptField(draft, "demeanor", "Безобидный бродяга");
        draft.PlayerName = "Юлия";
        draft.CharacterName = "Гносс";
        draft.Generation = 11;
        draft.Sire = "Мастер Заг";
        draft.Hunger = 1;
        VampireFinishingResolver.EnsureHealth(draft);
        Step("Шаг 1",
             $"Concept='{draft.Concept}', Clan='{draft.Clan}', Generation={draft.Generation}, Weakness='{draft.Weakness}'");
        Assert.True(VampireCreateResolver.IsConceptComplete(draft));

        // ШАГ 2 — PhysicalSecondary (Физ 7 / Ментал 5 / Соц 3).
        // У Носферату Привлекательность жёстко = 0, поэтому Social нужно уложить в 3
        // очка между Обаянием и Манипуляцией.
        //   Physical 7: Сила 4 (1+3), Ловкость 4 (1+3), Выносливость 2 (1+1).
        //   Social 3:   Обаяние 2 (1+1), Манипуляция 3 (1+2), Привлекательность 0.
        //   Mental 5:   Восприятие 3 (1+2), Интеллект 3 (1+2), Смекалка 2 (1+1).
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.PhysicalSecondary);
        VampireAttributesResolver.ApplyClanRules(draft);
        Enumerable.Range(0, 3).ToList().ForEach(_ => VampireAttributesResolver.Increment(draft, "Сила"));
        Enumerable.Range(0, 3).ToList().ForEach(_ => VampireAttributesResolver.Increment(draft, "Ловкость"));
        VampireAttributesResolver.Increment(draft, "Выносливость");
        VampireAttributesResolver.Increment(draft, "Обаяние");
        Enumerable.Range(0, 2).ToList().ForEach(_ => VampireAttributesResolver.Increment(draft, "Манипуляция"));
        // Привлекательность не инкрементируем — у Носферату клановое правило держит 0.
        Enumerable.Range(0, 2).ToList().ForEach(_ => VampireAttributesResolver.Increment(draft, "Восприятие"));
        Enumerable.Range(0, 2).ToList().ForEach(_ => VampireAttributesResolver.Increment(draft, "Интеллект"));
        VampireAttributesResolver.Increment(draft, "Смекалка");
        Assert.True(VampireAttributesResolver.IsAttributesComplete(draft));

        // ШАГ 3 — SkillsPrimary (Навыки 13 / Таланты 9 / Знания 5).
        // Skills (13):     Скрытность 3, Воровство 3, Выживание 3, Вождение 2,
        //                 Стрельба 1, Ремесло 1.
        // Talents (9):     Бдительность 3, Атлетика 3, Запугивание 3.
        // Knowledges (5):  Политика 2, Оккультизм 1, Электроника 1, Информатика 1.
        VampireAbilitiesResolver.SetPriority(draft, VampireAbilityPriority.SkillsPrimary);
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Скрытность").IsSuccess));
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Воровство").IsSuccess));
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Выживание").IsSuccess));
        Enumerable.Range(0, 2).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Вождение").IsSuccess));
        Assert.True(VampireAbilitiesResolver.Increment(draft, "Стрельба").IsSuccess);
        Assert.True(VampireAbilitiesResolver.Increment(draft, "Ремесло").IsSuccess);
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Бдительность").IsSuccess));
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Атлетика").IsSuccess));
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Запугивание").IsSuccess));
        Enumerable.Range(0, 2).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Политика").IsSuccess));
        Assert.True(VampireAbilitiesResolver.Increment(draft, "Оккультизм").IsSuccess);
        Assert.True(VampireAbilitiesResolver.Increment(draft, "Информатика").IsSuccess);
        Assert.True(VampireAbilitiesResolver.Increment(draft, "Электроника").IsSuccess);
        Assert.True(VampireAbilitiesResolver.IsAbilitiesComplete(draft));

        // ШАГ 4 — Носферату: Анимализм, Сокрытие, Мощь.
        VampireAdvantagesResolver.IncrementDiscipline(draft, "Анимализм");
        VampireAdvantagesResolver.IncrementDiscipline(draft, "Сокрытие");
        VampireAdvantagesResolver.IncrementDiscipline(draft, "Мощь");
        Assert.True(VampireAdvantagesResolver.IsDisciplinesComplete(draft));

        // 4.2 — BG (пул 5): Слуги 2 (крысы), Связи 1, Стая 1, Хеймс 1.
        VampireAdvantagesResolver.AddBackground(draft, "Слуги");
        VampireAdvantagesResolver.AddBackground(draft, "Связи");
        VampireAdvantagesResolver.AddBackground(draft, "Стая");
        VampireAdvantagesResolver.AddBackground(draft, "Хеймс");
        VampireAdvantagesResolver.IncrementBackground(draft, "Слуги");   // 1→2
        Assert.True(VampireAdvantagesResolver.IsBackgroundsComplete(draft));

        // 4.3 — Добродетели (пул 7): Совесть 2, Самоконтроль 4, Смелость 4.
        VampireAdvantagesResolver.SeedVirtues(draft);
        VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueConscience);
        Enumerable.Range(0, 3).ToList().ForEach(_ =>
            VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueSelfControl));
        Enumerable.Range(0, 3).ToList().ForEach(_ =>
            VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueCourage));
        Assert.True(VampireAdvantagesResolver.IsVirtuesComplete(draft));

        // ШАГ 5 — Flaws (4 пункта) + Merits (3 пункта).
        // Flaws: Забывчивость (1) + Мягкий ум (1) + Некрасивый (1) + Хромота (1) — но
        // Хромоты нет в каталоге, берём «Медленный рефлекс» (1) — итого 4.
        Assert.True(VampireMeritsFlawsResolver.AddFlawFromCatalog(draft, "Забывчивость").IsSuccess);
        Assert.True(VampireMeritsFlawsResolver.AddFlawFromCatalog(draft, "Мягкий ум").IsSuccess);
        Assert.True(VampireMeritsFlawsResolver.AddFlawFromCatalog(draft, "Некрасивый").IsSuccess);
        Assert.True(VampireMeritsFlawsResolver.AddFlawFromCatalog(draft, "Медленный рефлекс").IsSuccess);
        Assert.True(VampireMeritsFlawsResolver.AddMeritFromCatalog(draft, "Бдительный ум").IsSuccess);
        Assert.True(VampireMeritsFlawsResolver.AddMeritFromCatalog(draft, "Концентрация").IsSuccess);
        Step("Шаг 5: пул после Merits/Flaws (сц. B)",
             $"Flaws={VampireMeritsFlawsResolver.FlawsCost(draft)}, " +
             $"Merits={VampireMeritsFlawsResolver.MeritsCost(draft)}, " +
             $"EP={VampireFinishingResolver.EffectiveFreebiePool(draft)}");
        // 15 + 4 − 3 = 16.
        Assert.Equal(16, VampireFinishingResolver.EffectiveFreebiePool(draft));

        // 5.3 — Тратим 15 пунктов, оставляя 1.
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Ability, "Скрытность", out _);
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Background, "Слуги", out _);
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Background, "Стая", out _);
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Background, "Хеймс", out _);
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Ability, "Бдительность", out _);
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Attribute, "Ловкость", out _);
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Virtue,
            VampireParameterCatalog.VirtueSelfControl, out _);
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Background, "Связи", out _);
        Step("Шаг 5: пул до подтверждения (сц. B)",
             $"Consumed={VampireFinishingResolver.ConsumedFreebies(draft)}, " +
             $"Remaining={VampireFinishingResolver.RemainingFreebies(draft)}, " +
             $"FreebiesExhausted={VampireFinishingResolver.FreebiesExhausted(draft)}, " +
             $"SpecializationsAllowed={VampireFinishingResolver.SpecializationsAllowed(draft)}");
        Assert.Equal(15, VampireFinishingResolver.ConsumedFreebies(draft));
        Assert.Equal(1, VampireFinishingResolver.RemainingFreebies(draft));
        Assert.False(VampireFinishingResolver.FreebiesExhausted(draft));
        Assert.False(VampireFinishingResolver.SpecializationsAllowed(draft),
            "Специализации запрещены, пока пул не пуст и Шаг 5 не подтверждён.");

        var bEarly = VampireAbilitiesResolver.SetSpecialization(draft, "Скрытность", "городские крыши");
        Assert.False(bEarly.IsSuccess);

        VampireFinishingResolver.ConfirmStep5(draft);
        Step("Сценарий B: подтверждение через диалог",
             $"IsStep5Finalized={VampireFinishingResolver.IsStep5Finalized(draft)}, " +
             $"SpecializationsAllowed={VampireFinishingResolver.SpecializationsAllowed(draft)}");
        Assert.True(VampireFinishingResolver.IsStep5Finalized(draft));
        Assert.True(VampireFinishingResolver.SpecializationsAllowed(draft));

        Assert.True(VampireAbilitiesResolver.SetSpecialization(draft, "Скрытность", "городские крыши").IsSuccess);
        Assert.True(VampireAbilitiesResolver.SetSpecialization(draft, "Бдительность", "слух").IsSuccess);
        Assert.True(VampireAbilitiesResolver.SetSpecialization(draft, "Ловкость", "акробатика").IsSuccess);

        var totalBgs = draft.Backgrounds.Keys.Union(draft.FreebieBackgrounds.Keys).Count();
        Assert.True(totalBgs <= 6, $"BG превысили лимит 6: {totalBgs}");

        Section("ПРОИЗВОДНЫЕ + ЛИСТ (сценарий B)");
        Info($"Humanity={VampireFinishingResolver.ComputeHumanity(draft)}, " +
             $"Willpower={VampireFinishingResolver.ComputeWillpower(draft)}, " +
             $"Hunger={draft.Hunger}, Weakness='{draft.Weakness}'");

        var embed = VampireSheetEmbed.Build(draft);
        Assert.NotNull(embed);
        Assert.True(embed.Length <= 6000, $"embed слишком длинный: {embed.Length}");
        DumpSheet("ЛИСТ B — Носферату «Шпион подземелий» (выход через подтверждение)", embed);
    }

    // ════════════════════════════════════════════════════════════════════
    // СЦЕНАРИЙ C — Малкавиан «Пророк безумия» (MentalPrimary, пул добит)
    // ════════════════════════════════════════════════════════════════════

    private void BuildMalkavianOracle(
        StringBuilder log, System.Action<string> Section, System.Action<string, string> Step,
        System.Action<string> Info)
    {
        Section("СЦЕНАРИЙ C — МАЛКАВИАН, «Пророк безумия» (пул добит)");
        var draft = new VampireCharacter { CharacterId = Guid.NewGuid() };

        VampireCreateResolver.ApplyConceptField(draft, "concept", "Безумный оракул, слышащий голоса судеб");
        VampireCreateResolver.ApplyConceptField(draft, "clan", "Малкавиан");
        VampireCreateResolver.ApplyConceptField(draft, "nature", "Пророк");
        VampireCreateResolver.ApplyConceptField(draft, "demeanor", "Безумец");
        draft.PlayerName = "Михаил";
        draft.CharacterName = "Сильвестр Гроза";
        draft.Generation = 11;
        draft.Sire = "Анна-Лена Вииг";
        Step("Шаг 1",
             $"Concept='{draft.Concept}', Clan='{draft.Clan}', Generation={draft.Generation}, Weakness='{draft.Weakness}'");
        Assert.True(VampireCreateResolver.IsConceptComplete(draft));

        // ШАГ 2 — MentalPrimary (Мент 7 / Физ 5 / Соц 3).
        //   Mental 7:  Восприятие 5 (1+4), Интеллект 3 (1+2), Смекалка 2 (1+1).
        //   Physical 5: Сила 3 (1+2), Ловкость 3 (1+2), Выносливость 2 (1+1).
        //   Social 3:   Обаяние 2 (1+1), Манипуляция 2 (1+1), Привлекательность 2 (1+1).
        VampireAttributesResolver.SetPriority(draft, VampireAttributePriority.MentalPrimary);
        VampireAttributesResolver.ApplyClanRules(draft);
        Enumerable.Range(0, 4).ToList().ForEach(_ => VampireAttributesResolver.Increment(draft, "Восприятие"));
        Enumerable.Range(0, 2).ToList().ForEach(_ => VampireAttributesResolver.Increment(draft, "Интеллект"));
        VampireAttributesResolver.Increment(draft, "Смекалка");
        Enumerable.Range(0, 2).ToList().ForEach(_ => VampireAttributesResolver.Increment(draft, "Сила"));
        Enumerable.Range(0, 2).ToList().ForEach(_ => VampireAttributesResolver.Increment(draft, "Ловкость"));
        VampireAttributesResolver.Increment(draft, "Выносливость");
        VampireAttributesResolver.Increment(draft, "Обаяние");
        VampireAttributesResolver.Increment(draft, "Манипуляция");
        VampireAttributesResolver.Increment(draft, "Привлекательность");
        Assert.True(VampireAttributesResolver.IsAttributesComplete(draft));

        // ШАГ 3 — KnowledgesSecondary (Знания 13 / Навыки 9 / Таланты 5).
        // Знания (13): Оккультизм 3, Медицина 3, Расследование 3, Политика 2,
        //              Юриспруденция 1, Финансы 1.
        // Навыки (9):  Этикет 3, Скрытность 3, Вождение 2, Стрельба 1.
        // Таланты (5): Эмпатия 3, Шестое чувство 2.
        VampireAbilitiesResolver.SetPriority(draft, VampireAbilityPriority.KnowledgesSecondary);
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Оккультизм").IsSuccess));
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Медицина").IsSuccess));
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Расследование").IsSuccess));
        Enumerable.Range(0, 2).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Политика").IsSuccess));
        Assert.True(VampireAbilitiesResolver.Increment(draft, "Юриспруденция").IsSuccess);
        Assert.True(VampireAbilitiesResolver.Increment(draft, "Финансы").IsSuccess);
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Этикет").IsSuccess));
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Скрытность").IsSuccess));
        Enumerable.Range(0, 2).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Вождение").IsSuccess));
        Assert.True(VampireAbilitiesResolver.Increment(draft, "Стрельба").IsSuccess);
        Enumerable.Range(0, 3).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Эмпатия").IsSuccess));
        Enumerable.Range(0, 2).ToList().ForEach(_ => Assert.True(VampireAbilitiesResolver.Increment(draft, "Шестое чувство").IsSuccess));
        Assert.True(VampireAbilitiesResolver.IsAbilitiesComplete(draft));

        // ШАГ 4 — Малкавиан: Ясновидение, Помешательство, Сокрытие.
        VampireAdvantagesResolver.IncrementDiscipline(draft, "Ясновидение");
        VampireAdvantagesResolver.IncrementDiscipline(draft, "Помешательство");
        VampireAdvantagesResolver.IncrementDiscipline(draft, "Сокрытие");
        Assert.True(VampireAdvantagesResolver.IsDisciplinesComplete(draft));

        // 4.2 — BG (пул 5): Наставник 2, Наследие 1, Состояние 1, Связи 1.
        VampireAdvantagesResolver.AddBackground(draft, "Наставник");
        VampireAdvantagesResolver.AddBackground(draft, "Наследие");
        VampireAdvantagesResolver.AddBackground(draft, "Состояние");
        VampireAdvantagesResolver.AddBackground(draft, "Связи");
        VampireAdvantagesResolver.IncrementBackground(draft, "Наставник"); // 1→2
        Assert.True(VampireAdvantagesResolver.IsBackgroundsComplete(draft));

        // 4.3 — Добродетели (пул 7): Совесть 3, Самоконтроль 3, Смелость 4.
        // (spent = 2+2+3 = 7)
        VampireAdvantagesResolver.SeedVirtues(draft);
        Enumerable.Range(0, 2).ToList().ForEach(_ =>
            VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueConscience));
        Enumerable.Range(0, 2).ToList().ForEach(_ =>
            VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueSelfControl));
        Enumerable.Range(0, 3).ToList().ForEach(_ =>
            VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueCourage));
        Assert.True(VampireAdvantagesResolver.IsVirtuesComplete(draft));

        draft.Hunger = 1;
        VampireFinishingResolver.EnsureHealth(draft);

        // 5.1 — Flaws 5: Немой (4) + Мягкий ум (1).
        Assert.True(VampireMeritsFlawsResolver.AddFlawFromCatalog(draft, "Немой").IsSuccess);
        Assert.True(VampireMeritsFlawsResolver.AddFlawFromCatalog(draft, "Мягкий ум").IsSuccess);
        Step("5.1 Flaws (сц. C)",
             $"FlawsCost={VampireMeritsFlawsResolver.FlawsCost(draft)}, " +
             $"EffectivePool={VampireFinishingResolver.EffectiveFreebiePool(draft)}");
        Assert.Equal(5, VampireMeritsFlawsResolver.FlawsCost(draft));
        Assert.Equal(20, VampireFinishingResolver.EffectiveFreebiePool(draft));

        // 5.2 — Merits 6 (макс 7): Ясный ум (3) + Концентрация (2) + Бдительный ум (1).
        Assert.True(VampireMeritsFlawsResolver.AddMeritFromCatalog(draft, "Ясный ум").IsSuccess);
        Assert.True(VampireMeritsFlawsResolver.AddMeritFromCatalog(draft, "Концентрация").IsSuccess);
        Assert.True(VampireMeritsFlawsResolver.AddMeritFromCatalog(draft, "Бдительный ум").IsSuccess);
        Step("5.2 Merits (сц. C)",
             $"MeritsCost={VampireMeritsFlawsResolver.MeritsCost(draft)}, " +
             $"EffectivePool={VampireFinishingResolver.EffectiveFreebiePool(draft)}");
        Assert.Equal(6, VampireMeritsFlawsResolver.MeritsCost(draft));
        Assert.Equal(14, VampireFinishingResolver.EffectiveFreebiePool(draft));

        // 5.3 — Добиваем 14 пунктов:
        //   Наставник 2→3 (1) + Состояние 1→2 (1) + Связи 1→2 (1) + Наследие 1→2 (1) = 4 BG.
        //   Оккультизм 3→4 (2, для специализации «ритуалы»).
        //   Эмпатия 3→4 (2, для специализации «безумие»).
        //   Расследование 3→4 (2, для специализации «улики»).
        //   Самоконтроль 2→3 (2).
        //   Смелость 4→5 (2).
        // Итого 4 + 2 + 2 + 2 + 2 + 2 = 14 — пул добит.
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Background, "Наставник", out _);  // 2→3
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Background, "Состояние", out _);   // 1→2
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Background, "Связи", out _);       // 1→2
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Background, "Наследие", out _);    // 1→2
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Ability, "Оккультизм", out _);     // 3→4
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Ability, "Эмпатия", out _);        // 3→4
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Ability, "Расследование", out _);  // 3→4
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Virtue,
            VampireParameterCatalog.VirtueSelfControl, out _);                         // 2→3
        VampireFinishingResolver.AllocateFreebie(draft,
            VampireFinishingResolver.FreebieTarget.Virtue,
            VampireParameterCatalog.VirtueCourage, out _);                             // 4→5
        Step("5.3 Freebie (сц. C)",
             $"Consumed={VampireFinishingResolver.ConsumedFreebies(draft)}, " +
             $"Remaining={VampireFinishingResolver.RemainingFreebies(draft)}, " +
             $"FreebiesExhausted={VampireFinishingResolver.FreebiesExhausted(draft)}, " +
             $"SpecializationsAllowed={VampireFinishingResolver.SpecializationsAllowed(draft)}");
        Assert.Equal(14, VampireFinishingResolver.ConsumedFreebies(draft));
        Assert.Equal(0, VampireFinishingResolver.RemainingFreebies(draft));
        Assert.True(VampireFinishingResolver.FreebiesExhausted(draft));
        Assert.True(VampireFinishingResolver.SpecializationsAllowed(draft));

        var totalBgs = draft.Backgrounds.Keys.Union(draft.FreebieBackgrounds.Keys).Count();
        Assert.True(totalBgs <= 6, $"BG превысили лимит 6: {totalBgs}");

        // Специализации.
        Assert.True(VampireAbilitiesResolver.SetSpecialization(draft, "Оккультизм", "ритуалы").IsSuccess);
        Assert.True(VampireAbilitiesResolver.SetSpecialization(draft, "Эмпатия", "безумие").IsSuccess);
        Assert.True(VampireAbilitiesResolver.SetSpecialization(draft, "Расследование", "улики").IsSuccess);
        Assert.True(VampireAbilitiesResolver.SetSpecialization(draft, "Восприятие", "тени").IsSuccess);

        Section("ПРОИЗВОДНЫЕ + ЛИСТ (сценарий C)");
        Info($"Humanity={VampireFinishingResolver.ComputeHumanity(draft)}, " +
             $"Willpower={VampireFinishingResolver.ComputeWillpower(draft)}, " +
             $"Hunger={draft.Hunger}, Weakness='{draft.Weakness}'");

        var embed = VampireSheetEmbed.Build(draft);
        Assert.NotNull(embed);
        Assert.True(embed.Length <= 6000, $"embed слишком длинный: {embed.Length}");
        DumpSheet("ЛИСТ C — Малкавиан «Пророк безумия» (пул добит)", embed);
    }

    // ════════════════════════════════════════════════════════════════════
    // Утилиты дампа
    // ════════════════════════════════════════════════════════════════════

    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _sheets =
        new System.Collections.Concurrent.ConcurrentQueue<string>();

    public static readonly string DumpFilePath =
        System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..",
            "TestResults", "vtm-sheets-dump.txt"));

    private void DumpSheet(string title, Discord.Embed embed)
    {
        var sb = new StringBuilder();
        sb.AppendLine().AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine("  " + title);
        sb.AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine($"Author: {embed.Author?.Name}");
        sb.AppendLine($"Title:  {embed.Title}");
        if (!string.IsNullOrWhiteSpace(embed.Description))
            sb.AppendLine($"Desc:   {embed.Description}");
        if (embed.Fields != null)
            foreach (var f in embed.Fields)
            {
                sb.AppendLine();
                sb.AppendLine($"── {f.Name} ──");
                sb.AppendLine(f.Value ?? "");
            }
        sb.AppendLine("═══════════════════════════════════════════════════════");
        var text = sb.ToString();
        _sheets.Enqueue(text);
        Console.WriteLine(text);

        try { System.IO.File.AppendAllText(DumpFilePath, text, System.Text.Encoding.UTF8); }
        catch { }
    }

    [Fact]
    public void DiagnosticPrintSheets_ToFile()
    {
        if (_sheets.IsEmpty) return;
        var combined = new StringBuilder();
        while (_sheets.TryDequeue(out var s)) combined.AppendLine(s);

        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DumpFilePath)!);
            System.IO.File.WriteAllText(DumpFilePath, combined.ToString(), System.Text.Encoding.UTF8);
            Console.WriteLine("VTM SHEETS DUMPED TO: " + DumpFilePath);
        }
        catch { }
    }
}
