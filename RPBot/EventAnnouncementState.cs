using System;
using System.Collections.Generic;

namespace RPBot
{
public sealed class EventAnnouncementState
{
public Dictionary<ulong, GuildEventAnnouncements> Guilds { get; set; } = new();

    /// <summary>
    /// Флаг одноразовой перерисовки старых анонсов в каналах анонсов
    /// (см. <c>RPBot.EventOps.EventOpsRemigrationService</c>). После первого
    /// успешного прохода <c>RunAsync</c> выставляется в true, чтобы при
    /// последующих перезапусках бот не правил embed-ы заново.
    /// </summary>
    public bool RemigratedOnce { get; set; }

    public EventAnnouncementState()
    {
    // System.Text.Json при десериализации НЕ вызывает инициализаторы полей,
    // поэтому если в JSON нет ключа, словарь останется null. Возвращаем
    // пустую коллекцию, чтобы старые файлы не падали с NRE.
    Guilds ??= new Dictionary<ulong, GuildEventAnnouncements>();
    }
}

public sealed class GuildEventAnnouncements
{
public Dictionary<ulong, EventAnnouncementEntry> Events { get; set; } = new();

public GuildEventAnnouncements()
{
    Events ??= new Dictionary<ulong, EventAnnouncementEntry>();
}
}

public sealed class EventAnnouncementEntry
{
public ulong GuildId { get; set; }
public ulong EventId { get; set; }

public ulong AnnounceChannelId { get; set; }
public ulong AnnounceMessageId { get; set; }

public Dictionary<ulong, ulong> DmMessageIdsByUserId { get; set; } = new();

public EventAnnouncementEntry()
{
    // Явный default: System.Text.Json при десериализации НЕ вызывает инициализаторы полей,
    // поэтому если в JSON нет ключа, словарь останется null. Это исторически приводило
    // к NRE в AnnounceUpdatedInternalAsync при resync.
    DmMessageIdsByUserId ??= new Dictionary<ulong, ulong>();
}

public long TelegramChatId { get; set; }
public int TelegramMessageThreadId { get; set; }
public int TelegramMessageId { get; set; }
public bool TelegramHasPhoto { get; set; }

public DateTimeOffset LastUpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

// Снимок прошлого состояния события (для diff при ресинке).
public string? LastName { get; set; }
public string? LastDescription { get; set; }
public DateTimeOffset? LastStartTimeUtc { get; set; }
public DateTimeOffset? LastEndTimeUtc { get; set; }
public ulong? LastChannelId { get; set; }
public string? LastLocation { get; set; }
public string? LastCoverImageUrl { get; set; }
public string? LastUpdatedMark { get; set; }
public DateTime? LastUpdatedAt { get; set; }
}
}
