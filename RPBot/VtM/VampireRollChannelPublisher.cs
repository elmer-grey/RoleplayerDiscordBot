using System;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace RPBot.VtM;

/// <summary>
/// Единая точка публикации VtM-бросков в канал <c>VtMRollChannelID</c>
/// (<see cref="ServerConfig.VtMRollChannelID"/>).
/// <para>Все VtM-броски (обычный <c>/vampire_roll</c>, расчёт урона
/// <c>/vampire_damage</c>, проверка совести, бросок воли) должны идти
/// сюда, а не в канал вызова.</para>
/// <para>Если канал не задан (ID == 0), бот сообщает пользователю
/// «не задан канал для таких бросков» и ничего не публикует.</para>
/// </summary>
public static class VampireRollChannelPublisher
{
    /// <summary>
    /// Геттер канала для заданного guild. По умолчанию — из сконфигурированного
    /// DiscordSocketClient. Можно переопределить в тестах.
    /// </summary>
    public static Func<ulong, ITextChannel?> ChannelGetter { get; set; } = DefaultChannelGetter;

    /// <summary>
    /// Геттер ServerConfig по guild. По умолчанию — Program.ServerConfigResolver.
    /// Можно переопределить в тестах.
    /// </summary>
    public static Func<ulong, ServerConfig?> ConfigGetter { get; set; } =
        guildId => Program.ServerConfigResolver?.Invoke(guildId);

    private static DiscordSocketClient? _client;

    /// <summary>
    /// Один раз при старте Program.cs сохраняет DiscordSocketClient,
    /// чтобы обработчики кнопок могли публиковать без своего DI.
    /// </summary>
    public static void Configure(DiscordSocketClient client)
    {
        _client = client;
        ChannelGetter = DefaultChannelGetter;
    }

    private static ITextChannel? DefaultChannelGetter(ulong channelId)
    {
        return _client?.GetChannel(channelId) as ITextChannel;
    }

    /// <summary>
    /// Только резолв целевого канала без отправки. Используется в тестах
    /// и как helper из PublishAsync.
    /// </summary>
    /// <returns>
    /// null, если канал не задан или конфиг не загружен.
    /// Иначе (cfg, channel).
    /// </returns>
    public static (ServerConfig Config, ITextChannel Channel)? ResolveChannel(ulong guildId)
    {
        var cfg = ConfigGetter(guildId);
        if (cfg == null || cfg.VtMRollChannelID == 0UL) return null;

        var channel = ChannelGetter(cfg.VtMRollChannelID);
        if (channel == null) return null;

        return (cfg, channel);
    }

    /// <summary>
    /// Публикация embed в VtMRollChannel для шага 4 <c>/vampire_start</c>.
    /// <para>В отличие от <see cref="PublishAsync(SocketSlashCommand, Embed, MessageComponent?, IReadOnlyList{FileAttachment}?)"/>,
    /// <b>НЕ</b> вызывает <c>command.RespondAsync</c> — slash-команда
    /// <c>/vampire_start</c> уже задеферена в
    /// <c>VampireStartSlashModule.DispatchAsync</c>, и любой второй
    /// <c>RespondAsync</c> приводит к <c>InvalidOperationException:
    /// Cannot respond twice to the same interaction</c>.</para>
    /// <para>Решение: принимает только guildId + embed, отправляет
    /// embed + (опц.) PNG-кубы в VtMRollChannel, возвращает
    /// структурированный результат — вызывающий код сам решает, что
    /// писать пользователю в FollowupAsync.</para>
    /// </summary>
    /// <param name="guildId">ID гильдии, на которой вызвали команду.</param>
    /// <param name="embed">Готовое embed-сообщение (например, BuildTestRollEmbed).</param>
    /// <param name="extraAttachments">
    /// Доп. файлы отдельным сообщением под embed'ом (PNG-кубы d10).
    /// null/пусто = ничего не шлём.
    /// </param>
    /// <returns>
    /// <c>StartPublishResult</c> со статусом публикации:
    /// <see cref="StartPublishStatus.Published"/> — успех;
    /// <see cref="StartPublishStatus.ChannelNotConfigured"/> — VtMRollChannelID == 0;
    /// <see cref="StartPublishStatus.ConfigMissing"/> — ServerConfig не загружен;
    /// <see cref="StartPublishStatus.ChannelUnavailable"/> — канал не найден.
    /// </returns>
    public static async Task<StartPublishResult> PublishForStartAsync(
        ulong guildId,
        Embed embed,
        IReadOnlyList<FileAttachment>? extraAttachments = null)
    {
        var cfg = ConfigGetter(guildId);
        if (cfg == null)
            return new StartPublishResult(StartPublishStatus.ConfigMissing, 0, null);

        if (cfg.VtMRollChannelID == 0UL)
            return new StartPublishResult(StartPublishStatus.ChannelNotConfigured, 0, null);

        var channel = ChannelGetter(cfg.VtMRollChannelID);
        if (channel == null)
            return new StartPublishResult(
                StartPublishStatus.ChannelUnavailable, cfg.VtMRollChannelID, null);

        try
        {
            await channel.SendMessageAsync(embed: embed);
        }
        catch (Exception ex)
        {
            return new StartPublishResult(
                StartPublishStatus.SendFailed, cfg.VtMRollChannelID, null, ex.Message);
        }

        // PNG-кубы отдельным сообщением — best-effort.
        if (extraAttachments != null && extraAttachments.Count > 0)
        {
            try
            {
                await channel.SendFilesAsync(extraAttachments);
            }
            catch
            {
                // embed уже ушёл; картинки — best-effort.
            }
        }

        return new StartPublishResult(
            StartPublishStatus.Published, cfg.VtMRollChannelID, channel.Mention);
    }

    /// <summary>Статус публикации в VtMRollChannel для шага 4 /vampire_start.</summary>
    public enum StartPublishStatus
    {
        /// <summary>Embed (и опц. PNG) успешно отправлены в VtMRollChannel.</summary>
        Published,
        /// <summary>ServerConfig не загружен для данной гильдии.</summary>
        ConfigMissing,
        /// <summary>VtMRollChannelID == 0 — канал не настроен.</summary>
        ChannelNotConfigured,
        /// <summary>Канал с указанным ID не найден в кеше Discord.</summary>
        ChannelUnavailable,
        /// <summary>SendMessageAsync бросил исключение.</summary>
        SendFailed,
    }

    /// <summary>Результат публикации в VtMRollChannel для шага 4 /vampire_start.</summary>
    /// <param name="Status">Что произошло.</param>
    /// <param name="ChannelId">ID целевого канала (0 если неизвестен).</param>
    /// <param name="ChannelMention">Discord- mention канала (null если канал не задан/недоступен).</param>
    /// <param name="ErrorMessage">Текст ошибки (только для <see cref="StartPublishStatus.SendFailed"/>).</param>
    public readonly record struct StartPublishResult(
        StartPublishStatus Status,
        ulong ChannelId,
        string? ChannelMention,
        string? ErrorMessage = null)
    {
        /// <summary>True, если публикация прошла.</summary>
        public bool IsPublished => Status == StartPublishStatus.Published;
    }

    /// <summary>
    /// Пост embed и (опц.) кнопок в канал VtM-бросков. В канале вызова
    /// отправляет эфемерное подтверждение.
    /// </summary>
    /// <param name="command">Slash-команда, инициировавшая бросок.</param>
    /// <param name="embed">Готовое embed-сообщение.</param>
    /// <param name="components">Кнопки под сообщением (например, переброс за волю).</param>
    /// <param name="extraAttachments">
    /// Доп. файлы для отправки отдельным сообщением сразу под embed'ом
    /// (используется для PNG-кубиков d10 — один embed не вмещает несколько картинок).
    /// </param>
    /// <returns>True, если удалось отправить в VtMRollChannel; false иначе.</returns>
    public static async Task<bool> PublishAsync(
        SocketSlashCommand command,
        Embed embed,
        MessageComponent? components = null,
        IReadOnlyList<FileAttachment>? extraAttachments = null)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync(
                "VtM-броски работают только на сервере.", ephemeral: true);
            return false;
        }

        var cfg = ConfigGetter(guildId);
        if (cfg == null)
        {
            await command.RespondAsync(
                "Конфигурация сервера не загружена.", ephemeral: true);
            return false;
        }

        if (cfg.VtMRollChannelID == 0UL)
        {
            await command.RespondAsync(
                "⚠️ Не задан канал для таких бросков. " +
                "Установите vtm_roll_channel в BotUI.", ephemeral: true);
            return false;
        }

        var channel = ChannelGetter(cfg.VtMRollChannelID);
        if (channel == null)
        {
            await command.RespondAsync(
                $"⚠️ Канал VtM-бросков <#{cfg.VtMRollChannelID}> недоступен.", ephemeral: true);
            return false;
        }

        await channel.SendMessageAsync(embed: embed, components: components);

        // Отдельное сообщение с PNG-кубиками — сразу под embed'ом.
        // Пустой список или null = не отправляем ничего.
        if (extraAttachments != null && extraAttachments.Count > 0)
        {
            try
            {
                await channel.SendFilesAsync(extraAttachments);
            }
            catch (Exception)
            {
                // Сообщение с embed'ом уже ушло; картинки — best-effort.
            }
        }

        // Если slash-команда была вызвана прямо в VtMRollChannel — не плодим
        // дубль: ограничиваемся пустым эфемерным ack, чтобы Discord не ругался
        // на отсутствие ответа. Иначе — короткое упоминание, куда ушёл бросок.
        if (command.Channel.Id == cfg.VtMRollChannelID)
            await command.RespondAsync(string.Empty, ephemeral: true);
        else
            await command.RespondAsync($"🎲 Бросок опубликован в {channel.Mention}.",
                ephemeral: true);
        return true;
    }

    /// <summary>
    /// Пост из контекста кнопки (для будущих кнопок «сопротивление /
    /// игнорирование повреждения»). Использует guild из component.GuildId.
    /// </summary>
    /// <remarks>
    /// Если канал не задан — возвращает false без каких-либо ответов
    /// (вызывающий код сам решает, что показать).
    /// </remarks>
    public static async Task<bool> PublishAsync(
        SocketMessageComponent component,
        Embed embed,
        MessageComponent? components = null)
    {
        if (!component.GuildId.HasValue) return false;

        var cfg = ConfigGetter(component.GuildId.Value);
        if (cfg == null || cfg.VtMRollChannelID == 0UL) return false;

        var channel = ChannelGetter(cfg.VtMRollChannelID);
        if (channel == null) return false;

        await channel.SendMessageAsync(embed: embed, components: components);
        return true;
    }

    /// <summary>
    /// Обновить embed + кнопки в существующем сообщении, и (если есть
    /// <paramref name="extraAttachments"/>) отправить их отдельным
    /// сообщением сразу под ним.
    /// </summary>
    /// <remarks>
    /// Используется handler'ом переброса/повтора: после нажатия кнопки
    /// меняется embed, и под ним публикуются PNG-кубы нового результата
    /// (Discord embed не вмещает несколько картинок).
    /// </remarks>
    /// <param name="targetMessage">Сообщение, которое нужно обновить (в нём embed + кнопки).</param>
    /// <param name="embed">Новый embed.</param>
    /// <param name="components">Новые кнопки (например, после переброса — «Повторить» + «Готово»).</param>
    /// <param name="extraAttachments">PNG-файлы нового броска. null/пусто = ничего не шлём.</param>
    public static async Task<bool> ReplaceEmbedAndSendAttachmentsAsync(
        IUserMessage targetMessage,
        Embed embed,
        MessageComponent? components = null,
        IReadOnlyList<FileAttachment>? extraAttachments = null)
    {
        if (targetMessage == null) return false;
        try
        {
            await targetMessage.ModifyAsync(msg =>
            {
                msg.Embed = embed;
                msg.Components = components;
            });
        }
        catch
        {
            return false;
        }

        if (extraAttachments != null && extraAttachments.Count > 0)
        {
            try
            {
                var channel = targetMessage.Channel as ITextChannel;
                if (channel != null)
                {
                    await channel.SendFilesAsync(extraAttachments,
                        messageReference: new MessageReference(
                            targetMessage.Id,
                            failIfNotExists: false),
                        allowedMentions: AllowedMentions.None);
                }
            }
            catch
            {
                // embed уже обновлён; PNG — best-effort.
            }
        }
        return true;
    }
}
