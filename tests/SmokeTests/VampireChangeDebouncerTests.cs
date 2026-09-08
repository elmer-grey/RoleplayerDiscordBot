using System;
using System.Threading;
using System.Threading.Tasks;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Покрывает trailing-debounce обёртку <see cref="VampireChangeDebouncer"/>.
/// Использует короткий delay (100 мс), чтобы тесты выполнялись быстро.
/// </summary>
[Collection("BotConfig")]
public class VampireChangeDebouncerTests : IsolatedDataTestBase
{
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(100);

    public VampireChangeDebouncerTests() : base("vtm_debouncer")
    {
    }

    private static VampireStorage NewStorage(ulong guild = 42) => new VampireStorage(guild);

    /// <summary>Собрать (debouncer, syncWithCounter) с заданной задержкой.</summary>
    private (VampireChangeDebouncer Debouncer, FakeCountingSync Sync) BuildSync(TimeSpan? delay = null)
    {
        var storage = NewStorage();
        var display = new VampireDisplayIndex(guildId: 42);
        var fake = new FakeDiscordMessageAccessor();
        var counting = new FakeCountingSync(storage, display, fake);
        return (new VampireChangeDebouncer(counting, delay ?? ShortDelay), counting);
    }

    [Fact]
    public async Task SingleRequest_TriggersOneSync()
    {
        var (deb, sync) = BuildSync();
        try
        {
            deb.Request(Guid.NewGuid(), userId: 1);
            await Task.Delay(ShortDelay + TimeSpan.FromMilliseconds(100));
            Assert.Equal(1, sync.CallCount);
        }
        finally { deb.Dispose(); }
    }

    [Fact]
    public async Task RapidRequests_CoalesceToOneSync()
    {
        var (deb, sync) = BuildSync();
        try
        {
            var charId = Guid.NewGuid();
            // 5 кликов подряд в окне 50 мс (< 100 мс debounce).
            for (int i = 0; i < 5; i++)
            {
                deb.Request(charId, userId: 1);
                await Task.Delay(10);
            }
            // Ждём ещё немного после последнего клика.
            await Task.Delay(ShortDelay + TimeSpan.FromMilliseconds(100));
            Assert.Equal(1, sync.CallCount);
        }
        finally { deb.Dispose(); }
    }

    [Fact]
    public async Task SeparateBursts_ProduceSeparateSyncs()
    {
        var (deb, sync) = BuildSync();
        try
        {
            var charId = Guid.NewGuid();
            deb.Request(charId, userId: 1);
            await Task.Delay(ShortDelay + TimeSpan.FromMilliseconds(100));
            Assert.Equal(1, sync.CallCount);

            // Второй батч — после первого sync — должен дать второй sync.
            deb.Request(charId, userId: 1);
            await Task.Delay(ShortDelay + TimeSpan.FromMilliseconds(100));
            Assert.Equal(2, sync.CallCount);
        }
        finally { deb.Dispose(); }
    }

    [Fact]
    public async Task DifferentCharacters_DebounceIndependently()
    {
        var (deb, sync) = BuildSync();
        try
        {
            var c1 = Guid.NewGuid();
            var c2 = Guid.NewGuid();
            deb.Request(c1, userId: 1);
            deb.Request(c2, userId: 1);
            await Task.Delay(ShortDelay + TimeSpan.FromMilliseconds(100));
            // Каждый персонаж — один sync.
            Assert.Equal(2, sync.CallCount);
            Assert.Contains(sync.Calls, x => x.characterId == c1);
            Assert.Contains(sync.Calls, x => x.characterId == c2);
        }
        finally { deb.Dispose(); }
    }

    [Fact]
    public async Task Request_AfterDispose_DoesNothing()
    {
        var (deb, sync) = BuildSync();
        deb.Dispose();
        deb.Request(Guid.NewGuid(), userId: 1);
        await Task.Delay(ShortDelay + TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, sync.CallCount);
    }

    [Fact]
    public async Task FlushAsync_RunsImmediately()
    {
        var (deb, sync) = BuildSync();
        try
        {
            deb.Request(Guid.NewGuid(), userId: 1);
            await deb.FlushAsync();
            Assert.Equal(1, sync.CallCount);
        }
        finally { deb.Dispose(); }
    }

    [Fact]
    public async Task FlushAsync_AfterDispose_DoesNothing()
    {
        var (deb, _) = BuildSync();
        deb.Dispose();
        await deb.FlushAsync(); // не должен падать
    }

    [Fact]
    public void Constructor_RejectsNullSync()
    {
        Assert.Throws<ArgumentNullException>(() => new VampireChangeDebouncer(null!));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveDelay()
    {
        var storage = NewStorage();
        var display = new VampireDisplayIndex(guildId: 42);
        var fake = new FakeDiscordMessageAccessor();
        var sync = new FakeCountingSync(storage, display, fake);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VampireChangeDebouncer(sync, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VampireChangeDebouncer(sync, TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void DebounceDelay_ReflectsConstructorArg()
    {
        var (deb, _) = BuildSync(TimeSpan.FromMilliseconds(250));
        try
        {
            Assert.Equal(TimeSpan.FromMilliseconds(250), deb.DebounceDelay);
            Assert.Equal(TimeSpan.FromMilliseconds(1500), VampireChangeDebouncer.DefaultDelay);
        }
        finally { deb.Dispose(); }
    }

    // ─── helper: счётчик реальных вызовов SyncAllAsync ─────────────────────

    private sealed class FakeCountingSync : VampireSyncService
    {
        public int CallCount;
        public System.Collections.Generic.List<(Guid characterId, ulong userId)> Calls { get; } = new();

        public FakeCountingSync(VampireStorage storage, VampireDisplayIndex display, IDiscordMessageAccessor discord)
            : base(storage, display, discord) { }

        public override Task<SyncResult> SyncAllAsync(Guid characterId, ulong userId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            lock (Calls) Calls.Add((characterId, userId));
            // Не зовём базу — для подсчёта реальный обход не нужен.
            return Task.FromResult(new SyncResult(0, 0, 0));
        }
    }
}
