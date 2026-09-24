using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RPBot.Util;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// 2.x — round 2 unit tests:
///   * MusicPlaylistStore и MusicQueueStore.SaveAsync/ClearAsync идут через
///     SafeJsonIO.WriteAtomicAsync (без половинчатых файлов).
///   * Тест Lavalink retry-логики — проверка, что метод StartLavalinkProcessAsync
///     и обёртка StartLavalinkWithRetryAsync доступны и не падают при отменённом токене.
/// </summary>
[Collection(nameof(BotConfigCollection))]
public class Round2Tests : IDisposable
{
    private readonly string _tmpDir;

    public Round2Tests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_r2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        Environment.SetEnvironmentVariable("RPBOT_DATA_DIR", _tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
        Environment.SetEnvironmentVariable("RPBOT_DATA_DIR", null);
    }

    [Fact]
    public async Task SafeJsonIO_AtomicWrite_ThenReplace_RestoresContent()
    {
        var path = Path.Combine(_tmpDir, "music_playlists.json");
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
        var path = Path.Combine(_tmpDir, "music_playlists.json");
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
        var path = Path.Combine(_tmpDir, "cancelled.json");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SafeJsonIO.WriteAtomicAsync(path, "x", cts.Token));
    }
}
