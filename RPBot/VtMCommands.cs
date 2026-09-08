using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot;

/// <summary>
/// Обработчики slash-команд модуля VtM (Vampire: the Masquerade V20).
/// </summary>
/// <remarks>
/// <para>Сейчас обрабатываются команды привязки плееров:</para>
/// <list type="bullet">
/// <item><c>/vampire bind &lt;name&gt; [user]</c> — назначить плеера персонажу (ST only).</item>
/// <item><c>/vampire unbind &lt;name&gt;</c> — снять привязку (ST only).</item>
/// </list>
/// <para>ST-only права задаются на уровне регистрации команды
/// (<see cref="SlashCommandProperties.DefaultMemberPermissions"/>).</para>
/// <para>Сама проверка прав дублируется здесь — Discord-права могут не сработать
/// на этапе пре-регистрации у конкретных конфигураций гильдий.</para>
/// </remarks>
public sealed class VampireCommands
{
    private const string StPermission = "MANAGE_ROLES";

    /// <summary>
    /// <c>/vampire bind &lt;name&gt; [user]</c>. Только рассказчик.
    /// </summary>
    public async Task HandleBindAsync(SocketSlashCommand command)
    {
        var guildId = command.GuildId;
        if (!guildId.HasValue)
        {
            await command.RespondAsync("Команда доступна только на сервере.", ephemeral: true);
            return;
        }
        if (!await IsStorytellerAsync(command))
        {
            await command.RespondAsync("Только рассказчик может привязывать персонажей.", ephemeral: true);
            return;
        }

        var name = GetString(command, "name");
        var user = GetUser(command, "user");

        if (string.IsNullOrWhiteSpace(name))
        {
            await command.RespondAsync("Укажите имя персонажа.", ephemeral: true);
            return;
        }

        ulong targetUserId;
        if (user.HasValue)
        {
            targetUserId = user.Value.id;
        }
        else if (command.User is SocketGuildUser guildUser)
        {
            // Если @user не указан — привязываем к самому ST.
            targetUserId = guildUser.Id;
        }
        else
        {
            await command.RespondAsync("Не удалось определить целевого пользователя.", ephemeral: true);
            return;
        }

        var storage = await VampireStorageCache.GetAsync(guildId.Value);
        var result = await storage.BindAsync(name, targetUserId);
                await command.RespondAsync(embed: BindResultToEmbed(result, targetUserId), ephemeral: true);
    }

    /// <summary>
    /// <c>/vampire unbind &lt;name&gt;</c>. Только рассказчик.
    /// </summary>
    public async Task HandleUnbindAsync(SocketSlashCommand command)
    {
        var guildId = command.GuildId;
        if (!guildId.HasValue)
        {
            await command.RespondAsync("Команда доступна только на сервере.", ephemeral: true);
            return;
        }
        if (!await IsStorytellerAsync(command))
        {
            await command.RespondAsync("Только рассказчик может отвязывать персонажей.", ephemeral: true);
            return;
        }

        var name = GetString(command, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            await command.RespondAsync("Укажите имя персонажа.", ephemeral: true);
            return;
        }

        var storage = await VampireStorageCache.GetAsync(guildId.Value);
        var result = await storage.UnbindAsync(name);

        string text = result.Kind switch
        {
            VampireStorage.BindResultKind.Ok => $"✅ «{name}» отвязан. {result.Message}",
            VampireStorage.BindResultKind.NotFound => $"❌ {result.Message}",
            VampireStorage.BindResultKind.AlreadyBoundToSame => $"ℹ️ {result.Message}",
            _ => $"❓ Неизвестный исход: {result.Message}",
        };
        await command.RespondAsync(text, ephemeral: true);
    }

    // ── Хелперы ─────────────────────────────────────────────────────────

    /// <summary>
    /// <c>/vampire show &lt;name|user&gt; [where]</c>. Доступно всем, кто видит команду.
    /// DM (default): лист с кнопками уходит получателю в личку; в канале — ephemeral «отправлено».
    /// Public: лист публикуется embed'ом в текущем канале (без кнопок).
    /// </summary>
    public async Task HandleShowAsync(SocketSlashCommand command)
    {
        var guildId = command.GuildId;
        if (!guildId.HasValue)
        {
            await command.RespondAsync("Команда доступна только на сервере.", ephemeral: true);
            return;
        }

        var name = GetString(command, "name");
        var userOpt = GetUser(command, "user");
        var whereRaw = GetString(command, "where");
        var mode = string.Equals(whereRaw, "public", StringComparison.OrdinalIgnoreCase)
            ? VampireShowResolver.Mode.Public
            : VampireShowResolver.Mode.Dm;

        var mention = userOpt.HasValue && command.Channel is IGuildChannel gc
            ? $"<@{userOpt.Value.id}>"
            : null;

        var storage = await VampireStorageCache.GetAsync(guildId.Value);

        var decision = VampireShowResolver.Resolve(
            storage,
            mode,
            name,
            userOpt?.id,
            mention);

        if (decision.FailureCode != VampireShowResolver.Failure.None)
        {
            await command.RespondAsync(decision.Message, ephemeral: true);
            return;
        }

        var character = decision.Character!;
        var displayIndex = new VampireDisplayIndex(guildId.Value);
        await displayIndex.LoadAsync();

        if (mode == VampireShowResolver.Mode.Dm)
        {
            await SendSheetToDmAsync(command, character, displayIndex, guildId.Value);
        }
        else
        {
            await SendSheetPublicAsync(command, character, displayIndex, guildId.Value);
        }
    }

    private static async Task SendSheetToDmAsync(
        SocketSlashCommand command,
        VampireCharacter character,
        VampireDisplayIndex displayIndex,
        ulong guildId)
    {
        // Цель DM — если @user указан, шлём ему; иначе — вызывающему.
        ulong recipientId = command.User.Id;
        var userOpt = GetUser(command, "user");
        if (userOpt.HasValue) recipientId = userOpt.Value.id;

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild == null)
        {
            await command.RespondAsync("Не удалось получить гильдию для отправки в личку.", ephemeral: true);
            return;
        }

        var recipient = guild.GetUser(recipientId);
        if (recipient == null)
        {
            await command.RespondAsync($"<@{recipientId}> не найден на сервере.", ephemeral: true);
            return;
        }

        IUserMessage dmMessage;
        try
        {
            var dm = await recipient.CreateDMChannelAsync();
            dmMessage = await dm.SendMessageAsync(
                embed: VampireSheetEmbed.Build(character),
                components: VampireSheetComponents.Build(character, showExperienceButton: true, showFrenzyButton: true));
        }
        catch (Exception ex)
        {
            await command.RespondAsync(
                $"Не удалось отправить личное сообщение <@{recipientId}>: {ex.Message}",
                ephemeral: true);
            return;
        }

        await displayIndex.RegisterAsync(
            character.CharacterId.ToString("N"),
            dmMessage.Channel.Id,
            dmMessage.Id,
            SheetMessageKind.DmSheetWithButtons);
        await displayIndex.SaveAsync();

        await command.RespondAsync(
            $"✉️ Лист «{character.CharacterName}» отправлен в личку <@{recipientId}>.\n" +
            "Под листом кнопки Описание / Воля / Здоровье — нажатие переключает блоки.",
            ephemeral: true);
    }

    private static async Task SendSheetPublicAsync(
        SocketSlashCommand command,
        VampireCharacter character,
        VampireDisplayIndex displayIndex,
        ulong guildId)
    {
        var msg = await command.Channel.SendMessageAsync(embed: VampireSheetEmbed.Build(character));

        await displayIndex.RegisterAsync(
            character.CharacterId.ToString("N"),
            msg.Channel.Id,
            msg.Id,
            SheetMessageKind.PublicSheet);
        await displayIndex.SaveAsync();

        await command.RespondAsync($"📜 Лист «{character.CharacterName}» опубликован.", ephemeral: true);
    }

    // ── Создание персонажа (визард, шаг 1) ─────────────────────────────

    /// <summary>
    /// <c>/vampire create</c>. Доступно всем — игрок создаёт своего персонажа
    /// (или ST — NPC).
    /// </summary>
    /// <remarks>
    /// <para>В Этапе 1 реализован только шаг 1 «Концепция» (concept, clan, nature, demeanor, bio).
    /// Дальнейшие шаги (характеристики / способности / преимущества / штрихи)
    /// появятся в следующих релизах.</para>
    /// <para>Поведение:</para>
    /// <list type="bullet">
    /// <item>ephemeral-ответ в канал: «Запрос получен, начат процесс создания, перейдите в ЛС»;</item>
    /// <item>одновременно в ЛС игрока отправляется первый экран визарда (шаг 1).</item>
    /// </list>
    /// </remarks>
    public async Task HandleCreateAsync(SocketSlashCommand command)
    {
        var guildId = command.GuildId;
        if (!guildId.HasValue)
        {
            await command.RespondAsync("Команда доступна только на сервере.", ephemeral: true);
            return;
        }

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild == null)
        {
            await command.RespondAsync("Не удалось получить гильдию для запуска визарда.", ephemeral: true);
            return;
        }

        var user = guild.GetUser(command.User.Id);
        if (user == null)
        {
            await command.RespondAsync("Не удалось определить пользователя на сервере.", ephemeral: true);
            return;
        }

        // Если сессия уже есть — повторно используем её, не плодим дубликаты.
        var existing = VampireWizardRegistry.Instance.Get(guildId.Value, user.Id);
        if (existing != null)
        {
            existing.Step = VampireWizardStep.Concept;
            existing.Draft = new VampireCharacter
            {
                CharacterId = Guid.NewGuid(),
                PlayerId = user.Id,
                PlayerName = user.Username,
                Generation = 13,
                Hunger = 1,
            };
        }
        else
        {
            existing = new VampireWizardSession
            {
                Step = VampireWizardStep.Concept,
                Draft = new VampireCharacter
                {
                    CharacterId = Guid.NewGuid(),
                    PlayerId = user.Id,
                    PlayerName = user.Username,
                    Generation = 13,
                    Hunger = 1,
                },
            };
            VampireWizardRegistry.Instance.Set(guildId.Value, user.Id, existing);
        }

        // Открываем DM и рисуем первый экран.
        IDMChannel dm;
        try
        {
            dm = await user.CreateDMChannelAsync();
        }
        catch (Exception ex)
        {
            await command.RespondAsync(
                $"Не удалось открыть личные сообщения: {ex.Message}. " +
                "Разблокируйте ЛС в настройках Discord и повторите.",
                ephemeral: true);
            return;
        }

        try
        {
            await VampireWizardDmHandler.RenderConceptStepAsync(dm, existing);
        }
        catch (Exception ex)
        {
            await command.RespondAsync(
                $"Ошибка при отправке первого экрана визарда в ЛС: {ex.Message}",
                ephemeral: true);
            return;
        }

        await command.RespondAsync(
            "📨 Запрос на создание персонажа получен. Продолжение — в личных сообщениях с ботом.",
            ephemeral: true);
    }

    /// <summary>
    /// /vampire diablerie @attacker @victim gen:N hum:M [success:true|false]
    /// Справочный расчёт итогов диаблери по правилам V20.
    /// Не меняет листы — это решение рассказчика, который применяет результат вручную.
    /// </summary>

    /// <summary>
    /// <c>/vampire_send &lt;name&gt; [user]</c>. Только рассказчик.
    /// Принудительно отправляет лист с кнопками конкретному игроку в ЛС.
    /// </summary>
    public async Task HandleSendAsync(SocketSlashCommand command)
    {
        // Эквивалент HandleShowAsync с mode=Dm; если пользователь передал
        // where=public — игнорируем и шлём в ЛС (это семантика /vampire_send).
        var guildId = command.GuildId;
        if (!guildId.HasValue)
        {
            await command.RespondAsync("Команда доступна только на сервере.", ephemeral: true);
            return;
        }
        if (!await IsStorytellerAsync(command))
        {
            await command.RespondAsync("Только рассказчик может отправлять листы.", ephemeral: true);
            return;
        }

        var name = GetString(command, "name");
        var userOpt = GetUser(command, "user");
        var storage = await VampireStorageCache.GetAsync(guildId.Value);

        var decision = VampireShowResolver.Resolve(
            storage,
            VampireShowResolver.Mode.Dm,
            name,
            userOpt?.id,
            userOpt.HasValue && command.Channel is IGuildChannel gc ? $"<@{userOpt.Value.id}>" : null);

        if (decision.FailureCode != VampireShowResolver.Failure.None)
        {
            await command.RespondAsync(decision.Message, ephemeral: true);
            return;
        }

        var displayIndex = new VampireDisplayIndex(guildId.Value);
        await displayIndex.LoadAsync();
        await SendSheetToDmAsync(command, decision.Character!, displayIndex, guildId.Value);
    }

    /// <summary>
    /// <c>/vampire_show &lt;name&gt;</c>. Публичный embed без кнопок в канале.
    /// </summary>
    public async Task HandleShowPublicAsync(SocketSlashCommand command)
    {
        var guildId = command.GuildId;
        if (!guildId.HasValue)
        {
            await command.RespondAsync("Команда доступна только на сервере.", ephemeral: true);
            return;
        }

        var name = GetString(command, "name");
        var storage = await VampireStorageCache.GetAsync(guildId.Value);

        var decision = VampireShowResolver.Resolve(
            storage,
            VampireShowResolver.Mode.Public,
            name,
            null,
            null);

        if (decision.FailureCode != VampireShowResolver.Failure.None)
        {
            await command.RespondAsync(decision.Message, ephemeral: true);
            return;
        }

        var displayIndex = new VampireDisplayIndex(guildId.Value);
        await displayIndex.LoadAsync();
        await SendSheetPublicAsync(command, decision.Character!, displayIndex, guildId.Value);
    }

    public async Task HandleDiablerieAsync(SocketSlashCommand command)
    {
        var guildId = command.GuildId;
        if (!guildId.HasValue)
        {
            await command.RespondAsync("Команда доступна только на сервере.", ephemeral: true);
            return;
        }

        var attackerOpt = GetUser(command, "attacker");
        var victimOpt = GetUser(command, "victim");
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

        var genRaw = GetString(command, "gen");
        var humRaw = GetString(command, "hum");
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

        var successRaw = GetString(command, "success");
        var success = successRaw is null
            ? true
            : string.Equals(successRaw, "true", StringComparison.OrdinalIgnoreCase)
              || successRaw == "1"
              || string.Equals(successRaw, "yes", StringComparison.OrdinalIgnoreCase);

        // Поколение жертвы расчётно не знаем — пусть заменим в правиле
        // вызывающий код при следующей итерации; пока берём из листа жертвы,
        // а если нет — используем атакующего − 1 как фолбэк.
        var victimGen = await TryReadVictimGenerationAsync(guildId.Value, victimOpt.Value.id)
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

    private static async Task<int?> TryReadVictimGenerationAsync(ulong guildId, ulong victimUserId)
    {
        try
        {
            var storage = await VampireStorageCache.GetAsync(guildId);
            var character = storage.GetByPlayerId(victimUserId);
            return character?.Generation;
        }
        catch
        {
            return null;
        }
    }

    // ── Хелперы ─────────────────────────────────────────────────────────

    private static async Task<bool> IsStorytellerAsync(SocketSlashCommand command)
    {
        if (command.User is not SocketGuildUser g) return false;
        if (g.GuildPermissions.Administrator) return true;
        return g.GuildPermissions.ManageRoles;
    }

    /// <summary>
    /// Извлечь аргумент (часть после третьего «:») из CustomId формата
    /// <c>vtm_wiz:{action}:{guid}:{arg}</c>. Используется для кнопок
    /// с аргументом, обрабатываемых в общем case (например, DisciplineRename).
    /// </summary>
    private static string? ExtractCustomIdArg(string? customId)
    {
        if (string.IsNullOrEmpty(customId)) return null;
        var parts = customId.Split(new[] { ':' }, 4);
        return parts.Length >= 4 ? parts[3] : null;
    }

    private static string? GetString(SocketSlashCommand command, string key)
    {
        var opt = command.Data.Options.FirstOrDefault(o => o.Name == key);
        return opt?.Value as string;
    }

    private static (ulong id, bool found)? GetUser(SocketSlashCommand command, string key)
    {
        var opt = command.Data.Options.FirstOrDefault(o => o.Name == key);
        if (opt?.Value is not IUser user) return null;
        return (user.Id, true);
    }

    private static Embed BindResultToEmbed(VampireStorage.BindResult result, ulong requestedUserId)
    {
        var eb = new EmbedBuilder();
        switch (result.Kind)
        {
            case VampireStorage.BindResultKind.Ok:
                eb.Title = "✅ Привязка выполнена";
                eb.Description = $"«{result.Character?.CharacterName}» привязан к <@{requestedUserId}>.";
                eb.Color = Color.Green;
                break;
            case VampireStorage.BindResultKind.AlreadyBoundToSame:
                eb.Title = "ℹ️ Уже привязан";
                eb.Description = $"«{result.Character?.CharacterName}» уже привязан к этому игроку.";
                eb.Color = Color.LightGrey;
                break;
            case VampireStorage.BindResultKind.AlreadyBoundToOther:
                eb.Title = "⚠️ Другой игрок";
                eb.Description = $"«{result.Character?.CharacterName}» уже привязан к <@{result.CurrentPlayerId}>.\nСначала выполните `/vampire unbind {result.Character?.CharacterName}`.";
                eb.Color = Color.Orange;
                break;
            case VampireStorage.BindResultKind.NotFound:
                eb.Title = "❌ Не найдено";
                eb.Description = result.Message;
                eb.Color = Color.Red;
                break;
            case VampireStorage.BindResultKind.InvalidUserId:
                eb.Title = "❌ Некорректный пользователь";
                eb.Description = result.Message;
                eb.Color = Color.Red;
                break;
        }
        return eb.Build();
    }

    // ── Кнопки под листом (vtm_btn:*) ─────────────────────────────────

    /// <summary>
    /// Обработчик нажатий на кнопки <see cref="VampireSheetComponents"/>.
    /// </summary>
    public async Task HandleSheetButtonAsync(SocketMessageComponent component)
    {
        if (!VampireSheetComponents.TryParse(component.Data.CustomId, out var action, out var charId))
        {
            await component.RespondAsync("⚠️ Не удалось разобрать кнопку.", ephemeral: true);
            return;
        }

        if (!component.GuildId.HasValue)
        {
            await component.RespondAsync("Кнопки листа работают только на сервере.", ephemeral: true);
            return;
        }

        var storage = await VampireStorageCache.GetAsync(component.GuildId.Value);
        var character = storage.GetByCharacterId(charId);
        if (character == null)
        {
            await component.RespondAsync("❌ Чарник не найден в хранилище.", ephemeral: true);
            return;
        }

        switch (action)
        {
            case VampireSheetAction.Description:
            {
                var embed = VampireDescriptionEmbed.Build(character);
                if (embed == null)
                {
                    await component.RespondAsync(
                        $"ℹ️ У «{character.CharacterName}» пока нет описания (Bio) и аватара.",
                        ephemeral: true);
                }
                else
                {
                    await component.RespondAsync(embed: embed, ephemeral: true);
                }
                return;
            }
            case VampireSheetAction.Willpower:
            {
                var embed = VampireWillpowerEmbed.Build(character);
                await component.RespondAsync(embed: embed, ephemeral: true);
                return;
            }
            case VampireSheetAction.Health:
            {
                var embed = VampireHealthEmbed.Build(character);
                await component.RespondAsync(embed: embed, ephemeral: true);
                return;
            }
            case VampireSheetAction.Clan:
            {
                var embed = VampireClanEmbed.Build(character);
                if (embed == null)
                {
                    await component.RespondAsync(
                        $"ℹ️ У «{character.CharacterName}» не указан клан — нечего показать.",
                        ephemeral: true);
                }
                else
                {
                    await component.RespondAsync(embed: embed, ephemeral: true);
                }
                return;
            }
            case VampireSheetAction.Morality:
            {
                await component.RespondAsync(
                    embed: VampireMoralityEmbed.BuildEmbed(character),
                    components: VampireMoralityComponents.Build(charId),
                    ephemeral: true);
                return;
            }
            case VampireSheetAction.Experience:
            {
                await component.RespondAsync(
                    "**Опыт** — выберите действие:",
                    components: VampireExperienceComponents.Build(charId),
                    ephemeral: true);
                return;
            }
            case VampireSheetAction.Frenzy:
            {
                await component.RespondAsync(
                    embed: VampireFrenzyEmbed.Build(character),
                    components: VampireFrenzyComponents.Build(character),
                    ephemeral: true);
                return;
            }
            case VampireSheetAction.ToggleActive:
            {
                await ToggleActiveAsync(component, storage, character, charId);
                return;
            }
            default:
                await component.RespondAsync("⚠️ Неизвестное действие кнопки.", ephemeral: true);
                return;
        }
    }

    private static async Task ToggleActiveAsync(
        SocketMessageComponent component,
        VampireStorage storage,
        VampireCharacter character,
        Guid charId)
    {
        var guildId = component.GuildId!.Value;
        var playerId = component.User.Id;
        var registry = VampireActiveRegistry.Instance;

        // Проверяем, что у игрока вообще есть несколько чарников.
        var all = storage.ListAll();
        var ownedByPlayer = all
            .Where(c => c.PlayerId == playerId)
            .ToList();

        if (ownedByPlayer.Count <= 1)
        {
            await component.RespondAsync(
                "ℹ️ У вас только один персонаж — кнопка активности не нужна.",
                ephemeral: true);
            return;
        }

        var current = registry.GetActiveCharacterId(guildId, playerId);
        if (current.HasValue && current.Value == charId)
        {
            await component.RespondAsync(
                $"✅ «{character.CharacterName}» уже активен — он и так будет использоваться для бросков.",
                ephemeral: true);
            return;
        }

        registry.SetActiveCharacterId(guildId, playerId, charId);
        await component.RespondAsync(
            $"✅ «{character.CharacterName}» теперь активный. " +
            "Дальнейшие броски пойдут по нему. Переключить обратно — кнопка в листе.",
            ephemeral: true);
    }

    // ── Кнопки визарда (vtm_wiz:*) ────────────────────────────────────

    /// <summary>
    /// Обработчик нажатий на кнопки <see cref="VampireWizardComponents"/>.
    /// </summary>
    public async Task HandleWizardButtonAsync(SocketMessageComponent component)
    {
            // Если у кнопки есть arg-часть (Шаг 2: имя атрибута), пробуем TryParseWithArg.
            if (VampireWizardComponents.TryParseWithArg(
                    component.Data.CustomId, out var argAction, out var _, out var arg))
            {
                await HandleWizardButtonWithArgAsync(component, argAction, arg);
                return;
            }

            if (!VampireWizardComponents.TryParse(component.Data.CustomId, out var action, out var _))
            {
                await component.RespondAsync("⚠️ Не удалось разобрать кнопку визарда.", ephemeral: true);
                return;
            }

        // Визард живёт только в DM. Ищем сессию автора кнопки — в любой гильдии,
        // где у него есть активная сессия (для Этапа 1 — она одна).
        var session = FindActiveSessionForUser(component.User.Id);
        if (session == null)
        {
            await component.RespondAsync(
                "❌ Сессия создания персонажа не найдена. " +
                "Запустите `/vampire action:create` в канале заново.",
                ephemeral: true);
            return;
        }

        switch (action)
        {
            case VampireWizardAction.SetConcept:
                await AskAndStoreAsync(component, session, "concept",
                    "Введите **концепцию** персонажа (одной строкой, например: «циничный детектив, бывший коп»).");
                return;

            case VampireWizardAction.SetClan:
                await AskAndStoreAsync(component, session, "clan",
                    "Введите **клан** персонажа. Доступные: " +
                    string.Join(", ", VampireParameterCatalog.Clans) + ".");
                return;

            case VampireWizardAction.SetNature:
                await AskAndStoreAsync(component, session, "nature",
                    "Введите **натуру** персонажа (его истинное «я»).");
                return;

            case VampireWizardAction.SetDemeanor:
                await AskAndStoreAsync(component, session, "demeanor",
                    "Введите **маску** (Demeanor) — как персонаж выглядит для окружающих.");
                return;

            case VampireWizardAction.SetBio:
                await AskAndStoreAsync(component, session, "bio",
                    "Введите **описание** (Bio) — свободный текст, до 4000 символов. Можно пропустить.");
                return;

                        case VampireWizardAction.SetSire:
                            await AskAndStoreAsync(component, session, "sire",
                                "Введите имя **сира** (Sire) — кто обратил персонажа. Пустое сообщение = очистить.");
                            return;

                        case VampireWizardAction.ClearSire:
                            VampireCreateResolver.ApplyConceptField(session.Draft, "sire", "");
                            await RerenderWizardAsync(component, session);
                            return;

                        case VampireWizardAction.SetGeneration:
                            await AskAndStoreAsync(component, session, "generation",
                                "Введите **поколение** персонажа (число от 3 до 15; дефолт 13). Пустое сообщение = сброс к 13.");
                            return;

                        case VampireWizardAction.ResetGeneration:
                            VampireCreateResolver.ApplyConceptField(session.Draft, "generation", "13");
                            await RerenderWizardAsync(component, session);
                            return;

                        case VampireWizardAction.SkipBio:
                VampireCreateResolver.ApplyConceptField(session.Draft, "bio", "");
                await RerenderWizardAsync(component, session);
                return;

            case VampireWizardAction.ClearBio:
                VampireCreateResolver.ApplyConceptField(session.Draft, "bio", "");
                await RerenderWizardAsync(component, session);
                return;

            case VampireWizardAction.ClearConcept:
                // Сбросить concept — на случай если хочется переписать.
                VampireCreateResolver.ApplyConceptField(session.Draft, "concept", "");
                await RerenderWizardAsync(component, session);
                return;

            case VampireWizardAction.ClearClan:
                VampireCreateResolver.ApplyConceptField(session.Draft, "clan", "");
                await RerenderWizardAsync(component, session);
                return;

            case VampireWizardAction.ClearNature:
                VampireCreateResolver.ApplyConceptField(session.Draft, "nature", "");
                await RerenderWizardAsync(component, session);
                return;

            case VampireWizardAction.ClearDemeanor:
                VampireCreateResolver.ApplyConceptField(session.Draft, "demeanor", "");
                await RerenderWizardAsync(component, session);
                return;

            case VampireWizardAction.Next:
                            // Поведение «Далее» зависит от текущего шага.
                                        if (session.Step == VampireWizardStep.Abilities)
                            {
                                            if (!VampireAbilitiesResolver.IsAbilitiesComplete(session.Draft))
                                {
                                    await component.RespondAsync(
                                                    "❌ Шаг 3 ещё не завершён. Распределите все 27 пунктов по приоритету 13/9/5.",
                                        ephemeral: true);
                                    return;
                                }
                                            await CommitDraftAsync(component, session);
                                            await component.RespondAsync(
                                                "✅ Шаг 3 (способности) сохранён. Переходим к Шагу 4 (преимущества).",
                                                ephemeral: true);
                                            try
                                            {
                                                var dm = await component.User.CreateDMChannelAsync();
                                                await VampireWizardDmHandler.RenderDisciplinesStepAsync(dm, session);
                                            }
                                            catch (Exception ex)
                                            {
                                                BotLogger.Error(LogCategory.Discord, $"Ошибка при переходе 3→4: {ex.Message}");
                                            }
                                return;
                            }
                                        if (session.Step == VampireWizardStep.Attributes)
                                        {
                                            if (!VampireAttributesResolver.IsAttributesComplete(session.Draft))
                                            {
                                                await component.RespondAsync(
                                                    "❌ Шаг 2 ещё не завершён. Распределите все 15 пунктов по приоритету 7/5/3.",
                                                    ephemeral: true);
                                                return;
                                            }
                                            await component.RespondAsync(
                                                "✅ Шаг 2 (характеристики) сохранён. Переходим к Шагу 3 (способности).",
                                                ephemeral: true);
                                            await CommitDraftAsync(component, session);
                                            try
                                            {
                                                var dm = await component.User.CreateDMChannelAsync();
                                                await VampireWizardDmHandler.RenderAbilitiesStepAsync(dm, session);
                                            }
                                            catch (Exception ex)
                                            {
                                                BotLogger.Error(LogCategory.Discord, $"Ошибка при переходе 2→3: {ex.Message}");
                                            }
                                            return;
                                        }

                            // По умолчанию — Шаг 1 «Концепция».
                            if (!VampireCreateResolver.IsConceptComplete(session.Draft))
                            {
                                var d = session.Draft;
                                var missing = new List<string>();
                                if (string.IsNullOrWhiteSpace(d.Concept))  missing.Add("Амплуа");
                                if (string.IsNullOrWhiteSpace(d.Clan))     missing.Add("Клан");
                                if (string.IsNullOrWhiteSpace(d.Nature))   missing.Add("Натура");
                                if (string.IsNullOrWhiteSpace(d.Demeanor)) missing.Add("Маска");
                                await component.RespondAsync(
                                    "❌ Концепция ещё не заполнена. Не хватает: " + string.Join(", ", missing) + ".",
                                    ephemeral: true);
                                return;
                            }
                            // Переход 1 → 2: рендерим Шаг 2 в DM.
                            await component.RespondAsync("✅ Шаг 1 сохранён. Переходим к Шагу 2 (характеристики).", ephemeral: true);
                            await CommitDraftAsync(component, session);
                            try
                            {
                                var dm = await component.User.CreateDMChannelAsync();
                                await VampireWizardDmHandler.RenderAttributesStepAsync(dm, session);
                            }
                            catch (Exception ex)
                            {
                                BotLogger.Error(LogCategory.Discord, $"Ошибка при переходе 1→2: {ex.Message}");
                            }
                            return;

                        case VampireWizardAction.BackToConcept:
                            await component.RespondAsync("⬅ Возврат на Шаг 1 (Концепция).", ephemeral: true);
                            try
                            {
                                var dm = await component.User.CreateDMChannelAsync();
                                await VampireWizardDmHandler.RenderConceptStepAsync(dm, session);
                            }
                            catch (Exception ex)
                            {
                                BotLogger.Error(LogCategory.Discord, $"Ошибка при возврате 2→1: {ex.Message}");
                            }
                            return;

                                                case VampireWizardAction.BackToAttributes:
                                                    await component.RespondAsync("⬅ Возврат на Шаг 2 (характеристики).", ephemeral: true);
                                                    try
                                                    {
                                                        var dm = await component.User.CreateDMChannelAsync();
                                                        await VampireWizardDmHandler.RenderAttributesStepAsync(dm, session);
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        BotLogger.Error(LogCategory.Discord, $"Ошибка при возврате 3→2: {ex.Message}");
                                                    }
                                                    return;

                                                case VampireWizardAction.ResetAbilityProgress:
                                                    VampireAbilitiesResolver.ResetProgress(session.Draft);
                                                    await RerenderWizardAsync(component, session);
                                                    return;

                                                case VampireWizardAction.ResetAbilityAll:
                                                    VampireAbilitiesResolver.ResetAll(session.Draft);
                                                    await RerenderWizardAsync(component, session);
                                                    return;

                                                case VampireWizardAction.AbilityPriority:
                                                case VampireWizardAction.AbilityInc:
                                                case VampireWizardAction.AbilityDec:
                                                    {
                                                        // Эти actions несут аргументы в customId-arg и обрабатываются
                                                        // отдельным entry-point (HandleWizardSelectMenuWithArgAsync).
                                                        await component.RespondAsync("⚠️ Внутренняя ошибка визарда (ability без аргумента).", ephemeral: true);
                                                        return;
                                                    }

                                                // ── Шаг 4 «Преимущества»: кнопки (часть — обрабатывается через selectmenu) ──

                                                case VampireWizardAction.DisciplineInc:
                                                case VampireWizardAction.DisciplineDec:
                                                case VampireWizardAction.BackgroundInc:
                                                case VampireWizardAction.BackgroundDec:
                                                case VampireWizardAction.BackgroundRemove:
                                                case VampireWizardAction.BackgroundRename:
                                                case VampireWizardAction.VirtueInc:
                                                case VampireWizardAction.VirtueDec:
                                                    {
                                                        // Эти actions несут аргументы и обрабатываются отдельным selectmenu-entry-point.
                                                        await component.RespondAsync("⚠️ Внутренняя ошибка визарда (adv без аргумента).", ephemeral: true);
                                                        return;
                                                    }

                                                case VampireWizardAction.BackgroundAdd:
                                                    {
                                                        // Открываем текстовый ввод для имени нового факта.
                                                        session.PendingBackgroundOp = "add";
                                                        await component.RespondAsync(
                                                            "✏️ Введите имя нового факта биографии (например, `Стая`, `Ресурсы`, `Союзники 3`).",
                                                            ephemeral: true);
                                                        return;
                                                    }

                                                case VampireWizardAction.DisciplineRename:
                                                    {
                                                        // Открываем текстовый ввод для нового имени дисциплины Каитифа.
                                                        if (!VampireAdvantagesCatalog.IsCaitiff(session.Draft.Clan))
                                                        {
                                                            await component.RespondAsync(
                                                                "⚠️ Переименовывать можно только дисциплины Каитифа.",
                                                                ephemeral: true);
                                                            return;
                                                        }
                                                        var disciplineName = ExtractCustomIdArg(component.Data.CustomId);
                                                        if (string.IsNullOrWhiteSpace(disciplineName))
                                                        {
                                                            await component.RespondAsync(
                                                                "⚠️ Не указано имя переименовываемой дисциплины.",
                                                                ephemeral: true);
                                                            return;
                                                        }
                                                        session.PendingDisciplineRename = disciplineName;
                                                        await component.RespondAsync(
                                                            $"✏️ Введите новое имя для «{disciplineName}» (например, `Анимализм`, `Прорицание`, `Воздействие`).",
                                                            ephemeral: true);
                                                        return;
                                                    }

                                                case VampireWizardAction.ResetAdvProgress:
                                                case VampireWizardAction.ResetAdvAll:
                                                    {
                                                        if (session.Step != VampireWizardStep.Advantages)
                                                        {
                                                            await component.RespondAsync("⚠️ Сброс доступен только на Шаге 4.", ephemeral: true);
                                                            return;
                                                        }
                                                        VampireAdvantagesResolver.ResetProgress(session.Draft);
                                                        await RerenderAdvantagesAsync(component, session);
                                                        return;
                                                    }

                                                case VampireWizardAction.BackToAbilities:
                                                    {
                                                        await component.RespondAsync("⬅ Возврат на Шаг 3 (способности).", ephemeral: true);
                                                        try
                                                        {
                                                            var dm = await component.User.CreateDMChannelAsync();
                                                            await VampireWizardDmHandler.RenderAbilitiesStepAsync(dm, session);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            BotLogger.Error(LogCategory.Discord, $"Ошибка при возврате 4→3: {ex.Message}");
                                                        }
                                                        return;
                                                    }

                                                case VampireWizardAction.NextAdvToBackgrounds:
                                                    {
                                                        if (session.Step != VampireWizardStep.Advantages
                                                            || session.AdvantagesSubStep != VampireWizardAdvantagesSubStep.Disciplines)
                                                        {
                                                            await component.RespondAsync("⚠️ Этот переход доступен только с Шага 4.1.", ephemeral: true);
                                                            return;
                                                        }
                                                        if (!VampireAdvantagesResolver.IsDisciplinesComplete(session.Draft))
                                                        {
                                                            await component.RespondAsync(
                                                                $"❌ Шаг 4.1 ещё не завершён. Осталось {VampireAdvantagesResolver.RemainingDisciplinePool(session.Draft)} очков дисциплин.",
                                                                ephemeral: true);
                                                            return;
                                                        }
                                                        await CommitDraftAsync(component, session);
                                                        await component.RespondAsync("✅ Шаг 4.1 (дисциплины) сохранён. Переходим к Шагу 4.2 (факты).", ephemeral: true);
                                                        try
                                                        {
                                                            var dm = await component.User.CreateDMChannelAsync();
                                                            await VampireWizardDmHandler.RenderBackgroundsStepAsync(dm, session);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            BotLogger.Error(LogCategory.Discord, $"Ошибка при переходе 4.1→4.2: {ex.Message}");
                                                        }
                                                        return;
                                                    }

                                                case VampireWizardAction.NextAdvToVirtues:
                                                    {
                                                        if (session.Step != VampireWizardStep.Advantages
                                                            || session.AdvantagesSubStep != VampireWizardAdvantagesSubStep.Backgrounds)
                                                        {
                                                            await component.RespondAsync("⚠️ Этот переход доступен только с Шага 4.2.", ephemeral: true);
                                                            return;
                                                        }
                                                        if (!VampireAdvantagesResolver.IsBackgroundsComplete(session.Draft))
                                                        {
                                                            await component.RespondAsync(
                                                                $"❌ Шаг 4.2 ещё не завершён. Осталось {VampireAdvantagesResolver.RemainingBackgroundPool(session.Draft)} очков фактов.",
                                                                ephemeral: true);
                                                            return;
                                                        }
                                                        await CommitDraftAsync(component, session);
                                                        await component.RespondAsync("✅ Шаг 4.2 (факты) сохранён. Переходим к Шагу 4.3 (добродетели).", ephemeral: true);
                                                        try
                                                        {
                                                            var dm = await component.User.CreateDMChannelAsync();
                                                            await VampireWizardDmHandler.RenderVirtuesStepAsync(dm, session);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            BotLogger.Error(LogCategory.Discord, $"Ошибка при переходе 4.2→4.3: {ex.Message}");
                                                        }
                                                        return;
                                                    }

                                                case VampireWizardAction.NextAdvToFinishing:
                                                    {
                                                        if (session.Step != VampireWizardStep.Advantages
                                                            || session.AdvantagesSubStep != VampireWizardAdvantagesSubStep.Virtues)
                                                        {
                                                            await component.RespondAsync("⚠️ Этот переход доступен только с Шага 4.3.", ephemeral: true);
                                                            return;
                                                        }
                                                        if (!VampireAdvantagesResolver.IsVirtuesComplete(session.Draft))
                                                        {
                                                            await component.RespondAsync(
                                                                $"❌ Шаг 4.3 ещё не завершён. Осталось {VampireAdvantagesResolver.RemainingVirtuePool(session.Draft)} очков добродетелей.",
                                                                ephemeral: true);
                                                            return;
                                                        }
                                                        await CommitDraftAsync(component, session);

                                                        try
                                                        {
                                                            await component.DeferAsync(ephemeral: true);
                                                            var dm = await component.User.CreateDMChannelAsync();
                                                            await VampireWizardDmHandler.RenderFinishingStepAsync(dm, session);
                                                            await component.FollowupAsync("✅ Шаг 4 сохранён. Открыт Шаг 5 «Последние штрихи».", ephemeral: true);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            BotLogger.Error(LogCategory.Discord, $"Ошибка при переходе 4.3→5: {ex.Message}");
                                                            await component.FollowupAsync("❌ Не удалось открыть Шаг 5.", ephemeral: true);
                                                        }
                                                        return;
                                                    }

                                                case VampireWizardAction.FinishingReset:
                                                    {
                                                        if (session.Step != VampireWizardStep.FinishingTouches)
                                                        {
                                                            await component.RespondAsync("⚠️ Сброс Шага 5 доступен только из Шага 5.", ephemeral: true);
                                                            return;
                                                        }
                                                        VampireFinishingResolver.ResetFreebies(session.Draft);
                                                        await CommitDraftAsync(component, session);

                                                        try
                                                        {
                                                            var dm = await component.User.CreateDMChannelAsync();
                                                            await VampireWizardDmHandler.RenderFinishingStepAsync(dm, session);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            BotLogger.Error(LogCategory.Discord, $"Ошибка при сбросе Шага 5: {ex.Message}");
                                                        }
                                                        await component.RespondAsync("🧹 Свободные пункты возвращены в пул (15/15).", ephemeral: true);
                                                        return;
                                                    }

                                                case VampireWizardAction.FinishingDone:
                                                    {
                                                        if (session.Step != VampireWizardStep.FinishingTouches)
                                                        {
                                                            await component.RespondAsync("⚠️ Завершение Шага 5 доступно только из Шага 5.", ephemeral: true);
                                                            return;
                                                        }
                                                        await CommitDraftAsync(component, session);
                                                        await component.RespondAsync(
                                                            "✅ Шаг 5 сохранён. Финальный лист персонажа появится в Шаге 6 (следующее обновление).",
                                                            ephemeral: true);
                                                        return;
                                                    }

                                                case VampireWizardAction.FinishingFinalize:
                                                    {
                                                        if (session.Step != VampireWizardStep.FinishingTouches)
                                                        {
                                                            await component.RespondAsync("⚠️ Подтверждение Шага 5 доступно только из Шага 5.", ephemeral: true);
                                                            return;
                                                        }

                                                        // Подтверждаем, что игрок добровольно завершает Шаг 5 с непустым пулом.
                                                        // После этого специализации разрешены, но снять отметку можно только повторным
                                                        // переходом на эту страницу — поэтому явно делаем reset-флажка быть не должно.
                                                        VampireFinishingResolver.ConfirmStep5(session.Draft);
                                                        await CommitDraftAsync(component, session);

                                                        try
                                                        {
                                                            var dm = await component.User.CreateDMChannelAsync();
                                                            await VampireWizardDmHandler.RenderFinishingStepAsync(dm, session);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            BotLogger.Error(LogCategory.Discord, $"Ошибка при подтверждении Шага 5: {ex.Message}");
                                                        }
                                                        await component.RespondAsync(
                                                            "⚠ Шаг 5 заморожен. Свободные пункты больше нельзя тратить; специализации теперь доступны.",
                                                            ephemeral: true);
                                                        return;
                                                    }

                                                case VampireWizardAction.BackToAdvantages:
                                                    {
                                                        if (session.Step != VampireWizardStep.FinishingTouches)
                                                        {
                                                            await component.RespondAsync("⚠️ Возврат к 4.3 доступен только из Шага 5.", ephemeral: true);
                                                            return;
                                                        }
                                                        await CommitDraftAsync(component, session);

                                                        try
                                                        {
                                                            var dm = await component.User.CreateDMChannelAsync();
                                                            await VampireWizardDmHandler.RenderVirtuesStepAsync(dm, session);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            BotLogger.Error(LogCategory.Discord, $"Ошибка при возврате 5→4.3: {ex.Message}");
                                                        }
                                                        await component.RespondAsync("↩️ Возврат к Шагу 4.3 (добродетели).", ephemeral: true);
                                                        return;
                                                    }

                                                case VampireWizardAction.FinishingInc:
                                                    {
                                                        // Шаг 5 — SelectMenu; основная обработка идёт в HandleWizardSelectAsync.
                                                        await component.RespondAsync("⚠️ Шаг 5 работает через меню выбора, а не через кнопки.", ephemeral: true);
                                                        return;
                                                    }

                        case VampireWizardAction.ResetAttrProgress:
                            VampireAttributesResolver.ResetProgress(session.Draft);
                            await RerenderWizardAsync(component, session);
                            return;

                        case VampireWizardAction.ResetAttrAll:
                            VampireAttributesResolver.ResetAll(session.Draft);
                            await RerenderWizardAsync(component, session);
                            return;

                        case VampireWizardAction.AttrInc:
                        case VampireWizardAction.AttrDec:
                            {
                                // Эти actions на Шаге 2 несут имя атрибута в customId-arg.
                                // Парсинг и обработка делаются через отдельный entry-point из HandleWizardButtonWithArgAsync.
                                await component.RespondAsync("⚠️ Внутренняя ошибка визарда (attr без аргумента).", ephemeral: true);
                                return;
                            }

                        case VampireWizardAction.Cancel:
                VampireWizardRegistry.Instance.RemoveByUser(component.User.Id);
                try
                {
                    var dm = await component.User.CreateDMChannelAsync();
                    var msg = await dm.GetMessageAsync(session.DmMessageId ?? 0);
                    if (msg is IUserMessage um)
                    {
                        await um.ModifyAsync(m =>
                        {
                            m.Content = VampireWizardDmHandler.BuildCancelledMessage();
                            m.Components = null;
                        });
                    }
                }
                catch { /* swallow — главное что сессия снята */ }
                await component.RespondAsync("❌ Визард отменён.", ephemeral: true);
                return;

            default:
                await component.RespondAsync("⚠️ Неизвестное действие визарда.", ephemeral: true);
                return;
        }
    }

                    /// <summary>
                    /// Обработка кнопок визарда с дополнительным аргументом (Шаг 2 «Характеристики»).
                    /// Поддерживает attr_inc / attr_dec с именем атрибута в arg.
                    /// </summary>
                    private async Task HandleWizardButtonWithArgAsync(
                        SocketMessageComponent component,
                        VampireWizardAction action,
                        string arg)
                    {
                        var session = FindActiveSessionForUser(component.User.Id);
                        if (session == null)
                        {
                            await component.RespondAsync(
                                "❌ Сессия создания персонажа не найдена. " +
                                "Запустите `/vampire action:create` в канале заново.",
                                ephemeral: true);
                            return;
                        }

                        if (session.Step != VampireWizardStep.Attributes)
                        {
                            await component.RespondAsync(
                                "⚠️ Эта кнопка доступна только на Шаге 2 (характеристики). " +
                                $"Текущий шаг: {session.Step}.",
                                ephemeral: true);
                            return;
                        }

                        switch (action)
                        {
                            case VampireWizardAction.AttrInc:
                                {
                                    var dec = VampireAttributesResolver.Increment(session.Draft, arg);
                                    if (!dec.IsSuccess)
                                    {
                                        await component.RespondAsync("❌ " + dec.Message, ephemeral: true);
                                        return;
                                    }
                                    await RerenderWizardAsync(component, session);
                                    return;
                                }
                            case VampireWizardAction.AttrDec:
                                {
                                    var dec = VampireAttributesResolver.Decrement(session.Draft, arg);
                                    if (!dec.IsSuccess)
                                    {
                                        await component.RespondAsync("❌ " + dec.Message, ephemeral: true);
                                        return;
                                    }
                                    await RerenderWizardAsync(component, session);
                                    return;
                                }
                            default:
                                await component.RespondAsync("⚠️ Неизвестное действие с аргументом: " + action, ephemeral: true);
                                return;
                        }
                    }

    private static async Task AskAndStoreAsync(
        SocketMessageComponent component,
        VampireWizardSession session,
        string field,
        string prompt)
    {
        session.PendingField = field;
        await component.RespondAsync(prompt + "\n\n_(Ответьте текстом в этом же ЛС — я подставлю значение в draft.)_",
            ephemeral: true);
    }

    private static async Task RerenderWizardAsync(
        SocketMessageComponent component,
        VampireWizardSession session)
    {
        if (session.DmMessageId == null) return;
        try
        {
            var dm = await component.User.CreateDMChannelAsync();
            var msg = await dm.GetMessageAsync(session.DmMessageId.Value);
            if (msg is IUserMessage um)
            {
                    if (session.Step == VampireWizardStep.Attributes)
                    {
                        var text = VampireAttributesResolver.BuildAttributesStatusMessage(session.Draft);
                        var components = VampireWizardComponents.BuildForAttributesStep(session.Draft);
                        await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
                    }
                    else if (session.Step == VampireWizardStep.Abilities)
                    {
                        var text = VampireAbilitiesResolver.BuildAbilitiesStatusMessage(session.Draft);
                        var components = VampireWizardComponents.BuildForAbilitiesStep(session.Draft);
                        await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
                    }
                    else if (session.Step == VampireWizardStep.Advantages)
                    {
                        await RerenderAdvantagesInternal(um, session);
                    }
                    else
                    {
                        var text = VampireCreateResolver.BuildConceptStatusMessage(session.Draft);
                        var components = VampireWizardComponents.BuildForConceptStep(session.Draft);
                        await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
                    }
                }
            }
            catch { /* не критично */ }
            await component.RespondAsync("✅ Обновлено.", ephemeral: true);
        }

        /// <summary>
        /// Перерисовать текущий под-шаг Шага 4 в DM.
        /// </summary>
        private static async Task RerenderAdvantagesAsync(
            SocketMessageComponent component,
            VampireWizardSession session)
        {
            if (session.DmMessageId == null) return;
            try
            {
                var dm = await component.User.CreateDMChannelAsync();
                var msg = await dm.GetMessageAsync(session.DmMessageId.Value);
                if (msg is IUserMessage um)
                {
                    await RerenderAdvantagesInternal(um, session);
                }
            }
            catch { /* не критично */ }
            await component.RespondAsync("✅ Обновлено.", ephemeral: true);
        }

        private static async Task RerenderAdvantagesInternal(IUserMessage um, VampireWizardSession session)
        {
            switch (session.AdvantagesSubStep)
            {
                case VampireWizardAdvantagesSubStep.Disciplines:
                    {
                        var text = VampireAdvantagesResolver.BuildDisciplinesStatusMessage(session.Draft);
                        var components = VampireWizardComponents.BuildForDisciplinesStep(session.Draft);
                        await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
                        return;
                    }
                case VampireWizardAdvantagesSubStep.Backgrounds:
                    {
                        var text = VampireAdvantagesResolver.BuildBackgroundsStatusMessage(session.Draft);
                        var components = VampireWizardComponents.BuildForBackgroundsStep(session.Draft);
                        await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
                        return;
                    }
                case VampireWizardAdvantagesSubStep.Virtues:
                    {
                        var text = VampireAdvantagesResolver.BuildVirtuesStatusMessage(session.Draft);
                        var components = VampireWizardComponents.BuildForVirtuesStep(session.Draft);
                        await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
                        return;
                    }
            }
        }

    private async Task CommitDraftAsync(SocketMessageComponent component, VampireWizardSession session)
    {
        if (!component.GuildId.HasValue) return;
        var storage = await VampireStorageCache.GetAsync(component.GuildId.Value);
        await storage.UpsertDraftAsync(session.Draft);

        // Если у игрока ещё нет активного — ставим первый черновик активным.
        var registry = VampireActiveRegistry.Instance;
        var existing = registry.GetActiveCharacterId(component.GuildId.Value, component.User.Id);
        if (!existing.HasValue)
        {
            registry.SetActiveCharacterId(component.GuildId.Value, component.User.Id, session.Draft.CharacterId);
        }
    }

    private static VampireWizardSession? FindActiveSessionForUser(ulong userId)
    {
        // На Этапе 1 у пользователя одна сессия; ищем её.
        return VampireWizardRegistry.Instance.GetByUser(userId);
    }

    /// <summary>
    /// Обработка нажатия на кнопки Grant/Spend опыта (Roadmap #34).
    /// Открывает модалку с полем «Количество».
    /// </summary>
    public async Task HandleExperienceButtonAsync(SocketMessageComponent component)
    {
        if (!VampireExperienceComponents.TryParse(component.Data.CustomId, out var action, out var charId))
        {
            await component.RespondAsync("⚠️ Не удалось разобрать кнопку опыта.", ephemeral: true);
            return;
        }
        if (!component.GuildId.HasValue)
        {
            await component.RespondAsync("Кнопки опыта работают только на сервере.", ephemeral: true);
            return;
        }
        var storage = await VampireStorageCache.GetAsync(component.GuildId.Value);
        var character = storage.GetByCharacterId(charId);
        if (character == null)
        {
            await component.RespondAsync("❌ Чарник не найден.", ephemeral: true);
            return;
        }
        if (character.PlayerId != component.User.Id)
        {
            await component.RespondAsync("⚠️ Только владелец чарника может изменять опыт.", ephemeral: true);
            return;
        }
        await component.RespondWithModalAsync(VampireExperienceModal.Build(action, charId));
    }

    /// <summary>
    /// Обработка сабмита модалки опыта: применяет дельту к персонажу и сохраняет.
    /// </summary>
    public async Task HandleExperienceModalAsync(SocketModal modal)
    {
        if (!VampireExperienceModal.TryParse(modal.Data.CustomId, out var action, out var charId))
        {
            await modal.RespondAsync("⚠️ Не удалось разобрать модалку опыта.", ephemeral: true);
            return;
        }

        string raw = string.Empty;
        foreach (var comp in modal.Data.Components)
        {
            if (string.Equals(comp.CustomId, VampireExperienceModal.AmountFieldId, StringComparison.OrdinalIgnoreCase))
                raw = comp.Value ?? string.Empty;
        }
        if (!VampireExperienceModal.TryParseAmount(raw, out var amount))
        {
            await modal.RespondAsync("⚠️ Количество опыта должно быть положительным целым.", ephemeral: true);
            return;
        }

        var guildId = modal.GuildId ?? (modal.User as SocketGuildUser)?.Guild.Id;
        if (!guildId.HasValue)
        {
            await modal.RespondAsync("Модалка доступна только на сервере.", ephemeral: true);
            return;
        }
        var storage = await VampireStorageCache.GetAsync(guildId.Value);
        var character = storage.GetByCharacterId(charId);
        if (character == null)
        {
            await modal.RespondAsync("❌ Чарник не найден.", ephemeral: true);
            return;
        }
        if (character.PlayerId != modal.User.Id)
        {
            await modal.RespondAsync("⚠️ Только владелец чарника может изменять опыт.", ephemeral: true);
            return;
        }

        string msg;
        if (action == ExperienceModalAction.Grant)
        {
            character.ExperienceCurrent += amount;
            character.ExperienceTotal   += amount;
            msg = $"✅ Начислено **{amount}** опыта. Текущий: **{character.ExperienceCurrent}**.";
        }
        else
        {
            if (character.ExperienceCurrent < amount)
            {
                await modal.RespondAsync(
                    $"⚠️ Недостаточно опыта: доступно {character.ExperienceCurrent}, нужно {amount}.",
                    ephemeral: true);
                return;
            }
            character.ExperienceCurrent -= amount;
            msg = $"✅ Потрачено **{amount}** опыта. Остаток: **{character.ExperienceCurrent}**.";
        }

        await storage.UpsertAsync(character);

        await modal.RespondAsync(msg, ephemeral: true);
    }

    /// <summary>
    /// Обработка нажатий кнопок блока «Мораль» (Roadmap #37):
    /// Add — открывает меню расстройств; Remove — убирает последнее; Close — закрывает.
    /// </summary>
    public async Task HandleMoralityButtonAsync(SocketMessageComponent component)
    {
        if (!VampireMoralityComponents.TryParse(component.Data.CustomId, out var action, out var charId))
        {
            await component.RespondAsync("⚠️ Не удалось разобрать кнопку морали.", ephemeral: true);
            return;
        }
        if (!component.GuildId.HasValue)
        {
            await component.RespondAsync("Кнопки морали работают только на сервере.", ephemeral: true);
            return;
        }
        var storage = await VampireStorageCache.GetAsync(component.GuildId.Value);
        var character = storage.GetByCharacterId(charId);
        if (character == null)
        {
            await component.RespondAsync("❌ Чарник не найден.", ephemeral: true);
            return;
        }
        if (character.PlayerId != component.User.Id)
        {
            await component.RespondAsync("⚠️ Только владелец чарника может изменять мораль.", ephemeral: true);
            return;
        }

        switch (action)
        {
            case MoralityAction.RemoveLastDerangement:
                if (character.Derangements == null || character.Derangements.Count == 0)
                {
                    await component.RespondAsync("ℹ️ Список расстройств пуст.", ephemeral: true);
                    return;
                }
                var removed = character.Derangements[^1];
                character.Derangements.RemoveAt(character.Derangements.Count - 1);
                await storage.UpsertAsync(character);
                await component.RespondAsync($"✅ Удалено расстройство «{removed}».",
                    components: VampireMoralityComponents.Build(charId), ephemeral: true);
                return;

            case MoralityAction.StartAddDerangement:
                await component.RespondAsync(
                    "Выберите расстройство для добавления:",
                    components: VampireMoralityComponents.Build(charId, withDerangementMenu: true),
                    ephemeral: true);
                return;

            case MoralityAction.ConscienceCheck:
                await HandleConscienceCheckAsync(component, storage, character);
                return;
        }
    }

    /// <summary>
    /// Обработка нажатия «Проверка совести» (Roadmap #37, V20 стр. 333).
    /// Бросает пул по текущей Человечности/Пути, интерпретирует результат и применяет потери.
    /// </summary>
    private async Task HandleConscienceCheckAsync(
        SocketMessageComponent component,
        VampireStorage storage,
        VampireCharacter character)
    {
        var currentHumanity = VampireFinishingResolver.ComputeHumanity(character);
        var poolSize = VampireConscienceResolver.ConscienceDicePool(character, currentHumanity);

        if (poolSize < 1)
        {
            await component.RespondAsync(
                "ℹ️ Пул проверки совести равен 0 (Человечность не задана). Бросок не требуется.",
                ephemeral: true);
            return;
        }

        // Бросок V20-пула. Используем тот же IRandom, что и frenzy — SystemRandomAdapter.
        var rng = new SystemRandomAdapter();
        var roll = VampireDicePool.RollV20(poolSize, rng);

        // Интерпретация по правилам проверки совести (см. VampireConscienceResolver).
        var conscienceResult = VampireConscienceResolver.Roll(roll.Dice);
        var apply = VampireConscienceResolver.Apply(conscienceResult.Outcome);

        // Применяем потери к персонажу. Возвращает фактические изменения (с учётом границ).
        var applied = VampireMoralityResolver.ApplyConscience(character, apply);
        await storage.UpsertAsync(character);

        // Собираем embed-ответ.
        var embed = BuildConscienceCheckEmbed(character, poolSize, roll, conscienceResult, apply, applied);
        await component.RespondAsync(embed: embed, ephemeral: true);
    }

    /// <summary>
    /// Собрать embed с результатом проверки совести: пул, кубики, успехи, исход,
    /// фактически применённые потери и итоговые значения Humanity/Conscience.
    /// </summary>
    private static Embed BuildConscienceCheckEmbed(
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

        // Заголовок блока: цвет по исходу.
        string outcomeTitle = conscience.Outcome switch
        {
            VampireConscienceResolver.ConscienceRollOutcome.Success => "🟢 Успех",
            VampireConscienceResolver.ConscienceRollOutcome.Failure => "🟡 Неудача",
            VampireConscienceResolver.ConscienceRollOutcome.Botch   => "🔴 Провал",
            _ => "❔",
        };
        sb.Append('\n').Append("**").Append(outcomeTitle).Append(":** ")
          .Append(VampireConscienceResolver.Describe(conscience.Outcome)).Append('\n');

        // Что применилось.
        // Для Человечности показываем суммарное изменение (new − old), потому что при ботче
        // падение Conscience тоже даёт −1 к Чел. через формулу.
        if (applied.OldHumanity != applied.NewHumanity)
        {
            var totalDelta = applied.NewHumanity - applied.OldHumanity;
            sb.Append("📉 **Человечность:** ")
              .Append(applied.OldHumanity).Append(" → ").Append(applied.NewHumanity)
              .Append(" (").Append(totalDelta).Append(")\n");
            // Пояснение о структуре потерь.
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

        // Предупреждение о границе.
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
    /// Обработка выбора расстройства в SelectMenu блока «Мораль» (Roadmap #37).
    /// </summary>
    public async Task HandleMoralitySelectAsync(SocketMessageComponent component)
    {
        var charId = VampireMoralityComponents.TryParseSelectedMenu(component.Data.CustomId);
        if (charId == null)
        {
            await component.RespondAsync("⚠️ Не удалось разобрать выбор расстройства.", ephemeral: true);
            return;
        }
        if (!component.GuildId.HasValue)
        {
            await component.RespondAsync("Мораль работает только на сервере.", ephemeral: true);
            return;
        }
        var storage = await VampireStorageCache.GetAsync(component.GuildId.Value);
        var character = storage.GetByCharacterId(charId.Value);
        if (character == null)
        {
            await component.RespondAsync("❌ Чарник не найден.", ephemeral: true);
            return;
        }
        if (character.PlayerId != component.User.Id)
        {
            await component.RespondAsync("⚠️ Только владелец чарника может менять мораль.", ephemeral: true);
            return;
        }
        var values = component.Data.Values;
        if (values == null || values.Count == 0)
        {
            await component.RespondAsync("ℹ️ Ничего не выбрано.", ephemeral: true);
            return;
        }
        var picked = values.First();
        if (!VampireDerangementCatalog.IsKnown(picked))
        {
            await component.RespondAsync("⚠️ Неизвестное расстройство.", ephemeral: true);
            return;
        }
        character.Derangements ??= new System.Collections.Generic.List<string>();
        if (character.Derangements.Contains(picked))
        {
            await component.RespondAsync($"ℹ️ «{picked}» уже в списке расстройств.", ephemeral: true);
            return;
        }
        character.Derangements.Add(picked);
        await storage.UpsertAsync(character);
        await component.RespondAsync($"✅ Добавлено расстройство «{picked}».",
            components: VampireMoralityComponents.Build(charId.Value), ephemeral: true);
    }

    /// <summary>
    /// Обработка кнопок блока «Воля» (vtm_will:*).
    /// Потратить 1 пункт воли (spend) или восстановить 1 (restore).
    /// V20 стр. 116: 1 пункт воли = +1 к одному повторному броску, либо
    /// автоматический успех при сопротивлении ярости/ротшреку, либо
    /// «игнорирование повреждений» (бросок куба воли на каждое отменяемое).
    /// </summary>
    /// <remarks>
    /// <para>Текущий шаг — только обновляем запас пунктов воли. Бросок куба
    /// воли для «игнорирования повреждений» будет добавлен отдельной фичей.</para>
    /// </remarks>
    public async Task HandleWillpowerButtonAsync(SocketMessageComponent component)
    {
        if (!VampireWillpowerComponents.TryParse(component.Data.CustomId, out var action, out var charId))
        {
            await component.RespondAsync("⚠️ Не удалось разобрать кнопку воли.", ephemeral: true);
            return;
        }
        if (!component.GuildId.HasValue)
        {
            await component.RespondAsync("Кнопки воли работают только на сервере.", ephemeral: true);
            return;
        }
        var storage = await VampireStorageCache.GetAsync(component.GuildId.Value);
        var character = storage.GetByCharacterId(charId);
        if (character == null)
        {
            await component.RespondAsync("❌ Чарник не найден.", ephemeral: true);
            return;
        }
        if (character.PlayerId != component.User.Id)
        {
            await component.RespondAsync("⚠️ Только владелец чарника может менять волю.", ephemeral: true);
            return;
        }

#pragma warning disable CS0618 // WillpowerPoints устарело для листа, но используется runtime-кнопкой.
        character.EnsureWillpowerPointsValid();
        var ceiling = character.Willpower;
        var current = character.WillpowerPoints;

        switch (action)
        {
            case WillpowerAction.SpendOne:
                if (current <= 0)
                {
                    await component.RespondAsync("ℹ️ Нечего тратить — запас воли пуст.", ephemeral: true);
                    return;
                }
                if (character.WillpowerSpentThisTurn)
                {
                    await component.RespondAsync(
                        "⚠️ Уже использовался «Сопротивление» в этом ходу (V20: один раз за ход).",
                        ephemeral: true);
                    return;
                }
                character.WillpowerPoints = current - 1;
                character.WillpowerSpentThisTurn = true;
                await storage.UpsertAsync(character);
                await component.RespondAsync(
                    $"✅ Потрачен 1 пункт воли. Остаток: **{character.WillpowerPoints}** / {ceiling}.",
                    components: VampireWillpowerComponents.Build(charId),
                    ephemeral: true);
                return;

            case WillpowerAction.RestoreOne:
                if (current >= ceiling)
                {
                    await component.RespondAsync(
                        $"ℹ️ Запас воли уже полный: {current} / {ceiling}.",
                        ephemeral: true);
                    return;
                }
                character.WillpowerPoints = current + 1;
                await storage.UpsertAsync(character);
                await component.RespondAsync(
                    $"✅ Восстановлен 1 пункт воли. Запас: **{character.WillpowerPoints}** / {ceiling}.",
                    components: VampireWillpowerComponents.Build(charId),
                    ephemeral: true);
                return;

            default:
                await component.RespondAsync("⚠️ Неизвестное действие воли.", ephemeral: true);
                return;
        }
#pragma warning restore CS0618
    }

    /// <summary>
    /// Обработка кнопок блока «Здоровье» (vtm_health:*).
    /// Нанести нелетальный / летальный / агравированный урон (+1 ячейка)
    /// или вылечить 1 ячейку справа. V20 стр. 92.
    /// </summary>
    public async Task HandleHealthButtonAsync(SocketMessageComponent component)
    {
        if (!VampireHealthComponents.TryParse(component.Data.CustomId, out var action, out var charId))
        {
            await component.RespondAsync("⚠️ Не удалось разобрать кнопку здоровья.", ephemeral: true);
            return;
        }
        if (!component.GuildId.HasValue)
        {
            await component.RespondAsync("Кнопки здоровья работают только на сервере.", ephemeral: true);
            return;
        }
        var storage = await VampireStorageCache.GetAsync(component.GuildId.Value);
        var character = storage.GetByCharacterId(charId);
        if (character == null)
        {
            await component.RespondAsync("❌ Чарник не найден.", ephemeral: true);
            return;
        }
        if (character.PlayerId != component.User.Id)
        {
            await component.RespondAsync("⚠️ Только владелец чарника может менять здоровье.", ephemeral: true);
            return;
        }

        character.Health ??= new HealthState(7);
        var h = character.Health;

        switch (action)
        {
            case HealthAction.ApplyNonLethal:
                if (h.IsDead)
                {
                    await component.RespondAsync("☠️ Персонаж мёртв.", ephemeral: true);
                    return;
                }
                h.ApplyNonLethal(1);
                break;
            case HealthAction.ApplyLethal:
                if (h.IsDead)
                {
                    await component.RespondAsync("☠️ Персонаж уже мёртв.", ephemeral: true);
                    return;
                }
                h.ApplyLethal(1);
                break;
            case HealthAction.ApplyAggravated:
                if (h.IsDead)
                {
                    await component.RespondAsync("☠️ Персонаж уже мёртв.", ephemeral: true);
                    return;
                }
                h.ApplyAggravated(1);
                break;
            case HealthAction.HealOne:
                h.Heal(1);
                break;
            default:
                await component.RespondAsync("⚠️ Неизвестное действие здоровья.", ephemeral: true);
                return;
        }

        await storage.UpsertAsync(character);
        var status = h.IsDead ? "☠️ Персонаж мёртв." : h.IsDestroyed ? "💀 Небоеспособен." : "✅ Состояние обновлено.";
        await component.RespondAsync(
            $"{status}\n{VampireHealthEmbed.Build(character).Description}",
            components: VampireHealthComponents.Build(charId),
            ephemeral: true);
    }
}

/// <summary>
/// Потокобезопасный кеш per-guild <see cref="VampireStorage"/>, чтобы
/// не создавать новый инстанс (и не грузить с диска) на каждой slash-команде.
/// </summary>
internal static class VampireStorageCache
{
    private static readonly System.Collections.Generic.Dictionary<ulong, VampireStorage> _byGuild = new();
    private static readonly object _gate = new();

    public static async Task<VampireStorage> GetAsync(ulong guildId)
    {
        if (_byGuild.TryGetValue(guildId, out var existing))
            return existing;

        VampireStorage? created = null;
        lock (_gate)
        {
            if (_byGuild.TryGetValue(guildId, out existing)) return existing;
            created = new VampireStorage(guildId);
            _byGuild[guildId] = created;
        }

        await created.LoadAsync();
        return created;
    }

    /// <summary>Сбросить кеш (для тестов и для изменений конфигурации).</summary>
    public static void Invalidate(ulong guildId)
    {
        lock (_gate) _byGuild.Remove(guildId);
    }
}
