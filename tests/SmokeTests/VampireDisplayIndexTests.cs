using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RPBot;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

[Collection("BotConfig")]
public class VampireDisplayIndexTests : IDisposable
{
    private static int _initOnce;
    private static readonly object _envLock = new();
    private readonly string _tempDir;
    private readonly string? _prevEnv;

    public VampireDisplayIndexTests()
    {
        lock (_envLock)
        {
            _prevEnv = Environment.GetEnvironmentVariable("RPBOT_DATA_DIR");
            _tempDir = Path.Combine(Path.GetTempPath(), $"vtm_idx_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);

            Environment.SetEnvironmentVariable("RPBOT_DATA_DIR", _tempDir);
            ResetDataRootCache();
            Interlocked.Increment(ref _initOnce);
        }
    }

    public void Dispose()
    {
        lock (_envLock)
        {
            Environment.SetEnvironmentVariable("RPBOT_DATA_DIR", _prevEnv);
            ResetDataRootCache();
            Interlocked.Decrement(ref _initOnce);
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private static void ResetDataRootCache()
    {
        var t = typeof(BotConfig);
        foreach (var name in new[] { "_dataRootOverride", "_dataRootDefault" })
        {
            var f = t.GetField(name,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            f?.SetValue(null, null);
        }
    }

    private VampireDisplayIndex NewIndex(ulong guildId = 123)
        => new VampireDisplayIndex(guildId);

    [Fact]
    public async Task LoadAsync_NoFile_DoesNotThrow()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        Assert.Empty(idx.GetMessages("nobody"));
    }

    [Fact]
    public async Task RegisterAsync_AddsMessage_AndPersists()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        await idx.RegisterAsync("vic", 100, 200, SheetMessageKind.DmSheetWithButtons);

        // В памяти доступно сразу.
        var msgs = idx.GetMessages("vic");
        Assert.Single(msgs);
        Assert.Equal(200UL, msgs[0].MessageId);
        Assert.Equal(SheetMessageKind.DmSheetWithButtons, msgs[0].Kind);

        // После сохранения/перезагрузки — то же самое.
        await idx.SaveAsync();
        var idx2 = NewIndex();
        await idx2.LoadAsync();
        var reloaded = idx2.GetMessages("vic");
        Assert.Single(reloaded);
        Assert.Equal(200UL, reloaded[0].MessageId);
    }

    [Fact]
    public async Task RegisterAsync_SameMessage_UpdatesKind()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        await idx.RegisterAsync("vic", 100, 200, SheetMessageKind.PublicSheet);
        await idx.RegisterAsync("vic", 100, 200, SheetMessageKind.DmSheetWithButtons);

        var msgs = idx.GetMessages("vic");
        Assert.Single(msgs);
        Assert.Equal(SheetMessageKind.DmSheetWithButtons, msgs[0].Kind);
    }

    [Fact]
    public async Task UnregisterAsync_RemovesSpecifiedMessage()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        await idx.RegisterAsync("vic", 100, 200, SheetMessageKind.PublicSheet);
        await idx.RegisterAsync("vic", 100, 201, SheetMessageKind.PublicSheet);

        bool removed = await idx.UnregisterAsync(100, 200);
        Assert.True(removed);

        var msgs = idx.GetMessages("vic");
        Assert.Single(msgs);
        Assert.Equal(201UL, msgs[0].MessageId);
    }

    [Fact]
    public async Task UnregisterAsync_LastMessage_RemovesCharacterRecord()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        await idx.RegisterAsync("vic", 100, 200, SheetMessageKind.PublicSheet);

        await idx.UnregisterAsync(100, 200);

        Assert.Empty(idx.GetMessages("vic"));
    }

    [Fact]
    public async Task UnregisterAsync_NonExistent_ReturnsFalse()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        bool removed = await idx.UnregisterAsync(999, 999);
        Assert.False(removed);
    }

    [Fact]
    public async Task ForgetAsync_RemovesEntireCharacter()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        await idx.RegisterAsync("vic", 100, 200, SheetMessageKind.PublicSheet);
        await idx.RegisterAsync("max", 100, 300, SheetMessageKind.DmSheetWithButtons);

        bool forgot = await idx.ForgetAsync("vic");
        Assert.True(forgot);

        Assert.Empty(idx.GetMessages("vic"));
        Assert.Single(idx.GetMessages("max"));
    }

    [Fact]
    public async Task MultipleCharacters_AreIsolated()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        await idx.RegisterAsync("vic", 100, 200, SheetMessageKind.PublicSheet);
        await idx.RegisterAsync("max", 200, 300, SheetMessageKind.DmSheetWithButtons);

        Assert.Single(idx.GetMessages("vic"));
        Assert.Single(idx.GetMessages("max"));
    }

    [Fact]
    public async Task RegisterAsync_DifferentGuild_KeepsSeparateFiles()
    {
        var idxA = NewIndex(11);
        var idxB = NewIndex(22);
        await idxA.LoadAsync();
        await idxB.LoadAsync();

        await idxA.RegisterAsync("vic", 100, 200, SheetMessageKind.PublicSheet);

        Assert.Single(idxA.GetMessages("vic"));
        Assert.Empty(idxB.GetMessages("vic"));
    }

    [Fact]
    public async Task LoadAsync_BrokenJson_FallsBackToEmpty()
    {
        var idx = NewIndex();
        // Запишем мусор в файл до LoadAsync.
        Directory.CreateDirectory(Path.GetDirectoryName(idx.FilePath)!);
        await File.WriteAllTextAsync(idx.FilePath, "{not valid json");

        await idx.LoadAsync();
        Assert.Empty(idx.GetMessages("vic"));
    }
}
