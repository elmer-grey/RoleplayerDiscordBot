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
}
