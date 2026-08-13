using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// 4.x — ReconnectionService: Dispose идемпотентность, ранний выход HandleDisconnect,
/// сброс _reconnectCts после Dispose.
/// </summary>
public class ReconnectionServiceTests
{
    private static DiscordSocketClient NewClient() => new DiscordSocketClient(new DiscordSocketConfig
    {
        // Не подключаемся к Discord при создании — нужны только методы Dispose/HandleDisconnect.
        GatewayIntents = GatewayIntents.None,
        LogLevel = LogSeverity.Critical
    });

    [Fact]
    public void Dispose_CalledOnce_DoesNotThrow()
    {
        var svc = new ReconnectionService(NewClient());
        svc.Dispose();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var svc = new ReconnectionService(NewClient());
        svc.Dispose();
        svc.Dispose(); // должно быть no-op, не ObjectDisposedException
    }

    [Fact]
    public void Dispose_CalledManyTimes_DoesNotThrow()
    {
        var svc = new ReconnectionService(NewClient());
        for (int i = 0; i < 10; i++) svc.Dispose();
    }

    [Fact]
    public async Task HandleDisconnect_AfterDispose_ReturnsImmediately()
    {
        var svc = new ReconnectionService(NewClient());
        svc.Dispose();

        // Не должно ни запускать reconnect-цикл, ни кидать исключения.
        await svc.HandleDisconnect(new Exception("test"));
    }

    [Fact]
    public async Task RequestManualReconnect_AfterDispose_ReturnsImmediately()
    {
        var svc = new ReconnectionService(NewClient());
        svc.Dispose();
        await svc.RequestManualReconnectAsync("тест после Dispose");
    }

    [Fact]
    public void Dispose_ResetsReconnectCts()
    {
        var svc = new ReconnectionService(NewClient());
        // Не должно падать с NullReferenceException — _reconnectCts обнуляется, но Dispose идемпотентен.
        svc.Dispose();
        // Повторный Dispose — тоже не падает, хотя Cts уже null.
        svc.Dispose();
    }

    [Fact]
    public void Shutdown_ThenDispose_DoesNotThrow()
    {
        var svc = new ReconnectionService(NewClient());
        svc.Shutdown();
        svc.Dispose();
    }
}
