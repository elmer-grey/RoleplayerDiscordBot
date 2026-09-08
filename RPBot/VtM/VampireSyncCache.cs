using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace RPBot.VtM;

/// <summary>
/// Глобальный реестр <see cref="VampireChangeDebouncer"/>'ов.
/// Создаёт debouncer лениво, при первом обращении, и кеширует на всё время работы бота.
/// <para>
/// В тестах реестр сбрасывается через <see cref="ResetForTests"/> между прогонами.
/// </para>
/// </summary>
public static class VampireSyncCache
{
    private static readonly ConcurrentDictionary<int, Lazy<VampireChangeDebouncer>> _byGuild =
        new ConcurrentDictionary<int, Lazy<VampireChangeDebouncer>>();

    /// <summary>
    /// Получить debouncer для гильдии. <paramref name="factory"/> создаёт
    /// <see cref="VampireSyncService"/> с актуальной реализацией
    /// <see cref="IDiscordMessageAccessor"/> (в проде это реальный Discord-клиент,
    /// в тестах — <c>FakeDiscordMessageAccessor</c>).
    /// </summary>
    public static VampireChangeDebouncer Get(
        ulong guildId,
        Func<ulong, VampireSyncService> factory,
        TimeSpan? debounceDelay = null)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));

        var lazy = _byGuild.GetOrAdd(
            unchecked((int)guildId),
            _ => new Lazy<VampireChangeDebouncer>(
                () => new VampireChangeDebouncer(factory(guildId), debounceDelay)));
        return lazy.Value;
    }

    /// <summary>
    /// Принудительно выполнить все ожидающие синхронизации (для shutdown/тестов).
    /// </summary>
    public static async Task FlushAllAsync(System.Threading.CancellationToken ct = default)
    {
        foreach (var kv in _byGuild)
        {
            ct.ThrowIfCancellationRequested();
            if (kv.Value.IsValueCreated)
                await kv.Value.Value.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Сбросить реестр (только для тестов).</summary>
    public static void ResetForTests()
    {
        foreach (var kv in _byGuild)
        {
            if (kv.Value.IsValueCreated)
                kv.Value.Value.Dispose();
        }
        _byGuild.Clear();
    }
}
