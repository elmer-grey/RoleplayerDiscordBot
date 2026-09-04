using System.Text;

namespace RPBot.VtM;

/// <summary>
/// Сборка статус-сообщения Шага 5 для DM-рендера.
/// </summary>
public static partial class VampireFinishingResolver
{
    /// <summary>Полный текстовый статус Шага 5 для DM-рендера.</summary>
    public static string BuildStatusMessage(VampireCharacter draft)
    {
        if (draft == null) draft = new VampireCharacter();
        var sb = new StringBuilder();
        sb.AppendLine("**Шаг 5 — последние штрихи**");
        sb.AppendLine("Пул 15 свободных пунктов по ценам V20 (хар 5 / спос 2 / диск 7 / факт 1 / доброд 2 / Чел 2 / Воля 1).");
        sb.AppendLine();
        sb.Append("Человечность: **").Append(ComputeHumanity(draft)).AppendLine("**");
        sb.Append("Воля:         **").Append(ComputeWillpower(draft)).AppendLine("**");
        // Голод в V20 стартует с 1; на Шаге 5 не редактируется — только отображается.
        sb.Append("Голод:        ").Append(draft.Hunger).AppendLine(" (стартовое значение, в Шаге 5 не редактируется)");
        sb.Append("Слабость:     ").AppendLine(
            string.IsNullOrEmpty(draft.Weakness)
                ? "(пусто — задаётся автоматически кланом)"
                : draft.Weakness);

        EnsureHealth(draft);
        if (draft.Health != null)
        {
            var h = draft.Health;
            var penalty = h.TablePenalty;
            var penaltyText = h.IsIncapacitated
                ? "небоеспособен"
                : (penalty.HasValue ? penalty.Value.ToString("+0;-0;0") : "0");
            sb.Append("Здоровье:     ").Append(h.Render())
              .Append("   (V20, 7 ячеек, штраф: ").Append(penaltyText).AppendLine(")");
        }

        sb.AppendLine();
        var consumed = ConsumedFreebies(draft);
        var remaining = FreebiePool - consumed;
        sb.Append("**Свободные пункты: осталось ").Append(remaining)
          .Append(" / ").Append(FreebiePool)
          .Append("** (потрачено ").Append(consumed).Append(").");
        var spent = DescribeSpent(draft);
        if (spent.Count == 0)
        {
            sb.AppendLine(" Пока ничего не потрачено.");
        }
        else
        {
            sb.AppendLine();
            foreach (var line in spent) sb.Append("  • ").AppendLine(line);
        }

        return sb.ToString();
    }
}
