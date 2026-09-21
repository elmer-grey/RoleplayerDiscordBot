using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
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

        /// <summary>
        /// Обновляет анонс (Discord embed + Telegram + DM подписчикам) для события,
        /// которое пропало из кэша, но доступно через REST. Используется в RESYNC,
        /// когда после перезагрузки бота событие уже в финальном статусе
        /// (Active/Completed/Cancelled) и в кэше SocketGuild.Events его нет.
        /// Не трогает само событие в Discord — только наш анонс.
        /// </summary>
        public Task AnnounceStatusChangedFromRestAsync(RestGuildEvent restEvent, EventAnnouncementEntry entry, string status)
            => AnnounceStatusChangedFromRestInternalAsync(restEvent, entry, status);

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

            embedBuilder.WithFooter("Чтобы это сообщение видеть и в личке: /event_notify action: Подписаться на уведомления");
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
                        // Новый порядок блоков ТГ:
                        //   1) Шапка: название
                        //   2) Когда / Где / Кто
                        //   3) Пустая строка
                        //   4) Описание
                        //   5) Пустая строка
                        //   6) Изменения + ссылка
                        string? creatorName = null;
                        if (guildEvent.Creator != null)
                        {
                        if (serverConfigs.TryGetValue(guild.Id, out var scCr)
                            && scCr.MasterNameMap?.TryGetValue(guildEvent.Creator.Id.ToString(), out var mappedCr) == true
                            && !string.IsNullOrWhiteSpace(mappedCr))
                        creatorName = mappedCr;
                        else
                        creatorName = guildEvent.Creator.Username;
                        }

                        var sb = new StringBuilder();
                        sb.Append("📅 Новое событие: ").Append(guildEvent.Name).Append('\n');
                        sb.Append("🕒 Когда: ").Append(startMsk.ToString("dd.MM.yyyy HH:mm")).Append(" (по МСК)\n");
                        sb.Append("📍 Где: ").Append(guild.Name).Append(" → ").Append(whereTextPlain).Append('\n');
                        if (!string.IsNullOrWhiteSpace(creatorName))
                            sb.Append("👤 Создал: ").Append(creatorName).Append('\n');

                        if (!string.IsNullOrWhiteSpace(guildEvent.Description))
            {
                            var desc = guildEvent.Description.Trim();
                            if (desc.Length > 800) desc = desc.Substring(0, 799) + "…";
                            sb.Append('\n').Append(desc).Append('\n');
                        }

                        sb.Append('\n');
                        sb.Append("🔗 ").Append(eventUrl);

                        var tgText = sb.ToString();

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
            embedBuilder.AddField("✏️ Изменения", string.Join("\n", changesDiscord.Take(10)), false);
            embedBuilder.AddField("Статус", $"<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:F>", false);
                        embedBuilder.WithFooter("Чтобы это сообщение видеть и в личке: /event_notify action: Подписаться на уведомления");
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
                        string? creatorName = null;
                        if (guildEvent.Creator != null)
                        {
                        if (serverConfigs != null && serverConfigs.TryGetValue(guild.Id, out var scUp)
                            && scUp.MasterNameMap?.TryGetValue(guildEvent.Creator.Id.ToString(), out var mappedUp) == true
                            && !string.IsNullOrWhiteSpace(mappedUp))
                        creatorName = mappedUp;
                        else
                        creatorName = guildEvent.Creator.Username;
                        }

                        var sb = new StringBuilder();
                        sb.Append("📅 Событие обновлено: ").Append(guildEvent.Name).Append('\n');
                        sb.Append("🕒 Когда: ").Append(startMsk.ToString("dd.MM.yyyy HH:mm")).Append(" (по МСК)\n");
                        sb.Append("📍 Где: ").Append(guild.Name).Append(" → ").Append(whereTextPlain).Append('\n');
                        if (!string.IsNullOrWhiteSpace(creatorName))
                            sb.Append("👤 Создал: ").Append(creatorName).Append('\n');

                        if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                        {
                            var desc = guildEvent.Description.Trim();
                            if (desc.Length > 800) desc = desc.Substring(0, 799) + "…";
                            sb.Append('\n').Append(desc).Append('\n');
                        }

                        sb.Append('\n');
                        if (changes.Count > 0)
                        {
                            sb.Append("✏️ Изменения:\n");
                            // changesMsk уже включает префикс "- " для каждой записи (см. FormatChangesMsk),
                            // поэтому здесь не добавляем "- " ещё раз.
                            sb.Append(changesMsk).Append('\n');
                        }
                        sb.Append("ℹ️ ").Append(updatedMarkMsk).Append('\n');
                        sb.Append("🔗 ").Append(eventUrl);

                        var tgText = sb.ToString();

                        var (ok, err) = entry.TelegramHasPhoto
                        ? await _telegramNotifier.EditMessageCaptionWithDetailsAsync(guild.Id, entry.TelegramMessageId, tgText)
                        : await _telegramNotifier.EditMessageTextWithDetailsAsync(guild.Id, entry.TelegramMessageId, tgText);

                        Log($"[EVENT] update telegram {(ok ? "ok" : $"fail — {err}")} guild={guild.Id} event={guildEvent.Id} msg={entry.TelegramMessageId} hasPhoto={entry.TelegramHasPhoto}");
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

            // Записываем фактическое время старта в store — нужно, чтобы при
            // последующем completed показать «когда событие реально началось»,
            // а не плановое время из event-объекта.
            if (isStarted)
            {
                try
                {
                    entry.ActualStartTimeUtc = DateTimeOffset.UtcNow;
                    _store.UpdateEntry(entry);
                }
                catch { }
            }

            // Записываем фактическое время отмены — нужно EventOpsLifecycleService
            // чтобы отсчитать «+24ч удаление» от момента cancel, а не от
            // планового LastStartTimeUtc.
            if (isCancelled)
            {
                try
                {
                    entry.CancelledAtUtc = DateTimeOffset.UtcNow;
                    _store.UpdateEntry(entry);
                }
                catch { }
            }

            // Записываем фактическое время завершения — нужно для двух вещей:
            //   1) При status-from-rest (после рестарта) поле «Завершение» должно
            //      показывать реальное время окончания, а не момент рестарта.
            //   2) EventOpsLifecycleService.ScheduleCleanup24h считает «+24ч
            //      удаление» отсюда, а не от планового LastStartTimeUtc.
            // Пишем только при первом observed completed — повторные вызовы
            // (например, после рестарта через status-from-rest) сохранят
            // оригинальный момент.
            if (isCompleted && !entry.CompletedAtUtc.HasValue)
            {
                try
                {
                    entry.CompletedAtUtc = DateTimeOffset.UtcNow;
                    _store.UpdateEntry(entry);
                }
                catch { }
            }

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
            // Для completed «Начало» — фактическое время старта (из ActualStartTimeUtc,
            // записанного в момент started), а не плановое guildEvent.StartTime.
            var actualStartUtc = entry.ActualStartTimeUtc ?? new DateTimeOffset(DateTime.SpecifyKind(guildEvent.StartTime.UtcDateTime, DateTimeKind.Utc));
            embedBuilder.AddField("🕒 Начало", $"<t:{actualStartUtc.ToUnixTimeSeconds()}:F>", true);
            // «Завершение» — из сохранённого CompletedAtUtc (записан в момент
            // первого observed completed). Fallback на DateTimeOffset.UtcNow
            // нужен только если событие пришло completed минуя started
            // (ActualStartTimeUtc отсутствует) и CompletedAtUtc ещё не
            // успели проставить — это редкий кейс, на следующем рестарте
            // будет исправлено.
            var completedUtc = entry.CompletedAtUtc ?? DateTimeOffset.UtcNow;
            embedBuilder.AddField("🛑 Завершение", $"<t:{completedUtc.ToUnixTimeSeconds()}:F>", true);
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
                        embedBuilder.WithFooter("Чтобы это сообщение видеть и в личке: /event_notify action: Подписаться на уведомления");
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

            // Для completed используем фактическое время старта (из ActualStartTimeUtc,
            // записанного в момент started). Если такого нет — fallback на плановое.
            DateTime? actualStartMsk = null;
            if (isCompleted && entry.ActualStartTimeUtc.HasValue
                && TryGetMoscowTime(entry.ActualStartTimeUtc.Value.UtcDateTime, out var mskActual))
            {
                actualStartMsk = mskActual;
            }

            // Для completed используем фактическое время завершения из
            // entry.CompletedAtUtc (записано в момент первого observed completed).
            // Это гарантирует, что TG-сообщение «Завершение» не перезапишется
            // моментом рестарта при последующих ресинках.
            DateTime? completedMskParam = null;
            if (isCompleted && entry.CompletedAtUtc.HasValue
                && TryGetMoscowTime(entry.CompletedAtUtc.Value.UtcDateTime, out var mskCompleted))
            {
                completedMskParam = mskCompleted;
            }

            var tgText = BuildStatusTelegramText(guildEvent, prefix, statusText, whereTextPlain, eventUrl, markMsk, startMsk, isStarted, isCompleted, isCancelled, serverConfigs, actualStartMsk, completedMskParam);

            bool ok;
                        string? error = null;
                        if (entry.TelegramMessageId > 0)
                        {
                        (ok, error) = entry.TelegramHasPhoto
                        ? await _telegramNotifier.EditMessageCaptionWithDetailsAsync(guild.Id, entry.TelegramMessageId, tgText)
                        : await _telegramNotifier.EditMessageTextWithDetailsAsync(guild.Id, entry.TelegramMessageId, tgText);
                        Log($"[EVENT] status {status} telegram {(ok ? "ok" : $"fail — {error}")} guild={guild.Id} event={guildEvent.Id} msg={entry.TelegramMessageId}");
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
                // Помечаем запись финальным статусом и сохраняем в стор —
            // удаление произойдёт позже в EventOpsLifecycleService.RunCleanup24hAsync
            // через 24ч. Так запись переживёт рестарт бота (RebuildFromStoreAsync
            // по LastUpdatedMark поставит cleanup24h на восстановление).
            try
            {
                entry.LastUpdatedMark = status;
                entry.LastUpdatedAt = DateTime.UtcNow;
                _store.UpdateEntry(entry);
            }
            catch { }
            }
        }

        /// <summary>
        /// RESYNC-вариант обновления анонса, когда SocketGuildEvent нет в кэше
        /// (событие уже завершилось/отменено и Discord его архивировал),
        /// но RestGuildEvent доступен. Использует те же поля (Name/Description/
        /// StartTime/Status) из REST, чтобы перестроить embed и ТГ-текст.
        /// </summary>
        private async Task AnnounceStatusChangedFromRestInternalAsync(
            RestGuildEvent restEvent,
            EventAnnouncementEntry entry,
            string status)
        {
            if (restEvent == null) return;
            if (_store == null) return;
            if (entry == null) return;

            var client = ResolveClient();
            if (client.ConnectionState != ConnectionState.Connected || client.LoginState != LoginState.LoggedIn)
            {
                Log($"[EVENT] status-from-rest {status} skipped (client not connected) guild={entry.GuildId} event={entry.EventId}");
                return;
            }

            var guild = client.GetGuild(entry.GuildId);
            if (guild == null)
            {
                Log($"[EVENT] status-from-rest {status} skipped (guild not in cache) guild={entry.GuildId} event={entry.EventId}");
                return;
            }

            if (entry.DmMessageIdsByUserId == null)
                entry.DmMessageIdsByUserId = new Dictionary<ulong, ulong>();

            var isCancelled = string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);
            var isStarted = string.Equals(status, "started", StringComparison.OrdinalIgnoreCase);
            var isCompleted = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);
            var prefix = isCancelled ? "❌" : isStarted ? "▶️" : isCompleted ? "✅" : "ℹ️";
            var statusText = isCancelled ? "Событие отменено" : isStarted ? "Событие началось" : isCompleted ? "Событие завершено" : "Событие обновлено";

            var serverConfigs = _serverConfigsProvider();
            var eventUrl = $"https://discord.com/events/{entry.GuildId}/{entry.EventId}";
            var startMsk = TryGetMoscowTime(restEvent.StartTime.UtcDateTime, out var mskStart) ? mskStart : restEvent.StartTime.ToLocalTime().DateTime;
            var mskNow = TryGetMoscowTime(DateTime.UtcNow, out var mskNowValue) ? mskNowValue : DateTime.Now;
            var markMsk = $"{prefix} {statusText}: {mskNow:dd.MM.yyyy HH:mm} (по МСК)";

            // Канал анонса: для embed используем Discord-упоминание канала из REST (ChannelId).
            string whereText;
            string whereTextPlain;
            if (restEvent.ChannelId.HasValue && restEvent.ChannelId.Value != 0)
            {
                whereText = $"<#{restEvent.ChannelId.Value}>";
                whereTextPlain = restEvent.ChannelId.Value.ToString();
            }
            else if (!string.IsNullOrWhiteSpace(restEvent.Location))
            {
                whereText = restEvent.Location;
                whereTextPlain = whereText;
            }
            else
            {
                whereText = "не указано";
                whereTextPlain = whereText;
            }

            string? coverUrl = restEvent.CoverImageId != null
                ? $"https://cdn.discordapp.com/guild-events/{restEvent.Id}/{restEvent.CoverImageId}.png?size=1024"
                : null;

            // Имя создателя для REST-события: у RestGuildEvent нет удобного
            // доступа к Creator.Username, только CreatorId. Без сохранённого
            // snapshot'а мы не знаем отображаемое имя — пропускаем поле.
            string? displayName = null;

            // Фактическое время старта для completed.
            DateTime? actualStartMsk = null;
            if (isCompleted && entry.ActualStartTimeUtc.HasValue
                && TryGetMoscowTime(entry.ActualStartTimeUtc.Value.UtcDateTime, out var mskActual))
            {
                actualStartMsk = mskActual;
            }

            // ── 1) Discord embed ──
            var embedBuilder = new EmbedBuilder()
                .WithTitle($"{prefix} {statusText}: {restEvent.Name}")
                .WithUrl(eventUrl)
                .WithColor(isCancelled ? Color.DarkRed : isStarted ? Color.Green : isCompleted ? Color.DarkGreen : Color.Orange);
            if (!string.IsNullOrWhiteSpace(coverUrl))
                embedBuilder.WithImageUrl(coverUrl);
            if (!string.IsNullOrWhiteSpace(restEvent.Description))
                embedBuilder.WithDescription(restEvent.Description.Length <= 2048 ? restEvent.Description : restEvent.Description.Substring(0, 2047) + "…");

            if (isStarted)
            {
                embedBuilder.AddField("🕒 Начало", $"<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:F>", true);
            }
            else if (isCompleted)
            {
                var actualStartUtc = entry.ActualStartTimeUtc ?? restEvent.StartTime;
                embedBuilder.AddField("🕒 Начало", $"<t:{actualStartUtc.ToUnixTimeSeconds()}:F>", true);
                // «Завершение» — из сохранённого entry.CompletedAtUtc
                // (записывается при первом observed completed, переживает рестарт).
                // Если null — это первый resync completed-события после рестарта:
                // фиксируем момент «сейчас» в стор, чтобы при повторных рестартах
                // число не «плавало». Fallback ниже — только если setter стора упал.
                if (!entry.CompletedAtUtc.HasValue)
                {
                    entry.CompletedAtUtc = DateTimeOffset.UtcNow;
                    try { _store.UpdateEntry(entry); } catch { }
                }
                var completedUtc = entry.CompletedAtUtc ?? DateTimeOffset.UtcNow;
                embedBuilder.AddField("🛑 Завершение", $"<t:{completedUtc.ToUnixTimeSeconds()}:F>", true);
            }
            else if (isCancelled)
            {
                embedBuilder.AddField("❌ Отменено", $"<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:F>", true);
            }
            else
            {
                embedBuilder.AddField("🕒 Когда", $"<t:{restEvent.StartTime.ToUnixTimeSeconds()}:F>", true);
            }

            embedBuilder.AddField("📍 Где", whereText, true);
            if (!string.IsNullOrWhiteSpace(displayName))
                embedBuilder.AddField(isStarted ? "👤 Мастер" : "👤 Создал", displayName, true);
            embedBuilder.WithFooter("Чтобы это сообщение видеть и в личке: /event_notify action: Подписаться на уведомления");

            try
            {
                var ch = await client.GetChannelAsync(entry.AnnounceChannelId) as ITextChannel;
                var msg = ch != null ? await ch.GetMessageAsync(entry.AnnounceMessageId) as IUserMessage : null;
                if (msg != null)
                {
                    await msg.ModifyAsync(m => m.Embed = embedBuilder.Build());
                    Log($"[EVENT] status-from-rest {status} discord_channel ok guild={entry.GuildId} event={entry.EventId} msg={entry.AnnounceMessageId}");
                }
                else
                {
                    Log($"[EVENT] status-from-rest {status} discord_channel skip (msg null) guild={entry.GuildId} event={entry.EventId}");
                }
            }
            catch (Exception ex)
            {
                Log($"[EVENT] status-from-rest {status} discord_channel error guild={entry.GuildId} event={entry.EventId}: {ex.Message}");
            }

            // ── 2) DM подписчикам ──
            var dmMap = entry.DmMessageIdsByUserId ?? new Dictionary<ulong, ulong>();
            foreach (var kv in dmMap)
            {
                try
                {
                    if (kv.Value == 0) continue;
                    IUser? user = guild.GetUser(kv.Key) as IUser ?? client.GetUser(kv.Key);
                    if (user == null) continue;
                    var dm = await user.CreateDMChannelAsync();
                    var dmMsg = await dm.GetMessageAsync(kv.Value) as IUserMessage;
                    if (dmMsg != null)
                    {
                        var dmEmbed = embedBuilder.Build().ToEmbedBuilder();
                        dmEmbed.Footer = new EmbedFooterBuilder { Text = "Выкл: напиши «стоп» • Вкл: «хочу»" };
                        await dmMsg.ModifyAsync(m => m.Embed = dmEmbed.Build());
                        Log($"[EVENT] status-from-rest {status} discord_dm ok guild={entry.GuildId} event={entry.EventId} user={kv.Key} msg={kv.Value}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[EVENT] status-from-rest {status} discord_dm error guild={entry.GuildId} event={entry.EventId} user={kv.Key}: {ex.Message}");
                }
            }

            // ── 3) Telegram ──
            if (_telegramNotifier != null)
            {
                try
                {
                    if (serverConfigs != null && serverConfigs.TryGetValue(entry.GuildId, out var liveCfg))
                    {
                        entry.TelegramChatId = liveCfg.TelegramChatId;
                        entry.TelegramMessageThreadId = liveCfg.TelegramMessageThreadId;
                    }

                    var sb = new System.Text.StringBuilder();
                    sb.Append(prefix).Append(' ').Append(statusText).Append(": ").Append(restEvent.Name).Append('\n');

                    if (isStarted)
                    {
                        sb.Append("🕒 Начало: ").Append(mskNow.ToString("dd.MM.yyyy HH:mm")).Append(" (по МСК)\n");
                    }
                    else if (isCompleted)
                    {
                        var startMskActual = actualStartMsk ?? startMsk;
                        sb.Append("🕒 Начало: ").Append(startMskActual.ToString("dd.MM.yyyy HH:mm")).Append(" (по МСК)\n");
                        // «Завершение» — из entry.CompletedAtUtc (см. embed выше).
                        DateTime completedMsk;
                        if (entry.CompletedAtUtc.HasValue
                            && TryGetMoscowTime(entry.CompletedAtUtc.Value.UtcDateTime, out var mskCompletedRest))
                        {
                            completedMsk = mskCompletedRest;
                        }
                        else
                        {
                            completedMsk = TryGetMoscowTime(DateTime.UtcNow, out var mskNowC) ? mskNowC : DateTime.Now;
                        }
                        sb.Append("🛑 Завершение: ").Append(completedMsk.ToString("dd.MM.yyyy HH:mm")).Append(" (по МСК)\n");
                    }
                    else
                    {
                        sb.Append("🕒 Когда: ").Append(startMsk.ToString("dd.MM.yyyy HH:mm")).Append(" (по МСК)\n");
                    }

                    sb.Append("📍 Где: ").Append(guild.Name).Append(" → ").Append(whereTextPlain).Append('\n');

                    if (!string.IsNullOrWhiteSpace(displayName))
                        sb.Append(isStarted ? "👤 Мастер: " : "👤 Создал: ").Append(displayName).Append('\n');

                    if (!string.IsNullOrWhiteSpace(restEvent.Description))
                    {
                        var desc = restEvent.Description.Trim();
                        if (desc.Length > 800) desc = desc.Substring(0, 799) + "…";
                        sb.Append('\n').Append(desc).Append('\n');
                    }

                    sb.Append('\n');
                    if (isCancelled)
                    {
                        sb.Append("✏️ Изменения:\n");
                        sb.Append("- ").Append(markMsk).Append('\n');
                    }
                    sb.Append("🔗 ").Append(eventUrl);

                    var tgText = sb.ToString();

                    bool ok;
                    string? error = null;
                    if (entry.TelegramMessageId > 0)
                    {
                        (ok, error) = entry.TelegramHasPhoto
                            ? await _telegramNotifier.EditMessageCaptionWithDetailsAsync(entry.GuildId, entry.TelegramMessageId, tgText)
                            : await _telegramNotifier.EditMessageTextWithDetailsAsync(entry.GuildId, entry.TelegramMessageId, tgText);
                        Log($"[EVENT] status-from-rest {status} telegram {(ok ? "ok" : $"fail — {error}")} guild={entry.GuildId} event={entry.EventId} msg={entry.TelegramMessageId}");
                    }
                    else
                    {
                        var sentId = await _telegramNotifier.SendMessageReturningMessageIdAsync(entry.GuildId, tgText);
                        ok = sentId.HasValue;
                        if (sentId.HasValue)
                        {
                            entry.TelegramMessageId = sentId.Value;
                            entry.TelegramHasPhoto = false;
                        }
                        Log($"[EVENT] status-from-rest {status} telegram {(ok ? "sent" : "skip/fail")} guild={entry.GuildId} event={entry.EventId} msg={(sentId ?? 0)}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[EVENT] status-from-rest {status} telegram error guild={entry.GuildId} event={entry.EventId}: {ex.Message}");
                }
            }

            // Помечаем запись финальным статусом и сохраняем в стор.
            // Удаление произойдёт позже в EventOpsLifecycleService.RunCleanup24hAsync
            // через 24ч — это даёт шанс пережить рестарт бота.
            entry.LastUpdatedMark = status;
            entry.LastUpdatedAt = DateTime.UtcNow;
            try { _store.UpdateEntry(entry); } catch { }
        }

                    /// <summary>
                    /// Собирает текст Телеграм-сообщения для изменения статуса события
                    /// в формате «Шапка → Когда/Где/Кто → пустая строка → описание →
                    /// пустая строка → [изменения] + метка + ссылка».
                    /// Для started/completed блок «Изменения» не выводится — сам факт
                    /// старта/завершения уже виден в шапке. Для cancelled — метка
                    /// отмены попадает в «Изменения» как доп. контекст.
                    /// Имя мастера при status='started' берётся из активной сессии,
                    /// затем из MasterNameMap, иначе — из Creator.Username.
                    /// Имя создателя для остальных статусов — из MasterNameMap,
                    /// иначе — из Creator.Username.
                    /// </summary>
                    private static string BuildStatusTelegramText(
                        SocketGuildEvent guildEvent,
                        string prefix,
                        string statusText,
                        string whereTextPlain,
                        string eventUrl,
                        string markMsk,
                        DateTime startMsk,
                        bool isStarted,
                        bool isCompleted,
                        bool isCancelled,
                        IReadOnlyDictionary<ulong, ServerConfig>? serverConfigs,
                        DateTime? actualStartMsk = null,
                        DateTime? completedMsk = null)
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.Append(prefix).Append(' ').Append(statusText).Append(": ").Append(guildEvent.Name).Append('\n');

                        // Для started/completed — показываем «Начало» (фактическое время старта)
                        // и «Завершение» (фактическое время завершения). Для started
                        // используем момент сейчас, а не плановое startMsk — иначе
                        // пользователь видит путаницу «вроде началось, а время будущее».
                        if (isStarted)
                        {
                            var startedMskNow = TryGetMoscowTime(DateTime.UtcNow, out var mskNowStarted) ? mskNowStarted : DateTime.Now;
                            sb.Append("🕒 Начало: ").Append(startedMskNow.ToString("dd.MM.yyyy HH:mm")).Append(" (по МСК)\n");
                        }
                        else if (isCompleted)
                        {
                            // «Начало» показываем фактическое (когда событие реально стартовало),
                            // а не плановое. Если ActualStartTimeUtc не сохранилось
                            // (например, бот пропустил started) — fallback на плановое.
                            var startMskActual = actualStartMsk ?? startMsk;
                            sb.Append("🕒 Начало: ").Append(startMskActual.ToString("dd.MM.yyyy HH:mm")).Append(" (по МСК)\n");
                            // «Завершение» — из сохранённого completedMsk (передан
                            // вызывающим кодом из entry.CompletedAtUtc). Это гарантирует,
                            // что при рестарте/resync TG-сообщение не перезапишется
                            // моментом рестарта. Fallback на DateTime.UtcNow —
                            // только для редкого кейса «completed минуя started».
                            DateTime completedMskActual;
                            if (completedMsk.HasValue)
                            {
                                completedMskActual = completedMsk.Value;
                            }
                            else
                            {
                                completedMskActual = TryGetMoscowTime(DateTime.UtcNow, out var mskNowC) ? mskNowC : DateTime.Now;
                            }
                            sb.Append("🛑 Завершение: ").Append(completedMskActual.ToString("dd.MM.yyyy HH:mm")).Append(" (по МСК)\n");
                        }
                        else
                        {
                            sb.Append("🕒 Когда: ").Append(startMsk.ToString("dd.MM.yyyy HH:mm")).Append(" (по МСК)\n");
                        }

                        sb.Append("📍 Где: ").Append(guildEvent.Guild?.Name ?? "?").Append(" → ").Append(whereTextPlain).Append('\n');

                        string? displayName = null;
                        if (guildEvent.Creator != null)
                        {
                            var creatorIdStr = guildEvent.Creator.Id.ToString();

                            if (isStarted)
                            {
                                // Для started имя берём из активной сессии.
                                if (GameSessionCommands._sessions.TryGetValue(guildEvent.Guild!.Id, out var guildSessions))
                                {
                                    var linked = guildSessions.Values.FirstOrDefault(s => s.EventId == guildEvent.Id && !s.IsStopped);
                                    if (linked != null && !string.IsNullOrWhiteSpace(linked.MasterName))
                                        displayName = linked.MasterName;
                                }
                            }

                            if (displayName == null
                                && serverConfigs != null
                                && serverConfigs.TryGetValue(guildEvent.Guild!.Id, out var sc)
                                && sc.MasterNameMap?.TryGetValue(creatorIdStr, out var mapped) == true
                                && !string.IsNullOrWhiteSpace(mapped))
                            {
                                displayName = mapped;
                            }

                            displayName ??= guildEvent.Creator.Username;

                            sb.Append(isStarted ? "👤 Мастер: " : "👤 Создал: ").Append(displayName).Append('\n');
                        }

                        if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                        {
                            var desc = guildEvent.Description.Trim();
                            if (desc.Length > 800) desc = desc.Substring(0, 799) + "…";
                            sb.Append('\n').Append(desc).Append('\n');
                        }

                        sb.Append('\n');
                        // Блок «Изменения» для started/completed опускаем: сам факт
                        // старта/завершения уже виден в шапке. Для cancelled —
                        // показываем метку отмены в «Изменениях», чтобы пользователь
                        // видел контекст, что событие было именно отменено.
                        if (isCancelled)
                        {
                            sb.Append("✏️ Изменения:\n");
                            sb.Append("- ").Append(markMsk).Append('\n');
                        }
                        sb.Append("🔗 ").Append(eventUrl);

                        return sb.ToString();
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
            // Защита от двойного дефиса: если элемент уже начинается с "- ", не добавляем префикс ещё раз.
            var item = ConvertLocalTimesToMsk(changes[i]);
            var prefix = item.StartsWith("- ") ? string.Empty : "- ";
            if (i > 0) sb.Append('\n').Append(prefix);
            else sb.Append(prefix);
            sb.Append(item);
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
