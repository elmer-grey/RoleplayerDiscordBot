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
        var effective = EffectiveFreebiePool(draft);
        var remaining = Math.Max(0, effective - consumed);
        var flawsCost = VampireMeritsFlawsResolver.FlawsCost(draft);
        var meritsCost = VampireMeritsFlawsResolver.MeritsCost(draft);
        sb.Append("**Свободные пункты: осталось ").Append(remaining)
          .Append(" / ").Append(effective)
          .Append("** (потрачено ").Append(consumed).Append(")");
        if (flawsCost > 0 || meritsCost > 0)
        {
            sb.Append(" [база ").Append(BaseFreebiePool);
            if (flawsCost > 0) sb.Append(" +Flaws ").Append(flawsCost);
            if (meritsCost > 0) sb.Append(" −Merits ").Append(meritsCost);
            sb.Append("]");
        }
        sb.AppendLine(".");
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
        if (VampireMeritsFlawsResolver.MeritsCount(draft) > 0 ||
            VampireMeritsFlawsResolver.FlawsCount(draft) > 0)
        {
            sb.Append("Учтено: ");
            if (VampireMeritsFlawsResolver.MeritsCount(draft) > 0)
                sb.Append("Merits×").Append(VampireMeritsFlawsResolver.MeritsCount(draft));
            if (VampireMeritsFlawsResolver.MeritsCount(draft) > 0 &&
                VampireMeritsFlawsResolver.FlawsCount(draft) > 0) sb.Append(", ");
            if (VampireMeritsFlawsResolver.FlawsCount(draft) > 0)
                sb.Append("Flaws×").Append(VampireMeritsFlawsResolver.FlawsCount(draft));
            sb.AppendLine(".");
        }

        return sb.ToString();
    }
}
