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
                components: VampireSheetComponents.Build(character));
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

        await command.RespondAsync($"✉️ Лист «{character.CharacterName}» отправлен в личку <@{recipientId}>.", ephemeral: true);
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

    // ── Хелперы ─────────────────────────────────────────────────────────

    private static async Task<bool> IsStorytellerAsync(SocketSlashCommand command)
    {
        if (command.User is not SocketGuildUser g) return false;
        if (g.GuildPermissions.Administrator) return true;
        return g.GuildPermissions.ManageRoles;
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
