using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot.SlashModules;

/// <summary>
/// Slash-команды боевой механики VtM V20: <c>/vampire_damage</c> и <c>/vampire_combat_help</c>.
/// </summary>
/// <remarks>
/// <para>Логика расчёта вынесена в <see cref="VampireCombatDamageResolver"/>,
/// здесь только сбор параметров и вывод embed.</para>
/// </remarks>
public sealed class VampireCombatSlashModule : ISlashCommandModule
{
    public string Name => "vampire_combat";

    public IReadOnlyCollection<string> CommandNames { get; } = new[]
    {
        "vampire_damage",
        "vampire_combat_help",
    };

    public IReadOnlyList<SlashCommandBuilder> Register()
    {
        return new List<SlashCommandBuilder>
        {
            new SlashCommandBuilder()
                .WithName("vampire_damage")
                .WithDescription(
                    "Рассчитать пул урона по формуле V20: база + Сила + (успехи атаки − успехи защиты). " +
                    "Бросает кубы урона и возвращает количество успехов.")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("base")
                    .WithDescription("База манёвра (например, 1 для рукопашной/когтей; из таблицы оружия — для оружия).")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("strength")
                    .WithDescription("Сила атакующего (1..5).")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithMinValue(1)
                    .WithMaxValue(5)
                    .WithRequired(true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("attack")
                    .WithDescription("Успехи проверки атаки (≥0).")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("defense")
                    .WithDescription("Успехи проверки защиты (≥0).")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("damage_type")
                    .WithDescription("Тип наносимого повреждения (влияет только на текст справки).")
                    .WithType(ApplicationCommandOptionType.String)
                    .AddChoice("лёгкое", "light")
                    .AddChoice("тяжёлое", "aggravated")
                    .AddChoice("губительное", "deadly")
                    .WithRequired(false))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("label")
                    .WithDescription("Необязательная подпись (например, \"кулак\", \"Beretta\").")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(false)),

            new SlashCommandBuilder()
                .WithName("vampire_combat_help")
                .WithDescription("Справка по формуле боевой проверки и типам повреждений VtM V20."),
        };
    }

    public async Task<bool> DispatchAsync(SocketSlashCommand command)
    {
        return command.Data.Name switch
        {
            "vampire_damage" => await HandleDamageAsync(command),
            "vampire_combat_help" => await HandleHelpAsync(command),
            _ => await UnknownAsync(command),
        };
    }

    private static async Task<bool> HandleDamageAsync(SocketSlashCommand command)
    {
        if (!TryGetInt(command, "base", out var baseVal) ||
            !TryGetInt(command, "strength", out var strength) ||
            !TryGetInt(command, "attack", out var attack) ||
            !TryGetInt(command, "defense", out var defense))
        {
            await command.RespondAsync(
                "Все числовые параметры должны быть целыми числами.",
                ephemeral: true);
            return true;
        }

        if (baseVal < 0 || attack < 0 || defense < 0)
        {
            await command.RespondAsync(
                "Параметры `base`, `attack` и `defense` не могут быть отрицательными.",
                ephemeral: true);
            return true;
        }

        VampireCombatDamageResolver.Pool pool;
        try
        {
            pool = VampireCombatDamageResolver.ComputePool(baseVal, strength, attack, defense);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            await command.RespondAsync($"Параметр вне диапазона: {ex.Message}", ephemeral: true);
            return true;
        }

        var damageTypeRaw = TryGetString(command, "damage_type") ?? "light";
        var label = TryGetString(command, "label");

        // Бросок пула урона — d10, сложность 6
        var rng = new SystemRandomAdapter();
        var dice = Enumerable.Range(0, pool.DamagePoolSize)
            .Select(_ => rng.Next(1, 11))
            .ToArray();
        var successes = VampireCombatDamageResolver.CountDamageSuccesses(dice);

        var eb = new EmbedBuilder
        {
            Title = "⚔️ Урон по VtM V20",
            Color = new Color(0xC41E3A),
        };

        eb.AddField(
            "Формула",
            $"база ({pool.BaseManeuver}) + Сила ({pool.AttackerStrength}) + max(0, атака−защита) ({Math.Max(0, pool.NetAttackSuccesses)}) = **{pool.DamagePoolSize}**",
            inline: false);

        if (!string.IsNullOrWhiteSpace(label))
        {
            eb.AddField("Атака", label, inline: true);
        }
        eb.AddField("Превышение успехов", pool.NetAttackSuccesses.ToString(), inline: true);

        var diceStr = dice.Length == 0
            ? "—"
            : string.Join(", ", dice);
        eb.AddField("🎲 Пул урона", $"Бросок: {diceStr}\nСложность 6 → **{successes}** успех(ов)", inline: false);

        var typeText = damageTypeRaw switch
        {
            "aggravated" => "Тяжёлое повреждение (Х)",
            "deadly" => "Губительное повреждение (Ж)",
            _ => "Лёгкое повреждение (/)",
        };
        eb.AddField("Тип повреждения", typeText, inline: true);
        eb.AddField(
            "Куда отмечать",
            typeText switch
            {
                "Тяжёлое повреждение (Х)" => "В клетке «тяж. ранен» — крестик Х, остальные сдвигаются вниз.",
                "Губительное повреждение (Ж)" => "В клетке «тяж. ранен» — перечёркнутый крестик Ж.",
                _ => "В клетке «помят» — косая черта /, остальные сдвигаются вниз.",
            },
            inline: false);

        eb.Footer = new EmbedFooterBuilder
        {
            Text = "Vampire: the Masquerade V20, стр. 301-302. Игрок сам вносит повреждения в свой лист.",
        };

        await command.RespondAsync(embed: eb.Build());
        return true;
    }

    private static async Task<bool> HandleHelpAsync(SocketSlashCommand command)
    {
        var lines = VampireCombatDamageResolver.BuildHelpLines();
        var eb = new EmbedBuilder
        {
            Title = "📖 Боевая механика VtM V20",
            Color = new Color(0x808080),
        };
        eb.Description = string.Join("\n", lines);
        eb.Footer = new EmbedFooterBuilder
        {
            Text = "Источник: v20_p280-320.txt, стр. 301-302.",
        };
        await command.RespondAsync(embed: eb.Build(), ephemeral: true);
        return true;
    }

    private static async Task<bool> UnknownAsync(SocketSlashCommand command)
    {
        await command.RespondAsync("Неизвестная команда.", ephemeral: true);
        return true;
    }

    private static bool TryGetInt(SocketSlashCommand command, string key, out int value)
    {
        var opt = command.Data.Options.FirstOrDefault(o => o.Name == key);
        if (opt?.Value is long l) { value = (int)l; return true; }
        if (opt?.Value is int i) { value = i; return true; }
        value = 0;
        return false;
    }

    private static string? TryGetString(SocketSlashCommand command, string key)
    {
        var opt = command.Data.Options.FirstOrDefault(o => o.Name == key);
        return opt?.Value as string;
    }
}
