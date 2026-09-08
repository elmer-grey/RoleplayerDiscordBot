using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot.SlashModules;

/// <summary>
/// Slash-команда «бросок кубов VtM V20».
/// <para>Формат (по правилам из <c>rules-source/v20_pages270-320.txt</c> стр. 1889-1900):
/// «пул проверки равен сумме показателей одной из своих характеристик и одной
/// из своих способностей».</para>
/// <para>Дисциплина (Стремительность, Стойкость и т.п.) увеличивает пул, если
/// используется в соответствующей проверке (см. v20_p280-320.txt стр. 744-747).</para>
/// <para>Специализация: каждая «10» = 2 успеха (стр. 1899).</para>
/// <para>Без навыка: пул = одна характеристика, сложность +1 (стр. 1915-1919).</para>
/// </summary>
/// <remarks>
/// <para>Команда: <c>/vampire_roll характеристика:[Choice] [навык:[Choice]] [дисциплина:[Choice]] [бонус:int] [сложность:int] [ярлык:str]</c>.</para>
/// <para>Discord Choice поддерживает максимум 25 элементов, поэтому все Choice
/// отмечены <c>WithAutocomplete(true)</c>. Реальные подсказки (свыше 25) приходят
/// через обработчик <see cref="VampireRollAutocompleteHandler"/>.</para>
/// </remarks>
public sealed class VampireRollSlashModule : ISlashCommandModule
{
    public string Name => "vampire_roll";

    public IReadOnlyCollection<string> CommandNames { get; } = new[]
    {
        "vampire_roll",
    };

    public IReadOnlyList<SlashCommandBuilder> Register()
    {
        // Discord ограничивает Choice 25 элементами на опцию, поэтому
        // все текстовые опции (характеристика / навык / дисциплина)
        // помечены WithAutocomplete(true) и обслуживаются через
        // VampireRollAutocompleteHandler, подключённый к
        // DiscordSocketClient.AutocompleteExecuted в Program.cs.
        return new List<SlashCommandBuilder>
        {
            new SlashCommandBuilder()
                .WithName("vampire_roll")
                .WithDescription(
                    "Бросить кубы VtM V20: выбери характеристику и (опц.) навык и дисциплину. " +
                    "Под сообщением появятся кнопки «Переброс за волю», «Повторить», «Готово».")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("характеристика")
                    .WithDescription(
                        "Одна из характеристик (Сила / Ловкость / Выносливость / Обаяние / " +
                        "Манипуляция / Привлекательность / Восприятие / Интеллект / Смекалка).")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true)
                    .WithAutocomplete(true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("навык")
                    .WithDescription(
                        "Способность из листа (таланты / навыки / знания). " +
                        "Без навыка — пул = одна характеристика, сложность +1.")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(false)
                    .WithAutocomplete(true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("дисциплина")
                    .WithDescription(
                        "Дисциплина, увеличивающая пул (например, Стремительность, Стойкость). " +
                        "Берётся уровень из листа персонажа.")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(false)
                    .WithAutocomplete(true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("бонус")
                    .WithDescription(
                        "Дополнительные / штрафные d10 (например, +2 за благоприятные условия, " +
                        "-1 за травму). По умолчанию 0.")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(false)
                    .AddChoice("-5", -5L)
                    .AddChoice("-3", -3L)
                    .AddChoice("-2", -2L)
                    .AddChoice("-1", -1L)
                    .AddChoice("0", 0L)
                    .AddChoice("1", 1L)
                    .AddChoice("2", 2L)
                    .AddChoice("3", 3L)
                    .AddChoice("5", 5L))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("сложность")
                    .WithDescription(
                        "Сложность проверки (2..10). По умолчанию 6 — стандартная для V20.")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(false)
                    .AddChoice("2", 2L)
                    .AddChoice("3", 3L)
                    .AddChoice("4", 4L)
                    .AddChoice("5", 5L)
                    .AddChoice("6", 6L)
                    .AddChoice("7", 7L)
                    .AddChoice("8", 8L)
                    .AddChoice("9", 9L)
                    .AddChoice("10", 10L))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("ярлык")
                    .WithDescription("Свободная подпись (например, «Подкрасться»).")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(false))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("специализация")
                    .WithDescription(
                        "Специализация ad-hoc для этого броска (свободный текст). " +
                        "Если не указана — используется специализация из листа персонажа. " +
                        "Специализация удваивает десятки: каждая выпавшая 10 = 2 успеха (V20).")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(false))
        };
    }

    public async Task<bool> DispatchAsync(SocketSlashCommand command)
    {
        return command.Data.Name switch
        {
            "vampire_roll" => await HandleRollAsync(command),
            _ => await UnknownAsync(command),
        };
    }

    // ──────────────────────────────────────────────────────────────────
    //  /vampire_roll
    // ──────────────────────────────────────────────────────────────────

    private async Task<bool> HandleRollAsync(SocketSlashCommand command)
    {
        var characteristic = TryGetString(command, "характеристика");
        if (string.IsNullOrWhiteSpace(characteristic))
        {
            await command.RespondAsync("Укажи характеристику.", ephemeral: true);
            return true;
        }
        characteristic = characteristic.Trim();

        var ability = TryGetString(command, "навык")?.Trim();
        var discipline = TryGetString(command, "дисциплина")?.Trim();
        var adHocSpecialization = TryGetString(command, "специализация")?.Trim();
        TryGetInt(command, "бонус", out var bonus);
        TryGetInt(command, "сложность", out var difficulty);
        if (difficulty == 0) difficulty = 6;
        var label = TryGetString(command, "ярлык");

        var registry = RollContext.Registry;
        if (registry == null)
        {
            await command.RespondAsync(
                "Бросок VtM временно недоступен: реестр не инициализирован.",
                ephemeral: true);
            return true;
        }
        var activeLookup = RollContext.ActiveCharacterLookup;
        if (activeLookup == null || !command.GuildId.HasValue)
        {
            await command.RespondAsync(
                "Бросок по листу работает только на сервере и требует активного чарника. " +
                "Сначала используй /vampire bind в канале.",
                ephemeral: true);
            return true;
        }

        var active = await activeLookup(command.GuildId.Value, command.User.Id);
        if (active == null)
        {
            await command.RespondAsync(
                "Не нашёл активного персонажа. Используй /vampire bind на сервере.",
                ephemeral: true);
            return true;
        }

        RollContext.BuildResult build;
        try
        {
            build = RollContext.BuildPool(
                character: active.Character,
                characteristicName: characteristic,
                abilityName: ability,
                disciplineName: discipline,
                bonusDice: bonus,
                difficulty: difficulty);
        }
        catch (ArgumentException ex)
        {
            await command.RespondAsync($"Ошибка пула: {ex.Message}", ephemeral: true);
            return true;
        }

        if (build.PoolSize < 1)
        {
            await command.RespondAsync(
                "Итоговый пул < 1 — бросок не нужен.",
                ephemeral: true);
            return true;
        }
        if (build.PoolSize > VampireDicePool.MaxTotalDice)
        {
            await command.RespondAsync(
                $"Итоговый пул {build.PoolSize} превышает максимум " +
                $"{VampireDicePool.MaxTotalDice}.",
                ephemeral: true);
            return true;
        }
        if (build.Hunger < 0 || build.Hunger > VampireDicePool.MaxHunger)
        {
            await command.RespondAsync(
                $"Голод персонажа ({build.Hunger}) вне диапазона 0..{VampireDicePool.MaxHunger}.",
                ephemeral: true);
            return true;
        }
        if (build.Hunger > build.PoolSize)
        {
            await command.RespondAsync(
                $"Голод ({build.Hunger}) не может превышать размер пула ({build.PoolSize}).",
                ephemeral: true);
            return true;
        }

        V5RollResult result;
        try
        {
            // Гибрид V20 + V5: V5-разбиение regular/hunger (голодные кубы),
            // V20-специализация действует на regular кубы (см. CountSuccessesHybrid).
            result = VampireDicePool.RollV5(
                build.PoolSize, build.Hunger, new SystemRandomAdapter());
        }
        catch (ArgumentException ex)
        {
            await command.RespondAsync($"Ошибка броска: {ex.Message}", ephemeral: true);
            return true;
        }

        // V20: специализация удваивает десятки только в regular.
        int successes = VampireDicePool.CountSuccessesHybrid(
            result.RegularDice ?? Array.Empty<int>(),
            result.HungerDice ?? Array.Empty<int>(),
            build.Specialization);

        var eb = BuildRollEmbed(result, build, label, successes);
        var components = VampireRollComponents.BuildRollButtons(
            command.User.Id, build.Specialization);

        // По дизайн-решению: VtM-броски публикуются в выделенный канал
        // ServerConfig.VtMRollChannelID, а не в канал вызова команды.
        if (!await VampireRollChannelPublisher.PublishAsync(command, eb, components))
            return true;

        var botMessage = await command.GetOriginalResponseAsync();

        registry.Record(
            userId: command.User.Id,
            messageId: botMessage.Id,
            regularDice: result.RegularDice ?? Array.Empty<int>(),
            hungerDice: result.HungerDice ?? Array.Empty<int>(),
            specialization: build.Specialization,
            poolSize: build.PoolSize,
            bonusDie: result.BonusDie);
        return true;
    }

    /// <summary>
    /// Сборка embed'а основного броска (гибрид V20+V5).
    /// </summary>
    internal static Embed BuildRollEmbed(
        V5RollResult result,
        RollContext.BuildResult build,
        string? label,
        int successes)
    {
        var eb = new EmbedBuilder
        {
            Title = "🎲 Бросок VtM (гибрид V20+V5)",
            Color = new Color(0xC41E3A),
        };

        if (!string.IsNullOrWhiteSpace(label))
        {
            eb.AddField("Подпись", label, inline: false);
        }

        var lines = new List<string>
        {
            $"• **{build.CharacteristicName}** ({build.CharacteristicValue})",
        };
        if (!string.IsNullOrEmpty(build.AbilityName))
        {
            var specSuffix = !string.IsNullOrEmpty(build.Specialization)
                ? $" — специализация: «{build.Specialization}»"
                : "";
            lines.Add($"• **{build.AbilityName}** ({build.AbilityValue}){specSuffix}");
        }
        else
        {
            lines.Add("• _навык не указан (пул = одна характеристика, сложность +1)_");
        }
        if (!string.IsNullOrEmpty(build.DisciplineName))
        {
            lines.Add($"• **{build.DisciplineName}** (дисциплина {build.DisciplineValue})");
        }
        if (build.BonusDice != 0)
        {
            var sign = build.BonusDice > 0 ? "+" : "";
            lines.Add($"• Бонус/штраф: {sign}{build.BonusDice}");
        }
        lines.Add($"Итого: **{build.PoolSize}** кубов " +
                  $"(regular {result.RegularDice?.Length ?? 0}, " +
                  $"hunger {result.HungerDice?.Length ?? 0}, " +
                  $"сложность {build.Difficulty})");
        eb.AddField("Пул", string.Join("\n", lines), inline: false);

        var regularStr = (result.RegularDice == null || result.RegularDice.Length == 0)
            ? "—"
            : string.Join(", ", result.RegularDice);
        var hungerStr = (result.HungerDice == null || result.HungerDice.Length == 0)
            ? "—"
            : string.Join(", ", result.HungerDice);

        eb.AddField(
            "Кубы",
            $"regular ({result.RegularDice?.Length ?? 0}): {regularStr}\n" +
            $"hunger ({result.HungerDice?.Length ?? 0}): {hungerStr}" +
            (result.BonusDie.HasValue ? $"\nБонусный куб: {result.BonusDie}" : ""),
            inline: false);

        // V20: специализация удваивает десятки только в regular.
        int regTens = result.RegularDice?.Count(d => d == 10) ?? 0;
        int regSixesToNines = result.RegularDice?.Count(d => d >= 6 && d < 10) ?? 0;
        int hungTens = result.HungerDice?.Count(d => d == 10) ?? 0;
        int hungSixesToNines = result.HungerDice?.Count(d => d >= 6 && d < 10) ?? 0;
        string breakdown =
            !string.IsNullOrEmpty(build.Specialization)
                ? $"regular: 6–9 × {regSixesToNines} + 10 × {regTens}×2 «{build.Specialization}» = {regSixesToNines + regTens * 2}\n" +
                  $"hunger: 6–9 × {hungSixesToNines} + 10 × {hungTens} = {hungSixesToNines + hungTens}"
                : $"regular: 6–9 × {regSixesToNines} + 10 × {regTens} = {regSixesToNines + regTens}\n" +
                  $"hunger: 6–9 × {hungSixesToNines} + 10 × {hungTens} = {hungSixesToNines + hungTens}";

        eb.AddField(
            "Подсчёт успехов (V20+V5)",
            breakdown,
            inline: false);

        eb.AddField(
            "Итог",
            $"**{successes}** успехов" +
            (successes >= build.Difficulty
                ? $" — успех против сложности {build.Difficulty}"
                : $" — провал против сложности {build.Difficulty}") +
            (result.IsMessyCritical ? "\n⚠ **Messy Critical** — есть успех + хотя бы одна 1-ца в hunger" : "") +
            (result.IsBestialFailure ? "\n⚠ **Bestial Failure** — нет успехов + хотя бы одна 1-ца в hunger" : ""),
            inline: true);

        eb.Footer = new EmbedFooterBuilder
        {
            Text = "V20: специализация удваивает десятки в regular. " +
                   "V5: hunger-кубы не перебрасываются за волю. " +
                   "Переброс за волю (−1 WP), повтор (N-1), готово — кнопки под сообщением.",
        };

        return eb.Build();
    }

    // ──────────────────────────────────────────────────────────────────

    private static async Task<bool> UnknownAsync(SocketSlashCommand command)
    {
        await command.RespondAsync("Неизвестная подкоманда.", ephemeral: true);
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

/// <summary>
/// DI-точка для зависимостей модуля бросков VtM.
/// </summary>
public static class RollContext
{
    private static VampireRollRegistry? _registry;
    private static Func<ulong, ulong, Task<VampireActiveContext?>>? _activeCharacterLookup;

    public static VampireRollRegistry? Registry => _registry;

    public static Func<ulong, ulong, Task<VampireActiveContext?>>? ActiveCharacterLookup
        => _activeCharacterLookup;

    public static void Configure(
        VampireRollRegistry registry,
        Func<ulong, ulong, Task<VampireActiveContext?>>? activeCharacterLookup = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _activeCharacterLookup = activeCharacterLookup;
    }

    public static void Reset()
    {
        _registry = null;
        _activeCharacterLookup = null;
    }

    /// <summary>
    /// Пул по правилам V20: характеристика + навык + дисциплина + бонус.
    /// </summary>
    /// <remarks>
    /// <para>V20 (rules-source/v20_pages270-320.txt стр. 1889-1900): пул = сумма
    /// показателей одной характеристики и одной способности. Если навык не указан —
    /// пул = одна характеристика, сложность +1.</para>
    /// <para>Дисциплина (rules-source/v20_p280-320.txt стр. 744-747): Стремительность
    /// и Стойкость увеличивают пул проверок, в которых используются соответствующие
    /// характеристики.</para>
    /// </remarks>
    public static BuildResult BuildPool(
        VampireCharacter character,
        string characteristicName,
        string? abilityName,
        string? disciplineName,
        int bonusDice,
        int difficulty,
        string? adHocSpecialization = null)
    {
        if (character == null) throw new ArgumentNullException(nameof(character));
        if (string.IsNullOrWhiteSpace(characteristicName))
            throw new ArgumentException("Не указана характеристика.", nameof(characteristicName));
        if (difficulty < 2 || difficulty > 10)
            throw new ArgumentException(
                "Сложность должна быть в диапазоне 2..10.", nameof(difficulty));

        var characteristicNameNorm = characteristicName.Trim();
        if (!VampireParameterCatalog.IsCharacteristic(characteristicNameNorm))
            throw new ArgumentException(
                $"«{characteristicNameNorm}» не является характеристикой. " +
                "Выбери из: " + string.Join(", ", VampireParameterCatalog.Characteristics) + ".");

        if (!character.Attributes.TryGetValue(characteristicNameNorm, out var characteristicValue))
            throw new ArgumentException(
                $"Характеристика «{characteristicNameNorm}» не заполнена в листе персонажа.");

        int abilityValue = 0;
        bool hasAbility = false;
        string abilityResolvedName = "";
        string? specialization = null;
        int effectiveDifficulty = difficulty;

        if (!string.IsNullOrWhiteSpace(abilityName))
        {
            abilityResolvedName = abilityName.Trim();
            if (!character.Attributes.TryGetValue(abilityResolvedName, out abilityValue))
                throw new ArgumentException(
                    $"Навык «{abilityResolvedName}» не заполнен в листе персонажа.");
            if (!VampireParameterCatalog.IsValid(abilityResolvedName)
                || VampireParameterCatalog.IsCharacteristic(abilityResolvedName))
                throw new ArgumentException(
                    $"«{abilityResolvedName}» — не навык. " +
                    "Выбери талант, навык или знание (например, Атлетика, Скрытность, Оккультизм).");
            hasAbility = true;
            if (character.Specializations != null
                && character.Specializations.TryGetValue(abilityResolvedName, out var spec)
                && !string.IsNullOrWhiteSpace(spec))
            {
                specialization = spec.Trim();
            }
        }
        else
        {
            // V20: без навыка пул = одна характеристика, сложность +1.
            effectiveDifficulty = Math.Min(10, difficulty + 1);
        }

        // V20: ad-hoc специализация, указанная в /vampire_roll, перекрывает
        // специализацию из листа персонажа. Если у игрока есть постоянная
        // специализация, но в этом броске он бьёт по другой теме — он может
        // передать её здесь. Если не указана — используется та, что в листе.
        if (!string.IsNullOrWhiteSpace(adHocSpecialization))
        {
            specialization = adHocSpecialization.Trim();
        }

        int disciplineValue = 0;
        string? disciplineResolvedName = null;
        if (!string.IsNullOrWhiteSpace(disciplineName))
        {
            disciplineResolvedName = disciplineName.Trim();
            if (!character.Disciplines.TryGetValue(disciplineResolvedName, out disciplineValue))
                throw new ArgumentException(
                    $"Дисциплина «{disciplineResolvedName}» не изучена персонажем.");
        }

        int pool = characteristicValue + abilityValue + disciplineValue + bonusDice;
        if (pool < 1)
            throw new ArgumentException(
                $"Итоговый пул {pool} < 1 (хар-ка {characteristicValue}, навык {abilityValue}, " +
                $"дисциплина {disciplineValue}, бонус {bonusDice}).");

        var hunger = Math.Clamp(character.Hunger, 0, VampireDicePool.MaxHunger);

        return new BuildResult(
            PoolSize: pool,
            Hunger: hunger,
            Difficulty: effectiveDifficulty,
            CharacteristicName: characteristicNameNorm,
            CharacteristicValue: characteristicValue,
            AbilityName: hasAbility ? abilityResolvedName : null,
            AbilityValue: abilityValue,
            Specialization: specialization,
            DisciplineName: disciplineResolvedName,
            DisciplineValue: disciplineValue,
            BonusDice: bonusDice,
            AbilityMissingPenalty: !hasAbility);
    }

    /// <summary>
    /// Полный снапшот собранного пула для embed'а и реестра.
    /// </summary>
    public sealed record BuildResult(
        int PoolSize,
        int Hunger,
        int Difficulty,
        string CharacteristicName,
        int CharacteristicValue,
        string? AbilityName,
        int AbilityValue,
        string? Specialization,
        string? DisciplineName,
        int DisciplineValue,
        int BonusDice,
        bool AbilityMissingPenalty);
}

/// <summary>
/// Контекст активного чарника для handler'а (списание WP).
/// </summary>
public sealed record VampireActiveContext(
    RPBot.VtM.VampireStorage Storage,
    RPBot.VtM.VampireCharacter Character);
