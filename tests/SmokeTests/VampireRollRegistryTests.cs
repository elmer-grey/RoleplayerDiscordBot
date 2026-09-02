using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты для <see cref="VampireRollRegistry"/> и <see cref="VampireRollComponents"/>.
/// Discord-компоненты — это просто маршрутизация customId, без Discord API.
/// </summary>
public class VampireRollRegistryTests
{
    [Fact]
    public void Record_AndTryGet_RoundTrip()
    {
        var reg = new VampireRollRegistry();
        reg.Record(userId: 42, messageId: 100, regularDice: new[] { 1, 2, 3 }, hungerDice: new[] { 7, 8 }, bonusDie: null);

        Assert.True(reg.TryGet(42, out var snap));
        Assert.NotNull(snap);
        Assert.Equal(100UL, snap!.MessageId);
        Assert.Equal(new[] { 1, 2, 3 }, snap.RegularDice);
        Assert.Equal(new[] { 7, 8 }, snap.HungerDice);
        Assert.Null(snap.BonusDie);
        Assert.Equal(3, snap.RegularCount);
    }

    [Fact]
    public void TryGet_Missing_ReturnsFalse()
    {
        var reg = new VampireRollRegistry();
        Assert.False(reg.TryGet(99, out var snap));
        Assert.Null(snap);
    }

    [Fact]
    public void Forget_RemovesEntry()
    {
        var reg = new VampireRollRegistry();
        reg.Record(1, 10, new[] { 5 }, Array.Empty<int>(), null);
        Assert.True(reg.TryGet(1, out _));
        reg.Forget(1);
        Assert.False(reg.TryGet(1, out _));
    }

    [Fact]
    public void Record_OverwritesPrevious()
    {
        var reg = new VampireRollRegistry();
        reg.Record(1, 10, new[] { 5 }, Array.Empty<int>(), null);
        reg.Record(1, 20, new[] { 1, 2, 3 }, new[] { 4 }, 7);

        Assert.True(reg.TryGet(1, out var snap));
        Assert.Equal(20UL, snap!.MessageId);
        Assert.Equal(new[] { 1, 2, 3 }, snap.RegularDice);
        Assert.Equal(new[] { 4 }, snap.HungerDice);
        Assert.Equal(7, snap.BonusDie);
    }

    [Fact]
    public void TTL_ExpiresAfterTimeout()
    {
        var reg = new VampireRollRegistry(TimeSpan.FromMilliseconds(10));
        reg.Record(1, 10, new[] { 5 }, Array.Empty<int>(), null);
        Assert.True(reg.TryGet(1, out _));

        System.Threading.Thread.Sleep(50);

        Assert.False(reg.TryGet(1, out _));
    }

    [Fact]
    public void TTL_RejectsZeroOrNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VampireRollRegistry(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VampireRollRegistry(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Record_RejectsEmptyRegular()
    {
        var reg = new VampireRollRegistry();
        Assert.Throws<ArgumentException>(() =>
            reg.Record(1, 10, Array.Empty<int>(), Array.Empty<int>(), null));
    }

    [Fact]
    public void Record_AllowsEmptyHunger()
    {
        var reg = new VampireRollRegistry();
        reg.Record(1, 10, new[] { 5, 6 }, Array.Empty<int>(), null);
        Assert.True(reg.TryGet(1, out var snap));
        Assert.Empty(snap!.HungerDice);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var reg = new VampireRollRegistry();
        reg.Record(1, 10, new[] { 1 }, Array.Empty<int>(), null);
        reg.Record(2, 20, new[] { 2 }, Array.Empty<int>(), null);
        Assert.Equal(2, reg.Count);

        reg.Clear();
        Assert.Equal(0, reg.Count);
        Assert.False(reg.TryGet(1, out _));
        Assert.False(reg.TryGet(2, out _));
    }

    [Fact]
    public void DefaultTtl_IsOneMinute()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), VampireRollRegistry.DefaultTtl);
    }
}

public class VampireRollComponentsTests
{
    [Theory]
    [InlineData("vam_reroll:1:12345", 12345UL, 1)]
    [InlineData("vam_reroll:2:42", 42UL, 2)]
    [InlineData("vam_reroll:3:9999999999", 9999999999UL, 3)]
    public void TryParseReroll_Valid(string customId, ulong expectedUser, int expectedCount)
    {
        Assert.True(VampireRollComponents.TryParseReroll(customId, out var user, out var count));
        Assert.Equal(expectedUser, user);
        Assert.Equal(expectedCount, count);
    }

    [Theory]
    [InlineData("vam_reroll:0:42")]
    [InlineData("vam_reroll:4:42")]
    [InlineData("vam_reroll:-1:42")]
    [InlineData("vam_reroll:abc:42")]
    [InlineData("vam_reroll:1:notnumber")]
    [InlineData("vam_reroll:1")]
    [InlineData("vam_reroll:1:42:extra")]
    [InlineData("other_button")]
    [InlineData("")]
    public void TryParseReroll_Invalid_ReturnsFalse(string customId)
    {
        Assert.False(VampireRollComponents.TryParseReroll(customId, out _, out _));
    }

    [Theory]
    [InlineData("vam_done:12345", 12345UL, true)]
    [InlineData("vam_done:42", 42UL, true)]
    public void IsDoneButton_Valid(string customId, ulong expectedUser, bool expected)
    {
        Assert.Equal(expected, VampireRollComponents.IsDoneButton(customId, out var user));
        Assert.Equal(expectedUser, user);
    }

    [Theory]
    [InlineData("vam_done")]
    [InlineData("vam_done:")]
    [InlineData("vam_done:abc")]
    [InlineData("vam_done:1:extra")]
    [InlineData("other_button")]
    public void IsDoneButton_Invalid_ReturnsFalse(string customId)
    {
        Assert.False(VampireRollComponents.IsDoneButton(customId, out _));
    }

    [Fact]
    public void BuildRollButtons_ProducesNonEmptyComponent()
    {
        var buttons = VampireRollComponents.BuildRollButtons(42UL);
        Assert.NotNull(buttons);
        // Не проверяем структуру — это DNET-объект, мы лишь удостоверяемся,
        // что строитель не падает и отдаёт ненулевой MessageComponent.
    }

    [Fact]
    public void BuildEmpty_ProducesEmptyComponent()
    {
        var empty = VampireRollComponents.BuildEmpty();
        Assert.NotNull(empty);
    }
}