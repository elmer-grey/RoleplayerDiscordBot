using System;
using System.Reflection;
using RPBot;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Статусы событий в web-дашборде (/api/events, Program.GetEventStatusLabel).
/// 🩹 dashboard-live-status (2026-10-21): при прошедшем старт-времени и пустой/
/// «scheduled» метке RESYNC (Started не долёг до стора) показывается «Началось»,
/// а не «Запланировано» — карточка дашборда синхронна с Discord-клиентом («Идёт»).
/// Финальные метки/таймстемпы имеют приоритет; стор не трогается.
/// </summary>
public class EventDashboardStatusTests
{
    private static string? Invoke(EventAnnouncementEntry entry)
    {
        var method = typeof(Program).GetMethod(
            "GetEventStatusLabel", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, new object[] { entry });
    }

    private static EventAnnouncementEntry MakeEntry(
        string? mark, DateTime? startUtc,
        DateTime? completedUtc = null, DateTime? cancelledUtc = null)
        => new()
        {
            GuildId = 123,
            EventId = 456,
            AnnounceChannelId = 789,
            AnnounceMessageId = 1,
            LastName = "Тест-событие",
            LastUpdatedMark = mark,
            LastStartTimeUtc = startUtc,
            CompletedAtUtc = completedUtc,
            CancelledAtUtc = cancelledUtc,
        };

    [Fact]
    public void LiveEventWithEmptyMark_ShowsStarted()
    {
        var entry = MakeEntry(mark: null, startUtc: DateTime.UtcNow.AddMinutes(-30));
        Assert.Equal("Началось", Invoke(entry));
    }

    [Fact]
    public void LiveEventWithScheduledMark_ShowsStarted()
    {
        var entry = MakeEntry(mark: "scheduled", startUtc: DateTime.UtcNow.AddHours(-2));
        Assert.Equal("Началось", Invoke(entry));
    }

    [Fact]
    public void FutureEventWithEmptyMark_StaysScheduled()
    {
        var entry = MakeEntry(mark: "", startUtc: DateTime.UtcNow.AddHours(2));
        Assert.Equal("Запланировано", Invoke(entry));
    }

    [Fact]
    public void CompletedMark_BeatsTimeLogic()
    {
        var entry = MakeEntry(
            mark: "completed", startUtc: DateTime.UtcNow.AddHours(-5),
            completedUtc: DateTime.UtcNow.AddHours(-3));
        Assert.Equal("Завершено", Invoke(entry));
    }

    [Fact]
    public void CancelledMark_BeatsTimeLogic()
    {
        var entry = MakeEntry(
            mark: "cancelled", startUtc: DateTime.UtcNow.AddHours(-5),
            cancelledUtc: DateTime.UtcNow.AddHours(-4));
        Assert.Equal("Отменено", Invoke(entry));
    }

    [Fact]
    public void CompletedTimestampWithScheduledMark_ShowsCompleted()
    {
        // Финальный таймстемп записи перебивает stale-метку «scheduled»
        // (запись с mark=scheduled, но CompletedAtUtc от backfill/resync).
        var entry = MakeEntry(
            mark: "scheduled", startUtc: DateTime.UtcNow.AddHours(-5),
            completedUtc: DateTime.UtcNow.AddHours(-3));
        Assert.Equal("Завершено", Invoke(entry));
    }

    [Fact]
    public void CancelledTimestampWithEmptyMark_ShowsCancelled()
    {
        var entry = MakeEntry(
            mark: "", startUtc: DateTime.UtcNow.AddHours(-5),
            cancelledUtc: DateTime.UtcNow.AddHours(-4));
        Assert.Equal("Отменено", Invoke(entry));
    }

    [Fact]
    public void ScheduledEventInFutureWithScheduledMark_StaysScheduled()
    {
        var entry = MakeEntry(mark: "scheduled", startUtc: DateTime.UtcNow.AddHours(3));
        Assert.Equal("Запланировано", Invoke(entry));
    }

    [Fact]
    public void StartedMark_ShowsStarted()
    {
        var entry = MakeEntry(mark: "started", startUtc: DateTime.UtcNow.AddMinutes(-10));
        Assert.Equal("Началось", Invoke(entry));
    }

    [Fact]
    public void RussianMarkPrefix_IsUsedAsIs()
    {
        var entry = MakeEntry(mark: "▶️ Событие началось: 12.08.2026 14:00", startUtc: null);
        Assert.Equal("▶️ Событие началось", Invoke(entry));
    }
}