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
/// 🩹 reminder-send-gate (2026-10-21): все пути отправки напоминаний
/// (RunReminder1h, rehydrate-past, rebuild catch-up, watchdog-свайп) должны
/// сериализоваться семафором на (guild,event). Без гейта reconnect-реридрат мог
/// бы отправить дубль ровно в момент, когда свип/таймер уже отправляет то же
/// напоминание (в сторе остался бы id только одной отправки).
/// </summary>
public class EventOpsReminderSendGateTests : IDisposable
{
    private readonly string _tempDir;

    public EventOpsReminderSendGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rpbot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private EventOpsLifecycleService CreateService(string storePath) =>
        new(
            new EventAnnouncementStore(storePath),
            clientProvider: () => null!,
            telegramNotifier: null,
            eventNotifications: new EventNotificationService(Path.Combine(_tempDir, "event_notifications.json")),
            serverConfigsProvider: () => new Dictionary<ulong, ServerConfig>());

    private static EventAnnouncementEntry MakeEntry() => new()
    {
        GuildId = 123,
        EventId = 456,
        AnnounceChannelId = 789,
        AnnounceMessageId = 1,
        LastName = "Тест-событие",
        LastStartTimeUtc = DateTime.UtcNow.AddHours(-1),
        Reminder1hAtUtc = DateTime.UtcNow.AddHours(-2),
    };

    private static async Task InvokeSendAsync(EventOpsLifecycleService svc, EventAnnouncementEntry entry)
    {
        var method = typeof(EventOpsLifecycleService).GetMethod(
            "SendReminderAsync", BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(EventAnnouncementEntry) }, null);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(svc, new object[] { entry })!;
        await task;
    }

    private static SemaphoreSlim GetGate(EventOpsLifecycleService svc)
    {
        var field = svc.GetType().GetField("_sendGates", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var dict = (IEnumerable)field!.GetValue(svc)!;
        foreach (var kv in dict.Cast<object>())
        {
            return (SemaphoreSlim)kv.GetType().GetProperty("Value")!.GetValue(kv)!;
        }
        throw new InvalidOperationException("гейт для события не создан");
    }

    [Fact]
    public async Task SendReminderAsync_CreatesPerEventGate()
    {
        var entryPath = Path.Combine(_tempDir, "g1.json");
        File.WriteAllText(entryPath, JsonSerializer.Serialize(new EventAnnouncementState()));
        var svc = CreateService(entryPath);

        await InvokeSendAsync(svc, MakeEntry());

        var gate = GetGate(svc);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task SendReminderAsync_BlocksWhileGateHeld()
    {
        var entryPath = Path.Combine(_tempDir, "g2.json");
        File.WriteAllText(entryPath, JsonSerializer.Serialize(new EventAnnouncementState()));
        var svc = CreateService(entryPath);
        var entry = MakeEntry();

        // Первый вызов быстро уходит (client=null в core после входа в гейт) и создаёт гейт.
        await InvokeSendAsync(svc, entry);
        var gate = GetGate(svc);

        // Захватываем гейт вручную — имитируем отправку, идущую параллельным путём.
        Assert.True(gate.Wait(0), "гейт должен быть свободен после завершения первой отправки");

        var second = Task.Run(() => InvokeSendAsync(svc, entry));
        await Task.Delay(300);
        Assert.False(second.IsCompleted,
            "второй путь отправки обязан ждать освобождения гейта (сериализация по событию)");

        gate.Release();
        var finished = await Task.WhenAny(second, Task.Delay(3000));
        Assert.Same(second, finished);

        gate.Wait(0);
    }
}