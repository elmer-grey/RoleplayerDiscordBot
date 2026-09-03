using System;

namespace RPBot.VtM
{
    /// <summary>
    /// Сигнал, что Discord-сообщение не существует (404) и регистрацию
    /// нужно снять из индекса.
    /// </summary>
    /// <remarks>
    /// <para>Реальный адаптер вокруг <c>DiscordSocketClient</c> ловит
    /// <c>Discord.HttpException</c> с кодом 404 и транслирует его в этот тип —
    /// так сервис синхронизации остаётся чистым, без зависимости от
    /// WebSocket/Rest-сборок Discord.NET.</para>
    /// </remarks>
    public sealed class MessageNotFoundException : Exception
    {
        public ulong ChannelId { get; }
        public ulong MessageId { get; }

        public MessageNotFoundException(ulong channelId, ulong messageId)
            : base($"Discord message {messageId} not found in channel {channelId}.")
        {
            ChannelId = channelId;
            MessageId = messageId;
        }
    }
}
