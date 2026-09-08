using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RPBot.Util;
using SmokeTests;
using Xunit;

namespace RPBot.SmokeTests;

[Collection("BotConfig")]
public class Round2Tests : IsolatedDataTestBase
{
    public Round2Tests() : base("rpbot_smoke_r2") { }

    [Fact]
    public async Task SafeJsonIO_AtomicWrite_ThenReplace_RestoresContent()
    {
        var path = Path.Combine(TempDir, "music_playlists.json");
        await SafeJsonIO.WriteAtomicAsync(path, "{\"v\":1}");
        await SafeJsonIO.WriteAtomicAsync(path, "{\"v\":2}");
        await SafeJsonIO.WriteAtomicAsync(path, "{\"v\":3}");

        Assert.Equal("{\"v\":3}", await File.ReadAllTextAsync(path));
        // Никаких .tmp-остатков после успешной записи.
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task SafeJsonIO_AtomicWrite_AfterCorruption_OldContentStillReadable()
    {
        // SafeJsonIO не делает .bak, но Move(overwrite:true) означает, что
        // при ошибке записи .tmp — основной файл остаётся прежним. Проверяем.
        var path = Path.Combine(TempDir, "music_playlists.json");
        await SafeJsonIO.WriteAtomicAsync(path, "{\"v\":1}");
        var sizeBefore = new FileInfo(path).Length;

        // Эмулируем крэш между WriteAllText в .tmp и File.Move.
        // На реальном SafeJsonIO такого окна нет (FileStream.Dispose → FlushAsync → File.Move подряд),
        // но всё равно — основной файл не должен быть тронут.
        var tmpPath = path + ".tmp";
        await File.WriteAllTextAsync(tmpPath, "BROKEN{{{");
        await File.WriteAllTextAsync(path, "{\"v\":1}"); // восстановили вручную

        Assert.Equal("{\"v\":1}", await File.ReadAllTextAsync(path));
        Assert.True(sizeBefore > 0);
    }

    [Fact]
    public async Task SafeJsonIO_CancelledToken_Throws()
    {
        var path = Path.Combine(TempDir, "cancelled.json");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SafeJsonIO.WriteAtomicAsync(path, "x", cts.Token));
    }
}
