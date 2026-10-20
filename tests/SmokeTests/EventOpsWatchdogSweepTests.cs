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
/// Watchdog-свайп напоминаний (запрос владельца 2026-10-20, «каждые ~30 минут
/// проверять, установлено ли напоминание и ушло ли оно в своё время»).
///
/// Покрывает:
/// • пер-канальный предикат «уже отправлялось» и предикат старта события;
/// • свип ставит таймеры для будущих напоминаний даже после Cancel(() (слоты пусты);
/// • свип не рассылает пост-стартовые напоминания и снимает заброшенный план;
/// • свип не трогает deleteReminder15m до старта события;
/// • при недоступном клиенте стор-поле сохраняется — свип повторит попытку позже.
/// </summary>
public class EventOpsWatchdogSweepTests : IDisposable
{
    private readonly string _tempDir;

    public EventOpsWatchdogSweepTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rpbot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static string WriteStoreJson(string path, EventAnnouncementEntry entry)
    {
        var state = new EventAnnouncementState();
        state.Guilds[entry.GuildId] = new GuildEventAnnouncements();
        state.Guilds[entry.GuildId].Events[entry.EventId] = entry;
        File.WriteAllText(path, JsonSerializer.Serialize(state));
        return path;
    }

    private static EventAnnouncementEntry MakeEntry(
        DateTime? startUtc, DateTime? reminder1hUtc, DateTime? delete15mUtc, ulong eventId = 456)
        => new()
        {
            GuildId = 123,
            EventId = eventId,
            AnnounceChannelId = 789,
            AnnounceMessageId = 1,
            LastName = "Тест-событие",
            LastStartTimeUtc = startUtc,
            Reminder1hAtUtc = reminder1hUtc,
            DeleteReminder15mAtUtc = delete15mUtc,
        };

    private static EventOpsLifecycleService CreateService(string storePath) =>
        new(
            new EventAnnouncementStore(storePath),
            clientProvider: () => null!,
            telegramNotifier: null,
            eventNotifications: new EventNotificationService(Path.Combine(Path.GetDirectoryName(storePath)!, "event_notifications.json")),
            serverConfigsProvider: () => new Dictionary<ulong, ServerConfig>());

    private static EventAnnouncementStore GetStore(EventOpsLifecycleService svc)
        => (EventAnnouncementStore)svc.GetType()
            .GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(svc)!;

    private static EventAnnouncementEntry ReadEntry(EventOpsLifecycleService svc) =>
        Assert.Single(GetStore(svc).GetEntriesSnapshot());

    private static void StartSweep(EventOpsLifecycleService svc)
    {
        var method = svc.GetType().GetMethod(
            "StartReminderSweep", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(svc, null);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(150);
        }
        Assert.True(condition(), "условие не выполнилось в пределах таймаута");
    }

    private static SlotView ReadSlot(EventOpsLifecycleService svc)
    {
        var timersField = svc.GetType()
            .GetField("_timers", BindingFlags.NonPublic | BindingFlags.Instance);
        var timers = (IEnumerable)timersField!.GetValue(svc)!;
        foreach (var kv in timers.Cast<object>())
        {
            var slot = kv.GetType().GetProperty("Value")!.GetValue(kv)!;
            return new SlotView(
                Cts: Field(slot, "Reminder1hCts") != null,
                DeleteCts: Field(slot, "DeleteReminder15mCts") != null);
        }
        return new SlotView(Cts: false, DeleteCts: false);
    }

    private static object? Field(object obj, string name)
    {
        var f = obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        return f?.GetValue(obj);
    }

    private sealed record SlotView(bool Cts, bool DeleteCts);

    // ── Предикаты ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventAlreadyStarted_ReturnsTrueOnlyWhenStartPassed(bool started)
    {
        var entry = MakeEntry(
            started ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddHours(1), null, null);
        var method = typeof(EventOpsLifecycleService).GetMethod(
            "EventAlreadyStarted", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = (bool)method!.Invoke(null, new object[] { entry, DateTimeOffset.UtcNow })!;
        Assert.Equal(started, result);
    }

    [Fact]
    public void ReminderWasPerChannelSent_TrueForAnyChannelFlag()
    {
        var method = typeof(EventOpsLifecycleService).GetMethod(
            "ReminderWasPerChannelSent", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var invoke = (EventAnnouncementEntry e) => (bool)method!.Invoke(null, new object[] { e })!;

        Assert.False(invoke(MakeEntry(null, null, null)), "чистая запись ещё не отправляла напоминание");
        Assert.True(invoke(new EventAnnouncementEntry { ReminderAnnounceMessageId = 42 }));
        Assert.True(invoke(new EventAnnouncementEntry
        {
            ReminderDmMessageIdsByUserId = new Dictionary<ulong, ulong> { [5] = 77 },
        }));
        Assert.True(invoke(new EventAnnouncementEntry { ReminderTelegramMessageId = 9 }));
    }

    // ── Цикл свипа ────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_AfterCancel_RestoresFutureReminderTimer()
    {
        var entry = MakeEntry(DateTime.UtcNow.AddHours(2), DateTime.UtcNow.AddMinutes(90), null);
        var storePath = WriteStoreJson(Path.Combine(_tempDir, "f-reminder.json"), entry);
        var svc = CreateService(storePath);

        svc.Cancel();
        StartSweep(svc);

        await WaitUntil(() => ReadSlot(svc).Cts);

        svc.Cancel();
    }

    [Fact]
    public async Task Sweep_StartedEventWithUnsentReminder_CancelsAbandonedPlan()
    {
        // Событие началось час назад, напоминание (2 часа назад) не ушло —
        // пост-стартовые напоминания запрещены: свип обязан снять план и не ставить таймер.
        var entry = MakeEntry(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(-2), null);
        var storePath = WriteStoreJson(Path.Combine(_tempDir, "past-started.json"), entry);
        var store = new EventAnnouncementStore(storePath);
        var svc = CreateService(storePath);

        StartSweep(svc);
        await WaitUntil(() => !ReadEntry(svc).Reminder1hAtUtc.HasValue);

        var slot = ReadSlot(svc);
        Assert.False(slot.Cts);

        svc.Cancel();
    }

    [Fact]
    public async Task Sweep_OverdueReminderWithNoClient_KeepsStoreFieldForRetry()
    {
        // Клиент недоступен (в тесте provider → null): напоминание не ушло, поле
        // остаётся в сторе — следующий проход свипа повторит попытку.
        var entry = MakeEntry(DateTime.UtcNow.AddHours(2), DateTime.UtcNow.AddMinutes(-30), null);
        var storePath = WriteStoreJson(Path.Combine(_tempDir, "overdue-no-client.json"), entry);
        var store = new EventAnnouncementStore(storePath);
        var svc = CreateService(storePath);

        StartSweep(svc);
        await Task.Delay(1200);

        Assert.True(ReadEntry(svc).Reminder1hAtUtc.HasValue,
            "поле Reminder1hAtUtc должно сохраниться, пока отправка не удалась");
        Assert.False(ReadSlot(svc).Cts, "на прошедший момент таймер ставиться не должен");

        svc.Cancel();
    }

    [Fact]
    public async Task Sweep_RestoresDeleteTimerForStartedEvent()
    {
        var entry = MakeEntry(DateTime.UtcNow.AddMinutes(-20), null, DateTime.UtcNow.AddMinutes(10));
        var storePath = WriteStoreJson(Path.Combine(_tempDir, "f-delete.json"), entry);
        var svc = CreateService(storePath);

        StartSweep(svc);
        await WaitUntil(() => ReadSlot(svc).DeleteCts);

        svc.Cancel();
    }

    [Fact]
    public async Task Sweep_DeleteOverdueBeforeEventStart_IsNotExecuted()
    {
        // Удалять напоминания до старта события нельзя — напоминание может ещё уйти.
        var entry = MakeEntry(null, null, DateTime.UtcNow.AddMinutes(-30));
        var storePath = WriteStoreJson(Path.Combine(_tempDir, "delete-not-started.json"), entry);
        var store = new EventAnnouncementStore(storePath);
        var svc = CreateService(storePath);

        StartSweep(svc);
        await Task.Delay(1200);

        Assert.True(ReadEntry(svc).DeleteReminder15mAtUtc.HasValue,
            "delete-момент до старта события не должен удаляться свипом");
        Assert.False(ReadSlot(svc).DeleteCts);

        svc.Cancel();
    }

    [Fact]
    public async Task Sweep_TwiceStarted_RunsSingleFlightLoop()
    {
        // Второй вызов StartReminderSweep при живом цикле обязан быть no-op (single-flight):
        // гвард через private reflection — _sweepRunning == 1 после второго вызова, а
        // в сторе не появляется артефактов двойного прохода.
        var entry = MakeEntry(DateTime.UtcNow.AddHours(2), DateTime.UtcNow.AddMinutes(90), null);
        var storePath = WriteStoreJson(Path.Combine(_tempDir, "single-flight.json"), entry);
        var svc = CreateService(storePath);

        StartSweep(svc);
        StartSweep(svc);

        var guardField = svc.GetType().GetField("_sweepRunning", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(guardField);

        await WaitUntil(() => ReadSlot(svc).Cts);
        Assert.Equal(1, Convert.ToInt32(guardField!.GetValue(svc)));

        svc.Cancel();
    }
}