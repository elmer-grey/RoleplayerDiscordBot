using System;
using Discord.WebSocket;
using RPBot;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Smoke-тесты DisconnectReasonTranslator: покрывают основные ветви
/// классификации (DNS, таймаут, websocket, ручной реконнект, неизвестная причина).
/// </summary>
public class DisconnectReasonTranslatorTests
{
    [Fact]
    public void NullException_ReturnsUnknownReason()
    {
        Assert.Equal("Неизвестная причина", DisconnectReasonTranslator.GetFriendlyReason(null!));
    }

    [Fact]
    public void HostUnknown_ReturnsDnsError()
    {
        var ex = new Exception("Host Unknown");
        var msg = DisconnectReasonTranslator.GetFriendlyReason(ex);
        Assert.Contains("DNS", msg);
    }

    [Fact]
    public void ConnectionRefused_ReturnsRefusedMessage()
    {
        var ex = new Exception("Connection refused");
        Assert.Contains("отклонено", DisconnectReasonTranslator.GetFriendlyReason(ex));
    }

    [Fact]
    public void TimedOut_ReturnsTimeout()
    {
        var ex = new Exception("Operation timed out");
        Assert.Contains("Таймаут", DisconnectReasonTranslator.GetFriendlyReason(ex));
    }

    [Fact]
    public void WebSocket_ReturnsWebSocketMessage()
    {
        var ex = new Exception("WebSocket exception occurred");
        Assert.Contains("WebSocket", DisconnectReasonTranslator.GetFriendlyReason(ex));
    }

    [Fact]
    public void ManualReconnect_ReturnsManualMessage()
    {
        var ex = new ManualReconnectException();
        Assert.Contains("Ручной", DisconnectReasonTranslator.GetFriendlyReason(ex));
    }

    [Fact]
    public void BackgroundDisconnect_ReturnsBackgroundMessage()
    {
        var ex = new BackgroundDisconnectException();
        Assert.Contains("Фоновая", DisconnectReasonTranslator.GetFriendlyReason(ex));
    }

    [Fact]
    public void GatewayReconnect_ReturnsPlanned()
    {
        var ex = new GatewayReconnectException("planned");
        Assert.Contains("Плановый", DisconnectReasonTranslator.GetFriendlyReason(ex));
    }

    [Fact]
    public void RateLimit_ReturnsRateLimit()
    {
        var ex = new Exception("Rate limit exceeded");
        Assert.Contains("Rate", DisconnectReasonTranslator.GetFriendlyReason(ex));
    }

    [Fact]
    public void UnknownException_ReturnsTypeName()
    {
        var ex = new InvalidOperationException("Some weird thing");
        var msg = DisconnectReasonTranslator.GetFriendlyReason(ex);
        Assert.Equal("InvalidOperationException", msg);
    }

    [Fact]
    public void GetEmojiForDns_ReturnsGlobe()
    {
        Assert.Equal("🌐", DisconnectReasonTranslator.GetEmojiForReason("DNS ошибка"));
    }

    [Fact]
    public void GetEmojiForTimeout_ReturnsTimer()
    {
        Assert.Equal("⏱️", DisconnectReasonTranslator.GetEmojiForReason("Таймаут соединения"));
    }

    [Fact]
    public void GetEmojiForUnknown_ReturnsX()
    {
        Assert.Equal("❌", DisconnectReasonTranslator.GetEmojiForReason("Какая-то неизвестная причина"));
    }
}
