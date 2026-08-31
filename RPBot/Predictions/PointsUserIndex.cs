using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot
{
public sealed class PointsUserIndex
{
private readonly string _statePath;
private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, string>> _names = new();
private readonly SemaphoreSlim _ioLock = new(1, 1);

public PointsUserIndex(string statePath)
{
    _statePath = statePath;
}

public string? GetName(ulong guildId, ulong userId)
{
    if (_names.TryGetValue(guildId, out var g) && g.TryGetValue(userId, out var name))
    return name;
    return null;
}

public void Upsert(ulong guildId, ulong userId, string? name)
{
    if (string.IsNullOrWhiteSpace(name))
    return;

    var g = _names.GetOrAdd(guildId, _ => new ConcurrentDictionary<ulong, string>());
    g[userId] = name.Trim();
}

public void UpsertFromUser(ulong guildId, IUser user)
{
    if (user == null) return;
    var name = user.Username;
    // Prefer guild nickname when available
    if (user is SocketGuildUser gu && !string.IsNullOrWhiteSpace(gu.Nickname))
    name = gu.Nickname;
    Upsert(guildId, user.Id, name);
}

public async Task LoadAsync()
{
    await _ioLock.WaitAsync().ConfigureAwait(false);
    try
    {
    if (!File.Exists(_statePath))
    return;

    var json = await File.ReadAllTextAsync(_statePath).ConfigureAwait(false);
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var raw = JsonSerializer.Deserialize<Dictionary<ulong, Dictionary<ulong, string>>>(json, options);
    _names.Clear();
    if (raw != null)
    {
    foreach (var g in raw)
    {
        _names[g.Key] = new ConcurrentDictionary<ulong, string>(g.Value);
    }
    }
    }
    catch
    {
    // ignore
    }
    finally
    {
    _ioLock.Release();
    }
}

public async Task SaveAsync()
{
    await _ioLock.WaitAsync().ConfigureAwait(false);
    try
    {
    var dir = Path.GetDirectoryName(_statePath) ?? AppContext.BaseDirectory;
    Directory.CreateDirectory(dir);

    var snapshot = new Dictionary<ulong, Dictionary<ulong, string>>();
    foreach (var g in _names)
    snapshot[g.Key] = new Dictionary<ulong, string>(g.Value);

    var options = new JsonSerializerOptions { WriteIndented = true };
    var json = JsonSerializer.Serialize(snapshot, options);
    await File.WriteAllTextAsync(_statePath, json).ConfigureAwait(false);
    }
    catch
    {
    // ignore
    }
    finally
    {
    _ioLock.Release();
    }
}
}
}
