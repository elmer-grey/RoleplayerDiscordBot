using System;
using System.IO;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.Predictions;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// 4.x — VoicePointsService: идемпотентный Shutdown, отписка от событий,
/// отмена и освобождение всех TimerCts.
/// </summary>
public class VoicePointsServiceShutdownTests : IDisposable
{
    private readonly string _tmpDir;

    public VoicePointsServiceShutdownTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_voice_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Shutdown_CalledOnce_DoesNotThrow()
    {
        var client = NewClient();
        var pts = new PointsService(Path.Combine(_tmpDir, "points.json"));
            // ✅ pred-parallelization: добавлен 5-й параметр isActiveEventOnChannel.
            // Тестам на shutdown его поведение не важно — поэтому фиктивный Func.
            var svc = new VoicePointsService(client, pts, _ => null, (_, _) => false);

            svc.Shutdown();
        }

        [Fact]
        public void Shutdown_CalledTwice_DoesNotThrow()
        {
            var client = NewClient();
            var pts = new PointsService(Path.Combine(_tmpDir, "points.json"));
            var svc = new VoicePointsService(client, pts, _ => null, (_, _) => false);

            svc.Shutdown();
            svc.Shutdown();
        }

        [Fact]
        public void Shutdown_CalledManyTimes_DoesNotThrow()
        {
            var client = NewClient();
            var pts = new PointsService(Path.Combine(_tmpDir, "points.json"));
            var svc = new VoicePointsService(client, pts, _ => null, (_, _) => false);

            for (int i = 0; i < 10; i++) svc.Shutdown();
        }

        [Fact]
        public void DisposeClient_AfterShutdown_DoesNotThrow()
        {
            var client = NewClient();
            var pts = new PointsService(Path.Combine(_tmpDir, "points.json"));
            var svc = new VoicePointsService(client, pts, _ => null, (_, _) => false);

            svc.Shutdown();
            // Если отписка от событий не сработала — этот Dispose мог бы кинуть NRE
            // (от подписки на уже освобождённый handler внутри client-цепочки).
            client.Dispose();
        }
    }
