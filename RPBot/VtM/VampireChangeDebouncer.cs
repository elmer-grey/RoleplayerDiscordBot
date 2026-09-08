using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot.VtM;

/// <summary>
/// Debounce-обёртка над <see cref="VampireSyncService"/>.
/// При серии кликов подряд (например, игрок нажимает «+1 воля» 5 раз за секунду)
/// планирует один реальный <see cref="VampireSyncService.SyncAllAsync"/> через
/// ~<see cref="DebounceDelay"/> после последнего клика, а не на каждый.
/// <para>
/// Хранит per-character состояние в <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// При новом вызове <see cref="Request"/> отменяется предыдущий таймер этого
/// персонажа и запускается новая задержка (trailing debounce).
/// </para>
/// <para>
/// Потокобезопасность: запись состояния идёт через
/// <see cref="ConcurrentDictionary{TKey, TValue}.AddOrUpdate"/>.
/// Сам <see cref="VampireSyncService.SyncAllAsync"/> вызывается последовательно
/// для одного персонажа — между вызовами одного персонажа гонок нет.
/// </para>
/// </summary>
public sealed class VampireChangeDebouncer : IDisposable
{
    /// <summary>Стандартная задержка debounce (1.5 с).</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(1500);

    private readonly VampireSyncService _sync;
    private readonly TimeSpan _delay;
    private readonly ConcurrentDictionary<Guid, PerCharacterState> _states =
        new ConcurrentDictionary<Guid, PerCharacterState>();

    private int _disposed; // 0/1 — через Interlocked

    /// <summary>
    /// Создать debouncer.
    /// </summary>
    /// <param name="sync">Сервис, который будет вызываться после debounce-окна.</param>
    /// <param name="delay">Задержка. По умолчанию <see cref="DefaultDelay"/>. В тестах передают короткое значение (100 мс).</param>
    public VampireChangeDebouncer(VampireSyncService sync, TimeSpan? delay = null)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _delay = delay ?? DefaultDelay;
        if (_delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay), "Debounce delay должен быть > 0.");
    }

    /// <summary>Текущая задержка (для тестов и логов).</summary>
    public TimeSpan DebounceDelay => _delay;

    /// <summary>
    /// Запросить синхронизацию персонажа. Если в течение <see cref="DebounceDelay"/>
    /// придёт ещё один запрос по тому же персонажу — таймер сбрасывается.
    /// Метод неблокирующий: возвращает управление сразу.
    /// </summary>
    public void Request(Guid characterId, ulong userId)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        _states.AddOrUpdate(
            characterId,
            _ => CreateState(characterId, userId),
            (_, prev) => RefreshState(prev));
    }

    /// <summary>
    /// Принудительно выполнить все ожидающие синхронизации немедленно.
    /// Используется в тестах и при завершении работы. Каждый pending-таск отменяется
    /// и реальный <see cref="VampireSyncService.SyncAllAsync"/> запускается прямо сейчас.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        foreach (var kv in _states)
        {
            ct.ThrowIfCancellationRequested();
            if (_states.TryRemove(kv.Key, out var state))
            {
                CancelState(state);
                try
                {
                    await _sync.SyncAllAsync(state.CharacterId, state.UserId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Игнорируем сетевые ошибки — клик кнопки позже попробует снова.
                }
            }
        }
    }

    /// <summary>Количество персонажей с активным pending-таймером (для тестов/диагностики).</summary>
    public int PendingCount => _states.Count;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (var kv in _states)
        {
            if (_states.TryRemove(kv.Key, out var state))
                CancelState(state);
        }
    }

    // ─── internal: состояние одного персонажа ─────────────────────────

    private sealed class PerCharacterState
    {
        public Guid CharacterId;
        public ulong UserId;
        public CancellationTokenSource Cts = null!;
    }

    private PerCharacterState CreateState(Guid characterId, ulong userId)
    {
        var state = new PerCharacterState
        {
            CharacterId = characterId,
            UserId = userId,
            Cts = new CancellationTokenSource(),
        };
        ScheduleFireAndForget(state);
        return state;
    }

    private PerCharacterState RefreshState(PerCharacterState prev)
    {
        CancelState(prev);
        prev.Cts = new CancellationTokenSource();
        ScheduleFireAndForget(prev);
        return prev;
    }

    private void ScheduleFireAndForget(PerCharacterState state)
    {
        var cts = state.Cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_delay, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // перебито следующим Request или Dispose
            }

            // Достаём состояние из словаря. Если его там уже нет (Reset/Dispose)
            // или оно было подменено новым — ничего не делаем.
            if (!_states.TryRemove(state.CharacterId, out var actual) || actual != state)
                return;

            CancelState(actual);

            if (Volatile.Read(ref _disposed) != 0) return;

            try
            {
                await _sync.SyncAllAsync(actual.CharacterId, actual.UserId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Проглатываем: следующий клик кнопки сгенерирует новый Request.
            }
        }, cts.Token);
    }

    private static void CancelState(PerCharacterState state)
    {
        try { state.Cts.Cancel(); } catch { /* ignore */ }
        state.Cts.Dispose();
    }
}
