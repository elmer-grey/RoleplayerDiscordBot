using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot
{
public sealed class TelegramNotifier
{
        private readonly Func<ulong, ServerConfig?> _serverConfigAccessor;
private readonly HttpClient _httpClient;

public sealed record TelegramProbeResult(bool Success, string Message, int? TelegramMessageId = null);

public TelegramNotifier(Func<ulong, ServerConfig?> serverConfigAccessor, HttpClient? httpClient = null)
{
    _serverConfigAccessor = serverConfigAccessor ?? throw new ArgumentNullException(nameof(serverConfigAccessor));
    _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
}

public async Task<bool> SendPhotoAsync(ulong guildId, string photoUrl, string? caption = null, CancellationToken ct = default)
{
            var res = await SendPhotoReturningMessageIdAsync(guildId, photoUrl, caption, ct).ConfigureAwait(false);
    return res.HasValue;
}

public async Task<int?> SendPhotoReturningMessageIdAsync(ulong guildId, string photoUrl, string? caption = null, CancellationToken ct = default)
{
            var cfg = _serverConfigAccessor(guildId);
    if (cfg == null || !cfg.TelegramEnabled)
    return null;

    if (string.IsNullOrWhiteSpace(cfg.TelegramBotToken) || cfg.TelegramChatId == 0)
    return null;

    if (string.IsNullOrWhiteSpace(photoUrl))
    return null;

    var url = $"https://api.telegram.org/bot{cfg.TelegramBotToken}/sendPhoto";
    var payload = new Dictionary<string, object>
    {
    ["chat_id"] = cfg.TelegramChatId,
    ["photo"] = photoUrl,
    ["disable_notification"] = false
    };

    if (!string.IsNullOrWhiteSpace(caption))
    {
        payload["caption"] = EscapeHtmlPreservingTags(caption);
    payload["parse_mode"] = "HTML";
    payload["disable_web_page_preview"] = true;
    }

    if (cfg.TelegramMessageThreadId > 0)
    {
    payload["message_thread_id"] = cfg.TelegramMessageThreadId;
    }

            var sendResult = await PostJsonAsync(url, payload, ct).ConfigureAwait(false);
    return sendResult.ok ? TryParseTelegramMessageId(sendResult.body) : null;
}

public async Task<int?> SendMessageReturningMessageIdAsync(ulong guildId, string text, CancellationToken ct = default)
{
    var send = await SendMessageInternalReturningMessageIdAsync(guildId, text, ct).ConfigureAwait(false);
    return send.messageId;
}

/// <summary>
/// Публичная обёртка вокруг <see cref="SendMessageInternalReturningMessageIdAsync"/>,
/// возвращающая и messageId, и причину ошибки (если отправка не удалась).
/// Используется в EventAnnouncer, чтобы в логах различать «Telegram отключён / нет
/// chat_id» (skipped: no-config) и реальные сетевые/прочие ошибки.
/// </summary>
public async Task<(int? messageId, string? error)> SendMessageWithReasonAsync(ulong guildId, string text, CancellationToken ct = default)
{
    return await SendMessageInternalReturningMessageIdAsync(guildId, text, ct).ConfigureAwait(false);
}

public async Task<TelegramProbeResult> ProbeAsync(ulong guildId, CancellationToken ct = default)
{
    var cfg = _serverConfigAccessor(guildId);
    if (cfg == null)
    return new TelegramProbeResult(false, "ServerConfig не найден.");

    if (!cfg.TelegramEnabled)
    return new TelegramProbeResult(false, "Telegram отключён в serverconfigs.json.");

    if (string.IsNullOrWhiteSpace(cfg.TelegramBotToken))
    return new TelegramProbeResult(false, "TelegramBotToken пуст.");

    if (cfg.TelegramChatId == 0)
    return new TelegramProbeResult(false, "TelegramChatId = 0.");

    // Сначала проверим токен через getMe (без отправки сообщений в канал)
    var getMeUrl = $"https://api.telegram.org/bot{cfg.TelegramBotToken}/getMe";
    try
    {
    using var resp = await _httpClient.GetAsync(getMeUrl, ct).ConfigureAwait(false);
    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

    bool ok = false;
    long? botUserId = null;
    try
    {
    using var doc = JsonDocument.Parse(body);
    if (doc.RootElement.TryGetProperty("ok", out var okProp))
        ok = okProp.GetBoolean();

    if (ok && doc.RootElement.TryGetProperty("result", out var result))
    {
        if (result.TryGetProperty("id", out var idProp))
        botUserId = idProp.GetInt64();
    }
    }
    catch
    {
    return new TelegramProbeResult(false, "Ошибка парсинга ответа getMe.");
    }

    if (!ok)
    return new TelegramProbeResult(false, $"getMe вернул ok=false: {body}");

    if (!botUserId.HasValue)
    return new TelegramProbeResult(false, "Не удалось получить ID бота из getMe.");

    // Проверяем доступ к чату через getChat (без отправки сообщений)
    var getChatUrl = $"https://api.telegram.org/bot{cfg.TelegramBotToken}/getChat";
    var getChatPayload = new Dictionary<string, object>
    {
    ["chat_id"] = cfg.TelegramChatId
    };

    try
    {
    var getChatResult = await PostJsonAsync(getChatUrl, getChatPayload, ct).ConfigureAwait(false);
    if (!getChatResult.ok)
    {
        return new TelegramProbeResult(false, $"Не удалось получить информацию о чате: {FormatTelegramFailure(getChatResult.body)}");
    }

    // Извлекаем название чата для красивого отображения
    string? chatTitle = null;
    try
    {
        using var doc = JsonDocument.Parse(getChatResult.body);
        if (doc.RootElement.TryGetProperty("result", out var result))
        {
        if (result.TryGetProperty("title", out var titleProp))
        chatTitle = titleProp.GetString();
        }
    }
    catch { }

    var chatInfo = string.IsNullOrWhiteSpace(chatTitle) ? $"chat_id={cfg.TelegramChatId}" : $"{chatTitle} (id={cfg.TelegramChatId})";
    return new TelegramProbeResult(true, 
        $"Telegram OK: токен валиден, доступ к чату проверен ({chatInfo})", 
        null);
    }
    catch (Exception ex)
    {
    return new TelegramProbeResult(false, $"Ошибка проверки доступа к чату: {ex.Message}");
    }
    }
    catch (Exception ex)
    {
    return new TelegramProbeResult(false, $"Исключение при проверке: {ex.Message}");
    }
}

private async Task<(int? messageId, string? error)> SendMessageInternalReturningMessageIdAsync(ulong guildId, string text, CancellationToken ct = default)
{
    var cfg = _serverConfigAccessor(guildId);
    if (cfg == null || !cfg.TelegramEnabled)
    return (null, "Telegram отключён или ServerConfig не найден");

    if (string.IsNullOrWhiteSpace(cfg.TelegramBotToken) || cfg.TelegramChatId == 0)
    return (null, "TelegramBotToken пуст или TelegramChatId = 0");

    if (string.IsNullOrWhiteSpace(text))
    return (null, "пустой текст сообщения");

    var url = $"https://api.telegram.org/bot{cfg.TelegramBotToken}/sendMessage";
    var payload = BuildSendMessagePayload(cfg, text);
    var sendResult = await PostJsonAsync(url, payload, ct).ConfigureAwait(false);

    if (!sendResult.ok)
    return (null, FormatTelegramFailure(sendResult.body));

    var messageId = TryParseTelegramMessageId(sendResult.body);
    return messageId.HasValue
    ? (messageId, null)
    : (null, "Telegram вернул ok, но message_id отсутствует");
}

private static Dictionary<string, object> BuildSendMessagePayload(ServerConfig cfg, string text)
{
    // text предполагается уже собранным HTML-фрагментом с тегами <b>, <i>, <u>,
    // <s>, <a href="...">, <code>. Telegram требует экранировать &, <, > только
    // ВНУТРИ текстовых узлов — сами теги должны остаться как есть. Используем
    // EscapeHtmlPreservingTags: пробегает посимвольно и экранирует только там,
    // где не внутри тега.
    var payload = new Dictionary<string, object>
    {
    ["chat_id"] = cfg.TelegramChatId,
    ["text"] = EscapeHtmlPreservingTags(text),
    ["parse_mode"] = "HTML",
    ["disable_web_page_preview"] = true
    };

    if (cfg.TelegramMessageThreadId > 0)
    {
    payload["message_thread_id"] = cfg.TelegramMessageThreadId;
    }

    return payload;
}

private static string FormatTelegramFailure(string? body)
{
    if (string.IsNullOrWhiteSpace(body))
    return "пустой ответ";

    try
    {
    using var doc = JsonDocument.Parse(body);
    string? codePart = null;
    string? descriptionPart = null;

    if (doc.RootElement.TryGetProperty("error_code", out var errorCode))
    codePart = $"code={errorCode.GetInt32()}".Trim();

    if (doc.RootElement.TryGetProperty("description", out var description))
    descriptionPart = description.GetString()?.Trim();

    if (!string.IsNullOrWhiteSpace(codePart) && !string.IsNullOrWhiteSpace(descriptionPart))
    return $"{codePart} | {descriptionPart}";

    if (!string.IsNullOrWhiteSpace(descriptionPart))
    return descriptionPart;

    if (!string.IsNullOrWhiteSpace(codePart))
    return codePart;
    }
    catch
    {
    // leave raw body below
    }

    var singleLine = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
    return singleLine.Length <= 500 ? singleLine : singleLine.Substring(0, 499) + "…";
}

private async Task<(bool ok, string body)> PostJsonAsync(string url, Dictionary<string, object> payload, CancellationToken ct)
{
    try
    {
    var json = JsonSerializer.Serialize(payload);
    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    using var resp = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var ok = false;
    try
    {
    using var doc = JsonDocument.Parse(body);
    if (doc.RootElement.TryGetProperty("ok", out var okProp))
        ok = okProp.GetBoolean();
    }
    catch
    {
    ok = resp.IsSuccessStatusCode;
    }
    return (ok, body);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
    // Отмена снаружи — пробросить выше, чтобы вызывающий код увидел,
    // что сообщение не отправлено (без фальшивого "ok=false: ..." в логах).
    throw;
    }
    catch (HttpRequestException ex)
    {
    // Сетевые ошибки (DNS, TLS, разрыв соединения) — best-effort возврат;
    // логирование на стороне вызывающего.
    return (false, $"network error: {ex.Message}");
    }
    catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
    {
    // HttpClient.Timeout сработал (наш CancellationToken ещё не отменён).
    return (false, $"timeout: {ex.Message}");
    }
    catch (Exception ex)
    {
    return (false, ex.ToString());
    }
}

/// <summary>
/// Удаляет сообщение в Telegram-чате гильдии. Возвращает true при успехе.
/// Публичный метод — раньше был приватным, но нужен в EventOpsLifecycleService
/// для автоудаления анонсов через 24ч после EndTime / Cancelled.
/// </summary>
public async Task<bool> DeleteMessageAsync(ulong guildId, int messageId, CancellationToken ct = default)
{
    var cfg = _serverConfigAccessor(guildId);
    if (cfg == null || !cfg.TelegramEnabled)
        return false;

    if (string.IsNullOrWhiteSpace(cfg.TelegramBotToken) || cfg.TelegramChatId == 0)
        return false;

    if (messageId <= 0)
        return false;

    var url = $"https://api.telegram.org/bot{cfg.TelegramBotToken}/deleteMessage";
    var payload = new Dictionary<string, object>
    {
        ["chat_id"] = cfg.TelegramChatId,
        ["message_id"] = messageId
    };

    var (ok, _) = await PostJsonAsync(url, payload, ct).ConfigureAwait(false);
    return ok;
}

public async Task<bool> EditMessageTextAsync(ulong guildId, int messageId, string text, CancellationToken ct = default)
{
    (bool success, string? _) = await EditMessageTextWithDetailsAsync(guildId, messageId, text, ct).ConfigureAwait(false);
    return success;
}

/// <summary>
/// Вариант EditMessageTextAsync, возвращающий детальную причину ошибки
/// (для логирования: раньше fail без причины ввёл в ступор при разборе
/// инцидентов). Используется в EventAnnouncer для записи в лог.
/// </summary>
public async Task<(bool, string?)> EditMessageTextWithDetailsAsync(ulong guildId, int messageId, string text, CancellationToken ct = default)
{
    var cfg = _serverConfigAccessor(guildId);
    if (cfg == null || !cfg.TelegramEnabled)
    {
        return (false, "Telegram отключён или ServerConfig не найден");
    }

    if (string.IsNullOrWhiteSpace(cfg.TelegramBotToken) || cfg.TelegramChatId == 0)
    {
        return (false, "TelegramBotToken пуст или TelegramChatId = 0");
    }

    if (messageId <= 0 || string.IsNullOrWhiteSpace(text))
    {
        return (false, "message_id<=0 или пустой текст");
    }

    var url = $"https://api.telegram.org/bot{cfg.TelegramBotToken}/editMessageText";
    var payload = new Dictionary<string, object>
    {
        ["chat_id"] = cfg.TelegramChatId,
        ["message_id"] = messageId,
        ["text"] = EscapeHtmlPreservingTags(text),
        ["parse_mode"] = "HTML",
        ["disable_web_page_preview"] = true
    };

    if (cfg.TelegramMessageThreadId > 0)
    {
        payload["message_thread_id"] = cfg.TelegramMessageThreadId;
    }

    var send = await PostJsonAsync(url, payload, ct).ConfigureAwait(false);
    if (send.ok)
    {
        return (true, null);
    }
    return (false, FormatTelegramFailure(send.body));
}

public async Task<bool> EditMessageCaptionAsync(ulong guildId, int messageId, string caption, CancellationToken ct = default)
{
    (bool success, string? _) = await EditMessageCaptionWithDetailsAsync(guildId, messageId, caption, ct).ConfigureAwait(false);
    return success;
}

/// <summary>
/// Детальная версия EditMessageCaptionAsync — см. EditMessageTextWithDetailsAsync.
/// </summary>
public async Task<(bool ok, string? error)> EditMessageCaptionWithDetailsAsync(ulong guildId, int messageId, string caption, CancellationToken ct = default)
{
    var cfg = _serverConfigAccessor(guildId);
    if (cfg == null || !cfg.TelegramEnabled)
        return (false, "Telegram отключён или ServerConfig не найден");

    if (string.IsNullOrWhiteSpace(cfg.TelegramBotToken) || cfg.TelegramChatId == 0)
        return (false, "TelegramBotToken пуст или TelegramChatId = 0");

    if (messageId <= 0 || string.IsNullOrWhiteSpace(caption))
        return (false, "message_id<=0 или пустой caption");

    var url = $"https://api.telegram.org/bot{cfg.TelegramBotToken}/editMessageCaption";
    var payload = new Dictionary<string, object>
    {
        ["chat_id"] = cfg.TelegramChatId,
        ["message_id"] = messageId,
        ["caption"] = EscapeHtmlPreservingTags(caption),
        ["parse_mode"] = "HTML",
        ["disable_web_page_preview"] = true
    };

    if (cfg.TelegramMessageThreadId > 0)
    {
        payload["message_thread_id"] = cfg.TelegramMessageThreadId;
    }

    var send = await PostJsonAsync(url, payload, ct).ConfigureAwait(false);
    if (send.ok) return (true, null);
    return (false, FormatTelegramFailure(send.body));
}

private static int? TryParseTelegramMessageId(string json)
{
    try
    {
    using var doc = JsonDocument.Parse(json);
    if (!doc.RootElement.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
    return null;
    if (!doc.RootElement.TryGetProperty("result", out var result))
    return null;
    if (!result.TryGetProperty("message_id", out var messageIdProp))
    return null;
    return messageIdProp.GetInt32();
    }
    catch
    {
    return null;
    }
}

        public async Task<bool> SendMessageAsync(ulong guildId, string text, CancellationToken ct = default)
{
    var send = await SendMessageInternalReturningMessageIdAsync(guildId, text, ct).ConfigureAwait(false);
    return send.messageId.HasValue;
}


/// <summary>
/// Экранирует &, <, > в HTML-фрагменте, но НЕ трогает сами теги Telegram:
/// <b>, <i>, <u>, <s>, <a href="...">, <code>, <pre>. Нужен для случаев,
/// когда в текст уже подставлены пользовательские данные внутри тегов —
/// их надо экранировать, а теги — оставить. Внутри <a href="..."> значение
/// атрибута не трогается (URL должен быть сырым для Telegram).
/// </summary>
private static string EscapeHtmlPreservingTags(string html)
{
    if (string.IsNullOrEmpty(html)) return html;

    var sb = new StringBuilder(html.Length + 32);
    int i = 0;
    int n = html.Length;
    var openTags = new System.Collections.Generic.Stack<string>();

    while (i < n)
    {
        if (html[i] == '<')
        {
            int closeIdx = html.IndexOf('>', i + 1);
            if (closeIdx < 0)
            {
                AppendEscapedChar(sb, '<');
                i++;
                continue;
            }

            string tagBody = html.Substring(i, closeIdx - i + 1);
            string? tagName = ExtractTagName(tagBody);

            // Закрывающий тег </xxx>.
            bool isClose = tagName == null && tagBody.Length >= 4 && tagBody[1] == '/';
            if (isClose)
            {
                string name = tagBody.Substring(2, tagBody.Length - 3).Trim().ToLowerInvariant();
                if (IsPairedTag(name) && openTags.Count > 0 && openTags.Peek() == name)
                {
                    sb.Append(tagBody);
                    openTags.Pop();
                }
                else
                {
                    // Голый </tag> без открывающего — экранируем как текст.
                    AppendEscapedText(sb, tagBody, 0, tagBody.Length);
                }
                i = closeIdx + 1;
                continue;
            }

            if (tagName != null && IsAllowedTelegramTag(tagBody))
            {
                sb.Append(tagBody);
                i = closeIdx + 1;
                if (IsPairedTag(tagName))
                {
                    openTags.Push(tagName);
                    // Содержимое парного тега экранируем.
                    string closeTag = $"</{tagName}>";
                    int contentEnd = FindClosingTag(html, i, closeTag);
                    if (contentEnd < 0)
                    {
                        AppendEscapedText(sb, html, i, n);
                        i = n;
                    }
                    else
                    {
                        AppendEscapedText(sb, html, i, contentEnd);
                        sb.Append(closeTag);
                        openTags.Pop();
                        i = contentEnd + closeTag.Length;
                    }
                }
            }
            else
            {
                // Неизвестный «тег» — экранируем символ '<'.
                AppendEscapedChar(sb, '<');
                i++;
            }
        }
        else
        {
            int nextLt = html.IndexOf('<', i + 1);
            int end = nextLt < 0 ? n : nextLt;
            AppendEscapedText(sb, html, i, end);
            i = end;
        }
    }

    return sb.ToString();
}

private static void AppendEscapedText(StringBuilder sb, string text, int start, int end)
{
    for (int k = start; k < end; k++)
    {
        AppendEscapedChar(sb, text[k]);
    }
}

private static void AppendEscapedChar(StringBuilder sb, char c)
{
    switch (c)
    {
        case '&': sb.Append("&amp;"); break;
        case '<': sb.Append("&lt;"); break;
        case '>': sb.Append("&gt;"); break;
        case '"': sb.Append("&quot;"); break;
        default: sb.Append(c); break;
    }
}

private static string? ExtractTagName(string tag)
{
    if (tag.Length < 3 || tag[0] != '<' || tag[tag.Length - 1] != '>') return null;
    string inner = tag.Substring(1, tag.Length - 2).Trim();
    if (inner.Length > 0 && inner[0] == '/') return null;
    int spIdx = inner.IndexOf(' ');
    return (spIdx < 0 ? inner : inner.Substring(0, spIdx)).ToLowerInvariant();
}

private static bool IsPairedTag(string name) => name is "b" or "i" or "u" or "s" or "a" or "code" or "pre";

private static int FindClosingTag(string html, int from, string closeTag)
{
    return html.IndexOf(closeTag, from, StringComparison.OrdinalIgnoreCase);
}

private static bool IsAllowedTelegramTag(string tag)
{
    if (tag.Length < 3 || tag[0] != '<' || tag[tag.Length - 1] != '>') return false;
    string inner = tag.Substring(1, tag.Length - 2).Trim();

    if (inner.Length > 0 && inner[0] == '/')
    {
        string name = inner.Substring(1).Trim().ToLowerInvariant();
        return name is "b" or "i" or "u" or "s" or "a" or "code" or "pre";
    }

    int spIdx = inner.IndexOf(' ');
    string tagName = (spIdx < 0 ? inner : inner.Substring(0, spIdx)).ToLowerInvariant();
    if (tagName is not ("b" or "i" or "u" or "s" or "a" or "code" or "pre")) return false;

    if (tagName == "a")
    {
        return inner.Contains("href=\"") || inner.Contains("href='");
    }
    return true;
}
}
}
