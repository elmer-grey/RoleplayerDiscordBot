using System;
using RPBot.VtM;
using Xunit;

namespace RPBot.Tests;

public class VampireShowResolverTests
{
    private static VampireStorage NewStorage()
    {
        var guildId = (ulong)Math.Abs(Guid.NewGuid().GetHashCode());
        return new VampireStorage(guildId);
    }

    private static VampireStorage StorageWithOne(ulong playerId, string name)
    {
        var s = NewStorage();
        s.UpsertAsync(new VampireCharacter
        {
            CharacterId = Guid.NewGuid(),
            CharacterName = name,
            PlayerName = name,
            PlayerId = playerId,
        }).GetAwaiter().GetResult();
        return s;
    }

    private static VampireStorage StorageWithTwo(string name1, ulong p1, string name2, ulong p2)
    {
        var s = NewStorage();
        s.UpsertAsync(new VampireCharacter { CharacterId = Guid.NewGuid(), CharacterName = name1, PlayerName = name1, PlayerId = p1 }).GetAwaiter().GetResult();
        s.UpsertAsync(new VampireCharacter { CharacterId = Guid.NewGuid(), CharacterName = name2, PlayerName = name2, PlayerId = p2 }).GetAwaiter().GetResult();
        return s;
    }

    private static void AddUnbound(VampireStorage s, string name)
    {
        s.UpsertAsync(new VampireCharacter
        {
            CharacterId = Guid.NewGuid(),
            CharacterName = name,
            PlayerName = name,
            PlayerId = 0,
        }).GetAwaiter().GetResult();
    }

    // ─── Правило: имя обязательно ──────────────────────────────────────

    [Fact]
    public void Resolve_NoArgs_AnyMode_Fails_NameRequired()
    {
        var s = NewStorage();
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Dm, null, null);
        Assert.Equal(VampireShowResolver.Failure.NameRequired, d.FailureCode);

        var p = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Public, "", null);
        Assert.Equal(VampireShowResolver.Failure.NameRequired, p.FailureCode);
    }

    [Fact]
    public void Resolve_WhitespaceName_TreatedAsEmpty()
    {
        var s = NewStorage();
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Dm, "   ", null);
        Assert.Equal(VampireShowResolver.Failure.NameRequired, d.FailureCode);
    }

    // ─── DM-режим ───────────────────────────────────────────────────────

    [Fact]
    public void Resolve_Dm_ByUser_Returns_BoundCharacter()
    {
        var s = StorageWithOne(playerId: 42UL, name: "Виктор");
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Dm, null, userId: 42);
        Assert.Equal(VampireShowResolver.Failure.None, d.FailureCode);
        Assert.NotNull(d.Character);
        Assert.Equal("Виктор", d.Character!.CharacterName);
    }

    [Fact]
    public void Resolve_Dm_ByUser_NoBound_Fails_UserHasNoCharacter()
    {
        var s = NewStorage();
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Dm, null, userId: 42, mention: "@ghost");
        Assert.Equal(VampireShowResolver.Failure.UserHasNoCharacter, d.FailureCode);
        Assert.Contains("@ghost", d.Message);
    }

    [Fact]
    public void Resolve_Dm_ByName_Returns_Character()
    {
        var s = StorageWithOne(playerId: 42UL, name: "Виктор");
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Dm, "Виктор", null);
        Assert.Equal(VampireShowResolver.Failure.None, d.FailureCode);
        Assert.Equal("Виктор", d.Character!.CharacterName);
    }

    [Fact]
    public void Resolve_Dm_ByName_NotFound_Fails_CharacterNotFound()
    {
        var s = NewStorage();
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Dm, "Зак", null);
        Assert.Equal(VampireShowResolver.Failure.CharacterNotFound, d.FailureCode);
        Assert.Contains("Зак", d.Message);
    }

    [Fact]
    public void Resolve_Dm_ByName_CaseInsensitive()
    {
        var s = StorageWithOne(playerId: 42UL, name: "Виктор");
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Dm, "виктор", null);
        Assert.Equal(VampireShowResolver.Failure.None, d.FailureCode);
    }

    [Fact]
    public void Resolve_Dm_BothArgs_ConsistentUser_Returns()
    {
        var s = StorageWithOne(playerId: 42UL, name: "Виктор");
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Dm, "Виктор", userId: 42);
        Assert.Equal(VampireShowResolver.Failure.None, d.FailureCode);
    }

    [Fact]
    public void Resolve_Dm_BothArgs_UserMismatch_Fails()
    {
        var s = StorageWithOne(playerId: 42UL, name: "Виктор");
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Dm, "Виктор", userId: 99);
        Assert.Equal(VampireShowResolver.Failure.UserMismatch, d.FailureCode);
    }

    [Fact]
    public void Resolve_Dm_ByName_OnUnboundCharacter_StillWorks_ForST()
    {
        var s = NewStorage();
        AddUnbound(s, "Виктор");
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Dm, "Виктор", null);
        Assert.Equal(VampireShowResolver.Failure.None, d.FailureCode);
        Assert.Equal(0UL, d.Character!.PlayerId);
    }

    [Fact]
    public void Resolve_Dm_ByUnboundName_WithUser_Fails_UserMismatch()
    {
        var s = NewStorage();
        AddUnbound(s, "Виктор");
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Dm, "Виктор", userId: 42);
        Assert.Equal(VampireShowResolver.Failure.UserMismatch, d.FailureCode);
    }

    // ─── Public-режим ──────────────────────────────────────────────────

    [Fact]
    public void Resolve_Public_ByName_Returns()
    {
        var s = StorageWithOne(playerId: 42UL, name: "Виктор");
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Public, "Виктор", null);
        Assert.Equal(VampireShowResolver.Failure.None, d.FailureCode);
    }

    [Fact]
    public void Resolve_Public_ByUser_NoName_ReturnsBound()
    {
        var s = StorageWithOne(playerId: 42UL, name: "Виктор");
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Public, null, userId: 42);
        Assert.Equal(VampireShowResolver.Failure.None, d.FailureCode);
        Assert.Equal("Виктор", d.Character!.CharacterName);
    }

    [Fact]
    public void Resolve_Public_ByUser_NotBound_Fails()
    {
        var s = NewStorage();
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Public, null, userId: 42, mention: "@ghost");
        Assert.Equal(VampireShowResolver.Failure.UserHasNoCharacter, d.FailureCode);
    }

    [Fact]
    public void Resolve_Public_BothArgs_Mismatch_Fails()
    {
        var s = StorageWithOne(playerId: 42UL, name: "Виктор");
        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Public, "Виктор", userId: 99);
        Assert.Equal(VampireShowResolver.Failure.UserMismatch, d.FailureCode);
    }

    // ─── Диагностика при коллизии имён ─────────────────────────────────

    [Fact]
    public void Resolve_NameClash_ReportsFailure()
    {
        // _characters индексируется по PlayerName — поэтому используем разные PlayerName,
        // но одинаковое CharacterName, чтобы сымитировать однофамильцев.
        var s = NewStorage();
        s.UpsertAsync(new VampireCharacter
        {
            CharacterId = Guid.NewGuid(),
            CharacterName = "Виктор",
            PlayerName = "В1",
            PlayerId = 0,
        }).GetAwaiter().GetResult();
        s.UpsertAsync(new VampireCharacter
        {
            CharacterId = Guid.NewGuid(),
            CharacterName = "Виктор",
            PlayerName = "В2",
            PlayerId = 0,
        }).GetAwaiter().GetResult();

        var d = VampireShowResolver.Resolve(s, VampireShowResolver.Mode.Dm, "Виктор", null);
        Assert.Equal(VampireShowResolver.Failure.CharacterNotFound, d.FailureCode);
        Assert.Contains("Виктор", d.Message);
    }
}
