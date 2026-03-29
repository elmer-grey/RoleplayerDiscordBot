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
			_httpClient = httpClient ?? new HttpClient();
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
           var cfg = _serverConfigAccessor(guildId);
			if (cfg == null || !cfg.TelegramEnabled)
				return null;

			if (string.IsNullOrWhiteSpace(cfg.TelegramBotToken) || cfg.TelegramChatId == 0)
				return null;

			if (string.IsNullOrWhiteSpace(text))
				return null;

			var url = $"https://api.telegram.org/bot{cfg.TelegramBotToken}/sendMessage";
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

           var sendResult = await PostJsonAsync(url, payload, ct).ConfigureAwait(false);
			return sendResult.ok ? TryParseTelegramMessageId(sendResult.body) : null;
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

			var meUrl = $"https://api.telegram.org/bot{cfg.TelegramBotToken}/getMe";
			var (meOk, meBody) = await PostJsonAsync(meUrl, new Dictionary<string, object>(), ct).ConfigureAwait(false);
			if (!meOk)
				return new TelegramProbeResult(false, $"getMe failed: {meBody}");

			var probeText = $"🔎 Telegram startup check: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
			var sentId = await SendMessageReturningMessageIdAsync(guildId, probeText, ct).ConfigureAwait(false);
			if (!sentId.HasValue)
				return new TelegramProbeResult(false, "sendMessage failed (message_id не получен).");

			var deleteOk = await DeleteMessageAsync(guildId, sentId.Value, ct).ConfigureAwait(false);
			return new TelegramProbeResult(deleteOk, deleteOk
				? $"Telegram OK: getMe успешен, test message_id={sentId.Value} отправлен и удалён."
				: $"Telegram message_id={sentId.Value} отправлен, но удалить не удалось.", sentId);
		}

		private async Task<(bool ok, string body)> PostJsonAsync(string url, Dictionary<string, object> payload, CancellationToken ct)
		{
			try
			{
				var json = JsonSerializer.Serialize(payload);
				using var content = new StringContent(json, Encoding.UTF8, "application/json");
				using var resp = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
				var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
				return (resp.IsSuccessStatusCode && body.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase), body);
			}
			catch (Exception ex)
			{
				return (false, ex.Message);
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
            var cfg = _serverConfigAccessor(guildId);
			if (cfg == null || !cfg.TelegramEnabled)
				return false;

			if (string.IsNullOrWhiteSpace(cfg.TelegramBotToken) || cfg.TelegramChatId == 0)
				return false;

			if (string.IsNullOrWhiteSpace(text))
				return false;

			var url = $"https://api.telegram.org/bot{cfg.TelegramBotToken}/sendMessage";

			// Используем HTML parse_mode, т.к. он менее капризный, чем MarkdownV2.
			// Текст ожидается уже как plain text; экранируем спецсимволы.
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

		private static string EscapeHtml(string text)
		{
			return text
				.Replace("&", "&amp;", StringComparison.Ordinal)
				.Replace("<", "&lt;", StringComparison.Ordinal)
				.Replace(">", "&gt;", StringComparison.Ordinal);
		}
	}
}
