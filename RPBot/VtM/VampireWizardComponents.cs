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
                                    default:               action = default;                         return false;
        }
    }
}
