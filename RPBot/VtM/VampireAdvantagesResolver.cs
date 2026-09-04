using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RPBot.VtM;

/// <summary>
/// Бизнес-логика Шага 4 визарда VtM V20 «Преимущества».
/// </summary>
/// <remarks>
/// <para>Три секции, идущие одна за другой в визарде:</para>
/// <list type="number">
///   <item>4.1 <b>Дисциплины</b> — 3 пункта между клановыми (для Каитифа — свободный ввод).</item>
///   <item>4.2 <b>Факты биографии</b> — 5 пунктов суммарно по рангам 1..5.</item>
///   <item>4.3 <b>Добродетели</b> — 7 пунктов сверх базы 1/1/1 (V20 стр. 100).</item>
/// </list>
/// <para>Все три кэпа одинаковые — 5 на одно поле.</para>
/// </remarks>
public static class VampireAdvantagesResolver
{
    public enum Failure
    {
        None,
        UnknownField,
        AboveCap,
        BelowMin,
        PoolExhausted,
        BackgroundAlreadyExists,
        BackgroundNotFound,
        InvalidName,
        CannotRename,
    }

    public readonly struct Decision
    {
        public bool IsSuccess { get; }
        public Failure Failure { get; }
        public string Message { get; }

        private Decision(bool ok, Failure f, string msg)
        {
            IsSuccess = ok; Failure = f; Message = msg;
        }

        public static Decision Ok(string msg = "OK") => new Decision(true, Failure.None, msg);
        public static Decision Fail(Failure f, string msg) => new Decision(false, f, msg);
    }

    // ═══ Дисциплины ═══════════════════════════════════════════════════

    /// <summary>
    /// Допустимые имена для операций (Increment/Decrement/Set/Rename).
    /// Для обычных кланов — только клановые имена из каталога.
    /// Для Каитифа — каталожные слоты плюс любые ключи, уже введённые игроком
    /// через <see cref="RenameCaitiffDiscipline"/>.
    /// </summary>
    private static IReadOnlyList<string> Slots(VampireCharacter draft)
        => EffectiveDisciplineSlots(draft);

    /// <summary>
    /// Публичный аналог <see cref="Slots"/> — для UI и внешних вызовов.
    /// </summary>
    public static IReadOnlyList<string> EffectiveDisciplineSlots(VampireCharacter draft)
    {
        var catalog = VampireAdvantagesCatalog.GetDisciplineSlots(draft?.Clan);
        if (!VampireAdvantagesCatalog.IsCaitiff(draft?.Clan))
            return catalog;

        // Каитиф: объединяем каталожные слоты и любые уже введённые имена.
        var seen = new HashSet<string>(catalog, StringComparer.Ordinal);
        var combined = new List<string>(catalog);
        if (draft?.Disciplines != null)
        {
            foreach (var key in draft.Disciplines.Keys)
            {
                if (seen.Add(key)) combined.Add(key);
            }
        }
        return combined;
    }

    /// <summary>Получить значение дисциплины (Step 4.1 + freebie; 0 если не задано).</summary>
    public static int GetDisciplineValue(VampireCharacter draft, string name)
    {
        if (draft == null || string.IsNullOrEmpty(name)) return 0;
        int step = draft.Disciplines != null && draft.Disciplines.TryGetValue(name, out var v) ? Math.Max(0, v) : 0;
        int free = draft.FreebieDisciplines != null && draft.FreebieDisciplines.TryGetValue(name, out var fv) ? Math.Max(0, fv) : 0;
        return Math.Max(0, step + free);
    }

    /// <summary>Значение дисциплины только за Шаг 4.1 (без freebie).</summary>
    public static int GetStepDisciplineValue(VampireCharacter draft, string name)
    {
        if (draft?.Disciplines == null || string.IsNullOrEmpty(name)) return 0;
        return draft.Disciplines.TryGetValue(name, out var v) ? Math.Max(0, v) : 0;
    }

    private static void ApplyDisciplineValue(VampireCharacter draft, string name, int value)
        {
            if (draft.Disciplines == null) draft.Disciplines = new Dictionary<string, int>(StringComparer.Ordinal);
            draft.Disciplines[name] = Math.Max(0, value);
        }

    /// <summary>Потрачено очков на дисциплины.</summary>
    public static int TotalDisciplineSpent(VampireCharacter draft)
    {
        if (draft?.Disciplines == null) return 0;
        return draft.Disciplines.Values.Sum(v => Math.Max(0, v));
    }

    /// <summary>Остаток пула дисциплин.</summary>
    public static int RemainingDisciplinePool(VampireCharacter draft)
        => Math.Max(0, VampireAdvantagesCatalog.DisciplinePool - TotalDisciplineSpent(draft));

    /// <summary>Завершён ли Шаг 4.1.</summary>
    public static bool IsDisciplinesComplete(VampireCharacter draft)
        => TotalDisciplineSpent(draft) == VampireAdvantagesCatalog.DisciplinePool;

    /// <summary>Поднять дисциплину на 1 (только Шаг 4.1; freebie идёт в FreebieDisciplines).</summary>
    public static Decision IncrementDiscipline(VampireCharacter draft, string name)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (string.IsNullOrWhiteSpace(name))
            return Decision.Fail(Failure.InvalidName, "Имя дисциплины не указано.");
        var slots = Slots(draft);
        if (slots.Count > 0 && !slots.Contains(name))
            return Decision.Fail(Failure.UnknownField,
                $"«{name}» нет среди клановых дисциплин «{draft.Clan}». " +
                $"Доступные: {string.Join(", ", slots)}.");

        var current = GetStepDisciplineValue(draft, name);
        if (current + 1 > VampireAdvantagesCatalog.PerFieldCap)
            return Decision.Fail(Failure.AboveCap,
                $"«{name}» уже на кэпе {VampireAdvantagesCatalog.PerFieldCap}.");
        if (TotalDisciplineSpent(draft) + 1 > VampireAdvantagesCatalog.DisciplinePool)
            return Decision.Fail(Failure.PoolExhausted,
                $"Пул дисциплин ({VampireAdvantagesCatalog.DisciplinePool}) уже исчерпан.");

        ApplyDisciplineValue(draft, name, current + 1);
        return Decision.Ok($"«{name}» → {current + 1}.");
    }

    /// <summary>Опустить дисциплину на 1 (не ниже 0).</summary>
    public static Decision DecrementDiscipline(VampireCharacter draft, string name)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (string.IsNullOrWhiteSpace(name))
            return Decision.Fail(Failure.InvalidName, "Имя дисциплины не указано.");
        var current = GetStepDisciplineValue(draft, name);
        if (current <= VampireAdvantagesCatalog.MinDiscipline)
            return Decision.Fail(Failure.BelowMin,
                $"«{name}» уже на минимуме {VampireAdvantagesCatalog.MinDiscipline}.");
        ApplyDisciplineValue(draft, name, current - 1);
        if (current - 1 == 0) draft.Disciplines.Remove(name);
        return Decision.Ok($"«{name}» → {current - 1}.");
    }

    /// <summary>Установить точное значение (для текстового ввода).</summary>
    public static Decision SetDisciplineValue(VampireCharacter draft, string name, int value)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (string.IsNullOrWhiteSpace(name))
            return Decision.Fail(Failure.InvalidName, "Имя дисциплины не указано.");
        if (value < 0)
            return Decision.Fail(Failure.BelowMin, "Значение дисциплины не может быть меньше 0.");
        if (value > VampireAdvantagesCatalog.PerFieldCap)
            return Decision.Fail(Failure.AboveCap,
                $"Кэп дисциплины: {VampireAdvantagesCatalog.PerFieldCap}.");

        var slots = Slots(draft);
        if (slots.Count > 0 && !slots.Contains(name))
            return Decision.Fail(Failure.UnknownField,
                $"«{name}» нет среди клановых дисциплин «{draft.Clan}».");

        var current = GetDisciplineValue(draft, name);
        var delta = value - current;
        if (delta > 0 && TotalDisciplineSpent(draft) + delta > VampireAdvantagesCatalog.DisciplinePool)
            return Decision.Fail(Failure.PoolExhausted,
                $"Пул дисциплин ({VampireAdvantagesCatalog.DisciplinePool}) уже исчерпан.");

        if (value == 0) draft.Disciplines.Remove(name);
        else ApplyDisciplineValue(draft, name, value);
        return Decision.Ok($"«{name}» → {value}.");
    }

    /// <summary>Установить имя Каитиф-дисциплины (например, "Анимализм").</summary>
    /// <remarks>
    /// Работает и на пустом слоте (значение 0): если ключа <paramref name="oldName"/>
    /// ещё нет в <c>draft.Disciplines</c>, он создаётся с 0 и сразу переименовывается.
    /// Это позволяет Каитифу сначала назвать дисциплины, и только потом вкладывать очки.
    /// </remarks>
    public static Decision RenameCaitiffDiscipline(VampireCharacter draft, string oldName, string newName)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (!VampireAdvantagesCatalog.IsCaitiff(draft.Clan))
            return Decision.Fail(Failure.CannotRename,
                "Переименовывать можно только дисциплины Каитифа.");
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
            return Decision.Fail(Failure.InvalidName, "Имя дисциплины не указано.");
        var trimmedNew = newName.Trim();
        if (string.IsNullOrWhiteSpace(trimmedNew))
            return Decision.Fail(Failure.InvalidName, "Новое имя дисциплины пустое.");

        // Имя должно быть в списке доступных Каитифу слотов (или совпадать с уже существующим).
        var slots = Slots(draft);
        bool isKnownEmptySlot = slots.Contains(oldName) && !draft.Disciplines.ContainsKey(oldName);
        if (!draft.Disciplines.ContainsKey(oldName) && !isKnownEmptySlot)
            return Decision.Fail(Failure.UnknownField,
                $"«{oldName}» не найдено среди дисциплин.");

        // Не даём переименовать в имя, которое уже занято другой дисциплиной.
        if (draft.Disciplines.ContainsKey(trimmedNew) && trimmedNew != oldName)
            return Decision.Fail(Failure.BackgroundAlreadyExists,
                $"Дисциплина «{trimmedNew}» уже есть.");

        // Пустой слот: создаём запись с 0 и сразу переименовываем.
        if (!draft.Disciplines.TryGetValue(oldName, out var value))
        {
            draft.Disciplines[trimmedNew] = 0;
            return Decision.Ok($"Слот «{oldName}» назван «{trimmedNew}» (значение 0).");
        }

        // Слот с ненулевым значением: переименовываем, сохраняя значение.
        draft.Disciplines.Remove(oldName);
        draft.Disciplines[trimmedNew] = value;
        return Decision.Ok($"«{oldName}» → «{trimmedNew}» (сохранено значение {value}).");
    }

    // ═══ Факты биографии ══════════════════════════════════════════════

    /// <summary>Получить ранг факта (Шаг 4.2 + freebie; 0 если не задан).</summary>
    public static int GetBackgroundRank(VampireCharacter draft, string name)
    {
        if (draft == null || string.IsNullOrEmpty(name)) return 0;
        int step = draft.Backgrounds != null && draft.Backgrounds.TryGetValue(name, out var v) ? Math.Max(0, v) : 0;
        int free = draft.FreebieBackgrounds != null && draft.FreebieBackgrounds.TryGetValue(name, out var fv) ? Math.Max(0, fv) : 0;
        return Math.Max(0, step + free);
    }

    /// <summary>Значение факта только за Шаг 4.2 (без freebie).</summary>
    public static int GetStepBackgroundRank(VampireCharacter draft, string name)
    {
        if (draft?.Backgrounds == null || string.IsNullOrEmpty(name)) return 0;
        return draft.Backgrounds.TryGetValue(name, out var v) ? Math.Max(0, v) : 0;
    }

    /// <summary>Потрачено очков на факты.</summary>
    public static int TotalBackgroundSpent(VampireCharacter draft)
    {
        if (draft?.Backgrounds == null) return 0;
        return draft.Backgrounds.Values.Sum(v => Math.Max(0, v));
    }

    /// <summary>Остаток пула фактов.</summary>
    public static int RemainingBackgroundPool(VampireCharacter draft)
        => Math.Max(0, VampireAdvantagesCatalog.BackgroundPool - TotalBackgroundSpent(draft));

    /// <summary>Завершён ли Шаг 4.2.</summary>
    public static bool IsBackgroundsComplete(VampireCharacter draft)
        => TotalBackgroundSpent(draft) == VampireAdvantagesCatalog.BackgroundPool;

    /// <summary>Добавить факт с рангом 1.</summary>
    public static Decision AddBackground(VampireCharacter draft, string name)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (string.IsNullOrWhiteSpace(name))
            return Decision.Fail(Failure.InvalidName, "Имя факта не указано.");
        var trimmed = name.Trim();
        if (draft.Backgrounds == null) draft.Backgrounds = new Dictionary<string, int>(StringComparer.Ordinal);
        if (draft.Backgrounds.ContainsKey(trimmed))
            return Decision.Fail(Failure.BackgroundAlreadyExists,
                $"Факт «{trimmed}» уже есть.");
        if (TotalBackgroundSpent(draft) + 1 > VampireAdvantagesCatalog.BackgroundPool)
            return Decision.Fail(Failure.PoolExhausted,
                $"Пул фактов ({VampireAdvantagesCatalog.BackgroundPool}) уже исчерпан.");

        draft.Backgrounds[trimmed] = 1;
        return Decision.Ok($"Факт «{trimmed}» добавлен (ранг 1).");
    }

    /// <summary>Удалить факт.</summary>
    public static Decision RemoveBackground(VampireCharacter draft, string name)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (string.IsNullOrWhiteSpace(name))
            return Decision.Fail(Failure.InvalidName, "Имя факта не указано.");
        var trimmed = name.Trim();
        if (draft.Backgrounds == null || !draft.Backgrounds.Remove(trimmed))
            return Decision.Fail(Failure.BackgroundNotFound, $"Факт «{trimmed}» не найден.");
        return Decision.Ok($"Факт «{trimmed}» удалён.");
    }

    /// <summary>Переименовать факт.</summary>
    public static Decision RenameBackground(VampireCharacter draft, string oldName, string newName)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
            return Decision.Fail(Failure.InvalidName, "Имя факта не указано.");
        if (draft.Backgrounds == null || !draft.Backgrounds.TryGetValue(oldName.Trim(), out var rank))
            return Decision.Fail(Failure.BackgroundNotFound, $"Факт «{oldName}» не найден.");
        var newTrim = newName.Trim();
        if (draft.Backgrounds.ContainsKey(newTrim) && newTrim != oldName.Trim())
            return Decision.Fail(Failure.BackgroundAlreadyExists,
                $"Факт «{newTrim}» уже есть.");
        draft.Backgrounds.Remove(oldName.Trim());
        draft.Backgrounds[newTrim] = rank;
        return Decision.Ok($"«{oldName}» → «{newTrim}».");
    }

    /// <summary>Поднять ранг факта на 1.</summary>
    public static Decision IncrementBackground(VampireCharacter draft, string name)
  {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (string.IsNullOrWhiteSpace(name))
            return Decision.Fail(Failure.InvalidName, "Имя факта не указано.");
        var trimmed = name.Trim();
        if (draft.Backgrounds == null || !draft.Backgrounds.TryGetValue(trimmed, out var rank))
            return Decision.Fail(Failure.BackgroundNotFound, $"Факт «{trimmed}» не найден.");
        if (rank + 1 > VampireAdvantagesCatalog.PerFieldCap)
            return Decision.Fail(Failure.AboveCap,
                $"Факт «{trimmed}» уже на кэпе {VampireAdvantagesCatalog.PerFieldCap}.");
        if (TotalBackgroundSpent(draft) + 1 > VampireAdvantagesCatalog.BackgroundPool)
            return Decision.Fail(Failure.PoolExhausted,
                $"Пул фактов ({VampireAdvantagesCatalog.BackgroundPool}) уже исчерпан.");

        draft.Backgrounds[trimmed] = rank + 1;
        return Decision.Ok($"Факт «{trimmed}» → ранг {rank + 1}.");
    }

    /// <summary>Опустить ранг факта на 1 (не ниже 1).</summary>
    public static Decision DecrementBackground(VampireCharacter draft, string name)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (string.IsNullOrWhiteSpace(name))
            return Decision.Fail(Failure.InvalidName, "Имя факта не указано.");
        var trimmed = name.Trim();
        if (draft.Backgrounds == null || !draft.Backgrounds.TryGetValue(trimmed, out var rank))
            return Decision.Fail(Failure.BackgroundNotFound, $"Факт «{trimmed}» не найден.");
        if (rank <= 1)
            return Decision.Fail(Failure.BelowMin,
                "Удалить факт с рангом 1 можно только через «Удалить».");
        draft.Backgrounds[trimmed] = rank - 1;
        return Decision.Ok($"Факт «{trimmed}» → ранг {rank - 1}.");
    }

    // ═══ Добродетели ════════════════════════════════════════════════

    /// <summary>Заполнить базу 1/1/1 если ещё нет ни одной добродетели.</summary>
    public static Decision SeedVirtues(VampireCharacter draft)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (draft.Virtues == null) draft.Virtues = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!draft.Virtues.ContainsKey(VampireParameterCatalog.VirtueConscience))
            draft.Virtues[VampireParameterCatalog.VirtueConscience] = VampireAdvantagesCatalog.VirtueBaseConscience;
        if (!draft.Virtues.ContainsKey(VampireParameterCatalog.VirtueSelfControl))
            draft.Virtues[VampireParameterCatalog.VirtueSelfControl] = VampireAdvantagesCatalog.VirtueBaseSelfControl;
        if (!draft.Virtues.ContainsKey(VampireParameterCatalog.VirtueCourage))
            draft.Virtues[VampireParameterCatalog.VirtueCourage] = VampireAdvantagesCatalog.VirtueBaseCourage;
        return Decision.Ok("База доброделей 1/1/1 установлена.");
    }

    /// <summary>Получить значение добродетели (с авто-посевом базы 1).</summary>
    public static int GetVirtueValue(VampireCharacter draft, string name)
    {
        if (draft == null || string.IsNullOrEmpty(name)) return 0;
        if (draft.Virtues == null || !draft.Virtues.TryGetValue(name, out var v))
            return VampireAdvantagesCatalog.MinVirtue;
        return Math.Max(VampireAdvantagesCatalog.MinVirtue, v);
    }

    /// <summary>Потрачено очков сверх базы (1/1/1) — то, что игрок добавил из пула.</summary>
    public static int TotalVirtueSpent(VampireCharacter draft)
    {
        int sum = 0;
        foreach (var name in VampireParameterCatalog.Virtues)
            sum += Math.Max(0, GetVirtueValue(draft, name) - 1);
        return sum;
    }

    /// <summary>Остаток пула добродетелей.</summary>
    public static int RemainingVirtuePool(VampireCharacter draft)
        => Math.Max(0, VampireAdvantagesCatalog.VirtuePool - TotalVirtueSpent(draft));

    /// <summary>Завершён ли Шаг 4.3.</summary>
    public static bool IsVirtuesComplete(VampireCharacter draft)
        => TotalVirtueSpent(draft) == VampireAdvantagesCatalog.VirtuePool;

    /// <summary>Поднять добродетель на 1.</summary>
    public static Decision IncrementVirtue(VampireCharacter draft, string name)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (!VampireParameterCatalog.Virtues.Contains(name))
            return Decision.Fail(Failure.UnknownField,
                $"«{name}» — не базовая добродетель VtM V20. " +
                $"Допустимо: {string.Join(", ", VampireParameterCatalog.Virtues)}.");
        SeedVirtues(draft);
        var current = draft.Virtues[name];
        if (current + 1 > VampireAdvantagesCatalog.PerFieldCap)
            return Decision.Fail(Failure.AboveCap,
                $"Добродетель «{name}» уже на кэпе {VampireAdvantagesCatalog.PerFieldCap}.");
        if (TotalVirtueSpent(draft) + 1 > VampireAdvantagesCatalog.VirtuePool)
            return Decision.Fail(Failure.PoolExhausted,
                $"Пул добродетелей ({VampireAdvantagesCatalog.VirtuePool}) уже исчерпан.");

        draft.Virtues[name] = current + 1;
        return Decision.Ok($"«{name}» → {current + 1}.");
    }

    /// <summary>Опустить добродетель на 1 (не ниже 1 — база).</summary>
    public static Decision DecrementVirtue(VampireCharacter draft, string name)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (!VampireParameterCatalog.Virtues.Contains(name))
            return Decision.Fail(Failure.UnknownField, $"«{name}» — не базовая добродетель VtM V20.");
        SeedVirtues(draft);
        var current = draft.Virtues[name];
        if (current <= VampireAdvantagesCatalog.MinVirtue)
            return Decision.Fail(Failure.BelowMin,
                $"«{name}» уже на базе {VampireAdvantagesCatalog.MinVirtue}.");
        draft.Virtues[name] = current - 1;
        return Decision.Ok($"«{name}» → {current - 1}.");
    }

    // ═══ Сброс ════════════════════════════════════════════════════════

    /// <summary>Сбросить прогресс Шага 4 (клан и Generation сохраняются).</summary>
    public static Decision ResetProgress(VampireCharacter draft)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        draft.Disciplines = new Dictionary<string, int>(StringComparer.Ordinal);
        draft.Backgrounds = new Dictionary<string, int>(StringComparer.Ordinal);
        SeedVirtues(draft);
        return Decision.Ok("Прогресс Шага 4 сброшен.");
    }

    /// <summary>Сбросить вообще всё (для команды «Сбросить всё» — возврат к Шагу 4).</summary>
    public static Decision ResetAll(VampireCharacter draft)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        ResetProgress(draft);
        return Decision.Ok("Преимущества сброшены.");
    }

    // ═══ Статус-сообщения ════════════════════════════════════════════

    /// <summary>Шаг 4.1 — Дисциплины.</summary>
    public static string BuildDisciplinesStatusMessage(VampireCharacter draft)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Шаг 4.1 — Дисциплины** (распределите " +
                      $"{VampireAdvantagesCatalog.DisciplinePool} пункта преимуществ; кэп {VampireAdvantagesCatalog.PerFieldCap}):");
        if (draft == null || string.IsNullOrEmpty(draft.Clan))
        {
            sb.AppendLine("_Сначала выберите клан на Шаге 1._");
            return sb.ToString();
        }

        var slots = Slots(draft);
        sb.AppendLine($"Клан: **{draft.Clan}** {(VampireAdvantagesCatalog.IsCaitiff(draft.Clan) ? "(Каитиф — свободный ввод)" : "")}");
        // Для Каитифа показываем реально существующие ключи в draft.Disciplines.
        // Если ни один слот ещё не использован — рисуем три пустые заглушки.
        // Переименованный слот (например, "Дисциплина 1" → "Анимализм") отображается
        // только под новым именем — без дублирования старого.
        if (VampireAdvantagesCatalog.IsCaitiff(draft.Clan))
        {
            if (draft.Disciplines != null && draft.Disciplines.Count > 0)
            {
                foreach (var kv in draft.Disciplines)
                {
                    sb.Append("`").Append(PadRight(kv.Key, NameColumnWidth))
                      .Append("` ").AppendLine(Dots(kv.Value, VampireAdvantagesCatalog.PerFieldCap));
                }
            }
            else
            {
                foreach (var s in slots)
                {
                    sb.Append("`").Append(PadRight(s, NameColumnWidth))
                      .Append("` ").AppendLine(Dots(0, VampireAdvantagesCatalog.PerFieldCap));
                }
            }
        }
        else
        {
            foreach (var s in slots)
            {
                int v = GetDisciplineValue(draft, s);
                sb.Append("`").Append(PadRight(s, NameColumnWidth))
                  .Append("` ").AppendLine(Dots(v, VampireAdvantagesCatalog.PerFieldCap));
            }
        }
        sb.Append("Распределено: **").Append(TotalDisciplineSpent(draft))
          .Append("** / **").Append(VampireAdvantagesCatalog.DisciplinePool).AppendLine("**");
        return sb.ToString();
    }

    /// <summary>Шаг 4.2 — Факты биографии.</summary>
    public static string BuildBackgroundsStatusMessage(VampireCharacter draft)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Шаг 4.2 — Факты биографии** " +
                      $"(распределите {VampireAdvantagesCatalog.BackgroundPool} пунктов; кэп {VampireAdvantagesCatalog.PerFieldCap}):");
        if (draft?.Backgrounds != null && draft.Backgrounds.Count > 0)
        {
            foreach (var kv in draft.Backgrounds)
            {
                int rank = Math.Max(1, kv.Value);
                sb.Append("• ").Append(kv.Key).Append(' ').Append(Dots(rank, VampireAdvantagesCatalog.PerFieldCap)).AppendLine();
            }
        }
        else
        {
            sb.AppendLine("`—`");
        }
        sb.Append("Распределено: **").Append(TotalBackgroundSpent(draft))
          .Append("** / **").Append(VampireAdvantagesCatalog.BackgroundPool).AppendLine("**");
        return sb.ToString();
    }

    /// <summary>Шаг 4.3 — Добродетели.</summary>
    public static string BuildVirtuesStatusMessage(VampireCharacter draft)
    {
        SeedVirtues(draft);
        var sb = new StringBuilder();
        sb.AppendLine($"**Шаг 4.3 — Добродетели** " +
                      $"(база 1/1/1, пул {VampireAdvantagesCatalog.VirtuePool} сверху, кэп {VampireAdvantagesCatalog.PerFieldCap}):");
        foreach (var v in VampireParameterCatalog.Virtues)
        {
            int value = GetVirtueValue(draft, v);
            sb.Append("`").Append(PadRight(v, NameColumnWidth)).Append("` ").AppendLine(Dots(value, VampireAdvantagesCatalog.PerFieldCap));
        }
        sb.Append("Распределено сверху: **").Append(TotalVirtueSpent(draft))
          .Append("** / **").Append(VampireAdvantagesCatalog.VirtuePool).AppendLine("**");
        sb.AppendLine($"Человечность = Совесть + Самоконтроль = " +
                      $"{GetVirtueValue(draft, VampireParameterCatalog.VirtueConscience) + GetVirtueValue(draft, VampireParameterCatalog.VirtueSelfControl)}");
        sb.Append("Воля = Смелость = **").Append(GetVirtueValue(draft, VampireParameterCatalog.VirtueCourage)).AppendLine("**");
        return sb.ToString();
    }

    // ═══ Внутренние хелперы ══════════════════════════════════════════

    /// <summary>
    /// Ширина столбца имён в статус-сообщениях (в символах). 15 — достаточно для
    /// «Стремительность» (15 кириллических символов) без обрезки.
    /// </summary>
    private const int NameColumnWidth = 15;

    private static string PadRight(string s, int total)
    {
        if (s == null) return new string(' ', total);
        // Поддержка UTF-8 по визуальной ширине — НЕ идеальна без пересчёта глифов,
        // но для 1-столбцовой таблицы в Discord достаточно.
        if (s.Length >= total) return s.Substring(0, total);
        return s + new string(' ', total - s.Length);
    }

    private static string Dots(int filled, int total)
    {
        filled = Math.Clamp(filled, 0, total);
        return new string('●', filled) + new string('○', total - filled);
    }
}
