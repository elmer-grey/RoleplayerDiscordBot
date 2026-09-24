using System;
using System.Net.Http;
using System.Reflection;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// audit-leaks #1: TelegramNotifier.HttpClient должен освобождаться при Dispose,
/// если был создан внутри (own), но НЕ диспозиться, если инжектирован снаружи.
/// </summary>
public class TelegramNotifierDisposeTests
{
    [Fact]
    public void Dispose_OwnsHttpClient_DisposesIt()
    {
        var notifier = new TelegramNotifier(_ => null);

        var httpField = typeof(TelegramNotifier).GetField("_httpClient",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(httpField);
        var http = httpField!.GetValue(notifier) as HttpClient;
        Assert.NotNull(http);

        notifier.Dispose();

        // После Dispose бросает ObjectDisposedException при попытке использовать.
        Assert.Throws<ObjectDisposedException>(() => http!.Timeout = TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Dispose_InjectedHttpClient_DoesNotDisposeIt()
    {
        var injected = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var notifier = new TelegramNotifier(_ => null, injected);

        notifier.Dispose();

        // Инжектированный HttpClient должен быть жив.
        injected.Timeout = TimeSpan.FromSeconds(5);
        Assert.Equal(TimeSpan.FromSeconds(5), injected.Timeout);
        injected.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var notifier = new TelegramNotifier(_ => null);
        notifier.Dispose();
        notifier.Dispose(); // повторно — без исключений
        notifier.Dispose();
    }
}
