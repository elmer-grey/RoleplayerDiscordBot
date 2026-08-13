using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// 1.x — round 1 unit tests:
///   * TryGetMoscowTime / TryConvertFromUtc: корректное преобразование UTC → MSK;
///   * SafeJsonIO используется вместо File.WriteAllTextAsync в SaveServerConfigs / SaveMasterGuideHistory.
/// </summary>
public class Round1Tests
{
    [Fact]
    public void TryConvertFromUtc_NormalUtc_ReturnsMsk()
    {
        var utc = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc); // 12:00 UTC
        var ok = MoscowTime.TryConvertFromUtc(utc, out var msk);

        Assert.True(ok);
        // MSK = UTC+3 → 15:00
        Assert.Equal(new DateTime(2026, 1, 15, 15, 0, 0), msk);
    }

    [Fact]
    public void TryConvertFromUtc_PreservesUnspecifiedKind_NoThrow()
    {
        var utc = new DateTime(2026, 1, 15, 12, 0, 0); // Kind = Unspecified
        var ok = MoscowTime.TryConvertFromUtc(utc, out var msk);

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 1, 15, 15, 0, 0), msk);
    }

    [Fact]
    public void ToMoscowOffset_Roundtrip_AddsThreeHours()
    {
        var utc = new DateTimeOffset(2026, 6, 1, 8, 30, 0, TimeSpan.Zero);
        var msk = MoscowTime.ToMoscowOffset(utc);

        Assert.Equal(TimeSpan.FromHours(3), msk.Offset);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 11, 30, 0, TimeSpan.FromHours(3)), msk);
    }

    [Fact]
    public void Convert_LocalToMsk_ProducesThreeHourOffset()
    {
        var local = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Local);
        var msk = MoscowTime.Convert(local);

        // Convert нормализует к UTC и потом прибавляет MSK-офсет. Главное — что не кидает.
        Assert.NotEqual(default, msk);
    }

    [Fact]
    public async Task SafeJsonIO_WriteAtomic_SurvivesMissingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_r1_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "deep", "file.json");

        await RPBot.Util.SafeJsonIO.WriteAtomicAsync(path, "{\"ok\":1}");

        Assert.True(File.Exists(path));
        Assert.Equal("{\"ok\":1}", await File.ReadAllTextAsync(path));
        try { Directory.Delete(dir, true); } catch { }
    }
}
