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
    /// <summary>Первый шаг отмены: показать сводку draft и попросить подтверждение.</summary>
    Cancel,
    /// <summary>Подтвердить отмену визарда после сводки (второй шаг).</summary>
    ConfirmCancel,
    /// <summary>Передумал отменять — вернуться к текущему шагу визарда.</summary>
    ResumeWizard,

        // ── Шаг 2 «Характеристики 7/5/3» ─────────────────────────────────────

        /// <summary>Увеличить атрибут на 1 (Шаг 2). Action-arg = имя атрибута.</summary>
        AttrInc,
        /// <summary>Уменьшить атрибут на 1 (Шаг 2).</summary>
        AttrDec,
                /// <summary>SelectMenu выбора приоритета 7/5/3 (Шаг 2).</summary>
                AttrPriority,
                /// <summary>SelectMenu выбора атрибута в группе (Шаг 2, пагинация). Action-arg = имя группы.</summary>
                AttrSelect,
                /// <summary>Переключить страницу группы атрибутов вперёд (Шаг 2). Action-arg = текущая группа.</summary>
                AttrPageNext,
                /// <summary>Переключить страницу группы атрибутов назад (Шаг 2). Action-arg = текущая группа.</summary>
                AttrPagePrev,
                /// <summary>Сбросить прогресс Шага 2 (только атрибуты; приоритет сохраняется).</summary>
                ResetAttrProgress,
                /// <summary>Сбросить всё на Шаге 2 (приоритет и атрибуты).</summary>
                ResetAttrAll,
                /// <summary>Вернуться на Шаг 1 (Концепция).</summary>
                BackToConcept,

                // ── Шаг 3 «Способности 13/9/5» ─────────────────────────────────────

                /// <summary>SelectMenu выбора приоритета 13/9/5 (Шаг 3).</summary>
                AbilityPriority,
                /// <summary>SelectMenu выбора активной группы способностей (Шаг 3). Action-arg = имя группы.</summary>
                AbilityGroupSelect,
                /// <summary>SelectMenu выбора способности в активной группе (Шаг 3). Action-arg = имя группы.</summary>
                AbilitySelect,
                /// <summary>Увеличить выбранную способность на 1 (Шаг 3). Без arg — берётся из session.AbilitySelected.</summary>
                AbilityInc,
                /// <summary>Уменьшить выбранную способность на 1 (Шаг 3).</summary>
                AbilityDec,
                /// <summary>Задать специализацию (SelectMenu выбора подсказки или «Своя»). Action-arg = имя параметра.</summary>
                SpecChoice,
                /// <summary>Установить выбранную специализацию (SelectMenu). Action-arg = имя параметра.</summary>
                SpecSet,
                /// <summary>Сбросить прогресс Шага 3 (только способности активной группы; приоритет сохраняется).</summary>
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
                /// <summary>Сбросить только дисциплины (Шаг 4.1).</summary>
                ResetAdvDisciplines,
                /// <summary>Сбросить только факты биографии (Шаг 4.2).</summary>
                ResetAdvBackgrounds,
                /// <summary>Сбросить только добродетели (Шаг 4.3).</summary>
                ResetAdvVirtues,
                /// <summary>Сбросить прогресс всего Шага 4 (дисциплины/факты/добродетели).</summary>
                ResetAdvProgress,
                /// <summary>Сбросить всё на Шаге 4 (то же, что и <see cref="ResetAdvProgress"/>).</summary>
                ResetAdvAll,
                /// <summary>Вернуться на Шаг 3 (Способности).</summary>
                BackToAbilities,
                /// <summary>Вернуться на Шаг 4.1 (Дисциплины) — с под-экранов 4.2/4.3.</summary>
                BackToDisciplines,
                /// <summary>Вернуться на Шаг 4.2 (Факты биографии) — с под-экрана 4.3.</summary>
                BackToBackgrounds,
                /// <summary>Перейти к Шагу 4.2 (Факты биографии).</summary>
                NextAdvToBackgrounds,
                /// <summary>Перейти к Шагу 4.3 (Добродетели).</summary>
                NextAdvToVirtues,
                /// <summary>Перейти к Шагу 5 (Последние штрихи).</summary>
                NextAdvToFinishing,

                                // ── Шаг 5 «Последние штрихи» ────────────────────────────────

                                /// <summary>SelectMenu свободных пунктов: target+field+sign. Action-arg = "{sign}:{target}:{field}".</summary>
                                FinishingInc,
                                /// <summary>Шаг 5 каскад: выбор категории свободного пункта (Step 1).</summary>
                                FinishingPickTarget,
                                /// <summary>Шаг 5 каскад: выбор подгруппы (Step 2, только Attribute/Ability). Action-arg = target.</summary>
                                FinishingPickSubgroup,
                                /// <summary>Шаг 5 каскад: применить +/− к выбранному полю (Step 3). Action-arg = "{sign}:{target}:{subgroup}:{field}".</summary>
                                FinishingApplyField,
                                /// <summary>Шаг 5 каскад: вернуться с подгруппы (Step 2) на категории (Step 1).</summary>
                                FinishingBackFromSubgroup,
                                /// <summary>Шаг 5 каскад: вернуться с поля (Step 3) на подгруппу (Step 2).</summary>
                                FinishingBackFromField,
                                /// <summary>Кнопка «Сбросить всё» на Шаге 5 (откатить все траты).</summary>
                                FinishingReset,
                                /// <summary>Завершить Шаг 5 и перейти к Шагу 6 (или показать лист).</summary>
                                FinishingDone,
                                /// <summary>Подтвердить завершение Шага 5 через диалог (когда пул ещё не пуст).
                                /// После этого специализации разрешены, а траты freebie заморожены.</summary>
                                FinishingFinalize,
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
            /// <para>Структура UI (5 рядов максимум по лимиту Discord):</para>
            /// <list type="number">
            /// <item>Ряд 1: SelectMenu «Приоритет групп 7/5/3».</item>
            /// <item>Ряд 2: SelectMenu «Атрибут для изменения» (3 опции — текущая группа).</item>
            /// <item>Ряд 3: кнопки «− Атрибут / + Атрибут» (по выбранному атрибуту).</item>
            /// <item>Ряд 4: навигация по группам: «◀ Группа / Сброс / Сброс всё / Группа ▶».</item>
            /// <item>Ряд 5: «Шаг 1» и (если завершено) «Шаг 3 (способности)».</item>
            /// </list>
            /// <para>Идея: одна группа = одна «страница», переключаемая
            /// кнопками «◀ Группа» / «Группа ▶» (цикл 0=Физ → 1=Соц → 2=Мент → 0=Физ).
            /// На странице 3 опции атрибута; «+» и «−» действуют на выбранный
            /// атрибут. Пока ни один атрибут не выбран, ряд «±» — плейсхолдер
            /// с подсказкой «выберите атрибут выше».</para>
            /// </summary>
            public static MessageComponent BuildForAttributesStep(VampireCharacter draft)
            {
                if (draft == null) throw new ArgumentNullException(nameof(draft));
                if (draft.CharacterId == Guid.Empty)
                    throw new ArgumentException("CharacterId обязателен", nameof(draft));

                return BuildForAttributesStep(draft, 0, null);
            }

            /// <summary>
            /// Шаг «Подтверждение отмены»: две кнопки — «Продолжить визард» (нейтральная)
            /// и «Подтвердить отмену» (опасная). Показывается вместо обычного UI шага,
            /// когда игрок нажал «Отмена». draft не должен быть пустым — characterId
            /// нужен для customId кнопок.
            /// </summary>
            public static MessageComponent BuildForCancelConfirm(VampireCharacter draft)
            {
                if (draft == null) throw new ArgumentNullException(nameof(draft));
                if (draft.CharacterId == Guid.Empty)
                    throw new ArgumentException("CharacterId обязателен", nameof(draft));
                var cb = new ComponentBuilder();
                cb.WithButton("✅ Продолжить визард",
                    BuildCustomId(VampireWizardAction.ResumeWizard, draft.CharacterId),
                    ButtonStyle.Primary);
                cb.WithButton("❌ Подтвердить отмену",
                    BuildCustomId(VampireWizardAction.ConfirmCancel, draft.CharacterId),
                    ButtonStyle.Danger);
                return cb.Build();
            }

            /// <summary>
            /// Собрать UI Шага 2 на конкретной странице (группе) с уже выбранным
            /// атрибутом (опционально).
            /// </summary>
            /// <param name="draft">Черновик персонажа.</param>
            /// <param name="pageIndex">0=Физ, 1=Соц, 2=Мент.</param>
            /// <param name="selectedAttribute">Имя атрибута, который сейчас «выбран»
            /// (например, из сессии). Если null — ни одна кнопка ± не активна.</param>
            public static MessageComponent BuildForAttributesStep(
                VampireCharacter draft,
                int pageIndex,
                string? selectedAttribute)
            {
                if (draft == null) throw new ArgumentNullException(nameof(draft));
                if (draft.CharacterId == Guid.Empty)
                    throw new ArgumentException("CharacterId обязателен", nameof(draft));

                // Нормализуем pageIndex.
                if (pageIndex < 0) pageIndex = 0;
                if (pageIndex > 2) pageIndex = 2;
                var group = (VampireAttributeGroup)pageIndex;
                var groupNames = VampireAttributeCatalog.NamesInGroup(group);

                var cb = new ComponentBuilder();

                // Ряд 1: SelectMenu приоритета.
                var priorityMenu = new SelectMenuBuilder()
                    .WithCustomId(BuildCustomId(VampireWizardAction.AttrPriority, draft.CharacterId))
                    .WithPlaceholder(HasPriority(draft)
                        ? $"Приоритет: {draft.AttributesPriority} (по группам)"
                        : "Выберите приоритет групп (7/5/3)…");
                foreach (var p in VampireAttributePriorityExtensions.All)
                {
                    priorityMenu.AddOption(p.HumanName(), p.ToString());
                }
                cb.WithSelectMenu(priorityMenu);

                // Ряд 2: SelectMenu «Атрибут для изменения».
                var menu = new SelectMenuBuilder()
                    .WithCustomId(BuildCustomIdWithArg(VampireWizardAction.AttrSelect, draft.CharacterId, group.ToString()))
                    .WithPlaceholder($"Атрибут для изменения ({GroupLabel(group)})…");
                foreach (var name in groupNames)
                {
                    var cur = GetAttrValue(draft, name);
                    var baseVal = GetBaseValue(draft, name);
                    var max = GroupMaxForAttribute(draft, group, name);
                    var desc = $"база {baseVal}, сейчас {cur}, макс {max}";
                    var label = $"{(name == selectedAttribute ? "★ " : "")}{name}: {cur}";
                    menu.AddOption(new SelectMenuOptionBuilder()
                        .WithLabel(label)
                        .WithValue(name)
                        .WithDescription(desc));
                }
                cb.WithSelectMenu(menu);

                // Ряд 3: кнопки −/+. Активны только если selectedAttribute != null и
                // он в текущей группе. Если приоритет не выбран — обе кнопки
                // заблокированы, и показываем подсказку «сначала выберите
                // приоритет» (защита от случайных кликов и интуитивный сигнал,
                // что сначала нужно выбрать приоритет в SelectMenu выше).
                var selInGroup = !string.IsNullOrEmpty(selectedAttribute) &&
                                 groupNames.Contains(selectedAttribute);
                if (selInGroup && HasPriority(draft))
                {
                    var cur = GetAttrValue(draft, selectedAttribute!);
                    var baseVal = GetBaseValue(draft, selectedAttribute!);
                    var max = GroupMaxForAttribute(draft, group, selectedAttribute!);
                    var clanStriked = baseVal == 0 && cur == 0;

                    var decDisabled = cur <= baseVal;
                    var incDisabled = clanStriked || cur >= max;

                    cb.WithButton($"− {selectedAttribute} ({cur})",
                        BuildCustomIdWithArg(VampireWizardAction.AttrDec, draft.CharacterId, selectedAttribute!),
                        ButtonStyle.Secondary, disabled: decDisabled);
                    cb.WithButton($"+ {selectedAttribute} ({cur})",
                        BuildCustomIdWithArg(VampireWizardAction.AttrInc, draft.CharacterId, selectedAttribute!),
                        incDisabled ? ButtonStyle.Secondary : ButtonStyle.Primary, disabled: incDisabled);
                }
                else if (selInGroup && !HasPriority(draft))
                {
                    // Атрибут выбран, но приоритет групп ещё не задан — кнопки
                    // −/+ выключены, показываем noop с подсказкой.
                    cb.WithButton("− …", "vtm_wiz:noop:" + draft.CharacterId.ToString("N"),
                        ButtonStyle.Secondary, disabled: true);
                    cb.WithButton("Сначала выберите приоритет (выпадающее меню выше)",
                        "vtm_wiz:noop:" + draft.CharacterId.ToString("N"),
                        ButtonStyle.Secondary, disabled: true);
                }
                else
                {
                    cb.WithButton("Выберите атрибут в меню выше", "vtm_wiz:noop:" + draft.CharacterId.ToString("N"),
                        ButtonStyle.Secondary, disabled: true);
                }

                // Ряд 4: навигация по группам.
                cb.WithButton("◀ Группа", BuildCustomIdWithArg(VampireWizardAction.AttrPagePrev, draft.CharacterId, group.ToString()),
                    ButtonStyle.Secondary);
                cb.WithButton($"Группа: {GroupLabel(group)}", "vtm_wiz:noop:" + draft.CharacterId.ToString("N"),
                    ButtonStyle.Secondary, disabled: true); // плейсхолдер
                cb.WithButton("Группа ▶", BuildCustomIdWithArg(VampireWizardAction.AttrPageNext, draft.CharacterId, group.ToString()),
                    ButtonStyle.Secondary);
                cb.WithButton("Сбросить всё", BuildCustomId(VampireWizardAction.ResetAttrAll, draft.CharacterId), ButtonStyle.Danger);

                // Ряд 5: общая навигация визарда.
                cb.WithButton("⬅ Шаг 1", BuildCustomId(VampireWizardAction.BackToConcept, draft.CharacterId), ButtonStyle.Secondary);
                if (VampireAttributesResolver.IsAttributesComplete(draft))
                {
                    cb.WithButton("Далее → Шаг 3 (способности)", BuildCustomId(VampireWizardAction.Next, draft.CharacterId), ButtonStyle.Success);
                }

                return cb.Build();
            }

            /// <summary>Краткий лейбл группы для UI.</summary>
            private static string GroupLabel(VampireAttributeGroup g) => g switch
            {
                VampireAttributeGroup.Physical => "Физ",
                VampireAttributeGroup.Social   => "Соц",
                VampireAttributeGroup.Mental   => "Мент",
                _ => g.ToString()
            };

                            /// <summary>
                            /// Максимум атрибута в текущей группе по выбранному приоритету.
                            /// Если приоритет не выбран — 1 (минимум; VtM V20, стр. 84).
                            /// </summary>
                            private static int GroupMaxForAttribute(VampireCharacter draft, VampireAttributeGroup group, string attrName)
                            {
                                if (string.IsNullOrEmpty(draft.AttributesPriority))
                                    return 1;
                                if (!System.Enum.TryParse<VampireAttributePriority>(draft.AttributesPriority, out var p))
                                    return 1;
                                return p.PointsFor(group);
                            }

                            private static bool HasPriority(VampireCharacter d)
                            {
                                return !string.IsNullOrEmpty(d.AttributesPriority);
                            }

                                // ── Шаг 3 «Способности 13/9/5» ─────────────────────────────────────

                                /// <summary>
                                /// UI Шага 3 (вариант E — единообразно с Шагом 2): 5 рядов.
                                ///   1) SelectMenu выбора приоритета 13/9/5.
                                ///   2) SelectMenu выбора активной группы способностей (3 опции: Таланты/Навыки/Знания).
                                ///   3) SelectMenu выбора способности в активной группе (10 опций).
                                ///   4) Кнопки − / + (активны только если приоритет выбран и способность выбрана).
                                ///   5) «Сбросить группу» (активную) / «Сбросить всё» / «⬅ Назад» / (если завершено) «Далее».
                                /// </summary>
                                public static MessageComponent BuildForAbilitiesStep(
                                    VampireCharacter draft,
                                    int groupIndex = 0,
                                    string? selectedAbility = null)
                                {
                                    if (draft == null) throw new ArgumentNullException(nameof(draft));
                                    if (draft.CharacterId == Guid.Empty)
                                        throw new ArgumentException("CharacterId обязателен", nameof(draft));
                                    if (groupIndex < 0 || groupIndex > 2)
                                        groupIndex = 0;

                                    var activeGroup = (VampireAbilityGroup)groupIndex;
                                    var groupNames = VampireAbilitiesCatalog.NamesInGroup(activeGroup);

                                    var cb = new ComponentBuilder();

                                    // Ряд 1: SelectMenu с приоритетами 13/9/5.
                                    var priorityMenu = new SelectMenuBuilder()
                                        .WithCustomId(BuildCustomId(VampireWizardAction.AbilityPriority, draft.CharacterId))
                                        .WithPlaceholder(HasAbilityPriority(draft)
                                            ? $"Приоритет: {draft.AbilitiesPriority} (по группам)"
                                            : "Выберите приоритет групп (13/9/5)…");
                                    foreach (var p in VampireAbilityPriorityExtensions.All)
                                    {
                                        priorityMenu.AddOption(p.HumanName(), p.ToString());
                                    }
                                    cb.WithSelectMenu(priorityMenu);

                                    // Ряд 2: SelectMenu «Группа способностей».
                                    var groupMenu = new SelectMenuBuilder()
                                        .WithCustomId(BuildCustomIdWithArg(VampireWizardAction.AbilityGroupSelect, draft.CharacterId, activeGroup.ToString()))
                                        .WithPlaceholder($"Группа: {AbilGroupLabel(activeGroup)} (выберите)…");
                                    foreach (var g in new[] { VampireAbilityGroup.Talents, VampireAbilityGroup.Skills, VampireAbilityGroup.Knowledges })
                                    {
                                        groupMenu.AddOption(new SelectMenuOptionBuilder()
                                            .WithLabel(AbilGroupLabel(g))
                                            .WithValue(g.ToString())
                                            .WithDescription(g == activeGroup ? "активная группа" : "переключить на эту группу"));
                                    }
                                    cb.WithSelectMenu(groupMenu);

                                    // Ряд 3: SelectMenu «Способность для изменения» (10 опций в активной группе).
                                    var abilityMenu = new SelectMenuBuilder()
                                        .WithCustomId(BuildCustomIdWithArg(VampireWizardAction.AbilitySelect, draft.CharacterId, activeGroup.ToString()))
                                        .WithPlaceholder(string.IsNullOrEmpty(selectedAbility)
                                            ? $"Способность ({AbilGroupLabel(activeGroup)}) для изменения…"
                                            : $"Способность: {selectedAbility} — выберите другую…");
                                    foreach (var name in groupNames)
                                    {
                                        var cur = GetAbilityValue(draft, name);
                                        var label = $"{(name == selectedAbility ? "★ " : "")}{name}: {cur}";
                                        var max = 3;
                                        var desc = $"сейчас {cur}, макс шага 3 = {max}";
                                        abilityMenu.AddOption(new SelectMenuOptionBuilder()
                                            .WithLabel(label)
                                            .WithValue(name)
                                            .WithDescription(desc));
                                    }
                                    cb.WithSelectMenu(abilityMenu);

                                    // Ряд 4: кнопки −/+. Активны только если способность выбрана,
                                    // она в активной группе, и приоритет задан.
                                    var selInGroup = !string.IsNullOrEmpty(selectedAbility) &&
                                                     groupNames.Contains(selectedAbility);
                                    if (selInGroup && HasAbilityPriority(draft))
                                    {
                                        var cur = GetAbilityValue(draft, selectedAbility!);
                                        var max = 3;
                                        var decDisabled = cur <= 0;
                                        var incDisabled = cur >= max;

                                        cb.WithButton($"− {selectedAbility} ({cur})",
                                            BuildCustomId(VampireWizardAction.AbilityDec, draft.CharacterId),
                                            ButtonStyle.Secondary, disabled: decDisabled);
                                        cb.WithButton($"+ {selectedAbility} ({cur})",
                                            BuildCustomId(VampireWizardAction.AbilityInc, draft.CharacterId),
                                            incDisabled ? ButtonStyle.Secondary : ButtonStyle.Primary, disabled: incDisabled);
                                    }
                                    else if (selInGroup && !HasAbilityPriority(draft))
                                    {
                                        cb.WithButton("− …", "vtm_wiz:noop:" + draft.CharacterId.ToString("N"),
                                            ButtonStyle.Secondary, disabled: true);
                                        cb.WithButton("Сначала выберите приоритет (выпадающее меню выше)",
                                            "vtm_wiz:noop:" + draft.CharacterId.ToString("N"),
                                            ButtonStyle.Secondary, disabled: true);
                                    }
                                    else
                                    {
                                        cb.WithButton("Выберите способность в меню выше",
                                            "vtm_wiz:noop:" + draft.CharacterId.ToString("N"),
                                            ButtonStyle.Secondary, disabled: true);
                                    }

                                    // Ряд 5: навигация.
                                    cb.WithButton("Сбросить группу", BuildCustomId(VampireWizardAction.ResetAbilityProgress, draft.CharacterId), ButtonStyle.Secondary)
                                      .WithButton("Сбросить всё", BuildCustomId(VampireWizardAction.ResetAbilityAll, draft.CharacterId), ButtonStyle.Danger)
                                      .WithButton("⬅ Назад (Шаг 2)", BuildCustomId(VampireWizardAction.BackToAttributes, draft.CharacterId), ButtonStyle.Secondary);

                                    if (VampireAbilitiesResolver.IsAbilitiesComplete(draft))
                                    {
                                        cb.WithButton("Далее → Шаг 4 (преимущества)", BuildCustomId(VampireWizardAction.Next, draft.CharacterId), ButtonStyle.Success);
                                    }

                                    return cb.Build();
                                }

                                /// <summary>Краткий лейбл группы для UI Шага 3.</summary>
                                private static string AbilGroupLabel(VampireAbilityGroup g) => g switch
                                {
                                    VampireAbilityGroup.Talents => "Таланты",
                                    VampireAbilityGroup.Skills => "Навыки",
                                    VampireAbilityGroup.Knowledges => "Знания",
                                    _ => g.ToString()
                                };

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
                                      .WithButton("Сбросить экран", BuildCustomId(VampireWizardAction.ResetAdvDisciplines, draft.CharacterId), ButtonStyle.Secondary)
                                      .WithButton("Сбросить всё", BuildCustomId(VampireWizardAction.ResetAdvAll, draft.CharacterId), ButtonStyle.Danger);

                                    if (VampireAdvantagesResolver.IsDisciplinesComplete(draft))
                                    {
                                        cb.WithButton("Далее → 4.2 (факты)", BuildCustomId(VampireWizardAction.NextAdvToBackgrounds, draft.CharacterId), ButtonStyle.Success);
                                    }

                                    return cb.Build();
                                }

                                /// <summary>
                                /// UI Шага 4.2 «Факты биографии»: кнопка «Добавить факт» + один SelectMenu
                                /// со всеми фактами (по 4 опции на факт: + ранг, − ранг, Переименовать, Удалить).
                                /// </summary>
                                /// <remarks>
                                /// Раньше каждому факту соответствовал отдельный SelectMenu в отдельной ActionRow,
                                /// что ограничивало UI 2 фактами из-за лимита Discord (5 ActionRow на сообщение).
                                /// Сейчас все факты в одном SelectMenu: 6 × 4 = 24 опции, влезает в лимит 25.
                                /// </remarks>
                                public static MessageComponent BuildForBackgroundsStep(VampireCharacter draft)
                                {
                                    if (draft == null) throw new ArgumentNullException(nameof(draft));
                                    if (draft.CharacterId == Guid.Empty)
                                        throw new ArgumentException("CharacterId обязателен", nameof(draft));

                                    var cb = new ComponentBuilder();

                                    // Ряд 1: «Добавить факт» (открывает текстовый ввод).
                                    var addFactDisabled = draft.Backgrounds != null
                                        && draft.Backgrounds.Count >= VampireAdvantagesCatalog.MaxBackgroundsPerCharacter;
                                    cb.WithButton(
                                        addFactDisabled
                                            ? $"Добавить факт (лимит {VampireAdvantagesCatalog.MaxBackgroundsPerCharacter})"
                                            : "Добавить факт",
                                        BuildCustomId(VampireWizardAction.BackgroundAdd, draft.CharacterId),
                                        ButtonStyle.Secondary,
                                        disabled: addFactDisabled);

                                    // Ряд 2 (опционально): один SelectMenu со всеми фактами.
                                    if (draft.Backgrounds != null && draft.Backgrounds.Count > 0)
                                    {
                                        var allMenu = BuildBackgroundSelect(draft);
                                        if (allMenu != null) cb.WithSelectMenu(allMenu);
                                    }

                                    cb.WithButton("⬅ Назад (4.1)", BuildCustomId(VampireWizardAction.BackToDisciplines, draft.CharacterId), ButtonStyle.Secondary)
                                      .WithButton("Сбросить экран", BuildCustomId(VampireWizardAction.ResetAdvBackgrounds, draft.CharacterId), ButtonStyle.Secondary)
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

                                    cb.WithButton("⬅ Назад (4.2)", BuildCustomId(VampireWizardAction.BackToBackgrounds, draft.CharacterId), ButtonStyle.Secondary)
                                      .WithButton("Сбросить экран", BuildCustomId(VampireWizardAction.ResetAdvVirtues, draft.CharacterId), ButtonStyle.Secondary)
                                      .WithButton("Сбросить всё", BuildCustomId(VampireWizardAction.ResetAdvAll, draft.CharacterId), ButtonStyle.Danger);

                                    if (VampireAdvantagesResolver.IsVirtuesComplete(draft))
                                    {
                                        cb.WithButton("Далее → Шаг 5 (финал)", BuildCustomId(VampireWizardAction.NextAdvToFinishing, draft.CharacterId), ButtonStyle.Success);
                                    }

                                    return cb.Build();
                                }

                                                                    /// <summary>
                                                                    /// UI Шага 5 «Последние штрихи» в виде 3-шагового каскада:
                                                                    /// (1) категория → (2) подгруппа (для Attribute/Ability) → (3) поле.
                                                                    /// </summary>
                                                                    /// <param name="draft">Текущий черновик.</param>
                                                                    /// <param name="step">Текущий шаг каскада.</param>
                                                                    /// <param name="target">Выбранный target (если step = Subgroup или Field).</param>
                                                                    /// <param name="subgroup">Выбранная подгруппа (если step = Field).</param>
                                                                    public static MessageComponent BuildForFinishingStep(
                                                                        VampireCharacter draft,
                                                                        VampireFreebieCascadeStep step = VampireFreebieCascadeStep.Target,
                                                                        VampireFinishingResolver.FreebieTarget? target = null,
                                                                        string? subgroup = null)
                                                                    {
                                                                        if (draft == null) throw new ArgumentNullException(nameof(draft));
                                                                        if (draft.CharacterId == Guid.Empty)
                                                                            throw new ArgumentException("CharacterId обязателен", nameof(draft));

                                                                        var cb = new ComponentBuilder();

                                                                        // Step 1 (Target) и Step 2 (Subgroup) — в любом случае
                                                                        // показываем SelectMenu с 6 категориями.
                                                                        // Step 3 (Field) — показываем поля выбранной подгруппы.
                                                                        switch (step)
                                                                        {
                                                                            case VampireFreebieCascadeStep.None:
                                                                            case VampireFreebieCascadeStep.Target:
                                                                                BuildFinishingTargetSelect(cb, draft);
                                                                                break;
                                                                            case VampireFreebieCascadeStep.Subgroup:
                                                                                if (target.HasValue && !BuildFinishingSubgroupSelect(cb, draft, target.Value))
                                                                                    BuildFinishingTargetSelect(cb, draft);
                                                                                else if (!target.HasValue)
                                                                                    BuildFinishingTargetSelect(cb, draft);
                                                                                break;
                                                                            case VampireFreebieCascadeStep.Field:
                                                                                if (target.HasValue)
                                                                                    BuildFinishingFieldSelect(cb, draft, target.Value, subgroup);
                                                                                else
                                                                                    BuildFinishingTargetSelect(cb, draft);
                                                                                break;
                                                                        }

                                                                        // Специализации доступны только после завершения Шага 5.
                                                                        // V20 стр. 101: специализация требуется при значении ≥ 4; Шаг 2/3 дают
                                                                        // максимум 3, поэтому ждём Шаг 5. Завершение = либо пул = 0,
                                                                        // либо явное подтверждение игрока через диалог.
                                                                        var remaining = VampireFinishingResolver.RemainingFreebies(draft);
                                                                        var finalized = VampireFinishingResolver.IsStep5Finalized(draft);
                                                                        if (finalized || remaining == 0)
                                                                        {
                                                                            var specMenu = BuildSpecializationSelect(draft);
                                                                            if (specMenu != null) cb.WithSelectMenu(specMenu);
                                                                        }
                                                                        else if (remaining > 0 && !finalized)
                                                                        {
                                                                            // Пул ещё не пуст и Шаг 5 не подтверждён — предлагаем подтвердить.
                                                                            cb.WithButton(
                                                                                $"⚠ Подтвердить и заморозить (ост. {remaining})",
                                                                                BuildCustomId(VampireWizardAction.FinishingFinalize, draft.CharacterId),
                                                                                ButtonStyle.Danger);
                                                                        }

                                                                        // Кнопка «⬅ Назад к предыдущему шагу каскада» (только на Step 2/3).
                                                                        if (step == VampireFreebieCascadeStep.Subgroup)
                                                                        {
                                                                            cb.WithButton("⬅ Назад к категориям",
                                                                                BuildCustomId(VampireWizardAction.FinishingBackFromSubgroup, draft.CharacterId),
                                                                                ButtonStyle.Secondary);
                                                                        }
                                                                        else if (step == VampireFreebieCascadeStep.Field)
                                                                        {
                                                                            cb.WithButton("⬅ Назад к подгруппам",
                                                                                BuildCustomId(VampireWizardAction.FinishingBackFromField, draft.CharacterId),
                                                                                ButtonStyle.Secondary);
                                                                        }

                                                                        cb.WithButton("⬅ Назад (4.3)", BuildCustomId(VampireWizardAction.BackToAdvantages, draft.CharacterId), ButtonStyle.Secondary)
                                                                          .WithButton("Сбросить всё", BuildCustomId(VampireWizardAction.FinishingReset, draft.CharacterId), ButtonStyle.Danger);

                                                                        // Кнопка «Готово → лист» доступна только если Шаг 5 завершён
                                                                        // (либо пул = 0, либо игрок явно подтвердил).
                                                                        var finishingComplete = VampireFinishingResolver.IsFinishingComplete(draft);
                                                                        var doneButton = new ButtonBuilder()
                                                                            .WithLabel(finishingComplete
                                                                                ? "Готово → лист"
                                                                                : $"Готово → лист (сначала распределите или подтвердите, ост. {remaining})")
                                                                            .WithCustomId(BuildCustomId(VampireWizardAction.FinishingDone, draft.CharacterId))
                                                                            .WithStyle(ButtonStyle.Success)
                                                                            .WithDisabled(!finishingComplete);
                                                                        cb.WithButton(doneButton);

                                                                        return cb.Build();
                                                                    }

                                                                    /// <summary>
                                                                    /// Step 1 каскада Шага 5: 6 категорий свободных пунктов.
                                                                    /// В лейбле — категория и цена одного пункта. Текущие вложения НЕ показываем.
                                                                    /// </summary>
                                                                    private static void BuildFinishingTargetSelect(
                                                                        ComponentBuilder cb,
                                                                        VampireCharacter draft)
                                                                    {
                                                                        var remaining = VampireFinishingResolver.RemainingFreebies(draft);
                                                                        var menu = new SelectMenuBuilder()
                                                                            .WithCustomId(BuildCustomId(VampireWizardAction.FinishingPickTarget, draft.CharacterId))
                                                                            .WithPlaceholder($"Куда потратить? (ост. {remaining} свободных)");

                                                                        // 7 категорий: Attribute, Ability, Discipline, Background, Virtue, Humanity, Willpower.
                                                                        // Для Attribute и Ability после выбора покажем подгруппу.
                                                                        var targets = new (VampireFinishingResolver.FreebieTarget Target, string Label, bool HasSubgroup)[]
                                                                        {
                                                                            (VampireFinishingResolver.FreebieTarget.Attribute,  "Характеристика", true),
                                                                            (VampireFinishingResolver.FreebieTarget.Ability,    "Способность",    true),
                                                                            (VampireFinishingResolver.FreebieTarget.Discipline, "Дисциплина",     false),
                                                                            (VampireFinishingResolver.FreebieTarget.Background, "Факт",           false),
                                                                            (VampireFinishingResolver.FreebieTarget.Virtue,     "Добродетель",    false),
                                                                            (VampireFinishingResolver.FreebieTarget.Humanity,   "Человечность",   false),
                                                                            (VampireFinishingResolver.FreebieTarget.Willpower,  "Воля",           false),
                                                                        };

                                                                        foreach (var t in targets)
                                                                        {
                                                                            var cost = VampireFinishingResolver.CostOf(t.Target);
                                                                            var label = $"{t.Label} (-{cost})";
                                                                            var desc = t.HasSubgroup
                                                                                ? $"выбрать подгруппу (Физ/Соц/Мент или Таланты/Навыки/Знания)"
                                                                                : $"выбрать поле";
                                                                            menu.AddOption(new SelectMenuOptionBuilder()
                                                                                .WithLabel(TruncateLabel(label, 25))
                                                                                .WithValue(((int)t.Target).ToString())
                                                                                .WithDescription(TruncateLabel(desc, 50)));
                                                                        }

                                                                        cb.WithSelectMenu(menu);
                                                                    }

                                                                    /// <summary>
                                                                    /// Step 2 каскада Шага 5: подгруппы для Attribute (Physical/Social/Mental)
                                                                    /// и Ability (Talents/Skills/Knowledges). Для остальных категорий возвращает false —
                                                                    /// вызывающий код должен показать Step 1.
                                                                    /// </summary>
                                                                    /// <returns>true, если SelectMenu добавлен; false, если для target нет подгрупп.</returns>
                                                                    private static bool BuildFinishingSubgroupSelect(
                                                                        ComponentBuilder cb,
                                                                        VampireCharacter draft,
                                                                        VampireFinishingResolver.FreebieTarget target)
                                                                    {
                                                                        if (target != VampireFinishingResolver.FreebieTarget.Attribute
                                                                            && target != VampireFinishingResolver.FreebieTarget.Ability)
                                                                        {
                                                                            return false;
                                                                        }

                                                                        var remaining = VampireFinishingResolver.RemainingFreebies(draft);
                                                                        var menu = new SelectMenuBuilder()
                                                                            .WithCustomId(BuildCustomIdWithArg(VampireWizardAction.FinishingPickSubgroup, draft.CharacterId, ((int)target).ToString()))
                                                                            .WithPlaceholder($"Подгруппа? (ост. {remaining} свободных)");

                                                                        if (target == VampireFinishingResolver.FreebieTarget.Attribute)
                                                                        {
                                                                            foreach (var group in new[] { "Physical", "Social", "Mental" })
                                                                            {
                                                                                menu.AddOption(new SelectMenuOptionBuilder()
                                                                                    .WithLabel(TruncateLabel(GroupLabel(group), 25))
                                                                                    .WithValue(group)
                                                                                    .WithDescription("3 характеристики этой группы"));
                                                                            }
                                                                        }
                                                                        else // Ability
                                                                        {
                                                                            foreach (var group in new[] { "Talents", "Skills", "Knowledges" })
                                                                            {
                                                                                menu.AddOption(new SelectMenuOptionBuilder()
                                                                                    .WithLabel(TruncateLabel(GroupLabel(group), 25))
                                                                                    .WithValue(group)
                                                                                    .WithDescription("10 способностей этой группы"));
                                                                            }
                                                                        }

                                                                        cb.WithSelectMenu(menu);
                                                                        return true;
                                                                    }

                                                                    /// <summary>
                                                                    /// Step 3 каскада Шага 5: поля выбранной подгруппы с текущими значениями и ценой.
                                                                    /// </summary>
                                                                    private static void BuildFinishingFieldSelect(
                                                                        ComponentBuilder cb,
                                                                        VampireCharacter draft,
                                                                        VampireFinishingResolver.FreebieTarget target,
                                                                        string? subgroup)
                                                                    {
                                                                        var remaining = VampireFinishingResolver.RemainingFreebies(draft);
                                                                        var cost = VampireFinishingResolver.CostOf(target);
                                                                        var menu = new SelectMenuBuilder()
                                                                            .WithCustomId(BuildCustomIdWithArg(VampireWizardAction.FinishingApplyField, draft.CharacterId, ((int)target).ToString()))
                                                                            .WithPlaceholder($"Поле? (ост. {remaining}, цена -{cost})");

                                                                        if (target == VampireFinishingResolver.FreebieTarget.Humanity)
                                                                        {
                                                                            AppendHumanityWillpowerOptions(menu, draft);
                                                                        }
                                                                        else
                                                                        {
                                                                            // Список полей зависит от target и subgroup.
                                                                            var fields = ResolveFinishingFields(draft, target, subgroup);
                                                                            foreach (var field in fields)
                                                                            {
                                                                                var cur = ReadFinishingFieldValue(draft, target, field);
                                                                                var hardCap = TargetHardCap(target);
                                                                                var canAdd = remaining >= cost && cur + 1 <= hardCap;
                                                                                var canDec = cur > 0;
                                                                                if (canAdd)
                                                                                {
                                                                                    var incValue = $"+:{target}:{field}";
                                                                                    menu.AddOption(new SelectMenuOptionBuilder()
                                                                                        .WithLabel($"+ {Truncate(field, 18)} ({cur}→{cur + 1})")
                                                                                        .WithValue(incValue)
                                                                                        .WithDescription($"-{cost} свободных"));
                                                                                }
                                                                                if (canDec)
                                                                                {
                                                                                    var decValue = $"−:{target}:{field}";
                                                                                    menu.AddOption(new SelectMenuOptionBuilder()
                                                                                        .WithLabel($"− {Truncate(field, 18)} ({cur}→{cur - 1})")
                                                                                        .WithValue(decValue)
                                                                                        .WithDescription($"+{cost} свободных"));
                                                                                }
                                                                            }
                                                                        }

                                                                        cb.WithSelectMenu(menu);
                                                                    }

                                                                    private static void AppendHumanityWillpowerOptions(
                                                                        SelectMenuBuilder menu,
                                                                        VampireCharacter draft)
                                                                    {
                                                                        var remaining = VampireFinishingResolver.RemainingFreebies(draft);
                                                                        foreach (var label in new[] { "Человечность", "Воля" })
                                                                        {
                                                                            var target = label == "Человечность"
                                                                                ? VampireFinishingResolver.FreebieTarget.Humanity
                                                                                : VampireFinishingResolver.FreebieTarget.Willpower;
                                                                            var cost = VampireFinishingResolver.CostOf(target);
                                                                            var cur = label == "Человечность"
                                                                                ? Math.Max(0, draft.HumanityBonus)
                                                                                : Math.Max(0, draft.WillpowerBonus);
                                                                            var (hardCap, _) = VampireFinishingResolver.GetCaps(target, draft);
                                                                            var canAdd = remaining >= cost && cur + 1 <= hardCap;
                                                                            var canDec = cur > 0;
                                                                            if (canAdd)
                                                                            {
                                                                                menu.AddOption(new SelectMenuOptionBuilder()
                                                                                    .WithLabel($"+ {Truncate(label, 18)} ({cur}→{cur + 1})")
                                                                                    .WithValue($"+:{target}:{label}")
                                                                                    .WithDescription($"-{cost} свободных"));
                                                                            }
                                                                            if (canDec)
                                                                            {
                                                                                menu.AddOption(new SelectMenuOptionBuilder()
                                                                                    .WithLabel($"− {Truncate(label, 18)} ({cur}→{cur - 1})")
                                                                                    .WithValue($"−:{target}:{label}")
                                                                                    .WithDescription($"+{cost} свободных"));
                                                                            }
                                                                        }
                                                                    }

                                                                    /// <summary>Получить список имён полей для target+subgroup.</summary>
                                                                    private static IEnumerable<string> ResolveFinishingFields(
                                                                        VampireCharacter draft,
                                                                        VampireFinishingResolver.FreebieTarget target,
                                                                        string? subgroup)
                                                                    {
                                                                        if (target == VampireFinishingResolver.FreebieTarget.Attribute)
                                                                        {
                                                                            if (subgroup == "Physical") return new[] { "Сила", "Ловкость", "Выносливость" };
                                                                            if (subgroup == "Social")   return new[] { "Обаяние", "Манипуляция", "Привлекательность" };
                                                                            if (subgroup == "Mental")   return new[] { "Восприятие", "Интеллект", "Смекалка" };
                                                                            return AttributeFieldNames();
                                                                        }
                                                                        if (target == VampireFinishingResolver.FreebieTarget.Ability)
                                                                        {
                                                                            if (subgroup == "Talents")    return FilterAbilityGroup(VampireAbilityGroup.Talents);
                                                                            if (subgroup == "Skills")     return FilterAbilityGroup(VampireAbilityGroup.Skills);
                                                                            if (subgroup == "Knowledges") return FilterAbilityGroup(VampireAbilityGroup.Knowledges);
                                                                            return AbilityFieldNames();
                                                                        }
                                                                        if (target == VampireFinishingResolver.FreebieTarget.Discipline)
                                                                            return VampireAdvantagesResolver.EffectiveDisciplineSlots(draft);
                                                                        if (target == VampireFinishingResolver.FreebieTarget.Background)
                                                                            return draft.Backgrounds?.Keys.ToList() ?? new List<string>();
                                                                        if (target == VampireFinishingResolver.FreebieTarget.Virtue)
                                                                            return VampireParameterCatalog.Virtues;
                                                                        return System.Array.Empty<string>();
                                                                    }

                                                                    private static string[] FilterAbilityGroup(VampireAbilityGroup group)
                                                                        => VampireAbilitiesCatalog.NamesInGroup(group).ToArray();

                                                                    private static int ReadFinishingFieldValue(
                                                                        VampireCharacter draft,
                                                                        VampireFinishingResolver.FreebieTarget target,
                                                                        string field)
                                                                    {
                                                                        return target switch
                                                                        {
                                                                            VampireFinishingResolver.FreebieTarget.Attribute  => VampireFinishingResolver.ReadFieldValue(draft, target, field),
                                                                            VampireFinishingResolver.FreebieTarget.Ability    => VampireFinishingResolver.ReadFieldValue(draft, target, field),
                                                                            VampireFinishingResolver.FreebieTarget.Discipline => VampireAdvantagesResolver.GetDisciplineValue(draft, field),
                                                                            VampireFinishingResolver.FreebieTarget.Background => VampireAdvantagesResolver.GetBackgroundRank(draft, field),
                                                                            VampireFinishingResolver.FreebieTarget.Virtue     => VampireAdvantagesResolver.GetVirtueValue(draft, field),
                                                                            _ => 0,
                                                                        };
                                                                    }

                                                                    private static int TargetHardCap(VampireFinishingResolver.FreebieTarget target) => target switch
                                                                    {
                                                                        VampireFinishingResolver.FreebieTarget.Attribute  => 5,
                                                                        VampireFinishingResolver.FreebieTarget.Ability    => 5,
                                                                        VampireFinishingResolver.FreebieTarget.Discipline => 5,
                                                                        VampireFinishingResolver.FreebieTarget.Background => 5,
                                                                        VampireFinishingResolver.FreebieTarget.Virtue     => 5,
                                                                        VampireFinishingResolver.FreebieTarget.Humanity   => 10,
                                                                        VampireFinishingResolver.FreebieTarget.Willpower  => 10,
                                                                        _ => 5,
                                                                    };

                                                                    private static string GroupLabel(string en) => en switch
                                                                    {
                                                                        "Physical"   => "Физические (Сила/Лов/Вын)",
                                                                        "Social"     => "Социальные (Обаяние/Манип/Привл)",
                                                                        "Mental"     => "Ментальные (Восп/Инт/Смек)",
                                                                        "Talents"    => "Таланты",
                                                                        "Skills"     => "Навыки",
                                                                        "Knowledges" => "Знания",
                                                                        _ => en,
                                                                    };

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
                                /// SelectMenu Шага 4.2: все факты в одном меню (6 × 4 = 24 опции максимум).
                                /// Каждая опция: <c>value = "{operation}:{name}"</c>, где operation ∈
                                /// <c>+:rank</c>, <c>−:rank</c>, <c>rename</c>, <c>remove</c>.
                                /// </summary>
                                private static SelectMenuBuilder? BuildBackgroundSelect(VampireCharacter draft)
                                {
                                    if (draft.Backgrounds == null || draft.Backgrounds.Count == 0) return null;

                                    var menu = new SelectMenuBuilder()
                                        .WithCustomId(BuildCustomId(VampireWizardAction.BackgroundInc, draft.CharacterId))
                                        .WithPlaceholder($"Факты биографии ({draft.Backgrounds.Count} шт.)");

                                    foreach (var kv in draft.Backgrounds)
                                    {
                                        var name = kv.Key;
                                        var rank = kv.Value;
                                        var nameShort = Truncate(name, 18);

                                        menu.AddOption(new SelectMenuOptionBuilder()
                                            .WithLabel($"+ ранг «{nameShort}» ({rank})")
                                            .WithValue($"+:rank:{name}")
                                            .WithDescription($"поднять ранг «{nameShort}» на 1"));

                                        if (rank > 1)
                                        {
                                            menu.AddOption(new SelectMenuOptionBuilder()
                                                .WithLabel($"− ранг «{nameShort}» ({rank})")
                                                .WithValue($"−:rank:{name}")
                                                .WithDescription($"опустить ранг «{nameShort}» на 1"));
                                        }

                                        menu.AddOption(new SelectMenuOptionBuilder()
                                            .WithLabel($"✎ Переименовать «{nameShort}»")
                                            .WithValue($"rename:{name}")
                                            .WithDescription($"ввести новое имя для «{nameShort}» в ЛС"));

                                        menu.AddOption(new SelectMenuOptionBuilder()
                                            .WithLabel($"🗑 Удалить «{nameShort}»")
                                            .WithValue($"remove:{name}")
                                            .WithDescription($"полностью убрать «{nameShort}»"));
                                    }

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
        VampireWizardAction.ConfirmCancel => "confirm_cancel",
        VampireWizardAction.ResumeWizard  => "resume_wizard",
                VampireWizardAction.AttrInc     => "attr_inc",
                VampireWizardAction.AttrDec     => "attr_dec",
                                VampireWizardAction.AttrPriority => "attr_priority",
                                VampireWizardAction.AttrSelect   => "attr_select",
                                VampireWizardAction.AttrPageNext => "attr_page_next",
                                VampireWizardAction.AttrPagePrev => "attr_page_prev",
                VampireWizardAction.ResetAttrProgress => "reset_attr_progress",
                VampireWizardAction.ResetAttrAll      => "reset_attr_all",
                VampireWizardAction.BackToConcept     => "back_to_concept",
                        VampireWizardAction.AbilityPriority     => "ability_priority",
                        VampireWizardAction.AbilityGroupSelect  => "ability_group_select",
                        VampireWizardAction.AbilitySelect       => "ability_select",
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
                        VampireWizardAction.ResetAdvDisciplines => "reset_adv_disciplines",
                        VampireWizardAction.ResetAdvBackgrounds => "reset_adv_backgrounds",
                        VampireWizardAction.ResetAdvVirtues    => "reset_adv_virtues",
                        VampireWizardAction.ResetAdvProgress   => "reset_adv_progress",
                        VampireWizardAction.ResetAdvAll        => "reset_adv_all",
                        VampireWizardAction.BackToAbilities      => "back_to_abilities",
                        VampireWizardAction.BackToDisciplines    => "back_to_disciplines",
                        VampireWizardAction.BackToBackgrounds    => "back_to_backgrounds",
                        VampireWizardAction.NextAdvToBackgrounds => "next_adv_to_backgrounds",
                        VampireWizardAction.NextAdvToVirtues     => "next_adv_to_virtues",
                        VampireWizardAction.NextAdvToFinishing   => "next_adv_to_finishing",
                                                VampireWizardAction.FinishingInc        => "finishing_inc",
                                                VampireWizardAction.FinishingReset      => "finishing_reset",
                                                VampireWizardAction.FinishingDone       => "finishing_done",
                                                VampireWizardAction.FinishingFinalize   => "finishing_finalize",
                                                VampireWizardAction.FinishingPickTarget   => "finishing_pick_target",
                                                VampireWizardAction.FinishingPickSubgroup => "finishing_pick_subgroup",
                                                VampireWizardAction.FinishingApplyField   => "finishing_apply_field",
                                                VampireWizardAction.FinishingBackFromSubgroup => "finishing_back_from_subgroup",
                                                VampireWizardAction.FinishingBackFromField   => "finishing_back_from_field",
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
            case "confirm_cancel": action = VampireWizardAction.ConfirmCancel; return true;
            case "resume_wizard":  action = VampireWizardAction.ResumeWizard;  return true;
                        case "attr_inc":       action = VampireWizardAction.AttrInc;       return true;
                        case "attr_dec":       action = VampireWizardAction.AttrDec;       return true;
                                                case "attr_priority":  action = VampireWizardAction.AttrPriority;  return true;
                                                case "attr_select":    action = VampireWizardAction.AttrSelect;    return true;
                                                case "attr_page_next": action = VampireWizardAction.AttrPageNext; return true;
                                                case "attr_page_prev": action = VampireWizardAction.AttrPagePrev; return true;
                        case "reset_attr_progress": action = VampireWizardAction.ResetAttrProgress; return true;
                        case "reset_attr_all": action = VampireWizardAction.ResetAttrAll;  return true;
                        case "back_to_concept": action = VampireWizardAction.BackToConcept; return true;
                                    case "ability_priority":    action = VampireWizardAction.AbilityPriority;    return true;
                                    case "ability_group_select": action = VampireWizardAction.AbilityGroupSelect; return true;
                                    case "ability_select":      action = VampireWizardAction.AbilitySelect;      return true;
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
                                    case "reset_adv_disciplines": action = VampireWizardAction.ResetAdvDisciplines; return true;
                                    case "reset_adv_backgrounds": action = VampireWizardAction.ResetAdvBackgrounds; return true;
                                    case "reset_adv_virtues":     action = VampireWizardAction.ResetAdvVirtues;     return true;
                                    case "reset_adv_progress":    action = VampireWizardAction.ResetAdvProgress;    return true;
                                    case "reset_adv_all":         action = VampireWizardAction.ResetAdvAll;         return true;
                                    case "back_to_abilities":   action = VampireWizardAction.BackToAbilities;   return true;
                                    case "back_to_disciplines": action = VampireWizardAction.BackToDisciplines; return true;
                                    case "back_to_backgrounds": action = VampireWizardAction.BackToBackgrounds; return true;
                                    case "next_adv_to_backgrounds": action = VampireWizardAction.NextAdvToBackgrounds; return true;
                                    case "next_adv_to_virtues":     action = VampireWizardAction.NextAdvToVirtues;     return true;
                                    case "next_adv_to_finishing":   action = VampireWizardAction.NextAdvToFinishing;   return true;
                                    case "finishing_inc":          action = VampireWizardAction.FinishingInc;          return true;
                                    case "finishing_reset":        action = VampireWizardAction.FinishingReset;        return true;
                                    case "finishing_done":         action = VampireWizardAction.FinishingDone;         return true;
                                    case "finishing_finalize":     action = VampireWizardAction.FinishingFinalize;     return true;
                                    case "finishing_pick_target":   action = VampireWizardAction.FinishingPickTarget;   return true;
                                    case "finishing_pick_subgroup": action = VampireWizardAction.FinishingPickSubgroup; return true;
                                    case "finishing_apply_field":   action = VampireWizardAction.FinishingApplyField;   return true;
                                    case "finishing_back_from_subgroup": action = VampireWizardAction.FinishingBackFromSubgroup; return true;
                                    case "finishing_back_from_field":   action = VampireWizardAction.FinishingBackFromField;   return true;
                                    case "back_to_advantages":     action = VampireWizardAction.BackToAdvantages;     return true;
                                                default:               action = default;                         return false;
        }
    }
}
