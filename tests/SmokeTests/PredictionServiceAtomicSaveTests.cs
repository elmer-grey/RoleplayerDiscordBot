using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.Predictions;
using RPBot.Util;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// 4.x — PredictionService:
///   * SaveStateAsync идёт через SafeJsonIO.WriteAtomicAsync (файл валиден);
///   * Shutdown идемпотентен.
/// Чтобы не поднимать полноценный сервис, тестируем атомарную запись и
/// поведение Shutdown через сконструированный сценарий.
/// </summary>
[Collection(nameof(BotConfigCollection))]
public class PredictionServiceAtomicSaveTests : IDisposable
{
    private readonly string _tmpDir;

    public PredictionServiceAtomicSaveTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_pred_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        // PredictionService читает BotConfig.GetDataDirectory() — нужно подменить
        // переменную окружения до создания инстанса.
        Environment.SetEnvironmentVariable("RPBOT_DATA_DIR", _tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
        Environment.SetEnvironmentVariable("RPBOT_DATA_DIR", null);
    }

    private static DiscordSocketClient NewClient() => new DiscordSocketClient(new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.None,
        LogLevel = LogSeverity.Critical
    });

    [Fact]
    public async Task SafeJsonIO_PredictionState_WritesValidJson()
    {
        // Эмулирует то, что делает PredictionService.SaveStateAsync после раунда 4.
        var path = Path.Combine(_tmpDir, "predictions_state.json");
        var snapshot = new Dictionary<ulong, object>
        {
            [1] = new { Title = "Test prediction", GuildId = 1UL }
        };
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });

        await SafeJsonIO.WriteAtomicAsync(path, json);

        Assert.True(File.Exists(path));
        var back = JsonSerializer.Deserialize<Dictionary<ulong, JsonElement>>(await File.ReadAllTextAsync(path));
        Assert.NotNull(back);
        Assert.True(back!.ContainsKey(1));
    }

    [Fact]
    public async Task SafeJsonIO_PredictionState_AtomicRenameLeavesNoTmp()
    {
        var path = Path.Combine(_tmpDir, "predictions_state.json");
        await SafeJsonIO.WriteAtomicAsync(path, "{}");
        await SafeJsonIO.WriteAtomicAsync(path, "{\"a\":1}");

        Assert.True(File.Exists(path));
        // После успешной записи временный .tmp должен отсутствовать.
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task SafeJsonIO_SerialWrites_LastWriteWins_NoTmpRemains()
    {
        var path = Path.Combine(_tmpDir, "predictions_state.json");
        await SafeJsonIO.WriteAtomicAsync(path, "{}");
        for (int i = 0; i < 10; i++)
        {
            await SafeJsonIO.WriteAtomicAsync(path, $"{{\"v\":{i}}}");
        }

        Assert.True(File.Exists(path));
        var content = await File.ReadAllTextAsync(path);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(content);
        Assert.NotNull(parsed);
        Assert.Equal(9, parsed!["v"]); // последняя запись — v:9
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void PredictionService_Shutdown_IsIdempotent()
    {
        var pts = new PointsService(Path.Combine(_tmpDir, "points.json"));
        var svc = new PredictionService(NewClient(), pts);

        svc.Shutdown();
        svc.Shutdown();
        svc.Shutdown();
    }

    [Fact]
    public async Task PredictionService_EnsureStateFile_AfterShutdown_DoesNotThrow()
    {
        var pts = new PointsService(Path.Combine(_tmpDir, "points.json"));
        var svc = new PredictionService(NewClient(), pts);
        svc.Shutdown();

        // Должен либо корректно создать файл, либо тихо вернуть false — не падать.
        var result = await svc.EnsureStateFileAsync();
        // Ничего не утверждаем про значение — главное, что не было исключения.
    }
}
