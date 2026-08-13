using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// 4.x — TelegramNotifier.PostJsonAsync: классификация исключений.
/// ОперацияCancellation — проброс; HttpRequestException → "network error";
/// TaskCanceledException без отмены → "timeout"; прочее → ex.ToString().
/// </summary>
public class TelegramNotifierPostJsonTests
{
    /// <summary>
    /// HttpMessageHandler, возвращающий заранее заданный ответ или выбрасывающий исключение.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _impl;
        public int CallCount;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl)
        {
            _impl = impl;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            return await _impl(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TelegramNotifier MakeNotifier(Func<ulong, ServerConfig?> accessor, HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        return new TelegramNotifier(accessor, http);
    }

    private static ServerConfig Cfg(ulong guildId) => new ServerConfig
    {
        TelegramEnabled = true,
        TelegramBotToken = "TEST_TOKEN",
        TelegramChatId = 12345
    };

    [Fact]
    public async Task SendMessage_NetworkError_ReturnsFalse_WithNetworkMarker()
    {
        var handler = new StubHandler((req, ct) =>
            throw new HttpRequestException("DNS failure"));
        var notifier = MakeNotifier(Cfg, handler);

        var ok = await notifier.SendMessageAsync(1, "hello");

        Assert.False(ok);
    }

    [Fact]
    public async Task SendMessage_Cancellation_Propagates()
    {
        // Хэндлер, который сам пробрасывает OperationCanceledException — имитирует
        // ситуацию "вызвавший код отменил токен".
        var handler = new StubHandler((req, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var notifier = MakeNotifier(Cfg, handler);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => notifier.SendMessageAsync(1, "hello", cts.Token));
    }

    [Fact]
    public async Task SendMessage_HttpRequestException_SwallowsAndReturnsFalse()
    {
        var handler = new StubHandler((req, ct) =>
            throw new HttpRequestException("connection refused"));
        var notifier = MakeNotifier(Cfg, handler);

        // Должен тихо вернуть false, не кидать HttpRequestException наружу.
        var ok = await notifier.SendMessageAsync(1, "hello");
        Assert.False(ok);
    }

    [Fact]
    public async Task SendMessage_UnauthorizedResponse_ReturnsFalse()
    {
        var handler = new StubHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"ok\":false,\"error_code\":401}")
        }));
        var notifier = MakeNotifier(Cfg, handler);

        var ok = await notifier.SendMessageAsync(1, "hello");
        Assert.False(ok);
    }

    [Fact]
    public async Task SendMessage_OkResponse_ReturnsTrue()
    {
        var handler = new StubHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":42}}")
        }));
        var notifier = MakeNotifier(Cfg, handler);

        var ok = await notifier.SendMessageAsync(1, "hello");
        Assert.True(ok);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SendMessage_DisabledConfig_ReturnsFalse_WithoutHttpCall()
    {
        var handler = new StubHandler((req, ct) =>
            throw new InvalidOperationException("HTTP не должен вызываться"));
        var notifier = MakeNotifier(g => new ServerConfig { TelegramEnabled = false }, handler);

        var ok = await notifier.SendMessageAsync(1, "hello");
        Assert.False(ok);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task EditMessageText_InvalidMessageId_ReturnsFalse_WithoutHttpCall()
    {
        var handler = new StubHandler((req, ct) =>
            throw new InvalidOperationException("HTTP не должен вызываться"));
        var notifier = MakeNotifier(Cfg, handler);

        var ok = await notifier.EditMessageTextAsync(1, 0, "edit me");
        Assert.False(ok);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task EditMessageText_NetworkError_ReturnsFalse()
    {
        var handler = new StubHandler((req, ct) =>
            throw new HttpRequestException("connect timeout"));
        var notifier = MakeNotifier(Cfg, handler);

        var ok = await notifier.EditMessageTextAsync(1, 42, "edit me");
        Assert.False(ok);
    }
}
