using System;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace RPBot.VtM;

/// <summary>
/// Реальная реализация <see cref="IDiscordMessageAccessor"/> поверх
/// <see cref="DiscordSocketClient"/>. Используется в проде.
/// <para>
/// В тестах подменяется на <c>FakeDiscordMessageAccessor</c>.
/// </para>
/// <para>
/// Контракт: на каждом вызове <c>Update*</c>/<c>Delete</c> accessor заново
/// достаёт <c>IUserMessage</c> по (channelId, messageId) — handle хранит только
/// координаты, чтобы не удерживать ссылку на устаревший сокет-объект.
/// </para>
/// </summary>
public sealed class DiscordSocketMessageAccessor : IDiscordMessageAccessor
{
    private readonly DiscordSocketClient _client;

    public DiscordSocketMessageAccessor(DiscordSocketClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<DiscordMessageHandle?> TryGetHandleAsync(
        ulong channelId, ulong messageId, CancellationToken ct = default)
    {
        if (_client.ConnectionState != ConnectionState.Connected) return null;
        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel == null) return null;
        try
        {
            var msg = await channel.GetMessageAsync(messageId, options: new RequestOptions
            {
                CancelToken = ct,
            }).ConfigureAwait(false);
            return msg == null ? null : new DiscordMessageHandle(channelId, messageId);
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task UpdateEmbedAsync(
        DiscordMessageHandle handle, Embed embed, CancellationToken ct = default)
    {
        var msg = await ResolveMessageAsync(handle, ct).ConfigureAwait(false);
        try
        {
            await msg.ModifyAsync(m => m.Embed = embed, new RequestOptions { CancelToken = ct })
                .ConfigureAwait(false);
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new MessageNotFoundException(handle.ChannelId, handle.MessageId);
        }
    }

    public async Task UpdateEmbedAndComponentsAsync(
        DiscordMessageHandle handle,
        Embed embed,
        MessageComponent components,
        CancellationToken ct = default)
    {
        var msg = await ResolveMessageAsync(handle, ct).ConfigureAwait(false);
        try
        {
            await msg.ModifyAsync(m =>
            {
                m.Embed = embed;
                m.Components = components;
            }, new RequestOptions { CancelToken = ct }).ConfigureAwait(false);
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new MessageNotFoundException(handle.ChannelId, handle.MessageId);
        }
    }

    public async Task DeleteAsync(DiscordMessageHandle handle, CancellationToken ct = default)
    {
        var msg = await ResolveMessageAsync(handle, ct).ConfigureAwait(false);
        try
        {
            await msg.DeleteAsync(new RequestOptions { CancelToken = ct }).ConfigureAwait(false);
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new MessageNotFoundException(handle.ChannelId, handle.MessageId);
        }
    }

    private async Task<IUserMessage> ResolveMessageAsync(
        DiscordMessageHandle handle, CancellationToken ct)
    {
        var channel = _client.GetChannel(handle.ChannelId) as IMessageChannel
            ?? throw new MessageNotFoundException(handle.ChannelId, handle.MessageId);
        try
        {
            var msg = await channel.GetMessageAsync(handle.MessageId, options: new RequestOptions
            {
                CancelToken = ct,
            }).ConfigureAwait(false);
            if (msg is IUserMessage um) return um;
            throw new MessageNotFoundException(handle.ChannelId, handle.MessageId);
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new MessageNotFoundException(handle.ChannelId, handle.MessageId);
        }
    }
}
