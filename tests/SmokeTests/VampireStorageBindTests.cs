using System;
using System.Threading.Tasks;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты на <see cref="VampireStorage.BindAsync"/> и <see cref="VampireStorage.UnbindAsync"/>.
///
/// <para>Сторадж ключует по <c>PlayerName</c>, но привязка идёт по
/// <c>CharacterName</c> (более стабильное имя, от Discord-ника не зависит).</para>
/// </summary>
[Collection("BotConfig")]
public sealed class VampireStorageBindTests : IsolatedDataTestBase
{
    public VampireStorageBindTests() : base("vtm_storage_bind") { }

    private static (VampireStorage storage, string temp) NewStorage()
        {
            // Используем уникальный guildId чтобы изолировать файл characters_{guildId}.json.
            var storage = new VampireStorage(guildId: (ulong)Math.Abs(Guid.NewGuid().GetHashCode()));
            return (storage, Path.GetTempPath());
        }

    private static VampireCharacter MakeCharacter(string playerName, string charName) => new()
    {
        PlayerName = playerName,
        CharacterName = charName,
        CharacterId = Guid.NewGuid(),
        PlayerId = 0,
    };

    [Fact]
    public async Task BindAsync_AssignsPlayerId()
    {
        var (storage, _) = NewStorage();
        await storage.LoadAsync();
        var c = MakeCharacter("Андрей", "Виктор");
        await storage.UpsertAsync(c);

        var res = await storage.BindAsync("Виктор", userId: 123UL);

        Assert.Equal(VampireStorage.BindResultKind.Ok, res.Kind);
        Assert.Equal(123UL, res.Character!.PlayerId);
        Assert.Same(c, res.Character);
    }

    [Fact]
    public async Task BindAsync_CaseInsensitive()
    {
        var (storage, _) = NewStorage();
        await storage.LoadAsync();
        await storage.UpsertAsync(MakeCharacter("Андрей", "Виктор"));

        var res = await storage.BindAsync("виктор", userId: 7UL);

        Assert.Equal(VampireStorage.BindResultKind.Ok, res.Kind);
        Assert.Equal(7UL, res.Character!.PlayerId);
    }

    [Fact]
    public async Task BindAsync_UpdatesIndex()
    {
        var (storage, _) = NewStorage();
        await storage.LoadAsync();
        await storage.UpsertAsync(MakeCharacter("Андрей", "Виктор"));

        await storage.BindAsync("Виктор", userId: 42UL);

        var byId = storage.GetByPlayerId(42UL);
        Assert.NotNull(byId);
        Assert.Equal("Виктор", byId!.CharacterName);
    }

    [Fact]
    public async Task BindAsync_NotFound_ReturnsError()
    {
        var (storage, _) = NewStorage();
        await storage.LoadAsync();

        var res = await storage.BindAsync("Неизвестный", userId: 5UL);

        Assert.Equal(VampireStorage.BindResultKind.NotFound, res.Kind);
        Assert.Null(res.Character);
    }

    [Fact]
    public async Task BindAsync_AlreadySame_IsNoop()
    {
        var (storage, _) = NewStorage();
        await storage.LoadAsync();
        await storage.UpsertAsync(MakeCharacter("Андрей", "Виктор"));

        var first = await storage.BindAsync("Виктор", userId: 1UL);
        var second = await storage.BindAsync("Виктор", userId: 1UL);

        Assert.Equal(VampireStorage.BindResultKind.Ok, first.Kind);
        Assert.Equal(VampireStorage.BindResultKind.AlreadyBoundToSame, second.Kind);
    }

    [Fact]
    public async Task BindAsync_AlreadyBoundToOther_Blocks()
    {
        var (storage, _) = NewStorage();
        await storage.LoadAsync();
        await storage.UpsertAsync(MakeCharacter("Андрей", "Виктор"));

        var first = await storage.BindAsync("Виктор", userId: 10UL);
        var second = await storage.BindAsync("Виктор", userId: 99UL);

        Assert.Equal(VampireStorage.BindResultKind.Ok, first.Kind);
        Assert.Equal(VampireStorage.BindResultKind.AlreadyBoundToOther, second.Kind);
        Assert.Equal(10UL, second.CurrentPlayerId);
    }

    [Fact]
    public async Task BindAsync_ZeroUserId_Rejected()
    {
        var (storage, _) = NewStorage();
        await storage.LoadAsync();

        var res = await storage.BindAsync("Виктор", userId: 0UL);

        Assert.Equal(VampireStorage.BindResultKind.InvalidUserId, res.Kind);
    }

    [Fact]
    public async Task UnbindAsync_ClearsPlayerId()
    {
        var (storage, _) = NewStorage();
        await storage.LoadAsync();
        await storage.UpsertAsync(MakeCharacter("Андрей", "Виктор"));
        await storage.BindAsync("Виктор", userId: 30UL);

        var res = await storage.UnbindAsync("Виктор");

        Assert.Equal(VampireStorage.BindResultKind.Ok, res.Kind);
        Assert.Equal(0UL, res.Character!.PlayerId);
        Assert.Null(storage.GetByPlayerId(30UL));
    }

    [Fact]
    public async Task UnbindAsync_ThenBindOther_Succeeds()
    {
        var (storage, _) = NewStorage();
        await storage.LoadAsync();
        await storage.UpsertAsync(MakeCharacter("Андрей", "Виктор"));
        await storage.BindAsync("Виктор", userId: 50UL);
        await storage.UnbindAsync("Виктор");

        var res = await storage.BindAsync("Виктор", userId: 51UL);

        Assert.Equal(VampireStorage.BindResultKind.Ok, res.Kind);
        Assert.Equal(51UL, res.Character!.PlayerId);
    }

    [Fact]
    public async Task UnbindAsync_NoExisting_IsNoop()
    {
        var (storage, _) = NewStorage();
        await storage.LoadAsync();
        await storage.UpsertAsync(MakeCharacter("Андрей", "Виктор"));

        var res = await storage.UnbindAsync("Виктор");

        Assert.Equal(VampireStorage.BindResultKind.AlreadyBoundToSame, res.Kind);
    }

    [Fact]
    public async Task UnbindAsync_NotFound_ReturnsError()
    {
        var (storage, _) = NewStorage();
        await storage.LoadAsync();

        var res = await storage.UnbindAsync("Неизвестный");

        Assert.Equal(VampireStorage.BindResultKind.NotFound, res.Kind);
    }

    [Fact]
    public async Task PersistAfterBind_ReloadRoundTrips()
    {
        var (storage, _) = NewStorage();
        await storage.LoadAsync();
        await storage.UpsertAsync(MakeCharacter("Андрей", "Виктор"));
        await storage.BindAsync("Виктор", userId: 555UL);

        // Объёмный сценарий: перезагрузить из файла — должно сохраниться.
        await storage.LoadAsync();

        Assert.NotNull(storage.GetByPlayerId(555UL));
        Assert.Equal("Виктор", storage.GetByPlayerId(555UL)!.CharacterName);
    }
}
