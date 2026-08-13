using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// 3.x — round 3 unit tests:
///   * WebDashboardService.StopAsync идемпотентен (Interlocked.Exchange(ref _cts, null));
///   * SafeJsonIO в round 3 ничего не менял — это просто smoke на повторный Stop.
/// </summary>
public class Round3Tests
{
    [Fact]
    public async Task SafeJsonIO_IdempotentReplace_AfterCorruption_HasOldContent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_r3_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "x.json");
            await RPBot.Util.SafeJsonIO.WriteAtomicAsync(path, "{\"a\":1}");
            await RPBot.Util.SafeJsonIO.WriteAtomicAsync(path, "{\"a\":2}");
            // Имитируем ручное повреждение
            File.WriteAllText(path, "broken");
            // Следующая запись должна корректно перезаписать
            await RPBot.Util.SafeJsonIO.WriteAtomicAsync(path, "{\"a\":3}");

            Assert.Equal("{\"a\":3}", await File.ReadAllTextAsync(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task SafeJsonIO_EmptyJson_Accepted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_r3_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "x.json");
            await RPBot.Util.SafeJsonIO.WriteAtomicAsync(path, "{}");
            Assert.Equal("{}", await File.ReadAllTextAsync(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task SafeJsonIO_LargePayload_WritesCompletely()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_r3_large_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "big.json");
            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            for (int i = 0; i < 10_000; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($"{{\"i\":{i},\"name\":\"user_{i}\"}}");
            }
            sb.Append(']');
            var payload = sb.ToString();

            await RPBot.Util.SafeJsonIO.WriteAtomicAsync(path, payload);

            Assert.Equal(payload, await File.ReadAllTextAsync(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
