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
                private readonly EventAnnouncementStore? _store;
                private readonly EventNotificationService _eventNotifications;
                private readonly Func<DiscordSocketClient>? _clientProvider;
                private TelegramNotifier? _telegramNotifier;
                private readonly object _telegramNotifierLock = new();

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
                /// Сеттер для TelegramNotifier — нужен, потому что Program.cs при реконнекте
                /// пересоздаёт notifier (старый dispose'ится), а EventAnnouncer живёт дольше.
                /// Без сеттера resync после реконнекта падал с ObjectDisposedException,
                /// потому что EventAnnouncer продолжал держать старый, уже disposed, notifier.
                /// </summary>
                public void SetTelegramNotifier(TelegramNotifier? notifier)
                {
                    lock (_telegramNotifierLock)
                    {
                        _telegramNotifier = notifier;
                    }
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
                            var getChannelAttempts = 0;
                            while (getChannelAttempts < 3)
                            {
                                try
                                {
                                    announceChannel = await client.GetChannelAsync(config.GeneralRGChannelID) as ITextChannel;
                                    Log($"[EVENT] AnnounceCreatedInternalAsync: GetChannelAsync returned {announceChannel?.Id.ToString() ?? "null"} (attempt {getChannelAttempts + 1}/3)");
                                    break;
                                }
                                catch (HttpRequestException httpEx) when (getChannelAttempts < 2)
                                {
                                    // DNS / TLS / TCP-timeout — ретраим с backoff. На проде ловили
                                    // "Этот хост неизвестен. (discord.com:443)" в момент между Ready
                                    // и реальной отправкой. Раньше ловилось только подстрокой
                                    // "not logged in" и без ретрая; всё остальное (DNS/HTTP) шло
                                    // на выход без записи в стор → событие терялось до рестарта.
                                    Log($"[EVENT] announce GetChannel HTTP-fail (attempt {getChannelAttempts + 1}/3) guild={guild.Id} event={guildEvent.Id}: {httpEx.Message}");
                                    await Task.Delay(TimeSpan.FromSeconds(3 * (getChannelAttempts + 1)));
                                    getChannelAttempts++;
                                }
                                catch (Exception ex) when (ex.Message?.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0 && getChannelAttempts < 2)
                                {
                                    // REST клиента иногда тупит на старте даже когда ConnectionState=Connected.
                                    Log($"[EVENT] announce GetChannel 'not logged in' (attempt {getChannelAttempts + 1}/3) guild={guild.Id} event={guildEvent.Id}: {ex.Message}");
                                    await Task.Delay(2000);
                                    getChannelAttempts++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"[EVENT] announce GetChannel outer-fail guild={guild.Id} event={guildEvent.Id}: {ex.Message}");
                        }

                        // 🩹 announcer-getchannel-no-retry / announcer-reconnect-15s-giveup:
                        // даже если announceChannel == null (giveup после 15с ожидания коннекта,
                        // или DNS-fail без успешного retry), РАНЬШЕ сохраняли минимальный entry в стор.
                        // Без этого Lifecycle.HandleCreatedAsync не видел entry, retry TryGet за 2с
                        // не помогал, reminder1h не планировался и событие пропадало. Теперь:
                        //   - Snapshot LastName/LastStartTimeUtc/LastChannelId/LastLocation/LastCover
                        //     известны уже на этом этапе (из самого SocketGuildEvent).
                        //   - AnnounceChannelId/AnnounceMessageId = 0 — заполнятся на SendMessageAsync
                        //     ниже или при следующем RebroadcastFromStoreAsync.
                        //   - LastUpdatedMark = "pending_announce" — чтобы RebuildFromStoreAsync на
                        //     рестарте увидел «событие есть в сторе, но анонс в канал не ушёл»
                        //     и дослал его.
                        if (_store != null)
                        {
                            try
                            {
                                var earlyEntry = _store.TryGet(guild.Id, guildEvent.Id) ?? new EventAnnouncementEntry
                                {
                                    GuildId = guild.Id,
                                    EventId = guildEvent.Id
                                };
                                // AnnounceChannelId/AnnounceMessageId = 0 — пока ничего не отправили.
                                earlyEntry.AnnounceChannelId = announceChannel?.Id ?? 0;
                                earlyEntry.AnnounceMessageId = 0;
                                earlyEntry.DmMessageIdsByUserId ??= new Dictionary<ulong, ulong>();
                                if (serverConfigs.TryGetValue(guild.Id, out var scEarly))
                                {
                                    earlyEntry.TelegramChatId = scEarly.TelegramChatId;
                                    earlyEntry.TelegramMessageThreadId = scEarly.TelegramMessageThreadId;
                                }
                                // Snapshot — обязательно до любых return (даже если giveup ниже).
                                earlyEntry.LastName = guildEvent.Name;
                                earlyEntry.LastDescription = guildEvent.Description;
                                earlyEntry.LastStartTimeUtc = new DateTimeOffset(DateTime.SpecifyKind(guildEvent.StartTime.UtcDateTime, DateTimeKind.Utc));
                                earlyEntry.LastEndTimeUtc = guildEvent.EndTime.HasValue
                                    ? new DateTimeOffset(DateTime.SpecifyKind(guildEvent.EndTime.Value.UtcDateTime, DateTimeKind.Utc))
                                    : (DateTimeOffset?)null;
                                earlyEntry.LastChannelId = guildEvent.Channel?.Id;
                                earlyEntry.LastLocation = guildEvent.Location;
                                earlyEntry.LastCoverImageUrl = guildEvent.GetCoverImageUrl();
                                earlyEntry.LastCreatorId = guildEvent.Creator?.Id ?? 0;
                                if (announceChannel == null)
                                {
                                    earlyEntry.LastUpdatedMark = "pending_announce";
                                }
                                _store.Upsert(earlyEntry);
                                Log($"[EVENT] AnnounceCreatedInternalAsync: ранний Upsert (announceChannel={(announceChannel != null ? announceChannel.Id.ToString() : "null")}) guild={guild.Id} event={guildEvent.Id}");
                            }
                            catch (Exception ex)
                            {
                                Log($"[EVENT] AnnounceCreatedInternalAsync: ранний _store.Upsert упал: {ex.Message}");
                            }
                        }

                        if (announceChannel == null) { Log("[EVENT] AnnounceCreatedInternalAsync: announceChannel is null, return (entry в стор уже сохранён, см. ранний Upsert)"); return; }

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

            // ─── SendMessageAsync с retry ───────────────────────────────────────────
            // DNS-fail на прод-машине (discord.com:443 → "Этот хост неизвестен") иногда
            // проходит между GetChannelAsync (использует кеш) и реальным SendMessageAsync
            // (требует HTTP). Один короткий retry через 3с лечит транзиентный сбой; если
            // и retry не помог — всё равно регистрируем entry ниже с AnnounceMessageId=0,
            // чтобы lifecycle HandleCreatedAsync мог поставить reminder1h. Без этого при
            // DNS-сбое событие пропадало из стора, reminder не уходил никогда.
            IUserMessage? announceMsg = null;
            var sendAttempts = 0;
            while (sendAttempts < 2)
            {
                try
                {
                    announceMsg = await announceChannel.SendMessageAsync(embed: embed);
                    Log($"[EVENT] announce sent discord_channel guild={guild.Id} event={guildEvent.Id} channel={announceChannel.Id} msg={announceMsg.Id}");
                    break;
                }
                catch (Exception sendEx) when (sendAttempts == 0)
                {
                    Log($"[EVENT] announce SendMessageAsync failed (attempt 1/2) guild={guild.Id} event={guildEvent.Id}: {sendEx.Message}");
                    await Task.Delay(3000);
                    sendAttempts++;
                }
                catch (Exception sendEx)
                {
                    Log($"[EVENT] announce SendMessageAsync окончательно упал (attempt 2/2) guild={guild.Id} event={guildEvent.Id}: {sendEx.Message}");
                    sendAttempts++;
                }
            }
            Log($"[EVENT] AnnounceCreatedInternalAsync: announce message sent (or failed), proceeding to telegram/dm/store");

                        // ─── Upsert в стор СРАЗУ после анонса в канале ─────────────────────
                        // Раньше Upsert стоял после Telegram и DM-цикла. Если TG или хоть
                        // один из DM бросал исключение выше своего try/catch — запись в стор
                        // не появлялась, и HandleCreatedAsync lifecycle-сервиса в Orchestrator
                        // видел entry == null, не ставил reminder1h. На следующее Updated —
                        // становилось, но меджу — пропавший reminder. Теперь фиксируем
                        // минимальный entry (channel/msg/snapshot) сразу после SendMessageAsync
                        // (audit bug #3). TG и DM-идентификаторы добавляются через
                        // UpdateEntry ниже своими try/catch — они не должны ломать нам
                        // регистрацию события в сторе.
                        int? tgMessageId = null;
                        var tgHasPhoto = false;
                        if (_store != null)
                        {
                            var entry = _store.TryGet(guild.Id, guildEvent.Id) ?? new EventAnnouncementEntry
                            {
                                GuildId = guild.Id,
                                EventId = guildEvent.Id
                            };
                            entry.AnnounceChannelId = announceChannel.Id;
                            // Если SendMessageAsync упал (DNS-fail, etc) — announceMsg==null,
                            // но AnnounceChannelId уже валидный. Кладём entry в стор с
                            // AnnounceMessageId=0, чтобы lifecycle HandleCreatedAsync мог
                            // поставить reminder1h. Без этого при сетевом сбое событие
                            // полностью пропадало из стора и reminder не уходил никогда
                            // (см. audit bug #3 + prod DNS-fail 2026-09-23).
                            entry.AnnounceMessageId = announceMsg?.Id ?? 0;
                            entry.DmMessageIdsByUserId = new Dictionary<ulong, ulong>();
                            if (serverConfigs.TryGetValue(guild.Id, out var sc2))
                            {
                                entry.TelegramChatId = sc2.TelegramChatId;
                                entry.TelegramMessageThreadId = sc2.TelegramMessageThreadId;
                            }
                            // Snapshot состояния для будущего diff.
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
                            try { _store.Upsert(entry); }
                            catch (Exception ex) { Log($"[EVENT] AnnounceCreatedInternalAsync: _store.Upsert ранний упал: {ex.Message}"); }
                        }
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

                                                var descTruncated = !string.IsNullOrWhiteSpace(guildEvent.Description)
                                                    ? guildEvent.Description.Trim().Length <= 800
                                                        ? guildEvent.Description.Trim()
                                                        : guildEvent.Description.Trim().Substring(0, 799) + "…"
                                                    : null;

                                                var tgText = BuildEventTelegramText(
                                                    header: $"📅 <b>Новое событие:</b> {guildEvent.Name}",
                                                    whenLineOrLines: new[] { $"🕒 <b>Когда:</b> {startMsk.ToString("dd.MM.yyyy HH:mm")} (по МСК)" },
                                                                                                    whereText: whereTextPlain,
                                                    whoLabel: "Создал:",
                                                    whoText: creatorName,
                                                    description: descTruncated,
                                                    changesText: null,
                                                    eventUrl: eventUrl);

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
                            // Запись уже в сторе (ранний Upsert после SendMessageAsync).
                            // Досылаем DM-идентификаторы и TG-идентификаторы через UpdateEntry —
                            // это идемпотентный путь и не должен упасть.
                            try
                            {
                                var entry = _store.TryGet(guild.Id, guildEvent.Id) ?? new EventAnnouncementEntry
                                {
                                    GuildId = guild.Id,
                                    EventId = guildEvent.Id
                                };
                                entry.DmMessageIdsByUserId = dmMap;
                                if (tgMessageId.HasValue)
                                {
                                    entry.TelegramMessageId = tgMessageId.Value;
                                    entry.TelegramHasPhoto = tgHasPhoto;
                                }
                                _store.UpdateEntry(entry);
                            }
                            catch (Exception ex)
                            {
                                Log($"[EVENT] AnnounceCreatedInternalAsync: _store.UpdateEntry (dm/tg) упал: {ex.Message}");
                            }
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
                        //
                        // ВАЖНО: не затираем LastUpdatedMark, если событие уже в
                        // финальном/started-статусе. RebuildFromStoreAsync lifecycle'а
                        // читает эту метку как «completed/cancelled → поставить cleanup24h»
                        // и «started → …». Если Update затёр бы метку на «Обновлено: …»,
                        // cleanup никогда бы не сработал (audit bug #1).
                        // Snapshot-поля (LastName, LastDescription, LastStartTimeUtc, …)
                        // обновляем в любом случае — они нужны для ресинка и диффа.
                        try
                        {
                            var existingMark = entry.LastUpdatedMark;
                            entry.LastUpdatedMark = (existingMark == "completed"
                                                 || existingMark == "cancelled"
                                                 || existingMark == "started")
                                ? existingMark
                                : updatedMark;
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

                                                var descTruncated = !string.IsNullOrWhiteSpace(guildEvent.Description)
                                                    ? guildEvent.Description.Trim().Length <= 800
                                                        ? guildEvent.Description.Trim()
                                                        : guildEvent.Description.Trim().Substring(0, 799) + "…"
                                                    : null;

                                                var tgText = BuildEventTelegramText(
                                                    header: $"📅 <b>Событие обновлено:</b> {guildEvent.Name}",
                                                    whenLineOrLines: new[] { $"🕒 <b>Когда:</b> {startMsk.ToString("dd.MM.yyyy HH:mm")} (по МСК)" },
                                                                                                    whereText: whereTextPlain,
                                                    whoLabel: "Создал:",
                                                    whoText: creatorName,
                                                    description: descTruncated,
                                                    changesText: changes.Count > 0 ? changesMsk : null,
                                                    eventUrl: eventUrl)
                                                    + $"\n\nℹ️ {updatedMarkMsk}";

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

            var whenLines = new List<string>();
            string statusHeader;

            if (isStarted)
            {
                statusHeader = $"▶️ <b>Событие началось:</b> {guildEvent.Name}";
                whenLines.Add($"🕒 <b>Начало:</b> {mskNow.ToString("dd.MM.yyyy HH:mm")} (по МСК)");
            }
            else if (isCompleted)
            {
                statusHeader = $"✅ <b>Событие завершено:</b> {guildEvent.Name}";
                var actualStartMskForTg = actualStartMsk ?? startMsk;
                                whenLines.Add($"🕒 <b>Начало:</b> {actualStartMskForTg.ToString("dd.MM.yyyy HH:mm")} (по МСК)");
                var completedForTg = completedMskParam ?? (mskNow);
                whenLines.Add($"🛑 <b>Завершение:</b> {completedForTg.ToString("dd.MM.yyyy HH:mm")} (по МСК)");
            }
            else if (isCancelled)
            {
                statusHeader = $"❌ <b>Событие отменено:</b> {guildEvent.Name}";
                whenLines.Add($"🕒 <b>Когда:</b> {startMsk.ToString("dd.MM.yyyy HH:mm")} (по МСК)");
            }
            else
            {
                statusHeader = $"📅 <b>{statusText}:</b> {guildEvent.Name}";
                whenLines.Add($"🕒 <b>Когда:</b> {startMsk.ToString("dd.MM.yyyy HH:mm")} (по МСК)");
                whenLines.Add($"⏳ <b>Статус:</b> {markMsk}");
            }

            // whoText для status:
            string? creatorNameStatus = null;
            if (guildEvent.Creator != null)
            {
                if (serverConfigs != null && serverConfigs.TryGetValue(guild.Id, out var scSt)
                    && scSt.MasterNameMap?.TryGetValue(guildEvent.Creator.Id.ToString(), out var mappedSt) == true
                    && !string.IsNullOrWhiteSpace(mappedSt))
                {
                    creatorNameStatus = mappedSt;
                }
                else
                {
                    creatorNameStatus = guildEvent.Creator.Username;
                }
            }

            var descTruncatedStatus = !string.IsNullOrWhiteSpace(guildEvent.Description)
                ? guildEvent.Description.Trim().Length <= 800
                    ? guildEvent.Description.Trim()
                    : guildEvent.Description.Trim().Substring(0, 799) + "…"
                : null;

            var changesForStatus = isCancelled ? $"- {markMsk}" : null;

            var tgText = BuildEventTelegramText(
                header: statusHeader,
                whenLineOrLines: whenLines,
                whereText: whereTextPlain,
                whoLabel: (isStarted || isCompleted || isCancelled) ? "Мастер:" : "Создал:",
                whoText: creatorNameStatus,
                description: descTruncatedStatus,
                changesText: changesForStatus,
                eventUrl: eventUrl);

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
                        var sentResult = await _telegramNotifier.SendMessageWithReasonAsync(guild.Id, tgText);
                        ok = sentResult.messageId.HasValue;
                        var sentId = sentResult.messageId;
                        if (sentId.HasValue)
                        {
                        entry.TelegramMessageId = sentId.Value;
                        entry.TelegramHasPhoto = false;
                        _store.Upsert(entry);
                        }
                        string sendOutcome;
                        if (ok) sendOutcome = "sent";
                        else if (string.IsNullOrEmpty(sentResult.error)) sendOutcome = "skipped: no-config";
                        else sendOutcome = $"fail — {sentResult.error}";
                        Log($"[EVENT] status {status} telegram {sendOutcome} guild={guild.Id} event={guildEvent.Id} msg={(sentId ?? 0)}");
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
                Log($"[EVENT] status-from-rest {status} skipped (guild not in cache) guild={entry.GuildId} event={entry.EventId}, retrying up to 15s");
                // Guild мог ещё не попасть в кэш после рестарта/ready. Ждём до 15с
                // — если за это время появится, перерисуем анонс. Если нет — выходим;
                // cleanup24h всё равно поставится через RebuildFromStoreAsync.
                // Без этого фикса guild-not-in-cache при resync completed приводит к
                // тому, что анонс остаётся со старым embed, а участники видят
                // «событие активно», хотя в Discord оно уже завершено.
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(500);
                    guild = client.GetGuild(entry.GuildId);
                    if (guild != null) break;
                }
                if (guild == null)
                {
                    Log($"[EVENT] status-from-rest {status} giveup (guild still not in cache) guild={entry.GuildId} event={entry.EventId}");
                    return;
                }
                Log($"[EVENT] status-from-rest {status} retry ok guild={entry.GuildId} event={entry.EventId}");
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
                        // Для TG — пытаемся достать имя через guild.GetTextChannelAsync; если не
                        // получилось — fallback на ID (редкий кейс, лучше показать что-то, чем
                        // ничего). Audit cosmetic #1: раньше TG-блок печатал «1399301816017096874»
                        // сырым ID-ом канала.
                        string whereText;
                        string whereTextPlain;
                        string? channelName = null;
                        if (restEvent.ChannelId.HasValue && restEvent.ChannelId.Value != 0)
            {
                            whereText = $"<#{restEvent.ChannelId.Value}>";
                            try
                            {
                                            // Сначала пробуем Rest (доступно и offline-friendly),
                                            // затем Socket (на случай если REST не успел закэшировать
                                            // канал на старте сессии — имя уже могло появиться).
                                            var ch = await client.Rest.GetChannelAsync(restEvent.ChannelId.Value);
                                            if (ch is IGuildChannel gc && !string.IsNullOrWhiteSpace(gc.Name))
                                                channelName = gc.Name;
                                            else
                                            {
                                                try
                                                {
                                                    var sockCh = await client.GetChannelAsync(restEvent.ChannelId.Value) as SocketChannel;
                                                    if (sockCh is IGuildChannel sg && !string.IsNullOrWhiteSpace(sg.Name))
                                                        channelName = sg.Name;
                                                }
                                                catch { /* имя канала недоступно — оставляем ID */ }
                                            }
                                        }
                                        catch { /* имя канала недоступно — оставляем ID */ }
                                        whereTextPlain = channelName ?? restEvent.ChannelId.Value.ToString();
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

                                        // Готовим список строк «Когда/Начало/Завершение» и шапку под единый формат.
                                        var whenLines = new List<string>();
                                        string header;

                                        if (isStarted)
                                        {
                                            header = $"▶️ <b>Событие началось:</b> {restEvent.Name}";
                                            whenLines.Add($"🕒 <b>Начало:</b> {mskNow.ToString("dd.MM.yyyy HH:mm")} (по МСК)");
                                        }
                                        else if (isCompleted)
                                        {
                                            header = $"✅ <b>Событие завершено:</b> {restEvent.Name}";
                                            var startMskActual = actualStartMsk ?? startMsk;
                                            whenLines.Add($"🕒 <b>Начало:</b> {startMskActual.ToString("dd.MM.yyyy HH:mm")} (по МСК)");
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
                                            whenLines.Add($"🛑 <b>Завершение:</b> {completedMsk.ToString("dd.MM.yyyy HH:mm")} (по МСК)");
                                        }
                                        else if (isCancelled)
                                        {
                                            header = $"❌ <b>Событие отменено:</b> {restEvent.Name}";
                                            whenLines.Add($"🕒 <b>Когда:</b> {startMsk.ToString("dd.MM.yyyy HH:mm")} (по МСК)");
                                        }
                                        else
                                        {
                                            // Fallback — никогда не сюда в норме, но пусть будет.
                                            header = $"📅 <b>{statusText}:</b> {restEvent.Name}";
                                            whenLines.Add($"🕒 <b>Когда:</b> {startMsk.ToString("dd.MM.yyyy HH:mm")} (по МСК)");
                                        }

                                        var descTruncated = !string.IsNullOrWhiteSpace(restEvent.Description)
                                            ? restEvent.Description.Trim().Length <= 800
                                                ? restEvent.Description.Trim()
                                                : restEvent.Description.Trim().Substring(0, 799) + "…"
                                            : null;

                                        var changesTruncated = isCancelled ? $"- {markMsk}" : null;

                                        var tgText = BuildEventTelegramText(
                                            header: header,
                                            whenLineOrLines: whenLines,
                                                                                    whereText: whereTextPlain,
                                            whoLabel: isStarted ? "Мастер:" : "Создал:",
                                            whoText: displayName,
                                            description: descTruncated,
                                            changesText: changesTruncated,
                                            eventUrl: eventUrl);

                    bool ok;
                    string? error = null;
                    if (entry.TelegramMessageId > 0)
                    {
                        (ok, error) = entry.TelegramHasPhoto
                            ? await _telegramNotifier.EditMessageCaptionWithDetailsAsync(entry.GuildId, entry.TelegramMessageId, tgText)
                            : await _telegramNotifier.EditMessageTextWithDetailsAsync(entry.GuildId, entry.TelegramMessageId, tgText);
                                            // ✅ bug-fix: раздельно уровни — ok это Info, fail это Warn
                                            // (особенно ObjectDisposedException, который раньше молча
                                            // проскакивал как info и не привлекал внимания).
                                            if (ok)
                                                Log($"[EVENT] status-from-rest {status} telegram ok guild={entry.GuildId} event={entry.EventId} msg={entry.TelegramMessageId}");
                                            else
                                                LogWarn($"[EVENT] status-from-rest {status} telegram fail — {error} guild={entry.GuildId} event={entry.EventId} msg={entry.TelegramMessageId}");
                                        }
                                        else
                                        {
                                            var sentResult = await _telegramNotifier.SendMessageWithReasonAsync(entry.GuildId, tgText);
                                            ok = sentResult.messageId.HasValue;
                                            var sentId = sentResult.messageId;
                                            if (sentId.HasValue)
                                            {
                                                entry.TelegramMessageId = sentId.Value;
                                                entry.TelegramHasPhoto = false;
                                            }
                                            string sendOutcome;
                                            if (ok) sendOutcome = "sent";
                                            else if (string.IsNullOrEmpty(sentResult.error)) sendOutcome = "skipped: no-config";
                                                                                        else sendOutcome = $"fail — {sentResult.error}";
                                                                                        // ok/sent/skipped — info; fail — warn (особенно
                                                                                        // для disposed-notifier, который раньше выглядел
                                                                                        // как обычное info-сообщение).
                                                                                        if (ok || sendOutcome.StartsWith("skipped"))
                                                                                            Log($"[EVENT] status-from-rest {status} telegram {sendOutcome} guild={entry.GuildId} event={entry.EventId} msg={(sentId ?? 0)}");
                                                                                        else
                                                                                            LogWarn($"[EVENT] status-from-rest {status} telegram {sendOutcome} guild={entry.GuildId} event={entry.EventId} msg={(sentId ?? 0)}");
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

                private static string Truncate(string? value, int max)
                {
                    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
                    value = value.Trim();
                    return value.Length <= max ? value : value.Substring(0, max - 1) + "…";
                }

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
                /// Warn-уровень для не-критичных ошибок публикации — например,
                /// Telegram fail с disposed-notifier или сетевой таймаут.
                /// Info скрывал их среди обычных событий, и регрессии не
                /// привлекали внимания при разборе логов.
                /// </summary>
                private static void LogWarn(string msg)
                {
                    try { BotLogger.Warn(LogCategory.Discord, msg); }
                    catch { Console.Error.WriteLine($"[LogWarn-fallback] {msg}"); }
                }

        /// <summary>
        /// Diff между двумя живыми объектами SocketGuildEvent.
        /// </summary>
        /// <summary>
        /// Сравнение текущего события с последним сохранённым snapshot.
        /// Возвращает true, если состояние идентично (нечего обновлять).
        /// Используется для подавления дублей при resync.
                ///
                /// Сравниваются «содержательные» поля анонса: имя / описание / плановое
                /// начало и конец / канал / локация / обложка.
                /// Не сравниваем Status / Creator / EntityType — статус обрабатывается
                /// в AnnounceStatusChangedInternalAsync отдельным путём; Creator поменять
                /// нельзя (это просто для информации).
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
                    // 🩹 unchanged-detector: добавили сравнение LastEndTimeUtc — раньше сдвиг
                    // только конца события (без сдвига начала) проскакивал как «unchanged»
                    // и AnnounceUpdatedInternalAsync выходил без редактирования embed.
                    if (snapshot.LastEndTimeUtc.HasValue && current.EndTime.HasValue
                        && snapshot.LastEndTimeUtc.Value.UtcDateTime != current.EndTime.Value.UtcDateTime)
                    return false;
                    if (!snapshot.LastEndTimeUtc.HasValue && current.EndTime.HasValue)
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

                    /// <summary>
                    /// Единый шаблон Telegram-сообщения об анонсе события (для create/update/
                    /// started/completed/cancelled/reminder). Все метки — жирным (HTML), ссылка
                    /// кликабельная, формат одинаков во всех путях. Это исправляет разнобой,
                    /// когда TG-блоки create/status/reminder выглядели по-разному (жирные то
                    /// были, то нет; ссылка то URL, то текст «🔗 https://...»).
                    /// </summary>
                    /// <param name="header">Шапка. Уже с эмодзи и экранированными HTML-тегами (например, «📅 Новое событие: X»).</param>
                    /// <param name="whenLineOrLines">Одна или несколько строк блока «Когда» (могут быть «Начало»/«Завершение»). Уже с жирной меткой «<b>…</b>».</param>
                    /// <param name="whereText">Канал/локация — plain text (уже с #channel-name или текстом, без экранирования).</param>
                    /// <param name="whoLabel">«Мастер:» / «Создал:» (зависит от контекста).</param>
                    /// <param name="whoText">Имя мастера/создателя, null если не нужно.</param>
                    /// <param name="description">Описание (для create/update), null если нет.</param>
                    /// <param name="changesText">Блок «Изменения» (для update), null если не нужен. Без префикса «✏️ Изменения:» — он добавляется здесь.</param>
                    /// <param name="eventUrl">Ссылка на событие.</param>
                    internal static string BuildEventTelegramText(
                        string header,
                        IEnumerable<string> whenLineOrLines,
                        string whereText,
                        string whoLabel,
                        string? whoText,
                        string? description,
                        string? changesText,
                        string eventUrl)
                    {
                        var sb = new StringBuilder();

                        // 1) Шапка + пустая строка.
                        sb.Append(header).Append('\n').Append('\n');

                        // 2) Когда / Где / Кто — жирные метки.
                        foreach (var line in whenLineOrLines)
                        {
                            if (string.IsNullOrEmpty(line)) continue;
                            sb.Append(line).Append('\n');
                        }
                        sb.Append("📍 <b>Где:</b> ").Append(whereText).Append('\n');
                        if (!string.IsNullOrWhiteSpace(whoText))
                        {
                            sb.Append("👤 <b>").Append(whoLabel).Append("</b> ").Append(whoText).Append('\n');
                        }

                        // 3) Пустая строка + описание (если есть).
                        if (!string.IsNullOrWhiteSpace(description))
                        {
                            sb.Append('\n').Append(description.Trim()).Append('\n');
                        }

                        // 4) Изменения (если есть).
                        if (!string.IsNullOrWhiteSpace(changesText))
                        {
                            sb.Append('\n').Append("✏️ <b>Изменения:</b>\n").Append(changesText).Append('\n');
                        }

                        // 5) Пустая строка + кликабельная ссылка.
                        sb.Append('\n');
                        sb.Append("🔗 <a href=\"").Append(eventUrl).Append("\">Ссылка на событие в Discord</a>");

                        return sb.ToString();
                    }
                }
            }
