using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RPBot.EventOps;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Регрессия «reminder1h не перенёс время отправления после переноса события»
/// (2026-10-25, VPS-лог: событие «Wings of Nova: Project "Darkweb"» перенесли в
/// 10:57:09Z на 31.10 14:00Z, а reminder1h в 13:00:00Z ушёл по СТАРОМУ времени
/// от 25.10 14:00Z).
///
/// Механизм бага (два слоя):
/// 1. Program.OnGuildScheduledEventUpdated подменял multicast-делегат
///    `_eventOpsOrchestrator.OnUpdatedAsync = …` целиком — подписка
///    EventOpsLifecycleService.HandleUpdatedAsync стиралась и reschedule вообще
///    не вызывался (фикс в Program.cs: явный вызов анонсера, multicast не трогаем);
/// 2. даже при вызове ScheduleReminder1h все-канальный guard
///    «reminder уже отправлялся» (WasReminderAlreadySent) блокировал
///    пере-планирование: sent-поля оставались заполненными после первой отправки.
/// Фикс: RescheduleReminderForStartChangeAsync — снять таймер, удалить старые
/// reminder-сообщения, страховочно сбросить sent-поля и пере-планировать от нового
/// старта; плюс RehydrateTimersAsync корректирует рассинхрон стора.
/// </summary>
public class EventOpsLifecycleRescheduleReminderTests : IDisposable
{
    private const ulong GuildId = 123;
    private const ulong EventId = 456;

    private readonly string _tempDir;

    public EventOpsLifecycleRescheduleReminderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rpbot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteStoreJson(string fileName, Func<EventAnnouncementEntry, EventAnnouncementEntry> setup)
    {
        var entry = setup(new EventAnnouncementEntry
        {
            GuildId = GuildId,
            EventId = EventId,
            AnnounceChannelId = 789,
            AnnounceMessageId = 1,
            LastName = "Тест-событие",
        });
        var state = new EventAnnouncementState();
        state.Guilds[GuildId] = new GuildEventAnnouncements();
        state.Guilds[GuildId].Events[EventId] = entry;
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(state));
        return path;
    }

    private EventOpsLifecycleService CreateService(string storePath) =>
        new(
            new EventAnnouncementStore(storePath),
            clientProvider: () => null!,
            telegramNotifier: null,
            eventNotifications: new EventNotificationService(
                Path.Combine(_tempDir, "event_notifications.json")),
            serverConfigsProvider: () => new Dictionary<ulong, ServerConfig>());

    private static EventAnnouncementEntry ReloadEntry(string storePath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(storePath));
        var json = doc.RootElement.GetProperty("Guilds").GetProperty(GuildId.ToString())
            .GetProperty("Events").GetProperty(EventId.ToString()).GetRawText();
        return JsonSerializer.Deserialize<EventAnnouncementEntry>(json)!;
    }

    private static object InvokePrivate(object service, string methodName, params object[] args) =>
        service.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, args)!;

    private static object? GetTimerSlot(object service)
    {
        var timersField = service.GetType()
            .GetField("_timers", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(timersField);
        var timers = (IEnumerable)timersField!.GetValue(service)!;
        return timers.Cast<object>().Select(kv => kv.GetType().GetProperty("Value")!.GetValue(kv)!).FirstOrDefault();
    }

    private static bool HasReminderCts(object? slot)
    {
        if (slot is null) return false;
        var ctsField = slot.GetType()
            .GetField("Reminder1hCts", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        return ctsField?.GetValue(slot) != null;
    }

    /// <summary>
    /// T1: при переносе старта reschedule обязан сбросить sent-поля (иначе guard
    /// ScheduleReminder1h молча skip, как было на проде) и поставить reminder1h
    /// от НОВОГО старта: Reminder1hAtUtc == LastStartTimeUtc − 1ч.
    /// </summary>
    [Fact]
    public async Task RescheduleReminderForStartChange_ResetsSentFlagsAndReplans()
    {
        var oldStart = DateTime.UtcNow.AddHours(6);
        var newStart = DateTime.UtcNow.AddHours(2);
        var storePath = WriteStoreJson("reschedule.json", entry =>
        {
            entry.LastStartTimeUtc = oldStart;
            entry.Reminder1hAtUtc = oldStart.AddHours(-1);
            entry.ReminderDmMessageIdsByUserId = new Dictionary<ulong, ulong> { [42] = 1UL };
            entry.ReminderAnnounceMessageId = 111;
            entry.ReminderTelegramMessageId = 222;
            entry.ReminderTelegramChatId = 333;
            entry.ReminderTelegramThreadId = 444;
            return entry;
        });
        var svc = CreateService(storePath);
        try
        {
            var entry = new EventAnnouncementStore(storePath).TryGet(GuildId, EventId)!;
            entry.LastStartTimeUtc = newStart;

            await (Task)InvokePrivate(svc, "RescheduleReminderForStartChangeAsync", entry);

            var reloaded = ReloadEntry(storePath);
            Assert.True(reloaded.Reminder1hAtUtc.HasValue, "Reschedule обязан записать новое Reminder1hAtUtc от нового старта.");
            var expected = newStart.AddHours(-1).ToUniversalTime();
            Assert.True(Math.Abs((reloaded.Reminder1hAtUtc!.Value - new DateTimeOffset(expected)).TotalSeconds) < 1,
                $"Reminder1hAtUtc={reloaded.Reminder1hAtUtc:u} должен быть пере-вычислен от нового старта ({expected:u}).");
            Assert.True(reloaded.Reminder1hAtUtc!.Value > DateTimeOffset.UtcNow.AddMinutes(30),
                "Reminder1hAtUtc должен быть в будущем по новому старту, а не по старому.");
            Assert.Null(reloaded.ReminderDmMessageIdsByUserId);
            Assert.Equal(0UL, reloaded.ReminderAnnounceMessageId);
            Assert.Equal(0, reloaded.ReminderTelegramMessageId);
            Assert.Equal(0L, reloaded.ReminderTelegramChatId);
            Assert.Equal(0, reloaded.ReminderTelegramThreadId);
            Assert.True(HasReminderCts(GetTimerSlot(svc)),
                "После reschedule таймер reminder1h обязан быть запланированным (CTS в слоте).");
        }
        finally
        {
            svc.Cancel();
        }
    }

    /// <summary>
    /// T2: guard-ветка ScheduleReminder1h — при заполненных sent-полях всех трёх
    /// каналов reminder НЕ пере-планируется (Reminder1hAtUtc не записывается).
    /// Именно эта ветка блокировала reschedule на проде — поэтому reschedule обязан
    /// сбрасывать sent-поля ДО вызова ScheduleReminder1h.
    /// </summary>
    [Fact]
    public void ScheduleReminder1h_AllChannelsSent_SkipsReplanning()
    {
        var start = DateTime.UtcNow.AddHours(2);
        var storePath = WriteStoreJson("guard-all-sent.json", entry =>
        {
            entry.LastStartTimeUtc = start;
            entry.ReminderDmMessageIdsByUserId = new Dictionary<ulong, ulong> { [42] = 1UL };
            entry.ReminderAnnounceMessageId = 111;
            entry.ReminderTelegramMessageId = 222;
            entry.ReminderTelegramChatId = 333;
            return entry;
        });
        var svc = CreateService(storePath);
        try
        {
            var entry = new EventAnnouncementStore(storePath).TryGet(GuildId, EventId)!;
            entry.Reminder1hAtUtc = null;

            var scheduled = (bool)InvokePrivate(svc, "ScheduleReminder1h", entry)!;

            Assert.False(scheduled, "При заполненных sent-полях всех каналов планирование должно быть пропущено.");
            Assert.Null(entry.Reminder1hAtUtc);
            Assert.NotNull(entry.ReminderDmMessageIdsByUserId);
            Assert.Equal(111UL, entry.ReminderAnnounceMessageId);
        }
        finally
        {
            svc.Cancel();
        }
    }

    /// <summary>
    /// T3: rehydrate — самокоррекция рассинхрона стора: если Reminder1hAtUtc
    /// (13:00Z) относится к СТАРОМУ старту, а LastStartTimeUtc — новый (15:00Z),
    /// поле переписывается от нового старта (14:00Z) и таймер ставится.
    /// </summary>
    [Fact]
    public async Task RehydrateTimers_CorrectsStaleReminderToNewStart()
    {
        var newStart = DateTime.UtcNow.AddHours(3);
        var storePath = WriteStoreJson("rehydrate-stale.json", entry =>
        {
            entry.LastStartTimeUtc = newStart;
            entry.Reminder1hAtUtc = DateTime.UtcNow.AddHours(1); // от старого старта
            return entry;
        });
        var svc = CreateService(storePath);
        try
        {
            await svc.RehydrateTimersAsync(CancellationToken.None);

            var reloaded = ReloadEntry(storePath);
            var expected = newStart.AddHours(-1).ToUniversalTime();
            Assert.True(reloaded.Reminder1hAtUtc.HasValue,
                "RehydrateTimersAsync обязан записать корректный Reminder1hAtUtc (от нового старта).");
            Assert.True(Math.Abs((reloaded.Reminder1hAtUtc!.Value - new DateTimeOffset(expected)).TotalSeconds) < 1,
                $"Reminder1hAtUtc={reloaded.Reminder1hAtUtc:u} должен быть пере-вычислен от нового старта ({expected:u}); " +
                "иначе таймер сработает в несуществующее окно, как на проде 2026-10-25.");
            Assert.True(HasReminderCts(GetTimerSlot(svc)),
                "После коррекции таймер reminder1h обязан быть запланированным (CTS в слоте).");
        }
        finally
        {
            svc.Cancel();
        }
    }
}