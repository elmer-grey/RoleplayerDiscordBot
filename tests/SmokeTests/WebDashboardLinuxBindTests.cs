using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RPBot.Web;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Тесты на поведение WebDashboardService при бинде префиксов
/// в разных ОС. Особенно важно для переезда на Linux VPS — на
/// не-Windows дашборд должен биндиться ТОЛЬКО на 127.0.0.1/[::1],
/// даже если хост задан как 0.0.0.0 / *.
/// </summary>
[Collection(nameof(BotLoggerCollection))]
public class WebDashboardLinuxBindTests : IAsyncLifetime
{
    private string _tmpLogDir = "";
    private WebDashboardService? _svc;

    public Task InitializeAsync()
    {
        _tmpLogDir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_lnx_" + Guid.NewGuid().ToString("N"));
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

    private static int PickPort()
    {
        // Берём свободный порт выше эфемерного диапазона.
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// Поведение одинаково для Windows и Linux: при явном host="0.0.0.0" или host="*"
    /// на Windows биндятся все IP (как было), на Linux — только loopback.
    /// Тест адаптивный: проверяем, что _prefixes содержит только loopback на не-Windows.
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("*")]
    [InlineData("")]
    public void WildcardHost_OnLinux_BindsOnlyLoopback(string host)
    {
        if (OperatingSystem.IsWindows())
        {
            // На Windows ожидаем хотя бы loopback (fallback), но не empty.
            // Проверяем что сервис сконструировался без падения.
            var port = PickPort();
            var svc = MakeService(host, port);
            Assert.NotNull(svc);
        }
        else
        {
            var port = PickPort();
            var svc = MakeService(host, port);
            var prefixes = GetPrefixes(svc);
            Assert.NotEmpty(prefixes);

            // На Linux ВСЕ префиксы должны быть loopback (127.0.0.1 или [::1]).
            foreach (var p in prefixes)
            {
                Assert.True(
                    p.Contains("127.0.0.1") || p.Contains("[::1]"),
                    $"На Linux ожидается loopback-префикс, получили: {p}");
            }
        }
    }

    /// <summary>
    /// Конкретный IP (например 127.0.0.1 или 192.168.x.x) на любой ОС
    /// должен пройти как есть — без wildcard-логики.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public void ConcreteHost_IsPassedThrough(string host)
    {
        var port = PickPort();
        var svc = MakeService(host, port);
        var prefixes = GetPrefixes(svc);
        Assert.Single(prefixes);
        Assert.Contains($":{port}/", prefixes[0]);
    }

    private WebDashboardService MakeService(string host, int port)
    {
        // Минимальный набор Func-ов — тестируем только биндинг, реальные хендлеры не вызываем.
        object Health() => new { ok = true };
        var serverConfigs = new Dictionary<ulong, ServerConfig>();
        object Sessions() => Array.Empty<object>();

        var svc = new WebDashboardService(
            host: host,
            port: port,
            healthProvider: Health,
            serverConfigsProvider: () => serverConfigs,
            sessionsProvider: Sessions,
            eventsProvider: () => Array.Empty<object>(),
            clientProvider: () => null,
            rollsTodayProvider: () => 0,
            activeSessionsProvider: () => 0,
            chatMessagesTodayProvider: () => 0,
            usersInVoiceProvider: () => 0,
            activityProvider: () => Array.Empty<RPBot.Web.ActivityBucket>(),
            versionProvider: () => "test",
            uptimeProvider: () => TimeSpan.Zero,
            systemsProvider: () => Array.Empty<object>(),
            maxLogs: 100);
        _svc = svc;
        return svc;
    }

    private static List<string> GetPrefixes(WebDashboardService svc)
    {
        var field = typeof(WebDashboardService).GetField(
            "_prefixes", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (List<string>)field!.GetValue(svc)!;
    }
}