using System;
using System.Linq;
using Discord;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Smoke-тесты для VtM V20 визарда создания персонажа (Этап 1 — шаг 1 «Концепция»).
/// </summary>
public class VampireWizardTests
{
    private static VampireCharacter NewDraft() => new VampireCharacter
    {
        CharacterId = Guid.NewGuid(),
        PlayerId = 42,
        PlayerName = "TestUser",
        Generation = 13,
        Hunger = 1,
    };

    // ── Каталог: канонические кланы V20 (из PDF) ─────────────────────

    [Fact]
    public void Catalog_HasAll14CanonicalV20Clans()
    {
        var expected = new[]
        {
            "Ассамит", "Бруха", "Вентру", "Гангрел", "Джованни", "Каитиф",
            "Ласомбра", "Малкавиан", "Носферату", "Последователь Сета",
            "Равнос", "Тореадор", "Тремер", "Цимисхи",
        };
        foreach (var name in expected)
        {
            Assert.True(
                VampireParameterCatalog.IsValidClan(name),
                $"Канонический клан {name} должен быть в каталоге");
        }
    }

    [Fact]
    public void Catalog_InvalidClan_ReturnsFalse()
    {
        Assert.False(VampireParameterCatalog.IsValidClan("Танкреди"));
        Assert.False(VampireParameterCatalog.IsValidClan(""));
    }

    [Theory]
    [InlineData("Ассамит",          "Стремительность,Сокрытие,Упокоение")]
    [InlineData("Бруха",            "Стремительность,Мощь,Величие")]
    [InlineData("Вентру",           "Доминирование,Стойкость,Величие")]
    [InlineData("Гангрел",          "Анимализм,Стойкость,Метаморфозы")]
    [InlineData("Джованни",         "Доминирование,Некромантия,Мощь")]
    [InlineData("Каитиф",           "")]
    [InlineData("Ласомбра",         "Доминирование,Затемнение,Мощь")]
    [InlineData("Малкавиан",        "Ясновидение,Помешательство,Сокрытие")]
    [InlineData("Носферату",        "Анимализм,Сокрытие,Мощь")]
    [InlineData("Последователь Сета", "Сокрытие,Величие,Серпентис")]
    [InlineData("Равнос",           "Анимализм,Фантасмагория,Стойкость")]
    [InlineData("Тореадор",         "Ясновидение,Стремительность,Величие")]
    [InlineData("Тремер",           "Ясновидение,Доминирование,Тауматургия")]
    [InlineData("Цимисхи",          "Анимализм,Ясновидение,Преображение")]
    public void Catalog_GetClanDisciplines_MatchesV20Pdf(string clan, string expectedCsv)
    {
        var actual = VampireParameterCatalog.GetClanDisciplines(clan);
        var expected = string.IsNullOrEmpty(expectedCsv)
            ? Array.Empty<string>()
            : expectedCsv.Split(',');
        Assert.Equal(expected, actual.ToArray());
    }

    [Fact]
    public void Catalog_CaitiffHasNoClanDisciplines()
    {
        var d = VampireParameterCatalog.GetClanDisciplines("Каитиф");
        Assert.Empty(d);
    }

    [Fact]
    public void Catalog_GetClanFlawShort_NotEmpty()
    {
        foreach (var clan in VampireParameterCatalog.Clans)
        {
            var s = VampireParameterCatalog.GetClanFlawShort(clan);
            Assert.False(string.IsNullOrWhiteSpace(s), $"У {clan} должен быть клановый изъян");
        }
    }

    [Fact]
    public void Catalog_GetClanFlawLong_NotShorterThanShort()
    {
        VampireClanFlawCatalog.EnsureSeeded();
        foreach (var clan in VampireParameterCatalog.Clans)
        {
            var s = VampireParameterCatalog.GetClanFlawShort(clan);
            var l = VampireClanFlawCatalog.GetClanFlawLong(clan);
            Assert.True(l.Length >= s.Length,
                $"У {clan} полный изъян должен быть ≥ краткого");
        }
    }

    // ── Resolver: ApplyConceptField ──────────────────────────────────

    [Fact]
    public void Apply_ValidConcept_SetsFieldAndCompleteFalse()
    {
        var d = NewDraft();
        var r = VampireCreateResolver.ApplyConceptField(d, "concept", "Циничный детектив");
        Assert.Equal(VampireCreateConceptFailure.None, r.Failure);
        Assert.Equal("Циничный детектив", d.Concept);
        Assert.False(r.ConceptComplete);
    }

    [Fact]
    public void Apply_EmptyConcept_FailsConceptRequired()
    {
        var d = NewDraft();
        var r = VampireCreateResolver.ApplyConceptField(d, "concept", "");
        Assert.Equal(VampireCreateConceptFailure.ConceptRequired, r.Failure);
    }

    [Fact]
    public void Apply_ValidClan_SetsClanAndWeakness()
    {
        var d = NewDraft();
        var r = VampireCreateResolver.ApplyConceptField(d, "clan", "Бруха");
        Assert.Equal(VampireCreateConceptFailure.None, r.Failure);
        Assert.Equal("Бруха", d.Clan);
        Assert.False(string.IsNullOrEmpty(d.Weakness),
            "При выборе клана должен подставиться клановый изъян");
    }

    [Fact]
    public void Apply_InvalidClan_FailsClanInvalid()
    {
        var d = NewDraft();
        var r = VampireCreateResolver.ApplyConceptField(d, "clan", "Несуществующий");
        Assert.Equal(VampireCreateConceptFailure.ClanInvalid, r.Failure);
        Assert.Equal("", d.Clan);
    }

    [Fact]
    public void Apply_ClanCaitiff_NoClanDisciplinesInDraft()
    {
        var d = NewDraft();
        VampireCreateResolver.ApplyConceptField(d, "clan", "Каитиф");
        Assert.Equal("Каитиф", d.Clan);
    }

    [Fact]
    public void Apply_AllFourFields_MarksConceptComplete()
    {
        var d = NewDraft();
        VampireCreateResolver.ApplyConceptField(d, "concept", "Герой");
        VampireCreateResolver.ApplyConceptField(d, "clan", "Тремер");
        VampireCreateResolver.ApplyConceptField(d, "nature", "Судья");
        var r = VampireCreateResolver.ApplyConceptField(d, "demeanor", "Традиционалист");
        Assert.True(r.ConceptComplete);
        Assert.True(VampireCreateResolver.IsConceptComplete(d));
    }

    [Fact]
    public void Apply_BioEmpty_IsAllowed()
    {
        var d = NewDraft();
        var r = VampireCreateResolver.ApplyConceptField(d, "bio", "");
        Assert.Equal(VampireCreateConceptFailure.None, r.Failure);
    }

    [Fact]
    public void Apply_UnknownField_Fails()
    {
        var d = NewDraft();
        var r = VampireCreateResolver.ApplyConceptField(d, "weapons", "AK-47");
        Assert.NotEqual(VampireCreateConceptFailure.None, r.Failure);
    }

    [Fact]
    public void Apply_ClanTrimsAndAccepts()
    {
        var d = NewDraft();
        var r = VampireCreateResolver.ApplyConceptField(d, "clan", "  Бруха  ");
        Assert.Equal(VampireCreateConceptFailure.None, r.Failure);
        Assert.Equal("Бруха", d.Clan);
    }

    // ── Resolver: ClearConceptField ──────────────────────────────────

    [Fact]
    public void Clear_RemovesValue()
    {
        var d = NewDraft();
        VampireCreateResolver.ApplyConceptField(d, "clan", "Бруха");
        VampireCreateResolver.ClearConceptField(d, "clan");
        Assert.Equal("", d.Clan);
        Assert.Equal("", d.Weakness);
    }

    // ── Resolver: BuildConceptStatusMessage ──────────────────────────

    [Fact]
    public void BuildStatus_ContainsAllFiveFields()
    {
        var d = NewDraft();
        var msg = VampireCreateResolver.BuildConceptStatusMessage(d);
        Assert.Contains("Амплуа", msg);
        Assert.Contains("Клан", msg);
        Assert.Contains("Натура", msg);
        Assert.Contains("Маска", msg);
        Assert.Contains("Описание", msg);
    }

    [Fact]
    public void BuildStatus_ShowsCompletionHint()
    {
        var d = NewDraft();
        var msg = VampireCreateResolver.BuildConceptStatusMessage(d);
        Assert.Contains("заполн", msg.ToLowerInvariant());
    }

    // ── Wizard components ────────────────────────────────────────────

    [Fact]
    public void BuildConceptStep_AlwaysHasCancel()
    {
        var d = NewDraft();
        var mc = VampireWizardComponents.BuildForConceptStep(d);
        // Кнопки распределятся по рядам; проверим, что Cancel где-то есть.
        var labels = mc.Components.OfType<ActionRowComponent>()
            .SelectMany(r => r.Components.OfType<ButtonComponent>())
            .Select(b => b.Label)
            .ToList();
        Assert.Contains("Отмена", labels);
    }

    [Fact]
    public void BuildConceptStep_ShowsNext_WhenComplete()
    {
        var d = NewDraft();
        VampireCreateResolver.ApplyConceptField(d, "concept", "X");
        VampireCreateResolver.ApplyConceptField(d, "clan", "Бруха");
        VampireCreateResolver.ApplyConceptField(d, "nature", "Y");
        VampireCreateResolver.ApplyConceptField(d, "demeanor", "Z");

        var mc = VampireWizardComponents.BuildForConceptStep(d);
        var labels = mc.Components.OfType<ActionRowComponent>()
            .SelectMany(r => r.Components.OfType<ButtonComponent>())
            .Select(b => b.Label)
            .ToList();
        Assert.Contains(labels, l => l != null && l.Contains("Далее"));
    }

    [Fact]
    public void BuildConceptStep_HidesNext_WhenIncomplete()
    {
        var d = NewDraft();
        var mc = VampireWizardComponents.BuildForConceptStep(d);
        var labels = mc.Components.OfType<ActionRowComponent>()
            .SelectMany(r => r.Components.OfType<ButtonComponent>())
            .Select(b => b.Label)
            .ToList();
        Assert.DoesNotContain(labels, l => l != null && l.StartsWith("Далее"));
    }

    [Fact]
    public void BuildCustomId_RoundTripsAllActions()
    {
        var id = Guid.NewGuid();
        var actions = new[]
        {
            VampireWizardAction.SetConcept,  VampireWizardAction.SetClan,
            VampireWizardAction.SetNature,   VampireWizardAction.SetDemeanor,
            VampireWizardAction.SetBio,      VampireWizardAction.SkipBio,
            VampireWizardAction.ClearBio,    VampireWizardAction.ClearConcept,
            VampireWizardAction.ClearClan,   VampireWizardAction.ClearNature,
            VampireWizardAction.ClearDemeanor, VampireWizardAction.Next, VampireWizardAction.Cancel,
        };
        foreach (var a in actions)
        {
            var cid = VampireWizardComponents.BuildCustomId(a, id);
            Assert.True(VampireWizardComponents.TryParse(cid, out var parsed, out var parsedId),
                $"Parse failed for {a}");
            Assert.Equal(a, parsed);
            Assert.Equal(id, parsedId);
        }
    }

    [Fact]
    public void Parse_ForeignCustomId_ReturnsFalse()
    {
        Assert.False(VampireWizardComponents.TryParse("foo:bar:baz", out _, out _));
        Assert.False(VampireWizardComponents.TryParse("vtm_btn:desc:abc", out _, out _));
        Assert.False(VampireWizardComponents.TryParse("", out _, out _));
    }

    [Fact]
    public void IsOurButton_OnlyForWizardPrefix()
    {
        Assert.True(VampireWizardComponents.IsOurButton("vtm_wiz:next:abc"));
        Assert.False(VampireWizardComponents.IsOurButton("vtm_btn:desc:abc"));
    }

    // ── Wizard session / registry ────────────────────────────────────

    [Fact]
    public void Registry_SetGetRemove_Works()
    {
        var r = VampireWizardRegistry.Instance;
        var g = 100UL; var p = 200UL;
        var session = new VampireWizardSession { GuildId = g, PlayerId = p };
        // На Этапе 1 у пользователя 0-1 сессий; используем уникального игрока.
        var initial = r.Get(g, p);
        if (initial != null) r.Remove(g, p);

        r.Set(g, p, session);
        var got = r.Get(g, p);
        Assert.NotNull(got);
        Assert.Same(session, got);

        r.Remove(g, p);
        Assert.Null(r.Get(g, p));
    }

    [Fact]
    public void Registry_GetByUser_FindsAcrossGuilds()
    {
        var r = VampireWizardRegistry.Instance;
        var p = 999_999UL;
        // Уникальный id — чтобы не пересечься с другими тестами.
        var s1 = new VampireWizardSession { GuildId = 1, PlayerId = p };
        var s2 = new VampireWizardSession { GuildId = 2, PlayerId = p };
        r.Set(1, p, s1);
        r.Set(2, p, s2);

        var found = r.GetByUser(p);
        Assert.NotNull(found);
        Assert.Equal(p, found!.PlayerId);

        r.RemoveByUser(p);
        Assert.Null(r.GetByUser(p));
    }

    // ── Active registry ──────────────────────────────────────────────

    [Fact]
    public void ActiveRegistry_SetGetClear()
    {
        var reg = VampireActiveRegistry.Instance;
        var g = 5000UL; var p = 6000UL; var c = Guid.NewGuid();
        reg.ClearActiveCharacterId(g, p);

        Assert.False(reg.GetActiveCharacterId(g, p).HasValue);
        reg.SetActiveCharacterId(g, p, c);
        Assert.Equal(c, reg.GetActiveCharacterId(g, p));
        reg.ClearActiveCharacterId(g, p);
        Assert.False(reg.GetActiveCharacterId(g, p).HasValue);
    }

    [Fact]
    public void ActiveRegistry_ClearByCharacterId_RemovesOnlyMatching()
    {
        var reg = VampireActiveRegistry.Instance;
        var g = 5001UL;
        var c1 = Guid.NewGuid();
        var c2 = Guid.NewGuid();
        reg.SetActiveCharacterId(g, 1, c1);
        reg.SetActiveCharacterId(g, 2, c2);
        reg.ClearByCharacterId(g, c1);
        Assert.Null(reg.GetActiveCharacterId(g, 1));
        Assert.Equal(c2, reg.GetActiveCharacterId(g, 2));
    }

            // ── Step 1 patch: Sire + Generation ────────────────────────────────

            [Fact]
            public void ApplySire_ValidName_SetsSire()
            {
                var draft = NewDraft();
                var dec = VampireCreateResolver.ApplyConceptField(draft, "sire", "Граф Владимир Ромашков");
                Assert.Equal(VampireCreateConceptFailure.None, dec.Failure);
                Assert.Equal("Граф Владимир Ромашков", dec.Draft.Sire);
            }

            [Fact]
            public void ApplySire_Empty_IsAllowedButDraftRemainsEmpty()
            {
                var draft = NewDraft();
                draft.Sire = "Старый Сир";
                var dec = VampireCreateResolver.ApplyConceptField(draft, "sire", "");
                Assert.Equal(VampireCreateConceptFailure.None, dec.Failure);
                Assert.Equal("", dec.Draft.Sire);
            }

            [Fact]
            public void ApplyGeneration_Valid_SetsValue()
            {
                var draft = NewDraft();
                Assert.Equal(13, draft.Generation);
                var dec = VampireCreateResolver.ApplyConceptField(draft, "generation", "9");
                Assert.Equal(VampireCreateConceptFailure.None, dec.Failure);
                Assert.Equal(9, dec.Draft.Generation);
            }

            [Theory]
            [InlineData("2")]
            [InlineData("16")]
            [InlineData("0")]
            [InlineData("-1")]
            [InlineData("abc")]
            public void ApplyGeneration_OutOfRange_Fails(string value)
            {
                var draft = NewDraft();
                var dec = VampireCreateResolver.ApplyConceptField(draft, "generation", value);
                Assert.Equal(VampireCreateConceptFailure.GenerationOutOfRange, dec.Failure);
                // Generation не должно меняться при провале.
                Assert.Equal(13, dec.Draft.Generation);
            }

            [Fact]
            public void ClearGeneration_ResetsTo13()
            {
                var draft = NewDraft();
                draft.Generation = 5;
                var dec = VampireCreateResolver.ClearConceptField(draft, "generation");
                Assert.Equal(VampireCreateConceptFailure.None, dec.Failure);
                Assert.Equal(13, dec.Draft.Generation);
            }

            [Fact]
            public void ConceptComplete_DoesNotRequireSireOrGeneration()
            {
                var draft = NewDraft();
                Assert.False(VampireCreateResolver.IsConceptComplete(draft));
                VampireCreateResolver.ApplyConceptField(draft, "concept", "детектив");
                VampireCreateResolver.ApplyConceptField(draft, "clan", "Носферату");
                VampireCreateResolver.ApplyConceptField(draft, "nature", "Бродяга");
                VampireCreateResolver.ApplyConceptField(draft, "demeanor", "Призрак");
                Assert.True(VampireCreateResolver.IsConceptComplete(draft));
            }

            [Fact]
            public void BuildStatus_ContainsSireAndGeneration()
            {
                var draft = NewDraft();
                var msg = VampireCreateResolver.BuildConceptStatusMessage(draft);
                Assert.Contains("Сир", msg);
                Assert.Contains("Поколение", msg);
                Assert.Contains("13", msg); // дефолт
            }

            [Fact]
            public void BuildConceptStep_ShowsSireButtonAlways()
            {
                var draft = NewDraft();
                var comp = VampireWizardComponents.BuildForConceptStep(draft);
                var rows = GetRows(comp);
                var buttons = rows.SelectMany(r => r.Components).OfType<ButtonComponent>().ToList();
                // Всегда показываем кнопку для сира.
                Assert.Contains(buttons, b => b.CustomId.Contains("set_sire"));
                Assert.DoesNotContain(buttons, b => b.CustomId.Contains("clear_sire"));
            }

            [Fact]
            public void BuildConceptStep_ShowsClearSireWhenSireSet()
            {
                var draft = NewDraft();
                draft.Sire = "Анна";
                var comp = VampireWizardComponents.BuildForConceptStep(draft);
                var buttons = GetRows(comp).SelectMany(r => r.Components).OfType<ButtonComponent>().ToList();
                Assert.Contains(buttons, b => b.CustomId.Contains("set_sire"));
                Assert.Contains(buttons, b => b.CustomId.Contains("clear_sire"));
                Assert.Contains(buttons, b => b.Label != null && b.Label.Contains("Анна"));
            }

            [Fact]
            public void BuildConceptStep_ShowsResetGeneration_WhenNotDefault()
            {
                var draft = NewDraft();
                draft.Generation = 7;
                var comp = VampireWizardComponents.BuildForConceptStep(draft);
                var buttons = GetRows(comp).SelectMany(r => r.Components).OfType<ButtonComponent>().ToList();
                Assert.Contains(buttons, b => b.CustomId.Contains("reset_generation"));
                Assert.Contains(buttons, b => b.Label != null && b.Label.Contains("7"));
            }

            [Fact]
            public void BuildConceptStep_HidesResetGeneration_WhenDefault()
            {
                var draft = NewDraft();
                var comp = VampireWizardComponents.BuildForConceptStep(draft);
                var buttons = GetRows(comp).SelectMany(r => r.Components).OfType<ButtonComponent>().ToList();
                Assert.DoesNotContain(buttons, b => b.CustomId.Contains("reset_generation"));
            }

            [Fact]
            public void WizardActions_RoundTrip_NewSireGenActions()
            {
                var cid = Guid.NewGuid();
                Assert.True(VampireWizardComponents.TryParse(
                    VampireWizardComponents.BuildCustomId(VampireWizardAction.SetSire, cid),
                    out var a1, out _));
                Assert.Equal(VampireWizardAction.SetSire, a1);

                Assert.True(VampireWizardComponents.TryParse(
                    VampireWizardComponents.BuildCustomId(VampireWizardAction.ClearSire, cid),
                    out var a2, out _));
                Assert.Equal(VampireWizardAction.ClearSire, a2);

                Assert.True(VampireWizardComponents.TryParse(
                    VampireWizardComponents.BuildCustomId(VampireWizardAction.SetGeneration, cid),
                    out var a3, out _));
                Assert.Equal(VampireWizardAction.SetGeneration, a3);

                Assert.True(VampireWizardComponents.TryParse(
                    VampireWizardComponents.BuildCustomId(VampireWizardAction.ResetGeneration, cid),
                    out var a4, out _));
                Assert.Equal(VampireWizardAction.ResetGeneration, a4);
            }

            // ── Step 2 build ─────────────────────────────────────────────────────

                        [Fact]
            public void BuildAttributesStep_HasPrioritySelectMenuFirst()
            {
                var draft = NewDraft();
                var comp = VampireWizardComponents.BuildForAttributesStep(draft);
                var menus = GetRows(comp)
                    .SelectMany(r => r.Components)
                    .OfType<SelectMenuComponent>()
                    .ToList();
                Assert.NotEmpty(menus);
                Assert.Equal(6, menus[0].Options.Count);
                Assert.Contains("attr_priority", menus[0].CustomId);
            }

            [Fact]
            public void BuildAttributesStep_HasThreeGroupSelectMenus()
            {
                var draft = NewDraft();
                var comp = VampireWizardComponents.BuildForAttributesStep(draft);
                var menus = GetRows(comp)
                    .SelectMany(r => r.Components)
                    .OfType<SelectMenuComponent>()
                    .ToList();
                // 1 (приоритет) + 3 (группы) = 4 SelectMenu.
                Assert.Equal(4, menus.Count);
                var groupMenus = menus.Skip(1).ToList();
                Assert.All(groupMenus, m => Assert.Equal(6, m.Options.Count));
                Assert.Contains(groupMenus[0].Options, o => o.Label.Contains("Сила"));
                Assert.Contains(groupMenus[0].Options, o => o.Label.Contains("Ловкость"));
                Assert.Contains(groupMenus[0].Options, o => o.Label.Contains("Выносливость"));
                Assert.Contains(groupMenus[1].Options, o => o.Label.Contains("Обаяние"));
                Assert.Contains(groupMenus[1].Options, o => o.Label.Contains("Манипуляция"));
                Assert.Contains(groupMenus[1].Options, o => o.Label.Contains("Привлекательность"));
                Assert.Contains(groupMenus[2].Options, o => o.Label.Contains("Восприятие"));
                Assert.Contains(groupMenus[2].Options, o => o.Label.Contains("Интеллект"));
                Assert.Contains(groupMenus[2].Options, o => o.Label.Contains("Смекалка"));
                        }

                        [Fact]
                        public void BuildAttributesStep_NextButtonHiddenUntilComplete()
                        {
                            var draft = NewDraft();
                            var comp = VampireWizardComponents.BuildForAttributesStep(draft);
                            var buttons = GetRows(comp).SelectMany(r => r.Components).OfType<ButtonComponent>().ToList();
                            Assert.DoesNotContain(buttons, b => b.CustomId.Contains(":next:") && b.Label != null && b.Label.Contains("Шаг 3"));
                        }

                        [Fact]
                        public void BuildAttributesStep_NextButtonVisibleWhenComplete()
                        {
                            var draft = NewDraft();
                            draft.AttributesPriority = VampireAttributePriority.PhysicalPrimary.ToString();
                                                    // PhysicalPrimary: Физ=7, Соц=5, Мент=3.
                                                    // Базовые 1,1,1 — потрачено в группе: sum - 3.
                                                    // Физ: 4+2+4 - 3 = 7 ✓
                                                    // Соц: 3+2+3 - 3 = 5 ✓
                                                    // Мент: 2+1+3 - 3 = 3 ✓
                                                    draft.AttributesStruct = new VampireAttributes
                                                    {
                                                        Strength = 4, Dexterity = 2, Stamina = 4,
                                                        Charisma = 3, Manipulation = 2, Appearance = 3,
                                                        Perception = 2, Intelligence = 1, Wits = 3
                                                    };
                                                    var comp = VampireWizardComponents.BuildForAttributesStep(draft);
                                                    var buttons = GetRows(comp).SelectMany(r => r.Components).OfType<ButtonComponent>().ToList();
                                                    Assert.Contains(buttons, b => b.CustomId.Contains(":next:") && b.Label != null && b.Label.Contains("Шаг 3"));
                                                }

                        [Fact]
                        public void BuildAttributesStep_BackToConceptAlwaysPresent()
                        {
                            var draft = NewDraft();
                            var comp = VampireWizardComponents.BuildForAttributesStep(draft);
                            var buttons = GetRows(comp).SelectMany(r => r.Components).OfType<ButtonComponent>().ToList();
                            Assert.Contains(buttons, b => b.CustomId.Contains("back_to_concept:"));
                        }

                        [Fact]
                        public void BuildAttributesStep_RowsWithinDiscordLimit()
                        {
                            var draft = NewDraft();
                            var comp = VampireWizardComponents.BuildForAttributesStep(draft);
                            // Discord: 5 ActionRows max.
                                                        var rowCount = GetRows(comp).Count();
                                                        Assert.True(rowCount <= 5,
                                                            $"UI должно помещаться в лимит Discord (5 рядов), сейчас {rowCount}.");
                                                    }

                        [Fact]
                        public void TryParseWithArg_RoundTrip_AttrInc()
                        {
                            var cid = Guid.NewGuid();
                            var id = VampireWizardComponents.BuildCustomIdWithArg(
                                VampireWizardAction.AttrInc, cid, "Physical");
                            Assert.True(VampireWizardComponents.TryParseWithArg(id, out var a, out var rid, out var arg));
                            Assert.Equal(VampireWizardAction.AttrInc, a);
                            Assert.Equal(cid, rid);
                            Assert.Equal("Physical", arg);
                        }

                        [Fact]
                        public void TryParseWithArg_RejectsStandardCustomId()
                        {
                            var cid = Guid.NewGuid();
                            var id = VampireWizardComponents.BuildCustomId(VampireWizardAction.SetConcept, cid);
                            // Без 4-й части TryParseWithArg должен вернуть false.
                            Assert.False(VampireWizardComponents.TryParseWithArg(id, out _, out _, out _));
                        }

            // Хелпер: достать ряды из MessageComponent.
            // Использует публичное свойство `Components` (как в существующих тестах).
            private static System.Collections.Generic.IEnumerable<ActionRowComponent> GetRows(MessageComponent comp)
            {
                return comp.Components.OfType<ActionRowComponent>();
            }
        }
