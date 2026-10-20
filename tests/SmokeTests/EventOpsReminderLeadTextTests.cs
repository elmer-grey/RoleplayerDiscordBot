using System;
using System.Reflection;
using RPBot.EventOps;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// 🩹 reminder-lead-text: напоминание должно говорить, сколько реально осталось
/// до события, а не жёсткое «через час» (кейс 2026-10-20: catch-up шлёт позже
/// планового start−1ч, пост-стартовые напоминания не шлём вовсе).
/// </summary>
public class EventOpsReminderLeadTextTests
{
    private static string LeadText(EventAnnouncementEntry entry) =>
        (string)typeof(EventOpsLifecycleService)
            .GetMethod("BuildReminderLeadText", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { entry })!;

    private static string Phrase(EventAnnouncementEntry entry, string name) =>
        (string)typeof(EventOpsLifecycleService)
            .GetMethod("BuildReminderPhrase", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { entry, name })!;

    [Theory]
    [InlineData(60.1, "через час")]   // плановая отправка (start-1ч + джиттер)
    [InlineData(20.0, "через 20 мин")]
    [InlineData(1.5, "через 2 мин")]
    [InlineData(0.005, "прямо сейчас")] // < 30 c
    [InlineData(-0.05, "событие уже началось")] // старт уже прошёл
    public void LeadText_ReflectsRealRemaining(double minutes, string expected)
    {
        var entry = new EventAnnouncementEntry
        {
            LastStartTimeUtc = DateTimeOffset.UtcNow.AddMinutes(minutes),
        };
        Assert.Equal(expected, LeadText(entry));
    }

    [Fact]
    public void Phrase_LateSend_MentionsRealRemaining()
    {
        var entry = new EventAnnouncementEntry
        {
            LastName = "Собрание",
            LastStartTimeUtc = DateTimeOffset.UtcNow.AddMinutes(20),
        };
        Assert.Equal("через 20 мин начнётся событие **Собрание**",
            Phrase(entry, "**Собрание**"));
    }

    [Fact]
    public void Phrase_AlreadyStarted_SaysStarted()
    {
        var entry = new EventAnnouncementEntry
        {
            LastName = "Собрание",
            LastStartTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        Assert.Equal("событие **Собрание** уже началось",
            Phrase(entry, "**Собрание**"));
    }

    [Fact]
    public void Phrase_WithoutStartTime_FallsBackToLegacyHour()
    {
        var entry = new EventAnnouncementEntry { LastName = "Собрание" };
        Assert.Equal("через час начнётся событие **Собрание**",
            Phrase(entry, "**Собрание**"));
    }
}