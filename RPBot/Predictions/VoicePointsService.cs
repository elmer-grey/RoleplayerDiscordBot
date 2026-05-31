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

        private const int BasePointsPerTick = 10;
        private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

        private enum TimerMode
        {
            PerUserTimer,
            GlobalLoop
        }

        // Переключатель режима: достаточно поменять значение здесь
        private const TimerMode CurrentMode = TimerMode.PerUserTimer;

        private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, UserState>> _userStates = new();
        private readonly CancellationTokenSource _cts = new();

        private class UserState
        {
            public ulong VoiceChannelId { get; set; }
            public DateTimeOffset JoinTimeUtc { get; set; }
            public DateTimeOffset NextAwardUtc { get; set; }
            public CancellationTokenSource? TimerCts { get; set; }
        }

        public VoicePointsService(DiscordSocketClient client, PointsService points, Func<ulong, ServerConfig?> getServerConfig, string logPath)
        {
            _client = client;
            _points = points;
            _getServerConfig = getServerConfig;

            _client.UserVoiceStateUpdated += OnUserVoiceStateUpdatedAsync;
            _client.GuildScheduledEventStarted += OnGuildScheduledEventStartedAsync;
            _client.Ready += OnClientReadyAsync;

            if (_client.ConnectionState == ConnectionState.Connected)
            {
                try { SeedExistingUsersInEventChannels(); } catch { }
            }

            if (CurrentMode == TimerMode.GlobalLoop)
            {
                _ = Task.Run(() => GlobalLoopAsync(_cts.Token));
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
                if (cfg == null || !cfg.PredictionsEnabled || cfg.EventVoiceChannelID == 0)
                    return Task.CompletedTask;

                if (guildEvent.Channel is not SocketVoiceChannel voice)
                    return Task.CompletedTask;

                if (voice.Id != cfg.EventVoiceChannelID)
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
                if (cfg == null || !cfg.PredictionsEnabled || cfg.EventVoiceChannelID == 0)
                    continue;

                var eventVoice = guild.GetVoiceChannel(cfg.EventVoiceChannelID);
                if (eventVoice == null)
                    continue;

                var guildStates = _userStates.GetOrAdd(guild.Id, _ => new ConcurrentDictionary<ulong, UserState>());

                foreach (var user in eventVoice.ConnectedUsers.Where(u => !u.IsBot))
                {
                    if (guildStates.ContainsKey(user.Id))
                        continue;

                    StartTrackingUser(guild.Id, user.Id, cfg.EventVoiceChannelID, guildStates);
                }
            }
        }

        private async Task OnUserVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
        {
            // Игнорируем бота
            if (user.IsBot) return;

            var guild = (after.VoiceChannel ?? before.VoiceChannel)?.Guild;
            if (guild == null) return;

            var guildId = guild.Id;
            var cfg = _getServerConfig(guildId);
            if (cfg == null || !cfg.PredictionsEnabled || cfg.EventVoiceChannelID == 0)
                return;

            var eventChannelId = cfg.EventVoiceChannelID;
            var guildStates = _userStates.GetOrAdd(guildId, _ => new ConcurrentDictionary<ulong, UserState>());

            // Пользователь покинул ивент-канал
            if (after.VoiceChannel == null || after.VoiceChannel.Id != eventChannelId)
            {
                if (guildStates.TryRemove(user.Id, out var oldState))
                {
                    oldState.TimerCts?.Cancel();
                    await LogAsync($"VOICE_TRACK_STOP guild={guildId} user={user.Id} channel={eventChannelId}");
                }
                return;
            }

            // Пользователь зашёл в ивент-канал
            if (!guildStates.ContainsKey(user.Id))
            {
                StartTrackingUser(guildId, user.Id, eventChannelId, guildStates);
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
            else
            {
                guildStates[userId] = state;
            }
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
                    if (cfg == null || !cfg.PredictionsEnabled || cfg.EventVoiceChannelID == 0)
                        break;

                    // Проверяем, что пользователь всё ещё в нужном голосовом канале
                    var socketGuild = _client.GetGuild(guildId);
                    var guildUser = socketGuild?.GetUser(userId);
                    if (guildUser?.VoiceChannel == null || guildUser.VoiceChannel.Id != cfg.EventVoiceChannelID)
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
                        if (cfg == null || !cfg.PredictionsEnabled || cfg.EventVoiceChannelID == 0)
                            continue;

                        var socketGuild = _client.GetGuild(guildId);
                        if (socketGuild == null) continue;

                        foreach (var kv in guildStates.ToArray())
                        {
                            var userId = kv.Key;
                            var state = kv.Value;

                            if (now < state.NextAwardUtc)
                                continue;

                            var guildUser = socketGuild.GetUser(userId);
                            if (guildUser?.VoiceChannel == null || guildUser.VoiceChannel.Id != cfg.EventVoiceChannelID)
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

        public void Shutdown()
        {
            _cts.Cancel();
        }
    }
}