using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RPBot
{
        /// <summary>
        /// Переводит технические исключения в понятные человеку сообщения.
        /// Возвращает: friendly reason (короткое) + детали (конкретный код/статус/тип).
        /// </summary>
        public static class DisconnectReasonTranslator
        {
            /// <summary>
            /// Короткое человеко-читаемое описание причины (используется в логах и UI).
            /// </summary>
            public static string GetFriendlyReason(Exception ex)
                => GetDetails(ex).Short;

            /// <summary>
            /// Полное человеко-читаемое описание: короткая причина + детали (код закрытия WS,
            /// тип исключения, вложенное исключение). Полезно, когда «WebSocket ошибка»
            /// слишком абстрактно и хочется понять, что именно произошло.
            /// </summary>
            public static string GetFullReason(Exception ex)
            {
                var d = GetDetails(ex);
                return d.Short == d.Detail ? d.Short : $"{d.Short} ({d.Detail})";
            }

            /// <summary>
            /// Детальный разбор: отдельно короткая причина и отдельно техническая деталь.
            /// </summary>
            public static (string Short, string Detail) GetDetails(Exception ex)
            {
                if (ex == null) return ("Неизвестная причина", "");

                var msg = ex.Message?.ToLowerInvariant() ?? "";
                var fullMsg = ex.ToString()?.ToLowerInvariant() ?? "";
                var detail = ExtractDetail(ex);

                // ── Discord-специфичные причины (вначале, чтобы не маскировались под WS) ──
                if (ex is ManualReconnectException)
                    return ("Ручной реконнект по команде", detail);
                if (ex is BackgroundDisconnectException)
                    return ("Фоновая проверка: клиент всё ещё отключен", detail);
                if (ex is GatewayReconnectException)
                    return ("Плановый реконнект Discord", detail);
                if (msg.Contains("rate limit"))
                    return ("Rate limit достигнут", detail);
                if (msg.Contains("authentication") || msg.Contains("401") || msg.Contains("invalid token"))
                    return ("Ошибка аутентификации", detail);

                // ── WebSocket: разбираем конкретные причины ──
                if (ex is System.Net.WebSockets.WebSocketException wse)
                {
                    // NativeErrorCode / ErrorCode часто содержат полезные подсказки
                    var wsDetail = string.IsNullOrEmpty(detail)
                        ? $"{wse.GetType().Name}: {wse.ErrorCode} (native={wse.NativeErrorCode})"
                        : detail;
                    if (msg.Contains("1006") || (int)wse.ErrorCode == (int)System.Net.WebSockets.WebSocketError.ConnectionClosedPrematurely)
                        return ("WebSocket: обрыв без кода закрытия (1006)", wsDetail);
                    if (msg.Contains("1011")) return ("WebSocket: внутренняя ошибка сервера (1011)", wsDetail);
                    if (msg.Contains("1001")) return ("WebSocket: сервер уходит (going away, 1001)", wsDetail);
                    if (msg.Contains("1012")) return ("WebSocket: перезапуск сервера (1012)", wsDetail);
                    if (msg.Contains("1013")) return ("WebSocket: попробуйте позже (try again later, 1013)", wsDetail);
                    if (msg.Contains("1014")) return ("WebSocket: bad gateway (1014)", wsDetail);
                    if (msg.Contains("1015")) return ("WebSocket: ошибка TLS (1015)", wsDetail);
                    return ("WebSocket ошибка", wsDetail);
                }

                // ── Сетевые проблемы (до WS-фоллбэка) ──
                if (msg.Contains("host unknown") || fullMsg.Contains("gateway-us"))
                    return ("DNS ошибка — хост Discord не найден", detail);
                if (msg.Contains("connection refused"))
                    return ("Соединение отклонено (возможно блокировка)", detail);
                if (msg.Contains("timed out") || msg.Contains("timeout"))
                    return ("Таймаут соединения", detail);
                if (msg.Contains("connection reset"))
                    return ("Соединение сброшено", detail);
                if (msg.Contains("aborted"))
                    return ("Соединение прервано", detail);
                if (msg.Contains("websocket") || msg.Contains("web socket"))
                    return ("WebSocket ошибка", detail);

                // ── Системные ──
                if (msg.Contains("object disposed"))
                    return ("Клиент был пересоздан", detail);
                if (msg.Contains("canceled") || msg.Contains("cancelled"))
                    return ("Операция отменена", detail);

                return ($"{ex.GetType().Name}", detail);
            }

            /// <summary>
            /// Извлекает техническую деталь из исключения: код закрытия WS, InnerException и т.д.
            /// </summary>
            private static string ExtractDetail(Exception ex)
            {
                var parts = new List<string>();

                // Внутреннее исключение часто несёт реальную причину (например, SocketException)
                if (ex.InnerException != null)
                {
                    var inner = ex.InnerException;
                    var innerType = inner.GetType().Name;
                    var innerMsg = inner.Message;
                    if (!string.IsNullOrEmpty(innerMsg))
                        parts.Add($"{innerType}: {innerMsg}");
                    else
                        parts.Add(innerType);
                }

                // Попробуем вытащить Discord-специфичный код, если есть
                var discordCodeProp = ex.GetType().GetProperty("DiscordCode");
                if (discordCodeProp != null)
                {
                    var code = discordCodeProp.GetValue(ex);
                    if (code != null) parts.Add($"code={code}");
                }

                // WebSocket: дополнительно ErrorCode / NativeErrorCode
                if (ex is System.Net.WebSockets.WebSocketException wse)
                {
                    if (parts.Count == 0)
                    {
                        // Если inner пустой, вытащим коды
                        parts.Add($"ErrorCode={wse.ErrorCode}");
                        if (wse.NativeErrorCode != 0) parts.Add($"native={wse.NativeErrorCode}");
                    }
                }

                return parts.Count == 0 ? "" : string.Join("; ", parts);
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
