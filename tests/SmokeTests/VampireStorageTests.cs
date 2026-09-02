using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RPBot;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

// Все тесты этого класса используют RPBOT_DATA_DIR — выделенный в коллекцию,
// чтобы они выполнялись последовательно и не конкурировали с другими классами
// за глобальное состояние BotConfig.
[CollectionDefinition("BotConfig", DisableParallelization = true)]
public class BotConfigCollection { }

[Collection("BotConfig")]
public class VampireStorageTests : IDisposable
{
    private static int _initOnce;
    private static readonly object _envLock = new();
    private readonly string _tempDir;
    private readonly string _dataRoot;
    private readonly string? _prevEnv;

    public VampireStorageTests()
    {
                lock (_envLock)
                {
                    _prevEnv = Environment.GetEnvironmentVariable("RPBOT_DATA_DIR");
                    _tempDir = Path.Combine(Path.GetTempPath(), $"vtm_test_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(_tempDir);
                    _dataRoot = _tempDir;

                    // BotConfig.GetDataDirectory читает RPBOT_DATA_DIR через env и кэширует.
                    // Подменяем переменную и сбрасываем кэш через рефлексию, чтобы тест
                    // получил свежий корень данных.
                    Environment.SetEnvironmentVariable("RPBOT_DATA_DIR", _dataRoot);
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

    /// <summary>Сбросить статический кэш <see cref="BotConfig"/>.</summary>
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

    private VampireStorage NewStorage(ulong guildId = 123)
        => new VampireStorage(guildId);

    [Fact]
    public async Task LoadAsync_NoFile_ReturnsEmpty()
    {
        var s = NewStorage();
        await s.LoadAsync();
        Assert.Empty(s.ListAll());
    }

    [Fact]
    public async Task UpsertAsync_NewCharacter_ReturnsTrue()
    {
        var s = NewStorage();
        await s.LoadAsync();
        var isNew = await s.UpsertAsync(new VampireCharacter
        {
            PlayerName = "alice",
            CharacterName = "Vamp1",
            Hunger = 2
        });
        Assert.True(isNew);
        Assert.Single(s.ListAll());
        Assert.Equal("Vamp1", s.GetCharacter("alice")!.CharacterName);
    }

        [Fact]
        public async Task UpsertAsync_WithPlayerId_BuildsSecondaryIndex()
        {
            var s = NewStorage();
            await s.LoadAsync();
            await s.UpsertAsync(new VampireCharacter
            {
                PlayerName = "alice",
                PlayerId = 12345UL,
                CharacterName = "Vamp1"
            });

            var byId = s.GetByPlayerId(12345UL);
            Assert.NotNull(byId);
            Assert.Equal("alice", byId!.PlayerName);
            Assert.Equal("Vamp1", byId.CharacterName);
        }

        [Fact]
        public async Task GetByPlayerId_Zero_ReturnsNull()
        {
            var s = NewStorage();
            await s.LoadAsync();
            Assert.Null(s.GetByPlayerId(0));
        }

        [Fact]
        public async Task GetByPlayerId_Unknown_ReturnsNull()
        {
            var s = NewStorage();
            await s.LoadAsync();
            await s.UpsertAsync(new VampireCharacter { PlayerName = "alice", PlayerId = 11, CharacterName = "A" });
            Assert.Null(s.GetByPlayerId(99));
        }

        [Fact]
        public async Task RemoveAsync_ClearsSecondaryIndex()
        {
            var s = NewStorage();
            await s.LoadAsync();
            await s.UpsertAsync(new VampireCharacter { PlayerName = "alice", PlayerId = 11, CharacterName = "A" });
            Assert.NotNull(s.GetByPlayerId(11));

            await s.RemoveAsync("alice");
            Assert.Null(s.GetByPlayerId(11));
        }

        [Fact]
        public async Task LoadAsync_RestoresPlayerIdIndex()
        {
            var s1 = NewStorage(guildId: 444);
            await s1.LoadAsync();
            await s1.UpsertAsync(new VampireCharacter
            {
                PlayerName = "zoe",
                PlayerId = 999UL,
                CharacterName = "Зоя"
            });
            await s1.SaveAsync();

            var s2 = NewStorage(guildId: 444);
            await s2.LoadAsync();
            Assert.NotNull(s2.GetByPlayerId(999UL));
            Assert.Equal("Зоя", s2.GetByPlayerId(999UL)!.CharacterName);
        }

    [Fact]
    public async Task UpsertAsync_Existing_ReturnsFalse()
    {
        var s = NewStorage();
        await s.LoadAsync();
        await s.UpsertAsync(new VampireCharacter { PlayerName = "alice", CharacterName = "Old" });
        var isNew = await s.UpsertAsync(new VampireCharacter { PlayerName = "alice", CharacterName = "New" });
        Assert.False(isNew);
        Assert.Equal("New", s.GetCharacter("alice")!.CharacterName);
    }

    [Fact]
    public async Task RemoveAsync_Existing_ReturnsTrue()
    {
        var s = NewStorage();
        await s.LoadAsync();
        await s.UpsertAsync(new VampireCharacter { PlayerName = "alice", CharacterName = "V" });
        Assert.True(await s.RemoveAsync("alice"));
        Assert.Null(s.GetCharacter("alice"));
    }

    [Fact]
    public async Task RemoveAsync_Missing_ReturnsFalse()
    {
        var s = NewStorage();
        await s.LoadAsync();
        Assert.False(await s.RemoveAsync("nobody"));
    }

    [Fact]
    public async Task SaveAsync_WritesValidJson()
    {
        var s = NewStorage(guildId: 777);
        await s.LoadAsync();
        await s.UpsertAsync(new VampireCharacter
        {
            PlayerName = "bob",
            CharacterName = "Боб",
            Hunger = 3,
            Attributes = new() { ["Сила"] = 2 }
        });
        await s.SaveAsync();

        var path = Path.Combine(BotConfig.GetDataDirectory(), "vtm", "characters_777.json");
                Assert.True(File.Exists(path));
                var text = await File.ReadAllTextAsync(path);
                Assert.Contains("\"bob\"", text);
                Assert.Contains("\"Сила\"", text);
                Assert.Contains("\"hunger\": 3", text);
    }

    [Fact]
    public async Task LoadAsync_RestoresAfterReopen()
    {
        var s1 = NewStorage(guildId: 555);
        await s1.LoadAsync();
        await s1.UpsertAsync(new VampireCharacter
        {
            PlayerName = "carol",
            CharacterName = "Каролина",
            Hunger = 4
        });
        await s1.SaveAsync();

        var s2 = NewStorage(guildId: 555);
        await s2.LoadAsync();
        var ch = s2.GetCharacter("carol");
        Assert.NotNull(ch);
        Assert.Equal("Каролина", ch!.CharacterName);
        Assert.Equal(4, ch.Hunger);
    }

    [Fact]
    public async Task SetAttributeAsync_UpdatesAndPersists()
    {
        var s = NewStorage();
        await s.LoadAsync();
        await s.UpsertAsync(new VampireCharacter { PlayerName = "dave", CharacterName = "Д" });

        Assert.True(await s.SetAttributeAsync("dave", "Сила", 4));
        Assert.Equal(4, s.GetCharacter("dave")!.Attributes["Сила"]);

        var s2 = NewStorage();
        await s2.LoadAsync();
        Assert.Equal(4, s2.GetCharacter("dave")!.Attributes["Сила"]);
    }

    [Fact]
    public async Task SetAttributeAsync_MissingPlayer_ReturnsFalse()
    {
        var s = NewStorage();
        await s.LoadAsync();
        Assert.False(await s.SetAttributeAsync("ghost", "Сила", 2));
    }

    [Fact]
    public async Task SetAttributeAsync_OutOfRange_Throws()
    {
        var s = NewStorage();
        await s.LoadAsync();
        await s.UpsertAsync(new VampireCharacter { PlayerName = "eve", CharacterName = "Е" });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => s.SetAttributeAsync("eve", "Сила", 6));
    }

    [Fact]
    public async Task SetHungerAsync_Updates()
    {
        var s = NewStorage();
        await s.LoadAsync();
        await s.UpsertAsync(new VampireCharacter { PlayerName = "fred", CharacterName = "Ф", Hunger = 1 });

        Assert.True(await s.SetHungerAsync("fred", 5));
        Assert.Equal(5, s.GetCharacter("fred")!.Hunger);
    }

    [Fact]
    public async Task SetHungerAsync_Zero_Throws()
    {
        var s = NewStorage();
        await s.LoadAsync();
        await s.UpsertAsync(new VampireCharacter { PlayerName = "fred", CharacterName = "Ф" });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => s.SetHungerAsync("fred", 0));
    }

    [Fact]
    public async Task Concurrent_LoadSave_NoCorruption()
    {
        var s = NewStorage(guildId: 999);
        await s.LoadAsync();
        await s.UpsertAsync(new VampireCharacter { PlayerName = "g", CharacterName = "G" });

        // 50 параллельных SetAttributeAsync — SemaphoreSlim должен сериализовать.
        var tasks = new Task[50];
        for (int i = 0; i < 50; i++)
        {
            int v = i % 6; // 0..5
            tasks[i] = s.SetAttributeAsync("g", $"Attr{i}", v);
        }
        await Task.WhenAll(tasks);

        var ch = s.GetCharacter("g")!;
        Assert.Equal(50, ch.Attributes.Count);
        for (int i = 0; i < 50; i++)
            Assert.Equal(i % 6, ch.Attributes[$"Attr{i}"]);
    }
}