using System;
using System.ComponentModel;
using Discord;

namespace RPBot.VtM;

/// <summary>
/// Действия для кнопок визарда создания персонажа (в DM).
/// </summary>
/// <remarks>
/// <para>Эти кнопки показываются только в DM у создателя визарда
/// и обрабатываются отдельно от кнопок листа персонажа
/// (<see cref="VampireSheetAction"/>).</para>
/// <para>CustomId формат: <c>vtm_wiz:{action}:{characterId}</c>.</para>
/// </remarks>
public enum VampireWizardAction
{
    /// <summary>Задать поле «Амплуа» (concept).</summary>
    SetConcept,
    /// <summary>Задать поле «Клан» (clan).</summary>
    SetClan,
    /// <summary>Задать поле «Натура» (nature).</summary>
    SetNature,
    /// <summary>Задать поле «Маска» (demeanor).</summary>
    SetDemeanor,
    /// <summary>Задать поле «Описание» (bio) — опционально.</summary>
    SetBio,
    /// <summary>Пропустить описание (оставить Bio пустым).</summary>
    SkipBio,
    /// <summary>Очистить поле «Описание».</summary>
    ClearBio,
    /// <summary>Очистить поле «Амплуа».</summary>
    ClearConcept,
    /// <summary>Очистить поле «Клан».</summary>
    ClearClan,
    /// <summary>Очистить поле «Натура».</summary>
    ClearNature,
    /// <summary>Очистить поле «Маска».</summary>
    ClearDemeanor,
    /// <summary>Задать поле «Сир» (Sire) — кто обратил. Опционально.</summary>
    SetSire,
    /// <summary>Очистить поле «Сир».</summary>
    ClearSire,
    /// <summary>Задать поле «Поколение» (Generation, 3-15).</summary>
    SetGeneration,
    /// <summary>Сбросить поколение к 13 (дефолт).</summary>
    ResetGeneration,
    /// <summary>Перейти к следующему шагу (доступно, когда все обязательные поля заполнены).</summary>
    Next,
    /// <summary>Отменить визард и закрыть DM.</summary>
    Cancel,

        // ── Шаг 2 «Характеристики 7/5/3» ─────────────────────────────────────

        /// <summary>Увеличить атрибут на 1 (Шаг 2). Action-arg = имя атрибута.</summary>
        AttrInc,
        /// <summary>Уменьшить атрибут на 1 (Шаг 2).</summary>
        AttrDec,
                /// <summary>SelectMenu выбора приоритета 7/5/3 (Шаг 2).</summary>
                AttrPriority,
                /// <summary>Сбросить прогресс Шага 2 (только атрибуты; приоритет сохраняется).</summary>
                ResetAttrProgress,
                /// <summary>Сбросить всё на Шаге 2 (приоритет и атрибуты).</summary>
                ResetAttrAll,
                /// <summary>Вернуться на Шаг 1 (Концепция).</summary>
                BackToConcept,

                // ── Шаг 3 «Способности 13/9/5» ─────────────────────────────────────

                /// <summary>SelectMenu выбора приоритета 13/9/5 (Шаг 3).</summary>
                AbilityPriority,
                /// <summary>Увеличить способность на 1 (Шаг 3). Action-arg = имя способности.</summary>
                AbilityInc,
                /// <summary>Уменьшить способность на 1 (Шаг 3).</summary>
                AbilityDec,
                /// <summary>Задать специализацию (SelectMenu выбора подсказки или «Своя»). Action-arg = имя параметра.</summary>
                SpecChoice,
                /// <summary>Установить выбранную специализацию (SelectMenu). Action-arg = имя параметра.</summary>
                SpecSet,
                /// <summary>Сбросить прогресс Шага 3 (только способности; приоритет сохраняется).</summary>
                ResetAbilityProgress,
                /// <summary>Сбросить всё на Шаге 3 (приоритет и способности).</summary>
                ResetAbilityAll,
                /// <summary>Вернуться на Шаг 2 (Характеристики).</summary>
                BackToAttributes,

                // ── Шаг 4 «Преимущества» (дисциплины/факты/добродетели) ────────

                /// <summary>Увеличить дисциплину на 1 (Шаг 4.1). Action-arg = имя дисциплины.</summary>
                DisciplineInc,
                /// <summary>Уменьшить дисциплину на 1 (Шаг 4.1).</summary>
                DisciplineDec,
                /// <summary>Переименовать дисциплину Каитифа (Шаг 4.1, открывает текстовый ввод). Action-arg = старое имя.</summary>
                DisciplineRename,
                /// <summary>Добавить факт биографии (Шаг 4.2, открывает текстовый ввод имени).</summary>
                BackgroundAdd,
                /// <summary>Удалить факт биографии (Шаг 4.2). Action-arg = имя факта.</summary>
                BackgroundRemove,
                /// <summary>Переименовать факт биографии (Шаг 4.2, открывает текстовый ввод). Action-arg = старое имя.</summary>
                BackgroundRename,
                /// <summary>Увеличить ранг факта на 1 (Шаг 4.2). Action-arg = имя факта.</summary>
                BackgroundInc,
                /// <summary>Уменьшить ранг факта на 1 (Шаг 4.2). Action-arg = имя факта.</summary>
                BackgroundDec,
                /// <summary>Увеличить добродетель на 1 (Шаг 4.3). Action-arg = имя добродетели.</summary>
                VirtueInc,
                /// <summary>Уменьшить добродетель на 1 (Шаг 4.3).</summary>
                VirtueDec,
                /// <summary>Сбросить прогресс всего Шага 4 (дисциплины/факты/добродетели).</summary>
                ResetAdvProgress,
                /// <summary>Сбросить всё на Шаге 4 (то же, что и <see cref="ResetAdvProgress"/>).</summary>
                ResetAdvAll,
                /// <summary>Вернуться на Шаг 3 (Способности).</summary>
                BackToAbilities,
                /// <summary>Перейти к Шагу 4.2 (Факты биографии).</summary>
                NextAdvToBackgrounds,
                /// <summary>Перейти к Шагу 4.3 (Добродетели).</summary>
                NextAdvToVirtues,
                /// <summary>Перейти к Шагу 5 (Последние штрихи).</summary>
                NextAdvToFinishing,

                                // ── Шаг 5 «Последние штрихи» ────────────────────────────────

                                /// <summary>SelectMenu свободных пунктов: target+field+sign. Action-arg = "{sign}:{target}:{field}".</summary>
                                FinishingInc,
                                /// <summary>Кнопка «Сбросить всё» на Шаге 5 (откатить все траты).</summary>
                                FinishingReset,
                                /// <summary>Завершить Шаг 5 и перейти к Шагу 6 (или показать лист).</summary>
                                FinishingDone,
                                /// <summary>Вернуться на Шаг 4.3 (Добродетели).</summary>
                                BackToAdvantages,
                    }

/// <summary>
/// Кнопки визарда создания персонажа для DM-сообщения.
/// </summary>
public static class VampireWizardComponents
{
    public const string Prefix = "vtm_wiz";

    /// <summary>
    /// Собрать набор кнопок для шага 1 (концепция) с учётом текущего состояния черновика.
    /// </summary>
    /// <param name="draft">Текущий черновик персонажа (для определения, какие кнопки показывать).</param>
    /// <returns>Компонент с кнопками в 1-2 ряда.</returns>
    public static MessageComponent BuildForConceptStep(VampireCharacter draft)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (draft.CharacterId == Guid.Empty)
            throw new ArgumentException("CharacterId обязателен", nameof(draft));

        var cb = new ComponentBuilder();

        // Ряд 1: основные поля (4 кнопки).
        cb.WithButton("Амплуа", BuildCustomId(VampireWizardAction.SetConcept, draft.CharacterId), ButtonStyle.Secondary)
          .WithButton("Клан",   BuildCustomId(VampireWizardAction.SetClan,   draft.CharacterId), ButtonStyle.Secondary)
          .WithButton("Натура", BuildCustomId(VampireWizardAction.SetNature, draft.CharacterId), ButtonStyle.Secondary)
          .WithButton("Маска",  BuildCustomId(VampireWizardAction.SetDemeanor, draft.CharacterId), ButtonStyle.Secondary);

        // Ряд 2: сир (опционально).
        if (string.IsNullOrWhiteSpace(draft.Sire))
        {
            cb.WithButton("Указать сира", BuildCustomId(VampireWizardAction.SetSire, draft.CharacterId), ButtonStyle.Secondary);
        }
        else
        {
            cb.WithButton($"Сир: {TruncateLabel(draft.Sire, 18)}", BuildCustomId(VampireWizardAction.SetSire, draft.CharacterId), ButtonStyle.Secondary)
              .WithButton("Очистить сира", BuildCustomId(VampireWizardAction.ClearSire, draft.CharacterId), ButtonStyle.Secondary);
        }

        // Ряд 3: поколение (3-15). Дефолт 13.
        var genStyle = draft.Generation == 13 ? ButtonStyle.Secondary : ButtonStyle.Primary;
        cb.WithButton($"Поколение: {draft.Generation}", BuildCustomId(VampireWizardAction.SetGeneration, draft.CharacterId), genStyle);
        if (draft.Generation != 13)
        {
            cb.WithButton("Сбросить поколение (13)", BuildCustomId(VampireWizardAction.ResetGeneration, draft.CharacterId), ButtonStyle.Secondary);
        }

        // Ряд 4: описание (bio) — пропустить или задать.
        if (string.IsNullOrWhiteSpace(draft.Bio))
        {
            cb.WithButton("Добавить описание", BuildCustomId(VampireWizardAction.SetBio, draft.CharacterId), ButtonStyle.Secondary)
              .WithButton("Пропустить описание", BuildCustomId(VampireWizardAction.SkipBio, draft.CharacterId), ButtonStyle.Secondary);
        }
        else
        {
            cb.WithButton("Изменить описание", BuildCustomId(VampireWizardAction.SetBio, draft.CharacterId), ButtonStyle.Secondary)
              .WithButton("Очистить описание", BuildCustomId(VampireWizardAction.ClearBio, draft.CharacterId), ButtonStyle.Secondary);
        }

        // Ряд 5: «Далее» (если шаг завершён) + «Отмена».
        if (VampireCreateResolver.IsConceptComplete(draft))
        {
            cb.WithButton("Далее → Шаг 2 (характеристики)", BuildCustomId(VampireWizardAction.Next, draft.CharacterId), ButtonStyle.Success);
        }
        cb.WithButton("Отмена", BuildCustomId(VampireWizardAction.Cancel, draft.CharacterId), ButtonStyle.Danger);

        return cb.Build();
    }

            /// <summary>
            /// Собрать компоненты для Шага 2 «Характеристики 7/5/3».
            ///
            /// <para>Структура (Discord-лимит: 5 рядов × 5 кнопок):</para>
            /// <list type="bullet">
            /// <item>Ряд 1: SelectMenu с 6 приоритетами групп.</item>
            /// <item>Ряд 2: 3 кнопки −/+ для Физ (Сила, Ловкость, Выносливость).</item>
            /// <item>Ряд 3: 3 кнопки −/+ для Соц (Обаяние, Манипуляция, Привлекательность).</item>
            /// <item>Ряд 4: 3 кнопки −/+ для Мент (Восприятие, Интеллект, Смекалка).</item>
            /// <item>Ряд 5: «Назад / Сбросить / Отмена / Далее».</item>
            /// </list>
            /// </summary>
            public static MessageComponent BuildForAttributesStep(VampireCharacter draft)
            {
                if (draft == null) throw new ArgumentNullException(nameof(draft));
                if (draft.CharacterId == Guid.Empty)
                    throw new ArgumentException("CharacterId обязателен", nameof(draft));

                var cb = new ComponentBuilder();

                // Ряд 1: SelectMenu с приоритетами.
                // Ряд 1: SelectMenu с приоритетами.
                                var menu = new SelectMenuBuilder()
                                    .WithCustomId(BuildCustomId(VampireWizardAction.AttrPriority, draft.CharacterId))
                                    .WithPlaceholder(HasPriority(draft)
                                        ? $"Приоритет: {draft.AttributesPriority} (по группам)"
                                        : "Выберите приоритет групп (7/5/3)…");
                                foreach (var p in VampireAttributePriorityExtensions.All)
                                {
                                    menu.AddOption(p.HumanName(), p.ToString());
                                }
                                cb.WithSelectMenu(menu);

                                // Ряды 2-4: по одному SelectMenu на группу — 6 опций (+/-, по 3 атрибута).
                                cb.WithSelectMenu(BuildAttrGroupSelect(draft, VampireAttributeGroup.Physical));
                                cb.WithSelectMenu(BuildAttrGroupSelect(draft, VampireAttributeGroup.Social));
                                cb.WithSelectMenu(BuildAttrGroupSelect(draft, VampireAttributeGroup.Mental));

                                                // Ряд 5: вспомогательные действия.
                cb.WithButton("⬅ Назад (Шаг 1)", BuildCustomId(VampireWizardAction.BackToConcept, draft.CharacterId), ButtonStyle.Secondary)
                  .WithButton("Сбросить прогресс", BuildCustomId(VampireWizardAction.ResetAttrProgress, draft.CharacterId), ButtonStyle.Secondary)
                  .WithButton("Сбросить всё", BuildCustomId(VampireWizardAction.ResetAttrAll, draft.CharacterId), ButtonStyle.Danger);

                if (VampireAttributesResolver.IsAttributesComplete(draft))
                {
                    cb.WithButton("Далее → Шаг 3 (способности)", BuildCustomId(VampireWizardAction.Next, draft.CharacterId), ButtonStyle.Success);
                }

                return cb.Build();
            }

                            /// <summary>
                            /// SelectMenu для изменения одного атрибута в группе.
                            /// Опции: +Сила, −Сила, +Ловк, −Ловк, +Выносл, −Выносл (6 опций, макс Discord).
                            /// Value: <c>{sign}:{attributeName}</c>, например <c>+:Сила</c>.
                            /// </summary>
                            private static SelectMenuBuilder BuildAttrGroupSelect(
                                VampireCharacter draft,
                                VampireAttributeGroup group)
                            {
                                var groupLabel = group switch
                                {
                                    VampireAttributeGroup.Physical => "Физ",
                                    VampireAttributeGroup.Social   => "Соц",
                                    VampireAttributeGroup.Mental   => "Мент",
                                    _ => group.ToString()
                                };

                                var remaining = VampireAttributesResolver.RemainingInGroup(draft, group);
                                var placeholder = $"{groupLabel} (±): ост. {remaining}";

                                var menu = new SelectMenuBuilder()
                                    .WithCustomId(BuildCustomIdWithArg(VampireWizardAction.AttrInc, draft.CharacterId, group.ToString()))
                                    .WithPlaceholder(placeholder);

                                foreach (var name in VampireAttributeCatalog.NamesInGroup(group))
                                {
                                    var baseVal = GetBaseValue(draft, name);
                                    var cur = GetAttrValue(draft, name);

                                    // Опция «+».
                                    var incVal = $"+:{name}";
                                    var incDesc = $"текущее: {cur}, макс шага 2 = 7";
                                    if (cur >= 7) incDesc = "уже 7 (макс шага 2)";
                                    // Для Привлекательности Носферату/Самеди база = 0 → + невозможен.
                                    if (baseVal == 0 && cur == 0)
                                        incDesc = "зачёркнуто изъяном клана (всегда 0)";
                                    menu.AddOption(new SelectMenuOptionBuilder()
                                        .WithLabel($"+ {name} ({cur})")
                                        .WithValue(incVal)
                                        .WithDescription(incDesc));

                                    // Опция «−».
                                    var decVal = $"−:{name}";
                                    var decDesc = $"текущее: {cur}, база = {baseVal}";
                                    if (cur <= baseVal) decDesc = "уже на базе";
                                    menu.AddOption(new SelectMenuOptionBuilder()
                                        .WithLabel($"− {name} ({cur})")
                                        .WithValue(decVal)
                                        .WithDescription(decDesc));
                                }

                                return menu;
                            }

                            private static bool HasPriority(VampireCharacter d)
                            {
                                return !string.IsNullOrEmpty(d.AttributesPriority);
                            }

                                // ── Шаг 3 «Способности 13/9/5» ─────────────────────────────────────

                                /// <summary>
                                /// UI Шага 3: 5 рядов.
                                ///   1) SelectMenu выбора приоритета 13/9/5.
                                ///   2-4) SelectMenu по группам (Таланты/Навыки/Знания) — ± по 10 способностей.
                                ///   5) Назад / Сбросить / Сбросить всё / (Далее если завершено).
                                /// </summary>
                                public static MessageComponent BuildForAbilitiesStep(VampireCharacter draft)
                                {
                                    if (draft == null) throw new ArgumentNullException(nameof(draft));
                                    if (draft.CharacterId == Guid.Empty)
                                        throw new ArgumentException("CharacterId обязателен", nameof(draft));

                                    var cb = new ComponentBuilder();

                                    // Ряд 1: SelectMenu с приоритетами 13/9/5.
                                    var menu = new SelectMenuBuilder()
                                        .WithCustomId(BuildCustomId(VampireWizardAction.AbilityPriority, draft.CharacterId))
                                        .WithPlaceholder(HasAbilityPriority(draft)
                                            ? $"Приоритет: {draft.AbilitiesPriority}"
                                            : "Выберите приоритет групп (13/9/5)…");
                                    foreach (var p in VampireAbilityPriorityExtensions.All)
                                    {
                                        menu.AddOption(p.HumanName(), p.ToString());
                                    }
                                    cb.WithSelectMenu(menu);

                                    // Ряды 2-4: SelectMenu по группам. В каждом — 20 опций (±10 способностей).
                                    cb.WithSelectMenu(BuildAbilityGroupSelect(draft, VampireAbilityGroup.Talents));
                                    cb.WithSelectMenu(BuildAbilityGroupSelect(draft, VampireAbilityGroup.Skills));
                                    cb.WithSelectMenu(BuildAbilityGroupSelect(draft, VampireAbilityGroup.Knowledges));

                                    // Ряд 5: навигация.
                                    cb.WithButton("⬅ Назад (Шаг 2)", BuildCustomId(VampireWizardAction.BackToAttributes, draft.CharacterId), ButtonStyle.Secondary)
                                      .WithButton("Сбросить прогресс", BuildCustomId(VampireWizardAction.ResetAbilityProgress, draft.CharacterId), ButtonStyle.Secondary)
                                      .WithButton("Сбросить всё", BuildCustomId(VampireWizardAction.ResetAbilityAll, draft.CharacterId), ButtonStyle.Danger);

                                    if (VampireAbilitiesResolver.IsAbilitiesComplete(draft))
                                    {
                                        cb.WithButton("Далее → Шаг 4 (преимущества)", BuildCustomId(VampireWizardAction.Next, draft.CharacterId), ButtonStyle.Success);
                                    }

                                    return cb.Build();
                                }

                                // ── Шаг 4 «Преимущества» — 3 экрана ────────────────────────────────

                                /// <summary>
                                /// UI Шага 4.1 «Дисциплины»: 1 SelectMenu с 3 клановыми слотами (± по 1).
                                /// Для Каитифа каждый слот можно переименовать.
                                /// </summary>
                                public static MessageComponent BuildForDisciplinesStep(VampireCharacter draft)
                                {
                                    if (draft == null) throw new ArgumentNullException(nameof(draft));
                                    if (draft.CharacterId == Guid.Empty)
                                        throw new ArgumentException("CharacterId обязателен", nameof(draft));

                                    var cb = new ComponentBuilder();
                                    var slots = VampireAdvantagesResolver.EffectiveDisciplineSlots(draft);
                                    var isCaitiff = VampireAdvantagesCatalog.IsCaitiff(draft.Clan);

                                    if (slots.Count == 0)
                                    {
                                        // Клан ещё не выбран — даём подсказку.
                                        cb.WithButton("⬅ Назад (Шаг 3)", BuildCustomId(VampireWizardAction.BackToAbilities, draft.CharacterId), ButtonStyle.Secondary);
                                    }
                                    else
                                    {
                                        // Один SelectMenu с парами опций (± для каждой дисциплины).
                                        cb.WithSelectMenu(BuildDisciplineSelect(draft));

                                        // Для Каитифа — кнопки «Переименовать» на каждый слот.
                                        if (isCaitiff)
                                        {
                                            foreach (var name in VampireAdvantagesCatalog.GetDisciplineSlots(draft.Clan))
                                            {
                                                cb.WithButton(
                                                    $"✎ {Truncate(name, 16)}",
                                                    BuildCustomIdWithArg(VampireWizardAction.DisciplineRename, draft.CharacterId, name),
                                                    ButtonStyle.Secondary);
                                            }
                                        }
                                    }

                                    cb.WithButton("⬅ Назад (Шаг 3)", BuildCustomId(VampireWizardAction.BackToAbilities, draft.CharacterId), ButtonStyle.Secondary)
                                      .WithButton("Сбросить прогресс", BuildCustomId(VampireWizardAction.ResetAdvProgress, draft.CharacterId), ButtonStyle.Secondary)
                                      .WithButton("Сбросить всё", BuildCustomId(VampireWizardAction.ResetAdvAll, draft.CharacterId), ButtonStyle.Danger);

                                    if (VampireAdvantagesResolver.IsDisciplinesComplete(draft))
                                    {
                                        cb.WithButton("Далее → 4.2 (факты)", BuildCustomId(VampireWizardAction.NextAdvToBackgrounds, draft.CharacterId), ButtonStyle.Success);
                                    }

                                    return cb.Build();
                                }

                                /// <summary>
                                /// UI Шага 4.2 «Факты биографии»: кнопка «Добавить факт» (открывает текстовый ввод)
                                /// + ряд SelectMenu на каждый имеющийся факт (± по 1 ранг, удалить, переименовать).
                                /// </summary>
                                public static MessageComponent BuildForBackgroundsStep(VampireCharacter draft)
                                {
                                    if (draft == null) throw new ArgumentNullException(nameof(draft));
                                    if (draft.CharacterId == Guid.Empty)
                                        throw new ArgumentException("CharacterId обязателен", nameof(draft));

                                    var cb = new ComponentBuilder();

                                    // Ряд 1: «Добавить факт» (открывает текстовый ввод).
                                    cb.WithButton("Добавить факт", BuildCustomId(VampireWizardAction.BackgroundAdd, draft.CharacterId), ButtonStyle.Secondary);

                                    // По ряду на каждый факт с опциями ±/Удалить/Переименовать.
                                    if (draft.Backgrounds != null && draft.Backgrounds.Count > 0)
                                    {
                                        foreach (var kv in draft.Backgrounds)
                                        {
                                            cb.WithSelectMenu(BuildBackgroundSelect(draft, kv.Key, kv.Value));
                                        }
                                    }

                                    cb.WithButton("⬅ Назад (4.1)", BuildCustomId(VampireWizardAction.BackToAbilities, draft.CharacterId), ButtonStyle.Secondary)
                                      .WithButton("Сбросить прогресс", BuildCustomId(VampireWizardAction.ResetAdvProgress, draft.CharacterId), ButtonStyle.Secondary)
                                      .WithButton("Сбросить всё", BuildCustomId(VampireWizardAction.ResetAdvAll, draft.CharacterId), ButtonStyle.Danger);

                                    if (VampireAdvantagesResolver.IsBackgroundsComplete(draft))
                                    {
                                        cb.WithButton("Далее → 4.3 (добродетели)", BuildCustomId(VampireWizardAction.NextAdvToVirtues, draft.CharacterId), ButtonStyle.Success);
                                    }

                                    return cb.Build();
                                }

                                /// <summary>
                                /// UI Шага 4.3 «Добродетели»: 1 SelectMenu с 3 фиксированными добродетелями (± по 1).
                                /// </summary>
                                public static MessageComponent BuildForVirtuesStep(VampireCharacter draft)
                                {
                                    if (draft == null) throw new ArgumentNullException(nameof(draft));
                                    if (draft.CharacterId == Guid.Empty)
                                        throw new ArgumentException("CharacterId обязателен", nameof(draft));

                                    var cb = new ComponentBuilder();
                                    cb.WithSelectMenu(BuildVirtueSelect(draft));

                                    cb.WithButton("⬅ Назад (4.2)", BuildCustomId(VampireWizardAction.BackToAbilities, draft.CharacterId), ButtonStyle.Secondary)
                                      .WithButton("Сбросить прогресс", BuildCustomId(VampireWizardAction.ResetAdvProgress, draft.CharacterId), ButtonStyle.Secondary)
                                      .WithButton("Сбросить всё", BuildCustomId(VampireWizardAction.ResetAdvAll, draft.CharacterId), ButtonStyle.Danger);

                                    if (VampireAdvantagesResolver.IsVirtuesComplete(draft))
                                    {
                                        cb.WithButton("Далее → Шаг 5 (финал)", BuildCustomId(VampireWizardAction.NextAdvToFinishing, draft.CharacterId), ButtonStyle.Success);
                                    }

                                    return cb.Build();
                                }

                                                                    /// <summary>
                                                                    /// UI Шага 5 «Последние штрихи»: производные (read-only) +
                                                                    /// SelectMenu для свободных пунктов 15 (target×field×sign).
                                                                    /// </summary>
                                                                    public static MessageComponent BuildForFinishingStep(VampireCharacter draft)
                                                                    {
                                                                        if (draft == null) throw new ArgumentNullException(nameof(draft));
                                                                        if (draft.CharacterId == Guid.Empty)
                                                                            throw new ArgumentException("CharacterId обязателен", nameof(draft));

                                                                        var cb = new ComponentBuilder();
                                                                        cb.WithSelectMenu(BuildFinishingSelect(draft));

                                                                        // Специализации доступны только после полного распределения freebie-пула.
                                                                        // (V20 стр. 101: специализация требуется при значении ≥ 4; Шаг 2/3 дают
                                                                        // максимум 3, поэтому ждём Шаг 5.)
                                                                        if (VampireFinishingResolver.FreebiesExhausted(draft))
                                                                        {
                                                                            var specMenu = BuildSpecializationSelect(draft);
                                                                            if (specMenu != null) cb.WithSelectMenu(specMenu);
                                                                        }

                                                                        cb.WithButton("⬅ Назад (4.3)", BuildCustomId(VampireWizardAction.BackToAdvantages, draft.CharacterId), ButtonStyle.Secondary)
                                                                          .WithButton("Сбросить всё", BuildCustomId(VampireWizardAction.FinishingReset, draft.CharacterId), ButtonStyle.Danger);

                                                                        cb.WithButton("Готово → лист", BuildCustomId(VampireWizardAction.FinishingDone, draft.CharacterId), ButtonStyle.Success);

                                                                        return cb.Build();
                                                                    }

                                                                    /// <summary>
                                                                    /// SelectMenu Шага 5: каждая опция соответствует одной ячейке и знаку.
                                                                    /// Формат value: "{sign}:{target}:{field}".
                                                                    /// </summary>
                                                                    private static SelectMenuBuilder BuildFinishingSelect(VampireCharacter draft)
                                                                    {
                                                                        var remaining = VampireFinishingResolver.RemainingFreebies(draft);
                                                                        var menu = new SelectMenuBuilder()
                                                                            .WithCustomId(BuildCustomId(VampireWizardAction.FinishingInc, draft.CharacterId))
                                                                            .WithPlaceholder($"Свободные пункты (+): ост. {remaining}");

                                                                        AddOptionsFor(menu, draft, VampireFinishingResolver.FreebieTarget.Attribute,  draft.Attributes,   null);
                                                                        AddOptionsFor(menu, draft, VampireFinishingResolver.FreebieTarget.Ability,    null,                draft.AbilitiesStruct);
                                                                        AddOptionsFor(menu, draft, VampireFinishingResolver.FreebieTarget.Discipline, draft.Disciplines,   null);
                                                                        AddOptionsFor(menu, draft, VampireFinishingResolver.FreebieTarget.Background, draft.Backgrounds,   null);
                                                                        AddOptionsFor(menu, draft, VampireFinishingResolver.FreebieTarget.Virtue,      draft.Virtues,       null);
                                                                        AddHumanityWillpowerOptions(menu, draft);

                                                                        return menu;
                                                                    }

                                                                    /// <summary>
                                                                    /// SelectMenu специализаций Шага 5: показывает характеристики и способности
                                                                    /// с итоговым значением ≥ 4. Возвращает null, если ни одна не подходит
                                                                    /// (тогда ряд не добавляется в UI).
                                                                    /// </summary>
                                                                    internal static SelectMenuBuilder? BuildSpecializationSelect(VampireCharacter draft)
                                                                    {
                                                                        var eligible = new List<(string Name, int Cur, string Spec)>();

                                                                        // Характеристики.
                                                                        foreach (var name in new[] {
                                                                            VampireAttributeCatalog.Physical, VampireAttributeCatalog.Social, VampireAttributeCatalog.Mental
                                                                        }.SelectMany(g => g))
                                                                        {
                                                                            // Skip база = 0 (Носферату/Последователь Сета — Привлекательность).
                                                                            if (GetBaseValue(draft, name) == 0 && GetAttrValue(draft, name) == 0) continue;
                                                                            var cur = draft.GetAttributeValue(name);
                                                                            if (cur < 4) continue;
                                                                            var spec = VampireAbilitiesResolver.GetSpecialization(draft, name);
                                                                            eligible.Add((name, cur, spec));
                                                                        }

                                                                        // Способности.
                                                                        foreach (var group in new[] { VampireAbilityGroup.Talents, VampireAbilityGroup.Skills, VampireAbilityGroup.Knowledges })
                                                                        {
                                                                            foreach (var name in VampireAbilitiesCatalog.NamesInGroup(group))
                                                                            {
                                                                                var cur = draft.GetAbilityValue(name);
                                                                                if (cur < 4) continue;
                                                                                var spec = VampireAbilitiesResolver.GetSpecialization(draft, name);
                                                                                eligible.Add((name, cur, spec));
                                                                            }
                                                                        }

                                                                        if (eligible.Count == 0) return null;

                                                                        var menu = new SelectMenuBuilder()
                                                                            .WithCustomId(BuildCustomId(VampireWizardAction.SpecChoice, draft.CharacterId))
                                                                            .WithPlaceholder($"Специализация ({eligible.Count} пригодны, ≥4)");

                                                                        foreach (var (name, cur, spec) in eligible)
                                                                        {
                                                                            var hasSpec = !string.IsNullOrEmpty(spec);
                                                                            var label = hasSpec ? $"{Truncate(name, 20)} ★" : name;
                                                                            var desc = hasSpec
                                                                                ? $"сейчас: {spec} — задать новую"
                                                                                : $"значение {cur} — задать";
                                                                            menu.AddOption(new SelectMenuOptionBuilder()
                                                                                .WithLabel(TruncateLabel(label, 25))
                                                                                .WithValue(name)
                                                                                .WithDescription(TruncateLabel(desc, 50)));
                                                                        }
                                                                        return menu;
                                                                    }

                                                                    /// <summary>
                                                                    /// Добавить две одиночные опции: «+ Человечность» и «+ Воля».
                                                                    /// Для этих целей нет словаря/структуры — каждая цель представлена ровно одной опцией.
                                                                    /// </summary>
                                                                    private static void AddHumanityWillpowerOptions(
                                                                        SelectMenuBuilder menu,
                                                                        VampireCharacter draft)
                                                                    {
                                                                        AddHumanityWillpowerOne(menu, draft, VampireFinishingResolver.FreebieTarget.Humanity,  "Человечность");
                                                                        AddHumanityWillpowerOne(menu, draft, VampireFinishingResolver.FreebieTarget.Willpower, "Воля");
                                                                    }

                                                                    private static void AddHumanityWillpowerOne(
                                                                        SelectMenuBuilder menu,
                                                                        VampireCharacter draft,
                                                                        VampireFinishingResolver.FreebieTarget target,
                                                                        string label)
                                                                    {
                                                                        var cost = VampireFinishingResolver.CostOf(target);
                                                                        var remaining = VampireFinishingResolver.RemainingFreebies(draft);
                                                                        var cur = VampireFinishingResolver.ReadFieldValue(draft, target, label);
                                                                        var (hardCap, _) = VampireFinishingResolver.GetCaps(target, draft);
                                                                        var canAdd = remaining >= cost && cur + 1 <= hardCap;

                                                                        var value = $"+:{target}:{label}";
                                                                        var desc = $"+1 → {cur + 1}, -{cost}";
                                                                        if (!canAdd)
                                                                        {
                                                                            desc = cur >= hardCap
                                                                                ? $"уже на кэпе {hardCap}"
                                                                                : $"нужно {cost} свободных";
                                                                        }
                                                                        menu.AddOption(new SelectMenuOptionBuilder()
                                                                            .WithLabel($"+ {Truncate(label, 28)} ({cur}→{cur + 1})")
                                                                            .WithValue(value)
                                                                            .WithDescription(desc));
                                                                    }

                                                                    /// <summary>
                                                                    /// Добавить опции SelectMenu: для каждого имени в категории — одну опцию «+».
                                                                    /// </summary>
                                                                    private static void AddOptionsFor(
                                                                        SelectMenuBuilder menu,
                                                                        VampireCharacter draft,
                                                                        VampireFinishingResolver.FreebieTarget target,
                                                                        Dictionary<string, int>? dict,
                                                                        object? abilityStruct)
                                                                    {
                                                                        var targetName = VampireFinishingResolver.TargetName(target, draft);
                                                                        var cost = VampireFinishingResolver.CostOf(target);
                                                                        var remaining = VampireFinishingResolver.RemainingFreebies(draft);
                                                                        var cap = 5;

                                                                        if (abilityStruct != null)
                                                                        {
                                                                            foreach (var name in AbilityFieldNames())
                                                                            {
                                                                                var cur = VampireFinishingResolver.ReadFieldValue(draft, target, name);
                                                                                var canAdd = remaining >= cost && cur + 1 <= cap;
                                                                                AppendOneOption(menu, target, name, cur, cost, canAdd, targetName);
                                                                            }
                                                                            return;
                                                                        }

                                                                        if (dict != null && dict.Count > 0)
                                                                        {
                                                                            foreach (var (name, val) in dict)
                                                                            {
                                                                                var cur = val;
                                                                                var canAdd = remaining >= cost && cur + 1 <= cap;
                                                                                AppendOneOption(menu, target, name, cur, cost, canAdd, targetName);
                                                                            }
                                                                            return;
                                                                        }

                                                                        // Словарь пуст — отрисовать все имена для атрибутов и способностей,
                                                                        // и три стандартные добродетели для Virtue.
                                                                        if (target == VampireFinishingResolver.FreebieTarget.Attribute)
                                                                        {
                                                                            foreach (var name in AttributeFieldNames())
                                                                            {
                                                                                var cur = VampireFinishingResolver.ReadFieldValue(draft, target, name);
                                                                                var canAdd = remaining >= cost && cur + 1 <= cap;
                                                                                AppendOneOption(menu, target, name, cur, cost, canAdd, targetName);
                                                                            }
                                                                            return;
                                                                        }

                                                                        if (target == VampireFinishingResolver.FreebieTarget.Virtue)
                                                                        {
                                                                            foreach (var name in VampireParameterCatalog.Virtues)
                                                                            {
                                                                                var cur = VampireAdvantagesResolver.GetVirtueValue(draft, name);
                                                                                var canAdd = remaining >= cost && cur + 1 <= cap;
                                                                                AppendOneOption(menu, target, name, cur, cost, canAdd, targetName);
                                                                            }
                                                                        }
                                                                    }

                                                                    private static void AppendOneOption(
                                                                        SelectMenuBuilder menu,
                                                                        VampireFinishingResolver.FreebieTarget target,
                                                                        string field,
                                                                        int current,
                                                                        int cost,
                                                                        bool enabled,
                                                                        string targetName)
                                                                    {
                                                                        var value = $"+:{target}:{field}";
                                                                        var desc = $"сейчас {current}, -{cost}";
                                                                        if (!enabled)
                                                                        {
                                                                            desc = current >= 5 ? "уже на кэпе 5" : $"нужно {cost} свободных";
                                                                        }
                                                                        menu.AddOption(new SelectMenuOptionBuilder()
                                                                            .WithLabel($"+ {Truncate(targetName + ": " + field, 28)} ({current})")
                                                                            .WithValue(value)
                                                                            .WithDescription(desc));
                                                                    }

                                                                    private static IEnumerable<string> AttributeFieldNames() => new[]
                                                                    {
                                                                        "Сила", "Ловкость", "Выносливость",
                                                                        "Обаяние", "Манипуляция", "Привлекательность",
                                                                        "Восприятие", "Интеллект", "Смекалка",
                                                                    };

                                                                    private static IEnumerable<string> AbilityFieldNames() => new[]
                                                                    {
                                                                        "Атлетика", "Бдительность", "Драка", "Запугивание", "Красноречие",
                                                                        "Лидерство", "Уличное чутьё", "Хитрость", "Шестое чувство", "Эмпатия",
                                                                        "Вождение", "Воровство", "Выживание", "Исполнение", "Обращение с животными",
                                                                        "Ремесло", "Скрытность", "Стрельба", "Фехтование", "Этикет",
                                                                        "Гуманитарные науки", "Естественные науки", "Информатика", "Медицина", "Оккультизм",
                                                                        "Политика", "Расследование", "Финансы", "Электроника", "Юриспруденция",
                                                                    };

                                /// <summary>
                                /// SelectMenu Шага 4.1: пары опций (± для каждой дисциплины),
                                /// включая переименованные Каитифом ключи.
                                /// </summary>
                                private static SelectMenuBuilder BuildDisciplineSelect(VampireCharacter draft)
                                {
                                    var slots = VampireAdvantagesResolver.EffectiveDisciplineSlots(draft);
                                    var remaining = VampireAdvantagesResolver.RemainingDisciplinePool(draft);
                                    var menu = new SelectMenuBuilder()
                                        .WithCustomId(BuildCustomId(VampireWizardAction.DisciplineInc, draft.CharacterId))
                                        .WithPlaceholder($"Дисциплины (±): ост. {remaining}");

                                    foreach (var name in slots)
                                    {
                                        var cur = VampireAdvantagesResolver.GetDisciplineValue(draft, name);

                                        var incVal = $"+:{name}";
                                        var incDesc = $"текущее: {cur}";
                                        if (cur >= VampireAdvantagesCatalog.PerFieldCap) incDesc = $"уже на кэпе {VampireAdvantagesCatalog.PerFieldCap}";
                                        menu.AddOption(new SelectMenuOptionBuilder()
                                            .WithLabel($"+ {Truncate(name, 18)} ({cur})")
                                            .WithValue(incVal)
                                            .WithDescription(incDesc));

                                        var decVal = $"−:{name}";
                                        var decDesc = $"текущее: {cur}";
                                        if (cur <= 0) decDesc = "уже 0 (минимум)";
                                        menu.AddOption(new SelectMenuOptionBuilder()
                                            .WithLabel($"− {Truncate(name, 18)} ({cur})")
                                            .WithValue(decVal)
                                            .WithDescription(decDesc));
                                    }

                                    return menu;
                                }

                                /// <summary>
                                /// SelectMenu Шага 4.2: 5 опций на факт (+ранг / −ранг / Удалить / Переименовать / [Каитиф]).
                                /// </summary>
                                private static SelectMenuBuilder BuildBackgroundSelect(
                                    VampireCharacter draft,
                                    string name,
                                    int rank)
                                {
                                    var menu = new SelectMenuBuilder()
                                        .WithCustomId(BuildCustomIdWithArg(VampireWizardAction.BackgroundInc, draft.CharacterId, name))
                                        .WithPlaceholder($"«{Truncate(name, 22)}» (ранг {rank})");

                                    menu.AddOption(new SelectMenuOptionBuilder()
                                        .WithLabel($"+ ранг ({rank})")
                                        .WithValue("+:rank")
                                        .WithDescription("поднять ранг на 1"));

                                    if (rank > 1)
                                    {
                                        menu.AddOption(new SelectMenuOptionBuilder()
                                            .WithLabel($"− ранг ({rank})")
                                            .WithValue("−:rank")
                                            .WithDescription("опустить ранг на 1"));
                                    }

                                    menu.AddOption(new SelectMenuOptionBuilder()
                                        .WithLabel("Переименовать")
                                        .WithValue("rename")
                                        .WithDescription("ввести новое имя в ЛС"));

                                    menu.AddOption(new SelectMenuOptionBuilder()
                                        .WithLabel("Удалить")
                                        .WithValue("remove")
                                        .WithDescription("полностью убрать факт"));

                                    return menu;
                                }

                                /// <summary>
                                /// SelectMenu Шага 4.3: 6 опций (±3 добродетели).
                                /// </summary>
                                private static SelectMenuBuilder BuildVirtueSelect(VampireCharacter draft)
                                {
                                    var remaining = VampireAdvantagesResolver.RemainingVirtuePool(draft);
                                    var menu = new SelectMenuBuilder()
                                        .WithCustomId(BuildCustomId(VampireWizardAction.VirtueInc, draft.CharacterId))
                                        .WithPlaceholder($"Добродетели (±): ост. {remaining}");

                                    foreach (var name in VampireParameterCatalog.Virtues)
                                    {
                                        var cur = VampireAdvantagesResolver.GetVirtueValue(draft, name);

                                        var incVal = $"+:{name}";
                                        var incDesc = $"текущее: {cur}";
                                        if (cur >= VampireAdvantagesCatalog.PerFieldCap) incDesc = $"уже на кэпе {VampireAdvantagesCatalog.PerFieldCap}";
                                        menu.AddOption(new SelectMenuOptionBuilder()
                                            .WithLabel($"+ {Truncate(name, 18)} ({cur})")
                                            .WithValue(incVal)
                                            .WithDescription(incDesc));

                                        var decVal = $"−:{name}";
                                        var decDesc = $"текущее: {cur}";
                                        if (cur <= VampireAdvantagesCatalog.MinVirtue) decDesc = $"уже на базе {VampireAdvantagesCatalog.MinVirtue}";
                                        menu.AddOption(new SelectMenuOptionBuilder()
                                            .WithLabel($"− {Truncate(name, 18)} ({cur})")
                                            .WithValue(decVal)
                                            .WithDescription(decDesc));
                                    }

                                    return menu;
                                }

                                /// <summary>
                                /// SelectMenu для изменения одной способности в группе.
                                /// Опции: +Способность, −Способность для каждой из 10 в группе (20 опций).
                                /// </summary>
                                private static SelectMenuBuilder BuildAbilityGroupSelect(
                                    VampireCharacter draft,
                                    VampireAbilityGroup group)
                                {
                                    var groupLabel = group switch
                                    {
                                        VampireAbilityGroup.Talents => "Таланты",
                                        VampireAbilityGroup.Skills => "Навыки",
                                        VampireAbilityGroup.Knowledges => "Знания",
                                        _ => group.ToString()
                                    };

                                    var remaining = VampireAbilitiesResolver.RemainingInGroup(draft, group);
                                    var placeholder = $"{groupLabel} (±): ост. {remaining}";

                                    var menu = new SelectMenuBuilder()
                                        .WithCustomId(BuildCustomIdWithArg(VampireWizardAction.AbilityInc, draft.CharacterId, group.ToString()))
                                        .WithPlaceholder(placeholder);

                                    foreach (var name in VampireAbilitiesCatalog.NamesInGroup(group))
                                    {
                                        var cur = GetAbilityValue(draft, name);

                                        // Опция «+».
                                        var incVal = $"+:{name}";
                                        var incDesc = $"текущее: {cur}, макс шага 3 = 3";
                                        if (cur >= 3) incDesc = "уже 3 (макс шага 3)";
                                        menu.AddOption(new SelectMenuOptionBuilder()
                                            .WithLabel($"+ {Truncate(name, 18)} ({cur})")
                                            .WithValue(incVal)
                                            .WithDescription(incDesc));

                                        // Опция «−».
                                        var decVal = $"−:{name}";
                                        var decDesc = $"текущее: {cur}, база = 0";
                                        if (cur <= 0) decDesc = "уже на базе";
                                        menu.AddOption(new SelectMenuOptionBuilder()
                                            .WithLabel($"− {Truncate(name, 18)} ({cur})")
                                            .WithValue(decVal)
                                            .WithDescription(decDesc));
                                    }

                                    return menu;
                                }

                                /// <summary>
                                /// SelectMenu выбора специализации для конкретного параметра
                                /// (характеристики или способности). Опции: 3 подсказки из каталога
                                /// + «Своя…» (открывает текстовый ввод).
                                /// </summary>
                                /// <param name="paramName">Имя параметра (русское, как в каталоге).</param>
                                /// <param name="actionPrefix">Какой action использовать (SpecSet).</param>
                                public static SelectMenuBuilder BuildSpecChoiceSelect(
                                    VampireCharacter draft,
                                    string paramName)
                                {
                                    var menu = new SelectMenuBuilder()
                                        .WithCustomId(BuildCustomIdWithArg(VampireWizardAction.SpecChoice, draft.CharacterId, paramName))
                                        .WithPlaceholder($"Специализация «{paramName}»");

                                    var suggestions = VampireAbilitiesCatalog.GetSpecializationSuggestions(paramName);
                                    if (suggestions.Count == 0)
                                    {
                                        // Для параметров без справочника подсказок (например, характеристик) — сразу «Своя».
                                        menu.AddOption(new SelectMenuOptionBuilder()
                                            .WithLabel("Своя…")
                                            .WithValue("__custom__")
                                            .WithDescription("ввести произвольный текст в ЛС"));
                                    }
                                    else
                                    {
                                        foreach (var s in suggestions)
                                        {
                                            menu.AddOption(new SelectMenuOptionBuilder()
                                                .WithLabel(Truncate(s, 28))
                                                .WithValue(s)
                                                .WithDescription("выбрать готовый вариант"));
                                        }
                                        menu.AddOption(new SelectMenuOptionBuilder()
                                            .WithLabel("Своя…")
                                            .WithValue("__custom__")
                                            .WithDescription("ввести произвольный текст в ЛС"));
                                    }

                                    return menu;
                                }

                                private static bool HasAbilityPriority(VampireCharacter d)
                                {
                                    return !string.IsNullOrEmpty(d.AbilitiesPriority);
                                }

                                private static int GetAbilityValue(VampireCharacter draft, string name) => name switch
                                {
                                    "Атлетика"                 => draft.AbilitiesStruct.Атлетика,
                                    "Бдительность"             => draft.AbilitiesStruct.Бдительность,
                                    "Драка"                    => draft.AbilitiesStruct.Драка,
                                    "Запугивание"              => draft.AbilitiesStruct.Запугивание,
                                    "Красноречие"              => draft.AbilitiesStruct.Красноречие,
                                    "Лидерство"                => draft.AbilitiesStruct.Лидерство,
                                    "Уличное чутьё"            => draft.AbilitiesStruct.УличноеЧутьё,
                                    "Хитрость"                 => draft.AbilitiesStruct.Хитрость,
                                    "Шестое чувство"           => draft.AbilitiesStruct.ШестоеЧувство,
                                    "Эмпатия"                  => draft.AbilitiesStruct.Эмпатия,
                                    "Вождение"                 => draft.AbilitiesStruct.Вождение,
                                    "Воровство"                => draft.AbilitiesStruct.Воровство,
                                    "Выживание"                => draft.AbilitiesStruct.Выживание,
                                    "Исполнение"               => draft.AbilitiesStruct.Исполнение,
                                    "Обращение с животными"    => draft.AbilitiesStruct.ОбращениеСЖивотными,
                                    "Ремесло"                  => draft.AbilitiesStruct.Ремесло,
                                    "Скрытность"               => draft.AbilitiesStruct.Скрытность,
                                    "Стрельба"                 => draft.AbilitiesStruct.Стрельба,
                                    "Фехтование"               => draft.AbilitiesStruct.Фехтование,
                                    "Этикет"                   => draft.AbilitiesStruct.Этикет,
                                    "Гуманитарные науки"       => draft.AbilitiesStruct.ГуманитарныеНауки,
                                    "Естественные науки"       => draft.AbilitiesStruct.ЕстественныеНауки,
                                    "Информатика"              => draft.AbilitiesStruct.Информатика,
                                    "Медицина"                 => draft.AbilitiesStruct.Медицина,
                                    "Оккультизм"               => draft.AbilitiesStruct.Оккультизм,
                                    "Политика"                 => draft.AbilitiesStruct.Политика,
                                    "Расследование"            => draft.AbilitiesStruct.Расследование,
                                    "Финансы"                  => draft.AbilitiesStruct.Финансы,
                                    "Электроника"              => draft.AbilitiesStruct.Электроника,
                                    "Юриспруденция"            => draft.AbilitiesStruct.Юриспруденция,
                                    _ => 0,
                                };

                                private static string Truncate(string s, int max)
                                {
                                    if (string.IsNullOrEmpty(s)) return "";
                                    if (s.Length <= max) return s;
                                    return s.Substring(0, max - 1) + "…";
                                }

            private static int GetAttrValue(VampireCharacter draft, string name) => name switch
            {
                "Сила"              => draft.AttributesStruct.Strength,
                "Ловкость"          => draft.AttributesStruct.Dexterity,
                "Выносливость"      => draft.AttributesStruct.Stamina,
                "Обаяние"           => draft.AttributesStruct.Charisma,
                "Манипуляция"       => draft.AttributesStruct.Manipulation,
                "Привлекательность" => draft.AttributesStruct.Appearance,
                "Восприятие"        => draft.AttributesStruct.Perception,
                "Интеллект"         => draft.AttributesStruct.Intelligence,
                "Смекалка"          => draft.AttributesStruct.Wits,
                _ => 0,
            };

            private static int GetBaseValue(VampireCharacter draft, string name)
            {
                if (name == "Привлекательность"
                    && (draft.Clan == "Носферату" || draft.Clan == "Последователь Сета"))
                {
                    return 0;
                }
                return 1;
            }

            private static string TruncateLabel(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        return s.Substring(0, max - 1) + "…";
    }

    public static string BuildCustomId(VampireWizardAction action, Guid characterId)
        => $"{Prefix}:{ActionToString(action)}:{characterId:N}";

        /// <summary>
        /// Собрать customId с дополнительным аргументом (например, имя атрибута).
        /// Формат: <c>vtm_wiz:{action_str}:{characterId}:{arg}</c>.
        /// </summary>
        public static string BuildCustomIdWithArg(VampireWizardAction action, Guid characterId, string arg)
            => $"{Prefix}:{ActionToString(action)}:{characterId:N}:{(arg ?? "")}";

    public static bool TryParse(string customId, out VampireWizardAction action, out Guid characterId)
    {
        action = default;
        characterId = Guid.Empty;
        if (string.IsNullOrEmpty(customId)) return false;
        if (!customId.StartsWith(Prefix + ":")) return false;
        var parts = customId.Split(':');
        if (parts.Length != 3) return false;
        if (!TryParseAction(parts[1], out action)) return false;
        if (!Guid.TryParseExact(parts[2], "N", out characterId)) return false;
        return characterId != Guid.Empty;
    }

        /// <summary>
        /// Распарсить customId с дополнительным аргументом (например, имя атрибута).
        /// Формат: <c>vtm_wiz:{action}:{characterId:N}:{arg}</c>.
        /// </summary>
        public static bool TryParseWithArg(
            string customId,
            out VampireWizardAction action,
            out Guid characterId,
            out string arg)
        {
            action = default;
            characterId = Guid.Empty;
            arg = "";
            if (string.IsNullOrEmpty(customId)) return false;
            if (!customId.StartsWith(Prefix + ":")) return false;
            var parts = customId.Split(':', 4);
            if (parts.Length != 4) return false;
            if (!TryParseAction(parts[1], out action)) return false;
            if (!Guid.TryParseExact(parts[2], "N", out characterId)) return false;
            if (characterId == Guid.Empty) return false;
            arg = parts[3] ?? "";
            return true;
        }

    public static bool IsOurButton(string customId)
        => !string.IsNullOrEmpty(customId) && customId.StartsWith(Prefix + ":");

    private static string ActionToString(VampireWizardAction action) => action switch
    {
        VampireWizardAction.SetConcept  => "set_concept",
        VampireWizardAction.SetClan     => "set_clan",
        VampireWizardAction.SetNature   => "set_nature",
        VampireWizardAction.SetDemeanor => "set_demeanor",
        VampireWizardAction.SetBio      => "set_bio",
        VampireWizardAction.SkipBio     => "skip_bio",
        VampireWizardAction.ClearBio    => "clear_bio",
        VampireWizardAction.ClearConcept => "clear_concept",
        VampireWizardAction.ClearClan   => "clear_clan",
        VampireWizardAction.ClearNature => "clear_nature",
        VampireWizardAction.ClearDemeanor => "clear_demeanor",
        VampireWizardAction.SetSire      => "set_sire",
        VampireWizardAction.ClearSire    => "clear_sire",
        VampireWizardAction.SetGeneration => "set_generation",
        VampireWizardAction.ResetGeneration => "reset_generation",
        VampireWizardAction.Next        => "next",
        VampireWizardAction.Cancel      => "cancel",
                VampireWizardAction.AttrInc     => "attr_inc",
                VampireWizardAction.AttrDec     => "attr_dec",
                                VampireWizardAction.AttrPriority => "attr_priority",
                VampireWizardAction.ResetAttrProgress => "reset_attr_progress",
                VampireWizardAction.ResetAttrAll      => "reset_attr_all",
                VampireWizardAction.BackToConcept     => "back_to_concept",
                        VampireWizardAction.AbilityPriority     => "ability_priority",
                        VampireWizardAction.AbilityInc          => "ability_inc",
                        VampireWizardAction.AbilityDec          => "ability_dec",
                        VampireWizardAction.SpecChoice          => "spec_choice",
                        VampireWizardAction.SpecSet             => "spec_set",
                        VampireWizardAction.ResetAbilityProgress => "reset_ability_progress",
                        VampireWizardAction.ResetAbilityAll      => "reset_ability_all",
                        VampireWizardAction.BackToAttributes     => "back_to_attributes",
                        VampireWizardAction.DisciplineInc        => "discipline_inc",
                        VampireWizardAction.DisciplineDec        => "discipline_dec",
                        VampireWizardAction.DisciplineRename     => "discipline_rename",
                        VampireWizardAction.BackgroundAdd        => "background_add",
                        VampireWizardAction.BackgroundRemove     => "background_remove",
                        VampireWizardAction.BackgroundRename     => "background_rename",
                        VampireWizardAction.BackgroundInc        => "background_inc",
                        VampireWizardAction.BackgroundDec        => "background_dec",
                        VampireWizardAction.VirtueInc            => "virtue_inc",
                        VampireWizardAction.VirtueDec            => "virtue_dec",
                        VampireWizardAction.ResetAdvProgress     => "reset_adv_progress",
                        VampireWizardAction.ResetAdvAll          => "reset_adv_all",
                        VampireWizardAction.BackToAbilities      => "back_to_abilities",
                        VampireWizardAction.NextAdvToBackgrounds => "next_adv_to_backgrounds",
                        VampireWizardAction.NextAdvToVirtues     => "next_adv_to_virtues",
                        VampireWizardAction.NextAdvToFinishing   => "next_adv_to_finishing",
                                                VampireWizardAction.FinishingInc        => "finishing_inc",
                                                VampireWizardAction.FinishingReset      => "finishing_reset",
                                                VampireWizardAction.FinishingDone       => "finishing_done",
                                                VampireWizardAction.BackToAdvantages    => "back_to_advantages",
                                                _ => throw new InvalidEnumArgumentException(nameof(action), (int)action, typeof(VampireWizardAction)),
    };

    private static bool TryParseAction(string s, out VampireWizardAction action)
    {
        switch (s)
        {
            case "set_concept":    action = VampireWizardAction.SetConcept;   return true;
            case "set_clan":       action = VampireWizardAction.SetClan;      return true;
            case "set_nature":     action = VampireWizardAction.SetNature;    return true;
            case "set_demeanor":   action = VampireWizardAction.SetDemeanor;  return true;
            case "set_bio":        action = VampireWizardAction.SetBio;       return true;
            case "skip_bio":       action = VampireWizardAction.SkipBio;      return true;
            case "clear_bio":      action = VampireWizardAction.ClearBio;     return true;
            case "clear_concept":  action = VampireWizardAction.ClearConcept; return true;
            case "clear_clan":     action = VampireWizardAction.ClearClan;    return true;
            case "clear_nature":   action = VampireWizardAction.ClearNature;  return true;
            case "clear_demeanor": action = VampireWizardAction.ClearDemeanor; return true;
            case "set_sire":       action = VampireWizardAction.SetSire;      return true;
            case "clear_sire":     action = VampireWizardAction.ClearSire;    return true;
            case "set_generation": action = VampireWizardAction.SetGeneration; return true;
            case "reset_generation": action = VampireWizardAction.ResetGeneration; return true;
            case "next":           action = VampireWizardAction.Next;         return true;
            case "cancel":         action = VampireWizardAction.Cancel;       return true;
                        case "attr_inc":       action = VampireWizardAction.AttrInc;       return true;
                        case "attr_dec":       action = VampireWizardAction.AttrDec;       return true;
                                                case "attr_priority":  action = VampireWizardAction.AttrPriority;  return true;
                        case "reset_attr_progress": action = VampireWizardAction.ResetAttrProgress; return true;
                        case "reset_attr_all": action = VampireWizardAction.ResetAttrAll;  return true;
                        case "back_to_concept": action = VampireWizardAction.BackToConcept; return true;
                                    case "ability_priority":    action = VampireWizardAction.AbilityPriority;    return true;
                                    case "ability_inc":         action = VampireWizardAction.AbilityInc;         return true;
                                    case "ability_dec":         action = VampireWizardAction.AbilityDec;         return true;
                                    case "spec_choice":         action = VampireWizardAction.SpecChoice;         return true;
                                    case "spec_set":            action = VampireWizardAction.SpecSet;            return true;
                                    case "reset_ability_progress": action = VampireWizardAction.ResetAbilityProgress; return true;
                                    case "reset_ability_all":   action = VampireWizardAction.ResetAbilityAll;    return true;
                                    case "back_to_attributes":  action = VampireWizardAction.BackToAttributes;   return true;
                                    case "discipline_inc":      action = VampireWizardAction.DisciplineInc;      return true;
                                    case "discipline_dec":      action = VampireWizardAction.DisciplineDec;      return true;
                                    case "discipline_rename":   action = VampireWizardAction.DisciplineRename;   return true;
                                    case "background_add":      action = VampireWizardAction.BackgroundAdd;      return true;
                                    case "background_remove":   action = VampireWizardAction.BackgroundRemove;   return true;
                                    case "background_rename":   action = VampireWizardAction.BackgroundRename;   return true;
                                    case "background_inc":      action = VampireWizardAction.BackgroundInc;      return true;
                                    case "background_dec":      action = VampireWizardAction.BackgroundDec;      return true;
                                    case "virtue_inc":          action = VampireWizardAction.VirtueInc;          return true;
                                    case "virtue_dec":          action = VampireWizardAction.VirtueDec;          return true;
                                    case "reset_adv_progress":  action = VampireWizardAction.ResetAdvProgress;  return true;
                                    case "reset_adv_all":       action = VampireWizardAction.ResetAdvAll;       return true;
                                    case "back_to_abilities":   action = VampireWizardAction.BackToAbilities;   return true;
                                    case "next_adv_to_backgrounds": action = VampireWizardAction.NextAdvToBackgrounds; return true;
                                    case "next_adv_to_virtues":     action = VampireWizardAction.NextAdvToVirtues;     return true;
                                    case "next_adv_to_finishing":   action = VampireWizardAction.NextAdvToFinishing;   return true;
                                    case "finishing_inc":          action = VampireWizardAction.FinishingInc;          return true;
                                    case "finishing_reset":        action = VampireWizardAction.FinishingReset;        return true;
                                    case "finishing_done":         action = VampireWizardAction.FinishingDone;         return true;
                                    case "back_to_advantages":     action = VampireWizardAction.BackToAdvantages;     return true;
                                                default:               action = default;                         return false;
        }
    }
}
