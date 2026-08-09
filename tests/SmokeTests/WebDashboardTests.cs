using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RPBot.Web;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Изолированные тесты на WebDashboardService: поднимаем HTTP-listener на свободном порту
/// без подключения к Discord и проверяем поведение rate-limit/health/shutdown.
/// </summary>
public class WebDashboardTests : IAsyncLifetime
{
    private WebDashboardService? _svc;

    public Task InitializeAsync()
    {
        // Используем изолированный временный каталог, чтобы тесты не оставляли
        // записи в реальном production-каталоге данных (AppData\RPBot\Logs).
        _tmpLogDir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpLogDir);
        BotLogger.Initialize(_tmpLogDir, DateTime.Now);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (_svc != null)
        {
            try { _svc.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { _svc.Dispose(); } catch { }
            _svc = null;
        }
        try { Directory.Delete(_tmpLogDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private string _tmpLogDir = "";

    private static readonly int[] _allowedPorts = Enumerable.Range(50060, 41).ToArray();
    private static int _nextPortIndex = 0;

    private static int PickPort()
    {
        // Round-robin over pre-registered ports (see Add-UrlAcl during setup).
        var idx = Interlocked.Increment(ref _nextPortIndex);
        return _allowedPorts[idx % _allowedPorts.Length];
    }

    private WebDashboardService StartService()
    {
        var port = PickPort();
        var svc = new WebDashboardService(
            host: "127.0.0.1",
            port: port,
            healthProvider: () => new
            {
                Connected = true,
                Guilds = 1,
                StartupType = "Test",
                UtcNow = DateTimeOffset.UtcNow,
                Version = "smoke",
                Uptime = TimeSpan.FromSeconds(1),
            },
            serverConfigsProvider: () => new Dictionary<ulong, ServerConfig>(),
            sessionsProvider: () => Array.Empty<object>(),
            rollsTodayProvider: () => 7,
            activeSessionsProvider: () => 1,
            chatMessagesTodayProvider: () => 42,
            usersInVoiceProvider: () => 2,
            activityProvider: () => Array.Empty<ActivityBucket>(),
            versionProvider: () => "smoke",
            uptimeProvider: () => TimeSpan.FromSeconds(1));

        svc.Start();
        // Префикс известен из конструктора (port задаётся явно, без 0).
        BaseAddress = $"http://127.0.0.1:{port}";
        _svc = svc;
        return svc;
    }

    private static string GetPrefix(WebDashboardService svc)
    {
        // Префикс известен из конструктора; reflection используется только как запасной вариант.
        var field = typeof(WebDashboardService).GetField("_listener",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var listener = field!.GetValue(svc) as System.Net.HttpListener;
        Assert.NotNull(listener);
        return listener!.Prefixes.First();
    }

    private string? BaseAddress { get; set; }

    private static async Task<HttpResponseMessage> GetAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        return await http.GetAsync(url);
    }

    [Fact]
    public async Task Start_OnFreePort_Succeeds()
    {
        var svc = StartService();
        Assert.NotNull(BaseAddress);
        var resp = await GetAsync(BaseAddress + "/api/health");
        Assert.True(resp.IsSuccessStatusCode, $"Ожидался 2xx, получили {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsWrappedPayload_WithStatsAndTimestamp()
    {
        StartService();
        var resp = await GetAsync(BaseAddress + "/api/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("Provider", out _), "Должно быть поле Provider");
        Assert.True(root.TryGetProperty("Timestamp", out _), "Должно быть поле Timestamp");
        Assert.True(root.TryGetProperty("Stats", out var stats), "Должно быть поле Stats");

        Assert.True(stats.TryGetProperty("RollsToday", out _));
        Assert.True(stats.TryGetProperty("ActiveSessions", out _));
        Assert.True(stats.TryGetProperty("ChatMessagesToday", out _));
        Assert.True(stats.TryGetProperty("UsersInVoice", out _));
        Assert.True(stats.TryGetProperty("LogRecords", out _));
    }

    [Fact]
    public async Task RateLimit_61stRequest_Returns429_WithRetryAfter()
    {
        StartService();
        // Делаем 60 запросов с одного IP — все должны пройти.
        var okCount = 0;
        for (int i = 0; i < 60; i++)
        {
            var r = await GetAsync(BaseAddress + "/api/health");
            if ((int)r.StatusCode == 200) okCount++;
        }
        Assert.Equal(60, okCount);

        // 61-й должен быть отклонён.
        var rejected = await GetAsync(BaseAddress + "/api/health");
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task UnknownEndpoint_Returns404()
    {
        StartService();
        var resp = await GetAsync(BaseAddress + "/api/does-not-exist");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }
}