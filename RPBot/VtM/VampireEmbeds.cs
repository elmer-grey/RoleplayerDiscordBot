using Discord;
using RPBot.VtM;

namespace RPBot;

/// <summary>
/// Общие конструкторы embed-ов для VtM (проверка совести, трата воли и т. п.).
/// Вынесены из <see cref="VampireCommands"/> для повторного использования
/// дочерними хендлерами и unit-тестами.
/// </summary>
internal static class VampireEmbeds
{
    /// <summary>
    /// Embed с результатом проверки совести: пул, кубики, успехи, исход,
    /// фактически применённые потери и итоговые значения Humanity/Conscience.
    /// </summary>
    public static Embed BuildConscienceCheckEmbed(
        VampireCharacter character,
        int poolSize,
        V20RollResult roll,
        ConscienceRollResult conscience,
        ConscienceApplyResult apply,
        VampireMoralityResolver.MoralityApplyOutcome applied)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("🎲 **Пул:** ").Append(poolSize).Append(" кубов");
        if (!string.IsNullOrEmpty(character.Path))
            sb.Append(" (Путь «").Append(character.Path).Append("» = ").Append(character.PathRating).Append(")");
        sb.Append('\n');
        sb.Append("🎯 **Сложность:** ").Append(conscience.Difficulty).Append('\n');
        sb.Append("🔢 **Кубики:** ");
        for (int i = 0; i < roll.Dice.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            var d = roll.Dice[i];
            sb.Append(d >= conscience.Difficulty ? "**" : "")
              .Append(d)
              .Append(d >= conscience.Difficulty ? "**" : "");
        }
        sb.Append('\n');
        sb.Append("✅ **Успехов:** ").Append(conscience.Successes).Append('\n');

        string outcomeTitle = conscience.Outcome switch
        {
            VampireConscienceResolver.ConscienceRollOutcome.Success => "🟢 Успех",
            VampireConscienceResolver.ConscienceRollOutcome.Failure => "🟡 Неудача",
            VampireConscienceResolver.ConscienceRollOutcome.Botch   => "🔴 Провал",
            _ => "❔",
        };
        sb.Append('\n').Append("**").Append(outcomeTitle).Append(":** ")
          .Append(VampireConscienceResolver.Describe(conscience.Outcome)).Append('\n');

        if (applied.OldHumanity != applied.NewHumanity)
        {
            var totalDelta = applied.NewHumanity - applied.OldHumanity;
            sb.Append("📉 **Человечность:** ")
              .Append(applied.OldHumanity).Append(" → ").Append(applied.NewHumanity)
              .Append(" (").Append(totalDelta).Append(")\n");
            if (applied.AppliedConscienceLoss > 0 && applied.AppliedHumanityLoss > 0)
            {
                sb.Append("    _−").Append(applied.AppliedHumanityLoss)
                  .Append(" от −1 HumanityBonus, −").Append(applied.AppliedConscienceLoss)
                  .Append(" от −1 Совести (формула)_\n");
            }
            else if (applied.AppliedHumanityLoss > 0)
            {
                sb.Append("    _−").Append(applied.AppliedHumanityLoss).Append(" от −1 HumanityBonus_\n");
            }
            else if (applied.AppliedConscienceLoss > 0)
            {
                sb.Append("    _−").Append(applied.AppliedConscienceLoss).Append(" от −1 Совести_\n");
            }
        }
        if (applied.OldConscience != applied.NewConscience)
        {
            sb.Append("📉 **Совесть:** ")
              .Append(applied.OldConscience).Append(" → ").Append(applied.NewConscience).Append('\n');
        }
        if (!string.IsNullOrEmpty(applied.AddedDerangement))
        {
            sb.Append("🌀 **Получено расстройство:** «").Append(applied.AddedDerangement).Append("»\n");
        }

        if (apply.HumanityDelta != 0 && applied.AppliedHumanityLoss == 0 && applied.NewHumanity == 1)
        {
            sb.Append("\n⚠️ Дальнейшая потеря Человечности невозможна — персонаж уже на грани (Чел. = 1).\n");
        }

        var eb = new EmbedBuilder()
            .WithTitle("Проверка совести")
            .WithDescription(sb.ToString())
            .WithColor(conscience.Outcome switch
            {
                VampireConscienceResolver.ConscienceRollOutcome.Success => Color.Green,
                VampireConscienceResolver.ConscienceRollOutcome.Failure => Color.Orange,
                VampireConscienceResolver.ConscienceRollOutcome.Botch   => Color.Red,
                _ => Color.Default,
            });
        return eb.Build();
    }

    /// <summary>
    /// Embed для публикации события «потрачен 1 пункт воли» в VtMRollChannel.
    /// Никаких бросков кубиков — только фиксация траты.
    /// </summary>
    public static Embed BuildWillpowerSpendEmbed(
        VampireCharacter character, int ceiling, int remaining)
    {
        var eb = new EmbedBuilder
        {
            Title = "🩸 Потрачен пункт воли",
            Color = new Color(0x8B0000),
        };
        eb.AddField("Персонаж", $"**{character.CharacterName}**", inline: true);
        eb.AddField("Запас воли", $"{remaining} / {ceiling}", inline: true);
        eb.AddField("Что это значит",
            "Один пункт воли = +1 к одному повторному броску, либо автоуспех при " +
            "сопротивлении ярости/ротшреку, либо «игнорирование повреждения» " +
            "(бросок куба воли — отдельная кнопка).",
            inline: false);
        eb.Footer = new EmbedFooterBuilder
        {
            Text = "Vampire: the Masquerade V20, стр. 116. Бросок куба воли будет отдельной кнопкой.",
        };
        return eb.Build();
    }
}
