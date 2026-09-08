using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot.SlashModules;

/// <summary>
/// Slash-команды боевой механики VtM V20:
/// <c>/vampire_damage</c>, <c>/vampire_combat_help</c>, <c>/vampire_maneuver</c>, <c>/vampire_weapon</c>, <c>/vampire_armor</c>.
/// </summary>
/// <remarks>
/// <para>Логика расчёта вынесена в <see cref="VampireCombatDamageResolver"/>
/// и <see cref="VampireManeuverCatalog"/>. Здесь только сбор параметров
/// и вывод embed.</para>
/// </remarks>
public sealed class VampireCombatSlashModule : ISlashCommandModule
{
    public string Name => "vampire_combat";

    public IReadOnlyCollection<string> CommandNames { get; } = new[]
    {
        "vampire_damage",
        "vampire_combat_help",
        "vampire_maneuver",
        "vampire_weapon",
        "vampire_armor",
    };

    public IReadOnlyList<SlashCommandBuilder> Register()
    {
        var builder = new List<SlashCommandBuilder>();

        builder.Add(new SlashCommandBuilder()
            .WithName("vampire_damage")
            .WithDescription(
                "Рассчитать пул урона по формуле V20 (стр. 301): база + max(0, успехи−1).")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("base")
                .WithDescription("База манёвра или оружия. Напр.: 4 для револьвера .38, «Сила+1» для ножа.")
                .WithType(ApplicationCommandOptionType.Integer)
                .WithRequired(true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("attack")
                .WithDescription("Успехи проверки атаки (≥0).")
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
                .WithDescription("Необязательная подпись (например, «кулак», «Beretta»).")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(false)));

        builder.Add(new SlashCommandBuilder()
            .WithName("vampire_combat_help")
            .WithDescription("Справка по формуле боевой проверки, манёврам и таблицам VtM V20."));

        builder.Add(BuildChoiceCommand(
            "vampire_maneuver",
            "Описание боевого манёвра по V20 (стр. 303-309).",
            ManeuverChoices()));

        builder.Add(BuildChoiceCommand(
            "vampire_weapon",
            "Описание оружия по V20 (стр. 310-311).",
            WeaponChoices()));

        builder.Add(BuildChoiceCommand(
            "vampire_armor",
            "Описание класса брони по V20 (стр. 310).",
            ArmorChoices()));

        return builder;
    }

    private static SlashCommandBuilder BuildChoiceCommand(
        string name, string description, IReadOnlyList<(string Label, string Value)> choices)
    {
        var b = new SlashCommandBuilder()
            .WithName(name)
            .WithDescription(description)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("name")
                .WithDescription("Название из справочника.")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true));
        foreach (var c in choices)
        {
            b.AddOption(new SlashCommandOptionBuilder()
                .WithName("name")
                .WithDescription(c.Label)
                .WithType(ApplicationCommandOptionType.String)
                .AddChoice(c.Label, c.Value)
                .WithRequired(true));
        }
        return b;
    }

    public Task<bool> DispatchAsync(SocketSlashCommand command)
    {
        return command.Data.Name switch
        {
            "vampire_damage" => HandleDamageAsync(command),
            "vampire_combat_help" => HandleHelpAsync(command),
            "vampire_maneuver" => HandleManeuverAsync(command),
            "vampire_weapon" => HandleWeaponAsync(command),
            "vampire_armor" => HandleArmorAsync(command),
            _ => UnknownAsync(command),
        };
    }

    private static async Task<bool> HandleDamageAsync(SocketSlashCommand command)
    {
        if (!TryGetInt(command, "base", out var baseVal) ||
            !TryGetInt(command, "attack", out var attack))
        {
            await command.RespondAsync(
                "Все числовые параметры должны быть целыми числами.",
                ephemeral: true);
            return true;
        }

        if (baseVal < 0 || attack < 0)
        {
            await command.RespondAsync(
                "Параметры `base` и `attack` не могут быть отрицательными.",
                ephemeral: true);
            return true;
        }

        VampireCombatDamageResolver.Pool pool;
        try
        {
            pool = VampireCombatDamageResolver.ComputePool(baseVal, attack);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            await command.RespondAsync($"Параметр вне диапазона: {ex.Message}", ephemeral: true);
            return true;
        }

        var damageTypeRaw = TryGetString(command, "damage_type") ?? "light";
        var label = TryGetString(command, "label");

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
            $"база ({pool.BaseManeuver}) + max(0, успехи−1) ({pool.ExcessSuccesses}) = **{pool.DamagePoolSize}**",
            inline: false);

        if (!string.IsNullOrWhiteSpace(label))
        {
            eb.AddField("Атака", label, inline: true);
        }
        eb.AddField("Доп. успехи", pool.ExcessSuccesses.ToString(), inline: true);

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

        if (!await VampireRollChannelPublisher.PublishAsync(command, eb.Build()))
            return true;
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
            Text = "Источник: v20_p280-320.txt, стр. 300-312.",
        };
        await command.RespondAsync(embed: eb.Build(), ephemeral: true);
        return true;
    }

    private static async Task<bool> HandleManeuverAsync(SocketSlashCommand command)
    {
        var key = TryGetString(command, "name");
        if (string.IsNullOrWhiteSpace(key))
        {
            await command.RespondAsync("Укажите название манёвра.", ephemeral: true);
            return true;
        }

        var melee = VampireManeuverCatalog.FindMelee(key);
        var ranged = VampireManeuverCatalog.FindRanged(key);
        var m = melee ?? ranged;
        if (m == null)
        {
            await command.RespondAsync($"Манёвр «{key}» не найден.", ephemeral: true);
            return true;
        }

        var eb = new EmbedBuilder
        {
            Title = $"🤜 {m.Name}",
            Color = new Color(0xC41E3A),
        };
        eb.AddField("Параметры", m.Stat, inline: false);
        eb.AddField("Точность", FormatSigned(m.Accuracy), inline: true);
        eb.AddField("Урон", m.DamageFormula, inline: true);
        if (m.Notes != null)
        {
            eb.AddField("Примечания", m.Notes, inline: false);
        }
        eb.Footer = new EmbedFooterBuilder { Text = "VtM V20, стр. 303-309." };
        await command.RespondAsync(embed: eb.Build(), ephemeral: true);
        return true;
    }

    private static async Task<bool> HandleWeaponAsync(SocketSlashCommand command)
    {
        var key = TryGetString(command, "name");
        if (string.IsNullOrWhiteSpace(key))
        {
            await command.RespondAsync("Укажите название оружия.", ephemeral: true);
            return true;
        }

        var w = VampireManeuverCatalog.FindWeapon(key);
        if (w == null)
        {
            await command.RespondAsync($"Оружие «{key}» не найдено.", ephemeral: true);
            return true;
        }

        var eb = new EmbedBuilder
        {
            Title = $"🗡 {w.Name}",
            Color = new Color(0xC41E3A),
        };
        eb.AddField("Урон", w.DamageFormula, inline: true);
        eb.AddField("Скрытное ношение", w.Concealment, inline: true);
        if (w.Range > 0)
        {
            eb.AddField("Дистанция", $"{w.Range} м (макс {w.MaxRange})", inline: true);
            eb.AddField("Скорострельность", w.RateOfFire.ToString(), inline: true);
            eb.AddField("Боезапас", w.Magazine.ToString(), inline: true);
        }
        if (w.Notes != null)
        {
            eb.AddField("Примечания", w.Notes, inline: false);
        }
        eb.Footer = new EmbedFooterBuilder { Text = "VtM V20, стр. 310-311." };
        await command.RespondAsync(embed: eb.Build(), ephemeral: true);
        return true;
    }

    private static async Task<bool> HandleArmorAsync(SocketSlashCommand command)
    {
        var key = TryGetString(command, "name");
        if (string.IsNullOrWhiteSpace(key))
        {
            await command.RespondAsync("Укажите класс брони.", ephemeral: true);
            return true;
        }

        var a = VampireManeuverCatalog.FindArmor(key);
        if (a == null)
        {
            await command.RespondAsync($"Класс брони «{key}» не найден.", ephemeral: true);
            return true;
        }

        var eb = new EmbedBuilder
        {
            Title = $"🛡 {a.Name}",
            Color = new Color(0x808080),
        };
        eb.AddField("Показатель брони", a.Protection.ToString(), inline: true);
        eb.AddField("Модификатор удобства", FormatSigned(a.ComfortModifier), inline: true);
        eb.Description =
            "Показатель брони прибавляется к пулу прочности от лёгких, тяжёлых и губительных " +
            "(клыки/когти). Не защищает от огня/солнца. Удобство снижает пулы на ловкость.";
        eb.Footer = new EmbedFooterBuilder { Text = "VtM V20, стр. 310." };
        await command.RespondAsync(embed: eb.Build(), ephemeral: true);
        return true;
    }

    private static async Task<bool> UnknownAsync(SocketSlashCommand command)
    {
        await command.RespondAsync("Неизвестная команда.", ephemeral: true);
        return true;
    }

    private static string FormatSigned(int v) => v switch
    {
        > 0 => $"+{v}",
        < 0 => v.ToString(),
        _ => "0",
    };

    private static IReadOnlyList<(string Label, string Value)> ManeuverChoices()
    {
        var list = new List<(string, string)>();
        foreach (var m in VampireManeuverCatalog.Melee)
            list.Add((m.Name, m.Key));
        foreach (var m in VampireManeuverCatalog.Ranged)
            list.Add((m.Name, m.Key));
        return list;
    }

    private static IReadOnlyList<(string Label, string Value)> WeaponChoices()
    {
        var list = new List<(string, string)>();
        foreach (var w in VampireManeuverCatalog.MeleeWeapons)
            list.Add((w.Name, w.Key));
        foreach (var w in VampireManeuverCatalog.RangedWeapons)
            list.Add((w.Name, w.Key));
        return list;
    }

    private static IReadOnlyList<(string Label, string Value)> ArmorChoices()
    {
        var list = new List<(string, string)>();
        foreach (var a in VampireManeuverCatalog.Armor)
            list.Add((a.Name, a.Key));
        return list;
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
