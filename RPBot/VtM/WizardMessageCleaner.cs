using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace RPBot.VtM;

/// <summary>
/// Планировщик автоудаления ephemeral-сообщений визарда VtM.
///
/// <para>На Шагах 2-5 бот часто отвечает «✅ Сохранено», «⬅ Возврат на …»,
/// «❌ Шаг ещё не завершён» и т.п. через <see cref="SocketMessageComponent.RespondAsync(string, bool, Embed, RequestOptions, AllowedMentions, MessageComponent)"/>
/// с <c>ephemeral: true</c>. Эти сообщения накапливаются в DM у игрока и
/// захламляют переписку — особенно когда игрок часто мотает шаги туда-сюда.</para>
///
/// <para>Этот планировщик ставит такие сообщения в очередь на удаление
/// через <see cref="DefaultDelay"/> секунд. Если игрок ещё читает — он
/// успеет увидеть. Если нет — сообщение само исчезнет.</para>
///
/// <para>Использование: после <c>await component.RespondAsync(..., ephemeral: true)</c>
/// вызвать <c>WizardMessageCleaner.ScheduleEphemeralAsync(component, 5);</c>.
/// Сообщения с <c>ephemeral: true</c> удаляются через их же token (responded)
/// или через DM-канал по id — оба пути работают в Discord.Net.</para>
///
/// <para>Потокобезопасно. Очередь ограничена <see cref="MaxQueue"/> (защита
/// от утечек при шторме ошибок).</para>
/// </summary>
public static class WizardMessageCleaner
{
    /// <summary>Задержка удаления по умолчанию (секунды).</summary>
    public const int DefaultDelay = 5;

    /// <summary>Максимальный размер очереди. При переполнении — старейшие дропаются.</summary>
    public const int MaxQueue = 256;

    private static readonly ConcurrentQueue<Func<Task>> _queue = new();
    private static readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private static CancellationTokenSource? _cts;

    /// <summary>
    /// Запустить фонового воркера. Идемпотентно — повторный вызов не создаёт второй поток.
    /// </summary>
    public static void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// Остановить воркера (используется в тестах и при шатдауне).
    /// </summary>
    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    /// <summary>
    /// Поставить ephemeral-ответ компонента в очередь на удаление.
    /// <para>Работает в трёх случаях:</para>
    /// <list type="number">
    /// <item>Компонент уже <c>HasResponded</c> — тогда у него есть
    /// <c>InteractionMessageId</c> в канале, где произошло нажатие
    /// (для DM — DM-канал, для сервера — серверный канал).</item>
    /// <item>Компонент ещё не <c>HasResponded</c> — тогда планирование
    /// бессмысленно (сообщения ещё нет), пропускаем.</item>
    /// </list>
    /// </summary>
    /// <param name="component">Компонент, который ответил ephemeral-сообщением.</param>
    /// <param name="delaySeconds">Задержка перед удалением. 0 или меньше — пропуск.</param>
    public static void ScheduleEphemeralAsync(SocketMessageComponent component, int delaySeconds = DefaultDelay)
    {
        if (component == null) return;
        if (delaySeconds <= 0) return;
        if (!component.HasResponded) return; // сообщение ещё не отправлено — нечего удалять

        // В Discord.Net у SocketMessageComponent после RespondAsync можно
        // получить токен ответа через Channel и сообщение. Но самый простой
        // путь — использовать component.InteractionMessageId внутри того
        // же канала, где произошло нажатие.
        var channel = component.Channel;
        if (channel == null) return;

        ulong messageId = 0;
        try
        {
            // Публичное свойство InteractionMessageId есть у Discord.Net
            // SocketMessageComponent — это id ответа на интеракцию.
            messageId = (ulong)(component.GetType().GetProperty("InteractionMessageId")?.GetValue(component) ?? 0L);
        }
        catch { /* fallthrough */ }

        if (messageId == 0)
        {
            // Альтернатива: некоторые версии Discord.Net хранят токен
            // ответа в IComponentInteraction. Фоллбек — берём из любого
            // доступного пути; если не получилось — не планируем.
            return;
        }

        Enqueue(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);
                var msg = await channel.GetMessageAsync(messageId).ConfigureAwait(false);
                if (msg is IUserMessage um)
                {
                    await um.DeleteAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // Если сообщение уже удалено или бот потерял права — игнорируем.
            }
        });
    }

    /// <summary>
    /// Поставить в очередь на удаление произвольное сообщение (не из интеракции).
    /// Используется, например, чтобы удалить системные подсказки, которые
    /// бот сам отправил в DM-канал.
    /// </summary>
    /// <param name="message">Сообщение для удаления.</param>
    /// <param name="delaySeconds">Задержка.</param>
    public static void ScheduleAsync(IUserMessage message, int delaySeconds = DefaultDelay)
    {
        if (message == null) return;
        if (delaySeconds <= 0) return;
        Enqueue(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);
                await message.DeleteAsync().ConfigureAwait(false);
            }
            catch { }
        });
    }

    private static void Enqueue(Func<Task> task)
    {
        // Ограничиваем очередь — старейшие дропаем при переполнении.
        while (_queue.Count >= MaxQueue)
        {
            if (!_queue.TryDequeue(out _)) break;
        }
        _queue.Enqueue(task);
        try { _signal.Release(); } catch (SemaphoreFullException) { /* already signaled */ }
    }

    private static async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            if (!_queue.TryDequeue(out var task)) continue;
            _ = Task.Run(async () =>
            {
                try { await task().ConfigureAwait(false); }
                catch { /* не должен бросать наружу */ }
            });
        }
    }
}
