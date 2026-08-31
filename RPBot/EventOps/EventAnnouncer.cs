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
        private readonly Func<DiscordSocketClient>? _clientProvider;

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

        /// <summary>
        /// Конструктор с провайдером клиента — для случая, когда клиент пересоздаётся
        /// при рестарте (Program.cs пересоздаёт _client, а EventAnnouncer остаётся).
        /// </summary>
        public EventAnnouncer(
            Func<DiscordSocketClient> clientProvider,
            Func<IReadOnlyDictionary<ulong, ServerConfig>> serverConfigsProvider,
            TelegramNotifier? telegramNotifier,
            EventAnnouncementStore? store,
            EventNotificationService eventNotifications)
            : this(clientProvider(), serverConfigsProvider, telegramNotifier, store, eventNotifications)
        {
            _clientProvider = clientProvider;
        }

        private DiscordSocketClient ResolveClient()
        {
            // Если при создании передали провайдер — берём актуальный клиент оттуда.
            // Это покрывает случай, когда Program.cs пересоздал _client после рестарта,
            // а EventAnnouncer продолжает жить со старой (Disconnected) ссылкой.
            if (_clientProvider != null)
            {
                try
                {
                    var current = _clientProvider();
                    if (current != null && current.ConnectionState == ConnectionState.Connected)
                        return current;
                }
                catch { }
            }
            return _client;
        }

        public DiscordSocketClient Client => ResolveClient();

        public Task AnnounceCreatedAsync(SocketGuildEvent guildEvent)
            => AnnounceCreatedInternalAsync(guildEvent);

        public Task AnnounceUpdatedAsync(Cacheable<SocketGuildEvent, ulong> beforeCache, SocketGuildEvent guildEvent)
            => AnnounceUpdatedInternalAsync(beforeCache, guildEvent);

        public Task AnnounceStatusChangedAsync(SocketGuildEvent guildEvent, string status)
            => AnnounceStatusChangedInternalAsync(guildEvent, status);

        public async Task AnnounceCreatedInternalAsync(SocketGuildEvent guildEvent)
        {
            Log($"[EVENT] AnnounceCreatedInternalAsync enter guild={guildEvent?.Guild?.Id} event={guildEvent?.Id}");
            if (guildEvent?.Guild == null) { Log("[EVENT] AnnounceCreatedInternalAsync: guildEvent.Guild is null, return"); return; }

            var client = ResolveClient();
            Log($"[EVENT] AnnounceCreatedInternalAsync: resolveClient _client={_client.GetHashCode()} resolved={client.GetHashCode()} state={client.ConnectionState} login={client.LoginState}");

            var guild = guildEvent.Guild;
            var serverConfigs = _serverConfigsProvider();
            if (serverConfigs == null || !serverConfigs.TryGetValue(guild.Id, out var config))
            {
                Log($"[EVENT] AnnounceCreatedInternalAsync: no server config for guild={guild.Id}, return");
                return;
            }
            if (config.GeneralRGChannelID == 0)
            {
                Log($"[EVENT] AnnounceCreatedInternalAsync: GeneralRGChannelID=0 for guild={guild.Id}, return");
                return;
            }

            if (client.ConnectionState != ConnectionState.Connected)
            {
                Log($"[EVENT] announce skipped (client not connected) guild={guild.Id} event={guildEvent.Id} state={client.ConnectionState} login={client.LoginState}");
                // Подождём до 15с — типичный кейс resync после рестарта: client ещё не вошёл.
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline
                    && (client.ConnectionState != ConnectionState.Connected
                        || client.LoginState != LoginState.LoggedIn))
                {
                    await Task.Delay(250);
                }
                Log($"[EVENT] announce after wait: state={client.ConnectionState} login={client.LoginState}");
            }
            else
            {
                Log($"[EVENT] AnnounceCreatedInternalAsync: state={client.ConnectionState} login={client.LoginState}, proceeding with GetChannelAsync({config.GeneralRGChannelID})");
            }

            ITextChannel? announceChannel = null;
            try
            {
                announceChannel = await client.GetChannelAsync(config.GeneralRGChannelID) as ITextChannel;
                Log($"[EVENT] AnnounceCreatedInternalAsync: GetChannelAsync returned {announceChannel?.Id.ToString() ?? "null"}");
            }
            catch (Exception ex)
            {
                Log($"[EVENT] announce GetChannel failed guild={guild.Id} event={guildEvent.Id}: {ex.Message}");
                // Если провалилось из-за "не залогинен" — попробуем ещё раз через 2с.
                // REST клиента иногда тупит на старте даже когда ConnectionState=Connected.
                if (ex.Message?.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    await Task.Delay(2000);
                    try
                    {
                        Log($"[EVENT] AnnounceCreatedInternalAsync retry GetChannelAsync state={client.ConnectionState} login={client.LoginState}");
                        announceChannel = await client.GetChannelAsync(config.GeneralRGChannelID) as ITextChannel;
                        Log($"[EVENT] AnnounceCreatedInternalAsync retry returned {announceChannel?.Id.ToString() ?? "null"}");
                    }
                    catch (Exception ex2)
                    {
                        Log($"[EVENT] announce GetChannel retry failed: {ex2.Message}");
                    }
                }
            }
            if (announceChannel == null) { Log("[EVENT] AnnounceCreatedInternalAsync: announceChannel is null, return"); return; }

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

            // Поля — для канала. Footer для канала про /event_notify, для DM — отдельный (см. ниже).
            embedBuilder.AddField("🕒 Когда", $"<t:{new DateTimeOffset(DateTime.SpecifyKind(guildEvent.StartTime.UtcDateTime, DateTimeKind.Utc)).ToUnixTimeSeconds()}:F>", true);
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

            embedBuilder.WithFooter("Чтобы это сообщение видеть и в личке: '/event_notify action: subscribe'");
            var embed = embedBuilder.Build();

            var announceMsg = await announceChannel.SendMessageAsync(embed: embed);
            Log($"[EVENT] announce sent discord_channel guild={guild.Id} event={guildEvent.Id} channel={announceChannel.Id} msg={announceMsg.Id}");
            Log($"[EVENT] AnnounceCreatedInternalAsync: announce message sent, proceeding to telegram/dm/store");

            int? tgMessageId = null;
            var tgHasPhoto = false;
            try
            {
                if (_telegramNotifier != null)
                {
                    var tgText = $"📅 Новое событие: {guildEvent.Name}\n" +
                        $"📍 Где: {guild.Name} → {whereTextPlain}\n" +
                        $"🕒 Когда: {startMsk:dd.MM.yyyy HH:mm} (по МСК)\n" +
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

            var subscriberIds = _eventNotifications?.GetActiveSubscribers(guild.Id) ?? Array.Empty<ulong>();
            var dmMap = new Dictionary<ulong, ulong>();
            foreach (var userId in subscriberIds)
            {
                try
                {
                    var user = guild.GetUser(userId) as IUser ?? client.GetUser(userId);
                    if (user == null)
                    {
                        try { user = await client.Rest.GetUserAsync(userId); } catch { }
                    }
                    if (user == null) continue;

                    var dm = await user.CreateDMChannelAsync();
                    // Для DM переписываем footer на «Выкл: стоп • Вкл: хочу».
                    var dmEmbed = embedBuilder.Build().ToEmbedBuilder();
                    dmEmbed.Footer = new EmbedFooterBuilder
                    {
                        Text = "Выкл: напиши «стоп» • Вкл: «хочу»"
                    };
                    var dmMsg = await dm.SendMessageAsync(embed: dmEmbed.Build());
                    dmMap[userId] = dmMsg.Id;
                    Log($"[EVENT] announce sent discord_dm guild={guild.Id} event={guildEvent.Id} user={userId} msg={dmMsg.Id}");
                }
                catch (Exception ex)
                {
                                    // Сначала гарантированный fallback в stderr — даже если оба
                                    // логгера упадут, мы не потеряем диагностику DM-провала.
                                    Console.Error.WriteLine($"[EVENT] announce discord_dm error guild={guild.Id} event={guildEvent.Id} user={userId}: {ex.Message}");
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
                // Сохраняем snapshot состояния для будущего diff (создание/детект изменений).
                try
                {
                    entry.LastName = guildEvent.Name;
                    entry.LastDescription = guildEvent.Description;
                    entry.LastStartTimeUtc = new DateTimeOffset(DateTime.SpecifyKind(guildEvent.StartTime.UtcDateTime, DateTimeKind.Utc));
                    entry.LastEndTimeUtc = guildEvent.EndTime.HasValue
                        ? new DateTimeOffset(DateTime.SpecifyKind(guildEvent.EndTime.Value.UtcDateTime, DateTimeKind.Utc))
                        : (DateTimeOffset?)null;
                    entry.LastChannelId = guildEvent.Channel?.Id;
                    entry.LastLocation = guildEvent.Location;
                    entry.LastCoverImageUrl = guildEvent.GetCoverImageUrl();
                }
                catch { }
                _store.Upsert(entry);
            }
        }

        public async Task AnnounceUpdatedInternalAsync(Cacheable<SocketGuildEvent, ulong> beforeCache, SocketGuildEvent guildEvent)
        {
            Log($"[EVENT] AnnounceUpdatedInternalAsync enter guild={guildEvent?.Guild?.Id} event={guildEvent?.Id}");
            if (guildEvent?.Guild == null) { Log("[EVENT] AnnounceUpdatedInternalAsync: guildEvent.Guild is null, return"); return; }
            if (_store == null) { Log("[EVENT] AnnounceUpdatedInternalAsync: _store is null, return"); return; }

            var client = ResolveClient();
            Log($"[EVENT] AnnounceUpdatedInternalAsync: resolveClient _client={_client.GetHashCode()} resolved={client.GetHashCode()} state={client.ConnectionState} login={client.LoginState}");

            SocketGuildEvent? before = null;
            try { before = await beforeCache.GetOrDownloadAsync(); } catch { }

            var guild = guildEvent.Guild;
            var entry = _store.TryGet(guild.Id, guildEvent.Id);
            if (entry == null) { Log($"[EVENT] AnnounceUpdatedInternalAsync: no entry in store for guild={guild.Id} event={guildEvent.Id}, skipping (no existing announcement to update)"); return; }

            // Гарантируем non-null коллекции: на resync-пути entry может прийти
            // с незаполненными словарями (старые данные до добавления ??= в ctor'ах).
            if (entry.DmMessageIdsByUserId == null)
                entry.DmMessageIdsByUserId = new Dictionary<ulong, ulong>();

            // Защита от дублей: на resync мы вызываем AnnounceUpdatedAsync(default, ...)
            // для каждого события в EventAnnouncementStore. Если состояние события не
            // менялось с момента прошлого апдейта — не отправляем/не редактируем.
            if (IsUnchangedSinceLastUpdate(entry, guildEvent))
            {
                Log($"[EVENT] AnnounceUpdatedInternalAsync skip: unchanged since last update guild={guild.Id} event={guildEvent.Id}");
                return;
            }

            if (client.ConnectionState != ConnectionState.Connected)
            {
                Log($"[EVENT] update skipped (client not connected) guild={guild.Id} event={guildEvent.Id} state={client.ConnectionState} login={client.LoginState}");
                return;
            }
            else
            {
                Log($"[EVENT] AnnounceUpdatedInternalAsync: state={client.ConnectionState} login={client.LoginState}, proceeding");
            }

            // На resync-фазе before часто отсутствует (default Cacheable). Используем
            // сохранённый snapshot из store как «прошлое состояние», чтобы diff работал
            // и в resync, и в обычных update-вызовах.
            var serverConfigs = _serverConfigsProvider();

            var changes = new List<string>();
            var changesDiscord = new List<string>();
            try
            {
                // diff из live-cache, если before есть
                if (before != null)
                {
                    DiffBeforeVsAfter(before, guildEvent, changes, changesDiscord);
                }
                // diff из snapshot в store (используется при resync)
                else
                {
                    DiffSnapshotVsCurrent(entry, guildEvent, changes, changesDiscord);
                }
            }
            catch { }

            var updatedMark = $"Обновлено: {DateTime.Now:dd.MM.yyyy HH:mm}";
                        // Discord-время выводится как локальный unix-timestamp (рендерится в часовом поясе читателя).
            var updatedMsk = TryGetMoscowTime(DateTime.UtcNow, out var mskNow) ? mskNow : DateTime.Now;
            var updatedMarkMsk = $"Обновлено: {updatedMsk:dd.MM.yyyy HH:mm} (по МСК)";
            var eventUrl = $"https://discord.com/events/{guild.Id}/{guildEvent.Id}";
            var startLocal = guildEvent.StartTime.ToLocalTime();
            var startMsk = TryGetMoscowTime(guildEvent.StartTime.UtcDateTime, out var mskStartTime)
                ? mskStartTime
                : startLocal;

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
                .WithColor(Color.Orange);
            if (!string.IsNullOrWhiteSpace(imageUrl))
                embedBuilder.WithThumbnailUrl(imageUrl);
            if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                embedBuilder.WithDescription(Truncate(guildEvent.Description, 2048));
                        embedBuilder.AddField("🕒 Когда", $"<t:{new DateTimeOffset(DateTime.SpecifyKind(guildEvent.StartTime.UtcDateTime, DateTimeKind.Utc)).ToUnixTimeSeconds()}:F>", true);
            embedBuilder.AddField("📍 Где", whereText, true);
            if (guildEvent.Creator != null)
                embedBuilder.AddField("👤 Создал", MentionUtils.MentionUser(guildEvent.Creator.Id), true);
            if (changesDiscord.Count > 0)
                embedBuilder.AddField("✏️ Изменения", string.Join("\n", changesDiscord.Take(10)), false);
                        embedBuilder.AddField("Статус", $"<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:F>", false);
            embedBuilder.WithFooter("Чтобы это сообщение видеть и в личке: '/event_notify action: subscribe'");
            var embed = embedBuilder.Build();

            // Telegram: подставляем МСК-метку для обновлений.
            var changesMsk = FormatChangesMsk(changes);

            // Сохраняем маркер обновления и состояние в store, чтобы UI мог показать,
            // что анонс был обновлён ботом (а не остался прежним).
            try
            {
                entry.LastUpdatedMark = updatedMark;
                entry.LastUpdatedAt = DateTime.UtcNow;
                entry.LastName = guildEvent.Name;
                entry.LastDescription = guildEvent.Description;
                entry.LastStartTimeUtc = new DateTimeOffset(DateTime.SpecifyKind(guildEvent.StartTime.UtcDateTime, DateTimeKind.Utc));
                entry.LastEndTimeUtc = guildEvent.EndTime.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(guildEvent.EndTime.Value.UtcDateTime, DateTimeKind.Utc))
                    : (DateTimeOffset?)null;
                entry.LastChannelId = guildEvent.Channel?.Id;
                entry.LastLocation = guildEvent.Location;
                entry.LastCoverImageUrl = guildEvent.GetCoverImageUrl();
                _store.UpdateEntry(entry);
            }
            catch { }

            try
            {
                if (entry.AnnounceChannelId != 0 && entry.AnnounceMessageId != 0)
                {
                    var ch = await client.GetChannelAsync(entry.AnnounceChannelId) as ITextChannel;
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

            var subscriberIds = _eventNotifications?.GetActiveSubscribers(guild.Id) ?? Array.Empty<ulong>();
            var dmMap = entry.DmMessageIdsByUserId ?? new Dictionary<ulong, ulong>();
            foreach (var userId in subscriberIds)
            {
                try
                {
                    if (!dmMap.TryGetValue(userId, out var dmMessageId) || dmMessageId == 0)
                        continue;
                    var user = guild.GetUser(userId) as IUser ?? client.GetUser(userId);
                    if (user == null)
                    {
                        try { user = await client.Rest.GetUserAsync(userId); } catch { }
                    }
                    if (user == null) continue;
                    var dm = await user.CreateDMChannelAsync();
                    var dmMsg = await dm.GetMessageAsync(dmMessageId) as IUserMessage;
                    if (dmMsg != null)
                    {
                        // Для DM переписываем footer на «Выкл: стоп • Вкл: хочу».
                        var dmEmbed = embedBuilder.Build().ToEmbedBuilder();
                        dmEmbed.Footer = new EmbedFooterBuilder
                        {
                            Text = "Выкл: напиши «стоп» • Вкл: «хочу»"
                        };
                        await dmMsg.ModifyAsync(m => m.Embed = dmEmbed.Build());
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
                        $"📍 Где: {guild.Name} → {whereTextPlain}\n" +
                        $"🕒 Когда: {startMsk:dd.MM.yyyy HH:mm} (по МСК)\n" +
                        (guildEvent.Creator != null ? $"👤 Создал: {guildEvent.Creator.Username}\n" : string.Empty) +
                        (changes.Count > 0 ? $"\n✏️ Изменения:\n- {changesMsk}\n" : string.Empty) +
                        $"ℹ️ {updatedMarkMsk}\n" +
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

            if (entry.DmMessageIdsByUserId == null)
                entry.DmMessageIdsByUserId = new Dictionary<ulong, ulong>();

                    var client = ResolveClient();
                    if (client.ConnectionState != ConnectionState.Connected)
                    {
                        Log($"[EVENT] status {status} skipped (client not connected) guild={guild.Id} event={guildEvent.Id} state={client.ConnectionState} login={client.LoginState}");
                        // Подождём до 15с — типичный кейс сразу после рестарта: client ещё не вошёл.
                        var deadline = DateTime.UtcNow.AddSeconds(15);
                        while (DateTime.UtcNow < deadline
                            && (client.ConnectionState != ConnectionState.Connected
                                || client.LoginState != LoginState.LoggedIn))
                        {
                            await Task.Delay(500);
                        }
                        if (client.ConnectionState != ConnectionState.Connected || client.LoginState != LoginState.LoggedIn)
                        {
                            Log($"[EVENT] status {status} giveup (client still not connected) guild={guild.Id} event={guildEvent.Id} state={client.ConnectionState} login={client.LoginState}");
                            return;
                        }
                        Log($"[EVENT] status {status} retry after wait guild={guild.Id} event={guildEvent.Id} state={client.ConnectionState} login={client.LoginState}");
                    }
                    else
                    {
                        Log($"[EVENT] AnnounceStatusChangedInternalAsync({status}) guild={guild.Id} event={guildEvent.Id} state={client.ConnectionState} login={client.LoginState}, proceeding");
                    }

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
            var statusText = isCancelled ? "Событие отменено" : isStarted ? "Событие началось" : isCompleted ? "Событие завершено" : "Событие обновлено";
            var mark = $"{prefix} {statusText}: {DateTime.Now:dd.MM.yyyy HH:mm}";
                        // Discord-время выводится как локальный unix-timestamp (рендерится в часовом поясе читателя).
                        var mskNow = TryGetMoscowTime(DateTime.UtcNow, out var mskNowValue) ? mskNowValue : DateTime.Now;
                        var markMsk = $"{prefix} {statusText}: {mskNow:dd.MM.yyyy HH:mm} (по МСК)";

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
                .WithColor(isCancelled ? Color.DarkRed : isStarted ? Color.Green : isCompleted ? Color.DarkGreen : Color.Orange);
            if (!string.IsNullOrWhiteSpace(imageUrl))
                embedBuilder.WithThumbnailUrl(imageUrl);
            if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                embedBuilder.WithDescription(guildEvent.Description.Length <= 2048 ? guildEvent.Description : guildEvent.Description.Substring(0, 2047) + "…");
            // «Когда» переименовываем в «Начало» для started/completed (реальное время старта).
            // Для cancelled — отдельная метка «Отменено». Для updated — «Статус» (как раньше).
            if (isStarted)
            {
                embedBuilder.AddField("🕒 Начало", $"<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:F>", true);
                embedBuilder.AddField("📍 Где", whereText, true);
                if (guildEvent.Creator != null)
                    embedBuilder.AddField("👤 Создал", MentionUtils.MentionUser(guildEvent.Creator.Id), true);
            }
            else if (isCompleted)
            {
                embedBuilder.AddField("🕒 Начало", $"<t:{new DateTimeOffset(DateTime.SpecifyKind(guildEvent.StartTime.UtcDateTime, DateTimeKind.Utc)).ToUnixTimeSeconds()}:F>", true);
                embedBuilder.AddField("🛑 Завершение", $"<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:F>", true);
                embedBuilder.AddField("📍 Где", whereText, true);
                if (guildEvent.Creator != null)
                    embedBuilder.AddField("👤 Создал", MentionUtils.MentionUser(guildEvent.Creator.Id), true);
            }
            else if (isCancelled)
            {
                embedBuilder.AddField("❌ Отменено", $"<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:F>", true);
                embedBuilder.AddField("📍 Где", whereText, true);
                if (guildEvent.Creator != null)
                    embedBuilder.AddField("👤 Создал", MentionUtils.MentionUser(guildEvent.Creator.Id), true);
            }
            else
            {
                embedBuilder.AddField("🕒 Когда", $"<t:{new DateTimeOffset(DateTime.SpecifyKind(guildEvent.StartTime.UtcDateTime, DateTimeKind.Utc)).ToUnixTimeSeconds()}:F>", true);
                embedBuilder.AddField("📍 Где", whereText, true);
                if (guildEvent.Creator != null)
                    embedBuilder.AddField("👤 Создал", MentionUtils.MentionUser(guildEvent.Creator.Id), true);
                embedBuilder.AddField("Статус", $"<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:F>", false);
            }
            embedBuilder.WithFooter("Чтобы это сообщение видеть и в личке: '/event_notify action: subscribe'");
            var embed = embedBuilder.Build();

            try
            {
                            var ch = await client.GetChannelAsync(entry.AnnounceChannelId) as ITextChannel;
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
            var dmMap = entry.DmMessageIdsByUserId ?? new Dictionary<ulong, ulong>();
            foreach (var userId in subscriberIds)
            {
                try
                {
                    if (!dmMap.TryGetValue(userId, out var dmMessageId) || dmMessageId == 0)
                        continue;
                                var user = guild.GetUser(userId) as IUser ?? client.GetUser(userId);
                    if (user == null) continue;
                    var dm = await user.CreateDMChannelAsync();
                    var dmMsg = await dm.GetMessageAsync(dmMessageId) as IUserMessage;
                    if (dmMsg != null)
                    {
                        // Для DM переписываем footer на «Выкл: стоп • Вкл: хочу».
                        var dmEmbed = embedBuilder.Build().ToEmbedBuilder();
                        dmEmbed.Footer = new EmbedFooterBuilder
                        {
                            Text = "Выкл: напиши «стоп» • Вкл: «хочу»"
                        };
                        await dmMsg.ModifyAsync(m => m.Embed = dmEmbed.Build());
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
                        $"📍 Где: {guild.Name} → {whereTextPlain}\n" +
                        $"🕒 Когда: {startMsk:dd.MM.yyyy HH:mm} (по МСК)\n";

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

                    tgText += $"ℹ️ {markMsk}\n🔗 {eventUrl}";

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

        /// <summary>
        /// Преобразует список изменений в МСК-вариант.
        /// Заменяет все вхождения dd.MM.yyyy HH:mm → dd.MM.yyyy HH:mm (по МСК).
        /// Diff-функции используют startLocal при формировании текста, так что на
        /// летнее/зимнее время преобразование этих строк должно быть точным.
        /// Здесь мы просто переподписываем таймстемпы, найденные в формате
        /// "dd.MM.yyyy HH:mm" на их московские аналоги.
        /// </summary>
        private static string FormatChangesMsk(List<string> changes)
        {
            if (changes == null || changes.Count == 0) return string.Empty;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < changes.Count; i++)
            {
                if (i > 0) sb.Append("\n- ");
                else sb.Append("- ");
                sb.Append(ConvertLocalTimesToMsk(changes[i]));
            }
            return sb.ToString();
        }

        private static string ConvertLocalTimesToMsk(string text)
        {
            // Ищем вхождения "dd.MM.yyyy HH:mm" и переводим их в МСК.
            // Простая эвристика: если строка содержит формат, парсим каждое и
            // заменяем на московское представление.
            var pattern = new System.Text.RegularExpressions.Regex(@"\b(\d{2})\.(\d{2})\.(\d{4}) (\d{2}):(\d{2})\b");
            return pattern.Replace(text, m =>
            {
                try
                {
                    var day = int.Parse(m.Groups[1].Value);
                    var month = int.Parse(m.Groups[2].Value);
                    var year = int.Parse(m.Groups[3].Value);
                    var hour = int.Parse(m.Groups[4].Value);
                    var minute = int.Parse(m.Groups[5].Value);
                    // local time → UTC → Moscow
                    var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local);
                    var utc = local.ToUniversalTime();
                    if (TryGetMoscowTime(utc, out var msk))
                        return msk.ToString("dd.MM.yyyy HH:mm");
                }
                catch { }
                return m.Value;
            });
        }

        private static bool TryGetMoscowTime(DateTime utc, out DateTime msk)
            => MoscowTime.TryConvertFromUtc(utc, out msk);

        private static void Log(string msg)
        {
                    try { BotLogger.Info(LogCategory.Discord, msg); }
                    catch { Console.Error.WriteLine($"[Log-fallback] {msg}"); }
                }

                private static Task LogError(string msg)
                {
                    try { BotLogger.Error(LogCategory.Discord, msg); }
                    catch { Console.Error.WriteLine($"[LogError-fallback] {msg}"); }
                    return Task.CompletedTask;
                }

        /// <summary>
        /// Diff между двумя живыми объектами SocketGuildEvent.
        /// </summary>
        /// <summary>
        /// Сравнение текущего события с последним сохранённым snapshot.
        /// Возвращает true, если состояние идентично (нечего обновлять).
        /// Используется для подавления дублей при resync.
        /// </summary>
        private static bool IsUnchangedSinceLastUpdate(EventAnnouncementEntry snapshot, SocketGuildEvent current)
        {
            // Если у записи нет snapshot — это не resync, а реальное обновление.
            if (snapshot.LastName == null && snapshot.LastStartTimeUtc == null)
                return false;

            if (!string.Equals(snapshot.LastName, current.Name, StringComparison.Ordinal))
                return false;
            if (!string.Equals(snapshot.LastDescription ?? string.Empty, current.Description ?? string.Empty, StringComparison.Ordinal))
                return false;
            if (snapshot.LastStartTimeUtc.HasValue && snapshot.LastStartTimeUtc.Value.UtcDateTime != current.StartTime.UtcDateTime)
                return false;
            if (snapshot.LastChannelId != (current.Channel?.Id ?? 0))
                return false;
            if (!string.Equals(snapshot.LastLocation ?? string.Empty, current.Location ?? string.Empty, StringComparison.Ordinal))
                return false;
            if (!string.Equals(snapshot.LastCoverImageUrl ?? string.Empty, current.GetCoverImageUrl() ?? string.Empty, StringComparison.Ordinal))
                return false;
            return true;
        }

        private static void DiffBeforeVsAfter(
            SocketGuildEvent before,
            SocketGuildEvent after,
            List<string> changes,
            List<string> changesDiscord)
        {
            if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
            {
                var text = $"Название: '{before.Name}' → '{after.Name}'";
                changes.Add(text);
                changesDiscord.Add(text);
            }
            if (!string.Equals(before.Description ?? string.Empty, after.Description ?? string.Empty, StringComparison.Ordinal))
            {
                changes.Add("Описание изменено");
                changesDiscord.Add("Описание изменено");
            }
            if (before.StartTime != after.StartTime)
            {
                changes.Add($"Начало: {before.StartTime.ToLocalTime():dd.MM.yyyy HH:mm} → {after.StartTime.ToLocalTime():dd.MM.yyyy HH:mm}");
                changesDiscord.Add($"Начало: {DiscordTimeFormatter.FullDateTime(before.StartTime.ToLocalTime())} → {DiscordTimeFormatter.FullDateTime(after.StartTime.ToLocalTime())}");
            }
            if (before.EndTime != after.EndTime)
            {
                var bEnd = before.EndTime?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "—";
                var aEnd = after.EndTime?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "—";
                changes.Add($"Окончание: {bEnd} → {aEnd}");

                var bEndDiscord = before.EndTime.HasValue
                    ? DiscordTimeFormatter.FullDateTime(before.EndTime.Value.ToLocalTime())
                    : "—";
                var aEndDiscord = after.EndTime.HasValue
                    ? DiscordTimeFormatter.FullDateTime(after.EndTime.Value.ToLocalTime())
                    : "—";
                changesDiscord.Add($"Окончание: {bEndDiscord} → {aEndDiscord}");
            }
            if ((before.Channel?.Id ?? 0) != (after.Channel?.Id ?? 0) ||
                !string.Equals(before.Location ?? string.Empty, after.Location ?? string.Empty, StringComparison.Ordinal))
            {
                changes.Add("Место проведения изменено");
                changesDiscord.Add("Место проведения изменено");
            }
            if (!string.Equals(before.GetCoverImageUrl() ?? string.Empty, after.GetCoverImageUrl() ?? string.Empty, StringComparison.Ordinal))
            {
                changes.Add("Изображение изменено");
                changesDiscord.Add("Изображение изменено");
            }
        }

        /// <summary>
        /// Diff между snapshot в store и текущим SocketGuildEvent.
        /// Используется при resync, когда before-кэш не вернул данные.
        /// </summary>
        private static void DiffSnapshotVsCurrent(
            EventAnnouncementEntry snapshot,
            SocketGuildEvent current,
            List<string> changes,
            List<string> changesDiscord)
        {
            if (!string.Equals(snapshot.LastName, current.Name, StringComparison.Ordinal))
            {
                var text = $"Название: '{snapshot.LastName}' → '{current.Name}'";
                changes.Add(text);
                changesDiscord.Add(text);
            }
            if (!string.Equals(snapshot.LastDescription ?? string.Empty, current.Description ?? string.Empty, StringComparison.Ordinal))
            {
                changes.Add("Описание изменено");
                changesDiscord.Add("Описание изменено");
            }
            var beforeStart = snapshot.LastStartTimeUtc?.UtcDateTime;
            if (beforeStart.HasValue && beforeStart.Value != current.StartTime.UtcDateTime)
            {
                var a = beforeStart.Value.ToLocalTime();
                var b = current.StartTime.ToLocalTime();
                changes.Add($"Начало: {a:dd.MM.yyyy HH:mm} → {b:dd.MM.yyyy HH:mm}");
                changesDiscord.Add($"Начало: {DiscordTimeFormatter.FullDateTime(a)} → {DiscordTimeFormatter.FullDateTime(b)}");
            }
            var beforeEnd = snapshot.LastEndTimeUtc;
            if (beforeEnd != current.EndTime?.UtcDateTime)
            {
                var bEnd = beforeEnd?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "—";
                var aEnd = current.EndTime?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "—";
                changes.Add($"Окончание: {bEnd} → {aEnd}");

                var bEndDiscord = beforeEnd.HasValue
                    ? DiscordTimeFormatter.FullDateTime(beforeEnd.Value.ToLocalTime())
                    : "—";
                var aEndDiscord = current.EndTime.HasValue
                    ? DiscordTimeFormatter.FullDateTime(current.EndTime.Value.ToLocalTime())
                    : "—";
                changesDiscord.Add($"Окончание: {bEndDiscord} → {aEndDiscord}");
            }
            if (snapshot.LastChannelId.HasValue && snapshot.LastChannelId.Value != (current.Channel?.Id ?? 0))
            {
                changes.Add("Место проведения изменено");
                changesDiscord.Add("Место проведения изменено");
            }
            else if (!string.Equals(snapshot.LastLocation ?? string.Empty, current.Location ?? string.Empty, StringComparison.Ordinal))
            {
                changes.Add("Место проведения изменено");
                changesDiscord.Add("Место проведения изменено");
            }
            if (!string.Equals(snapshot.LastCoverImageUrl ?? string.Empty, current.GetCoverImageUrl() ?? string.Empty, StringComparison.Ordinal))
            {
                changes.Add("Изображение изменено");
                changesDiscord.Add("Изображение изменено");
            }
        }
    }
}
