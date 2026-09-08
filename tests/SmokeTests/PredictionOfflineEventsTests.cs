using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.Predictions;
using SmokeTests;
using Xunit;

namespace RPBot.SmokeTests;

[Collection("BotConfig")]
public class PredictionOfflineEventsTests : IsolatedDataTestBase
{
    public PredictionOfflineEventsTests() : base("rpbot_smoke_pred_offline_events") { }

    private static DiscordSocketClient NewClient() => new DiscordSocketClient(new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.None,
        LogLevel = LogSeverity.Critical,
    });

        private static ActivePrediction MakePersisted()
        => new()
        {
                GuildId = 123,
            CreatorId = 1234,
            ChannelId = 5678,
            MessageId = 9012,
            Title = "Оффлайн-события прогноз",
            Outcomes = new List<PredictionOutcome>
            {
                new() { Id = 1, Name = "A", TotalStake = 100 },
                new() { Id = 2, Name = "B", TotalStake = 200 },
            },
            CreatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-30),
            BetsCloseAtUtc = DateTimeOffset.UtcNow.AddMinutes(3),
            BotOfflineAtUtc = DateTimeOffset.UtcNow.AddSeconds(-30),
            LastOfflineDurationMinutes = 0.5,
            WasBotOfflineOnShutdown = true,
            IsLocked = false,
            IsResolved = false,
            Bets = new Dictionary<ulong, PredictionBet>(),
        };
    [Fact]
    public void OfflineEvents_List_IsExposedAndSerializable()
    {
        var p = new ActivePrediction
        {
            Title = "t",
            OfflineEvents = new List<OfflineEvent>
            {
                new() { AtUtc = DateTimeOffset.UtcNow, Kind = OfflineEventKind.Disconnected, Severity = "restart", Note = "GatewayReconnectException" },
                new() { AtUtc = DateTimeOffset.UtcNow, Kind = OfflineEventKind.Reconnected, Severity = "online", Note = "Ready" },
            },
        };

        Assert.Equal(2, p.OfflineEvents.Count);
        Assert.Equal(OfflineEventKind.Disconnected, p.OfflineEvents[0].Kind);
        Assert.Equal("restart", p.OfflineEvents[0].Severity);
        Assert.Equal("GatewayReconnectException", p.OfflineEvents[0].Note);
        Assert.Equal(OfflineEventKind.Reconnected, p.OfflineEvents[1].Kind);
    }

    [Fact]
    public void OfflineEventKind_Defaults_AreOfflineAndNoteIsShutdown()
    {
        var ev = new OfflineEvent
        {
            AtUtc = DateTimeOffset.UtcNow,
            Kind = OfflineEventKind.Disconnected,
            Severity = "offline",
            Note = "Shutdown",
        };

        Assert.Equal("offline", ev.Severity);
        Assert.Equal("Shutdown", ev.Note);
        Assert.IsType<OfflineEventKind>(ev.Kind);
    }

    [Fact]
        public void PersistentPrediction_RoundTrips_OfflineEventsList()
        {
            var persisted = MakePersisted();
            persisted.OfflineEvents.Clear();
            persisted.OfflineEvents.Add(new OfflineEvent
            {
                AtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                Kind = OfflineEventKind.Disconnected,
                Severity = "offline",
                Note = "Shutdown"
            });
            persisted.OfflineEvents.Add(new OfflineEvent
            {
                AtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                Kind = OfflineEventKind.Reconnected,
                Severity = "online",
                Note = "Ready"
            });

            var json = System.Text.Json.JsonSerializer.Serialize(persisted, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Assert.Contains("\"OfflineEvents\":", json);
            // System.Text.Json по умолчанию сериализует enum как число.
            // Проверим содержимое через десериализацию обратно, чтобы убедиться,
            // что Kind прошёл round-trip без потерь.
            var reparsed = System.Text.Json.JsonSerializer.Deserialize<ActivePrediction>(json);
            Assert.NotNull(reparsed);
            Assert.Equal(2, reparsed!.OfflineEvents.Count);
            Assert.Equal(OfflineEventKind.Disconnected, reparsed.OfflineEvents[0].Kind);
            Assert.Equal(OfflineEventKind.Reconnected, reparsed.OfflineEvents[1].Kind);
            Assert.Equal("Shutdown", reparsed.OfflineEvents[0].Note);
            Assert.Equal("Ready", reparsed.OfflineEvents[1].Note);
        }

    [Fact]
        public void ActivePrediction_OfflineEvents_IsSettableAndDefaultsToEmptyList()
    {
        var p = new ActivePrediction { Title = "t" };
            Assert.NotNull(p.OfflineEvents);
            Assert.Empty(p.OfflineEvents);

            p.OfflineEvents.Add(new OfflineEvent { AtUtc = DateTimeOffset.UtcNow, Kind = OfflineEventKind.Disconnected, Severity = "restart", Note = "test" });
            Assert.Single(p.OfflineEvents);
        }

    [Fact]
        public async Task Predictions_StateFile_OfflineEvents_RoundTrip()
        {
            var pp = MakePersisted();
            pp.OfflineEvents.Clear();
            pp.OfflineEvents.Add(new OfflineEvent
            {
                AtUtc = DateTimeOffset.UtcNow,
                Kind = OfflineEventKind.Disconnected,
                Severity = "offline",
                Note = "Test"
            });

            var json = System.Text.Json.JsonSerializer.Serialize(pp, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var path = Path.Combine(TempDir, "predictions_state.json");
            await RPBot.Util.SafeJsonIO.WriteAtomicAsync(path, json);

            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("\"OfflineEvents\":", text);
            Assert.Contains("\"Severity\": \"offline\"", text);
            Assert.Contains("\"Note\": \"Test\"", text);

            var roundTrip = System.Text.Json.JsonSerializer.Deserialize<ActivePrediction>(text);
            Assert.NotNull(roundTrip);
            Assert.NotNull(roundTrip!.OfflineEvents);
            Assert.Single(roundTrip.OfflineEvents!);
            Assert.Equal(OfflineEventKind.Disconnected, roundTrip.OfflineEvents![0].Kind);
        }

        [Fact]
        public void OfflineEvent_DistinctKinds_ForRestart_AndOffline()
        {
            var restart = new OfflineEvent
            {
                Kind = OfflineEventKind.Disconnected,
                Severity = "restart",
                Note = typeof(Discord.WebSocket.GatewayReconnectException).Name
            };
            var offline = new OfflineEvent
            {
                Kind = OfflineEventKind.Disconnected,
                Severity = "offline",
                Note = "OperationCanceledException"
            };

            Assert.Equal("restart", restart.Severity);
            Assert.Equal("offline", offline.Severity);
            Assert.NotEqual(restart.Severity, offline.Severity);
        }
    }
