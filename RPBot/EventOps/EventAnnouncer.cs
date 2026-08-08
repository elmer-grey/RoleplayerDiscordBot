using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace RPBot.EventOps
{
    /// <summary>
    /// Логика анонсов scheduled-ивенов (создание/обновление/старт/отмена/завершение).
    /// Раньше жила прямо в Program — здесь изолирована и принимает зависимости через конструктор.
    /// </summary>
    public sealed class EventAnnouncer
    {
        private readonly DiscordSocketClient _client;
        private readonly Func<IReadOnlyDictionary<ulong, ServerConfig>> _serverConfigsProvider;
        private readonly TelegramNotifier? _telegramNotifier;
        private readonly EventAnnouncementStore? _store;
        private readonly EventNotificationService _eventNotifications;

        public EventAnnouncer(
            DiscordSocketClient client,
            Func<IReadOnlyDictionary<ulong, ServerConfig>> serverConfigsProvider,
            TelegramNotifier? telegramNotifier,
            EventAnnouncementStore? store,
            EventNotificationService eventNotifications)
        {
            _client = client;
            _serverConfigsProvider = serverConfigsProvider;
            _telegramNotifier = telegramNotifier;
            _store = store;
            _eventNotifications = eventNotifications;
        }

        public Task AnnounceCreatedAsync(SocketGuildEvent guildEvent)
            => AnnounceCreatedInternalAsync(guildEvent);

        public Task AnnounceUpdatedAsync(Cacheable<SocketGuildEvent, ulong> beforeCache, SocketGuildEvent guildEvent)
            => AnnounceUpdatedInternalAsync(beforeCache, guildEvent);

        public Task AnnounceStatusChangedAsync(SocketGuildEvent guildEvent, string status)
            => AnnounceStatusChangedInternalAsync(guildEvent, status);

        public async Task AnnounceCreatedInternalAsync(SocketGuildEvent guildEvent)
        {
            if (guildEvent?.Guild == null) return;

            var guild = guildEvent.Guild;
            var serverConfigs = _serverConfigsProvider();
            if (serverConfigs == null || !serverConfigs.TryGetValue(guild.Id, out var config)) return;
            if (config.GeneralRGChannelID == 0) return;

            var announceChannel = await _client.GetChannelAsync(config.GeneralRGChannelID) as ITextChannel;
            if (announceChannel == null) return;

            var eventUrl = $"https://discord.com/events/{guild.Id}/{guildEvent.Id}";
            var startLocal = guildEvent.StartTime.ToLocalTime();
            var startMsk = TryGetMoscowTime(guildEvent.StartTime.UtcDateTime, out var mskStartTime)
                ? mskStartTime
                : startLocal.DateTime;

            string whereText;
            string whereTextPlain;
            if (guildEvent.Channel != null)
            {
                whereText = $"<#{guildEvent.Channel.Id}>";
                whereTextPlain = guildEvent.Channel.Name;
            }
            else if (!string.IsNullOrWhiteSpace(guildEvent.Location))
            {
                whereText = Truncate(guildEvent.Location, 256);
                whereTextPlain = whereText;
            }
            else
            {
                whereText = "не указано";
                whereTextPlain = whereText;
            }

            var embedBuilder = new EmbedBuilder()
                .WithTitle($"📅 Новое событие: {guildEvent.Name}")
                .WithUrl(eventUrl)
                .WithColor(Color.Blue)
                .WithCurrentTimestamp();

            var imageUrl = guildEvent.GetCoverImageUrl();
            if (!string.IsNullOrWhiteSpace(imageUrl))
                embedBuilder.WithThumbnailUrl(imageUrl);

            if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                embedBuilder.WithDescription(Truncate(guildEvent.Description, 2048));

            embedBuilder.AddField("🏰 Сервер", guild.Name, true);
            embedBuilder.AddField("🕒 Когда", DiscordTimeFormatter.FullDateTime(startLocal), true);
            embedBuilder.AddField("📍 Где", whereText, true);

            string? createdByPlain = null;
            if (guildEvent.Creator != null)
            {
                embedBuilder.AddField("👤 Создал", MentionUtils.MentionUser(guildEvent.Creator.Id), true);
                if (serverConfigs.TryGetValue(guild.Id, out var scCr)
                    && scCr.MasterNameMap?.TryGetValue(guildEvent.Creator.Id.ToString(), out var mappedCr) == true
                    && !string.IsNullOrWhiteSpace(mappedCr))
                    createdByPlain = mappedCr;
                else
                    createdByPlain = guildEvent.Creator.Username;
            }

            embedBuilder.WithFooter("Чтобы приходило в личку: /event_notify subscribe • Выкл: напиши «стоп» • Вкл: «хочу»");
            var embed = embedBuilder.Build();

            var announceMsg = await announceChannel.SendMessageAsync(embed: embed);
            Log($"[EVENT] announce sent discord_channel guild={guild.Id} event={guildEvent.Id} channel={announceChannel.Id} msg={announceMsg.Id}");

            int? tgMessageId = null;
            var tgHasPhoto = false;
            try
            {
                if (_telegramNotifier != null)
                {
                    var tgText = $"📅 Новое событие: {guildEvent.Name}\n" +
                        $"🏰 Сервер: {guild.Name}\n" +
                        $"🕒 Когда: {startMsk:dd.MM.yyyy HH:mm} (по МСК)\n" +
                        $"📍 Где: {whereTextPlain}\n" +
                        (createdByPlain != null ? $"👤 Создал: {createdByPlain}\n" : string.Empty) +
                        $"🔗 {eventUrl}";

                    if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                    {
                        var desc = guildEvent.Description.Trim();
                        if (desc.Length > 800) desc = desc.Substring(0, 799) + "…";
                        tgText += $"\n\nОписание события:\n{desc}";
                    }

                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        tgMessageId = await _telegramNotifier.SendPhotoReturningMessageIdAsync(guild.Id, imageUrl, tgText);
                        tgHasPhoto = tgMessageId.HasValue;
                    }
                    else
                    {
                        tgMessageId = await _telegramNotifier.SendMessageReturningMessageIdAsync(guild.Id, tgText);
                    }
                    Log($"[EVENT] announce sent telegram guild={guild.Id} event={guildEvent.Id} msg={(tgMessageId.HasValue ? tgMessageId.Value : 0)} hasPhoto={tgHasPhoto}");
                }
            }
            catch (Exception ex)
            {
                Log($"[EVENT] announce telegram error guild={guild.Id} event={guildEvent.Id}: {ex.Message}");
                await LogError($"[EVENT] announce telegram error guild={guild.Id} event={guildEvent.Id}: {ex}");
            }

            var subscriberIds = _eventNotifications.GetActiveSubscribers(guild.Id);
            var dmMap = new Dictionary<ulong, ulong>();
            foreach (var userId in subscriberIds)
            {
                try
                {
                    var user = guild.GetUser(userId) as IUser ?? _client.GetUser(userId);
                    if (user == null)
                    {
                        try { user = await _client.Rest.GetUserAsync(userId); } catch { }
                    }
                    if (user == null) continue;

                    var dm = await user.CreateDMChannelAsync();
                    var dmMsg = await dm.SendMessageAsync(embed: embed);
                    dmMap[userId] = dmMsg.Id;
                    Log($"[EVENT] announce sent discord_dm guild={guild.Id} event={guildEvent.Id} user={userId} msg={dmMsg.Id}");
                }
                catch (Exception ex)
                {
                    Log($"[EVENT] announce discord_dm error guild={guild.Id} event={guildEvent.Id} user={userId}: {ex.Message}");
                    try { await LogError($"Не удалось отправить DM о событии пользователю {userId} на сервере {guild.Id}: {ex.Message}"); } catch { }
                }
            }

            if (_store != null)
            {
                var entry = _store.TryGet(guild.Id, guildEvent.Id) ?? new EventAnnouncementEntry
                {
                    GuildId = guild.Id,
                    EventId = guildEvent.Id
                };
                entry.AnnounceChannelId = announceChannel.Id;
                entry.AnnounceMessageId = announceMsg.Id;
                entry.DmMessageIdsByUserId = dmMap;
                if (serverConfigs.TryGetValue(guild.Id, out var sc2))
                {
                    entry.TelegramChatId = sc2.TelegramChatId;
                    entry.TelegramMessageThreadId = sc2.TelegramMessageThreadId;
                }
                if (tgMessageId.HasValue)
                {
                    entry.TelegramMessageId = tgMessageId.Value;
                    entry.TelegramHasPhoto = tgHasPhoto;
                }
                _store.Upsert(entry);
            }
        }

        public async Task AnnounceUpdatedInternalAsync(Cacheable<SocketGuildEvent, ulong> beforeCache, SocketGuildEvent guildEvent)
        {
            if (guildEvent?.Guild == null) return;
            if (_store == null) return;

            SocketGuildEvent? before = null;
            try { before = await beforeCache.GetOrDownloadAsync(); } catch { }

            var guild = guildEvent.Guild;
            var entry = _store.TryGet(guild.Id, guildEvent.Id);
            if (entry == null) return;

            var serverConfigs = _serverConfigsProvider();

            var changes = new List<string>();
            var changesDiscord = new List<string>();
            try
            {
                if (before != null)
                {
                    if (!string.Equals(before.Name, guildEvent.Name, StringComparison.Ordinal))
                    {
                        var text = $"Название: '{before.Name}' → '{guildEvent.Name}'";
                        changes.Add(text);
                        changesDiscord.Add(text);
                    }
                    if (!string.Equals(before.Description ?? string.Empty, guildEvent.Description ?? string.Empty, StringComparison.Ordinal))
                    {
                        changes.Add("Описание изменено");
                        changesDiscord.Add("Описание изменено");
                    }
                    if (before.StartTime != guildEvent.StartTime)
                    {
                        changes.Add($"Начало: {before.StartTime.ToLocalTime():dd.MM.yyyy HH:mm} → {guildEvent.StartTime.ToLocalTime():dd.MM.yyyy HH:mm}");
                        changesDiscord.Add($"Начало: {DiscordTimeFormatter.FullDateTime(before.StartTime.ToLocalTime())} → {DiscordTimeFormatter.FullDateTime(guildEvent.StartTime.ToLocalTime())}");
                    }
                    if (before.EndTime != guildEvent.EndTime)
                    {
                        var bEnd = before.EndTime?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "—";
                        var aEnd = guildEvent.EndTime?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "—";
                        changes.Add($"Окончание: {bEnd} → {aEnd}");

                        var bEndDiscord = before.EndTime.HasValue
                            ? DiscordTimeFormatter.FullDateTime(before.EndTime.Value.ToLocalTime())
                            : "—";
                        var aEndDiscord = guildEvent.EndTime.HasValue
                            ? DiscordTimeFormatter.FullDateTime(guildEvent.EndTime.Value.ToLocalTime())
                            : "—";
                        changesDiscord.Add($"Окончание: {bEndDiscord} → {aEndDiscord}");
                    }
                    if ((before.Channel?.Id ?? 0) != (guildEvent.Channel?.Id ?? 0) ||
                        !string.Equals(before.Location ?? string.Empty, guildEvent.Location ?? string.Empty, StringComparison.Ordinal))
                    {
                        changes.Add("Место проведения изменено");
                        changesDiscord.Add("Место проведения изменено");
                    }
                    if (!string.Equals(before.GetCoverImageUrl() ?? string.Empty, guildEvent.GetCoverImageUrl() ?? string.Empty, StringComparison.Ordinal))
                    {
                        changes.Add("Изображение изменено");
                        changesDiscord.Add("Изображение изменено");
                    }
                }
            }
            catch { }

            var updatedMark = $"Обновлено: {DateTime.Now:dd.MM.yyyy HH:mm}";
            var updatedMarkDiscord = $"Обновлено: {DiscordTimeFormatter.FullDateTime(DateTime.Now)}";
            var eventUrl = $"https://discord.com/events/{guild.Id}/{guildEvent.Id}";
            var startLocal = guildEvent.StartTime.ToLocalTime();
            var startMsk = TryGetMoscowTime(guildEvent.StartTime.UtcDateTime, out var mskStartTime)
                ? mskStartTime
                : startLocal.DateTime;

            string whereText;
            string whereTextPlain;
            if (guildEvent.Channel != null)
            {
                whereText = $"<#{guildEvent.Channel.Id}>";
                whereTextPlain = guildEvent.Channel.Name;
            }
            else if (!string.IsNullOrWhiteSpace(guildEvent.Location))
            {
                whereText = Truncate(guildEvent.Location, 256);
                whereTextPlain = whereText;
            }
            else
            {
                whereText = "не указано";
                whereTextPlain = whereText;
            }

            var imageUrl = guildEvent.GetCoverImageUrl();
            var embedBuilder = new EmbedBuilder()
                .WithTitle($"📅 Событие обновлено: {guildEvent.Name}")
                .WithUrl(eventUrl)
                .WithColor(Color.Orange)
                .WithCurrentTimestamp();
            if (!string.IsNullOrWhiteSpace(imageUrl))
                embedBuilder.WithThumbnailUrl(imageUrl);
            if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                embedBuilder.WithDescription(Truncate(guildEvent.Description, 2048));
            embedBuilder.AddField("🏰 Сервер", guild.Name, true);
            embedBuilder.AddField("🕒 Когда", DiscordTimeFormatter.FullDateTime(startLocal), true);
            embedBuilder.AddField("📍 Где", whereText, true);
            if (guildEvent.Creator != null)
                embedBuilder.AddField("👤 Создал", MentionUtils.MentionUser(guildEvent.Creator.Id), true);
            if (changesDiscord.Count > 0)
                embedBuilder.AddField("✏️ Изменения", string.Join("\n", changesDiscord.Take(10)), false);
            embedBuilder.AddField("Статус", updatedMarkDiscord, false);
            var embed = embedBuilder.Build();

            try
            {
                if (entry.AnnounceChannelId != 0 && entry.AnnounceMessageId != 0)
                {
                    var ch = await _client.GetChannelAsync(entry.AnnounceChannelId) as ITextChannel;
                    if (ch != null)
                    {
                        var msg = await ch.GetMessageAsync(entry.AnnounceMessageId) as IUserMessage;
                        if (msg != null)
                        {
                            await msg.ModifyAsync(m => m.Embed = embed);
                            Log($"[EVENT] update discord_channel ok guild={guild.Id} event={guildEvent.Id} channel={entry.AnnounceChannelId} msg={entry.AnnounceMessageId}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[EVENT] update discord_channel error guild={guild.Id} event={guildEvent.Id} msg={entry.AnnounceMessageId}: {ex.Message}");
            }

            var subscriberIds = _eventNotifications.GetActiveSubscribers(guild.Id);
            foreach (var userId in subscriberIds)
            {
                try
                {
                    if (!entry.DmMessageIdsByUserId.TryGetValue(userId, out var dmMessageId) || dmMessageId == 0)
                        continue;
                    var user = guild.GetUser(userId) as IUser ?? _client.GetUser(userId);
                    if (user == null)
                    {
                        try { user = await _client.Rest.GetUserAsync(userId); } catch { }
                    }
                    if (user == null) continue;
                    var dm = await user.CreateDMChannelAsync();
                    var dmMsg = await dm.GetMessageAsync(dmMessageId) as IUserMessage;
                    if (dmMsg != null)
                    {
                        await dmMsg.ModifyAsync(m => m.Embed = embed);
                        Log($"[EVENT] update discord_dm ok guild={guild.Id} event={guildEvent.Id} user={userId} msg={dmMessageId}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[EVENT] update discord_dm error guild={guild.Id} event={guildEvent.Id} user={userId}: {ex.Message}");
                    try { await LogError($"[EVENT] update discord_dm error guild={guild.Id} event={guildEvent.Id} user={userId}: {ex}"); } catch { }
                }
            }

            try
            {
                if (_telegramNotifier != null && entry.TelegramMessageId > 0)
                {
                    var tgText = $"📅 Событие обновлено: {guildEvent.Name}\n" +
                        $"🏰 Сервер: {guild.Name}\n" +
                        $"🕒 Когда: {startMsk:dd.MM.yyyy HH:mm} (по МСК)\n" +
                        $"📍 Где: {whereTextPlain}\n" +
                        (guildEvent.Creator != null ? $"👤 Создал: {guildEvent.Creator.Username}\n" : string.Empty) +
                        (changes.Count > 0 ? $"\n✏️ Изменения:\n- {string.Join("\n- ", changes.Take(10))}\n" : string.Empty) +
                        $"ℹ️ {updatedMark}\n" +
                        $"🔗 {eventUrl}";

                    if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                    {
                        var desc = guildEvent.Description.Trim();
                        if (desc.Length > 800) desc = desc.Substring(0, 799) + "…";
                        tgText += $"\n\nОписание события:\n{desc}";
                    }

                    var ok = entry.TelegramHasPhoto
                        ? await _telegramNotifier.EditMessageCaptionAsync(guild.Id, entry.TelegramMessageId, tgText)
                        : await _telegramNotifier.EditMessageTextAsync(guild.Id, entry.TelegramMessageId, tgText);

                    Log($"[EVENT] update telegram {(ok ? "ok" : "fail")} guild={guild.Id} event={guildEvent.Id} msg={entry.TelegramMessageId} hasPhoto={entry.TelegramHasPhoto}");
                }
            }
            catch (Exception ex)
            {
                Log($"[EVENT] update telegram error guild={guild.Id} event={guildEvent.Id} msg={entry.TelegramMessageId}: {ex.Message}");
                try { await LogError($"[EVENT] update telegram error guild={guild.Id} event={guildEvent.Id} msg={entry.TelegramMessageId}: {ex}"); } catch { }
            }
        }

        public async Task AnnounceStatusChangedInternalAsync(SocketGuildEvent guildEvent, string status)
        {
            if (guildEvent?.Guild == null) return;
            if (_store == null) return;

            var guild = guildEvent.Guild;
            var entry = _store.TryGet(guild.Id, guildEvent.Id);
            if (entry == null) return;

            var serverConfigs = _serverConfigsProvider();

            var eventUrl = $"https://discord.com/events/{guild.Id}/{guildEvent.Id}";
            var startLocal = guildEvent.StartTime.ToLocalTime();
            var startMsk = TryGetMoscowTime(guildEvent.StartTime.UtcDateTime, out var mskStartTime)
                ? mskStartTime
                : startLocal.DateTime;
            var imageUrl = guildEvent.GetCoverImageUrl();

            var isCancelled = string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);
            var isStarted = string.Equals(status, "started", StringComparison.OrdinalIgnoreCase);
            var isCompleted = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);
            var prefix = isCancelled ? "❌" : isStarted ? "▶️" : isCompleted ? "✅" : "ℹ️";
            var statusText = isCancelled ? "Событие отменено/удалено" : isStarted ? "Событие началось" : isCompleted ? "Событие завершено" : "Событие обновлено";
            var mark = $"{prefix} {statusText}: {DateTime.Now:dd.MM.yyyy HH:mm}";
            var markDiscord = $"{prefix} {statusText}: {DiscordTimeFormatter.FullDateTime(DateTime.Now)}";

            string whereText;
            string whereTextPlain;
            if (guildEvent.Channel != null)
            {
                whereText = $"<#{guildEvent.Channel.Id}>";
                whereTextPlain = guildEvent.Channel.Name;
            }
            else if (!string.IsNullOrWhiteSpace(guildEvent.Location))
            {
                whereText = guildEvent.Location;
                whereTextPlain = whereText;
            }
            else
            {
                whereText = "не указано";
                whereTextPlain = whereText;
            }

            var embedBuilder = new EmbedBuilder()
                .WithTitle($"{prefix} {statusText}: {guildEvent.Name}")
                .WithUrl(eventUrl)
                .WithColor(isCancelled ? Color.DarkRed : isStarted ? Color.Green : isCompleted ? Color.DarkGreen : Color.Orange)
                .WithCurrentTimestamp();
            if (!string.IsNullOrWhiteSpace(imageUrl))
                embedBuilder.WithThumbnailUrl(imageUrl);
            if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                embedBuilder.WithDescription(guildEvent.Description.Length <= 2048 ? guildEvent.Description : guildEvent.Description.Substring(0, 2047) + "…");
            embedBuilder.AddField("🏰 Сервер", guild.Name, true);
            embedBuilder.AddField("🕒 Когда", DiscordTimeFormatter.FullDateTime(startLocal), true);
            embedBuilder.AddField("📍 Где", whereText, true);
            if (guildEvent.Creator != null)
                embedBuilder.AddField("👤 Создал", MentionUtils.MentionUser(guildEvent.Creator.Id), true);
            embedBuilder.AddField("Статус", markDiscord, false);
            var embed = embedBuilder.Build();

            try
            {
                var ch = await _client.GetChannelAsync(entry.AnnounceChannelId) as ITextChannel;
                var msg = ch != null ? await ch.GetMessageAsync(entry.AnnounceMessageId) as IUserMessage : null;
                if (msg != null)
                {
                    await msg.ModifyAsync(m => m.Embed = embed);
                    Log($"[EVENT] status {status} discord_channel ok guild={guild.Id} event={guildEvent.Id} msg={entry.AnnounceMessageId}");
                }
            }
            catch (Exception ex)
            {
                Log($"[EVENT] status {status} discord_channel error guild={guild.Id} event={guildEvent.Id}: {ex.Message}");
            }

            var subscriberIds = _eventNotifications.GetActiveSubscribers(guild.Id);
            foreach (var userId in subscriberIds)
            {
                try
                {
                    if (!entry.DmMessageIdsByUserId.TryGetValue(userId, out var dmMessageId) || dmMessageId == 0)
                        continue;
                    var user = guild.GetUser(userId) as IUser ?? _client.GetUser(userId);
                    if (user == null) continue;
                    var dm = await user.CreateDMChannelAsync();
                    var dmMsg = await dm.GetMessageAsync(dmMessageId) as IUserMessage;
                    if (dmMsg != null)
                    {
                        await dmMsg.ModifyAsync(m => m.Embed = embed);
                        Log($"[EVENT] status {status} discord_dm ok guild={guild.Id} event={guildEvent.Id} user={userId} msg={dmMessageId}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[EVENT] status {status} discord_dm error guild={guild.Id} event={guildEvent.Id} user={userId}: {ex.Message}");
                }
            }

            try
            {
                if (_telegramNotifier != null)
                {
                    if (serverConfigs != null && serverConfigs.TryGetValue(guild.Id, out var liveCfg))
                    {
                        entry.TelegramChatId = liveCfg.TelegramChatId;
                        entry.TelegramMessageThreadId = liveCfg.TelegramMessageThreadId;
                        _store.Upsert(entry);
                    }

                    var tgText = $"{prefix} {statusText}: {guildEvent.Name}\n" +
                        $"🏰 Сервер: {guild.Name}\n" +
                        $"🕒 Когда: {startMsk:dd.MM.yyyy HH:mm} (по МСК)\n" +
                        $"📍 Где: {whereTextPlain}\n";

                    if (isStarted && guildEvent.Creator != null)
                    {
                        string? masterDisplayName = null;
                        if (GameSessionCommands._sessions.TryGetValue(guild.Id, out var guildSessions))
                        {
                            var linkedSession = guildSessions.Values.FirstOrDefault(s => s.EventId == guildEvent.Id && !s.IsStopped);
                            if (linkedSession != null)
                                masterDisplayName = linkedSession.MasterName;
                        }
                        if (masterDisplayName == null && serverConfigs != null
                            && serverConfigs.TryGetValue(guild.Id, out var sc))
                        {
                            sc.MasterNameMap?.TryGetValue(guildEvent.Creator.Id.ToString(), out masterDisplayName);
                        }
                        masterDisplayName ??= guildEvent.Creator.Username;
                        tgText += $"👤 Мастер: {masterDisplayName}\n";
                    }
                    else if (guildEvent.Creator != null)
                    {
                        string? creatorName = null;
                        if (serverConfigs != null
                            && serverConfigs.TryGetValue(guild.Id, out var scSt)
                            && scSt.MasterNameMap?.TryGetValue(guildEvent.Creator.Id.ToString(), out var mappedSt) == true
                            && !string.IsNullOrWhiteSpace(mappedSt))
                            creatorName = mappedSt;
                        else
                            creatorName = guildEvent.Creator.Username;
                        tgText += $"👤 Создал: {creatorName}\n";
                    }

                    tgText += $"ℹ️ {mark}\n🔗 {eventUrl}";

                    if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                    {
                        var desc = guildEvent.Description.Trim();
                        if (desc.Length > 800) desc = desc.Substring(0, 799) + "…";
                        tgText += $"\n\nОписание события:\n{desc}";
                    }

                    bool ok;
                    if (entry.TelegramMessageId > 0)
                    {
                        ok = entry.TelegramHasPhoto
                            ? await _telegramNotifier.EditMessageCaptionAsync(guild.Id, entry.TelegramMessageId, tgText)
                            : await _telegramNotifier.EditMessageTextAsync(guild.Id, entry.TelegramMessageId, tgText);
                        Log($"[EVENT] status {status} telegram {(ok ? "ok" : "fail")} guild={guild.Id} event={guildEvent.Id} msg={entry.TelegramMessageId}");
                    }
                    else
                    {
                        var sentId = await _telegramNotifier.SendMessageReturningMessageIdAsync(guild.Id, tgText);
                        ok = sentId.HasValue;
                        if (sentId.HasValue)
                        {
                            entry.TelegramMessageId = sentId.Value;
                            entry.TelegramHasPhoto = false;
                            _store.Upsert(entry);
                        }
                        Log($"[EVENT] status {status} telegram {(ok ? "sent" : "skip/fail")} guild={guild.Id} event={guildEvent.Id} msg={(sentId ?? 0)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[EVENT] status {status} telegram error guild={guild.Id} event={guildEvent.Id}: {ex.Message}");
            }

            if (isCancelled || isCompleted)
            {
                try { _store.Remove(guild.Id, guildEvent.Id); } catch { }
            }
        }

        private static string Truncate(string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            value = value.Trim();
            return value.Length <= max ? value : value.Substring(0, max - 1) + "…";
        }

        private static bool TryGetMoscowTime(DateTime utc, out DateTime msk)
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time")
                         ?? TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
                if (tz == null) { msk = default; return false; }
                msk = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
                return true;
            }
            catch { msk = default; return false; }
        }

        private static void Log(string msg)
        {
            try { Console.WriteLine(msg); } catch { }
        }

        private static Task LogError(string msg)
        {
            try { BotLogger.Error(LogCategory.Discord, msg); } catch { }
            return Task.CompletedTask;
        }
    }
}
