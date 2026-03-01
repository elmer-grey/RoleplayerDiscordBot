using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>
    /// Переводит технические исключения в понятные человеку сообщения
    /// </summary>
    public static class DisconnectReasonTranslator
    {
        public static string GetFriendlyReason(Exception ex)
        {
            if (ex == null) return "Неизвестная причина";

            var msg = ex.Message?.ToLower() ?? "";
            var fullMsg = ex.ToString()?.ToLower() ?? "";

            // Сетевые проблемы
            if (msg.Contains("host unknown") || fullMsg.Contains("gateway-us"))
                return "DNS ошибка - хост Discord не найден";
            if (msg.Contains("connection refused"))
                return "Соединение отклонено (возможно блокировка)";
            if (msg.Contains("timed out"))
                return "Таймаут соединения";
            if (msg.Contains("reset"))
                return "Соединение сброшено";
            if (msg.Contains("aborted"))
                return "Соединение прервано";
            if (msg.Contains("websocket") || msg.Contains("web socket"))
                return " WebSocket ошибка";

            // Discord специфичные
            if (ex is GatewayReconnectException)
                return "Плановый реконнект Discord";
            if (msg.Contains("rate limit"))
                return "Rate limit достигнут";
            if (msg.Contains("authentication"))
                return "Ошибка аутентификации";

            // Системные
            if (msg.Contains("object disposed"))
                return "Клиент был пересоздан";
            if (msg.Contains("canceled"))
                return "Операция отменена";

            return $" {ex.GetType().Name}";
        }

        public static string GetEmojiForReason(string reason)
        {
            if (reason.Contains("DNS")) return "🌐";
            if (reason.Contains("Таймаут")) return "⏱️";
            if (reason.Contains("WebSocket")) return "🔌";
            if (reason.Contains("Плановый")) return "🔄";
            if (reason.Contains("Rate")) return "⚠️";
            if (reason.Contains("Аутентификация")) return "🔑";
            return "❌";
        }
    }
}
