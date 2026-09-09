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
                "Рассчитать урон V20: база + max(0, успехи−1). " +
                "Манёвр подставит базу и тип автоматически.")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("attack")
                .WithDescription("Успехи проверки атаки (≥0).")
                .WithType(ApplicationCommandOptionType.Integer)
                .WithRequired(true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("манёвр")
                .WithDescription(
                    "Манёвр V20 (напр. «Укус», «Клинч»). " +
                    "Бот подставит базу и тип; нельзя с base/damage_type.")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(false)
                .WithAutocomplete(true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("оружие")
                .WithDescription(
                    "Оружие для манёвров с формулой «Оружие». " +
                    "Бот подставит базу и тип.")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(false)
                .WithAutocomplete(true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("base")
                .WithDescription(
                    "База манёвра/оружия вручную (если манёвр не указан). " +
                    "Напр.: 4 для револьвера .38, «Сила+1» для ножа.")
                .WithType(ApplicationCommandOptionType.Integer)
                .WithRequired(false))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("damage_type")
                .WithDescription(
                    "Тип повреждения вручную (если манёвр не указан). " +
                    "Влияет только на текст подписи.")
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

    /// <summary>
    /// Построить команду-справочник: одно текстовое поле <c>name</c>,
    /// которое handler ниже ищет в каталоге (<see cref="VampireManeuverCatalog"/>).
    /// </summary>
    /// <remarks>
    /// Раньше здесь перечислялись <c>AddChoice</c> для всех записей каталога —
    /// это ломалось на Discord: одна и та же опция <c>name</c> объявлялась
    /// N+1 раз, и при регистрации приходил ответ
    /// <c>APPLICATION_COMMAND_OPTIONS_NAME_INVALID: Option name name is already used</c>.
    /// Сейчас поле одно, и игрок вводит название/ключ руками
    /// (autocomplete можно добавить позже через <c>ISlashCommandModule</c>).
    /// </remarks>
    private static SlashCommandBuilder BuildChoiceCommand(
        string name, string description,
        IReadOnlyList<(string Label, string Value)> _choices)
    {
        return new SlashCommandBuilder()
            .WithName(name)
            .WithDescription(description)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("name")
                .WithDescription(
                    $"Название или ключ (доступно {_choices.Count} записей; " +
                    "можно ввести по-русски или ключом).")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true));
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
        // attack всегда обязателен.
        if (!TryGetInt(command, "attack", out var attack))
        {
            await command.RespondAsync(
                "Параметр `attack` должен быть целым числом.",
                ephemeral: true);
            return true;
        }
        if (attack < 0)
        {
            await command.RespondAsync(
                "Параметр `attack` не может быть отрицательным.",
                ephemeral: true);
            return true;
        }

        var maneuverKey = TryGetString(command, "манёвр")?.Trim();
        var weaponKey = TryGetString(command, "оружие")?.Trim();
        var hasBase = TryGetInt(command, "base", out var baseVal);
        var damageTypeRaw = TryGetString(command, "damage_type");
        var label = TryGetString(command, "label");

        // Конфликт: пользователь указал и манёвр, и (base / damage_type).
        var hasManualOverride = hasBase || !string.IsNullOrWhiteSpace(damageTypeRaw);
        if (!string.IsNullOrEmpty(maneuverKey) && hasManualOverride)
        {
            await command.RespondAsync(
                "Указаны и `манёвр`, и `base`/`damage_type`. " +
                "Выбери что-то одно: либо манёвр (бот сам подставит базу и тип), " +
                "либо ручные `base`+`damage_type`.",
                ephemeral: true);
            return true;
        }

        // Резолвим источник базы и тип урона.
        int resolvedBase;
        string resolvedDamageTypeKey; // "light" | "aggravated" | "deadly"
        Maneuver? resolvedManeuver = null;
        Weapon? resolvedWeapon = null;

        if (!string.IsNullOrEmpty(maneuverKey))
        {
            // Ищем манёвр в объединённом списке ближнего и дистанционного боя.
            resolvedManeuver =
                Maneuver.Find(VampireManeuverCatalog.Melee, maneuverKey)
                ?? Maneuver.Find(VampireManeuverCatalog.Ranged, maneuverKey);

            if (resolvedManeuver == null)
            {
                await command.RespondAsync(
                    $"Манёвр «{maneuverKey}» не найден. Используй автокомплит.",
                    ephemeral: true);
                return true;
            }

            // Парсим формулу урона.
            switch (resolvedManeuver.DamageFormula.Trim())
            {
                case "Сила":
                    // «Сила» — чистая Сила активного персонажа.
                    var str = await ResolveActiveStrengthAsync(command);
                    if (str < 0)
                    {
                        await command.RespondAsync(
                            "Манёвр «Сила» требует активного персонажа с заполненной Силой.",
                            ephemeral: true);
                        return true;
                    }
                    resolvedBase = str;
                    break;

                case "Оружие":
                    // Требуется опция `оружие`.
                    if (string.IsNullOrEmpty(weaponKey))
                    {
                    await command.RespondAsync(
                        $"Манёвр «{resolvedManeuver.Name}» требует указания оружия. " +
                        "Добавь опцию `оружие`.",
                        ephemeral: true);
                    return true;
                    }
                    resolvedWeapon = ResolveWeapon(weaponKey);
                    if (resolvedWeapon == null)
                    {
                        await command.RespondAsync(
                        $"Оружие «{weaponKey}» не найдено. Используй автокомплит.",
                        ephemeral: true);
                    return true;
                    }
                    resolvedBase = ResolveBaseFromWeapon(resolvedWeapon);
                    if (resolvedWeapon.DamageFormulaKind == WeaponDamageKind.StrengthPlus)
                    {
                    var s2 = await ResolveActiveStrengthAsync(command);
                    if (s2 < 0)
                    {
                        await command.RespondAsync(
                            $"Оружие «{resolvedWeapon.Name}» требует активного персонажа.",
                            ephemeral: true);
                        return true;
                    }
                    resolvedBase += s2;
                    }
                    break;

                case "—":
                case "Особый":
                    await command.RespondAsync(
                    $"Манёвр «{resolvedManeuver.Name}» не наносит урона числом — " +
                    "это защитное или особое действие.",
                    ephemeral: true);
                    return true;

                default:
                    // Пытаемся распарсить «Сила+N», «Сила + N», «N».
                    if (!TryParseDamageFormula(resolvedManeuver.DamageFormula,
                        out var fixedPart, out var usesStrength))
                    {
                    await command.RespondAsync(
                        $"Не удалось разобрать формулу урона «{resolvedManeuver.DamageFormula}» " +
                        $"для манёвра «{resolvedManeuver.Name}». " +
                        "Укажи `base` и `damage_type` вручную.",
                        ephemeral: true);
                    return true;
                    }

                    if (usesStrength)
                    {
                    // «Сила + N»: база = Сила персонажа + N.
                    var s = await ResolveActiveStrengthAsync(command);
                    if (s < 0)
                    {
                        await command.RespondAsync(
                            $"Манёвр «{resolvedManeuver.Name}» использует Силу, " +
                            "но активный персонаж не привязан.",
                            ephemeral: true);
                        return true;
                    }
                    resolvedBase = s + (fixedPart ?? 0);
                    }
                    else
                    {
                    // Просто число.
                    resolvedBase = fixedPart ?? 0;
                    }
                    break;
            }

            resolvedDamageTypeKey = MapDamageTypeToKey(resolvedManeuver.DamageKind);
        }
        else if (!string.IsNullOrEmpty(weaponKey) && !hasBase)
        {
            // Оружие без манёвра — фиксированная база.
            resolvedWeapon = ResolveWeapon(weaponKey);
            if (resolvedWeapon == null)
            {
                await command.RespondAsync(
                    $"Оружие «{weaponKey}» не найдено. Используй автокомплит.",
                    ephemeral: true);
                return true;
            }
            resolvedBase = ResolveBaseFromWeapon(resolvedWeapon);
            resolvedDamageTypeKey = MapDamageTypeToKey(resolvedWeapon.DamageType);
        }
        else if (hasBase)
        {
            // Ручной режим.
            if (baseVal < 0)
            {
                await command.RespondAsync(
                    "Параметр `base` не может быть отрицательным.",
                    ephemeral: true);
                return true;
            }
            resolvedBase = baseVal;
            resolvedDamageTypeKey = string.IsNullOrWhiteSpace(damageTypeRaw) ? "light" : damageTypeRaw;
        }
        else
        {
            await command.RespondAsync(
                "Укажи либо `манёвр` (и при необходимости `оружие`), либо `base` + `damage_type`.",
                ephemeral: true);
            return true;
        }

        if (resolvedBase < 0)
        {
            await command.RespondAsync(
                "Итоговая база урона не может быть отрицательной.",
                ephemeral: true);
            return true;
        }

        VampireCombatDamageResolver.Pool pool;
        try
        {
            pool = VampireCombatDamageResolver.ComputePool(resolvedBase, attack);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            await command.RespondAsync($"Параметр вне диапазона: {ex.Message}", ephemeral: true);
            return true;
        }

        var rng = new SystemRandomAdapter();
        var dice = Enumerable.Range(0, pool.DamagePoolSize)
            .Select(_ => rng.Next(1, 11))
            .ToArray();
        var successes = VampireCombatDamageResolver.CountDamageSuccesses(dice);

        var eb = new EmbedBuilder
        {
            Color = new Color(0xC41E3A),
        };

        // Название: либо манёвр, либо оружие, либо подпись.
        var headerName = !string.IsNullOrWhiteSpace(label)
            ? label
            : resolvedManeuver?.Name
              ?? resolvedWeapon?.Name
              ?? "Нанесение урона";
        eb.AddField("Нанесение урона", headerName, inline: false);

        eb.AddField(
            "Формула",
            $"база ({pool.BaseManeuver}) + max(0, успехи−1) ({pool.ExcessSuccesses}) = **{pool.DamagePoolSize}**",
            inline: false);

        eb.AddField("Доп. успехи", pool.ExcessSuccesses.ToString(), inline: true);

        var diceStr = dice.Length == 0
            ? "—"
            : string.Join(", ", dice);
        eb.AddField("🎲 Пул урона", $"Бросок: {diceStr}\nСложность 6 → **{successes}** успех(ов)", inline: false);

        var typeText = resolvedDamageTypeKey switch
        {
            "aggravated" => "Тяжёлое повреждение (Х)",
            "deadly" => "Губительное повреждение (Ж)",
            _ => "Лёгкое повреждение (/)",
        };
        eb.AddField("Тип повреждения", typeText, inline: true);

        eb.Footer = new EmbedFooterBuilder
        {
            Text = "Vampire: the Masquerade V20, стр. 301-302.",
        };

        if (!await VampireRollChannelPublisher.PublishAsync(command, eb.Build()))
            return true;
        return true;
    }

    /// <summary>
    /// Достаёт Силу активного персонажа. Возвращает -1, если активного чарника нет
    /// или Сила не заполнена.
    /// </summary>
    private static async Task<int> ResolveActiveStrengthAsync(SocketSlashCommand command)
    {
        var lookup = RollContext.ActiveCharacterLookup;
        if (lookup == null || !command.GuildId.HasValue) return -1;
        var active = await lookup(command.GuildId.Value, command.User.Id);
        if (active == null || active.Character == null) return -1;
        if (active.Character.Attributes == null) return -1;
        if (!active.Character.Attributes.TryGetValue("Сила", out var v)) return -1;
        return v;
    }

    /// <summary>
    /// Ищет оружие по ключу в списке ближнего и дистанционного оружия.
    /// </summary>
    private static Weapon? ResolveWeapon(string key)
    {
        foreach (var w in VampireManeuverCatalog.MeleeWeapons)
            if (w.Key == key) return w;
        foreach (var w in VampireManeuverCatalog.RangedWeapons)
            if (w.Key == key) return w;
        return null;
    }

    /// <summary>
    /// Возвращает числовую базу урона для оружия. Для StrengthPlus — N (без Силы,
    /// потому что Сила прибавляется отдельно в формуле манёвра).
    /// </summary>
    private static int ResolveBaseFromWeapon(Weapon w)
    {
        // У оружия DamageBase уже содержит фиксированную часть: «Сила+2» → 2.
        return w.DamageBase;
    }

    /// <summary>
    /// Парсит формулу урона. Возвращает true, если получилось.
    /// <list type="bullet">
    ///   <item>«Сила» → fixedPart=null, usesStrength=true (но этот случай обработан отдельно).</item>
    ///   <item>«Сила + N» → fixedPart=N, usesStrength=true.</item>
    ///   <item>«N» → fixedPart=N, usesStrength=false.</item>
    /// </list>
    /// </summary>
    private static bool TryParseDamageFormula(
        string formula, out int? fixedPart, out bool usesStrength)
    {
        fixedPart = null;
        usesStrength = false;
        if (string.IsNullOrWhiteSpace(formula)) return false;
        var f = formula.Trim();

        // Только число.
        if (int.TryParse(f, out var justNumber))
        {
            fixedPart = justNumber;
            return true;
        }

        // «Сила» или «Сила+N» или «Сила + N».
        if (f.StartsWith("Сила", StringComparison.OrdinalIgnoreCase))
        {
            var rest = f.Substring("Сила".Length).Trim();
            if (string.IsNullOrEmpty(rest))
            {
                usesStrength = true;
                return true;
            }
            rest = rest.TrimStart('+', ' ').Trim();
            if (int.TryParse(rest, out var n))
            {
                fixedPart = n;
                usesStrength = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Маппинг <see cref="DamageType"/> → ключ команды <c>/vampire_damage</c>.
    /// </summary>
    /// <remarks>
    /// В <c>/vampire_damage</c> ключи: <c>light</c> = лёгкие (/), <c>deadly</c> = тяжёлые (X),
    /// <c>aggravated</c> = губительные (Ж). Внутренний <see cref="DamageType"/>
    /// совпадает с этим разбиением по сути, но имена в нём: Bashing / Lethal / Aggravated.
    /// </remarks>
    private static string MapDamageTypeToKey(DamageType t) => t switch
    {
        DamageType.Bashing => "light",
        DamageType.Lethal => "deadly",
        DamageType.Aggravated => "aggravated",
        _ => "light",
    };

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
