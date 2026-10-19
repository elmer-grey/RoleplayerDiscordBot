using System;
using System.Collections.Generic;
using RPBot;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Merge-логика VoiceRoomCommandPermissions.EnsureChannelAllow:
/// существующие overrides (роли/юзеры/другие каналы) не теряются; идемпотентность; перезапись deny.
/// </summary>
public class VoiceRoomCommandPermissionsTests
{
    private const ulong ChannelId = 123456789012345678UL;

    [Fact]
    public void Empty_NoEntries_AddsChannelAllow()
    {
        var perms = new List<VoiceRoomCommandPermissions.PermissionEntryDto>();

        var changed = VoiceRoomCommandPermissions.EnsureChannelAllow(perms, ChannelId);

        Assert.True(changed);
        Assert.Single(perms);
        Assert.Equal(ChannelId, perms[0].TargetId);
        Assert.Equal(3, perms[0].TargetType); // Channel
        Assert.True(perms[0].Permission);
    }

    [Fact]
    public void ExistingOtherEntries_Preserved()
    {
        var perms = new List<VoiceRoomCommandPermissions.PermissionEntryDto>
        {
            new() { TargetId = 111UL, TargetType = 1, Permission = true },   // роль
            new() { TargetId = 222UL, TargetType = 2, Permission = false },  // юзер deny
        };

        var changed = VoiceRoomCommandPermissions.EnsureChannelAllow(perms, ChannelId);

        Assert.True(changed);
        Assert.Equal(3, perms.Count);
        Assert.Contains(perms, p => p.TargetId == 111UL && p.TargetType == 1);
        Assert.Contains(perms, p => p.TargetId == 222UL && p.TargetType == 2 && !p.Permission);
        Assert.Contains(perms, p => p.TargetId == ChannelId && p.TargetType == 3 && p.Permission);
    }

    [Fact]
    public void AlreadyAllowed_Idempotent_NoChange()
    {
        var perms = new List<VoiceRoomCommandPermissions.PermissionEntryDto>
        {
            new() { TargetId = ChannelId, TargetType = 3, Permission = true },
        };

        var changed = VoiceRoomCommandPermissions.EnsureChannelAllow(perms, ChannelId);

        Assert.False(changed);
        Assert.Single(perms);
    }

    [Fact]
    public void ChannelDeny_OverwrittenToAllow()
    {
        var perms = new List<VoiceRoomCommandPermissions.PermissionEntryDto>
        {
            new() { TargetId = ChannelId, TargetType = 3, Permission = false },
        };

        var changed = VoiceRoomCommandPermissions.EnsureChannelAllow(perms, ChannelId);

        Assert.True(changed);
        Assert.Single(perms);
        Assert.True(perms[0].Permission);
    }
}