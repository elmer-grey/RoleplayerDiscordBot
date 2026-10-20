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
/// Регрессия «напоминание потеряно после gateway-реконнекта» (2026-10-20, событие
/// «Берсеш 511: Блестящий Лжец», reminder1h 12:00Z не отправился).
///
/// Механизм бага: EventOpsLifecycleService.Cancel() вызывается на каждом
/// OnDisconnected (плановые gateway-реконнекты Discord — каждые 1–3 часа) и рвал
/// _shutdownCts.Cancel() без пересоздания. Все последующие RehydrateTimersAsync
/// линковали на этот мёртвый токен (linkedCts) и разрывались на первой итерации
/// цикла (ct.IsCancellationRequested) — сводки выходили со всеми нулями, а
/// reminder1h/deleteReminder15m/cleanup24h никогда не восстанавливались.
/// Фикс: Cancel() после отмены пересоздаёт _shutdownCts.
/// </summary>
public class EventOpsLifecycleRehydrateAfterCancelTests : IDisposable
{
    private readonly string _tempDir;

    public EventOpsLifecycleRehydrateAfterCancelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rpbot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static string WriteStoreJson(string path, DateTime nowUtc, int reminderLeadMinutes)
    {
        var start = nowUtc.AddMinutes(reminderLeadMinutes + 60);
        var entry = new EventAnnouncementEntry
        {
            GuildId = 123,
            EventId = 456,
            AnnounceChannelId = 789,
            AnnounceMessageId = 1,
            LastName = "Тест-событие",
            LastStartTimeUtc = start,
            Reminder1hAtUtc = start.AddHours(-1),
        };
        var state = new EventAnnouncementState();
        state.Guilds[123] = new GuildEventAnnouncements();
        state.Guilds[123].Events[456] = entry;
        var json = JsonSerializer.Serialize(state);
        File.WriteAllText(path, json);
        return path;
    }

    private static EventOpsLifecycleService CreateService(string storePath, string tempDir) =>
        new(
            new EventAnnouncementStore(storePath),
            clientProvider: () => null!,
            telegramNotifier: null,
            eventNotifications: new EventNotificationService(Path.Combine(tempDir, "event_notifications.json")),
            serverConfigsProvider: () => new Dictionary<ulong, ServerConfig>());

    private static object? GetTimerSlot(object service)
    {
        var timersField = service.GetType()
            .GetField("_timers", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(timersField);
        var timers = (IEnumerable)timersField!.GetValue(service)!;
        foreach (var kv in timers.Cast<object>())
        {
            var slot = kv.GetType().GetProperty("Value")!.GetValue(kv)!;
            return slot;
        }
        return null;
    }

    private static bool HasReminderCts(object? slot)
    {
        if (slot is null) return false;
        var ctsField = slot.GetType()
            .GetField("Reminder1hCts", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        return ctsField?.GetValue(slot) != null;
    }

    [Fact]
    public async Task RehydrateTimers_WithoutCancel_ReschedulesFutureReminder()
    {
        var storePath = WriteStoreJson(
            Path.Combine(_tempDir, "store-baseline.json"), DateTime.UtcNow, reminderLeadMinutes: 60);
        var svc = CreateService(storePath, _tempDir);

        await svc.RehydrateTimersAsync(CancellationToken.None);

        Assert.True(HasReminderCts(GetTimerSlot(svc)),
            "RehydrateTimersAsync на чистом токене обязан переставить future-reminder (CTS в слоте).");

        svc.Cancel();
    }

    [Fact]
    public async Task RehydrateTimers_AfterCancel_ReschedulesFutureReminder()
    {
        var storePath = WriteStoreJson(
            Path.Combine(_tempDir, "store-after-cancel.json"), DateTime.UtcNow, reminderLeadMinutes: 60);
        var svc = CreateService(storePath, _tempDir);

        svc.Cancel();
        await svc.RehydrateTimersAsync(CancellationToken.None);

        Assert.True(HasReminderCts(GetTimerSlot(svc)),
            "После Cancel() последующий RehydrateTimersAsync обязан снова планировать таймеры " +
            "(_shutdownCts должен быть пересоздан); иначе reminder1h теряется на каждом gateway-реконнекте.");

        svc.Cancel();
    }
}