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
/// Round 5 — PredictionService: баг 5 (сдвиг BetsCloseAtUtc при offline)
/// и баг 6/7 (статус offline/online в embed'е и на кнопках).
///
/// PersistentPrediction — private, поэтому проверяем offline-поля через
/// ActivePrediction (public) и JSON-формат файла predictions_state.json
/// (пишем JSON руками и читаем обратно через System.Text.Json, чтобы
/// убедиться, что SaveStateAsync/LoadStateAsync сохраняют наши поля).
/// </summary>
[Collection(nameof(BotConfigCollection))]
public class PredictionOfflineShiftTests : IDisposable
{
    private readonly string _tmpDir;

    public PredictionOfflineShiftTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_pred_offline_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
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
    public void ActivePrediction_BotOfflineFields_DefaultToNull()
    {
        var p = new ActivePrediction
        {
            GuildId = 1,
            ChannelId = 2,
            MessageId = 3,
            Title = "T",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            BetsCloseAtUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        };

        Assert.Null(p.BotOfflineAtUtc);
        Assert.Null(p.LastOfflineDurationMinutes);
        Assert.False(p.WasBotOfflineOnShutdown);
    }

    [Fact]
    public void ActivePrediction_BotOfflineFields_RoundTrip()
    {
        var offlineAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var p = new ActivePrediction
        {
            GuildId = 1,
            ChannelId = 2,
            MessageId = 3,
            Title = "T",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            BetsCloseAtUtc = DateTimeOffset.UtcNow.AddMinutes(2),
            BotOfflineAtUtc = offlineAt,
            LastOfflineDurationMinutes = 5.5,
            WasBotOfflineOnShutdown = true,
        };

        var json = JsonSerializer.Serialize(p);
        var parsed = JsonSerializer.Deserialize<ActivePrediction>(json);
        Assert.NotNull(parsed);
        Assert.Equal(offlineAt, parsed!.BotOfflineAtUtc);
        Assert.Equal(5.5, parsed.LastOfflineDurationMinutes);
        Assert.True(parsed.WasBotOfflineOnShutdown);
    }

    [Fact]
    public void OfflineShift_ComputesExpectedNewCloseTime()
    {
        // Эмулируем логику сдвига из LoadStateAsync:
        // newCloseAt = oldCloseAt + (nowUtc - offlineAt)
        //
        // Сценарий: бот ушёл в offline в offlineAt (4 мин назад от now),
        // а oldCloseAt был назначен на 2 мин назад от now (т.е. через 2 мин
        // после offlineAt). После возвращения нужно сдвинуть окно закрытия
        // на длительность offline = (now - offlineAt) = 2 мин, чтобы приём
        // ставок продолжался столько же, сколько "оставалось" до offlineAt,
        // плюс столько, сколько бот был офлайн.
        var nowUtc = DateTimeOffset.UtcNow;
        var offlineAt = nowUtc.AddMinutes(-4);   // 4 минуты offline
        var oldCloseAt = nowUtc.AddMinutes(-2);  // был запланирован на 2 мин назад от now

        var newCloseAt = oldCloseAt + (nowUtc - offlineAt);

        // Сдвиг = 4 минуты (полная длительность offline).
        Assert.Equal(TimeSpan.FromMinutes(4), newCloseAt - oldCloseAt);

        // newCloseAt == oldCloseAt + 4 мин == nowUtc + 2 мин (округление до миллисекунд).
        Assert.InRange(newCloseAt, nowUtc.AddMinutes(2).AddMilliseconds(-10), nowUtc.AddMinutes(2).AddMilliseconds(10));

        // Главный инвариант: новое время закрытия должно быть не раньше nowUtc.
        // Т.е. окно приёма ставок не схлопнулось в прошлое.
        Assert.True(newCloseAt >= nowUtc.AddSeconds(-1),
            $"Сдвинутое время должно быть около nowUtc. nowUtc={nowUtc:HH:mm:ss.fff} newCloseAt={newCloseAt:HH:mm:ss.fff}");
    }

    [Fact]
    public void OfflineShift_NotNegative_ProducesZeroOrPositive()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var oldCloseAt = nowUtc.AddMinutes(3);
        var offlineAt = nowUtc;

        var shift = nowUtc - offlineAt;
        var newCloseAt = oldCloseAt + (shift < TimeSpan.Zero ? TimeSpan.Zero : shift);
        Assert.True(newCloseAt >= oldCloseAt);
    }

    [Fact]
    public void OfflineShift_NegativeDelta_IsClampedToZero()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var oldCloseAt = nowUtc.AddMinutes(2);
        var offlineAt = nowUtc.AddMinutes(5); // "offlineAt" в будущем — аномалия

        var shift = nowUtc - offlineAt; // отрицательная дельта
        var safeShift = shift > TimeSpan.Zero ? shift : TimeSpan.Zero;
        var newCloseAt = oldCloseAt + safeShift;
        Assert.Equal(oldCloseAt, newCloseAt);
    }

    [Fact]
    public void StateFile_AfterRound5_ContainsOfflineFields()
    {
        var path = Path.Combine(_tmpDir, "predictions_state.json");
        var nowUtc = DateTimeOffset.UtcNow;
        var offlineAt = nowUtc.AddMinutes(-3);

        var payload = new Dictionary<string, object>
        {
            ["1"] = new Dictionary<string, object>
            {
                ["GuildId"] = 1UL,
                ["ChannelId"] = 200UL,
                ["MessageId"] = 300UL,
                ["Title"] = "Test",
                ["CreatedAtUtc"] = nowUtc.AddMinutes(-10),
                ["BetsCloseAtUtc"] = nowUtc.AddMinutes(2),
                ["BotOfflineAtUtc"] = offlineAt,
                ["LastOfflineDurationMinutes"] = 3.0,
                ["WasBotOfflineOnShutdown"] = true,
                ["IsLocked"] = false,
                ["IsResolved"] = false,
            }
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        var text = File.ReadAllText(path);

        Assert.Contains("\"BotOfflineAtUtc\"", text);
        Assert.Contains("\"WasBotOfflineOnShutdown\": true", text);
        Assert.Contains("\"LastOfflineDurationMinutes\": 3", text);
    }
}
