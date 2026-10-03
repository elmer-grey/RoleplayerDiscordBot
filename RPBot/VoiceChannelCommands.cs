using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.Common;

namespace RPBot;

/// <summary>
/// Slash-команда <c>/voice</c> и обработка кнопок выбора лимита.
/// Доступно только пользователям с мастер-ролью (<see cref="ServerConfig.MasterRoleId"/>)
/// на сервере (или с правами администратора — на случай, если роль ещё не назначена).
/// После выбора лимита создаётся временный голосовой канал в той же категории,
/// где была вызвана команда. Канал автоматически удаляется через 5 минут
/// полного отсутствия пользователей; при заходе кого-либо таймер сбрасывается.
/// </summary>
public class VoiceChannelCommands
{
    /// <summary>Минимальный лимит человек для временной комнаты.</summary>
    public const int MinLimit = 2;

    /// <summary>Максимальный лимит человек для временной комнаты.</summary>
    public const int MaxLimit = 7;

    /// <summary>Задержка удаления пустой комнаты.</summary>
    public static readonly TimeSpan EmptyRoomTtl = TimeSpan.FromMinutes(5);

    /// <summary>Интервал опроса пустых комнат.</summary>
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Префикс custom_id для кнопок выбора лимита: <c>voice_limit:N</c>.</summary>
    public const string ButtonPrefix = "voice_limit:";

    private static readonly ConcurrentDictionary<ulong, RoomTracker> _activeRooms = new();
    private static CancellationTokenSource? _monitorCts;
    private static readonly object _monitorLock = new();
    private static Task? _monitorLoop;

    /// <summary>
    /// Ссылка на Discord-клиент для доступа к кэшу гильдий (подсчёт пользователей в голосовых).
    /// Устанавливается из Program.cs после создания клиента.
    /// </summary>
    private static DiscordSocketClient? _client;

    /// <summary>
    /// Устанавливает (или сбрасывает) ссылку на Discord-клиент. Вызывать из Program.cs.
    /// </summary>
    public static void SetClient(DiscordSocketClient? client) => _client = client;

    /// <summary>
    /// Останавливает polling-таск и очищает трекер. Вызывать из Program.cs при полном
    /// рестарте клиента (DisposeClientSafely → новый _client), чтобы старый монитор
    /// не тикал на disposed HttpClient старого клиента.
    /// </summary>
    public static void ResetForNewClient()
    {
        lock (_monitorLock)
        {
            try { _monitorCts?.Cancel(); } catch { }
            try { _monitorCts?.Dispose(); } catch { }
            _monitorCts = null;
            _monitorLoop = null;
            _activeRooms.Clear();
        }
        _client = null;
    }

    /// <summary>
    /// Обработчик команды <c>/voice</c>. Отвечает ephemeral-сообщением с кнопками 2..7.
    /// </summary>
    public async Task VoiceAsync(SocketSlashCommand command)
    {
        try { await command.DeferAsync(ephemeral: true).ConfigureAwait(false); }
        catch (Exception ex)
        {
            DeferFailureLogger.Log("Voice", ex, command, input: null);
            return;
        }

        var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
        if (guildId == null)
        {
            await command.FollowupAsync("Команда доступна только на сервере.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var guildUser = command.User as SocketGuildUser;
        var config = Program.ServerConfigResolver?.Invoke(guildId.Value);
        if (!IsMasterOrAdmin(guildUser, config?.MasterRoleId))
        {
            await command.FollowupAsync(
                "Команда доступна только мастерам сервера (или администраторам).",
                ephemeral: true).ConfigureAwait(false);
            return;
        }

        var builder = new ComponentBuilder();
        for (var i = MinLimit; i <= MaxLimit; i++)
        {
            builder.WithButton(
                i.ToString(),
                $"{ButtonPrefix}{i}",
                ButtonStyle.Primary,
                row: (i - MinLimit) / 4);
        }

        await command.FollowupAsync(
            "Выбери по одной из кнопок ниже, на скольких человек рассчитан канал. Больше этого числа пользователей не будет.",
            components: builder.Build(),
            ephemeral: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Обработчик нажатия кнопки <c>voice_limit:N</c>. Создаёт голосовой канал,
    /// удаляет ephemeral-сообщение с кнопками и регистрирует канал в трекере.
    /// </summary>
    public async Task HandleLimitButtonAsync(SocketMessageComponent component)
    {
        BotLogger.Info(LogCategory.Discord, $"[VoiceButton] customId={component.Data.CustomId} user={component.User.Id}");

        // Сразу ACK'аем взаимодействие, иначе Discord покажет «Приложение не отвечает»
        // через 3 секунды. После DeferAsync можно отвечать Followup'ом и удалять сообщение.
        try { await component.DeferAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            DeferFailureLogger.Log("VoiceButton", ex, component, customId: component.Data.CustomId);
            return;
        }

        await Task.Yield();

        var customId = component.Data.CustomId;
        if (!TryParseLimit(customId, out var limit))
        {
            await component.FollowupAsync("Неизвестная кнопка.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var guildId = (component.Channel as SocketGuildChannel)?.Guild.Id;
        if (guildId == null)
        {
            await component.FollowupAsync("Команда доступна только на сервере.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var guildUser = component.User as SocketGuildUser;
        var config = Program.ServerConfigResolver?.Invoke(guildId.Value);
        if (!IsMasterOrAdmin(guildUser, config?.MasterRoleId))
        {
            await component.FollowupAsync("Недостаточно прав.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var sourceChannel = component.Channel as SocketGuildChannel;
        var guild = sourceChannel!.Guild;
        var nested = sourceChannel as INestedChannel;
        var category = nested?.CategoryId != null
            ? guild.GetCategoryChannel(nested.CategoryId.Value)
            : null;

        // Имя комнаты: «Тайная вечеря — 5 (Domen_)»
        var userLabel = guildUser?.GlobalName ?? guildUser?.Username ?? "unknown";
        var roomName = $"Тайная вечеря — {limit} ({userLabel})";

        IGuildChannel? created = null;
        try
        {
            created = await guild.CreateVoiceChannelAsync(roomName, props =>
            {
                props.UserLimit = limit;
                if (category != null)
                    props.CategoryId = category.Id;

                // Копируем права категории, если она есть — иначе берём дефолтные права гильдии.
                if (category != null)
                {
                    props.PermissionOverwrites = new Optional<IEnumerable<Overwrite>>(category.PermissionOverwrites);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLogger.Error(LogCategory.Discord, $"[Voice] Не удалось создать канал: {ex.GetType().Name}: {ex.Message}");
            try
            {
                await component.FollowupAsync("Не удалось создать голосовой канал. Подробности в логах.", ephemeral: true).ConfigureAwait(false);
            }
            catch { }
            return;
        }

        // CreateVoiceChannelAsync возвращает RestVoiceChannel (не SocketVoiceChannel) в Discord.NET 3.x.
        // Нам нужны только Id/Name/DeleteAsync — берём IVoiceChannel.
        if (created is not IVoiceChannel voice)
        {
            try
            {
                if (created != null) await created.DeleteAsync().ConfigureAwait(false);
            }
            catch { /* best effort */ }

            BotLogger.Error(LogCategory.Discord, $"[Voice] Неожиданный тип созданного канала: {created?.GetType().FullName ?? "null"}");
            await component.FollowupAsync("Не удалось создать голосовой канал.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        // Регистрируем канал в трекере и запускаем polling, если ещё не запущен.
        var tracker = new RoomTracker(voice, guildId.Value, limit);
        _activeRooms[voice.Id] = tracker;
        EnsureMonitorRunning();
        PersistRooms();

        BotLogger.Info(LogCategory.Discord, $"[Voice] Создан канал {voice.Name} ({voice.Id}) лимит={limit}, мастер={userLabel}.");

        // Удаляем сообщение с кнопками (требование пользователя) и подтверждаем взаимодействие.
        // DeferAsync выше сделал ACK. Сначала Followup — чтобы пользователь увидел результат
        // даже если удаление сообщения упадёт. Потом пытаемся удалить.
        try
        {
            await component.FollowupAsync(
                $"Готово: создан «{voice.Name}».",
                ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLogger.Warn(LogCategory.Discord, $"[Voice] Не удалось отправить Followup: {ex.GetType().Name}: {ex.Message}");
        }

        // Удаление через прямой запрос к каналу: на некоторых версиях Discord.NET
        // component.Message указывает на уже-disposed сообщение после Followup.
        try
        {
            if (component.Channel is IMessageChannel ch)
                await ch.DeleteMessageAsync(component.Message.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLogger.Warn(LogCategory.Discord, $"[Voice] Не удалось удалить сообщение с кнопками: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Парсит custom_id вида <c>voice_limit:N</c> и возвращает N, иначе <c>false</c>.
    /// </summary>
    public static bool TryParseLimit(string customId, out int limit)
    {
        limit = 0;
        if (string.IsNullOrEmpty(customId)) return false;
        if (!customId.StartsWith(ButtonPrefix, StringComparison.Ordinal)) return false;
        return int.TryParse(customId.Substring(ButtonPrefix.Length), out limit)
               && limit >= MinLimit && limit <= MaxLimit;
    }

    private static bool IsMasterOrAdmin(SocketGuildUser? user, ulong? masterRoleId)
    {
        if (user == null) return false;
        if (user.GuildPermissions.Administrator) return true;
        if (!masterRoleId.HasValue || masterRoleId.Value == 0) return false;
        return user.Roles.Any(r => r.Id == masterRoleId.Value);
    }

    private static void EnsureMonitorRunning()
    {
        lock (_monitorLock)
        {
            if (_monitorLoop != null && !_monitorLoop.IsCompleted)
                return;

            _monitorCts?.Dispose();
            _monitorCts = new CancellationTokenSource();
            var token = _monitorCts.Token;
            _monitorLoop = Task.Run(() => MonitorLoopAsync(token), token);
        }
    }

    private static async Task MonitorLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (_activeRooms.IsEmpty)
                continue;

            var now = DateTime.UtcNow;
            var toDelete = new List<ulong>();
            var stateChanged = false;

            foreach (var kv in _activeRooms)
            {
                var tracker = kv.Value;
                IVoiceChannel? channel = null;
                try { channel = tracker.Channel; }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.Discord, $"[Voice] Трекер для канала {kv.Key}: ошибка доступа: {ex.Message}");
                    toDelete.Add(kv.Key);
                    continue;
                }

                if (channel == null)
                {
                    toDelete.Add(kv.Key);
                    continue;
                }

                int users;
                try
                {
                    // Считаем пользователей в канале через кэш гильдии.
                    // Работает и для RestVoiceChannel (созданных через CreateVoiceChannelAsync),
                    // у которых нет свойства ConnectedUsers.
                    var client = _client;
                    if (client == null)
                    {
                        BotLogger.Warn(LogCategory.Discord, $"[Voice] _client == null в MonitorLoopAsync, гильдия {tracker.GuildId}, канал {tracker.Channel.Id}.");
                        continue;
                    }
                    SocketGuild? guild = client.GetGuild(tracker.GuildId);
                    if (guild == null)
                    {
                        // Гильдия ещё не загружена в кэш (или бот не на этом сервере).
                        // SocketGuild нужен для доступа к guild.Users с VoiceChannel.
                        BotLogger.Info(LogCategory.Discord, $"[Voice] Канал {tracker.Channel.Name}: гильдия {tracker.GuildId} недоступна в кэше, пропускаю. client.Guilds={client.Guilds.Count}");
                        continue;
                    }

                    users = guild.Users
                        .Count(u => u.VoiceChannel?.Id == tracker.Channel.Id);
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.Discord, $"[Voice] Не удалось получить список участников {channel.Name}: {ex.Message}");
                    continue;
                }

                if (users > 0)
                {
                    if (tracker.EmptySinceUtc != null)
                    {
                        tracker.EmptySinceUtc = null;
                        stateChanged = true;
                        BotLogger.Info(LogCategory.Discord, $"[Voice] Канал {tracker.Channel.Name} занят, таймер остановлен.");
                    }
                }
                else if (users == 0)
                {
                    if (tracker.EmptySinceUtc == null)
                    {
                        tracker.EmptySinceUtc = now;
                        stateChanged = true;
                        BotLogger.Info(LogCategory.Discord, $"[Voice] Канал {tracker.Channel.Name} стал пуст, таймер запущен (TTL {EmptyRoomTtl.TotalMinutes:F0} мин).");
                    }
                    else if (now - tracker.EmptySinceUtc.Value >= EmptyRoomTtl)
                        toDelete.Add(kv.Key);
                }
                // users == -1: не знаем, не трогаем
            }

            foreach (var channelId in toDelete)
            {
                if (!_activeRooms.TryGetValue(channelId, out var tracker))
                    continue;

                bool deleted = false;
                try
                {
                    await tracker.Channel.DeleteAsync().ConfigureAwait(false);
                    deleted = true;
                    BotLogger.Info(LogCategory.Discord, $"[Voice] Канал {tracker.Channel.Name} удалён (пуст > {EmptyRoomTtl.TotalMinutes:F0} мин).");
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.Discord, $"[Voice] Не удалось удалить канал {tracker.Channel.Name}: {ex.GetType().Name}: {ex.Message}");
                }

                // Убираем из трекера только после успешного удаления. Иначе — оставляем,
                // чтобы следующий тик попробовал снова. Иначе канал-сирота навсегда останется
                // на сервере, а JSON перезапишется без него.
                if (deleted)
                    _activeRooms.TryRemove(channelId, out _);
                else
                    stateChanged = true; // на следующий тик снова попробуем
            }

            // Сохраняем JSON при любом изменении состояния или удалении.
            if (stateChanged || toDelete.Count > 0)
                PersistRooms();
        }
    }

    /// <summary>Трекер одной временной комнаты.</summary>
    private sealed class RoomTracker
    {
        public RoomTracker(IVoiceChannel channel, ulong guildId, int userLimit)
        {
            Channel = channel;
            GuildId = guildId;
            UserLimit = userLimit;
        }

        public IVoiceChannel Channel { get; }
        public ulong GuildId { get; }
        public int UserLimit { get; }
        public DateTime? EmptySinceUtc { get; set; }
    }

    // ───────────────────────── Persistence ─────────────────────────

    private sealed class PersistedRoom
    {
        [JsonPropertyName("channel_id")]
        public ulong ChannelId { get; set; }

        [JsonPropertyName("guild_id")]
        public ulong GuildId { get; set; }

        [JsonPropertyName("user_limit")]
        public int UserLimit { get; set; }

        [JsonPropertyName("created_at_utc")]
        public DateTime CreatedAtUtc { get; set; }

        [JsonPropertyName("empty_since_utc")]
        public DateTime? EmptySinceUtc { get; set; }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Путь к файлу с сохранёнными временными комнатами.
    /// Берётся из <see cref="BotConfig.GetDataRootDirectory"/> + <c>Data\voice_rooms.json</c>.
    /// </summary>
    private static string GetPersistencePath()
    {
        var dataRoot = BotConfig.GetDataRootDirectory();
        var dataDir = Path.Combine(dataRoot, "Data");
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, "voice_rooms.json");
    }

    /// <summary>
    /// Записывает текущий список отслеживаемых комнат на диск.
    /// </summary>
    private static void PersistRooms()
    {
        try
        {
            var path = GetPersistencePath();
            var rooms = _activeRooms.Values
                .Select(t => new PersistedRoom
                {
                    ChannelId = t.Channel.Id,
                    GuildId = t.GuildId,
                    UserLimit = t.UserLimit,
                    CreatedAtUtc = t.Channel.CreatedAt.UtcDateTime,
                    EmptySinceUtc = t.EmptySinceUtc,
                })
                .ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(rooms, _jsonOpts));
        }
        catch (Exception ex)
        {
            BotLogger.Warn(LogCategory.Discord, $"[Voice] Не удалось сохранить список комнат: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Загружает сохранённые комнаты в трекер. Должен вызываться после подключения
    /// к Discord-шлюзу (когда доступны guild/channels). Запускает polling, если есть
    /// валидные комнаты.
    /// </summary>
    /// <param name="client">Активный Discord-клиент.</param>
    public static async Task LoadPersistedAsync(DiscordSocketClient client)
    {
        var path = GetPersistencePath();
        if (!File.Exists(path))
        {
            BotLogger.Info(LogCategory.Discord, $"[Voice] Persistence: {Path.GetFileName(path)} не найден, нечего загружать.");
            return;
        }

        List<PersistedRoom>? rooms = null;
        try
        {
            var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            rooms = JsonSerializer.Deserialize<List<PersistedRoom>>(text, _jsonOpts);
        }
        catch (Exception ex)
        {
            BotLogger.Warn(LogCategory.Discord, $"[Voice] Не удалось прочитать {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (rooms == null || rooms.Count == 0)
        {
            // Пустой список — почистим файл.
            try { File.Delete(path); } catch { /* ignore */ }
            return;
        }

        var valid = new List<RoomTracker>();
        var stale = new List<ulong>();

        foreach (var r in rooms)
        {
            var guild = client.GetGuild(r.GuildId);
            if (guild == null)
            {
                BotLogger.Info(LogCategory.Discord, $"[Voice] Гильдия {r.GuildId} недоступна, канал {r.ChannelId} удалён из трекера.");
                stale.Add(r.ChannelId);
                continue;
            }

            var ch = guild.GetVoiceChannel(r.ChannelId);
            if (ch == null)
            {
                BotLogger.Info(LogCategory.Discord, $"[Voice] Канал {r.ChannelId} удалён снаружи, убираю из трекера.");
                stale.Add(r.ChannelId);
                continue;
            }

            var tracker = new RoomTracker(ch, r.GuildId, r.UserLimit);

            // Проверяем текущее состояние канала: занят или пуст?
            int users;
            try
            {
                users = ch is SocketVoiceChannel socket ? socket.ConnectedUsers.Count : -1;
            }
            catch
            {
                users = -1;
            }

            if (users > 0)
            {
                tracker.EmptySinceUtc = null; // занят — таймер не тикает
            }
            else if (r.EmptySinceUtc.HasValue)
            {
                // Канал был пуст ещё до рестарта — продолжаем таймер с того же момента.
                tracker.EmptySinceUtc = r.EmptySinceUtc.Value;
            }
            else
            {
                // Канал пуст, но в файле не было отметки — запускаем таймер заново.
                tracker.EmptySinceUtc = DateTime.UtcNow;
            }

            _activeRooms[ch.Id] = tracker;
            valid.Add(tracker);
            var emptySinceStr = tracker.EmptySinceUtc.HasValue
                ? tracker.EmptySinceUtc.Value.ToString("HH:mm:ss")
                : "null";
            BotLogger.Info(LogCategory.Discord, $"[Voice] Загружен канал {ch.Name} ({ch.Id}) лимит={r.UserLimit}, users={users}, empty_since_utc={emptySinceStr}.");
        }

        if (valid.Count > 0)
        {
            EnsureMonitorRunning();
            PersistRooms(); // перезапишем файл с отфильтрованным списком
        }
        else
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
