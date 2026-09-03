using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace RPBot.VtM;

/// <summary>
/// Отвечает за рендер DM-сообщения визарда в текущем шаге.
/// </summary>
/// <remarks>
/// <para>Это тонкий слой: вся логика состояния — в
/// <see cref="VampireWizardSession"/>, вся валидация — в
/// <see cref="VampireCreateResolver"/>. Хендлер только:</para>
/// <list type="bullet">
/// <item>строит текст + кнопки по текущему шагу;</item>
/// <item>отправляет / редактирует DM-сообщение;</item>
/// <item>запоминает id DM-сообщения в сессии (для последующих правок).</item>
/// </list>
/// </remarks>
public static class VampireWizardDmHandler
{
    /// <summary>
    /// Шаг Концепция — единственный шаг, который поддерживается в Этапе 1.
    /// </summary>
    public static async Task RenderConceptStepAsync(
        IMessageChannel dmChannel,
        VampireWizardSession session)
    {
        if (dmChannel == null) throw new ArgumentNullException(nameof(dmChannel));
        if (session == null) throw new ArgumentNullException(nameof(session));

        session.Step = VampireWizardStep.Concept;
        session.DmChannelId = dmChannel.Id;

        var text = VampireCreateResolver.BuildConceptStatusMessage(session.Draft);
        var components = VampireWizardComponents.BuildForConceptStep(session.Draft);

        if (session.DmMessageId == null)
        {
            var sent = await dmChannel.SendMessageAsync(text, components: components);
            session.DmMessageId = sent.Id;
        }
        else
        {
            try
            {
                var msg = await dmChannel.GetMessageAsync(session.DmMessageId.Value);
                if (msg is IUserMessage um)
                {
                    await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
                }
                else
                {
                    // Сообщение пропало — отправим заново.
                    var sent = await dmChannel.SendMessageAsync(text, components: components);
                    session.DmMessageId = sent.Id;
                }
            }
            catch
            {
                // Сообщение недоступно — отправим заново.
                var sent = await dmChannel.SendMessageAsync(text, components: components);
                session.DmMessageId = sent.Id;
            }
        }
    }

    /// <summary>
    /// Сообщение об успешной отправке черновика на следующий шаг.
    /// </summary>
    public static string BuildAdvancedMessage() =>
        "✅ Шаг 1 (Концепция) сохранён. Шаги 2-5 появятся в следующих обновлениях.";

    /// <summary>
        /// Шаг 2 «Характеристики 7/5/3» — рендер/правка DM-сообщения.
    /// </summary>
        public static async Task RenderAttributesStepAsync(
            IMessageChannel dmChannel,
            VampireWizardSession session)
        {
            if (dmChannel == null) throw new ArgumentNullException(nameof(dmChannel));
            if (session == null) throw new ArgumentNullException(nameof(session));

            session.Step = VampireWizardStep.Attributes;
            session.DmChannelId = dmChannel.Id;

            var text = VampireAttributesResolver.BuildAttributesStatusMessage(session.Draft);
            var components = VampireWizardComponents.BuildForAttributesStep(session.Draft);

            if (session.DmMessageId == null)
            {
                var sent = await dmChannel.SendMessageAsync(text, components: components);
                session.DmMessageId = sent.Id;
            }
            else
            {
                try
                {
                    var msg = await dmChannel.GetMessageAsync(session.DmMessageId.Value);
                    if (msg is IUserMessage um)
                    {
                        await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
                        return;
                    }
                }
                catch
                {
                    // Сообщение недоступно — отправим заново.
                }

                var sent = await dmChannel.SendMessageAsync(text, components: components);
                session.DmMessageId = sent.Id;
            }
        }

        /// <summary>
        /// Сообщение об отмене визарда.
        /// </summary>
    public static string BuildCancelledMessage() =>
        "❌ Создание персонажа отменено. Все введённые данные сброшены. " +
        "Чтобы начать заново — вызовите `/vampire action:create` в канале.";
}
