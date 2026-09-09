using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot.VtM;

/// <summary>
/// Автооткат подменю кнопок/меню VtM-броска через 15 с (согласование 2026-09-09):
/// если пользователь открыл picker (выбор количества кубиков для переброса за волю)
/// и за 15 секунд не выбрал 1/2/3 — меню откатывается назад к «Переброс / Повторить / Готово».
/// Аналогично для других подменю, где требуется решение от пользователя.
///
/// <para>Хранит один <see cref="CancellationTokenSource"/> на <c>messageId</c>:
///   • при показе подменю — создаётся/перезаписывается и планируется задача;
///   • при действии пользователя — CTS отменяется, откат не выполняется;
///   • по истечении TTL — вызывается переданный <c>rollback</c>-callback.
///   Если сообщение уже было изменено пользователем — callback тихо игнорируется.</para>
///
/// <para>Race condition: если пользователь нажал в момент срабатывания
/// таймера, выигрывает тот, кто первый успел изменить компоненты.
/// Чтобы избежать «устаревших» откатов, мы храним <see cref="MenuState"/>
/// в локальной записи и сравниваем при пробуждении — если состояние
/// не соответствует ожидаемому, откат не выполняется.</para>
/// </summary>
public static class VampireRollMenuAutoRollback
{
    /// <summary>TTL по умолчанию — 15 секунд.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Колбэк отката (бросает исключение, если сообщение недоступно).</summary>
    /// <param name="expectedState">Ожидаемое состояние подменю (для проверки).</param>
    public delegate Task RollbackAction(MenuState expectedState);

    private sealed record Entry(
        ulong MessageId,
        MenuState ExpectedState,
        CancellationTokenSource Cts);

    private static readonly ConcurrentDictionary<ulong, Entry> _byMessageId = new();

    /// <summary>
    /// Запланировать автооткат для сообщения через <paramref name="timeout"/>.
    /// Если для этого <paramref name="messageId"/> уже есть активный таймер — он отменяется.
    /// </summary>
    public static void Schedule(
        ulong messageId,
        MenuState expectedState,
        RollbackAction rollback,
        TimeSpan? timeout = null)
    {
        if (rollback == null) throw new ArgumentNullException(nameof(rollback));

        // Отменить предыдущий таймер, если был.
        Cancel(messageId);

        var cts = new CancellationTokenSource();
        _byMessageId[messageId] = new Entry(
            MessageId: messageId,
            ExpectedState: expectedState,
            Cts: cts);

        _ = RunAsync(messageId, expectedState, rollback, cts.Token, timeout ?? DefaultTimeout);
    }

    /// <summary>
    /// Отменить автооткат (например, пользователь успел нажать).
    /// </summary>
    public static void Cancel(ulong messageId)
    {
        if (_byMessageId.TryRemove(messageId, out var old))
        {
            try { old.Cts.Cancel(); } catch (ObjectDisposedException) { /* ignore */ }
            old.Cts.Dispose();
        }
    }

    /// <summary>Сколько сейчас активных таймеров (для тестов).</summary>
    public static int ActiveCount => _byMessageId.Count;

    /// <summary>Очистить все таймеры (для тестов).</summary>
    public static void Clear()
    {
        foreach (var kvp in _byMessageId)
        {
            if (_byMessageId.TryRemove(kvp.Key, out var entry))
            {
                try { entry.Cts.Cancel(); } catch (ObjectDisposedException) { /* ignore */ }
                entry.Cts.Dispose();
            }
        }
    }

    private static async Task RunAsync(
        ulong messageId,
        MenuState expectedState,
        RollbackAction rollback,
        CancellationToken token,
        TimeSpan timeout)
    {
        try
        {
            await Task.Delay(timeout, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Проверяем состояние подменю.
        if (!_byMessageId.TryGetValue(messageId, out var entry)) return;
        if (entry.ExpectedState != expectedState) return;
        if (!_byMessageId.TryRemove(messageId, out _)) return;

        try
        {
            await rollback(expectedState).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Сообщение могло быть удалено / бот потерял доступ — тихо игнорируем.
        }
        finally
        {
            entry.Cts.Dispose();
        }
    }

    /// <summary>Идентификатор состояния подменю (для защиты от race).</summary>
    public enum StateKind
    {
        RerollPicker,
    }

    /// <summary>
    /// Описание ожидаемого состояния подменю.
    /// </summary>
    public sealed record MenuState(StateKind Kind, ulong OriginalUserId)
    {
        public static MenuState RerollPicker(ulong userId) => new(StateKind.RerollPicker, userId);
    }
}
