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
                        /// Отобразить Шаг 3 «Способности 13/9/5» в DM.
                        /// Перерисовывает то же сообщение визарда (или создаёт новое, если
                        /// <see cref="VampireWizardSession.DmMessageId"/> ещё не задан).
                        /// </summary>
                        public static async Task RenderAbilitiesStepAsync(
                            IMessageChannel dmChannel,
                            VampireWizardSession session)
                        {
                            if (dmChannel == null) throw new ArgumentNullException(nameof(dmChannel));
                            if (session == null) throw new ArgumentNullException(nameof(session));

                            session.Step = VampireWizardStep.Abilities;
                            session.DmChannelId = dmChannel.Id;

                            var text = VampireAbilitiesResolver.BuildAbilitiesStatusMessage(session.Draft);
                            var components = VampireWizardComponents.BuildForAbilitiesStep(session.Draft);

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
        "Чтобы начать заново — вызовите `/vampire_create` в канале.";

                        /// <summary>
                        /// Сводка draft, показываемая при нажатии «Отмена».
                        /// Содержит то, что игрок уже ввёл (concept, clan, generation, атрибуты
                        /// и т. п.), чтобы он мог убедиться, что отменяет не случайно.
                        /// </summary>
                        public static EmbedBuilder BuildCancelSummary(VampireCharacter c)
                        {
                            if (c == null) throw new ArgumentNullException(nameof(c));
                            var eb = new EmbedBuilder
                            {
                                Title = "⚠️ Вы уверены, что хотите отменить визард?",
                                Description = "Ниже — всё, что вы уже ввели. " +
                                    "Если нажмёте «❌ Подтвердить отмену», **эти данные будут удалены безвозвратно**. " +
                                    "Если передумали — нажмите «✅ Продолжить визард».",
                                Color = new Color(0xE6, 0x7E, 0x22), // оранжевый — предупреждение
                            };
                            // Имя.
                            eb.AddField(new EmbedFieldBuilder
                            {
                                Name = "Имя",
                                Value = string.IsNullOrWhiteSpace(c.CharacterName) ? "_(не задано)_" : c.CharacterName,
                                IsInline = true,
                            });
                            // Клан.
                            eb.AddField(new EmbedFieldBuilder
                            {
                                Name = "Клан",
                                Value = string.IsNullOrWhiteSpace(c.Clan) ? "_(не задан)_" : c.Clan,
                                IsInline = true,
                            });
                            // Поколение.
                            eb.AddField(new EmbedFieldBuilder
                            {
                                Name = "Поколение",
                                Value = c.Generation > 0 ? c.Generation.ToString() : "_(по умолчанию)_",
                                IsInline = true,
                            });
                            // Амплуа / натура / маска.
                            eb.AddField(new EmbedFieldBuilder
                            {
                                Name = "Амплуа",
                                Value = string.IsNullOrWhiteSpace(c.Concept) ? "_(не задано)_" : c.Concept,
                                IsInline = true,
                            });
                            eb.AddField(new EmbedFieldBuilder
                            {
                                Name = "Натура",
                                Value = string.IsNullOrWhiteSpace(c.Nature) ? "_(не задана)_" : c.Nature,
                                IsInline = true,
                            });
                            eb.AddField(new EmbedFieldBuilder
                            {
                                Name = "Маска",
                                Value = string.IsNullOrWhiteSpace(c.Demeanor) ? "_(не задана)_" : c.Demeanor,
                                IsInline = true,
                            });
                            // Приоритеты.
                            var prio = string.IsNullOrWhiteSpace(c.AttributesPriority) ? "_(не задан)_" : c.AttributesPriority;
                            var abPrio = string.IsNullOrWhiteSpace(c.AbilitiesPriority) ? "_(не задан)_" : c.AbilitiesPriority;
                            eb.AddField(new EmbedFieldBuilder
                            {
                                Name = "Приоритет характеристик (7/5/3)",
                                Value = prio,
                                IsInline = true,
                            });
                            eb.AddField(new EmbedFieldBuilder
                            {
                                Name = "Приоритет способностей (13/9/5)",
                                Value = abPrio,
                                IsInline = true,
                            });
                            // Очки/свободные.
                            int bgCount = (c.Backgrounds?.Count ?? 0) + (c.FreebieBackgrounds?.Count ?? 0);
                            int discCount = (c.Disciplines?.Count ?? 0) + (c.FreebieDisciplines?.Count ?? 0);
                            eb.AddField(new EmbedFieldBuilder
                            {
                                Name = "Дисциплины / Факты",
                                Value = $"Дисциплин: {discCount} · Фактов: {bgCount}",
                                IsInline = true,
                            });
                            eb.AddField(new EmbedFieldBuilder
                            {
                                Name = "Голод",
                                Value = c.Hunger > 0 ? c.Hunger.ToString() : "_(не задан)_",
                                IsInline = true,
                            });
                            return eb;
                        }

        /// <summary>
        /// Шаг 4.1 «Дисциплины» — рендер/правка DM-сообщения.
        /// </summary>
        public static async Task RenderDisciplinesStepAsync(
            IMessageChannel dmChannel,
            VampireWizardSession session)
        {
            if (dmChannel == null) throw new ArgumentNullException(nameof(dmChannel));
            if (session == null) throw new ArgumentNullException(nameof(session));

            session.Step = VampireWizardStep.Advantages;
            session.AdvantagesSubStep = VampireWizardAdvantagesSubStep.Disciplines;
            session.DmChannelId = dmChannel.Id;

            var text = VampireAdvantagesResolver.BuildDisciplinesStatusMessage(session.Draft);
            var components = VampireWizardComponents.BuildForDisciplinesStep(session.Draft);
            await RenderIntoAsync(dmChannel, session, text, components);
        }

        /// <summary>
        /// Шаг 4.2 «Факты биографии» — рендер/правка DM-сообщения.
        /// </summary>
        public static async Task RenderBackgroundsStepAsync(
            IMessageChannel dmChannel,
            VampireWizardSession session)
        {
            if (dmChannel == null) throw new ArgumentNullException(nameof(dmChannel));
            if (session == null) throw new ArgumentNullException(nameof(session));

            session.Step = VampireWizardStep.Advantages;
            session.AdvantagesSubStep = VampireWizardAdvantagesSubStep.Backgrounds;
            session.DmChannelId = dmChannel.Id;

            var text = VampireAdvantagesResolver.BuildBackgroundsStatusMessage(session.Draft);
            var components = VampireWizardComponents.BuildForBackgroundsStep(session.Draft);
            await RenderIntoAsync(dmChannel, session, text, components);
        }

        /// <summary>
        /// Шаг 4.3 «Добродетели» — рендер/правка DM-сообщения.
        /// </summary>
        public static async Task RenderVirtuesStepAsync(
            IMessageChannel dmChannel,
            VampireWizardSession session)
        {
            if (dmChannel == null) throw new ArgumentNullException(nameof(dmChannel));
            if (session == null) throw new ArgumentNullException(nameof(session));

            session.Step = VampireWizardStep.Advantages;
            session.AdvantagesSubStep = VampireWizardAdvantagesSubStep.Virtues;
            session.DmChannelId = dmChannel.Id;

            var text = VampireAdvantagesResolver.BuildVirtuesStatusMessage(session.Draft);
            var components = VampireWizardComponents.BuildForVirtuesStep(session.Draft);
            await RenderIntoAsync(dmChannel, session, text, components);
        }

        /// <summary>
        /// Шаг 5 «Последние штрихи» — рендер/правка DM-сообщения.
        /// Пул freebie = 15, цены по V20 стр. 86 (хар 5 / спос 2 / диск 7 / факт 1 / доброд 2).
        /// Чел и Воля рассчитываются автоматически из добродетелей — не редактируются свободно.
        /// </summary>
        public static async Task RenderFinishingStepAsync(
            IMessageChannel dmChannel,
            VampireWizardSession session)
        {
            if (dmChannel == null) throw new ArgumentNullException(nameof(dmChannel));
            if (session == null) throw new ArgumentNullException(nameof(session));

            session.Step = VampireWizardStep.FinishingTouches;
            // Шаг 5 не относится к Advantages: оставляем AdvantagesSubStep без изменений.
            session.DmChannelId = dmChannel.Id;

            var text = VampireFinishingResolver.BuildStatusMessage(session.Draft);
            var components = VampireWizardComponents.BuildForFinishingStep(session.Draft);
            await RenderIntoAsync(dmChannel, session, text, components);
        }

        /// <summary>
        /// Универсальный helper: отправить или отредактировать DM-сообщение.
        /// </summary>
        private static async Task RenderIntoAsync(
            IMessageChannel dmChannel,
            VampireWizardSession session,
            string text,
            MessageComponent components)
        {
            if (session.DmMessageId == null)
            {
                var sent = await dmChannel.SendMessageAsync(text, components: components);
                session.DmMessageId = sent.Id;
                return;
            }

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

            var sentNew = await dmChannel.SendMessageAsync(text, components: components);
            session.DmMessageId = sentNew.Id;
        }
}
