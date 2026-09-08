using System;
using System.Threading.Tasks;
using RPBot;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

[Collection("BotConfig")]
public class VampireDmBlockIndexTests : IsolatedDataTestBase
{
    public VampireDmBlockIndexTests() : base("vtm_dmblk") { }

    private VampireDmBlockIndex NewIndex(ulong userId = 555)
        => new VampireDmBlockIndex(userId);

    [Fact]
    public void Constructor_ZeroUserId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new VampireDmBlockIndex(0));
    }

    [Fact]
    public async Task LoadAsync_NoFile_DoesNotThrow()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        Assert.Null(idx.Get("vic", DmBlockKind.Willpower));
    }

    [Fact]
    public async Task Set_AndGet_ReturnsSame()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        await idx.SetAsync("vic", DmBlockKind.Willpower, 100, 200);

        var refr = idx.Get("vic", DmBlockKind.Willpower);
        Assert.NotNull(refr);
        Assert.Equal(100UL, refr!.ChannelId);
        Assert.Equal(200UL, refr.MessageId);
    }

    [Fact]
    public async Task MultipleKinds_AreIsolated()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        await idx.SetAsync("vic", DmBlockKind.Willpower, 100, 200);
        await idx.SetAsync("vic", DmBlockKind.Health, 100, 300);
        await idx.SetAsync("vic", DmBlockKind.Description, 100, 400);

        var all = idx.GetAll("vic");
        Assert.Equal(3, all.Count);
        Assert.Equal(200UL, all[DmBlockKind.Willpower].MessageId);
        Assert.Equal(300UL, all[DmBlockKind.Health].MessageId);
        Assert.Equal(400UL, all[DmBlockKind.Description].MessageId);
    }

    [Fact]
    public async Task Clear_RemovesOneKind_KeepsOthers()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        await idx.SetAsync("vic", DmBlockKind.Willpower, 100, 200);
        await idx.SetAsync("vic", DmBlockKind.Health, 100, 300);

        bool removed = await idx.ClearAsync("vic", DmBlockKind.Willpower);
        Assert.True(removed);

        Assert.Null(idx.Get("vic", DmBlockKind.Willpower));
        Assert.NotNull(idx.Get("vic", DmBlockKind.Health));
    }

    [Fact]
    public async Task Clear_NonExistent_ReturnsFalse()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        bool removed = await idx.ClearAsync("vic", DmBlockKind.Willpower);
        Assert.False(removed);
    }

    [Fact]
    public async Task Clear_LastBlock_RemovesCharacterRecord()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        await idx.SetAsync("vic", DmBlockKind.Willpower, 100, 200);

        await idx.ClearAsync("vic", DmBlockKind.Willpower);

        Assert.Empty(idx.GetAll("vic"));
    }

    [Fact]
    public async Task Forget_RemovesAllKindsForCharacter()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        await idx.SetAsync("vic", DmBlockKind.Willpower, 100, 200);
        await idx.SetAsync("max", DmBlockKind.Health, 100, 300);

        bool forgot = await idx.ForgetAsync("vic");
        Assert.True(forgot);

        Assert.Empty(idx.GetAll("vic"));
        Assert.Single(idx.GetAll("max"));
    }

    [Fact]
    public async Task Reload_AfterSet_PreservesState()
    {
        var idx = NewIndex(99);
        await idx.LoadAsync();
        await idx.SetAsync("vic", DmBlockKind.Willpower, 100, 200);
        await idx.SetAsync("vic", DmBlockKind.Health, 100, 300);
        await idx.SaveAsync();

        var idx2 = NewIndex(99);
        await idx2.LoadAsync();
        Assert.Equal(200UL, idx2.Get("vic", DmBlockKind.Willpower)!.MessageId);
        Assert.Equal(300UL, idx2.Get("vic", DmBlockKind.Health)!.MessageId);
    }

    [Fact]
    public async Task DifferentUsers_KeepSeparateFiles()
    {
        var idxA = NewIndex(11);
        var idxB = NewIndex(22);
        await idxA.LoadAsync();
        await idxB.LoadAsync();
        await idxA.SetAsync("vic", DmBlockKind.Willpower, 100, 200);

        Assert.NotNull(idxA.Get("vic", DmBlockKind.Willpower));
        Assert.Null(idxB.Get("vic", DmBlockKind.Willpower));
    }

    [Fact]
    public async Task Set_OverwritesPreviousMessageId()
    {
        var idx = NewIndex();
        await idx.LoadAsync();
        await idx.SetAsync("vic", DmBlockKind.Willpower, 100, 200);
        await idx.SetAsync("vic", DmBlockKind.Willpower, 100, 500);

        var refr = idx.Get("vic", DmBlockKind.Willpower);
        Assert.Equal(500UL, refr!.MessageId);
    }

    [Fact]
    public async Task LoadAsync_BrokenJson_FallsBackToEmpty()
    {
        var idx = NewIndex();
        Directory.CreateDirectory(Path.GetDirectoryName(idx.FilePath)!);
        await File.WriteAllTextAsync(idx.FilePath, "{not valid json");

        await idx.LoadAsync();
        Assert.Null(idx.Get("vic", DmBlockKind.Willpower));
    }

    [Fact]
    public async Task NewBlockKinds_RoundTrip()
    {
        // Закрытие todo vtm-sheet-mirror-registry: Morality, Frenzy, Clan должны
        // проходить через индекс наравне с базовыми видами.
        var idx = NewIndex();
        await idx.LoadAsync();

        await idx.SetAsync("vic", DmBlockKind.Morality, 1, 11);
        await idx.SetAsync("vic", DmBlockKind.Frenzy, 2, 22);
        await idx.SetAsync("vic", DmBlockKind.Clan, 3, 33);

        var all = idx.GetAll("vic");
        Assert.Equal(11UL, all[DmBlockKind.Morality].MessageId);
        Assert.Equal(22UL, all[DmBlockKind.Frenzy].MessageId);
        Assert.Equal(33UL, all[DmBlockKind.Clan].MessageId);

        var idx2 = NewIndex();
        await idx2.LoadAsync();
        Assert.Equal(11UL, idx2.Get("vic", DmBlockKind.Morality)!.MessageId);
        Assert.Equal(22UL, idx2.Get("vic", DmBlockKind.Frenzy)!.MessageId);
        Assert.Equal(33UL, idx2.Get("vic", DmBlockKind.Clan)!.MessageId);
    }

    [Fact]
    public void AllBlockKinds_AreDistinct()
    {
        // Защита от регрессии при добавлении новых видов.
        var values = (DmBlockKind[])System.Enum.GetValues(typeof(DmBlockKind));
        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.Contains(DmBlockKind.Description, values);
        Assert.Contains(DmBlockKind.Willpower, values);
        Assert.Contains(DmBlockKind.Health, values);
        Assert.Contains(DmBlockKind.Morality, values);
        Assert.Contains(DmBlockKind.Frenzy, values);
        Assert.Contains(DmBlockKind.Clan, values);
    }
}
