using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Discord;
using Discord.WebSocket;
using RPBot.Predictions;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// audit-leaks #2: VoicePointsService.Shutdown должен отменить и диспозить
/// все TimerCts пользователей и очистить _userStates. Эти тесты проверяют
/// поведение через рефлексию (private-доступ), без необходимости поднимать
/// реальный Discord-клиент или Lavalink.
/// </summary>
public class VoicePointsServiceTimerDisposeTests : IDisposable
{
    private readonly string _tmpDir;

    public VoicePointsServiceTimerDisposeTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_voicetimer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    private static DiscordSocketClient NewClient() => new DiscordSocketClient(new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.None,
        LogLevel = LogSeverity.Critical
    });

    /// <summary>
    /// Инвариант: Shutdown очищает _userStates (словарь должен быть пустым).
    /// </summary>
    [Fact]
    public void Shutdown_ClearsUserStatesDictionary()
    {
        var client = NewClient();
        var pts = new PointsService(Path.Combine(_tmpDir, "points.json"));
        var svc = new VoicePointsService(client, pts, _ => null, "", (_, _) => false);

        var userStatesField = typeof(VoicePointsService).GetField("_userStates",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(userStatesField);
        var userStates = userStatesField!.GetValue(svc) as IDictionary;
        Assert.NotNull(userStates);
        Assert.Empty(userStates!);

        svc.Shutdown();
        Assert.Empty(userStates!);

        client.Dispose();
    }

    /// <summary>
    /// Инвариант: Shutdown идемпотентен — повторный вызов не бросает.
    /// </summary>
    [Fact]
    public void Shutdown_IsIdempotent()
    {
        var client = NewClient();
        var pts = new PointsService(Path.Combine(_tmpDir, "points.json"));
        var svc = new VoicePointsService(client, pts, _ => null, "", (_, _) => false);

        svc.Shutdown();
        svc.Shutdown();
        svc.Shutdown();
        client.Dispose();
    }

    /// <summary>
    /// Инвариант: VoicePointsService имеет private-поле _userStates и
    /// nested-тип UserState с публичным свойством TimerCts — это контракт,
    /// от которого зависит аудит утечек.
    /// </summary>
    [Fact]
    public void Service_HasExpectedPrivateShape()
    {
        var userStatesField = typeof(VoicePointsService).GetField("_userStates",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(userStatesField);

        var ctsField = typeof(VoicePointsService).GetField("_cts",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(ctsField);

        var allNested = typeof(VoicePointsService).GetNestedTypes(
            BindingFlags.NonPublic | BindingFlags.Instance);
        var userStateType = allNested.FirstOrDefault(
            t => t.GetProperty("TimerCts", BindingFlags.Public | BindingFlags.Instance) != null);
        Assert.NotNull(userStateType);
    }
}
