using System;
using System.Threading;
using System.Threading.Tasks;
using Discord;

namespace RPBot.VtM
{
    /// <summary>
    /// Абстракция доступа к Discord-каналам/сообщениям для тестируемой синхронизации.
    /// </summary>
    /// <remarks>
    /// <para>Реальная реализация — адаптер вокруг <see cref="DiscordSocketClient"/>;
    /// в тестах используется in-memory fake.</para>
    /// <para>Использует <see cref="DiscordMessageHandle"/> вместо прямого
    /// <c>IMessage</c>: handle оборачивает реальное сообщение, но фейк
    /// может не реализовывать <c>IMessage</c>.</para>
    /// <para><see cref="TryGetHandleAsync"/> должен вернуть null, если сообщение
    /// удалено/недоступно, а не бросать исключение — это основной сигнал для очистки
    /// индекса.</para>
    /// </remarks>
    public interface IDiscordMessageAccessor
    {
        /// <summary>
        /// Попытаться получить handle сообщения по (channelId, messageId). Возвращает null,
        /// если сообщение не найдено.
        /// </summary>
        Task<DiscordMessageHandle?> TryGetHandleAsync(ulong channelId, ulong messageId, CancellationToken ct = default);

        /// <summary>
        /// Обновить embed сообщения. Бросает <see cref="MessageNotFoundException"/>,
        /// если сообщение было удалено (HTTP 404).
        /// </summary>
        Task UpdateEmbedAsync(DiscordMessageHandle handle, Embed embed, CancellationToken ct = default);

        /// <summary>
        /// Обновить embed и кнопки (например, для блоков Описание/Воля/Здоровье).
        /// </summary>
        Task UpdateEmbedAndComponentsAsync(
            DiscordMessageHandle handle,
            Embed embed,
            MessageComponent components,
            CancellationToken ct = default);

        /// <summary>
        /// Удалить сообщение (например, при тоггле блока). Бросает
        /// <see cref="MessageNotFoundException"/>, если сообщение уже удалено.
        /// </summary>
        Task DeleteAsync(DiscordMessageHandle handle, CancellationToken ct = default);
    }
}
