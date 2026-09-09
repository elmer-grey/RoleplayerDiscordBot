using System;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot;

/// <summary>
/// Slash-команды модуля VtM, отвечающие за управление персонажами:
///<list type="bullet">
///   <item><c>/vampire_bind &lt;name&gt; [user]</c> — привязать к игроку (ST only).</item>
///   <item><c>/vampire_unbind &lt;name&gt;</c> — отвязать (ST only).</item>
///   <item><c>/vampire_show &lt;name&gt; [user] [where]</c> — лист в DM/публично.</item>
///   <item><c>/vampire_send &lt;name&gt; [user]</c> — принудительно в DM (ST only).</item>
///   <item><c>/vampire_create</c> — открыть визард в ЛС.</item>
/// </list>
/// Выделено из <see cref="VampireCommands"/> (бывший VtMCommands.cs)
/// для уменьшения размера файла.
/// </summary>
internal sealed class VampireCharacterCommands
{
    /// <summary><c>/vampire_bind</c>.</summary>
    public async Task HandleBindAsync(SocketSlashCommand command)
    {
        var guildId = command.GuildId;
        if (!guildId.HasValue)
        {
            await command.RespondAsync("Команда доступна только на сервере.", ephemeral: true);
            return;
        }
        if (!await VampireCommandHelpers.IsStorytellerAsync(command))
        {
            await command.RespondAsync("Только рассказчик может привязывать персонажей.", ephemeral: true);
            return;
        }

        var name = VampireCommandHelpers.GetString(command, "name");
        var userOpt = VampireCommandHelpers.GetUser(command, "user");

        if (string.IsNullOrWhiteSpace(name))
        {
            await command.RespondAsync("Укажите имя персонажа.", ephemeral: true);
            return;
        }
        if (!userOpt.HasValue)
        {
            await command.RespondAsync("Укажите пользователя (`user`).", ephemeral: true);
            return;
        }

        var storage = await VampireStorageCache.GetAsync(guildId.Value);
        var result = await storage.BindAsync(name, userOpt.Value.id);

        await command.RespondAsync(
            embed: VampireCommandHelpers.BindResultToEmbed(result, userOpt.Value.id),
            ephemeral: true);
    }

    /// <summary><c>/vampire_unbind</c>.</summary>
    public async Task HandleUnbindAsync(SocketSlashCommand command)
    {
        var guildId = command.GuildId;
        if (!guildId.HasValue)
        {
            await command.RespondAsync("Команда доступна только на сервере.", ephemeral: true);
            return;
        }
        if (!await VampireCommandHelpers.IsStorytellerAsync(command))
        {
            await command.RespondAsync("Только рассказчик может отвязывать персонажей.", ephemeral: true);
            return;
        }

        var name = VampireCommandHelpers.GetString(command, "name");
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

    /// <summary><c>/vampire_show</c> (DM-вариант; top-level <c>/vampire_show</c> показывает в канале).</summary>
    public async Task HandleShowAsync(SocketSlashCommand command)
    {
        var guildId = command.GuildId;
        if (!guildId.HasValue)
        {
            await command.RespondAsync("Команда доступна только на сервере.", ephemeral: true);
            return;
        }

        var name = VampireCommandHelpers.GetString(command, "name");
        var userOpt = VampireCommandHelpers.GetUser(command, "user");
        var whereRaw = VampireCommandHelpers.GetString(command, "where");
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

    /// <summary><c>/vampire_send</c>.</summary>
    public async Task HandleSendAsync(SocketSlashCommand command)
    {
        var guildId = command.GuildId;
        if (!guildId.HasValue)
        {
            await command.RespondAsync("Команда доступна только на сервере.", ephemeral: true);
            return;
        }
        if (!await VampireCommandHelpers.IsStorytellerAsync(command))
        {
            await command.RespondAsync("Только рассказчик может отправлять листы.", ephemeral: true);
            return;
        }

        var name = VampireCommandHelpers.GetString(command, "name");
        var userOpt = VampireCommandHelpers.GetUser(command, "user");
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

    /// <summary><c>/vampire_show &lt;name&gt;</c>.</summary>
    public async Task HandleShowPublicAsync(SocketSlashCommand command)
    {
        var guildId = command.GuildId;
        if (!guildId.HasValue)
        {
            await command.RespondAsync("Команда доступна только на сервере.", ephemeral: true);
            return;
        }

        var name = VampireCommandHelpers.GetString(command, "name");
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

    /// <summary><c>/vampire_create</c> — открыть визард в ЛС.</summary>
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

    // ── Private helpers (sheet DM/public) ─────────────────────────────────────

    private static async Task SendSheetToDmAsync(
        SocketSlashCommand command,
        VampireCharacter character,
        VampireDisplayIndex displayIndex,
        ulong guildId)
    {
        ulong recipientId = command.User.Id;
        var userOpt = VampireCommandHelpers.GetUser(command, "user");
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
}
