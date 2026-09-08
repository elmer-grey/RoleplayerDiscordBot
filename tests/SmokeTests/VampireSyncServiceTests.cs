using System;
using System.IO;
using System.Threading.Tasks;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

[Collection("BotConfig")]
public class VampireSyncServiceTests : IsolatedDataTestBase
{
    public VampireSyncServiceTests() : base("vtm_sync")
    {
    }

    private static VampireStorage NewStorage(ulong guild = 42) => new VampireStorage(guild);

    private static VampireCharacter NewCharacter(string name = "Виктор", ulong player = 1)
    {
        var c = new VampireCharacter
        {
            PlayerName = "Андрей",
            CharacterName = name,
            PlayerId = player,
            Clan = "Toreador",
            Hunger = 2,
            Willpower = 5,
            WillpowerPoints = 4,
            Humanity = 7,
        };
        c.CharacterId = Guid.NewGuid();
        return c;
    }

    [Fact]
    public async Task SyncCharacter_UnknownId_AllZeros()
    {
        var storage = NewStorage();
        await storage.LoadAsync();
        var display = new VampireDisplayIndex(guildId: 42);
        await display.LoadAsync();
        var fake = new FakeDiscordMessageAccessor();
        var svc = new VampireSyncService(storage, display, fake);

        var res = await svc.SyncCharacterAsync(Guid.NewGuid());
        Assert.Equal(0, res.Updated);
        Assert.Equal(0, res.Missing);
        Assert.Equal(0, res.Errors);
    }

    [Fact]
    public async Task SyncCharacter_NoRegisteredMessages_NoCalls()
    {
        var storage = NewStorage();
        await storage.LoadAsync();
        var display = new VampireDisplayIndex(guildId: 42);
        await display.LoadAsync();
        var fake = new FakeDiscordMessageAccessor();
        var svc = new VampireSyncService(storage, display, fake);

        var c = NewCharacter();
        await storage.UpsertAsync(c);

        var res = await svc.SyncCharacterAsync(c.CharacterId);
        Assert.Equal(0, res.Updated);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task SyncCharacter_DmWithButtons_UpdatesWithComponents()
    {
        var storage = NewStorage();
        await storage.LoadAsync();
        var display = new VampireDisplayIndex(guildId: 42);
        await display.LoadAsync();
        var fake = new FakeDiscordMessageAccessor { Known = { (1, 100) } };
        var svc = new VampireSyncService(storage, display, fake);

        var c = NewCharacter();
        await storage.UpsertAsync(c);
        await display.RegisterAsync(c.CharacterId.ToString("N"), 1, 100, SheetMessageKind.DmSheetWithButtons);

        var res = await svc.SyncCharacterAsync(c.CharacterId);

        Assert.Equal(1, res.Updated);
        Assert.Equal(0, res.Missing);
        var upd = Assert.Single(fake.Calls.FindAll(c => c.Method == "UpdateEmbedAndComponentsAsync"));
        Assert.NotNull(upd.Components);
    }

    [Fact]
    public async Task SyncCharacter_PublicSheet_UpdatesEmbedOnly()
    {
        var storage = NewStorage();
        await storage.LoadAsync();
        var display = new VampireDisplayIndex(guildId: 42);
        await display.LoadAsync();
        var fake = new FakeDiscordMessageAccessor { Known = { (1, 200) } };
        var svc = new VampireSyncService(storage, display, fake);

        var c = NewCharacter();
        await storage.UpsertAsync(c);
        await display.RegisterAsync(c.CharacterId.ToString("N"), 1, 200, SheetMessageKind.PublicSheet);

        var res = await svc.SyncCharacterAsync(c.CharacterId);

        Assert.Equal(1, res.Updated);
        var upd = Assert.Single(fake.Calls.FindAll(c => c.Method == "UpdateEmbedAsync"));
        Assert.Null(upd.Components);
    }

    [Fact]
    public async Task SyncCharacter_MessageMissing_Unregisters()
    {
        var storage = NewStorage();
        await storage.LoadAsync();
        var display = new VampireDisplayIndex(guildId: 42);
        await display.LoadAsync();
        // Known пуст — TryGet вернёт null → сразу Unregister, без Update*.
        var fake = new FakeDiscordMessageAccessor();
        var svc = new VampireSyncService(storage, display, fake);

        var c = NewCharacter();
        await storage.UpsertAsync(c);
        await display.RegisterAsync(c.CharacterId.ToString("N"), 1, 300, SheetMessageKind.PublicSheet);

        var res = await svc.SyncCharacterAsync(c.CharacterId);

        Assert.Equal(1, res.Missing);
        Assert.Empty(display.GetMessages(c.CharacterId.ToString("N")));
    }

    [Fact]
    public async Task SyncDmBlocks_UnknownUser_NoCalls()
    {
        var storage = NewStorage();
        await storage.LoadAsync();
        var display = new VampireDisplayIndex(guildId: 42);
        await display.LoadAsync();
        var fake = new FakeDiscordMessageAccessor();
        var svc = new VampireSyncService(storage, display, fake);

        var c = NewCharacter();
        await storage.UpsertAsync(c);

        var res = await svc.SyncDmBlocksForUserAsync(99UL, c.CharacterId);
        Assert.Equal(0, res.Updated);
    }

    [Fact]
    public async Task SyncDmBlocks_WillpowerBlock_UpdatesEmbedAndComponents()
    {
        var storage = NewStorage();
        await storage.LoadAsync();
        var display = new VampireDisplayIndex(guildId: 42);
        await display.LoadAsync();
        var fake = new FakeDiscordMessageAccessor { Known = { (0, 500) } };
        var svc = new VampireSyncService(storage, display, fake);

        var c = NewCharacter();
        await storage.UpsertAsync(c);
        var blocks = new VampireDmBlockIndex(userId: 99);
        await blocks.LoadAsync();
        await blocks.SetAsync(c.CharacterId.ToString("N"), DmBlockKind.Willpower, channelId: 0, messageId: 500);

        var res = await svc.SyncDmBlocksForUserAsync(99UL, c.CharacterId);

        Assert.Equal(1, res.Updated);
        Assert.Single(fake.Calls.FindAll(c => c.Method == "UpdateEmbedAndComponentsAsync"));
    }

    [Fact]
    public async Task SyncDmBlocks_HealthBlock_Updates()
    {
        var storage = NewStorage();
        await storage.LoadAsync();
        var display = new VampireDisplayIndex(guildId: 42);
        await display.LoadAsync();
        var fake = new FakeDiscordMessageAccessor { Known = { (0, 600) } };
        var svc = new VampireSyncService(storage, display, fake);

        var c = NewCharacter();
        c.Health = new HealthState(7);
        await storage.UpsertAsync(c);
        var blocks = new VampireDmBlockIndex(userId: 99);
        await blocks.LoadAsync();
        await blocks.SetAsync(c.CharacterId.ToString("N"), DmBlockKind.Health, channelId: 0, messageId: 600);

        var res = await svc.SyncDmBlocksForUserAsync(99UL, c.CharacterId);

        Assert.Equal(1, res.Updated);
    }

    [Fact]
    public async Task SyncDmBlocks_MessageMissing_Unregisters()
    {
        var storage = NewStorage();
        await storage.LoadAsync();
        var display = new VampireDisplayIndex(guildId: 42);
        await display.LoadAsync();
        var fake = new FakeDiscordMessageAccessor(); // Known пуст
        var svc = new VampireSyncService(storage, display, fake);

        var c = NewCharacter();
        await storage.UpsertAsync(c);
        var blocks = new VampireDmBlockIndex(userId: 99);
        await blocks.LoadAsync();
        await blocks.SetAsync(c.CharacterId.ToString("N"), DmBlockKind.Health, channelId: 0, messageId: 700);

        var res = await svc.SyncDmBlocksForUserAsync(99UL, c.CharacterId);

        Assert.Equal(1, res.Missing);
                // Перечитываем из файла — SyncService работает с собственным экземпляром индекса.
                await blocks.LoadAsync();
                Assert.Empty(blocks.GetAll(c.CharacterId.ToString("N")));
    }
}
