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
/// <summary>
/// ID создателя события (Discord user). Запоминается в сторе при Created,
/// чтобы SendReminderAsync не дёргал client.Rest.GetGuildAsync/GetEventAsync
/// на каждом напоминании ради creatorId (см. rest-per-reminder).
/// </summary>
public ulong? LastCreatorId { get; set; }
public string? LastUpdatedMark { get; set; }
public DateTime? LastUpdatedAt { get; set; }

// Фактическое время старта события (UTC). Записывается в момент started,
// чтобы при completed/cancelled корректно показать «когда событие
// действительно началось», а не плановое время.
public DateTimeOffset? ActualStartTimeUtc { get; set; }

// Фактическое время отмены события (UTC). Записывается в момент cancelled,
// чтобы EventOpsLifecycleService мог отсчитать от него «+24ч удаление».
// Если null — fallback на LastStartTimeUtc (плановое).
public DateTimeOffset? CancelledAtUtc { get; set; }

// Фактическое время завершения события (UTC). Записывается в момент completed
// в AnnounceStatusChangedInternalAsync (или берётся из уже сохранённого при
// resync через REST). Используется для двух целей:
//   1) Чтобы при status-from-rest поле «Завершение» показывало реальное время
//      окончания, а не момент рестарта бота.
//   2) Чтобы EventOpsLifecycleService.ScheduleCleanup24h считал «+24ч удаление»
//      от момента завершения, а не от планового LastStartTimeUtc.
// Если null — fallback на CancelledAtUtc ?? ActualStartTimeUtc ?? LastStartTimeUtc.
public DateTimeOffset? CompletedAtUtc { get; set; }

// Абсолютное время UTC, когда анонс должен быть удалён из канала / DM / TG
// (после Completed или Cancelled). Выставляется EventOpsLifecycleService в
// HandleCompletedAsync / HandleCancelledAsync и сохраняется в JSON, чтобы
// пережить рестарт бота: при старте RebuildFromStoreAsync читает это поле
// и ставит cleanup-таймер заново. Если null — вычисляется на лету
// (CancelledAtUtc ?? ActualStartTimeUtc ?? LastStartTimeUtc) + 24ч.
public DateTimeOffset? CleanupAtUtc { get; set; }

// ID DM-сообщений с напоминанием за час (рассылка EventOpsLifecycleService).
// Хранится отдельно от DmMessageIdsByUserId (там лежат анонсы), чтобы при
// удалении напоминания через 15 мин после старта события не трогать анонсы.
public Dictionary<ulong, ulong>? ReminderDmMessageIdsByUserId { get; set; }

// ID Telegram-сообщения с напоминанием за час и параметры чата/топика.
// Нужны для последующего удаления через 15 мин после ActualStartTimeUtc.
public int ReminderTelegramMessageId { get; set; }
public long ReminderTelegramChatId { get; set; }
public int ReminderTelegramThreadId { get; set; }

// ID сообщения с напоминанием за час в основном канале анонса
// (entry.AnnounceChannelId). Напоминание шлётся отдельным сообщением рядом
// с анонсом; через 15 мин после ActualStartTimeUtc удаляется именно оно,
// а не сам анонс.
public ulong ReminderAnnounceMessageId { get; set; }

// Абсолютный момент UTC, в который должен сработать reminder1h
// (LastStartTimeUtc - 1ч). Выставляется EventOpsLifecycleService и
// сохраняется в JSON, чтобы пережить рестарт бота/дисконнект:
// RebuildFromStoreAsync читает поле и либо ставит Task.Delay на остаток,
// либо, если момент уже в прошлом, шлёт reminder немедленно
// (превращается в catch-up для случая «пока бот был оффлайн, прошло
// reminder-окно»). null означает «не запланировано».
public DateTimeOffset? Reminder1hAtUtc { get; set; }

// Абсолютный момент UTC, в который нужно удалить reminder-сообщения
// (DM/канал анонса/Telegram). Считается как ActualStartTimeUtc ?? LastStartTimeUtc
// + 15мин в HandleStartedAsync и сохраняется в JSON, чтобы пережить рестарт
// бота/дисконнект: RebuildFromStoreAsync читает поле и либо ставит
// Task.Delay на остаток, либо, если момент уже в прошлом, чистит
// reminder-месседжи напрямую через DeleteReminderMessagesAsync (catch-up
// для случая «пока бот был оффлайн, прошло 15-мин окно»). null — не
// запланировано.
public DateTimeOffset? DeleteReminder15mAtUtc { get; set; }
}
}
