using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM;

/// <summary>
/// Результат проверки ввода на шаге 1 (концепция) визарда создания персонажа.
/// </summary>
public enum VampireCreateConceptFailure
{
    None,
    ConceptRequired,
    ClanRequired,
    ClanInvalid,
    NatureRequired,
    DemeanorRequired,
    UnknownField,
}

/// <summary>
/// Решение о состоянии шага 1 «Концепция» визарда.
///
/// <para>Используется в <c>VampireWizardDmHandler</c> для обновления сообщения DM.
/// Чистая логика — без побочных эффектов; легко тестируется.</para>
/// </summary>
/// <remarks>
/// <para>Шаг 1 — концепция персонажа по VtM V20 (книга, стр. 81):
/// амплуа (Concept), клан, натура (Nature), маска (Demeanor). Все поля задаются,
/// кроме «описания персонажа» (свободное Bio) — оно может быть пропущено.</para>
/// </remarks>
public sealed record VampireCreateConceptDecision(
    VampireCreateConceptFailure Failure,
    string Message,
    VampireCharacter Draft,
    bool ConceptComplete)
{
    public bool IsSuccess => Failure == VampireCreateConceptFailure.None && ConceptComplete;
}

/// <summary>
/// Резолвер шага 1 визарда (концепция персонажа). Чистая логика — без I/O.
/// </summary>
public static class VampireCreateResolver
{
    /// <summary>
    /// Применить ввод пользователя к черновику на шаге 1 «Концепция».
    /// </summary>
    /// <param name="draft">Текущий черновик (мутируется ссылочно, но в тестах мы передаём копию).</param>
    /// <param name="field">Поле, которое обновляет пользователь: "concept"/"clan"/"nature"/"demeanor"/"bio".</param>
    /// <param name="value">Новое значение (для клана — название из <see cref="VampireParameterCatalog.Clans"/>).</param>
    /// <returns>Решение с результатом валидации и обновлённым черновиком.</returns>
    public static VampireCreateConceptDecision ApplyConceptField(
        VampireCharacter draft,
        string field,
        string? value)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        field = (field ?? "").ToLowerInvariant().Trim();
        value = (value ?? "").Trim();

        switch (field)
        {
            case "concept":
                if (string.IsNullOrEmpty(value))
                    return Fail(draft, VampireCreateConceptFailure.ConceptRequired,
                        "Амплуа не может быть пустым.");
                draft.Concept = value;
                break;

            case "clan":
                if (string.IsNullOrEmpty(value))
                    return Fail(draft, VampireCreateConceptFailure.ClanRequired,
                        "Укажите клан (см. /vampire — там подсказка со списком).");
                if (!VampireParameterCatalog.IsValidClan(value))
                    return Fail(draft, VampireCreateConceptFailure.ClanInvalid,
                        $"«{value}» — не валидный клан. Допустимые: {string.Join(", ", VampireParameterCatalog.Clans)}.");
                draft.Clan = value;
                // Клановый изъян сохраняем в Weakness сразу — чтобы не вводить отдельно.
                draft.Weakness = VampireParameterCatalog.GetClanFlawShort(value);
                break;

            case "nature":
                if (string.IsNullOrEmpty(value))
                    return Fail(draft, VampireCreateConceptFailure.NatureRequired,
                        "Натура не может быть пустой.");
                draft.Nature = value;
                break;

            case "demeanor":
                if (string.IsNullOrEmpty(value))
                    return Fail(draft, VampireCreateConceptFailure.DemeanorRequired,
                        "Маска не может быть пустой.");
                draft.Demeanor = value;
                break;

            case "bio":
                // Bio можно пропустить (пустая строка = «без описания»).
                draft.Bio = value;
                break;

            default:
                return Fail(draft, VampireCreateConceptFailure.UnknownField,
                    $"Неизвестное поле «{field}». Допустимые: concept, clan, nature, demeanor, bio.");
        }

        return CompleteCheck(draft);
    }

    /// <summary>
    /// Очистить поле (для команды «отмена ввода»). Полезно в UI визарда.
    /// </summary>
    public static VampireCreateConceptDecision ClearConceptField(
        VampireCharacter draft,
        string field)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        field = (field ?? "").ToLowerInvariant().Trim();
        switch (field)
        {
            case "concept": draft.Concept = ""; break;
            case "clan":    draft.Clan = ""; draft.Weakness = ""; break;
            case "nature":  draft.Nature = ""; break;
            case "demeanor": draft.Demeanor = ""; break;
            case "bio":     draft.Bio = ""; break;
            default:
                return Fail(draft, VampireCreateConceptFailure.UnknownField,
                    $"Неизвестное поле «{field}».");
        }
        return CompleteCheck(draft);
    }

    /// <summary>Проверить, что шаг 1 завершён (все 4 обязательных поля заданы).</summary>
    public static bool IsConceptComplete(VampireCharacter draft)
    {
        if (draft == null) return false;
        return !string.IsNullOrWhiteSpace(draft.Concept)
            && !string.IsNullOrWhiteSpace(draft.Clan)
            && !string.IsNullOrWhiteSpace(draft.Nature)
            && !string.IsNullOrWhiteSpace(draft.Demeanor);
    }

    /// <summary>
    /// Текст текущего состояния шага 1 для вывода в DM.
    /// Показывает, какие поля уже заданы, а какие требуются.
    /// </summary>
    public static string BuildConceptStatusMessage(VampireCharacter draft)
    {
        if (draft == null) throw new System.ArgumentNullException(nameof(draft));
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("**Шаг 1 — Концепция персонажа**");
        sb.AppendLine();
        sb.AppendLine("Заполните 4 обязательных поля (амплуа, клан, натура, маска). Описание — опционально.");
        sb.AppendLine();

        AppendFieldStatus(sb, "Амплуа (concept)", draft.Concept);
        AppendFieldStatus(sb, "Клан (clan)",     draft.Clan);
        AppendFieldStatus(sb, "Натура (nature)",  draft.Nature);
        AppendFieldStatus(sb, "Маска (demeanor)", draft.Demeanor);
        AppendFieldStatus(sb, "Описание (bio)",   draft.Bio, optional: true);

        if (!string.IsNullOrEmpty(draft.Clan))
        {
            var discs = VampireParameterCatalog.GetClanDisciplines(draft.Clan);
            if (discs.Count > 0)
                sb.AppendLine($"\n**Клановые дисциплины {draft.Clan}:** {string.Join(", ", discs)}");
        }

        sb.AppendLine();
        sb.AppendLine(IsConceptComplete(draft)
            ? "✅ Все обязательные поля заполнены. Нажмите «Далее», чтобы перейти к шагу 2 (характеристики)."
            : "⏳ Заполните оставшиеся поля. Когда все 4 обязательных поля будут готовы, появится кнопка «Далее».");
        return sb.ToString();
    }

    /// <summary>Список подсказок для каждого клана (для UI выбора).</summary>
    public static IReadOnlyList<string> ClanSuggestions()
        => VampireParameterCatalog.Clans;

    // ── Внутренние хелперы ───────────────────────────────────────────────

    private static void AppendFieldStatus(
        System.Text.StringBuilder sb,
        string label,
        string value,
        bool optional = false)
    {
        var mark = optional ? "○" : "•";
        if (string.IsNullOrWhiteSpace(value))
        {
            sb.AppendLine($"{mark} {label}: _не задано_ {(optional ? "(опционально)" : "")}");
        }
        else
        {
            sb.AppendLine($"{mark} {label}: **{value}**");
        }
    }

    private static VampireCreateConceptDecision Fail(
        VampireCharacter draft,
        VampireCreateConceptFailure code,
        string message)
        => new(code, message, draft, IsConceptComplete(draft));

    private static VampireCreateConceptDecision CompleteCheck(VampireCharacter draft)
        => new(
            VampireCreateConceptFailure.None,
            IsConceptComplete(draft) ? "Поля обновлены. Все обязательные заполнены." : "Поля обновлены.",
            draft,
            IsConceptComplete(draft));
}
