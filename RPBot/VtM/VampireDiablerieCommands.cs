using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot;

/// <summary>
/// <c>/vampire_diablerie</c> (V20 стр. 310-311) — справочный расчёт итогов
/// диаблери. Ничего не меняет в листах — это решение рассказчика,
/// который применяет результат вручную.
/// </summary>
internal sealed class VampireDiablerieCommands
{
    public async Task HandleDiablerieAsync(SocketSlashCommand command)
    {
        var guildId = command.GuildId;
        if (!guildId.HasValue)
        {
            await command.RespondAsync("Команда доступна только на сервере.", ephemeral: true);
            return;
        }

        var attackerOpt = VampireCommandHelpers.GetUser(command, "attacker");
        var victimOpt = VampireCommandHelpers.GetUser(command, "victim");
        if (!attackerOpt.HasValue || !victimOpt.HasValue)
        {
            await command.RespondAsync(
                "Нужны оба участника: `@attacker` и `@victim`.",
                ephemeral: true);
            return;
        }
        if (attackerOpt.Value.id == victimOpt.Value.id)
        {
            await command.RespondAsync(
                "Атакующий и жертва — один и тот же пользователь.",
                ephemeral: true);
            return;
        }

        var genRaw = VampireCommandHelpers.GetString(command, "gen");
        var humRaw = VampireCommandHelpers.GetString(command, "hum");
        if (!int.TryParse(genRaw, out var attackerGen))
        {
            await command.RespondAsync(
                "`gen` должен быть целым числом 3..15 (поколение атакующего).",
                ephemeral: true);
            return;
        }
        if (!int.TryParse(humRaw, out var attackerHum))
        {
            await command.RespondAsync(
                "`hum` должен быть целым числом 1..10 (Человечность или Path атакующего).",
                ephemeral: true);
            return;
        }

        var successRaw = VampireCommandHelpers.GetString(command, "success");
        var success = successRaw is null
            ? true
            : string.Equals(successRaw, "true", StringComparison.OrdinalIgnoreCase)
              || successRaw == "1"
              || string.Equals(successRaw, "yes", StringComparison.OrdinalIgnoreCase);

        var victimGen = await VampireCommandHelpers.TryReadVictimGenerationAsync(guildId.Value, victimOpt.Value.id)
                        ?? Math.Max(3, attackerGen - 1);

        VampireDiablerieResolver.Result result;
        try
        {
            result = VampireDiablerieResolver.Resolve(
                attackerGen,
                victimGen,
                attackerHum,
                success);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            await command.RespondAsync($"Параметр вне диапазона: {ex.Message}", ephemeral: true);
            return;
        }

        var eb = new EmbedBuilder
        {
            Title = success ? "🩸 Диаблери: итоги" : "🩸 Диаблери: провал",
            Color = success ? new Color(0x8B0000) : new Color(0x555555),
        };
        eb.AddField("Атакующий", $"<@{attackerOpt.Value.id}>", inline: true);
        eb.AddField("Жертва", $"<@{victimOpt.Value.id}>", inline: true);
        eb.AddField("Поколение жертвы", $"{victimGen}-е", inline: true);

        if (success)
        {
            eb.AddField(
                "Поколение атакующего",
                $"{result.AttackerGenerationBefore} → **{result.AttackerGenerationAfter}** (−{result.GenerationDrop})",
                inline: false);
            eb.AddField(
                "Чёрные полосы в ауре",
                $"{result.AuraStainsYears} г.",
                inline: true);
            eb.AddField(
                "Проверка эйфории",
                $"сложность **{result.EuphoriaDifficulty}**",
                inline: true);
            eb.AddField(
                "Снижение Человечности",
                $"минимум −{result.HumanityLossFlat} (плюс проверка совести по решению ST).",
                inline: false);
        }
        else
        {
            eb.AddField(
                "Поколение атакующего",
                $"{result.AttackerGenerationBefore} (без изменений).",
                inline: false);
        }

        var noteText = string.Join("\n• ", new[] { string.Empty }.Concat(result.Notes));
        eb.AddField("Заметки", noteText, inline: false);
        eb.Footer = new EmbedFooterBuilder
        {
            Text = "Vampire: the Masquerade V20, стр. 310-311. Применить изменения — в листах вручную.",
        };

        await command.RespondAsync(embed: eb.Build(), ephemeral: true);
    }
}
