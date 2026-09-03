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
                await component.RespondAsync(VampireWizardDmHandler.BuildAdvancedMessage(), ephemeral: true);
                // Сохраняем draft в storage, чтобы было видно в /vampire show.
                await CommitDraftAsync(component, session);
                return;

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
                var text = VampireCreateResolver.BuildConceptStatusMessage(session.Draft);
                var components = VampireWizardComponents.BuildForConceptStep(session.Draft);
                await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
            }
        }
        catch { /* не критично */ }
        await component.RespondAsync("✅ Обновлено.", ephemeral: true);
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
