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
				payload["caption"] = EscapeHtml(caption);
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
			var payload = new Dictionary<string, object>
			{
				["chat_id"] = cfg.TelegramChatId,
				["text"] = EscapeHtml(text),
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
			catch (Exception ex)
			{
             return (false, ex.ToString());
			}
		}

		private async Task<bool> DeleteMessageAsync(ulong guildId, int messageId, CancellationToken ct = default)
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
			var cfg = _serverConfigAccessor(guildId);
			if (cfg == null || !cfg.TelegramEnabled)
				return false;

			if (string.IsNullOrWhiteSpace(cfg.TelegramBotToken) || cfg.TelegramChatId == 0)
				return false;

			if (messageId <= 0 || string.IsNullOrWhiteSpace(text))
				return false;

			var url = $"https://api.telegram.org/bot{cfg.TelegramBotToken}/editMessageText";
			var payload = new Dictionary<string, object>
			{
				["chat_id"] = cfg.TelegramChatId,
				["message_id"] = messageId,
				["text"] = EscapeHtml(text),
				["parse_mode"] = "HTML",
				["disable_web_page_preview"] = true
			};

			if (cfg.TelegramMessageThreadId > 0)
			{
				payload["message_thread_id"] = cfg.TelegramMessageThreadId;
			}

			var json = JsonSerializer.Serialize(payload);
			using var content = new StringContent(json, Encoding.UTF8, "application/json");
			try
			{
				using var resp = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
				return resp.IsSuccessStatusCode;
			}
			catch
			{
				return false;
			}
		}

		public async Task<bool> EditMessageCaptionAsync(ulong guildId, int messageId, string caption, CancellationToken ct = default)
		{
			var cfg = _serverConfigAccessor(guildId);
			if (cfg == null || !cfg.TelegramEnabled)
				return false;

			if (string.IsNullOrWhiteSpace(cfg.TelegramBotToken) || cfg.TelegramChatId == 0)
				return false;

			if (messageId <= 0 || string.IsNullOrWhiteSpace(caption))
				return false;

			var url = $"https://api.telegram.org/bot{cfg.TelegramBotToken}/editMessageCaption";
			var payload = new Dictionary<string, object>
			{
				["chat_id"] = cfg.TelegramChatId,
				["message_id"] = messageId,
				["caption"] = EscapeHtml(caption),
				["parse_mode"] = "HTML",
				["disable_web_page_preview"] = true
			};

			if (cfg.TelegramMessageThreadId > 0)
			{
				payload["message_thread_id"] = cfg.TelegramMessageThreadId;
			}

			var json = JsonSerializer.Serialize(payload);
			using var content = new StringContent(json, Encoding.UTF8, "application/json");
			try
			{
				using var resp = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
				return resp.IsSuccessStatusCode;
			}
			catch
			{
				return false;
			}
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

		private static string EscapeHtml(string text)
		{
			return text
				.Replace("&", "&amp;", StringComparison.Ordinal)
				.Replace("<", "&lt;", StringComparison.Ordinal)
				.Replace(">", "&gt;", StringComparison.Ordinal);
		}
	}
}
