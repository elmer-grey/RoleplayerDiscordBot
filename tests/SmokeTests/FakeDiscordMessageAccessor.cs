using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using RPBot.VtM;

namespace SmokeTests;

/// <summary>
/// Тест-double для <see cref="IDiscordMessageAccessor"/>.
/// Запоминает все вызовы и позволяет настроить ответы.
/// </summary>
public sealed class FakeDiscordMessageAccessor : IDiscordMessageAccessor
{
    public sealed record Call(
        string Method,
        ulong ChannelId,
        ulong MessageId,
        Embed? Embed = null,
        MessageComponent? Components = null);

    public List<Call> Calls { get; } = new();

    /// <summary>
    /// Карта "существующих" сообщений. Если ключ отсутствует — TryGet возвращает null,
    /// Update*/Delete бросают <see cref="MessageNotFoundException"/>.
    /// </summary>
    public HashSet<(ulong ChannelId, ulong MessageId)> Known { get; } = new();

    /// <summary>Если непусто — все Update*/Delete бросают эту ошибку.</summary>
    public Exception? ThrowOnUpdate { get; set; }

    public Task<DiscordMessageHandle?> TryGetHandleAsync(ulong channelId, ulong messageId, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(TryGetHandleAsync), channelId, messageId));
        if (!Known.Contains((channelId, messageId)))
            return Task.FromResult<DiscordMessageHandle?>(null);
        return Task.FromResult<DiscordMessageHandle?>(new DiscordMessageHandle(channelId, messageId));
    }

    public Task UpdateEmbedAsync(DiscordMessageHandle handle, Embed embed, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(UpdateEmbedAsync), handle.ChannelId, handle.MessageId, embed, null));
        if (ThrowOnUpdate != null) throw ThrowOnUpdate;
        if (!Known.Contains((handle.ChannelId, handle.MessageId)))
            throw new MessageNotFoundException(handle.ChannelId, handle.MessageId);
        return Task.CompletedTask;
    }

    public Task UpdateEmbedAndComponentsAsync(
        DiscordMessageHandle handle, Embed embed, MessageComponent components, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(UpdateEmbedAndComponentsAsync), handle.ChannelId, handle.MessageId, embed, components));
        if (ThrowOnUpdate != null) throw ThrowOnUpdate;
        if (!Known.Contains((handle.ChannelId, handle.MessageId)))
            throw new MessageNotFoundException(handle.ChannelId, handle.MessageId);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(DiscordMessageHandle handle, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(DeleteAsync), handle.ChannelId, handle.MessageId));
        if (ThrowOnUpdate != null) throw ThrowOnUpdate;
        if (!Known.Contains((handle.ChannelId, handle.MessageId)))
            throw new MessageNotFoundException(handle.ChannelId, handle.MessageId);
        return Task.CompletedTask;
    }
}
