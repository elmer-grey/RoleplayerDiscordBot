using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM;

/// <summary>
/// Хранилище активного чарника на каждого игрока, для каждой гильдии.
/// </summary>
/// <remarks>
/// <para>Используется, чтобы бот понимал, по какому именно персонажу делать бросок,
/// если у игрока несколько чарников в одной гильдии. Один игрок → один
/// активный чарник в каждой гильдии (привязка по Discord ID игрока).</para>
///
/// <para>Хранится в памяти процесса. На рестарте теряется — игрок заново
/// сделает активным нужного чарника кнопкой. Постоянное хранение
/// (в VampireStorage) — в плане, но не критично для текущей итерации.</para>
/// </remarks>
public sealed class VampireActiveRegistry
{
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, Guid>> _byGuild
        = new();

    private static readonly VampireActiveRegistry _instance = new();
    public static VampireActiveRegistry Instance => _instance;

    private VampireActiveRegistry() { }

    /// <summary>
    /// Получить активный чарник игрока в гильдии. null — если не задан.
    /// </summary>
    public Guid? GetActiveCharacterId(ulong guildId, ulong playerId)
    {
        if (_byGuild.TryGetValue(guildId, out var byPlayer)
            && byPlayer.TryGetValue(playerId, out var charId))
        {
            return charId;
        }
        return null;
    }

    /// <summary>
    /// Установить активный чарник игрока в гильдии.
    /// </summary>
    public void SetActiveCharacterId(ulong guildId, ulong playerId, Guid characterId)
    {
        var byPlayer = _byGuild.GetOrAdd(guildId, _ => new ConcurrentDictionary<ulong, Guid>());
        byPlayer[playerId] = characterId;
    }

    /// <summary>
    /// Снять привязку (например, чарник удалён). Игрок без активного — сможет
    /// выбрать заново кнопкой.
    /// </summary>
    public void ClearActiveCharacterId(ulong guildId, ulong playerId)
    {
        if (_byGuild.TryGetValue(guildId, out var byPlayer))
            byPlayer.TryRemove(playerId, out _);
    }

    /// <summary>
    /// Снять привязку ко всем чарникам, которые ссылаются на этот id.
    /// Вызывать при удалении персонажа.
    /// </summary>
    public void ClearByCharacterId(ulong guildId, Guid characterId)
    {
        if (!_byGuild.TryGetValue(guildId, out var byPlayer)) return;
        foreach (var kv in byPlayer.ToArray())
        {
            if (kv.Value == characterId) byPlayer.TryRemove(kv.Key, out _);
        }
    }
}
