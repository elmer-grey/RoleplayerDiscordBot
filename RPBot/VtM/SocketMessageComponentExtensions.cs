using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace RPBot.VtM;

/// <summary>
/// Extension-методы для <see cref="SocketMessageComponent"/>, которые
/// автоматически планируют автоудаление ephemeral-сообщений через
/// <see cref="WizardMessageCleaner"/>. Заменяют прямые вызовы
/// <c>component.RespondAsync(..., ephemeral: true)</c> в визарде, чтобы DM
/// не захламлялся подтверждениями «Обновлено», «Сохранено» и т.п.
/// </summary>
public static class SocketMessageComponentExtensions
{
    /// <summary>
    /// Стандартная задержка (5 секунд) для автоудаления ack-сообщений.
    /// </summary>
    public const int DefaultEphemeralDelaySeconds = 5;

    /// <summary>
    /// Ответить на компонент-взаимодействие ephemeral-сообщением и поставить его
    /// в очередь на удаление через <paramref name="delaySeconds"/> секунд.
    /// </summary>
    /// <remarks>
    /// <para>Полностью заменяет прямой <c>component.RespondAsync(text, ephemeral: true)</c>:
    /// диспатчит RespondAsync, затем через <see cref="WizardMessageCleaner"/> планирует
    /// удаление. Если RespondAsync упал, планирование не выполняется.</para>
    /// <para>Планирование лучше-чем-идеально: при сбое удаления (сообщение уже удалено,
    /// нет прав) глотаем исключение внутри воркера.</para>
    /// </remarks>
    public static async Task RespondEphemeralAsync(
        this SocketMessageComponent component,
        string text,
        int delaySeconds = DefaultEphemeralDelaySeconds)
    {
        await component.RespondAsync(text, ephemeral: true);
        WizardMessageCleaner.ScheduleEphemeralAsync(component, delaySeconds);
    }

    /// <summary>
    /// Followup-аналог: отправить ephemeral-сообщение после Defer и поставить его
    /// в очередь на удаление.
    /// </summary>
    public static async Task FollowupEphemeralAsync(
        this SocketMessageComponent component,
        string text,
        int delaySeconds = DefaultEphemeralDelaySeconds)
    {
        await component.FollowupAsync(text, ephemeral: true);
        WizardMessageCleaner.ScheduleEphemeralAsync(component, delaySeconds);
    }

    /// <summary>
    /// RespondAsync с MessageComponent (кнопки) — с автоудалением через <paramref name="delaySeconds"/>.
    /// </summary>
    public static async Task RespondEphemeralAsync(
        this SocketMessageComponent component,
        string text,
        MessageComponent components,
        int delaySeconds = DefaultEphemeralDelaySeconds)
    {
        await component.RespondAsync(text, embeds: null, isTTS: false, ephemeral: true, allowedMentions: null, components: components);
        WizardMessageCleaner.ScheduleEphemeralAsync(component, delaySeconds);
    }
}
