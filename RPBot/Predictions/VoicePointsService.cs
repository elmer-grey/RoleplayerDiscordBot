using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot
{
    /// <summary>
    /// Начисляет костяшки пользователям, сидящим в ивент-голосовом канале.
    /// Поддерживает два режима таймера, между которыми можно переключаться константой.
    /// </summary>
    public class VoicePointsService
    {
        private readonly DiscordSocketClient _client;
        private readonly PointsService _points;
        private readonly Func<ulong, ServerConfig?> _getServerConfig;
            // ✅ pred-parallelization: вместо жёстко зашитого cfg.EventVoiceChannelID проверяем,
            // активно ли событие на канале — костяшки начисляются в ЛЮБОМ event-активном голосовом канале.
            private readonly Func<ulong, ulong, bool> _isActiveEventOnChannel;
                    private readonly object _shutdownLock = new();
                private int _shutdownStarted; // 0 = running, 1 = shutting down (Interlocked guard)

                private const int BasePointsPerTick = 10;
                private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);
                private static readonly TimeSpan SaveCoalesceInterval = TimeSpan.FromSeconds(30);

                private enum TimerMode
                {
                    PerUserTimer,
                    GlobalLoop
                }

                // Переключатель режима: достаточно поменять значение здесь
                private const TimerMode CurrentMode = TimerMode.PerUserTimer;

                private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, UserState>> _userStates = new();
                private readonly CancellationTokenSource _cts = new();
                private volatile bool _disposed;

                private class UserState
                {
                    public ulong VoiceChannelId { get; set; }
                    public DateTimeOffset JoinTimeUtc { get; set; }
                    public DateTimeOffset NextAwardUtc { get; set; }
                    public CancellationTokenSource? TimerCts { get; set; }
                }

                public VoicePointsService(DiscordSocketClient client, PointsService points, Func<ulong, ServerConfig?> getServerConfig, Func<ulong, ulong, bool> isActiveEventOnChannel)
                {
                    _client = client;
                    _points = points;
                    _getServerConfig = getServerConfig;
                    _isActiveEventOnChannel = isActiveEventOnChannel;

                    _client.UserVoiceStateUpdated += OnUserVoiceStateUpdatedAsync;
                    _client.GuildScheduledEventStarted += OnGuildScheduledEventStartedAsync;
                    _client.Ready += OnClientReadyAsync;

                    if (_client.ConnectionState == ConnectionState.Connected)
                    {
                        try { SeedExistingUsersInEventChannels(); } catch { }
                    }

                    if (CurrentMode == TimerMode.GlobalLoop)
                    {
                    #pragma warning disable CS0162 // Переключатель режима: код GlobalLoop-ветки выполняется только при CurrentMode == GlobalLoop
                                    _ = Task.Run(() => GlobalLoopAsync(_cts.Token));
                    #pragma warning restore CS0162
                                }
                            }

                /// <summary>
                /// Идемпотентная остановка сервиса: отменяет все таймеры, отписывается от
                /// событий Discord, освобождает CancellationTokenSource. Безопасно вызывать
                /// несколько раз (второй вызов — no-op).
                /// </summary>
                public void Shutdown()
                {
                    if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
                        return;

                    lock (_shutdownLock)
                    {
                        if (_disposed)
                            return;

                        try
                        {
                            _client.UserVoiceStateUpdated -= OnUserVoiceStateUpdatedAsync;
                            _client.GuildScheduledEventStarted -= OnGuildScheduledEventStartedAsync;
                            _client.Ready -= OnClientReadyAsync;
                        }
                        catch
                        {
                            // клиент уже отписан — это норма при перезапуске
                        }

                        foreach (var guildStates in _userStates.Values)
                        {
                            foreach (var state in guildStates.Values)
                            {
                                try { state.TimerCts?.Cancel(); } catch { }
                                try { state.TimerCts?.Dispose(); } catch { }
                            }
                            guildStates.Clear();
                        }
                        _userStates.Clear();

                        try { _cts.Cancel(); } catch { }
                        try { _cts.Dispose(); } catch { }

                        _disposed = true;
                    }
                }

        private Task OnClientReadyAsync()
        {
            try
            {
                SeedExistingUsersInEventChannels();
            }
            catch
            {
                // игнорируем ошибки первичной синхронизации
            }

            return Task.CompletedTask;
        }

        private Task OnGuildScheduledEventStartedAsync(SocketGuildEvent guildEvent)
        {
            try
            {
                if (guildEvent?.Guild == null)
                    return Task.CompletedTask;

                var guildId = guildEvent.Guild.Id;
                var cfg = _getServerConfig(guildId);
                                if (cfg == null || !cfg.PredictionsEnabled)
                    return Task.CompletedTask;

                if (guildEvent.Channel is not SocketVoiceChannel voice)
                    return Task.CompletedTask;

                                // ✅ pred-parallelization: теперь костяшки начисляются в любом event-канале.
                                if (!_isActiveEventOnChannel(guildId, voice.Id))
                                    return Task.CompletedTask;

                var guildStates = _userStates.GetOrAdd(guildId, _ => new ConcurrentDictionary<ulong, UserState>());
                foreach (var u in voice.ConnectedUsers.Where(x => !x.IsBot))
                {
                    if (!guildStates.ContainsKey(u.Id))
                    {
                        StartTrackingUser(guildId, u.Id, voice.Id, guildStates);
                    }
                }
            }
            catch
            {
                // ignore
            }

            return Task.CompletedTask;
        }

        private void SeedExistingUsersInEventChannels()
        {
            foreach (var guild in _client.Guilds)
            {
                var cfg = _getServerConfig(guild.Id);
                        if (cfg == null || !cfg.PredictionsEnabled)
                    continue;

                        // ✅ pred-parallelization: для каждой гильдии обходим ВСЕ активные события
                        // и начисляем костяшки в их каналах (раньше был только один cfg.EventVoiceChannelID).
                        var activeEventChannels = guild.Events
                            .Where(e => e.Status == GuildScheduledEventStatus.Active && e.Channel is SocketVoiceChannel)
                            .Select(e => (SocketVoiceChannel)e.Channel!)
                            .ToList();

                        var guildStates = _userStates.GetOrAdd(guild.Id, _ => new ConcurrentDictionary<ulong, UserState>());

                        foreach (var channel in activeEventChannels)
                        {
                            foreach (var user in channel.ConnectedUsers.Where(u => !u.IsBot))
                            {
                                if (guildStates.ContainsKey(user.Id))
                                    continue;

                                StartTrackingUser(guild.Id, user.Id, channel.Id, guildStates);
                            }
                        }
                    }
                }

        private async Task OnUserVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
        {
            // Игнорируем бота
            if (user.IsBot) return;

            // Быстрый выход: канал не менялся (это могло быть mute/deafen/видео)
            if (before.VoiceChannel?.Id == after.VoiceChannel?.Id) return;

            var guild = (after.VoiceChannel ?? before.VoiceChannel)?.Guild;
            if (guild == null) return;

            var guildId = guild.Id;
            var cfg = _getServerConfig(guildId);
                        if (cfg == null || !cfg.PredictionsEnabled)
                return;

                        var guildStates = _userStates.GetOrAdd(guildId, _ => new ConcurrentDictionary<ulong, UserState>());

                        // ✅ pred-parallelization: определяем, в каком канале сейчас пользователь
                        // и активен ли там event. Если активен — начисляем; если нет — удаляем из трекинга.
                        var afterChannel = after.VoiceChannel;
                        var beforeChannel = before.VoiceChannel;

                        // Пользователь ушёл из ивент-канала и не пришёл в другой ивент-канал — стопаем трекинг.
                        if (beforeChannel != null && _isActiveEventOnChannel(guildId, beforeChannel.Id))
                        {
                            // Если пользователь перешёл в другой voice-канал, который тоже event-активный —
                            // перерегистрируем на новый канал (новый NextAwardUtc).
                            if (afterChannel != null && afterChannel.Id != beforeChannel.Id && _isActiveEventOnChannel(guildId, afterChannel.Id))
                            {
                                if (guildStates.TryRemove(user.Id, out var oldState))
                                {
                                    // Bug audit-leaks #2: Cancel+Dispose — иначе CTS течёт при каждом переходе.
                                    try { oldState?.TimerCts?.Cancel(); } catch { }
                                    try { oldState?.TimerCts?.Dispose(); } catch { }
                                }
                                StartTrackingUser(guildId, user.Id, afterChannel.Id, guildStates);
                                return;
                            }

                            // Иначе — пользователь покинул ивент-канал.
                            if (guildStates.TryRemove(user.Id, out var oldState2))
                            {
                                // Bug audit-leaks #2: Cancel+Dispose — иначе CTS течёт при каждом выходе.
                                try { oldState2?.TimerCts?.Cancel(); } catch { }
                                try { oldState2?.TimerCts?.Dispose(); } catch { }
                                await LogAsync($"VOICE_TRACK_STOP guild={guildId} user={user.Id} channel={beforeChannel.Id}");
                            }
                            return;
                        }

                        // Пользователь вошёл в ивент-канал (или ранее не был в нём) — начинаем трекинг.
                        if (afterChannel != null && _isActiveEventOnChannel(guildId, afterChannel.Id))
                        {
                            if (!guildStates.ContainsKey(user.Id))
                            {
                                StartTrackingUser(guildId, user.Id, afterChannel.Id, guildStates);
                            }
                        }
                    }

        private void StartTrackingUser(ulong guildId, ulong userId, ulong eventChannelId, ConcurrentDictionary<ulong, UserState> guildStates)
        {
            var state = new UserState
            {
                VoiceChannelId = eventChannelId,
                JoinTimeUtc = DateTimeOffset.UtcNow,
                NextAwardUtc = DateTimeOffset.UtcNow + TickInterval
            };

            if (CurrentMode == TimerMode.PerUserTimer)
            {
                var cts = new CancellationTokenSource();
                state.TimerCts = cts;
                guildStates[userId] = state;
                _ = Task.Run(() => PerUserTimerLoopAsync(guildId, userId, state, cts.Token));
            }
            #pragma warning disable CS0162 // Переключатель режима: GlobalLoop-ветка закомментирована, но сохранена для повторного включения
                        else
                        {
                            guildStates[userId] = state;
                        }
            #pragma warning restore CS0162
                    }

        private async Task PerUserTimerLoopAsync(ulong guildId, ulong userId, UserState state, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var delay = state.NextAwardUtc - DateTimeOffset.UtcNow;
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

                    await Task.Delay(delay, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) break;

                    if (!_userStates.TryGetValue(guildId, out var guildStates)) break;
                    if (!guildStates.TryGetValue(userId, out var currentState)) break;

                    var cfg = _getServerConfig(guildId);
                                        if (cfg == null || !cfg.PredictionsEnabled)
                        break;

                                        // ✅ pred-parallelization: проверяем наличие активного события на канале,
                                        // за которым следим (state.VoiceChannelId). Если событие кончилось — стопаем трекинг.
                                        if (!_isActiveEventOnChannel(guildId, state.VoiceChannelId))
                                        {
                                            guildStates.TryRemove(userId, out _);
                                            break;
                                        }

                                        // Проверяем, что пользователь всё ещё в нужном голосовом канале
                                        var socketGuild = _client.GetGuild(guildId);
                                        var guildUser = socketGuild?.GetUser(userId);
                                        if (guildUser?.VoiceChannel == null || guildUser.VoiceChannel.Id != state.VoiceChannelId)
                                        {
                                            guildStates.TryRemove(userId, out _);
                                            break;
                                        }

                    var amount = CalculatePointsForUser(guildUser, BasePointsPerTick);
                    _points.Add(guildId, userId, amount);
                    var balance = _points.GetBalance(guildId, userId);
                    await _points.SaveAsync().ConfigureAwait(false);

                    currentState.NextAwardUtc = DateTimeOffset.UtcNow + TickInterval;
                }
            }
            catch (TaskCanceledException)
            {
                // нормальное завершение
            }
            catch
            {
                // подавляем ошибки, чтобы не уронить сервис
            }
        }

        private async Task GlobalLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var g in _userStates)
                    {
                        var guildId = g.Key;
                        var guildStates = g.Value;

                        var cfg = _getServerConfig(guildId);
                                                if (cfg == null || !cfg.PredictionsEnabled)
                            continue;

                        var socketGuild = _client.GetGuild(guildId);
                        if (socketGuild == null) continue;

                        foreach (var kv in guildStates.ToArray())
                        {
                            var userId = kv.Key;
                            var state = kv.Value;

                            if (now < state.NextAwardUtc)
                                continue;

                                                    // ✅ pred-parallelization: трекаем канал по state, а не по cfg.
                                                    // Также проверяем, что событие всё ещё активно на этом канале.
                                                    if (!_isActiveEventOnChannel(guildId, state.VoiceChannelId))
                                                    {
                                                        guildStates.TryRemove(userId, out _);
                                                        continue;
                                                    }

                                                    var guildUser = socketGuild.GetUser(userId);
                                                    if (guildUser?.VoiceChannel == null || guildUser.VoiceChannel.Id != state.VoiceChannelId)
                                                    {
                                                        guildStates.TryRemove(userId, out _);
                                                        continue;
                                                    }

                            var amount = CalculatePointsForUser(guildUser, BasePointsPerTick);
                            _points.Add(guildId, userId, amount);
                            var balance = _points.GetBalance(guildId, userId);
                            await _points.SaveAsync().ConfigureAwait(false);

                            state.NextAwardUtc = now + TickInterval;
                            guildStates[userId] = state;
                        }
                    }
                }
                catch
                {
                    // подавляем ошибки цикла
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private long CalculatePointsForUser(SocketGuildUser user, int baseAmount)
        {
            var config = _getServerConfig(user.Guild.Id);
            var hasMasterRole = config?.MasterRoleId.HasValue == true && config.MasterRoleId.Value != 0 &&
                user.Roles.Any(r => r.Id == config.MasterRoleId.Value);
            if (!hasMasterRole)
                return baseAmount;

            return (long)(baseAmount * 1.5);
        }

        private Task LogAsync(string message)
                {
                    BotLogger.Info(LogCategory.Points, message);
                    return Task.CompletedTask;
                }
            }
        }