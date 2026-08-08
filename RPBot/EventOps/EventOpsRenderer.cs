using System;

namespace RPBot.EventOps
{
    public sealed class EventOpsRenderer
    {
        public EventOpsRenderResult Render(EventOpsContext context)
        {
            var eventName = context.CurrentEvent.Name ?? "(без названия)";
            var status = context.StatusText ?? OperationToStatus(context.Operation);

            return new EventOpsRenderResult
            {
                DiscordTitle = $"{status}: {eventName}",
                TelegramTitle = $"{status}: {eventName}",
                ShortSummary = $"guild={context.CurrentEvent.Guild?.Id} event={context.CurrentEvent.Id} op={context.Operation}",
            };
        }

        private static string OperationToStatus(EventOperationKind operation)
        {
            return operation switch
            {
                EventOperationKind.Created => "📅 Событие создано",
                EventOperationKind.Updated => "✏️ Событие обновлено",
                EventOperationKind.Started => "▶️ Событие началось",
                EventOperationKind.Cancelled => "❌ Событие отменено",
                EventOperationKind.Completed => "✅ Событие завершено",
                _ => "ℹ️ Событие",
            };
        }
    }
}
