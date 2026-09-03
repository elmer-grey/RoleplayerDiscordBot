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

    private static string TruncateLabel(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        return s.Substring(0, max - 1) + "…";
    }

    public static string BuildCustomId(VampireWizardAction action, Guid characterId)
        => $"{Prefix}:{ActionToString(action)}:{characterId:N}";

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
            default:               action = default;                         return false;
        }
    }
}
