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
    // ─── Новая схема (одна кнопка открытия + picker) ────────────────────────

    [Theory]
    [InlineData("vam_reroll:12345", 12345UL)]
    [InlineData("vam_reroll:42", 42UL)]
    [InlineData("vam_reroll:9999999999", 9999999999UL)]
    public void IsRerollOpenButton_Valid(string customId, ulong expectedUser)
    {
        Assert.True(VampireRollComponents.IsRerollOpenButton(customId, out var user));
        Assert.Equal(expectedUser, user);
    }

    [Theory]
    [InlineData("vam_reroll")]
    [InlineData("vam_reroll:")]
    [InlineData("vam_reroll:abc")]
    [InlineData("vam_reroll:1:42")]      // старая схема — невалидно для открытия
    [InlineData("vam_reroll:42:extra")]
    [InlineData("other_button")]
    [InlineData("")]
    public void IsRerollOpenButton_Invalid_ReturnsFalse(string customId)
    {
        Assert.False(VampireRollComponents.IsRerollOpenButton(customId, out _));
    }

    [Theory]
    [InlineData("vam_reroll_back:12345", 12345UL, true)]
    [InlineData("vam_reroll_back:42", 42UL, true)]
    public void IsRerollBackButton_Valid(string customId, ulong expectedUser, bool expected)
    {
        Assert.Equal(expected, VampireRollComponents.IsRerollBackButton(customId, out var user));
        Assert.Equal(expectedUser, user);
    }

    [Theory]
    [InlineData("vam_reroll_back")]
    [InlineData("vam_reroll_back:")]
    [InlineData("vam_reroll_back:abc")]
    [InlineData("vam_reroll_back:1:extra")]
    [InlineData("other_button")]
    public void IsRerollBackButton_Invalid_ReturnsFalse(string customId)
    {
        Assert.False(VampireRollComponents.IsRerollBackButton(customId, out _));
    }

    [Theory]
    [InlineData("vam_repeat:12345", 12345UL, true)]
    [InlineData("vam_repeat:42", 42UL, true)]
    public void IsRepeatButton_Valid(string customId, ulong expectedUser, bool expected)
    {
        Assert.Equal(expected, VampireRollComponents.IsRepeatButton(customId, out var user));
        Assert.Equal(expectedUser, user);
    }

    [Theory]
    [InlineData("vam_repeat")]
    [InlineData("vam_repeat:")]
    [InlineData("vam_repeat:abc")]
    [InlineData("vam_repeat:1:extra")]
    [InlineData("other_button")]
    public void IsRepeatButton_Invalid_ReturnsFalse(string customId)
    {
        Assert.False(VampireRollComponents.IsRepeatButton(customId, out _));
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

    // ─── Старая схема (vam_reroll:N:userId) больше НЕ парсится ──────────────
    // Подтверждаем: старый формат не открывает picker и не считается кнопкой выбора числа.

    [Theory]
    [InlineData("vam_reroll:1:12345")]
    [InlineData("vam_reroll:2:42")]
    [InlineData("vam_reroll:3:42")]
    public void OldRerollSchema_NotOpenButton(string customId)
    {
        Assert.False(VampireRollComponents.IsRerollOpenButton(customId, out _));
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
    public void BuildRerollAmountPicker_ProducesNonEmptyComponent()
    {
        var picker = VampireRollComponents.BuildRerollAmountPicker(42UL);
        Assert.NotNull(picker);
    }

    [Fact]
    public void BuildEmpty_ProducesEmptyComponent()
    {
        var empty = VampireRollComponents.BuildEmpty();
        Assert.NotNull(empty);
    }

    // ─── SelectMenu customId (vam_reroll_menu:{userId}) ────────────────────
    // Кнопка открытия теперь одна, а picker — SelectMenu; customId меню
    // содержит только userId (значение бросается в component.Data.Values[0]).

    [Theory]
    [InlineData("vam_reroll_menu:12345", 12345UL)]
    [InlineData("vam_reroll_menu:42", 42UL)]
    [InlineData("vam_reroll_menu:9999999999", 9999999999UL)]
    public void TryParseRerollMenu_Valid(string customId, ulong expectedUser)
    {
        Assert.True(VampireRollComponents.TryParseRerollMenu(customId, out var user));
        Assert.Equal(expectedUser, user);
    }

    [Theory]
    [InlineData("vam_reroll_menu")]
    [InlineData("vam_reroll_menu:")]
    [InlineData("vam_reroll_menu:abc")]
    [InlineData("vam_reroll_menu:1:extra")]
    [InlineData("vam_reroll:42")] // open-button — не меню
    [InlineData("other_button")]
    [InlineData("")]
    public void TryParseRerollMenu_Invalid_ReturnsFalse(string customId)
    {
        Assert.False(VampireRollComponents.TryParseRerollMenu(customId, out _));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("3", 3)]
    public void TryParseRerollMenuValue_Valid(string raw, int expected)
    {
        Assert.True(VampireRollComponents.TryParseRerollMenuValue(raw, out var count));
        Assert.Equal(expected, count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseRerollMenuValue_Invalid_ReturnsFalse(string? raw)
    {
        Assert.False(VampireRollComponents.TryParseRerollMenuValue(raw, out _));
    }
}