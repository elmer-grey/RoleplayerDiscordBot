using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>
    /// Разрешение slash-команд бота во временных voice-комнатах, созданных командой /voice.
    ///
    /// Проблема: на серверах с per-channel разграничением команд интеграции (Server Settings →
    /// Integrations → включено только в избранных каналах) созданная ботом комната не входит
    /// в whitelist — slash-команды бота в ней не видны.
    ///
    /// Решение: Discord Application Command Permissions API — добавляем channel-override
    /// (type=3, permission=true) для выбранного списка команд (ServerConfig.VoiceRoomCommandNames).
    ///
    /// Discord.NET 3.20.1 не предоставляет публичных обёрток для этого REST-эндпоинта
    /// (методы есть только на internal DiscordRestApiClient), поэтому используется прямой HttpClient.
    ///
    /// Эндпоинты:
    ///   GET  /applications/{appId}/guilds/{gid}/commands/permissions       — все overrides гильдии
    ///   PUT  /applications/{appId}/guilds/{gid}/commands/{cid}/permissions — ЗАМЕНЯЕТ весь список
    ///        overrides одной команды → обязательно слить с существующими перед отправкой.
    ///   Batch-эндпоинт /guilds/{gid}/application-command-permissions — deprecated, не используется.
    ///
    /// Discord сам чистит channel-override'ы удалённых каналов — ручной уборки не требуется.
    /// Требуется пермишен бота ManageGuild; при 403 пишем лог с подсказкой.
    /// </summary>
    public static class VoiceRoomCommandPermissions
    {
        private const int TargetChannel = 3; // ApplicationCommandPermissionTarget в REST: Role=1, User/Member=2, Channel=3
        private const string ApiBase = "https://discord.com/api/v10";

        private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1); // сериализация GET→PUT (защита от гонки параллельных комнат)
        private static HttpClient? _http;

        #region DTO (снежинки — строки)

        public sealed class PermissionEntryDto
        {
            [JsonPropertyName("id")]
            [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
            public ulong TargetId { get; set; }

            [JsonPropertyName("type")]
            public int TargetType { get; set; }

            [JsonPropertyName("permission")]
            public bool Permission { get; set; }
        }

        public sealed class CommandPermissionsDto
        {
            [JsonPropertyName("id")]
            [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
            public ulong CommandId { get; set; }

            [JsonPropertyName("permissions")]
            public List<PermissionEntryDto> Permissions { get; set; } = new List<PermissionEntryDto>();
        }

        public sealed class ModifyCommandPermissionsDto
        {
            [JsonPropertyName("permissions")]
            public List<PermissionEntryDto> Permissions { get; set; } = new List<PermissionEntryDto>();
        }

        #endregion

        /// <summary>
        /// Включает slash-команды из commandNames в указанной voice-комнате (channel-override type=3/true).
        /// Вызов неблокирующий по смыслу — предназначен для Task.Run; все исключения проглатываются с логом.
        /// </summary>
        public static async Task EnableBotCommandsInChannelAsync(SocketGuild guild, ulong voiceChannelId, IEnumerable<string>? commandNames)
        {
            if (guild == null || voiceChannelId == 0) return;

            var names = (commandNames ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim().TrimStart('/').ToLowerInvariant())
                .Where(n => n.Length > 0)
                .Distinct()
                .ToHashSet();

            if (names.Count == 0)
            {
                BotLogger.Info(LogCategory.Discord, $"[VoiceCmdPerms] Список команд пуст (voice_room_commands=clear) — комната {voiceChannelId} пропущена.");
                return;
            }

            var token = GetBotToken();
            if (string.IsNullOrEmpty(token))
            {
                BotLogger.Warn(LogCategory.Discord, $"[VoiceCmdPerms] Токен бота недоступен (env/config) — команды в комнату {voiceChannelId} не включены.");
                return;
            }

            var http = GetHttpClient(token);

            // SocketGuild не раскрывает клиента в Discord.NET 3.x — application id берём из REST.
            var appId = await GetApplicationIdAsync(http).ConfigureAwait(false);
            if (appId == 0)
            {
                BotLogger.Warn(LogCategory.Discord, $"[VoiceCmdPerms] Не определён application id — команды в комнату {voiceChannelId} не включены.");
                return;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // 1. Mapping имя→id по guild-командам приложения.
                IReadOnlyCollection<IApplicationCommand> appCommands;
                try
                {
                    appCommands = await guild.GetApplicationCommandsAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    BotLogger.Error(LogCategory.Discord, $"[VoiceCmdPerms] GET commands гильдии {guild.Id}: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                var targets = appCommands
                    .Where(c => names.Contains((c.Name ?? string.Empty).ToLowerInvariant()))
                    .ToList();
                if (targets.Count == 0)
                {
                    BotLogger.Info(LogCategory.Discord, $"[VoiceCmdPerms] Команды [{string.Join(", ", names)}] не найдены среди guild-команд приложения — комната {voiceChannelId} пропущена.");
                    return;
                }

                // 2. Текущие overrides гильдии (404 = overrides нет вообще — считаем пустым набором).
                var current = new Dictionary<ulong, List<PermissionEntryDto>>();
                try
                {
                    var permsUrl = $"{ApiBase}/applications/{appId}/guilds/{guild.Id}/commands/permissions";
                    using var resp = await http.GetAsync(permsUrl).ConfigureAwait(false);
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Нет ни одного override — команды включены везде, комнате тоже видны.
                        BotLogger.Info(LogCategory.Discord, $"[VoiceCmdPerms] Overrides гильдии {guild.Id} отсутствуют — команды и так видны в комнате {voiceChannelId}.");
                        return;
                    }
                    if (!resp.IsSuccessStatusCode)
                    {
                        BotLogger.Error(LogCategory.Discord, $"[VoiceCmdPerms] GET permissions гильдии {guild.Id}: HTTP {(int)resp.StatusCode} — команды в комнату {voiceChannelId} не включены{((int)resp.StatusCode == 403 ? " (нужен пермишен ManageGuild)" : "")}.");
                        return;
                    }

                    await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    var list = await JsonSerializer.DeserializeAsync<List<CommandPermissionsDto>>(stream, JsonOpts).ConfigureAwait(false);
                    if (list != null)
                    {
                        foreach (var cp in list)
                            current[cp.CommandId] = cp.Permissions ?? new List<PermissionEntryDto>();
                    }
                }
                catch (Exception ex)
                {
                    BotLogger.Error(LogCategory.Discord, $"[VoiceCmdPerms] GET permissions гильдии {guild.Id}: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                // 3. Merge + PUT только по командам, где реально добавили/включили channel-override.
                var enabled = 0;
                foreach (var cmd in targets)
                {
                    var perms = current.TryGetValue(cmd.Id, out var existing) && existing != null
                        ? new List<PermissionEntryDto>(existing)
                        : new List<PermissionEntryDto>();

                    if (!EnsureChannelAllow(perms, voiceChannelId))
                        continue; // уже разрешено

                    var body = JsonSerializer.Serialize(new ModifyCommandPermissionsDto { Permissions = perms }, JsonOpts);
                    var putUrl = $"{ApiBase}/applications/{appId}/guilds/{guild.Id}/commands/{cmd.Id}/permissions";

                    try
                    {
                        if (!await PutPermissionsAsync(http, putUrl, body).ConfigureAwait(false)) continue;
                        enabled++;
                        BotLogger.Info(LogCategory.Discord, $"[VoiceCmdPerms] Команда /{cmd.Name} разрешена в комнате {voiceChannelId} (гильдия {guild.Id}).");
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Error(LogCategory.Discord, $"[VoiceCmdPerms] PUT команды /{cmd.Name} (id={cmd.Id}) гильдия {guild.Id}: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (enabled > 0)
                    BotLogger.Info(LogCategory.Discord, $"[VoiceCmdPerms] Включено команд в комнате {voiceChannelId}: {enabled} из {targets.Count}.");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Merge-логика: гарантировать (channelId, Channel, true) в списке пермишенов.
        /// Возвращает true, если список изменён (нужен PUT), false — разрешение уже есть.
        /// Существующие записи (роли/юзеры/другие каналы) не теряются.
        /// </summary>
        public static bool EnsureChannelAllow(List<PermissionEntryDto> permissions, ulong channelId)
        {
            if (permissions == null) throw new ArgumentNullException(nameof(permissions));

            var existing = permissions.FirstOrDefault(p => p != null && p.TargetType == TargetChannel && p.TargetId == channelId);
            if (existing != null)
            {
                if (existing.Permission) return false;
                existing.Permission = true;
                return true;
            }

            permissions.Add(new PermissionEntryDto { TargetId = channelId, TargetType = TargetChannel, Permission = true });
            return true;
        }

        /// <summary>Application id бота через GET /applications/@me (снежинка — строка). 0 при ошибке.</summary>
        private static async Task<ulong> GetApplicationIdAsync(HttpClient http)
        {
            try
            {
                using var resp = await http.GetAsync($"{ApiBase}/applications/@me").ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    BotLogger.Error(LogCategory.Discord, $"[VoiceCmdPerms] GET /applications/@me: HTTP {(int)resp.StatusCode}.");
                    return 0;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
                var idStr = doc.RootElement.GetProperty("id").GetString();
                return ulong.TryParse(idStr, out var id) ? id : 0;
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Discord, $"[VoiceCmdPerms] GET /applications/@me: {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>PUT с одним ретраем по 429 (Retry-After). Возвращает true при успехе; по финальной ошибке — лог и false.</summary>
        private static async Task<bool> PutPermissionsAsync(HttpClient http, string url, string jsonBody)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(500).ConfigureAwait(false); // короткая пауза перед ретраем

                using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                using var resp = await http.PutAsync(url, content).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode) return true;

                if ((int)resp.StatusCode == 429)
                {
                    var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    BotLogger.Warn(LogCategory.Discord, $"[VoiceCmdPerms] PUT {url}: HTTP 429, ретрай через {retryAfter.TotalSeconds:F1} c.");
                    await Task.Delay(retryAfter).ConfigureAwait(false);
                    continue;
                }

                BotLogger.Error(LogCategory.Discord, $"[VoiceCmdPerms] PUT {url}: HTTP {(int)resp.StatusCode}{((int)resp.StatusCode == 403 ? " (нужен пермишен ManageGuild)" : "")}.");
                return false;
            }

            BotLogger.Error(LogCategory.Discord, $"[VoiceCmdPerms] PUT {url}: HTTP 429 после ретрая.");
            return false;
        }

        private static HttpClient GetHttpClient(string token)
        {
            if (_http == null)
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // Authorization обновляем на каждый вызов: токен мог смениться между запусками комнат.
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
            return _http;
        }

        /// <summary>env DISCORD_BOT_TOKEN → Settings/config.json (как в Program.GetBotToken).</summary>
        private static string? GetBotToken()
        {
            var fromEnv = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
            if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

            try
            {
                var cfg = BotConfig.Load(System.IO.Path.Combine("Settings", "config.json"));
                return cfg?.BotToken;
            }
            catch (Exception ex)
            {
                BotLogger.Warn(LogCategory.Discord, $"[VoiceCmdPerms] Не удалось прочитать Settings/config.json: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }
}