namespace RPBot.VtM;

/// <summary>
/// Лёгкий дескриптор сообщения для <see cref="IDiscordMessageAccessor"/>.
/// </summary>
/// <remarks>
/// <para>Нужен, чтобы сервис не тянул в себя реализацию <c>Discord.IMessage</c> —
/// это позволило бы любому тесту имитировать сообщение без 30+ свойств.</para>
/// <para>Реальный адаптер оборачивает <c>Discord.WebSocket.SocketUserMessage</c>
/// один раз при чтении; тест-fake оборачивает произвольный ulong.</para>
/// </remarks>
public readonly record struct DiscordMessageHandle(ulong ChannelId, ulong MessageId)
{
    public bool IsResolved => MessageId != 0UL;
}
