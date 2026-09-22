using Discord;
using Discord.WebSocket;
using RPBot.Common;
using RPBot.Music;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>
    /// Обработчик команды /music и кнопок плеера.
    /// Все ответы пользователю — эфемерные; embed с управлением отправляется в канал.
    /// </summary>
    public class MusicCommands
    {
        private readonly LavalinkService    _lavalink;
        private readonly MusicPlaylistStore? _playlistStore;
        private readonly MusicQueueStore?   _queueStore;
        private readonly MusicStats?        _stats;
        private DiscordSocketClient _discord;
        private Timer? _progressTimer;

        // ── Кэш результатов поиска: ключ = "guildId:userId", значение = список треков + время создания + сообщение
                // Обращения в норме идут из gateway-callback'ов Discord.NET (один диспатчер),
                // но Task.Delay.ContinueWith может сработать на другом контексте — потому синхронизируем
                // по _searchCacheLock, чтобы TTL-продление не нарвалось на мутацию «уже использован/удалён».
                private readonly Dictionary<string, (List<LavalinkService.TrackSearchResult> Tracks, DateTime CreatedAt, IUserMessage? Message)> _searchCache = new();
                private readonly object _searchCacheLock = new();
                private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(2);

                private string SearchCacheKey(ulong guildId, ulong userId) => $"{guildId}:{userId}";

                private void PutSearchCache(ulong guildId, ulong userId, List<LavalinkService.TrackSearchResult> tracks, IUserMessage? message = null)
                {
                    var key = SearchCacheKey(guildId, userId);
                    lock (_searchCacheLock)
                    {
                        _searchCache[key] = (tracks, DateTime.UtcNow, message);
                        // Чистим просроченные записи
                        var now = DateTime.UtcNow;
                        foreach (var k in _searchCache.Keys.Where(k => now - _searchCache[k].CreatedAt > SearchCacheTtl).ToList())
                            _searchCache.Remove(k);
                    }

                    // Таймер: через TTL редактируем сообщение как устаревшее
                    if (message is not null)
                    {
                        _ = Task.Delay(SearchCacheTtl).ContinueWith(async _ =>
                        {
                            lock (_searchCacheLock)
                            {
                                if (!_searchCache.TryGetValue(key, out var entry)) return;        // уже использован/удалён
                                if (entry.Message?.Id != message.Id) return;                     // заменён новым поиском
                                _searchCache.Remove(key);
                            }
                            try
                            {
                                await message.ModifyAsync(m =>
                                {
                                    m.Content    = "⏳ **Результаты поиска устарели.** Повтори `/music` с новым запросом.";
                                    m.Components = new ComponentBuilder().Build();               // убираем дропдаун
                                });
                            }
                            catch { }
                        }, TaskScheduler.Default);
                    }
                }

                private List<LavalinkService.TrackSearchResult>? GetSearchCache(ulong guildId, ulong userId)
                {
                    var key = SearchCacheKey(guildId, userId);
                    lock (_searchCacheLock)
                    {
                        if (!_searchCache.TryGetValue(key, out var entry)) return null;
                        if (DateTime.UtcNow - entry.CreatedAt > SearchCacheTtl) { _searchCache.Remove(key); return null; }
                        return entry.Tracks;
                    }
                }

                private IUserMessage? GetSearchCacheMessage(ulong guildId, ulong userId)
                {
                    var key = SearchCacheKey(guildId, userId);
                    lock (_searchCacheLock)
                    {
                        if (!_searchCache.TryGetValue(key, out var entry)) return null;
                        return entry.Message;
                    }
                }

        public Action<string>? LogSink { get; set; }

        public MusicCommands(
            LavalinkService      lavalink,
            DiscordSocketClient  discord,
            MusicPlaylistStore?  playlistStore = null,
            MusicQueueStore?     queueStore    = null,
            MusicStats?          stats         = null,
            Action<string>?      logSink       = null)
        {
            _lavalink      = lavalink;
            _discord       = discord;
            _playlistStore = playlistStore;
            _queueStore    = queueStore;
            _stats         = stats;

            // Обновляем прогресс-бар каждые 5 секунд (#9)
            _progressTimer = new Timer(OnProgressTick, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            // Подключаем колбэки смены трека
            _lavalink.SetTrackCallbacks(
                onStarted: OnTrackStartedAsync,
                onEnded: OnTrackEndedAsync);

            // Авто-пауза: слушаем изменения голосового канала (#4)
            _discord.UserVoiceStateUpdated += OnVoiceStateUpdatedAsync;
            Log($"[Music] MusicCommands инициализирован, client={_discord.GetHashCode()}");
        }

        /// <summary>Переподписывает обработчик голосовых событий на новый клиент (после рестарта бота).</summary>
        public void UpdateDiscordClient(DiscordSocketClient newClient)
        {
            _discord.UserVoiceStateUpdated -= OnVoiceStateUpdatedAsync;
            _discord = newClient;
            _discord.UserVoiceStateUpdated += OnVoiceStateUpdatedAsync;
                    // Лог удалён — это была внутренняя бухгалтерия при рестарте, не нужная в run.log.
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Авто-пауза при пустом голосовом канале (#4)
        // ═══════════════════════════════════════════════════════════════════

        private async Task OnVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
        {
            try
            {
            if (user.IsBot) return;
            // Быстрый выход: канал не менялся (mute/deafen/video)
            if (before.VoiceChannel?.Id == after.VoiceChannel?.Id) return;
            var guild = (before.VoiceChannel ?? after.VoiceChannel)?.Guild;
            if (guild is null) return;
            var guildId = guild.Id;

            var state = _lavalink.GetOrCreateState(guildId);

            var botVoice = guild.GetUser(_discord.CurrentUser.Id)?.VoiceChannel;
            if (botVoice is null) return;

            // Определяем: вошёл или вышел из канала бота
            var joinedBotChannel = after.VoiceChannel?.Id == botVoice.Id;
            var leftBotChannel   = before.VoiceChannel?.Id == botVoice.Id && after.VoiceChannel?.Id != botVoice.Id;

            // Логируем только реальный вход/выход из канала бота
            if (joinedBotChannel)
                Log($"[Music] {user.Username} вошёл в '{botVoice.Name}'");
            else if (leftBotChannel)
                Log($"[Music] {user.Username} вышел из '{botVoice.Name}'");
            else
                return; // мут/анмут/перемещение в другой канал — игнорируем

            var actualUsers = guild.Users
                .Where(u => !u.IsBot && u.VoiceChannel?.Id == botVoice.Id)
                .ToList();
            var cachedCount = actualUsers.Count;

            int humans = cachedCount;
            if (leftBotChannel)
                humans = Math.Max(0, cachedCount - 1); // пользователь ещё числится в кэше
            else if (joinedBotChannel && !actualUsers.Any(u => u.Id == user.Id))
                humans = cachedCount + 1; // пользователь ещё не попал в кэш

            Log($"[Music] Людей в канале '{botVoice.Name}': {humans}");

            }
            catch (Exception ex)
            {
                Log($"[Music] OnVoiceStateUpdatedAsync ошибка: {ex.Message}");
            }
        }

        private async Task TriggerAutoPauseAsync(ulong guildId)
        {
            var state = _lavalink.GetOrCreateState(guildId);
            if (state.IsPaused)
            {
                Log($"[Music] TriggerAutoPause: уже на паузе (гильдия {guildId}), пропускаем.");
                return;
            }
            var emptySince = state.ChannelEmptySince.HasValue
                ? $"{(DateTime.UtcNow - state.ChannelEmptySince.Value):mm\\:ss}"
                : "неизвестно";
            Log($"[Music] Авто-пауза сработала (гильдия {guildId}). Канал пуст уже {emptySince}.");

            // Сбрасываем таймер паузы (он уже сработал), стоп-таймер оставляем
            state.AutoPauseTimer?.Dispose();
            state.AutoPauseTimer = null;

            await _lavalink.PauseAsync(guildId);
            state.IsPaused = true;
            // Принудительно синхронизируем со state плеера чтобы embed показал паузу
            var playerPaused = await _lavalink.IsPlayerPausedAsync(guildId);
            if (playerPaused.HasValue) state.IsPaused = playerPaused.Value;

            await UpdateNowPlayingAsync(guildId, state);

            if (state.NowPlayingChannel is not null)
            {
                var notice = await state.NowPlayingChannel.SendMessageAsync(
                    "⏸ Никого нет в канале в течение 5 минут — музыка поставлена на паузу.");
                state.AutoPauseNoticeId = notice.Id;
            }
        }

        private async Task TriggerAutoStopAsync(ulong guildId)
        {
            var state = _lavalink.GetOrCreateState(guildId);
            var emptySince = state.ChannelEmptySince.HasValue
                ? $"{(DateTime.UtcNow - state.ChannelEmptySince.Value):mm\\:ss}"
                : "неизвестно";
            Log($"[Music] Авто-стоп сработал (гильдия {guildId}). Канал пуст уже {emptySince}. Очищаем очередь и выходим.");

            // Сохраняем ссылки на сообщения до RemoveState
            var channel        = state.NowPlayingChannel;
            var pauseNoticeId  = state.AutoPauseNoticeId;
            IUserMessage? stopNotice = null;

            if (channel is not null)
                stopNotice = await channel.SendMessageAsync(
                    "⏹ Никого не было 10 минут — воспроизведение остановлено.");

            if (_queueStore is not null) await _queueStore.ClearAsync(guildId);
            await _lavalink.StopAsync(guildId);
            await DeleteNowPlayingAsync(guildId);
            _lavalink.RemoveState(guildId);

            // Удаляем оба уведомления с небольшой задержкой
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(300));
                if (channel is not null && pauseNoticeId.HasValue)
                    try { var m = await channel.GetMessageAsync(pauseNoticeId.Value); if (m is IUserMessage u) await u.DeleteAsync(); } catch { }
                if (stopNotice is not null)
                    try { await stopNotice.DeleteAsync(); } catch { }
            });
        }

        private async Task OnTrackEndedAsync(ulong guildId)
        {
            // При loop=Track Lavalink сам перезапустит — OnTrackStartedAsync вызовется снова.
            var state = _lavalink.GetOrCreateState(guildId);
            if (state.LoopMode != LoopMode.Track)
                await UpdateQueueMessageIfVisibleAsync(guildId, state);
        }

        private void OnProgressTick(object? _)
        {
            _ = Task.Run(async () =>
            {
                foreach (var (guildId, state) in _lavalink.GetAllStates())
                {
                    if (!state.NowPlayingMessageId.HasValue) continue;
                    // Синхронизируем TrackStartedAtUtc по реальной позиции плеера
                    var pos = await _lavalink.GetPositionAsync(guildId);
                    if (pos.HasValue && state.CurrentTrackDuration.HasValue)
                    {
                        state.TrackStartedAtUtc = DateTime.UtcNow - pos.Value.Elapsed;
                    }
                    // Синхронизируем IsPaused с реальным состоянием плеера (#4)
                    var playerPaused = await _lavalink.IsPlayerPausedAsync(guildId);
                    if (playerPaused.HasValue) state.IsPaused = playerPaused.Value;

                    await UpdateNowPlayingAsync(guildId, state);

                    // ── Проверка пустого голосового канала каждые 5 сек ──
                    await CheckEmptyChannelAsync(guildId, state);
                }
            });
        }

        /// <summary>
        /// Полинг: проверяем есть ли люди в голосовом канале бота.
        /// Если нет — запускаем/продлеваем таймеры авто-паузы и авто-стопа.
        /// Если есть — сбрасываем их.
        /// </summary>
        private async Task CheckEmptyChannelAsync(ulong guildId, MusicPlayerState state)
        {
            var guild = _discord.GetGuild(guildId);
            if (guild is null) return;

            var botVoice = guild.GetUser(_discord.CurrentUser.Id)?.VoiceChannel;
            if (botVoice is null) return; // бот не в канале

            var humans = guild.Users.Count(u => !u.IsBot && u.VoiceChannel?.Id == botVoice.Id);

            if (humans == 0)
            {
                // Никого нет — запускаем таймеры если ещё не запущены
                if (state.ChannelEmptySince is null)
                {
                    state.ChannelEmptySince = DateTime.UtcNow;
                    Log($"[Music] Канал '{botVoice.Name}' пуст (гильдия {guildId}) — запущен отсчёт: пауза через 5 мин, стоп через 10 мин.");

                    state.AutoPauseTimer?.Dispose();
                    state.AutoPauseTimer = new Timer(
                        _ => _ = Task.Run(() => TriggerAutoPauseAsync(guildId)),
                        null, TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);

                    state.AutoStopTimer?.Dispose();
                    state.AutoStopTimer = new Timer(
                        _ => _ = Task.Run(() => TriggerAutoStopAsync(guildId)),
                        null, TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
                }
                else
                {
                    var elapsed = DateTime.UtcNow - state.ChannelEmptySince.Value;
                    var minuteMark = (int)elapsed.TotalMinutes;
                    if (minuteMark != state.LastLoggedEmptyMinute)
                    {
                        state.LastLoggedEmptyMinute = minuteMark;
                        Log($"[Music] Канал всё ещё пуст: {elapsed:mm\\:ss} из 5:00 до паузы (гильдия {guildId})");
                    }
                }
            }
            else
            {
                // Кто-то есть — сбрасываем, если таймеры были запущены
                if (state.ChannelEmptySince is not null)
                {
                    var waited = DateTime.UtcNow - state.ChannelEmptySince.Value;
                    state.ChannelEmptySince = null;
                    state.LastLoggedEmptyMinute = -1;
                    state.AutoPauseTimer?.Dispose(); state.AutoPauseTimer = null;
                    state.AutoStopTimer?.Dispose();  state.AutoStopTimer = null;
                    Log($"[Music] Канал '{botVoice.Name}' снова занят (гильдия {guildId}), таймеры сброшены. Был пуст: {waited:mm\\:ss}");

                    // Если бот на паузе из-за авто-паузы — предложить продолжить
                    if (state.IsPaused && state.AutoPausePromptId is null && state.NowPlayingChannel is not null && state.NowPlayingMessageId.HasValue)
                    {
                        Log($"[Music] Канал снова занят после паузы ({waited:mm\\:ss}) — показываем prompt продолжения (гильдия {guildId})");
                        var components = new ComponentBuilder()
                            .WithButton("▶️ Да", "music_autopause_resume", ButtonStyle.Success)
                            .WithButton("❌ Нет", "music_autopause_skip", ButtonStyle.Danger)
                            .Build();
                        var prompt = await state.NowPlayingChannel.SendMessageAsync(
                            "👋 Кто-то вернулся! Продолжить воспроизведение?", components: components);
                        state.AutoPausePromptId = prompt.Id;
                    }
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Slash-команды
        // ═══════════════════════════════════════════════════════════════════

        public async Task HandleMusicAsync(SocketSlashCommand command)
        {
            // Эфемерный defer — только пользователь видит подтверждение
            try { await command.DeferAsync(ephemeral: true); }
            catch (Exception ex)
            {
                DeferFailureLogger.Log("Music", ex, command);
                return;
            }

            _ = Task.Run(async () =>
            {
                var action = command.Data.Options
                    .FirstOrDefault(o => o.Name == "action")?.Value as string ?? "";

                switch (action)
                {
                    case "play":    await HandlePlayAsync(command);    break;
                    case "stop":    await HandleStopAsync(command);    break;
                    case "pause":   await HandlePauseAsync(command);   break;
                    case "resume":  await HandleResumeAsync(command);  break;
                    case "skip":    await HandleSkipAsync(command);    break;
                    case "queue":   await HandleQueueAsync(command);   break;
                    case "loop":    await HandleLoopAsync(command);    break;
                    case "shuffle": await HandleShuffleAsync(command); break;
                    case "seek":    await HandleSeekAsync(command);    break;
                    case "remove":  await HandleRemoveAsync(command);  break;
                    default:
                        await command.FollowupAsync("❌ Неизвестное действие.", ephemeral: true);
                        break;
                }
            });
        }

        // ─── play ─────────────────────────────────────────────────────────

        private async Task HandlePlayAsync(SocketSlashCommand command)
        {
            var user = command.User as SocketGuildUser;
            if (user is null) { await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true); return; }

            var input = command.Data.Options.FirstOrDefault(o => o.Name == "запрос")?.Value as string ?? "";
            if (string.IsNullOrWhiteSpace(input))
            {
                await command.FollowupAsync("❌ Укажи ссылку, название трека или название плейлиста.", ephemeral: true);
                return;
            }

            // ── Случай 1: URL → воспроизвести напрямую
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                await HandlePlayUrlAsync(command, user, input);
                return;
            }

            // ── Случай 2: совпадает с именем сохранённого плейлиста → загрузить плейлист
            var guildId = user.Guild.Id;
            if (_playlistStore is not null)
            {
                var playlist = _playlistStore.FindByName(guildId, command.User.Id, input);
                if (playlist is not null)
                {
                    if (!playlist.IsPublic && playlist.OwnerId != command.User.Id)
                    {
                        await command.FollowupAsync("❌ Этот плейлист создан не вами.", ephemeral: true);
                        return;
                    }
                    // Переиспользуем логику загрузки плейлиста
                    await HandlePlaylistLoadCoreAsync(command, user, guildId, playlist, playlist.Name);
                    return;
                }
            }

            // ── Случай 3: текстовый поиск по YouTube
            var results = await _lavalink.SearchTracksAsync(input);
            if (results.Count == 0) { await command.FollowupAsync("🔍 Ничего не найдено.", ephemeral: true); return; }

            PutSearchCache(guildId, command.User.Id, results);

            var menu = new SelectMenuBuilder()
                .WithCustomId($"music_search_select:{guildId}:{command.User.Id}")
                .WithPlaceholder("🎵 Выбери трек…")
                .WithMinValues(1)
                .WithMaxValues(1);

            for (int i = 0; i < results.Count; i++)
            {
                var t = results[i];
                var label = t.Title.Length > 100 ? t.Title[..97] + "…" : t.Title;
                var desc  = $"{t.Author} · {MusicEmbedBuilder.FormatTime(t.Duration)}";
                if (desc.Length > 100) desc = desc[..97] + "…";
                menu.AddOption(label, i.ToString(), desc);
            }

            var components = new ComponentBuilder().WithSelectMenu(menu).Build();
            var sb = new StringBuilder($"🔍 **Результаты поиска по «{input}»:** ({results.Count} треков)\n");
            sb.AppendLine("*Выбор действителен 2 минуты.*");
            var sentMsg = await command.FollowupAsync(sb.ToString(), components: components, ephemeral: true);
            PutSearchCache(guildId, command.User.Id, results, sentMsg);
        }

        // Воспроизведение по прямой ссылке (вынесено из старого HandlePlayAsync)
        private async Task HandlePlayUrlAsync(SocketSlashCommand command, SocketGuildUser user, string url)
        {
            try
            {
                var result = await _lavalink.PlayRichAsync(user, url);

                if (!result.IsNewTrack && !result.IsQueued && !result.IsPlaylist)
                {
                    await command.FollowupAsync(result.Message, ephemeral: true);
                    return;
                }

                var guildId = user.Guild.Id;
                var state   = _lavalink.GetOrCreateState(guildId);

                if (result.IsPlaylist)
                {
                    // Плейлист загружен
                    var reply = await command.FollowupAsync(result.Message, ephemeral: true);
                    _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(8)); try { await reply.DeleteAsync(); } catch { } });

                    // Если бот не играл — заполняем state первым треком и показываем плеер
                    if (result.IsNewTrack || state.NowPlayingMessageId is null)
                    {
                        state.CurrentTrackTitle      = result.TrackTitle;
                        state.CurrentTrackDuration   = result.Duration;
                        state.CurrentTrackArtworkUrl = result.ArtworkUrl;
                        state.CurrentTrackUrl        = result.TrackUrl;
                        state.CurrentTrackAuthor     = result.Author;
                        state.TrackStartedAtUtc      = DateTime.UtcNow;
                        await SendOrUpdateNowPlayingAsync(command.Channel as ITextChannel, guildId, state);
                    }
                    else
                    {
                        // Уже играет — обновляем footer/очередь
                        await UpdateNowPlayingAsync(guildId, state);
                        await UpdateQueueMessageIfVisibleAsync(guildId, state);
                    }
                }
                else if (result.IsQueued)
                {
                    // "Добавлено в очередь" — коротко, исчезает через 5 сек
                    var reply = await command.FollowupAsync(result.Message, ephemeral: true);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5));
                        try { await reply.DeleteAsync(); } catch { }
                    });
                    await UpdateNowPlayingAsync(guildId, state);
                    await UpdateQueueMessageIfVisibleAsync(guildId, state);
                }
                else
                {
                    await command.FollowupAsync(result.Message, ephemeral: true);
                }

                if (result.IsNewTrack && !result.IsPlaylist)
                {
                    state.CurrentTrackTitle      = result.TrackTitle;
                    state.CurrentTrackDuration   = result.Duration;
                    state.CurrentTrackArtworkUrl = result.ArtworkUrl;
                    state.CurrentTrackUrl        = result.TrackUrl;
                    state.CurrentTrackAuthor     = result.Author;
                    state.TrackStartedAtUtc      = DateTime.UtcNow;
                    await SendOrUpdateNowPlayingAsync(command.Channel as ITextChannel, guildId, state);
                }

                // Сохраняем очередь для восстановления после перезапуска
                if (_queueStore is not null)
                {
                    var currentUrl = state.CurrentTrackUrl ?? url;
                    var queueUrls  = await _lavalink.GetQueueUrlsAsync(guildId);
                    await _queueStore.SaveAsync(guildId, currentUrl, queueUrls);
                }
            }
            catch (Exception ex)
            {
                Log($"[Music] Ошибка play: {ex.Message}");
                await command.FollowupAsync($"❌ Ошибка воспроизведения: {ex.Message}", ephemeral: true);
            }
        }

        // ─── stop ─────────────────────────────────────────────────────────

        private async Task HandleStopAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null) { await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true); return; }
            try
            {
                var msg = await _lavalink.StopAsync(guildId.Value);
                await command.FollowupAsync(msg, ephemeral: true);
                if (_queueStore is not null) await _queueStore.ClearAsync(guildId.Value);
                await DeleteNowPlayingAsync(guildId.Value);
                _lavalink.RemoveState(guildId.Value);
            }
            catch (Exception ex) { Log($"[Music] Ошибка stop: {ex.Message}"); await command.FollowupAsync($"❌ {ex.Message}", ephemeral: true); }
        }

        // ─── pause ────────────────────────────────────────────────────────

        private async Task HandlePauseAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null) { await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true); return; }
            try
            {
                var msg = await _lavalink.PauseAsync(guildId.Value);
                await command.FollowupAsync(msg, ephemeral: true);
                var state = _lavalink.GetOrCreateState(guildId.Value);
                state.IsPaused = true;
                await UpdateNowPlayingAsync(guildId.Value, state);
            }
            catch (Exception ex) { Log($"[Music] Ошибка pause: {ex.Message}"); await command.FollowupAsync($"❌ {ex.Message}", ephemeral: true); }
        }

        // ─── resume ───────────────────────────────────────────────────────

        private async Task HandleResumeAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null) { await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true); return; }
            try
            {
                var msg = await _lavalink.ResumeAsync(guildId.Value);
                await command.FollowupAsync(msg, ephemeral: true);
                var state = _lavalink.GetOrCreateState(guildId.Value);
                state.IsPaused = false;
                await UpdateNowPlayingAsync(guildId.Value, state);
            }
            catch (Exception ex) { Log($"[Music] Ошибка resume: {ex.Message}"); await command.FollowupAsync($"❌ {ex.Message}", ephemeral: true); }
        }

        // ─── skip ─────────────────────────────────────────────────────────

        private async Task HandleSkipAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null) { await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true); return; }
            try
            {
                var msg = await _lavalink.SkipAsync(guildId.Value);
                await command.FollowupAsync(msg, ephemeral: true);
                var state = _lavalink.GetOrCreateState(guildId.Value);
                await UpdateNowPlayingAsync(guildId.Value, state);
            }
            catch (Exception ex) { Log($"[Music] Ошибка skip: {ex.Message}"); await command.FollowupAsync($"❌ {ex.Message}", ephemeral: true); }
        }

        // ─── queue ────────────────────────────────────────────────────────

        private async Task HandleQueueAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null) { await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true); return; }
            try { await command.FollowupAsync(await _lavalink.GetQueueInfoAsync(guildId.Value), ephemeral: true); }
            catch (Exception ex) { Log($"[Music] Ошибка queue: {ex.Message}"); await command.FollowupAsync($"❌ {ex.Message}", ephemeral: true); }
        }

        // ─── loop ─────────────────────────────────────────────────────────

        private async Task HandleLoopAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null) { await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true); return; }

            var modeStr = command.Data.Options.FirstOrDefault(o => o.Name == "mode")?.Value as string ?? "none";
            var mode = modeStr switch
            {
                "track" => LoopMode.Track,
                "queue" => LoopMode.Queue,
                _       => LoopMode.None,
            };
            try
            {
                var msg = await _lavalink.SetLoopAsync(guildId.Value, mode);
                await command.FollowupAsync(msg, ephemeral: true);
                var state = _lavalink.GetOrCreateState(guildId.Value);
                await UpdateNowPlayingAsync(guildId.Value, state);
            }
            catch (Exception ex) { await command.FollowupAsync($"❌ {ex.Message}", ephemeral: true); }
        }

        // ─── shuffle ──────────────────────────────────────────────────────

        private async Task HandleShuffleAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null) { await command.FollowupAsync("❌ Команда доступна только на сервере.", ephemeral: true); return; }
            try
            {
                var msg = await _lavalink.ShuffleAsync(guildId.Value);
                await command.FollowupAsync(msg, ephemeral: true);
                var state = _lavalink.GetOrCreateState(guildId.Value);
                await UpdateNowPlayingAsync(guildId.Value, state);
                await UpdateQueueMessageIfVisibleAsync(guildId.Value, state);
            }
            catch (Exception ex) { await command.FollowupAsync($"❌ {ex.Message}", ephemeral: true); }
        }

        // ─── seek ─────────────────────────────────────────────────────────

        private async Task HandleSeekAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null) { await command.FollowupAsync("❌ Только на сервере.", ephemeral: true); return; }

            var timeStr = command.Data.Options.FirstOrDefault(o => o.Name == "время")?.Value as string ?? "";
            if (!TryParseTime(timeStr, out var position))
            {
                await command.FollowupAsync("❌ Неверный формат. Используй `1:30` или `90` (секунды).", ephemeral: true);
                return;
            }
            var msg = await _lavalink.SeekAsync(guildId.Value, position);
            await command.FollowupAsync(msg, ephemeral: true);
            await UpdateNowPlayingAsync(guildId.Value, _lavalink.GetOrCreateState(guildId.Value));
        }

        // ─── remove ───────────────────────────────────────────────────────

        private async Task HandleRemoveAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null) { await command.FollowupAsync("❌ Только на сервере.", ephemeral: true); return; }

            var numObj = command.Data.Options.FirstOrDefault(o => o.Name == "номер")?.Value;
            if (numObj is not long num) { await command.FollowupAsync("❌ Укажи номер трека.", ephemeral: true); return; }

            var msg = await _lavalink.RemoveFromQueueAsync(guildId.Value, (int)num);
            var state = _lavalink.GetOrCreateState(guildId.Value);
            await UpdateQueueMessageIfVisibleAsync(guildId.Value, state);
            await AutoDeleteFollowupAsync(command, msg);
        }

        private static bool TryParseTime(string s, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (s.Contains(':'))
            {
                var parts = s.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out int m) && int.TryParse(parts[1], out int sec))
                { result = TimeSpan.FromSeconds(m * 60 + sec); return true; }
                if (parts.Length == 3 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int mm) && int.TryParse(parts[2], out int ss))
                { result = TimeSpan.FromSeconds(h * 3600 + mm * 60 + ss); return true; }
                return false;
            }
            if (double.TryParse(s, out double totalSec)) { result = TimeSpan.FromSeconds(totalSec); return true; }
            return false;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Кнопки (ButtonExecuted)
        // ═══════════════════════════════════════════════════════════════════

        public async Task HandleButtonAsync(SocketMessageComponent component)
        {
            var guildId = (component.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId is null)
            {
                try { await component.DeferAsync(); }
                catch (Exception ex) { DeferFailureLogger.Log("MusicButton", ex, component, "no_guild"); }
                return;
            }

            // Модальное окно нельзя открыть после DeferAsync — обрабатываем отдельно
            if (component.Data.CustomId == "music_queue_goto")
            {
                await ButtonQueueGotoAsync(guildId.Value, component);
                return;
            }

            try { await component.DeferAsync(ephemeral: true); }
            catch (Exception ex)
            {
                DeferFailureLogger.Log("MusicButton", ex, component, component.Data.CustomId);
                return;
            }

            _ = Task.Run(async () =>
            {
                switch (component.Data.CustomId)
                {
                    case MusicEmbedBuilder.BtnPausePlay: await ButtonTogglePauseAsync(guildId.Value, component); break;
                    case MusicEmbedBuilder.BtnSkip:      await ButtonSkipAsync(guildId.Value, component);        break;
                    case MusicEmbedBuilder.BtnStop:      await ButtonStopAsync(guildId.Value, component);        break;
                    case MusicEmbedBuilder.BtnLoop:      await ButtonLoopAsync(guildId.Value, component);        break;
                    case MusicEmbedBuilder.BtnShuffle:   await ButtonShuffleAsync(guildId.Value, component);     break;
                    case MusicEmbedBuilder.BtnVolDown:   await ButtonVolumeAsync(guildId.Value, component, -10); break;
                    case MusicEmbedBuilder.BtnVolUp:     await ButtonVolumeAsync(guildId.Value, component, +10); break;
                    case MusicEmbedBuilder.BtnQueue:     await ButtonToggleQueueAsync(guildId.Value, component); break;
                    case MusicEmbedBuilder.BtnPrev:      await ButtonPrevAsync(guildId.Value, component);        break;
                    case "music_autopause_resume":       await ButtonAutoPauseResumeAsync(guildId.Value, component); break;
                    case "music_autopause_skip":         await ButtonAutoPauseSkipAsync(guildId.Value, component);  break;
                    case "music_queue_prev":             await ButtonQueuePageAsync(guildId.Value, component, -1);  break;
                    case "music_queue_next":             await ButtonQueuePageAsync(guildId.Value, component, +1);  break;
                    default:
                        if (component.Data.CustomId.StartsWith("music_search_select:"))
                            await SelectSearchPickAsync(guildId.Value, component);
                        else if (component.Data.CustomId.StartsWith("playlist_public_yes_"))
                            await ButtonPlaylistPublicAsync(component, true);
                        else if (component.Data.CustomId.StartsWith("playlist_public_no_"))
                            await ButtonPlaylistPublicAsync(component, false);
                        else if (component.Data.CustomId.StartsWith("playlist_overwrite_yes_"))
                            await ButtonPlaylistOverwriteYesAsync(component);
                        else if (component.Data.CustomId.StartsWith("playlist_overwrite_no_"))
                            await ButtonPlaylistOverwriteNoAsync(component);
                        break;
                }
            });
        }

        private async Task ButtonAutoPauseResumeAsync(ulong guildId, SocketMessageComponent component)
        {
            Log($"[Music] {component.User.Username} выбрал [Продолжить] после авто-паузы (гильдия {guildId})");
            var state = _lavalink.GetOrCreateState(guildId);
            await DeleteAutoPausePromptAsync(state);
            await _lavalink.ResumeAsync(guildId);
            state.IsPaused = false;
            await UpdateNowPlayingAsync(guildId, state);
        }

        private async Task ButtonAutoPauseSkipAsync(ulong guildId, SocketMessageComponent component)
        {
            Log($"[Music] {component.User.Username} выбрал [Нет] (не продолжать) после авто-паузы (гильдия {guildId})");
            var state = _lavalink.GetOrCreateState(guildId);
            await DeleteAutoPausePromptAsync(state);
        }

        private async Task DeleteAutoPausePromptAsync(MusicPlayerState state)
        {
            if (state.NowPlayingChannel is null) return;

            // Удаляем prompt «Продолжить воспроизведение?»
            if (state.AutoPausePromptId.HasValue)
            {
                try
                {
                    var msg = await state.NowPlayingChannel.GetMessageAsync(state.AutoPausePromptId.Value);
                    if (msg is IUserMessage uMsg) await uMsg.DeleteAsync();
                }
                catch { }
                state.AutoPausePromptId = null;
            }

            // Удаляем уведомление «5 минут — пауза»
            if (state.AutoPauseNoticeId.HasValue)
            {
                try
                {
                    var msg = await state.NowPlayingChannel.GetMessageAsync(state.AutoPauseNoticeId.Value);
                    if (msg is IUserMessage uMsg) await uMsg.DeleteAsync();
                }
                catch { }
                state.AutoPauseNoticeId = null;
            }
        }

        private async Task SelectSearchPickAsync(ulong guildId, SocketMessageComponent component)
        {
            // custom ID: music_search_select:{guildId}:{userId}
            // value: индекс выбранного трека в кэше
            var parts = component.Data.CustomId.Split(':');
            if (parts.Length < 3) return;
            if (!ulong.TryParse(parts[2], out var userId)) return;

            var user = component.User as SocketGuildUser;
            if (user is null) return;

            var selected = component.Data.Values?.FirstOrDefault();
            if (selected is null || !int.TryParse(selected, out var idx)) return;

            var cache = GetSearchCache(guildId, userId);
            if (cache is null || idx < 0 || idx >= cache.Count)
            {
                await component.FollowupAsync("⏳ Результаты поиска устарели. Повтори запрос.", ephemeral: true);
                return;
            }

            var track = cache[idx];
            // DeferAsync уже был вызван в HandleButtonAsync
            // Берём сохранённое сообщение из кэша — component.Message недоступен для эфемерных после Defer
            var cachedMsg = GetSearchCacheMessage(guildId, userId);

            var result = await _lavalink.PlayRichAsync(user, track.Url);
            var state = _lavalink.GetOrCreateState(guildId);
            if (state.NowPlayingChannel is null && component.Channel is ITextChannel tc)
                state.NowPlayingChannel = tc;

            if (result.IsNewTrack)
            {
                state.CurrentTrackTitle      = result.TrackTitle;
                state.CurrentTrackDuration   = result.Duration;
                state.CurrentTrackArtworkUrl = result.ArtworkUrl;
                state.CurrentTrackUrl        = result.TrackUrl;
                state.CurrentTrackAuthor     = result.Author;
                state.TrackStartedAtUtc      = DateTime.UtcNow;
                await SendOrUpdateNowPlayingAsync(state.NowPlayingChannel, guildId, state);
            }
            else if (result.IsQueued)
            {
                await UpdateNowPlayingAsync(guildId, state);
                await UpdateQueueMessageIfVisibleAsync(guildId, state);
            }
            else if (!string.IsNullOrEmpty(result.Message))
            {
                await component.FollowupAsync(result.Message, ephemeral: true);
                return;
            }

            // Обновляем текст через сохранённый IUserMessage — дропдаун остаётся для выбора ещё треков
            if (cachedMsg is not null)
            {
                try
                {
                    var trackLabel = track.Title.Length > 0 ? track.Title : "трек";
                    var statusText = result.IsQueued
                        ? $"📋 **{trackLabel}** добавлен в очередь. Можно выбрать ещё трек."
                        : $"▶️ **{trackLabel}** — воспроизводится. Можно добавить ещё в очередь.";
                    await cachedMsg.ModifyAsync(m => m.Content = statusText);
                }
                catch { }
            }
        }

        private async Task ButtonPlaylistPublicAsync(SocketMessageComponent component, bool makePublic)
        {
            // custom ID: playlist_public_yes_{guildId}_{userId}_{name}
            //            playlist_public_no_{guildId}_{name}
            try
            {
                var parts = component.Data.CustomId.Split('_');
                // yes: playlist_public_yes_{gid}_{uid}_{name}  → parts[3]=gid, parts[4]=uid, parts[5..]=name
                // no:  playlist_public_no_{gid}_{name}         → parts[3]=gid, parts[4..]=name
                ulong guildId, userId;
                string encodedName;
                if (makePublic)
                {
                    guildId     = ulong.Parse(parts[3]);
                    userId      = ulong.Parse(parts[4]);
                    encodedName = string.Join("_", parts[5..]);
                }
                else
                {
                    guildId     = ulong.Parse(parts[3]);
                    userId      = component.User.Id;
                    encodedName = string.Join("_", parts[4..]);
                }
                var name = Uri.UnescapeDataString(encodedName);

                if (_playlistStore is not null)
                    await _playlistStore.SetPublicAsync(guildId, userId, name, makePublic);

                var reply = makePublic
                    ? $"🌐 Плейлист **{name}** теперь **публичный** — его видят все участники сервера."
                    : $"🔒 Плейлист **{name}** остался **приватным**.";

                try { await component.Message.DeleteAsync(); } catch { }
                await SendEphemeralAutoDeleteAsync(component, reply);
            }
            catch { /* игнорируем */ }
        }

        // custom ID: playlist_overwrite_yes_{guildId}_{userId}_{encodedName}
        private async Task ButtonPlaylistOverwriteYesAsync(SocketMessageComponent component)
        {
                // parts: [0]=playlist [1]=overwrite [2]=yes [3]=guildId [4]=userId [5..]=encodedName
                var parts = component.Data.CustomId.Split('_');
                var guildId     = ulong.Parse(parts[3]);
                var userId      = ulong.Parse(parts[4]);
                var encodedName = string.Join("_", parts[5..]);
                var name        = Uri.UnescapeDataString(encodedName);

                if (userId != component.User.Id)
                {
                    await component.FollowupAsync("❌ Эта кнопка не для вас.", ephemeral: true);
                    return;
                }

                if (_playlistStore is null)
                {
                    await component.FollowupAsync("❌ Недоступно.", ephemeral: true);
                    return;
                }

                var urls = await _lavalink.GetQueueUrlsAsync(guildId);
                if (urls.Count == 0)
                {
                    await component.FollowupAsync("❌ Очередь пуста.", ephemeral: true);
                    return;
                }

                var playlist = new MusicPlaylist { Name = name, OwnerId = userId, Urls = urls, CreatedAt = DateTime.UtcNow };
                await _playlistStore.SavePlaylistAsync(guildId, userId, playlist);

                try { await component.Message.DeleteAsync(); } catch { }

                // Предложить сделать публичным
                var newEncodedName = Uri.EscapeDataString(name);
                var buttons = new ComponentBuilder()
                    .WithButton("✅ Да", $"playlist_public_yes_{guildId}_{userId}_{newEncodedName}", ButtonStyle.Success)
                    .WithButton("❌ Нет", $"playlist_public_no_{guildId}_{newEncodedName}", ButtonStyle.Secondary)
                    .Build();
                var followupMsg = await component.FollowupAsync(
                    $"💾 Плейлист **{name}** перезаписан ({urls.Count} треков).\n📢 Сделать его **публичным**?",
                    components: buttons,
                    ephemeral: true);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    try { await followupMsg.DeleteAsync(); } catch { }
                });
        }

        // custom ID: playlist_overwrite_no_{encodedName}
        private async Task ButtonPlaylistOverwriteNoAsync(SocketMessageComponent component)
        {
            var parts       = component.Data.CustomId.Split('_');
            var encodedName = string.Join("_", parts[3..]);
            try { await component.Message.DeleteAsync(); } catch { }
            await SendEphemeralAutoDeleteAsync(component, "⚠️ Поменяйте имя плейлиста.");
        }

        private async Task ButtonPrevAsync(ulong guildId, SocketMessageComponent component)
        {
            var msg = await _lavalink.PreviousAsync(guildId);
            var state = _lavalink.GetOrCreateState(guildId);
            await UpdateNowPlayingAsync(guildId, state);
            await SendEphemeralAutoDeleteAsync(component, msg);
        }

        private async Task ButtonTogglePauseAsync(ulong guildId, SocketMessageComponent component)
        {
            var state = _lavalink.GetOrCreateState(guildId);
            string msg;
            bool nowPaused;
            if (!state.IsPaused)
            {
                msg = await _lavalink.PauseAsync(guildId);
                nowPaused = true;
            }
            else
            {
                msg = await _lavalink.ResumeAsync(guildId);
                nowPaused = false;
            }
            state.IsPaused = nowPaused;
            await UpdateNowPlayingAsync(guildId, state, isPaused: nowPaused);
            await UpdateQueueMessageIfVisibleAsync(guildId, state);
            // Кратко отвечаем с автоудалением (#10)
            await SendEphemeralAutoDeleteAsync(component, msg);
        }

        private async Task ButtonSkipAsync(ulong guildId, SocketMessageComponent component)
        {
            var msg = await _lavalink.SkipAsync(guildId);
            await SendEphemeralAutoDeleteAsync(component, msg);
        }

        private async Task ButtonStopAsync(ulong guildId, SocketMessageComponent component)
        {
            var msg = await _lavalink.StopAsync(guildId);
            await component.FollowupAsync(msg, ephemeral: true);
            await DeleteNowPlayingAsync(guildId);
            _lavalink.RemoveState(guildId);
        }

        private async Task ButtonLoopAsync(ulong guildId, SocketMessageComponent component)
        {
            var next = _lavalink.GetLoopMode(guildId) switch
            {
                LoopMode.None  => LoopMode.Track,
                LoopMode.Track => LoopMode.Queue,
                _              => LoopMode.None,
            };
            var msg = await _lavalink.SetLoopAsync(guildId, next);
            var state = _lavalink.GetOrCreateState(guildId);
            await UpdateNowPlayingAsync(guildId, state);
            await UpdateQueueMessageIfVisibleAsync(guildId, state);
            await SendEphemeralAutoDeleteAsync(component, msg);
        }

        private async Task ButtonShuffleAsync(ulong guildId, SocketMessageComponent component)
        {
            var msg = await _lavalink.ShuffleAsync(guildId);
            var state = _lavalink.GetOrCreateState(guildId);
            await UpdateNowPlayingAsync(guildId, state);
            await UpdateQueueMessageIfVisibleAsync(guildId, state);
            await SendEphemeralAutoDeleteAsync(component, msg);
        }

        private async Task ButtonVolumeAsync(ulong guildId, SocketMessageComponent component, int delta)
        {
            var vol = _lavalink.GetVolume(guildId) + delta;
            await _lavalink.SetVolumeAsync(guildId, vol);
            // Обновляем embed (там показана громкость) — без лишнего сообщения (#10)
            await UpdateNowPlayingAsync(guildId, _lavalink.GetOrCreateState(guildId));
            await SendEphemeralAutoDeleteAsync(component, $"🔊 {_lavalink.GetVolume(guildId)}%", seconds: 3);
        }

        private async Task ButtonQueuePageAsync(ulong guildId, SocketMessageComponent component, int delta)
        {
            var state = _lavalink.GetOrCreateState(guildId);
            if (state.QueueMessageId is null || state.NowPlayingChannel is null) return;

            var (minPage, maxPage) = MusicEmbedBuilder.GetMasterQueuePageRange(
                state.MasterQueue, state.MasterCurrentIndex);
            state.QueuePage = Math.Clamp(state.QueuePage + delta, minPage, maxPage);

            var (embed, comps) = await BuildQueueEmbedAsync(guildId, state);
            try
            {
                var msg = await state.NowPlayingChannel.GetMessageAsync(state.QueueMessageId.Value);
                if (msg is IUserMessage uMsg) await uMsg.ModifyAsync(p => { p.Embed = embed; p.Components = comps; });
            }
            catch { state.QueueMessageId = null; }
        }

        private async Task ButtonQueueGotoAsync(ulong guildId, SocketMessageComponent component)
        {
            var modal = new ModalBuilder()
                .WithTitle("Перейти к треку")
                .WithCustomId($"music_goto_modal:{guildId}")
                .AddTextInput("Номер трека", "track_number", TextInputStyle.Short,
                    placeholder: "Например: 7", minLength: 1, maxLength: 6, required: true)
                .Build();
            await component.RespondWithModalAsync(modal);
        }

        public async Task HandleGoToModalAsync(SocketModal modal)
        {
            var parts  = modal.Data.CustomId.Split(':');
            if (parts.Length < 2 || !ulong.TryParse(parts[1], out var guildId)) return;

            var input = modal.Data.Components
                .FirstOrDefault(c => c.CustomId == "track_number")?.Value ?? "";

            if (!int.TryParse(input.Trim(), out int trackNumber) || trackNumber < 1)
            {
                await modal.RespondAsync("❌ Введи корректный номер трека.", ephemeral: true);
                return;
            }

            try { await modal.DeferAsync(ephemeral: true); }
            catch (Exception ex)
            {
                DeferFailureLogger.Log("MusicGoTo", ex, modal, modal.Data.CustomId);
                return;
            }
            var result = await _lavalink.GoToTrackNumberAsync(guildId, trackNumber);
            await modal.FollowupAsync(result, ephemeral: true);
        }

        private async Task ButtonToggleQueueAsync(ulong guildId, SocketMessageComponent component)
        {
            var state = _lavalink.GetOrCreateState(guildId);
            if (state.QueueMessageId is not null && state.NowPlayingChannel is not null)
            {
                try
                {
                    var qMsg = await state.NowPlayingChannel.GetMessageAsync(state.QueueMessageId.Value);
                    if (qMsg is IUserMessage uMsg) await uMsg.DeleteAsync();
                }
                catch { }
                state.QueueMessageId = null;
                await SendEphemeralAutoDeleteAsync(component, "📋 Список скрыт.", seconds: 3);
            }
            else
            {
                state.QueuePage = 0;
                var (embed, comps) = await BuildQueueEmbedAsync(guildId, state);
                if (state.NowPlayingChannel is not null)
                {
                    var sent = await state.NowPlayingChannel.SendMessageAsync(embed: embed, components: comps);
                    state.QueueMessageId = sent.Id;
                }
                await SendEphemeralAutoDeleteAsync(component, "📋 Список показан.", seconds: 3);
            }
        }

        /// <summary>Отправляет эфемерное сообщение и удаляет его через указанное кол-во секунд (по умолчанию 10).</summary>
        private static async Task SendEphemeralAutoDeleteAsync(SocketMessageComponent component, string text, int seconds = 10)
        {
            var msg = await component.FollowupAsync(text, ephemeral: true);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                try { await msg.DeleteAsync(); } catch { }
            });
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Публичный хук — вызывается при смене трека (из Program.cs)
        // ═══════════════════════════════════════════════════════════════════

        public async Task OnTrackStartedAsync(ulong guildId, string title, string? author,
            TimeSpan duration, string? artworkUrl, string? trackUrl)
        {
            var state = _lavalink.GetOrCreateState(guildId);

            // Синхронизируем MasterCurrentIndex по URL и при необходимости добавляем трек
            if (trackUrl is not null)
            {
                await state.MasterQueueLock.WaitAsync();
                try
                {
                    var idx = state.MasterQueue.FindIndex(
                        e => string.Equals(e.Url, trackUrl, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                    {
                        state.MasterCurrentIndex = idx;
                    }
                    else
                    {
                        // Трек не был заранее добавлен (например, одиночный /играть) — добавляем
                        var entry = new MasterTrackEntry
                        {
                            Number     = state.MasterQueue.Count + 1,
                            Title      = title,
                            Author     = author,
                            Url        = trackUrl,
                            ArtworkUrl = artworkUrl,
                            Duration   = duration,
                        };
                        state.MasterQueue.Add(entry);
                        state.MasterCurrentIndex = state.MasterQueue.Count - 1;
                    }
                }
                finally { state.MasterQueueLock.Release(); }
            }

            state.CurrentTrackTitle      = title;
            state.CurrentTrackAuthor     = author;
            state.CurrentTrackDuration   = duration;
            state.CurrentTrackArtworkUrl = artworkUrl;
            state.CurrentTrackUrl        = trackUrl;
            state.TrackStartedAtUtc      = DateTime.UtcNow;
            state.IsPaused               = false;
            state.QueuePage              = 0;

            // Статистика (#13)
            state.TracksPlayedSession++;
            if (_stats is not null) await _stats.IncrementAsync();

            await UpdateNowPlayingAsync(guildId, state);
            await UpdateQueueMessageIfVisibleAsync(guildId, state);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Now-Playing helpers
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>Публичный вход для PlaylistCommands — отправить/обновить Now Playing embed.</summary>
        public Task SendNowPlayingPublicAsync(ITextChannel? channel, ulong guildId, MusicPlayerState state)
            => SendOrUpdateNowPlayingAsync(channel, guildId, state);

        private async Task SendOrUpdateNowPlayingAsync(
            ITextChannel? channel, ulong guildId, MusicPlayerState state, bool isPaused = false)
        {
            if (channel is null) return;
            state.NowPlayingChannel = channel;

            int queueCount = Math.Max(0, state.MasterQueue.Count - state.MasterCurrentIndex - 1);
            var volume = _lavalink.GetVolume(guildId);
            var sessionCount = state.TracksPlayedSession;
            var allTimeCount = _stats?.TotalTracksAllTime ?? 0;
            var (embed, components) = MusicEmbedBuilder.Build(state, isPaused, volume, queueCount, sessionCount, allTimeCount);

            if (state.NowPlayingMessageId.HasValue)
            {
                try
                {
                    var existing = await channel.GetMessageAsync(state.NowPlayingMessageId.Value);
                    if (existing is IUserMessage uMsg)
                    {
                        await uMsg.ModifyAsync(p => { p.Embed = embed; p.Components = components; });
                        return;
                    }
                }
                catch { }
            }

            var sent = await channel.SendMessageAsync(embed: embed, components: components);
            state.NowPlayingMessageId = sent.Id;
        }

        private async Task UpdateNowPlayingAsync(ulong guildId, MusicPlayerState state, bool? isPaused = null)
        {
            if (state.NowPlayingChannel is null || !state.NowPlayingMessageId.HasValue) return;

            // Используем переданное значение или берём из state (#4)
            var paused = isPaused ?? state.IsPaused;

            int queueCount = Math.Max(0, state.MasterQueue.Count - state.MasterCurrentIndex - 1);
            var volume = _lavalink.GetVolume(guildId);
            var sessionCount = state.TracksPlayedSession;
            var allTimeCount = _stats?.TotalTracksAllTime ?? 0;
            var (embed, components) = MusicEmbedBuilder.Build(state, paused, volume, queueCount, sessionCount, allTimeCount);

            try
            {
                var existing = await state.NowPlayingChannel.GetMessageAsync(state.NowPlayingMessageId.Value);
                if (existing is IUserMessage uMsg)
                    await uMsg.ModifyAsync(p => { p.Embed = embed; p.Components = components; });
            }
            catch { }
        }

        private async Task DeleteNowPlayingAsync(ulong guildId)
        {
            var state = _lavalink.GetOrCreateState(guildId);
            if (state.NowPlayingChannel is null) return;

            foreach (var msgId in new[] { state.NowPlayingMessageId, state.QueueMessageId })
            {
                if (msgId is null) continue;
                try
                {
                    var msg = await state.NowPlayingChannel.GetMessageAsync(msgId.Value);
                    if (msg is IUserMessage uMsg) await uMsg.DeleteAsync();
                }
                catch { }
            }
            state.NowPlayingMessageId = null;
            state.QueueMessageId = null;
        }

        private async Task UpdateQueueMessageIfVisibleAsync(ulong guildId, MusicPlayerState state)
        {
            if (state.QueueMessageId is null || state.NowPlayingChannel is null) return;
            var (embed, comps) = await BuildQueueEmbedAsync(guildId, state);
            try
            {
                var msg = await state.NowPlayingChannel.GetMessageAsync(state.QueueMessageId.Value);
                if (msg is IUserMessage uMsg) await uMsg.ModifyAsync(p => { p.Embed = embed; p.Components = comps; });
            }
            catch { state.QueueMessageId = null; }
        }

        private Task<(Embed embed, MessageComponent components)> BuildQueueEmbedAsync(ulong guildId, MusicPlayerState state)
        {
            return Task.FromResult(MusicEmbedBuilder.BuildQueueEmbedFromMaster(
                state.MasterQueue, state.MasterCurrentIndex, state.LoopMode, state.QueuePage));
        }

        // ─── Вспомогательные ─────────────────────────────────────────────

        // ═══════════════════════════════════════════════════════════════════
        //  Плейлисты
        // ═══════════════════════════════════════════════════════════════════

        public async Task HandleMusicPlaylistAsync(SocketSlashCommand command)
        {
            try { await command.DeferAsync(ephemeral: true); }
            catch (Exception ex)
            {
                DeferFailureLogger.Log("MusicPlaylist", ex, command);
                return;
            }
            _ = Task.Run(async () =>
            {
            var action = command.Data.Options
                .FirstOrDefault(o => o.Name == "действие")?.Value as string ?? "";
            switch (action)
            {
                case "playlist_save":   await HandlePlaylistSaveAsync(command);   break;
                case "playlist_load":   await HandlePlaylistLoadAsync(command);   break;
                case "playlist_list":   await HandlePlaylistListAsync(command);   break;
                case "playlist_rename": await HandlePlaylistRenameAsync(command); break;
                case "playlist_access": await HandlePlaylistAccessAsync(command); break;
                case "playlist_delete": await HandlePlaylistDeleteAsync(command); break;
                default:
                    await command.FollowupAsync("❌ Неизвестное действие.", ephemeral: true);
                    break;
            }
            });
        }

        private async Task HandlePlaylistSaveAsync(SocketSlashCommand command)
        {
            var name = command.Data.Options.FirstOrDefault(o => o.Name == "название")?.Value as string;
            if (string.IsNullOrWhiteSpace(name))
            { await command.FollowupAsync("❌ Укажи название плейлиста.", ephemeral: true); return; }

            var guildId = GetGuildId(command);
            if (guildId is null || _playlistStore is null) { await command.FollowupAsync("❌ Недоступно.", ephemeral: true); return; }

            var urls = await _lavalink.GetQueueUrlsAsync(guildId.Value);
            if (urls.Count == 0) { await command.FollowupAsync("❌ Очередь пуста, нечего сохранять.", ephemeral: true); return; }

            // Если существует плейлист этого пользователя с таким именем — спросить про перезапись
            var existing = _playlistStore.FindByName(guildId.Value, command.User.Id, name);
            if (existing is not null && existing.OwnerId == command.User.Id)
            {
                var encodedName = Uri.EscapeDataString(name);
                var uid = guildId.Value;
                var confirmButtons = new ComponentBuilder()
                    .WithButton("✅ Да", $"playlist_overwrite_yes_{uid}_{command.User.Id}_{encodedName}", ButtonStyle.Danger)
                    .WithButton("❌ Нет", $"playlist_overwrite_no_{encodedName}", ButtonStyle.Secondary)
                    .Build();
                var confirmMsg = await command.FollowupAsync(
                    $"⚠️ Обнаружен плейлист с таким же названием **{name}**. Перезаписать его?",
                    components: confirmButtons,
                    ephemeral: true);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    try { await confirmMsg.DeleteAsync(); } catch { }
                });
                return;
            }

            await DoSavePlaylistAsync(command, guildId.Value, name, urls);
        }

        private async Task DoSavePlaylistAsync(SocketSlashCommand command, ulong guildId, string name, List<string> urls)
        {
            var playlist = new MusicPlaylist { Name = name, OwnerId = command.User.Id, Urls = urls, CreatedAt = DateTime.UtcNow };
            await _playlistStore!.SavePlaylistAsync(guildId, command.User.Id, playlist);

            // Prompt — сделать публичным?
            var encodedName = Uri.EscapeDataString(name);
            var buttons = new ComponentBuilder()
                .WithButton("✅ Да", $"playlist_public_yes_{guildId}_{command.User.Id}_{encodedName}", ButtonStyle.Success)
                .WithButton("❌ Нет", $"playlist_public_no_{guildId}_{encodedName}", ButtonStyle.Secondary)

                .Build();

            var saveMsg = await command.FollowupAsync(
                $"💾 Плейлист **{name}** сохранён ({urls.Count} треков).\n📢 Сделать этот плейлист **публичным** (виден всем участникам сервера)?",
                components: buttons,
                ephemeral: true);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                try { await saveMsg.DeleteAsync(); } catch { }
            });
        }

        private async Task HandlePlaylistLoadAsync(SocketSlashCommand command)
        {
            var name = command.Data.Options.FirstOrDefault(o => o.Name == "название")?.Value as string;
            if (string.IsNullOrWhiteSpace(name))
            { await command.FollowupAsync("❌ Укажи название плейлиста.", ephemeral: true); return; }

            var guildId = GetGuildId(command);
            if (guildId is null || _playlistStore is null) { await command.FollowupAsync("❌ Недоступно.", ephemeral: true); return; }

            var user = command.User as SocketGuildUser;
            if (user is null) { await command.FollowupAsync("❌ Только на сервере.", ephemeral: true); return; }

            if (user.VoiceChannel is null) { await command.FollowupAsync("❌ Войди в голосовой канал.", ephemeral: true); return; }

            var playlist = _playlistStore.FindByName(guildId.Value, command.User.Id, name);
            if (playlist is null)
            { await command.FollowupAsync($"❌ Плейлист **{name}** не найден.", ephemeral: true); return; }

            if (!playlist.IsPublic && playlist.OwnerId != command.User.Id)
            { await command.FollowupAsync("❌ Этот плейлист создан не вами. Запросите изменения параметра у автора.", ephemeral: true); return; }

            await HandlePlaylistLoadCoreAsync(command, user, guildId.Value, playlist, name);
        }

        private async Task HandlePlaylistLoadCoreAsync(
            SocketSlashCommand command,
            SocketGuildUser user,
            ulong guildId,
            Music.MusicPlaylist playlist,
            string name)
        {
            if (user.VoiceChannel is null) { await command.FollowupAsync("❌ Войди в голосовой канал.", ephemeral: true); return; }

            // Останавливаем текущее воспроизведение БЕЗ выхода из канала (если уже играло)
            await _lavalink.StopPlaybackOnlyAsync(guildId);
            await DeleteNowPlayingAsync(guildId);
            if (_queueStore is not null) await _queueStore.ClearAsync(guildId);

            var channel = command.Channel as ITextChannel;
            var state = _lavalink.GetOrCreateState(guildId);


            // ── Шаг 1: первый трек — запускаем воспроизведение (и подключаемся к каналу)
            int loaded = 0;
            LavalinkService.PlayResult? firstResult = null;
            int firstIndex = -1;

            for (int i = 0; i < playlist.Urls.Count; i++)
            {
                try
                {
                    var r = await _lavalink.PlayRichAsync(user, playlist.Urls[i]);
                    if (r.IsNewTrack || r.IsQueued || r.IsPlaylist)
                    {
                        firstResult = r;
                        firstIndex  = i;
                        loaded += r.IsPlaylist ? r.PlaylistTracksCount : 1;
                        break;
                    }
                }
                catch { /* пропускаем недоступный трек */ }
            }

            if (firstResult is null)
            {
                await command.FollowupAsync($"❌ Не удалось воспроизвести ни одного трека из **{name}**.", ephemeral: true);
                return;
            }

            // Обновляем embed сразу после старта первого трека
            if (firstResult.IsNewTrack || firstResult.IsPlaylist)
            {
                state.CurrentTrackTitle      = firstResult.TrackTitle;
                state.CurrentTrackDuration   = firstResult.Duration;
                state.CurrentTrackArtworkUrl = firstResult.ArtworkUrl;
                state.CurrentTrackUrl        = firstResult.TrackUrl;
                state.CurrentTrackAuthor     = firstResult.Author;
                state.TrackStartedAtUtc      = DateTime.UtcNow;
                await SendOrUpdateNowPlayingAsync(channel, guildId, state);
            }

            await AutoDeleteFollowupAsync(command, $"▶️ Плейлист **{name}**: воспроизвожу трек {firstIndex + 1} из {playlist.Urls.Count}…");
            Log($"[Music] Плейлист «{name}»: старт с трека {firstIndex + 1}/{playlist.Urls.Count}.");

            // ── Шаг 2: добавляем оставшиеся треки в очередь в фоне
            _ = Task.Run(async () =>
            {
                for (int i = firstIndex + 1; i < playlist.Urls.Count; i++)
                {
                    try
                    {
                        var r = await _lavalink.PlayRichAsync(user, playlist.Urls[i]);
                        if (r.IsNewTrack || r.IsQueued || r.IsPlaylist)
                            loaded += r.IsPlaylist ? r.PlaylistTracksCount : 1;
                    }
                    catch { /* пропускаем недоступный трек */ }
                }
                Log($"[Music] Плейлист «{name}»: загружено {loaded} из {playlist.Urls.Count} треков.");
            });
        }

        private async Task HandlePlaylistRenameAsync(SocketSlashCommand command)
        {
            var name    = command.Data.Options.FirstOrDefault(o => o.Name == "название")?.Value as string;
            var newName = command.Data.Options.FirstOrDefault(o => o.Name == "новое_название")?.Value as string;

            if (string.IsNullOrWhiteSpace(name))
            { await command.FollowupAsync("❌ Укажи текущее название плейлиста.", ephemeral: true); return; }
            if (string.IsNullOrWhiteSpace(newName))
            { await command.FollowupAsync("❌ Укажи новое название плейлиста.", ephemeral: true); return; }

            var guildId = GetGuildId(command);
            if (guildId is null || _playlistStore is null) { await command.FollowupAsync("❌ Недоступно.", ephemeral: true); return; }

            var pl = _playlistStore.FindByName(guildId.Value, command.User.Id, name);
            if (pl is null)
            { await command.FollowupAsync($"❌ Плейлист **{name}** не найден.", ephemeral: true); return; }
            if (pl.OwnerId != command.User.Id)
            { await command.FollowupAsync("❌ Этот плейлист создан не вами. Запросите изменения параметра у автора.", ephemeral: true); return; }

            var renamed = await _playlistStore.RenamePlaylistAsync(guildId.Value, command.User.Id, name, newName);
            await AutoDeleteFollowupAsync(command, renamed
                ? $"✏️ Плейлист **{name}** переименован в **{newName}**."
                : $"❌ Имя **{newName}** уже занято.");
        }

        private async Task HandlePlaylistAccessAsync(SocketSlashCommand command)
        {
            var name   = command.Data.Options.FirstOrDefault(o => o.Name == "название")?.Value as string;
            var access = command.Data.Options.FirstOrDefault(o => o.Name == "доступ")?.Value as string;

            if (string.IsNullOrWhiteSpace(name))
            { await command.FollowupAsync("❌ Укажи название плейлиста.", ephemeral: true); return; }
            if (string.IsNullOrWhiteSpace(access))
            { await command.FollowupAsync("❌ Укажи уровень доступа: `личный` или `публичный`.", ephemeral: true); return; }

            var guildId = GetGuildId(command);
            if (guildId is null || _playlistStore is null) { await command.FollowupAsync("❌ Недоступно.", ephemeral: true); return; }

            var pl = _playlistStore.FindByName(guildId.Value, command.User.Id, name);
            if (pl is null)
            { await command.FollowupAsync($"❌ Плейлист **{name}** не найден.", ephemeral: true); return; }
            if (pl.OwnerId != command.User.Id)
            { await command.FollowupAsync("❌ Этот плейлист создан не вами. Запросите изменения параметра у автора.", ephemeral: true); return; }

            bool isPublic = access == "public";
            await _playlistStore.SetPublicAsync(guildId.Value, command.User.Id, name, isPublic);
            var label = isPublic ? "🌐 публичным" : "🔒 личным";
            await AutoDeleteFollowupAsync(command, $"✅ Плейлист **{name}** теперь {label}.");
        }

        private async Task HandlePlaylistListAsync(SocketSlashCommand command)
        {
            var guildId = GetGuildId(command);
            if (guildId is null || _playlistStore is null) { await command.FollowupAsync("❌ Недоступно.", ephemeral: true); return; }

            var mine   = _playlistStore.GetAll(guildId.Value, command.User.Id);
            var public_ = _playlistStore.GetAllPublic(guildId.Value, command.User.Id);

            if (mine.Count == 0 && public_.Count == 0)
            { await command.FollowupAsync("📋 Нет сохранённых плейлистов.", ephemeral: true); return; }

            var sb = new StringBuilder();
            if (mine.Count > 0)
            {
                sb.AppendLine("📋 **Ваши плейлисты:**");
                foreach (var pl in mine)
                    sb.AppendLine($"• **{pl.Name}** — {pl.Urls.Count} треков (создан {pl.CreatedAt:dd.MM.yyyy}){(pl.IsPublic ? " 🌐" : "")}");
            }
            if (public_.Count > 0)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine("🌐 **Публичные плейлисты:**");
                foreach (var pl in public_)
                    sb.AppendLine($"• **{pl.Name}** — {pl.Urls.Count} треков");
            }
            await AutoDeleteFollowupAsync(command, sb.ToString(), seconds: 60);
        }

        private async Task HandlePlaylistDeleteAsync(SocketSlashCommand command)
        {
            var name = command.Data.Options.FirstOrDefault(o => o.Name == "название")?.Value as string;
            if (string.IsNullOrWhiteSpace(name))
            { await command.FollowupAsync("❌ Укажи название плейлиста.", ephemeral: true); return; }

            var guildId = GetGuildId(command);
            if (guildId is null || _playlistStore is null) { await command.FollowupAsync("❌ Недоступно.", ephemeral: true); return; }

            var deleted = await _playlistStore.DeletePlaylistAsync(guildId.Value, command.User.Id, name);
            await AutoDeleteFollowupAsync(command,
                deleted ? $"🗑️ Плейлист **{name}** удалён." : $"❌ Плейлист **{name}** не найден.");
        }

        private static ulong? GetGuildId(SocketSlashCommand command)
            => (command.Channel as SocketGuildChannel)?.Guild.Id;

        private static async Task AutoDeleteFollowupAsync(SocketSlashCommand command, string text, int seconds = 10)
        {
            var msg = await command.FollowupAsync(text, ephemeral: true);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                try { await msg.DeleteAsync(); } catch { }
            });
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Восстановление очереди после перезапуска / реконнекта (#14)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Пытается восстановить сохранённые очереди для всех гильдий.
        /// Вызывается после успешного переподключения к Lavalink.
        /// </summary>
        public async Task TryRestoreQueuesAsync()
        {
            if (_queueStore is null) return;

            var savedQueues = await _queueStore.LoadAllAsync();
            foreach (var saved in savedQueues)
            {
                try
                {
                    var guildId = saved.GuildId;
                    var guild   = _discord.GetGuild(guildId);
                    if (guild is null) continue;

                    // Найдём голосовой канал с участниками
                    SocketVoiceChannel? voiceChannel = null;
                    SocketGuildUser?    botUser       = null;
                    foreach (var vc in guild.VoiceChannels)
                    {
                        var bot = vc.GetUser(_discord.CurrentUser.Id);
                        if (bot is not null) { voiceChannel = vc; botUser = bot; break; }
                    }
                    if (voiceChannel is null || botUser is null) continue;

                    // Восстанавливаем текущий трек
                    var allUrls = new List<string>();
                    if (!string.IsNullOrWhiteSpace(saved.CurrentUrl))
                        allUrls.Add(saved.CurrentUrl);
                    allUrls.AddRange(saved.QueueUrls ?? new List<string>());

                    foreach (var url in allUrls)
                    {
                        try { await _lavalink.PlayRichAsync(botUser, url); }
                        catch { /* пропускаем недоступный трек */ }
                    }

                    Log($"[Music] Очередь восстановлена для гильдии {guildId}: {allUrls.Count} треков.");
                    await _queueStore.ClearAsync(guildId);
                }
                catch (Exception ex)
                {
                    Log($"[Music] Ошибка восстановления очереди: {ex.Message}");
                }
            }
        }

        private static readonly string _tempLogPath = System.IO.Path.Combine(
            BotConfig.ResolvePath("Logs"),
            $"MusicTemp_{DateTime.Now:yyyyMMdd}.txt");

        private void Log(string message)
        {
            BotLogger.Info(LogCategory.Music, message);
        }
    }
}
