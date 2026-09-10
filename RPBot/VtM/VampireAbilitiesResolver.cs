using System.Text;

namespace RPBot.VtM;

/// <summary>
/// Бизнес-логика Шага 3 «Способности 13/9/5».
/// </summary>
/// <remarks>
/// <para>VtM V20 (стр. 89): распределить 13/9/5 между талантами, навыками
/// и знаниями. База = 0. На этом шаге максимум 3 (далее поднимается
/// свободными пунктами на Шаге 5).</para>
/// <para>Специализация требуется при достижении значением параметра ≥ 4
/// (V20 стр. 101). Бот хранит 3 примера специализаций для каждой
/// способности + свободный ввод.</para>
/// </remarks>
public static class VampireAbilitiesResolver
{
    public enum VampireAbilitiesFailure
    {
        None,
        PriorityRequired,
        UnknownAbility,
        BelowBase,
        GroupBudgetExceeded,
        AboveStepThreeCap,
        InvalidPriority,
        SpecializationRequired,
    }

    public readonly struct VampireAbilitiesDecision
    {
        public bool IsSuccess { get; }
        public VampireAbilitiesFailure Failure { get; }
        public string Message { get; }

        public VampireAbilitiesDecision(bool ok, VampireAbilitiesFailure f, string msg)
        {
            IsSuccess = ok;
            Failure = f;
            Message = msg;
        }

        public static VampireAbilitiesDecision Ok(string msg = "OK") =>
            new VampireAbilitiesDecision(true, VampireAbilitiesFailure.None, msg);
        public static VampireAbilitiesDecision Fail(VampireAbilitiesFailure f, string msg) =>
            new VampireAbilitiesDecision(false, f, msg);
    }

    /// <summary>
    /// Установить приоритет групп способностей (13/9/5).
    /// </summary>
    public static VampireAbilitiesDecision SetPriority(
        VampireCharacter draft,
        VampireAbilityPriority priority)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        if (!VampireAbilityPriorityExtensions.All.Contains(priority))
            return VampireAbilitiesDecision.Fail(VampireAbilitiesFailure.InvalidPriority,
                "Приоритет должен быть одной из 6 стандартных перестановок 13/9/5.");

        draft.AbilitiesPriority = priority.ToString();
        return CompleteCheck(draft);
    }

    /// <summary>
    /// Инкрементировать способность на 1 (Шаг 3: макс 3 в любой способности).
    /// </summary>
    public static VampireAbilitiesDecision Increment(
        VampireCharacter draft,
        VampireAbilityGroup group,
        string abilityName)
    {
        return SetAbilityDelta(draft, abilityName, +1, group);
    }

    /// <summary>Инкрементировать — обратная совместимость: найти группу по имени.</summary>
    public static VampireAbilitiesDecision Increment(VampireCharacter draft, string abilityName)
    {
        var group = VampireAbilitiesCatalog.FindGroup(abilityName);
        if (!group.HasValue)
            return VampireAbilitiesDecision.Fail(VampireAbilitiesFailure.UnknownAbility,
                $"Неизвестная способность: {abilityName}");
        return Increment(draft, group.Value, abilityName);
    }

    /// <summary>Декрементировать способность на 1 (не ниже 0).</summary>
    public static VampireAbilitiesDecision Decrement(
        VampireCharacter draft,
        VampireAbilityGroup group,
        string abilityName)
    {
        return SetAbilityDelta(draft, abilityName, -1, group);
    }

    public static VampireAbilitiesDecision Decrement(VampireCharacter draft, string abilityName)
    {
        var group = VampireAbilitiesCatalog.FindGroup(abilityName);
        if (!group.HasValue)
            return VampireAbilitiesDecision.Fail(VampireAbilitiesFailure.UnknownAbility,
                $"Неизвестная способность: {abilityName}");
        return Decrement(draft, group.Value, abilityName);
    }

    /// <summary>Сбросить все способности к 0 (для кнопки «Сбросить прогресс»).</summary>
    public static VampireAbilitiesDecision ResetProgress(VampireCharacter draft)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        draft.AbilitiesStruct = new VampireAbilities();
        // Сбросить специализации для способностей (но не трогать специализации характеристик).
        ClearAbilitySpecializations(draft);
        return CompleteCheck(draft);
    }

    /// <summary>Сбросить способности только в одной группе (для кнопки «Сбросить группу» на Шаге 3).</summary>
    public static VampireAbilitiesDecision ResetGroup(VampireCharacter draft, VampireAbilityGroup group)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        var groupNames = VampireAbilitiesCatalog.NamesInGroup(group);
        if (groupNames == null || groupNames.Count == 0)
        {
            return VampireAbilitiesDecision.Fail(VampireAbilitiesFailure.UnknownAbility,
                $"Неизвестная группа: {group}");
        }
        foreach (var name in groupNames)
        {
            SetAbilityValue(draft.AbilitiesStruct, name, 0);
        }
        ClearAbilitySpecializationsForGroup(draft, group);
        return CompleteCheck(draft);
    }

    /// <summary>Сбросить и приоритет, и способности (для кнопки «Сбросить всё»).</summary>
    public static VampireAbilitiesDecision ResetAll(VampireCharacter draft)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        draft.AbilitiesPriority = "";
        draft.AbilitiesStruct = new VampireAbilities();
        ClearAbilitySpecializations(draft);
        return VampireAbilitiesDecision.Ok("Сброшено.");
    }

    /// <summary>
    /// Проверить, завершён ли Шаг 3 (всего потрачено 27 пунктов: 13+9+5).
    /// </summary>
    public static bool IsAbilitiesComplete(VampireCharacter draft)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        if (!TryParsePriority(draft.AbilitiesPriority, out var priority)) return false;
        var totalSpent = GetTotalSpent(draft.AbilitiesStruct);
        if (totalSpent != 27) return false;

        // Все очки каждой группы должны быть истрачены полностью.
        foreach (var group in new[]
                 { VampireAbilityGroup.Talents, VampireAbilityGroup.Skills, VampireAbilityGroup.Knowledges })
        {
            var spent = GetGroupSpent(draft.AbilitiesStruct, group);
            var budget = priority.PointsFor(group);
            if (spent != budget) return false;
        }
        return true;
    }

    /// <summary>
    /// Сколько очков осталось в группе.
    /// </summary>
    public static int RemainingInGroup(VampireCharacter draft, VampireAbilityGroup group)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        if (!TryParsePriority(draft.AbilitiesPriority, out var priority)) return 0;
        return priority.PointsFor(group) - GetGroupSpent(draft.AbilitiesStruct, group);
    }

    /// <summary>
        /// Установить специализацию для способности или характеристики (вызывается из UI
        /// или текстового ввода). Специализация требуется при итоговом значении ≥ 4
        /// (V20 стр. 101).
        /// </summary>
        /// <remarks>
        /// Универсальный метод: имя параметра может быть как именем способности
        /// (<see cref="VampireAbilitiesCatalog"/>), так и именем характеристики
        /// (<see cref="VampireAttributeCatalog"/>). Хранится в общем словаре
        /// <see cref="VampireCharacter.Specializations"/>.
        /// </remarks>
            public static VampireAbilitiesDecision SetSpecialization(
            VampireCharacter draft, string paramName, string specialization)
        {
            if (draft == null) throw new System.ArgumentNullException(nameof(draft));
            if (string.IsNullOrWhiteSpace(paramName))
                return VampireAbilitiesDecision.Fail(VampireAbilitiesFailure.UnknownAbility,
                    "Не указано имя параметра для специализации.");

            if (string.IsNullOrWhiteSpace(specialization))
            {
                        // Очистка специализации разрешена в любой момент (даже до Шага 5).
                draft.Specializations.Remove(paramName);
                return VampireAbilitiesDecision.Ok("Специализация очищена.");
            }

            var abilityGroup = VampireAbilitiesCatalog.FindGroup(paramName);
            var attributeGroup = abilityGroup.HasValue
                ? null
                : VampireAttributeCatalog.FindGroup(paramName);

            if (!abilityGroup.HasValue && !attributeGroup.HasValue)
                return VampireAbilitiesDecision.Fail(VampireAbilitiesFailure.UnknownAbility,
                    "Не является ни валидной способностью, ни валидной характеристикой.");

            // Специализация опирается на итоговое значение (Шаг + freebie-бонус).
            var value = abilityGroup.HasValue
                ? draft.GetAbilityValue(paramName)
                : draft.GetAttributeValue(paramName);
            if (value < 4)
                return VampireAbilitiesDecision.Fail(VampireAbilitiesFailure.SpecializationRequired,
                    $"Специализация доступна при значении 4 и более. У «{paramName}» сейчас {value}.");

                    // Запрет: Шаги 2 и 3 ещё не учитывают freebie; специализации способностей
                    // требуют завершения Шага 5 (либо пул = 0, либо явное подтверждение).
                    // V20 стр. 101: специализация при value ≥ 4. Способности Шагом 3 дают
                    // максимум 3 — поэтому Шаг 5 ОБЯЗАТЕЛЕН для их специализации.
                    if (!VampireFinishingResolver.SpecializationsAllowed(draft))
                        return VampireAbilitiesDecision.Fail(VampireAbilitiesFailure.SpecializationRequired,
                            "Специализации доступны только после завершения Шага 5 (потратьте все freebie или подтвердите завершение через диалог).");

                    draft.Specializations[paramName] = specialization.Trim();
                    return VampireAbilitiesDecision.Ok($"Специализация «{paramName}» → «{specialization.Trim()}».");
                }

    /// <summary>
    /// Получить специализацию (или пустую строку).
    /// </summary>
    public static string GetSpecialization(VampireCharacter draft, string paramName)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        return draft.Specializations.TryGetValue(paramName, out var s) ? s : "";
    }

    /// <summary>
    /// Сообщение статуса для embed Шага 3.
    /// </summary>
    public static string BuildAbilitiesStatusMessage(VampireCharacter draft)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        var sb = new StringBuilder();
        sb.AppendLine("**Шаг 3 — Способности 13/9/5**");
        sb.AppendLine();
        sb.AppendLine("Распределите очки по способностям (13/9/5). Каждая способность стартует с 0 пунктов. На этом шаге максимум — 3.");
        sb.AppendLine();

        if (TryParsePriority(draft.AbilitiesPriority, out var priority))
        {
            sb.AppendLine($"**Приоритет:** {priority.HumanName()}");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("_Приоритет не выбран. Используйте выпадающее меню выше._");
            sb.AppendLine();
        }

        foreach (var group in new[]
                 { VampireAbilityGroup.Talents, VampireAbilityGroup.Skills, VampireAbilityGroup.Knowledges })
        {
            var groupNames = group switch
            {
                VampireAbilityGroup.Talents => "Таланты",
                VampireAbilityGroup.Skills => "Навыки",
                VampireAbilityGroup.Knowledges => "Знания",
                _ => group.ToString(),
            };

            var budget = TryParsePriority(draft.AbilitiesPriority, out var p) ? p.PointsFor(group) : 0;
            var spent = GetGroupSpent(draft.AbilitiesStruct, group);
            var overBudget = budget > 0 && spent > budget;
            var header = overBudget
                ? $"**{groupNames}** (⚠️ потрачено {spent}/{budget} — превышение на {spent - budget})"
                : $"**{groupNames}** (потрачено {spent}/{budget}):";
            sb.AppendLine(header);

            foreach (var name in VampireAbilitiesCatalog.NamesInGroup(group))
            {
                var value = GetAbilityValue(draft.AbilitiesStruct, name);
                var spec = GetSpecialization(draft, name);
                var line = $"  • {name}: **{value}**";
                if (!string.IsNullOrEmpty(spec))
                {
                    line += $"\n    *Спец: {spec}*";
                }
                sb.AppendLine(line);
            }
            sb.AppendLine();
        }

        var totalSpent = GetTotalSpent(draft.AbilitiesStruct);
        sb.AppendLine($"**Итого потрачено:** {totalSpent}/27");
        sb.AppendLine();

        if (TryParsePriority(draft.AbilitiesPriority, out var pr))
        {
            var overGroups = new System.Collections.Generic.List<string>();
            foreach (var g in new[] { VampireAbilityGroup.Talents, VampireAbilityGroup.Skills, VampireAbilityGroup.Knowledges })
            {
                var s = GetGroupSpent(draft.AbilitiesStruct, g);
                var b = pr.PointsFor(g);
                if (s > b) overGroups.Add(g switch
                {
                    VampireAbilityGroup.Talents => "Таланты",
                    VampireAbilityGroup.Skills => "Навыки",
                    VampireAbilityGroup.Knowledges => "Знания",
                    _ => g.ToString(),
                });
            }
            if (overGroups.Count > 0)
            {
                sb.AppendLine($"⚠️ **Превышение бюджета:** {string.Join(", ", overGroups)}. " +
                              "Уменьшите значения в этих группах или смените приоритет.");
                sb.AppendLine();
            }
        }

        sb.AppendLine(IsAbilitiesComplete(draft)
            ? "✅ Все 27 пунктов распределены по приоритету. Нажмите «Далее», чтобы перейти к Шагу 4 (преимущества)."
            : "⏳ Распределите все пункты. Когда все группы будут потрачены полностью — появится кнопка «Далее».");
        return sb.ToString();
    }

    // ── Внутренние хелперы ──────────────────────────────────────────────

    private static VampireAbilitiesDecision SetAbilityDelta(
        VampireCharacter draft,
        string abilityName,
        int delta,
        VampireAbilityGroup? groupArg = null)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        var group = groupArg ?? VampireAbilitiesCatalog.FindGroup(abilityName);
        if (!group.HasValue)
            return VampireAbilitiesDecision.Fail(VampireAbilitiesFailure.UnknownAbility,
                $"«{abilityName}» не является валидной способностью.");

        if (!TryParsePriority(draft.AbilitiesPriority, out var priority))
            return VampireAbilitiesDecision.Fail(VampireAbilitiesFailure.PriorityRequired,
                "Сначала выберите приоритет (выпадающее меню выше).");

        var current = GetAbilityValue(draft.AbilitiesStruct, abilityName);
        var newValue = current + delta;

        if (newValue < 0)
            return VampireAbilitiesDecision.Fail(VampireAbilitiesFailure.BelowBase,
                $"Нельзя уменьшить «{abilityName}» ниже 0.");

        // На Шаге 3 максимум = 3 (V20 стр. 89).
        if (delta > 0 && newValue > 3)
            return VampireAbilitiesDecision.Fail(VampireAbilitiesFailure.AboveStepThreeCap,
                $"На Шаге 3 максимум 3 пункта в любой способности. Свободные пункты тратятся на Шаге 5.");

        var g = group.Value;
        var budget = priority.PointsFor(g);
        var spent = GetGroupSpent(draft.AbilitiesStruct, g);
        var newSpent = spent + delta;
        if (newSpent > budget)
            return VampireAbilitiesDecision.Fail(VampireAbilitiesFailure.GroupBudgetExceeded,
                $"Превышен бюджет группы {g}. Доступно: {budget - spent} (нужно {delta}).");

        SetAbilityValue(draft.AbilitiesStruct, abilityName, newValue);
        return CompleteCheck(draft);
    }

    private static bool TryParsePriority(string s, out VampireAbilityPriority priority)
    {
        priority = default;
        if (string.IsNullOrEmpty(s)) return false;
        return System.Enum.TryParse(s, out priority);
    }

    private static int GetGroupSpent(VampireAbilities abs, VampireAbilityGroup group)
    {
        int sum = 0;
        foreach (var name in VampireAbilitiesCatalog.NamesInGroup(group))
            sum += GetAbilityValue(abs, name);
        return sum;
    }

    private static int GetTotalSpent(VampireAbilities abs)
    {
        int sum = 0;
        foreach (var group in new[]
                 { VampireAbilityGroup.Talents, VampireAbilityGroup.Skills, VampireAbilityGroup.Knowledges })
            sum += GetGroupSpent(abs, group);
        return sum;
    }

    /// <summary>Получить значение способности по русскому имени (например, «Атлетика» → Атлетика).</summary>
    public static int GetAbilityValue(VampireAbilities abs, string name) => name switch
    {
        "Атлетика"                 => abs.Атлетика,
        "Бдительность"             => abs.Бдительность,
        "Драка"                    => abs.Драка,
        "Запугивание"              => abs.Запугивание,
        "Красноречие"              => abs.Красноречие,
        "Лидерство"                => abs.Лидерство,
        "Уличное чутьё"            => abs.УличноеЧутьё,
        "Хитрость"                 => abs.Хитрость,
        "Шестое чувство"           => abs.ШестоеЧувство,
        "Эмпатия"                  => abs.Эмпатия,
        "Вождение"                 => abs.Вождение,
        "Воровство"                => abs.Воровство,
        "Выживание"                => abs.Выживание,
        "Исполнение"               => abs.Исполнение,
        "Обращение с животными"    => abs.ОбращениеСЖивотными,
        "Ремесло"                  => abs.Ремесло,
        "Скрытность"               => abs.Скрытность,
        "Стрельба"                 => abs.Стрельба,
        "Фехтование"               => abs.Фехтование,
        "Этикет"                   => abs.Этикет,
        "Гуманитарные науки"       => abs.ГуманитарныеНауки,
        "Естественные науки"       => abs.ЕстественныеНауки,
        "Информатика"              => abs.Информатика,
        "Медицина"                 => abs.Медицина,
        "Оккультизм"               => abs.Оккультизм,
        "Политика"                 => abs.Политика,
        "Расследование"            => abs.Расследование,
        "Финансы"                  => abs.Финансы,
        "Электроника"              => abs.Электроника,
        "Юриспруденция"            => abs.Юриспруденция,
        _ => 0,
    };

    private static void SetAbilityValue(VampireAbilities abs, string name, int value)
    {
        if (value < 0) value = 0;
        switch (name)
        {
            case "Атлетика":              abs.Атлетика = value; break;
            case "Бдительность":          abs.Бдительность = value; break;
            case "Драка":                 abs.Драка = value; break;
            case "Запугивание":           abs.Запугивание = value; break;
            case "Красноречие":           abs.Красноречие = value; break;
            case "Лидерство":             abs.Лидерство = value; break;
            case "Уличное чутьё":         abs.УличноеЧутьё = value; break;
            case "Хитрость":              abs.Хитрость = value; break;
            case "Шестое чувство":        abs.ШестоеЧувство = value; break;
            case "Эмпатия":               abs.Эмпатия = value; break;
            case "Вождение":              abs.Вождение = value; break;
            case "Воровство":             abs.Воровство = value; break;
            case "Выживание":             abs.Выживание = value; break;
            case "Исполнение":            abs.Исполнение = value; break;
            case "Обращение с животными": abs.ОбращениеСЖивотными = value; break;
            case "Ремесло":               abs.Ремесло = value; break;
            case "Скрытность":            abs.Скрытность = value; break;
            case "Стрельба":              abs.Стрельба = value; break;
            case "Фехтование":            abs.Фехтование = value; break;
            case "Этикет":                abs.Этикет = value; break;
            case "Гуманитарные науки":    abs.ГуманитарныеНауки = value; break;
            case "Естественные науки":    abs.ЕстественныеНауки = value; break;
            case "Информатика":           abs.Информатика = value; break;
            case "Медицина":              abs.Медицина = value; break;
            case "Оккультизм":            abs.Оккультизм = value; break;
            case "Политика":              abs.Политика = value; break;
            case "Расследование":         abs.Расследование = value; break;
            case "Финансы":               abs.Финансы = value; break;
            case "Электроника":           abs.Электроника = value; break;
            case "Юриспруденция":         abs.Юриспруденция = value; break;
        }
    }

    private static void ClearAbilitySpecializations(VampireCharacter draft)
    {
        // Удаляем только специализации, ключ которых — известная способность.
        var keysToRemove = new System.Collections.Generic.List<string>();
        foreach (var kv in draft.Specializations)
        {
            if (VampireAbilitiesCatalog.FindGroup(kv.Key).HasValue)
                keysToRemove.Add(kv.Key);
        }
        foreach (var k in keysToRemove) draft.Specializations.Remove(k);
    }

    /// <summary>
    /// Удалить специализации только в одной группе способностей
    /// (для <see cref="ResetGroup"/>). Атрибутные специализации не трогаем.
    /// </summary>
    private static void ClearAbilitySpecializationsForGroup(VampireCharacter draft, VampireAbilityGroup group)
    {
        var names = VampireAbilitiesCatalog.NamesInGroup(group);
        if (names == null) return;
        var nameSet = new System.Collections.Generic.HashSet<string>(names);
        var keysToRemove = new System.Collections.Generic.List<string>();
        foreach (var kv in draft.Specializations)
        {
            if (nameSet.Contains(kv.Key))
                keysToRemove.Add(kv.Key);
        }
        foreach (var k in keysToRemove) draft.Specializations.Remove(k);
    }

    private static VampireAbilitiesDecision CompleteCheck(VampireCharacter draft)
    {
        return VampireAbilitiesDecision.Ok();
    }
}
