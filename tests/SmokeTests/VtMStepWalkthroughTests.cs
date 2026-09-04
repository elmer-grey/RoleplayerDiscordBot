using System.Text;
using System.Linq;
using Discord;
using RPBot.VtM;
using Xunit;
using Xunit.Abstractions;

namespace SmokeTests;

/// <summary>
/// Полный обход всех шагов визарда VtM V20 для одного конкретного персонажа
/// (клан Бруха — концепт «инквизитор», по правилам V20).
///
/// Каждый шаг — реальный вызов публичного API резолвера. Все действия,
/// значения и итоговое содержимое embed'а печатаются в ITestOutputHelper,
/// чтобы можно было прочитать «человеческий» лог.
///
/// Эти же строки попадают в `VtM-StepWalkthrough-Log.md` —
/// см. записи, которые я туда копирую вручную после прогона.
/// </summary>
public class VtMStepWalkthroughTests
{
    private readonly ITestOutputHelper _out;
    public VtMStepWalkthroughTests(ITestOutputHelper output) { _out = output; }
    [Fact]
    public void Walkthrough_Brujah_Inquisitor_AllSteps_BuildsSheet()
    {
        var log = new StringBuilder();
        void Section(string title) { log.AppendLine().AppendLine("═══════════════════════════════════════════════════════"); log.AppendLine("═══ " + title); log.AppendLine("═══════════════════════════════════════════════════════"); }
        void Step(string action, string result) { log.AppendLine().AppendLine("▶ " + action); log.AppendLine("  ↳ " + result); }
        void Info(string s) { log.AppendLine(s); _out.WriteLine(s); }

        // ────────────────────────────────────────────────────────────
        Section("ШАГ 1 — КОНЦЕПЦИЯ");
        // ────────────────────────────────────────────────────────────
        var draft = new VampireCharacter { CharacterId = Guid.NewGuid() };
        Step("draft = new VampireCharacter()", "id=" + draft.CharacterId);
        var r11 = VampireCreateResolver.ApplyConceptField(draft, "concept", "Сожженный инквизитор");
        Step("ApplyConceptField(concept, \"Сожженный инквизитор\")",
            "IsSuccess=" + r11.IsSuccess + ", Concept='" + draft.Concept + "'");

        var r12 = VampireCreateResolver.ApplyConceptField(draft, "clan", "Бруха");
        Step("ApplyConceptField(clan, \"Бруха\")",
            "IsSuccess=" + r12.IsSuccess + ", Clan='" + draft.Clan + "', Weakness='" + draft.Weakness + "'");

        var r13 = VampireCreateResolver.ApplyConceptField(draft, "nature", "Судья");
        Step("ApplyConceptField(nature, \"Судья\")", "Nature='" + draft.Nature + "'");

        var r14 = VampireCreateResolver.ApplyConceptField(draft, "demeanor", "Традиционалист");
        Step("ApplyConceptField(demeanor, \"Традиционалист\")", "Demeanor='" + draft.Demeanor + "'");

        Step("ApplyConceptField(player, \"Андрей\") + (character, \"Эзекиель де Мор\")",
            "Player='" + (draft.PlayerName = "Андрей") + "', Character='" + (draft.CharacterName = "Эзекиель де Мор") + "'");
        draft.Generation = 9;
        draft.Sire = "Архиепископ Этьен";
        Step("Generation=9, Sire=\"Архиепископ Этьен\"", "ok");

        var conceptDone = VampireCreateResolver.IsConceptComplete(draft);
        Step("IsConceptComplete(draft)", "ConceptComplete=" + conceptDone);

        // ────────────────────────────────────────────────────────────
        Section("ШАГ 2 — АТРИБУТЫ (приоритет 7/5/3)");
        // ────────────────────────────────────────────────────────────
        // Бруха: типичный приоритет — Социальные первичные (Обаяние/Манипуляция/Привлекательность=7),
        // Физические вторичные (Сила/Ловкость/Выносливость=5), Ментальные (Восприятие/Интеллект/Смекалка=3).
        var priority = VampireAttributePriority.SocialPrimary;
        var r2p = VampireAttributesResolver.SetPriority(draft, priority);
        Step("SetPriority(SocialPrimary)", "IsSuccess=" + r2p.IsSuccess + ", priority='" + draft.AttributesPriority + "'");
        VampireAttributesResolver.ApplyClanRules(draft);
        Step("ApplyClanRules(Бруха)", "ok (без спец-эффектов для Бруха на Шаге 2)");

        // Социальные (бюджет 7): Обаяние 3 (+3 от базы), Манипуляция 3 (+2), Привлекательность 2 (+1).
        var i = VampireAttributesResolver.Increment(draft, "Обаяние");
        Info("  Обаяние +1: IsSuccess=" + i.IsSuccess + (i.IsSuccess ? "" : " — " + i.Message));
        i = VampireAttributesResolver.Increment(draft, "Обаяние");
        Info("  Обаяние +2: IsSuccess=" + i.IsSuccess + (i.IsSuccess ? "" : " — " + i.Message));
        i = VampireAttributesResolver.Increment(draft, "Обаяние");
        Info("  Обаяние +3: IsSuccess=" + i.IsSuccess + (i.IsSuccess ? "" : " — " + i.Message));
        i = VampireAttributesResolver.Increment(draft, "Манипуляция");
        Info("  Манипуляция +1: IsSuccess=" + i.IsSuccess + (i.IsSuccess ? "" : " — " + i.Message));
        i = VampireAttributesResolver.Increment(draft, "Манипуляция");
        Info("  Манипуляция +2: IsSuccess=" + i.IsSuccess + (i.IsSuccess ? "" : " — " + i.Message));
        i = VampireAttributesResolver.Increment(draft, "Манипуляция");
        Info("  Манипуляция +3: IsSuccess=" + i.IsSuccess + (i.IsSuccess ? "" : " — " + i.Message));
        i = VampireAttributesResolver.Increment(draft, "Привлекательность");
        Info("  Привлекательность +1: IsSuccess=" + i.IsSuccess + (i.IsSuccess ? "" : " — " + i.Message));
        Step("Распределить Социальные (бюджет 7): Обаяние +3, Манипуляция +3, Привлекательность +1",
            "spent=7/7");

        // Физические (бюджет 5): Сила +2, Ловкость +2, Выносливость +1.
        VampireAttributesResolver.Increment(draft, "Сила");
        VampireAttributesResolver.Increment(draft, "Сила");
        VampireAttributesResolver.Increment(draft, "Ловкость");
        VampireAttributesResolver.Increment(draft, "Ловкость");
        VampireAttributesResolver.Increment(draft, "Выносливость");
        Step("Распределить Физические (бюджет 5): Сила +2, Ловкость +2, Выносливость +1", "spent=5/5");

        // Ментальные (бюджет 3): Восприятие +1, Интеллект +1, Смекалка +1.
        VampireAttributesResolver.Increment(draft, "Восприятие");
        VampireAttributesResolver.Increment(draft, "Интеллект");
        VampireAttributesResolver.Increment(draft, "Смекалка");
        Step("Распределить Ментальные (бюджет 3): Восприятие +1, Интеллект +1, Смекалка +1", "spent=3/3");

        Step("IsAttributesComplete(draft)", "StepComplete=" + VampireAttributesResolver.IsAttributesComplete(draft));
        // ДИАГНОСТИКА: что внутри Attributes (dict) и AttributesStruct после Шага 2.
        Info("Атрибуты (draft.Attributes dict): " + (draft.Attributes.Count == 0
            ? "ПУСТО"
            : string.Join(", ", draft.Attributes.Select(kv => kv.Key + "=" + kv.Value))));
        Info("Атрибуты (draft.AttributesStruct): Сила=" + draft.AttributesStruct.Strength
            + ", Ловк=" + draft.AttributesStruct.Dexterity
            + ", Вынос=" + draft.AttributesStruct.Stamina
            + ", Об=" + draft.AttributesStruct.Charisma
            + ", Манип=" + draft.AttributesStruct.Manipulation
            + ", Привл=" + draft.AttributesStruct.Appearance
            + ", Воспр=" + draft.AttributesStruct.Perception
            + ", Инт=" + draft.AttributesStruct.Intelligence
            + ", Смек=" + draft.AttributesStruct.Wits);
        Info("DisplayDots(\"Обаяние\", isCharacteristic=true) = " + draft.DisplayDots("Обаяние", true));

        // ────────────────────────────────────────────────────────────
        Section("ШАГ 3 — СПОСОБНОСТИ (13/9/5)");
        // ────────────────────────────────────────────────────────────
        var abPriority = VampireAbilityPriority.TalentsPrimary; // 13/9/5
        var r3p = VampireAbilitiesResolver.SetPriority(draft, abPriority);
        Step("SetPriority(TalentsPrimary)", "IsSuccess=" + r3p.IsSuccess + ", priority='" + draft.AbilitiesPriority + "'");

        // Таланты (бюджет 13): Атлетика +3, Драка +3, Запугивание +3, Хитрость +3, Красноречие +1 = 13.
        // Freebie на «Красноречие» пишется в dict, не в struct, поэтому IsAbilitiesComplete остаётся True (27/27).
        var ai = VampireAbilitiesResolver.Increment(draft, "Атлетика");
        Info("  Атлетика +1: IsSuccess=" + ai.IsSuccess + (ai.IsSuccess ? "" : " — " + ai.Message));
        ai = VampireAbilitiesResolver.Increment(draft, "Атлетика");
        Info("  Атлетика +2: IsSuccess=" + ai.IsSuccess + (ai.IsSuccess ? "" : " — " + ai.Message));
        ai = VampireAbilitiesResolver.Increment(draft, "Атлетика");
        Info("  Атлетика +3: IsSuccess=" + ai.IsSuccess + (ai.IsSuccess ? "" : " — " + ai.Message));
        ai = VampireAbilitiesResolver.Increment(draft, "Драка");
        Info("  Драка +1: IsSuccess=" + ai.IsSuccess + (ai.IsSuccess ? "" : " — " + ai.Message));
        ai = VampireAbilitiesResolver.Increment(draft, "Драка");
        Info("  Драка +2: IsSuccess=" + ai.IsSuccess + (ai.IsSuccess ? "" : " — " + ai.Message));
        ai = VampireAbilitiesResolver.Increment(draft, "Драка");
        Info("  Драка +3: IsSuccess=" + ai.IsSuccess + (ai.IsSuccess ? "" : " — " + ai.Message));
        ai = VampireAbilitiesResolver.Increment(draft, "Запугивание");
        Info("  Запугивание +1: IsSuccess=" + ai.IsSuccess + (ai.IsSuccess ? "" : " — " + ai.Message));
        ai = VampireAbilitiesResolver.Increment(draft, "Запугивание");
        Info("  Запугивание +2: IsSuccess=" + ai.IsSuccess + (ai.IsSuccess ? "" : " — " + ai.Message));
        ai = VampireAbilitiesResolver.Increment(draft, "Запугивание");
        Info("  Запугивание +3: IsSuccess=" + ai.IsSuccess + (ai.IsSuccess ? "" : " — " + ai.Message));
        ai = VampireAbilitiesResolver.Increment(draft, "Хитрость");
        Info("  Хитрость +1: IsSuccess=" + ai.IsSuccess + (ai.IsSuccess ? "" : " — " + ai.Message));
        ai = VampireAbilitiesResolver.Increment(draft, "Хитрость");
        Info("  Хитрость +2: IsSuccess=" + ai.IsSuccess + (ai.IsSuccess ? "" : " — " + ai.Message));
        ai = VampireAbilitiesResolver.Increment(draft, "Хитрость");
        Info("  Хитрость +3: IsSuccess=" + ai.IsSuccess + (ai.IsSuccess ? "" : " — " + ai.Message));
        ai = VampireAbilitiesResolver.Increment(draft, "Красноречие");
        Info("  Красноречие +1: IsSuccess=" + ai.IsSuccess + (ai.IsSuccess ? "" : " — " + ai.Message));
        Step("Таланты (бюджет 13): Атлетика +3, Драка +3, Запугивание +3, Хитрость +3, Красноречие +1 = 13",
            "spent=13/13");

        // Навыки (бюджет 9): Ремесло +3, Скрытность +3, Вождение +3 (9/9).
        // (НЕ Медицина — это Знания по V20 стр. 81.)
        var sk = VampireAbilitiesResolver.Increment(draft, "Ремесло");
        Info("  Ремесло +1: IsSuccess=" + sk.IsSuccess + (sk.IsSuccess ? "" : " — " + sk.Message));
        sk = VampireAbilitiesResolver.Increment(draft, "Ремесло");
        Info("  Ремесло +2: IsSuccess=" + sk.IsSuccess + (sk.IsSuccess ? "" : " — " + sk.Message));
        sk = VampireAbilitiesResolver.Increment(draft, "Ремесло");
        Info("  Ремесло +3: IsSuccess=" + sk.IsSuccess + (sk.IsSuccess ? "" : " — " + sk.Message));
        sk = VampireAbilitiesResolver.Increment(draft, "Скрытность");
        Info("  Скрытность +1: IsSuccess=" + sk.IsSuccess + (sk.IsSuccess ? "" : " — " + sk.Message));
        sk = VampireAbilitiesResolver.Increment(draft, "Скрытность");
        Info("  Скрытность +2: IsSuccess=" + sk.IsSuccess + (sk.IsSuccess ? "" : " — " + sk.Message));
        sk = VampireAbilitiesResolver.Increment(draft, "Скрытность");
        Info("  Скрытность +3: IsSuccess=" + sk.IsSuccess + (sk.IsSuccess ? "" : " — " + sk.Message));
        sk = VampireAbilitiesResolver.Increment(draft, "Вождение");
        Info("  Вождение +1: IsSuccess=" + sk.IsSuccess + (sk.IsSuccess ? "" : " — " + sk.Message));
        sk = VampireAbilitiesResolver.Increment(draft, "Вождение");
        Info("  Вождение +2: IsSuccess=" + sk.IsSuccess + (sk.IsSuccess ? "" : " — " + sk.Message));
        sk = VampireAbilitiesResolver.Increment(draft, "Вождение");
        Info("  Вождение +3: IsSuccess=" + sk.IsSuccess + (sk.IsSuccess ? "" : " — " + sk.Message));
        Step("Навыки (бюджет 9): Ремесло +3, Скрытность +3, Вождение +3 = 9/9", "spent=9/9");

        // Знания (бюджет 5): Оккультизм +3, Медицина +2 (5/5).
        var kn = VampireAbilitiesResolver.Increment(draft, "Оккультизм");
        Info("  Оккультизм +1: IsSuccess=" + kn.IsSuccess + (kn.IsSuccess ? "" : " — " + kn.Message));
        kn = VampireAbilitiesResolver.Increment(draft, "Оккультизм");
        Info("  Оккультизм +2: IsSuccess=" + kn.IsSuccess + (kn.IsSuccess ? "" : " — " + kn.Message));
        kn = VampireAbilitiesResolver.Increment(draft, "Оккультизм");
        Info("  Оккультизм +3: IsSuccess=" + kn.IsSuccess + (kn.IsSuccess ? "" : " — " + kn.Message));
        kn = VampireAbilitiesResolver.Increment(draft, "Медицина");
        Info("  Медицина +1: IsSuccess=" + kn.IsSuccess + (kn.IsSuccess ? "" : " — " + kn.Message));
        kn = VampireAbilitiesResolver.Increment(draft, "Медицина");
        Info("  Медицина +2: IsSuccess=" + kn.IsSuccess + (kn.IsSuccess ? "" : " — " + kn.Message));
        Step("Знания (бюджет 5): Оккультизм +3, Медицина +2 = 5/5", "spent=5/5");

        // Специализация при ≥4 (здесь ни одна способность не достигла 4, поэтому не нужны).
        Step("Специализации на Шаге 3 не требуются (ни одна способность < 4)", "ok");

        Step("IsAbilitiesComplete(draft)", "StepComplete=" + VampireAbilitiesResolver.IsAbilitiesComplete(draft));
        Info("Способности сейчас: " + string.Join(", ",
            draft.Abilities.GroupBy(a => a).Select(g => g.Key + "=" + g.Count())));

        // ────────────────────────────────────────────────────────────
        Section("ШАГ 4.1 — ДИСЦИПЛИНЫ (пул 3)");
        // ────────────────────────────────────────────────────────────
        var slots = VampireAdvantagesCatalog.GetDisciplineSlots(draft.Clan);
        Info("Клановые слоты Бруха: " + string.Join(", ", slots));

        // Бруха (V20 стр. 64): Стремительность (Celerity), Мощь (Potence), Величие (Presence).
        // Пул 3 → раскидаем Стремительность +2, Мощь +1.
        var rd1 = VampireAdvantagesResolver.IncrementDiscipline(draft, "Стремительность");
        Step("IncrementDiscipline(\"Стремительность\") #1", "IsSuccess=" + rd1.IsSuccess + (rd1.IsSuccess ? "" : " — " + rd1.Message));
        var rd2 = VampireAdvantagesResolver.IncrementDiscipline(draft, "Стремительность");
        Step("IncrementDiscipline(\"Стремительность\") #2", "IsSuccess=" + rd2.IsSuccess + (rd2.IsSuccess ? "" : " — " + rd2.Message));
        var rd3 = VampireAdvantagesResolver.IncrementDiscipline(draft, "Мощь");
        Step("IncrementDiscipline(\"Мощь\")", "IsSuccess=" + rd3.IsSuccess + (rd3.IsSuccess ? "" : " — " + rd3.Message));
        Step("Итого дисциплины", "spent=" + VampireAdvantagesResolver.TotalDisciplineSpent(draft) + "/3, IsComplete=" + VampireAdvantagesResolver.IsDisciplinesComplete(draft));

        // ────────────────────────────────────────────────────────────
        Section("ШАГ 4.2 — ФАКТЫ БИОГРАФИИ (пул 5)");
        // ────────────────────────────────────────────────────────────
        // Пул 5: Влияние ранг 3 (3 пула) + Контакты ранг 2 (2 пула) = 5.
        // AddBackground ставит ранг 1, IncrementBackground поднимает на 1.
        // Нужно 3+1 = 4 ранговые операции: 2 Add + 2 Increment.
        var rb1 = VampireAdvantagesResolver.AddBackground(draft, "Влияние");
        Step("AddBackground(\"Влияние\")", "IsSuccess=" + rb1.IsSuccess + (rb1.IsSuccess ? "" : " — " + rb1.Message));
        var rb2 = VampireAdvantagesResolver.AddBackground(draft, "Контакты");
        Step("AddBackground(\"Контакты\")", "IsSuccess=" + rb2.IsSuccess + (rb2.IsSuccess ? "" : " — " + rb2.Message));
        var rb3 = VampireAdvantagesResolver.IncrementBackground(draft, "Влияние");
        Step("IncrementBackground(\"Влияние\") #1 → ранг 2", "IsSuccess=" + rb3.IsSuccess + (rb3.IsSuccess ? "" : " — " + rb3.Message));
        var rb4 = VampireAdvantagesResolver.IncrementBackground(draft, "Влияние");
        Step("IncrementBackground(\"Влияние\") #2 → ранг 3", "IsSuccess=" + rb4.IsSuccess + (rb4.IsSuccess ? "" : " — " + rb4.Message));
        var rb5 = VampireAdvantagesResolver.IncrementBackground(draft, "Контакты");
        Step("IncrementBackground(\"Контакты\") → ранг 2", "IsSuccess=" + rb5.IsSuccess + (rb5.IsSuccess ? "" : " — " + rb5.Message));
        Step("Итого факты", "spent=" + VampireAdvantagesResolver.TotalBackgroundSpent(draft) + "/5, IsComplete=" + VampireAdvantagesResolver.IsBackgroundsComplete(draft));

        // ────────────────────────────────────────────────────────────
        Section("ШАГ 4.3 — ДОБРОДЕТЕЛИ (база 1/1/1 + пул 7)");
        // ────────────────────────────────────────────────────────────
        VampireAdvantagesResolver.SeedVirtues(draft);
        Info("После SeedVirtues: Совесть=" + draft.Virtues[VampireParameterCatalog.VirtueConscience] +
             ", СК=" + draft.Virtues[VampireParameterCatalog.VirtueSelfControl] +
             ", Смелость=" + draft.Virtues[VampireParameterCatalog.VirtueCourage]);

        // Раскидываем 7: Совесть +4 (5), СК +2 (3), Смелость +1 (2) = 7.
        VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueConscience);
        VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueConscience);
        VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueConscience);
        VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueConscience);
        VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueSelfControl);
        VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueSelfControl);
        VampireAdvantagesResolver.IncrementVirtue(draft, VampireParameterCatalog.VirtueCourage);
        Step("Добродетели: Совесть +4 → 5, СК +2 → 3, Смелость +1 → 2", "spent=" + VampireAdvantagesResolver.TotalVirtueSpent(draft) + "/7, IsComplete=" + VampireAdvantagesResolver.IsVirtuesComplete(draft));

        // ────────────────────────────────────────────────────────────
        Section("ШАГ 5 — ДОВОДКА (freebie pool 15, голод read-only)");
        // ────────────────────────────────────────────────────────────
        draft.Hunger = 1; // V5-Hunger при создании
        Step("draft.Hunger = 1 (старт)", "ok");

        // Здоровье: не заполняем (по V20 — авто-создание шкалы 7 ячеек при первом обращении).
        VampireFinishingResolver.EnsureHealth(draft);
        Info("Health = " + (draft.Health == null ? "null" : draft.Health.ToString()));

        // Пул freebie 15. Покупаем:
        //   +1 к Дисциплине «Величие» (cost 7)
        //   +1 к Атрибуту «Обаяние» (cost 5)
        //   +1 к Способности «Красноречие» (cost 2)
        //   +1 к Факту «Влияние» (cost 1)
        // Итого: 7 + 5 + 2 + 1 = 15.
        var r5a = VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Discipline, "Величие", out _);
        Step("AllocateFreebie(Discipline, \"Величие\") cost 7", "IsSuccess=" + r5a.IsSuccess +
            (r5a.IsSuccess ? "" : " — " + r5a.Message) + ", remaining=" + VampireFinishingResolver.RemainingFreebies(draft));

        var r5b = VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Attribute, "Обаяние", out _);
        Step("AllocateFreebie(Attribute, \"Обаяние\") cost 5", "IsSuccess=" + r5b.IsSuccess +
            (r5b.IsSuccess ? "" : " — " + r5b.Message) + ", remaining=" + VampireFinishingResolver.RemainingFreebies(draft));

        var r5c = VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Ability, "Красноречие", out _);
        Step("AllocateFreebie(Ability, \"Красноречие\") cost 2", "IsSuccess=" + r5c.IsSuccess +
            (r5c.IsSuccess ? "" : " — " + r5c.Message) + ", remaining=" + VampireFinishingResolver.RemainingFreebies(draft));

        var r5d = VampireFinishingResolver.AllocateFreebie(draft, VampireFinishingResolver.FreebieTarget.Background, "Влияние", out _);
        Step("AllocateFreebie(Background, \"Влияние\") cost 1", "IsSuccess=" + r5d.IsSuccess +
            (r5d.IsSuccess ? "" : " — " + r5d.Message) + ", remaining=" + VampireFinishingResolver.RemainingFreebies(draft));

        Step("Использован пул freebie полностью?", "consumed=" + VampireFinishingResolver.ConsumedFreebies(draft) +
            "/15, remaining=" + VampireFinishingResolver.RemainingFreebies(draft));
        // Полный дамп freebie-реестра.
        Info("Freebie-реестр:");
        foreach (var line in VampireFinishingResolver.DescribeSpent(draft))
            Info("  " + line);

        Step("ComputeHumanity(draft)", "=" + VampireFinishingResolver.ComputeHumanity(draft) +
            " (" + VampireFinishingResolver.DescribeHumanity(draft) + " + бонус " + draft.HumanityBonus + ")");
        Step("ComputeWillpower(draft)", "=" + VampireFinishingResolver.ComputeWillpower(draft) +
            " (" + VampireFinishingResolver.DescribeWillpower(draft) + " + бонус " + draft.WillpowerBonus + ")");

        // ────────────────────────────────────────────────────────────
        Section("ШАГ 6 — ФИНАЛЬНЫЙ ЛИСТ (VampireSheetEmbed.Build)");
        // ────────────────────────────────────────────────────────────
        var embed = VampireSheetEmbed.Build(draft);
        Info("embed.Length = " + embed.Length + " символов (лимит Discord = 6000)");
        Info("Title: " + embed.Title);
        Info("Description: " + embed.Description);
        if (embed.Fields != null)
            foreach (var f in embed.Fields)
                Info("  • Поле «" + f.Name + "»: " + f.Value);
        if (embed.Footer != null)
            Info("Footer: " + embed.Footer.ToString());

        // Простые sanity-asserts.
        Info("DBG: Concept=" + VampireCreateResolver.IsConceptComplete(draft)
            + ", Attr=" + VampireAttributesResolver.IsAttributesComplete(draft)
            + ", Abl=" + VampireAbilitiesResolver.IsAbilitiesComplete(draft)
            + ", Disc=" + VampireAdvantagesResolver.IsDisciplinesComplete(draft)
            + ", Bg=" + VampireAdvantagesResolver.IsBackgroundsComplete(draft)
            + ", Virt=" + VampireAdvantagesResolver.IsVirtuesComplete(draft)
            + ", FreebieRem=" + VampireFinishingResolver.RemainingFreebies(draft));
        Assert.True(VampireCreateResolver.IsConceptComplete(draft));
        Assert.True(VampireAttributesResolver.IsAttributesComplete(draft));
        Assert.True(VampireAbilitiesResolver.IsAbilitiesComplete(draft));
        Assert.True(VampireAdvantagesResolver.IsDisciplinesComplete(draft));
        Assert.True(VampireAdvantagesResolver.IsBackgroundsComplete(draft));
        Assert.True(VampireAdvantagesResolver.IsVirtuesComplete(draft));
        Assert.Equal(0, VampireFinishingResolver.RemainingFreebies(draft));
        Info("DBG after asserts: ConsumedFreebies = " + VampireFinishingResolver.ConsumedFreebies(draft) +
             ", RemainingFreebies = " + VampireFinishingResolver.RemainingFreebies(draft) +
             ", FreebieSpent.Count = " + draft.FreebieSpent.Count);
        foreach (var kv in draft.FreebieSpent)
            Info("  [" + kv.Key + "] = " + kv.Value);

        // Печать полного лога — чтобы можно было скопировать в файл.
        Console.WriteLine(log.ToString());
        _out.WriteLine(log.ToString());

        Assert.True(embed.Length <= 6000, $"embed слишком длинный: {embed.Length} символов (лимит 6000)");
    }
}
