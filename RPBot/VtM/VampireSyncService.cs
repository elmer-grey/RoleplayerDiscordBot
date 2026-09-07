using System;
using System.Threading;
using System.Threading.Tasks;
using Discord;

namespace RPBot.VtM;

/// <summary>
/// Сервис глобальной синхронизации видимых листов персонажа и блоков под листом.
/// </summary>
/// <remarks>
/// <para>Зависит от трёх внешних слоёв:</para>
/// <list type="bullet">
///   <item><see cref="VampireStorage"/> — источник истины персонажа (горячая замена
///   не подменяется: при каждом обращении читается текущий снимок).</item>
///   <item><see cref="VampireDisplayIndex"/> — где хранятся ссылки на сообщения
///   с листом (DM + public).</item>
///   <item><see cref="VampireDmBlockIndex"/> — где хранятся ссылки на видимые
///   блоки под листом в DM конкретного игрока. Передаётся не как зависимость —
///   см. <see cref="SyncDmBlocksForUserAsync"/>.</item>
///   <item><see cref="IDiscordMessageAccessor"/> — сетевой слой (реальный или тест-fake).</item>
/// </list>
/// <para>Метод <see cref="SyncCharacterAsync"/> единообразно для всех сообщений по
/// персонажу: пересобирает embed (и компоненты, где они заданы), вызывает
/// <see cref="IDiscordMessageAccessor"/>, при <see cref="MessageNotFoundException"/> —
/// снимает регистрацию из индекса.</para>
/// </remarks>
public sealed class VampireSyncService
{
    private readonly VampireStorage _storage;
    private readonly VampireDisplayIndex _displayIndex;
    private readonly IDiscordMessageAccessor _discord;

    public VampireSyncService(
        VampireStorage storage,
        VampireDisplayIndex displayIndex,
        IDiscordMessageAccessor discord)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _displayIndex = displayIndex ?? throw new ArgumentNullException(nameof(displayIndex));
        _discord = discord ?? throw new ArgumentNullException(nameof(discord));
    }

    /// <summary>
    /// Обновить все видимые листы персонажа (DM + public).
    /// </summary>
    public async Task<SyncResult> SyncCharacterAsync(Guid characterId, CancellationToken ct = default)
    {
        var ch = _storage.GetByCharacterId(characterId);
        if (ch == null)
            return new SyncResult(0, 0, 0);

        var messages = _displayIndex.GetMessages(characterId.ToString("N"));
        return await UpdateSheetMessagesAsync(ch, messages, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Обновить видимые блоки под листом в DM конкретного игрока.
    /// </summary>
    public async Task<SyncResult> SyncDmBlocksForUserAsync(
        ulong userId,
        Guid characterId,
        CancellationToken ct = default)
    {
        var ch = _storage.GetByCharacterId(characterId);
        if (ch == null)
            return new SyncResult(0, 0, 0);

        var blocks = new VampireDmBlockIndex(userId);
        await blocks.LoadAsync(ct).ConfigureAwait(false);
        var all = blocks.GetAll(characterId.ToString("N"));
        int updated = 0, missing = 0, errors = 0;

        foreach (var kv in all)
        {
            ct.ThrowIfCancellationRequested();
            var refr = kv.Value;
            DiscordMessageHandle? handle;
            try
            {
                handle = await _discord.TryGetHandleAsync(refr.ChannelId, refr.MessageId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                errors++;
                continue;
            }

            if (handle == null)
            {
                await blocks.ClearAsync(characterId.ToString("N"), kv.Key, ct).ConfigureAwait(false);
                missing++;
                continue;
            }

            try
            {
                await UpdateOneDmBlockAsync(handle.Value, ch, kv.Key, ct).ConfigureAwait(false);
                updated++;
            }
            catch (OperationCanceledException) { throw; }
            catch (MessageNotFoundException)
            {
                await blocks.ClearAsync(characterId.ToString("N"), kv.Key, ct).ConfigureAwait(false);
                missing++;
            }
            catch
            {
                errors++;
            }
        }

        return new SyncResult(updated, missing, errors);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private async Task<SyncResult> UpdateSheetMessagesAsync(
        VampireCharacter ch,
        System.Collections.Generic.IReadOnlyList<DisplayMessageRef> messages,
        CancellationToken ct)
    {
        int updated = 0, missing = 0, errors = 0;

        foreach (var refr in messages)
        {
            ct.ThrowIfCancellationRequested();
            DiscordMessageHandle? handle;
            try
            {
                handle = await _discord.TryGetHandleAsync(refr.ChannelId, refr.MessageId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                errors++;
                continue;
            }

            if (handle == null)
            {
                await _displayIndex.UnregisterAsync(refr.ChannelId, refr.MessageId, ct).ConfigureAwait(false);
                missing++;
                continue;
            }

            try
            {
                var embed = VampireSheetEmbed.Build(ch);
                var components = refr.Kind == SheetMessageKind.DmSheetWithButtons
                    ? VampireSheetComponents.Build(ch, showExperienceButton: true, showFrenzyButton: true)
                    : null;
                if (components != null)
                    await _discord.UpdateEmbedAndComponentsAsync(handle.Value, embed, components, ct).ConfigureAwait(false);
                else
                    await _discord.UpdateEmbedAsync(handle.Value, embed, ct).ConfigureAwait(false);
                updated++;
            }
            catch (OperationCanceledException) { throw; }
            catch (MessageNotFoundException)
            {
                await _displayIndex.UnregisterAsync(refr.ChannelId, refr.MessageId, ct).ConfigureAwait(false);
                missing++;
            }
            catch
            {
                errors++;
            }
        }

        return new SyncResult(updated, missing, errors);
    }

    private Task UpdateOneDmBlockAsync(
        DiscordMessageHandle handle,
        VampireCharacter ch,
        DmBlockKind kind,
        CancellationToken ct)
    {
        switch (kind)
        {
            case DmBlockKind.Description:
            {
                var desc = VampireDescriptionEmbed.Build(ch);
                if (desc == null)
                    throw new InvalidOperationException("Описание недоступно (нет Bio и аватара)");
                return _discord.UpdateEmbedAndComponentsAsync(
                    handle,
                    desc,
                    VampireDescriptionComponents.Build(ch),
                    ct);
            }
            case DmBlockKind.Willpower:
                return _discord.UpdateEmbedAndComponentsAsync(
                    handle,
                    VampireWillpowerEmbed.Build(ch),
                    VampireWillpowerComponents.Build(ch.CharacterId),
                    ct);
            case DmBlockKind.Health:
                return _discord.UpdateEmbedAndComponentsAsync(
                    handle,
                    VampireHealthEmbed.Build(ch),
                    VampireHealthComponents.Build(ch.CharacterId),
                    ct);
            default:
                throw new InvalidOperationException($"Unknown block kind {kind}");
        }
    }
}

/// <summary>
/// Сводка результата синхронизации.
/// </summary>
/// <param name="Updated">Успешно обновлено сообщений.</param>
/// <param name="Missing">Сообщений не найдено (индекс очищен).</param>
/// <param name="Errors">Сообщений с ошибкой (прочие исключения).</param>
public readonly record struct SyncResult(int Updated, int Missing, int Errors)
{
    public bool HasAny => Updated + Missing + Errors > 0;
}
