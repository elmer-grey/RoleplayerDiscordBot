using Discord;
using Discord.WebSocket;
using RPBot.Predictions;
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
    /// Управляет жизненным циклом прогнозов: создание, ставки, закрытие ставок по времени и завершение.
    /// </summary>
    public class PredictionService
    {
        private readonly DiscordSocketClient _client;
        private readonly PointsService _points;
        private readonly string _logPath;
        private readonly ConcurrentDictionary<ulong, ActivePrediction> _active = new();
        private readonly ConcurrentDictionary<ulong, ISocketMessageChannel> _activeChannels = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly string _stateFilePath;
        private readonly SemaphoreSlim _stateFileGate = new(1, 1);

        // ✅ НОВОЕ: История прогнозов, статистика и достижения
        private PredictionHistoryStore _history = new();
        private readonly ConcurrentDictionary<ulong, Dictionary<ulong, UserPredictionStats>> _userStats = new(); // guildId -> userId -> stats
        private readonly ConcurrentDictionary<ulong, List<UserAchievement>> _userAchievements = new(); // guildId -> achievements
        private readonly string _historyFilePath;
        private readonly string _statsFilePath;
        private readonly string _achievementsFilePath;
        private readonly SemaphoreSlim _historyGate = new(1, 1);
        private readonly SemaphoreSlim _statsGate = new(1, 1);
        private readonly SemaphoreSlim _achievementsGate = new(1, 1);

        public PredictionService(DiscordSocketClient client, PointsService points, string logPath)
        {
            _client = client;
            _points = points;
            _logPath = logPath;
            // State file for persisting active predictions across restarts
            try
            {
                var settingsDir = BotConfig.ResolvePath(BotConfig.SettingsFolderName);
                Directory.CreateDirectory(settingsDir);
                _stateFilePath = Path.Combine(settingsDir, "predictions_state.json");
                _historyFilePath = Path.Combine(settingsDir, "predictions_history.json");
                _statsFilePath = Path.Combine(settingsDir, "predictions_stats.json");
                _achievementsFilePath = Path.Combine(settingsDir, "predictions_achievements.json");
            }
            catch
            {
                _stateFilePath = Path.Combine(AppContext.BaseDirectory, "predictions_state.json");
                _historyFilePath = Path.Combine(AppContext.BaseDirectory, "predictions_history.json");
                _statsFilePath = Path.Combine(AppContext.BaseDirectory, "predictions_stats.json");
                _achievementsFilePath = Path.Combine(AppContext.BaseDirectory, "predictions_achievements.json");
            }

            // Загружаем историю, статистику и достижения
            _ = Task.Run(() => LoadHistoryAsync());
            _ = Task.Run(() => LoadStatsAsync());
            _ = Task.Run(() => LoadAchievementsAsync());

            // Фоновая задача для авто-блокировки ставок по истечении времени
            _ = Task.Run(() => MonitorLoopAsync(_cts.Token));

            // Попробуем загрузить ранее сохранённые прогнозы после готовности клиента,
            // иначе кэш каналов/гильдий может быть пустым и мы получим ложные RESTORE_FAIL.
            _client.Ready += OnClientReadyForRestore;

            // ✅ БАГ 6: Обновляем сообщения при отключении бота
            _client.Disconnected += OnClientDisconnected;
        }

        private Task OnClientDisconnected(Exception exception)
        {
            // Обновляем все активные прогнозы с пометкой "Бот неактивен"
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var kv in _active.ToArray())
                    {
                        var p = kv.Value;
                        if (!p.IsResolved)
                        {
                            await UpdateMessageAsync(p, showLocked: p.IsLocked, botOffline: true).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    await PredictionErrorLogger.LogAsync("OnClientDisconnected", ex, "Failed to update predictions on disconnect").ConfigureAwait(false);
                }
            });
            return Task.CompletedTask;
        }

        private Task OnClientReadyForRestore()
        {
            _client.Ready -= OnClientReadyForRestore;
            _ = Task.Run(() => LoadStateAsync());
            return Task.CompletedTask;
        }

        private class PersistentPrediction
        {
            public ulong GuildId { get; set; }
            public ulong CreatorId { get; set; }
            public ulong ChannelId { get; set; }
            public ulong MessageId { get; set; }
            public string Title { get; set; } = string.Empty;

            // ✅ Новое поле для поддержки N исходов
            public List<PredictionOutcome>? Outcomes { get; set; }

            // Старые поля для обратной совместимости
            public PredictionOutcome? Outcome1 { get; set; }
            public PredictionOutcome? Outcome2 { get; set; }

            public DateTimeOffset CreatedAtUtc { get; set; }
            public DateTimeOffset BetsCloseAtUtc { get; set; }
            public bool IsLocked { get; set; }
            public bool IsResolved { get; set; }
            public int? WinningOutcomeId { get; set; }
            public Dictionary<ulong, PredictionBet> Bets { get; set; } = new();
        }

        private async Task SaveStateAsync()
        {
            await _stateFileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var snapshot = _active.ToDictionary(kv => kv.Key, kv => new PersistentPrediction
                {
                    GuildId = kv.Value.GuildId,
                    CreatorId = kv.Value.CreatorId,
                    ChannelId = kv.Value.ChannelId,
                    MessageId = kv.Value.MessageId,
                    Title = kv.Value.Title,
                    Outcomes = kv.Value.Outcomes, // ✅ Сохраняем новый формат
                    CreatedAtUtc = kv.Value.CreatedAtUtc,
                    BetsCloseAtUtc = kv.Value.BetsCloseAtUtc,
                    IsLocked = kv.Value.IsLocked,
                    IsResolved = kv.Value.IsResolved,
                    WinningOutcomeId = kv.Value.WinningOutcomeId,
                    Bets = kv.Value.Bets
                });

                var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                var json = System.Text.Json.JsonSerializer.Serialize(snapshot, options);
                await File.WriteAllTextAsync(_stateFilePath, json).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("SaveStateAsync", ex).ConfigureAwait(false);
            }
            finally
            {
                _stateFileGate.Release();
            }
        }

        public async Task<bool> EnsureStateFileAsync()
        {
            if (File.Exists(_stateFilePath)) return false;
            await SaveStateAsync().ConfigureAwait(false);
            if (!File.Exists(_stateFilePath))
            {
                var dir = Path.GetDirectoryName(_stateFilePath) ?? AppContext.BaseDirectory;
                Directory.CreateDirectory(dir);
                await _stateFileGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await File.WriteAllTextAsync(_stateFilePath, "{}", Encoding.UTF8).ConfigureAwait(false);
                }
                finally
                {
                    _stateFileGate.Release();
                }
            }
            return true;
        }

        private async Task LoadStateAsync()
        {
            await _stateFileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(_stateFilePath)) return;
                var json = await File.ReadAllTextAsync(_stateFilePath).ConfigureAwait(false);
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<ulong, PersistentPrediction>>(json);
                if (dict == null) return;

                foreach (var kv in dict)
                {
                    try
                    {
                        var p = kv.Value;

                        // Skip already resolved/cancelled items.
                        if (p.IsResolved)
                            continue;

                        var ap = new ActivePrediction
                        {
                            GuildId = p.GuildId,
                            CreatorId = p.CreatorId,
                            ChannelId = p.ChannelId,
                            MessageId = p.MessageId,
                            Title = p.Title,
                            CreatedAtUtc = p.CreatedAtUtc,
                            BetsCloseAtUtc = p.BetsCloseAtUtc,
                            IsLocked = p.IsLocked,
                            IsResolved = p.IsResolved,
                            WinningOutcomeId = p.WinningOutcomeId,
                            Bets = p.Bets ?? new Dictionary<ulong, PredictionBet>()
                        };

                        // ✅ БАГ 7: Логируем для диагностики потери ставок
                        await LogAsync($"RESTORE_DEBUG guild={p.GuildId} betsFromFile={p.Bets?.Count ?? 0} betsInAP={ap.Bets.Count}").ConfigureAwait(false);

                        // ✅ Обновлено: загрузка исходов (поддержка старого и нового формата)
                        if (p.Outcomes != null && p.Outcomes.Count > 0)
                        {
                            // Новый формат: используем Outcomes
                            ap.Outcomes = p.Outcomes;
                        }
                        else
                        {
                            // Старый формат: используем Outcome1 и Outcome2
                            ap.Outcomes.Add(p.Outcome1 ?? new PredictionOutcome { Id = 1 });
                            ap.Outcomes.Add(p.Outcome2 ?? new PredictionOutcome { Id = 2 });
                        }

                        // Normalize: recompute totals from bets to avoid zeroed pools after restart.
                        foreach (var outcome in ap.Outcomes)
                        {
                            outcome.TotalStake = 0;
                        }

                        foreach (var b in ap.Bets.Values)
                        {
                            var outcome = ap.GetOutcomeById(b.OutcomeId);
                            if (outcome != null)
                            {
                                outcome.TotalStake += b.Amount;
                            }
                        }

                        var restoredOutcomesList = string.Join(", ", ap.Outcomes.Select(o => $"{o.Id}: {o.Name}"));
                        ap.UseCompactOutcomeLabels = $"Исход ({restoredOutcomesList})".Length > 45;
                        ap.UseInlineOutcomeFields = ap.Outcomes.Count <= 3
                            && ap.Outcomes.All(o => $"📊 Исход {o.Id}: {o.Name}".Length <= 256);

                        // Validate message existence. If the original message is gone, auto-cancel and refund.
                        ISocketMessageChannel? ch = _client.GetChannel(p.ChannelId) as ISocketMessageChannel
                            ?? _client.GetGuild(p.GuildId)?.GetChannel(p.ChannelId) as ISocketMessageChannel;

                        // Cache might not be warm yet after reconnect/restart; try async fetch once.
                        if (ch == null)
                        {
                            try
                            {
                                var fetched = await _client.GetChannelAsync(p.ChannelId).ConfigureAwait(false);
                                ch = fetched as ISocketMessageChannel;
                            }
                catch (Exception ex)
                {
                    await PredictionErrorLogger.LogAsync("AutoCancelRestoredPredictionAsync:NotifyChannel", ex, $"guild={p.GuildId} channel={p.ChannelId}").ConfigureAwait(false);
                }
                        }

                        if (ch == null || p.MessageId == 0)
                        {
                            await LogAsync($"RESTORE_FAIL guild={p.GuildId} reason=channel_missing channelId={p.ChannelId} messageId={p.MessageId} bets={ap.Bets.Count}").ConfigureAwait(false);
                            await AutoCancelRestoredPredictionAsync(ap, cancelReason: "Восстановление невозможно: сообщение прогноза не найдено. Прогноз отменён, ставки возвращены.").ConfigureAwait(false);

                            // Remove from persisted state so it doesn't keep re-triggering on next restart.
                            try
                            {
                                dict.Remove(kv.Key);
                            }
                            catch { }

                            continue;
                        }

                        try
                        {
                            var msg = await ch.GetMessageAsync(p.MessageId).ConfigureAwait(false);
                            if (msg == null)
                            {
                                await LogAsync($"RESTORE_FAIL guild={p.GuildId} reason=message_missing channelId={p.ChannelId} messageId={p.MessageId} bets={ap.Bets.Count}").ConfigureAwait(false);
                                await AutoCancelRestoredPredictionAsync(ap, cancelReason: "Восстановление невозможно: сообщение прогноза удалено. Прогноз отменён, ставки возвращены.").ConfigureAwait(false);

                                try { dict.Remove(kv.Key); } catch { }
                                continue;
                            }
                        }
                        catch
                        {
                            await LogAsync($"RESTORE_FAIL guild={p.GuildId} reason=message_check_error channelId={p.ChannelId} messageId={p.MessageId} bets={ap.Bets.Count}").ConfigureAwait(false);
                            await AutoCancelRestoredPredictionAsync(ap, cancelReason: "Восстановление невозможно: ошибка проверки сообщения. Прогноз отменён, ставки возвращены.").ConfigureAwait(false);
                            continue;
                        }

                        // Ensure Sync is new
                        // Add to active dictionaries
                        _active[p.GuildId] = ap;

                        _activeChannels[p.GuildId] = ch;

                        await LogAsync($"RESTORE_OK guild={p.GuildId} channelId={p.ChannelId} messageId={p.MessageId} bets={ap.Bets.Count} pool={ap.TotalPool}").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await PredictionErrorLogger.LogAsync("LoadStateAsync:entry", ex, $"guild={kv.Key}").ConfigureAwait(false);
                    }
                }

                _ = Task.Run(async () => await SaveStateAsync().ConfigureAwait(false));

                // If we removed any broken entries, persist the cleaned dict too.
                try
                {
                    var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                    var cleanedJson = System.Text.Json.JsonSerializer.Serialize(dict, options);
                    await File.WriteAllTextAsync(_stateFilePath, cleanedJson).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await PredictionErrorLogger.LogAsync("LoadStateAsync:saveCleanedState", ex).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("LoadStateAsync", ex).ConfigureAwait(false);
            }
            finally
            {
                _stateFileGate.Release();
            }
        }

        private async Task AutoCancelRestoredPredictionAsync(ActivePrediction p, string cancelReason)
        {
            try
            {
                // Refund
                await p.Sync.WaitAsync().ConfigureAwait(false);
                try
                {
                    foreach (var bet in p.Bets.Values)
                        _points.Add(p.GuildId, bet.UserId, bet.Amount);
                }
                finally
                {
                    p.Sync.Release();
                }

                // Try to notify in channel
                try
                {
                    var channel = _client.GetChannel(p.ChannelId) as ISocketMessageChannel
                        ?? _client.GetGuild(p.GuildId)?.GetChannel(p.ChannelId) as ISocketMessageChannel;
                    if (channel != null)
                    {
                        var cancelEmbed = BuildCancelEmbed(p, cancelReason);
                        await channel.SendMessageAsync(embed: cancelEmbed).ConfigureAwait(false);
                    }
                }
                            catch (Exception ex)
                            {
                                await PredictionErrorLogger.LogAsync("LoadStateAsync:GetChannelAsync", ex, $"guild={p.GuildId} channel={p.ChannelId}").ConfigureAwait(false);
                            }

                await LogAsync($"AUTO_CANCEL_RESTORE guild={p.GuildId} channelId={p.ChannelId} bets={p.Bets.Count} reason='{cancelReason}'").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("AutoCancelRestoredPredictionAsync", ex, $"guild={p.GuildId} channel={p.ChannelId}").ConfigureAwait(false);
            }
        }

        public Task HandleSlashCommand(SocketSlashCommand command)
        {
            return command.RespondAsync("Команда prediction временно недоступна.", ephemeral: true);
        }

        public ActivePrediction? GetActive(ulong guildId)
        {
            _active.TryGetValue(guildId, out var p);
            return p;
        }

        public async Task<(bool ok, string error, ActivePrediction? prediction)> CreateAsync(
            ulong guildId,
            ulong creatorId,
            ISocketMessageChannel targetChannel,
            string title,
            string outcome1Name,
            string outcome2Name,
            TimeSpan duration)
        {
            // Используем новую перегрузку с 2 исходами
            return await CreateAsync(guildId, creatorId, targetChannel, title, new[] { outcome1Name, outcome2Name }, duration).ConfigureAwait(false);
        }

        /// <summary>
        /// ✅ НОВАЯ ПЕРЕГРУЗКА: Создание прогноза с произвольным количеством исходов
        /// </summary>
        public async Task<(bool ok, string error, ActivePrediction? prediction)> CreateAsync(
            ulong guildId,
            ulong creatorId,
            ISocketMessageChannel targetChannel,
            string title,
            string[] outcomeNames,
            TimeSpan duration)
        {
            if (_active.ContainsKey(guildId))
                return (false, "Уже есть активный прогноз на этом сервере.", null);

            if (outcomeNames == null || outcomeNames.Length < 2)
                return (false, "Должно быть минимум 2 исхода.", null);

            if (outcomeNames.Length > 10)
                return (false, "Максимум 10 исходов.", null);

            if (duration <= TimeSpan.Zero)
                duration = TimeSpan.FromMinutes(1);

            if (targetChannel == null)
            {
                await LogAsync($"CREATE_FAIL_CHANNEL guild={guildId} creator={creatorId} channelId=0 rawType=null rawName='(без имени)'");
                return (false, "Не удалось найти канал для создания прогноза.", null);
            }

            var channelId = targetChannel.Id;
            var now = DateTimeOffset.UtcNow;
            var closeAt = now + duration;

            var guild = _client.GetGuild(guildId);
            var guildName = guild?.Name ?? "(без_названия)";
            var creatorGuildUser = guild?.GetUser(creatorId);
            var creatorUser = creatorGuildUser ?? _client.GetUser(creatorId);
            var creatorName = creatorGuildUser?.DisplayName
                               ?? creatorGuildUser?.Nickname
                               ?? creatorGuildUser?.Username
                               ?? creatorUser?.Username
                               ?? creatorId.ToString();
            var channelName = targetChannel is IChannel c ? c.Name ?? "(без имени)" : "(без имени)";

            var prediction = new ActivePrediction
            {
                GuildId = guildId,
                CreatorId = creatorId,
                ChannelId = channelId,
                Title = title,
                CreatedAtUtc = now,
                BetsCloseAtUtc = closeAt,
                IsLocked = false,
                IsResolved = false
            };

            // Создаём исходы из массива названий
            for (int i = 0; i < outcomeNames.Length; i++)
            {
                prediction.Outcomes.Add(new PredictionOutcome
                {
                    Id = i + 1,
                    Name = outcomeNames[i].Trim()
                });
            }

            var outcomesList = string.Join(", ", prediction.Outcomes.Select(o => $"{o.Id}: {o.Name}"));
            prediction.UseCompactOutcomeLabels = $"Исход ({outcomesList})".Length > 45;
            prediction.UseInlineOutcomeFields = prediction.Outcomes.Count <= 3
                && prediction.Outcomes.All(o => $"📊 Исход {o.Id}: {o.Name}".Length <= 256);

            var embed = BuildEmbed(prediction, showLocked: false);
            var components = BuildComponents(prediction, showLocked: false);
            var message = await targetChannel.SendMessageAsync(embed: embed, components: components.Build()).ConfigureAwait(false);
            prediction.MessageId = message.Id;

            if (_active.TryAdd(guildId, prediction))
            {
                _activeChannels[guildId] = targetChannel;

                if (!_userStats.TryGetValue(guildId, out var guildStats))
                {
                    guildStats = new Dictionary<ulong, UserPredictionStats>();
                    _userStats[guildId] = guildStats;
                }

                if (!guildStats.TryGetValue(creatorId, out var creatorStats))
                {
                    creatorStats = new UserPredictionStats { UserId = creatorId };
                    guildStats[creatorId] = creatorStats;
                }

                creatorStats.CreatedPredictions++;

                var totalPool = prediction.TotalPool;
                var outcomesInfo = string.Join("; ", prediction.Outcomes.Select(o => $"{o.Id}:'{o.Name}'"));

                await LogAsync(
                    $"CREATE guild={guildId}({guildName}) channel={channelId}({channelName}) creator={creatorId}({creatorName}) title='{title}' dur={duration} outcomes=[{outcomesInfo}] pool={totalPool}");

                // Persist prediction state so it survives bot restarts
                _ = Task.Run(async () => await SaveStateAsync().ConfigureAwait(false));
                _ = Task.Run(async () => await SaveStatsAsync().ConfigureAwait(false));

                return (true, string.Empty, prediction);
            }

            try { await message.DeleteAsync().ConfigureAwait(false); } catch { }
            return (false, "Не удалось зарегистрировать прогноз.", null);
        }

        public async Task<(bool ok, string error, ActivePrediction? prediction)> CreateAsync(
            ulong guildId,
            ulong creatorId,
            ulong channelId,
            string title,
            string outcome1Name,
            string outcome2Name,
            TimeSpan duration)
        {
            var rawChannel = _client.GetChannel(channelId) ?? _client.GetGuild(guildId)?.GetChannel(channelId);
            var channel = rawChannel as ISocketMessageChannel;
            if (channel == null)
            {
                var rawType = rawChannel?.GetType().FullName ?? "null";
                var rawName = (rawChannel as IChannel)?.Name ?? "(без имени)";
                await LogAsync($"CREATE_FAIL_CHANNEL guild={guildId} creator={creatorId} channelId={channelId} rawType={rawType} rawName='{rawName}'");
                return (false, "Не удалось найти канал для создания прогноза.", null);
            }

            return await CreateAsync(guildId, creatorId, channel, title, outcome1Name, outcome2Name, duration).ConfigureAwait(false);
        }

        public async Task<(bool ok, string error)> PlaceBetAsync(
            ulong guildId,
            ulong userId,
            int outcomeId,
            long amount)
        {
            if (!_active.TryGetValue(guildId, out var p))
                return (false, "Активного прогноза нет.");

            if (p.IsLocked)
                return (false, "Приём ставок уже завершён.");

            if (DateTimeOffset.UtcNow >= p.BetsCloseAtUtc)
            {
                p.IsLocked = true;
                await UpdateMessageAsync(p, showLocked: true).ConfigureAwait(false);
                _ = Task.Run(async () => await SaveStateAsync().ConfigureAwait(false));

                // ✅ НОВОЕ: Логируем закрытие приёма ставок
                var betsDuration = DateTimeOffset.UtcNow - p.CreatedAtUtc;
                await LogAsync($"LOCK guild={guildId} title='{p.Title}' bets={p.Bets.Count} duration={betsDuration.TotalSeconds:F0}s totalPool={p.TotalPool}");

                return (false, "Время приёма ставок истекло.");
            }

            if (amount <= 0)
                return (false, "Сумма ставки должна быть положительной.");

            // Если пользователь уже ставил
            await p.Sync.WaitAsync().ConfigureAwait(false);
            try
            {
                if (p.Bets.TryGetValue(userId, out var existing))
            {
                // Разрешаем только добавление на тот же исход
                if (existing.OutcomeId != outcomeId)
                    return (false, "Вы уже сделали ставку на другой исход — изменить её нельзя.");

                // Тратим дополнительные очки
                    if (!_points.TrySpend(guildId, userId, amount))
                        return (false, "Недостаточно костяшек для этой ставки.");

                    existing.Amount += amount;

                    // ✅ Обновлено: поиск исхода по ID
                    var outcome = p.GetOutcomeById(outcomeId);
                    if (outcome == null)
                        return (false, "Неверный ID исхода.");

                    outcome.TotalStake += amount;
                    if (!outcome.TopUserId.HasValue || existing.Amount > outcome.TopUserStake)
                    {
                        outcome.TopUserId = userId;
                        outcome.TopUserStake = existing.Amount;
                    }

                    await UpdateMessageAsync(p, showLocked: false).ConfigureAwait(false);

                    // ✅ БАГ 7 ИСПРАВЛЕН: Сохраняем состояние после увеличения ставки
                    _ = Task.Run(async () => await SaveStateAsync().ConfigureAwait(false));

                    await LogAsync($"BET_ADD guild={guildId} user={userId} outcome={outcomeId} added={amount} total={existing.Amount}");
                    return (true, string.Empty);
            }

            // Новая ставка
                if (!_points.TrySpend(guildId, userId, amount))
                    return (false, "Недостаточно костяшек для этой ставки.");

                var bet = new PredictionBet
                {
                    UserId = userId,
                    OutcomeId = outcomeId,
                    Amount = amount
                };

                p.Bets[userId] = bet;

                if (!_userStats.TryGetValue(guildId, out var guildStats))
                {
                    guildStats = new Dictionary<ulong, UserPredictionStats>();
                    _userStats[guildId] = guildStats;
                }

                if (!guildStats.TryGetValue(userId, out var userStats))
                {
                    userStats = new UserPredictionStats { UserId = userId };
                    guildStats[userId] = userStats;
                }

                if (p.Bets.Count == 1)
                {
                    userStats.FirstBets++;
                }

                // ✅ Обновлено: поиск исхода по ID
                var outcomeNew = p.GetOutcomeById(outcomeId);
                if (outcomeNew == null)
                    return (false, "Неверный ID исхода.");

                outcomeNew.TotalStake += amount;
                if (!outcomeNew.TopUserId.HasValue || amount > outcomeNew.TopUserStake)
                {
                    outcomeNew.TopUserId = userId;
                    outcomeNew.TopUserStake = amount;
                }

                await UpdateMessageAsync(p, showLocked: false).ConfigureAwait(false);

                // ✅ БАГ 7 ИСПРАВЛЕН: Сохраняем состояние после новой ставки
                _ = Task.Run(async () => await SaveStateAsync().ConfigureAwait(false));

                await LogAsync($"BET guild={guildId} user={userId} outcome={outcomeId} amount={amount}");
                return (true, string.Empty);
            }
            finally
            {
                p.Sync.Release();
            }
        }

        public async Task<(bool ok, string error)> ResolveAsync(
            ulong guildId,
            ulong resolverId,
            bool isAdminOverride,
            int winningOutcomeId)
        {
            if (!_active.TryGetValue(guildId, out var p))
                return (false, "Активного прогноза нет.");

            if (p.IsResolved)
                return (false, "Прогноз уже завершён.");

            // Нельзя завершать прогноз до окончания приёма ставок, если нет админского оверрайда
            if (!isAdminOverride && DateTimeOffset.UtcNow < p.BetsCloseAtUtc)
            {
                return (false, "Прогноз ещё идёт: приём ставок не завершён. Дождитесь окончания времени или используйте административный доступ.");
            }

            if (!(resolverId == p.CreatorId || isAdminOverride))
                return (false, "Завершить прогноз может только создатель или администратор.");

            p.IsResolved = true;
            p.WinningOutcomeId = winningOutcomeId;
            p.IsLocked = true;

            // ✅ Обновлено: получение победившего исхода динамически
            var winningOutcome = p.GetOutcomeById(winningOutcomeId);
            if (winningOutcome == null)
                return (false, $"Неверный ID исхода: {winningOutcomeId}");

            var totalPool = p.TotalPool;
            var winningPool = winningOutcome.TotalStake;

            // Коэффициент показа округляем для пользователя, но выплаты считаем по тому же отображаемому значению.
            double coefRaw = winningPool <= 0 ? 1.0 : (double)totalPool / winningPool;
            double coefRounded = Math.Round(coefRaw, 2, MidpointRounding.AwayFromZero);

            long topWinnerUserId = 0;
            long topWinnerProfit = 0;
            long winnersProfitTotal = 0;
            int winnersCount = 0;

            // Ensure thread-safety when distributing payouts
            await p.Sync.WaitAsync().ConfigureAwait(false);

            // Список для логирования выплат
            var payoutLogs = new List<string>();
            payoutLogs.Add($"=== ВЫПЛАТА ПОИНТОВ ===");
            payoutLogs.Add($"Прогноз: {p.Title}");
            payoutLogs.Add($"Сервер: {guildId}");
            payoutLogs.Add($"Победивший исход: {winningOutcome.Name} (ID={winningOutcomeId})");
            payoutLogs.Add($"Коэффициент: {coefRounded:F2}");
            payoutLogs.Add($"Общий пул: {totalPool}");
            payoutLogs.Add($"Дата: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            payoutLogs.Add($"");
            payoutLogs.Add($"ДЕТАЛИ ВЫПЛАТ:");

            try
            {
                foreach (var bet in p.Bets.Values.Where(b => b.OutcomeId == winningOutcomeId))
                {
                    // ✅ ИСПРАВЛЕНО: выигрыш = ставка × коэффициент, прибыль = выигрыш - ставка
                    var totalReturn = CalculatePayout(bet.Amount, coefRounded);
                    var profit = totalReturn - bet.Amount; // Чистая прибыль (без ставки)

                    _points.Add(guildId, bet.UserId, totalReturn);

                    winnersProfitTotal += profit;
                    winnersCount++;

                    if (profit > topWinnerProfit)
                    {
                        topWinnerProfit = profit;
                        topWinnerUserId = (long)bet.UserId;
                    }

                    // Логируем каждую выплату
                    payoutLogs.Add($"  UserID {bet.UserId}: ставка {bet.Amount}, прибыль {profit}, итого {totalReturn}");
                }

                payoutLogs.Add($"");
                payoutLogs.Add($"ИТОГО:");
                payoutLogs.Add($"  Победителей: {winnersCount}");
                payoutLogs.Add($"  Общая прибыль: {winnersProfitTotal}");
                payoutLogs.Add($"  Топовый участник: UserID {topWinnerUserId}, прибыль {topWinnerProfit}");
                payoutLogs.Add($"======================");

                // Записываем в отдельный файл
                await LogPayoutsAsync(payoutLogs);
            }
            finally
            {
                p.Sync.Release();
            }

            var othersProfit = winnersProfitTotal - topWinnerProfit;
            var othersCount = Math.Max(0, winnersCount - (topWinnerUserId != 0 ? 1 : 0));

            // Удаляем старое сообщение и публикуем новое с результатом
            try
            {
                var channel = GetActiveMessageChannel(p);
                if (channel != null)
                {
                    if (p.MessageId != 0)
                    {
                        try
                        {
                            var msg = await channel.GetMessageAsync(p.MessageId).ConfigureAwait(false);
                            if (msg != null)
                                await msg.DeleteAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            await PredictionErrorLogger.LogAsync("ResolveAsync:DeleteMessage", ex, $"guild={p.GuildId} channel={p.ChannelId} message={p.MessageId}").ConfigureAwait(false);
                        }
                    }

                    var resultEmbed = BuildResultEmbed(
                        p,
                        winningOutcome,
                        coefRounded,
                        totalPool,
                        topWinnerUserId == 0 ? (ulong?)null : (ulong)topWinnerUserId,
                        topWinnerProfit,
                        othersProfit,
                        othersCount);
                    await channel.SendMessageAsync(embed: resultEmbed).ConfigureAwait(false);

                    // ✅ НОВОЕ: Обновляем статистику и достижения
                    try
                    {
                        await UpdateUserStatsAfterResolution(guildId, p, winningOutcomeId, coefRounded).ConfigureAwait(false);
                        await UpdateAchievementsAsync(guildId, p).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await PredictionErrorLogger.LogAsync("ResolveAsync:UpdateStats", ex, $"guild={guildId}").ConfigureAwait(false);
                    }

                    // ✅ НОВОЕ: Отправляем сообщение о достижениях (после небольшой задержки для обновления статистики)
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        try
                        {
                            var achievementsEmbed = BuildAchievementsEmbed(guildId, p.Title);
                            if (achievementsEmbed != null)
                            {
                                await channel.SendMessageAsync(embed: achievementsEmbed).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            await PredictionErrorLogger.LogAsync("ResolveAsync:AchievementsPost", ex, $"guild={guildId}").ConfigureAwait(false);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("ResolveAsync:ResultPost", ex, $"guild={guildId} resolver={resolverId} win={winningOutcomeId}").ConfigureAwait(false);
            }

            // ✅ НОВОЕ: Добавляем в историю
            await AddToHistoryAsync(p, winningOutcomeId, wasCancelled: false);

            _active.TryRemove(guildId, out _);
            _activeChannels.TryRemove(guildId, out _);
            _ = Task.Run(async () => await SaveStateAsync().ConfigureAwait(false));

            // ✅ УЛУЧШЕНО: Логируем с информацией о ставках и победителях
            var totalBets = p.Bets.Count;
            var winningBets = p.Bets.Values.Count(b => b.OutcomeId == winningOutcomeId);
            var losingBets = totalBets - winningBets;
            await LogAsync($"RESOLVE guild={guildId} resolver={resolverId} win={winningOutcomeId} coef={coefRounded:F2} totalBets={totalBets} winners={winningBets} losers={losingBets}");
            return (true, string.Empty);
        }

        public async Task<(bool ok, string error)> CancelAsync(
            ulong guildId,
            ulong resolverId,
            bool isAdminOverride,
            string? cancelReason = null)
        {
            if (!_active.TryGetValue(guildId, out var p))
                return (false, "Активного прогноза нет.");

            if (p.IsResolved)
                return (false, "Прогноз уже завершён.");

            if (!(resolverId == p.CreatorId || isAdminOverride))
                return (false, "Отменить прогноз может только создатель или администратор.");

            // Refunds - perform under lock
            await p.Sync.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var bet in p.Bets.Values)
                {
                    _points.Add(guildId, bet.UserId, bet.Amount);
                }
            }
            finally
            {
                p.Sync.Release();
            }

            try
            {
                var channel = GetActiveMessageChannel(p);
                if (channel != null)
                {
                    if (p.MessageId != 0)
                    {
                        try
                        {
                            var msg = await channel.GetMessageAsync(p.MessageId).ConfigureAwait(false);
                            if (msg != null)
                                await msg.DeleteAsync().ConfigureAwait(false);
                        }
                        catch { }
                    }

                    var cancelEmbed = BuildCancelEmbed(p, cancelReason);
                    await channel.SendMessageAsync(embed: cancelEmbed).ConfigureAwait(false);
                }
            }
            catch { }

            // ✅ НОВОЕ: Добавляем в историю
            await AddToHistoryAsync(p, winningOutcomeId: null, wasCancelled: true);

            _active.TryRemove(guildId, out _);
            _activeChannels.TryRemove(guildId, out _);

            // ✅ УЛУЧШЕНО: Логируем с информацией о возвращённых ставках
            var totalBets = p.Bets.Count;
            var totalAmount = p.Bets.Values.Sum(b => b.Amount);
            await LogAsync($"CANCEL guild={guildId} resolver={resolverId} totalBets={totalBets} refundedAmount={totalAmount} reason='{cancelReason ?? "manual"}'");
            return (true, string.Empty);
        }

        private Embed BuildCancelEmbed(ActivePrediction p, string? cancelReason = null)
        {
            var totalRefund = p.Bets.Values.Sum(b => b.Amount);
            var description = string.IsNullOrWhiteSpace(cancelReason)
                ? "Все исходы не сыграли. Все ставки возвращены участникам в полном объёме."
                : cancelReason;

            return new EmbedBuilder()
                .WithTitle($"Прогноз отменён: {p.Title}")
                .WithDescription(description)
                .AddField("Возвращено костяшек", totalRefund.ToString(), false)
                .WithColor(Color.DarkGrey)
                .Build();
        }

        private Embed BuildEmbed(ActivePrediction p, bool showLocked, bool botOffline = false)
        {
            var builder = new EmbedBuilder()
                .WithTitle($"🎯 Прогноз: {p.Title}")
                .WithColor(botOffline ? Color.Red : (showLocked ? Color.Orange : Color.Blue));

            // ✅ БАГ 6: Показываем предупреждение если бот offline
            if (botOffline)
            {
                builder.WithDescription("⚠️ **БОТ НЕАКТИВЕН** — таймер может отставать");
            }

            var totalPool = p.TotalPool;

            // ✅ ОБНОВЛЕНО: Прогресс-бары и коэффициенты
            foreach (var outcome in p.Outcomes)
            {
                var field = new StringBuilder();

                // Процент от общего банка
                var percentage = totalPool > 0 ? (double)outcome.TotalStake / totalPool * 100 : 0;

                // Прогресс-бар (20 блоков = 100%)
                var filledBlocks = (int)(percentage / 5);
                var emptyBlocks = 20 - filledBlocks;
                var progressBar = new string('█', Math.Max(0, filledBlocks)) + new string('░', Math.Max(0, emptyBlocks));

                // Количество ставок на этот исход
                var betCount = p.Bets.Values.Count(b => b.OutcomeId == outcome.Id);

                field.AppendLine($"{progressBar} {percentage:F1}%");
                field.AppendLine($"💰 {outcome.TotalStake:N0} костяшек ({betCount} ставок)");

                var coef = p.GetCoefficient(outcome.Id);
                field.AppendLine($"📈 Коэффициент: **{coef:F2}x**");
                field.AppendLine($"└─ На 100 → вернётся {(100 * coef):N0}");

                if (outcome.TopUserId.HasValue)
                {
                    field.AppendLine($"🏆 Топ: <@{outcome.TopUserId}> — {outcome.TopUserStake:N0}");
                }

                builder.AddField($"📊 Исход {outcome.Id}: {outcome.Name}", field.ToString(), inline: p.UseInlineOutcomeFields);
            }

            builder.AddField("💎 Общий банк", $"{totalPool:N0} костяшек", false);

            // Время окончания показываем только пока приём ставок открыт
            if (!showLocked && TryGetMoscowTime(p.BetsCloseAtUtc.UtcDateTime, out var mskTime))
            {
                var left = p.BetsCloseAtUtc - DateTimeOffset.UtcNow;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                var leftSeconds = (int)Math.Ceiling(left.TotalSeconds);
                var leftStr = leftSeconds <= 0
                    ? "0с"
                    : leftSeconds < 1
                        ? "<1с"
                        : leftSeconds >= 3600
                            ? $"{leftSeconds / 3600}ч {(leftSeconds % 3600) / 60}м {leftSeconds % 60}с"
                            : leftSeconds >= 60
                                ? $"{leftSeconds / 60}м {leftSeconds % 60}с"
                                : $"{leftSeconds}с";

                builder.AddField("⏱️ До закрытия", leftStr, true);
                builder.AddField("📅 Закрытие", $"{mskTime:HH:mm} МСК", true);
            }

            builder.WithFooter(
                showLocked ? "⏸️ Приём ставок завершён — ожидание результата" :
                (botOffline && !showLocked) ? "⚠️ Бот неактивен — таймер может отставать" :
                botOffline && showLocked ? "⚠️ Бот неактивен" :
                "💰 Ставьте костяшки до указанного времени");

            return builder.Build();
        }

        private ComponentBuilder BuildComponents(ActivePrediction p, bool showLocked)
        {
            var mb = new ComponentBuilder();

            if (!p.IsLocked && !p.IsResolved)
            {
                // Пока приём ставок открыт: кнопки сделать ставку и отменить
                mb.WithButton("Сделать ставку", customId: $"pred_bet:{p.GuildId}", style: ButtonStyle.Primary);
                mb.WithButton("Отменить прогноз", customId: $"pred_cancel:{p.GuildId}", style: ButtonStyle.Danger);
            }
            else if (p.IsLocked && !p.IsResolved)
            {
                // ✅ Обновлено: динамические кнопки для всех исходов
                foreach (var outcome in p.Outcomes.Take(5)) // Discord позволяет макс 5 кнопок в ряду
                {
                    mb.WithButton(
                        $"Выбрать: {outcome.Name}", 
                        customId: $"pred_resolve:{p.GuildId}:{outcome.Id}", 
                        style: ButtonStyle.Success);
                }

                mb.WithButton("Отменить прогноз", customId: $"pred_cancel:{p.GuildId}", style: ButtonStyle.Danger);
            }

            return mb;
        }

        private Embed BuildResultEmbed(
            ActivePrediction p,
            PredictionOutcome winningOutcome,
            double coef,
            long totalPool,
            ulong? topWinnerUserId,
            long topWinnerProfit,
            long othersProfit,
            int othersCount)
        {
            var builder = new EmbedBuilder()
                .WithTitle($"Результат прогноза: {p.Title}")
                .WithColor(Color.Green);

            builder.AddField("Победивший исход", winningOutcome.Name, false);
            builder.AddField("Ставки на победивший исход", winningOutcome.TotalStake.ToString(), true);

            // ✅ Обновлено: показываем все проигравшие исходы
            var losingOutcomes = p.Outcomes.Where(o => o.Id != winningOutcome.Id).ToList();
            if (losingOutcomes.Count == 1)
            {
                builder.AddField("Ставки на другой исход", losingOutcomes[0].TotalStake.ToString(), true);
            }
            else if (losingOutcomes.Count > 1)
            {
                var losingStakes = string.Join(", ", losingOutcomes.Select(o => $"{o.Name}: {o.TotalStake}"));
                builder.AddField("Ставки на проигравшие исходы", losingStakes, false);
            }

            builder.AddField("Общий пул", totalPool.ToString(), false);
            builder.AddField("Коэффициент", Math.Max(0, coef).ToString("F2"), false);

            // Всегда показываем информацию о выигрыше, даже если победитель один
            if (topWinnerUserId.HasValue && topWinnerProfit > 0)
            {
                var topWinnerBet = p.Bets.Values.FirstOrDefault(b => b.UserId == (ulong)topWinnerUserId && b.OutcomeId == winningOutcome.Id);
                var topWinnerReturn = topWinnerBet != null ? CalculatePayout(topWinnerBet.Amount, coef) : 0;

                builder.AddField("🏆 Топ выигрыш", 
                    $"<@{topWinnerUserId}>:\n" +
                    $"  💰 Ставка: {topWinnerBet?.Amount ?? 0:N0}\n" +
                    $"  📈 Возврат: {topWinnerReturn:N0} ({coef:F2}x)\n" +
                    $"  💎 Прибыль: **+{topWinnerProfit:N0}**", 
                    false);

                // Показываем остальных только если их больше одного
                if (othersCount > 0 && othersProfit > 0)
                {
                    builder.AddField("Остальные победители", $"Ещё {othersCount} участников заработали {othersProfit:N0} костяшек прибыли", false);
                }
            }
            else if (winningOutcome.TotalStake > 0)
            {
                // Если нет топового участника, но были ставки - показываем общую сумму
                var totalWinnings = CalculatePayout(winningOutcome.TotalStake, coef);
                var totalProfit = totalWinnings - winningOutcome.TotalStake;
                builder.AddField("Выплаты победителям", 
                    $"Всего возвращено: {totalWinnings:N0} костяшек\n" +
                    $"Общая прибыль: **+{totalProfit:N0}**", 
                    false);
            }

            return builder.Build();
        }

        private static long CalculatePayout(long amount, double coefficient)
        {
            return (long)Math.Round(amount * coefficient, MidpointRounding.AwayFromZero);
        }

        private async Task UpdateMessageAsync(ActivePrediction p, bool showLocked, bool botOffline = false)
        {
            try
            {
                var channel = GetActiveMessageChannel(p);
                if (channel == null || p.MessageId == 0)
                    return;

                var msg = await channel.GetMessageAsync(p.MessageId).ConfigureAwait(false) as IUserMessage;
                if (msg == null)
                    return;

                var embed = BuildEmbed(p, showLocked, botOffline);
                // Build components only if prediction is not resolved/cancelled
                MessageComponent? comps = null;
                if (!p.IsResolved && !botOffline) // ✅ Отключаем кнопки при offline
                {
                    var cb = BuildComponents(p, showLocked);
                    comps = cb?.Build();
                }

                await msg.ModifyAsync(props =>
                {
                    props.Embed = embed;
                    props.Components = comps;
                }).ConfigureAwait(false);
            }
            catch
            {
                // лог ошибок ниже, чтобы не спамить
            }
        }

        private ISocketMessageChannel? GetActiveMessageChannel(ActivePrediction p)
        {
            if (_activeChannels.TryGetValue(p.GuildId, out var activeChannel) && activeChannel != null)
                return activeChannel;

            return _client.GetChannel(p.ChannelId) as ISocketMessageChannel
                ?? _client.GetGuild(p.GuildId)?.GetChannel(p.ChannelId) as ISocketMessageChannel;
        }

        private async Task MonitorLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var delaySeconds = 10;
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var kv in _active.ToArray())
                    {
                        var p = kv.Value;
                        try
                        {
                            if (!p.IsLocked)
                            {
                                var toClose = p.BetsCloseAtUtc - now;
                                if (toClose <= TimeSpan.FromSeconds(15) && toClose > TimeSpan.Zero)
                                    delaySeconds = 1;
                            }

                            if (!p.IsLocked && now >= p.BetsCloseAtUtc)
                            {
                                p.IsLocked = true;
                                await UpdateMessageAsync(p, showLocked: true).ConfigureAwait(false);
                                _ = Task.Run(async () => await SaveStateAsync().ConfigureAwait(false));

                                // ✅ УЛУЧШЕНО: Расширенное логирование закрытия приёма ставок
                                var betsDuration = now - p.CreatedAtUtc;
                                await LogAsync($"LOCK guild={p.GuildId} title='{p.Title}' bets={p.Bets.Count} duration={betsDuration.TotalSeconds:F0}s totalPool={p.TotalPool}");
                            }
                            else if (!p.IsResolved)
                            {
                                // Keep refreshing while active so the countdown stays up-to-date.
                                await UpdateMessageAsync(p, showLocked: p.IsLocked).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            await LogAsync($"MONITOR_ERROR guild={p.GuildId} msg={p.MessageId} err='{ex.Message}'").ConfigureAwait(false);
                        }
                    }
                }
                catch
                {
                    // игнорируем ошибки цикла
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private async Task LogAsync(string message)
        {
            try
            {
                var dir = Path.GetDirectoryName(_logPath) ?? AppContext.BaseDirectory;
                Directory.CreateDirectory(dir);
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                await File.AppendAllTextAsync(_logPath, line, Encoding.UTF8).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("LogAsync", ex, message).ConfigureAwait(false);
            }
        }

        private async Task LogPayoutsAsync(List<string> payoutLines)
        {
            try
            {
                var dir = Path.GetDirectoryName(_logPath) ?? AppContext.BaseDirectory;
                Directory.CreateDirectory(dir);

                // Создаём отдельный файл для выплат с датой
                var payoutsFileName = $"PredictionPayouts_{DateTime.Now:yyyyMMdd}.log";
                var payoutsPath = Path.Combine(dir, payoutsFileName);

                var content = string.Join(Environment.NewLine, payoutLines) + Environment.NewLine + Environment.NewLine;
                await File.AppendAllTextAsync(payoutsPath, content, Encoding.UTF8).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("LogPayoutsAsync", ex, "Failed to log payouts").ConfigureAwait(false);
            }
        }

        private static bool TryGetMoscowTime(DateTime utc, out DateTime msk)
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
                msk = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
                return true;
            }
            catch
            {
                msk = utc;
                return false;
            }
        }

        // ==================== ИСТОРИЯ ПРОГНОЗОВ ====================

        private async Task LoadHistoryAsync()
        {
            await _historyGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(_historyFilePath))
                {
                    _history = new PredictionHistoryStore();
                    return;
                }

                var json = await File.ReadAllTextAsync(_historyFilePath).ConfigureAwait(false);
                _history = System.Text.Json.JsonSerializer.Deserialize<PredictionHistoryStore>(json) ?? new();
                await LogAsync($"HISTORY_LOADED entries={_history.History.Sum(kv => kv.Value.Count)}");
            }
            catch (Exception ex)
            {
                await LogAsync($"HISTORY_LOAD_ERROR: {ex.Message}");
                _history = new PredictionHistoryStore();
            }
            finally
            {
                _historyGate.Release();
            }
        }

        private async Task SaveHistoryAsync()
        {
            await _historyGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(_history, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_historyFilePath, json).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await LogAsync($"HISTORY_SAVE_ERROR: {ex.Message}");
            }
            finally
            {
                _historyGate.Release();
            }
        }

        private async Task AddToHistoryAsync(ActivePrediction pred, int? winningOutcomeId, bool wasCancelled)
        {
            try
            {
                if (!_history.History.ContainsKey(pred.GuildId))
                {
                    _history.History[pred.GuildId] = new List<PredictionHistoryEntry>();
                }

                var entry = new PredictionHistoryEntry
                {
                    GuildId = pred.GuildId,
                    Title = pred.Title,
                    StartTime = pred.CreatedAtUtc.DateTime,
                    EndTime = DateTime.UtcNow,
                    Outcomes = pred.Outcomes,
                    WinningOutcomeId = winningOutcomeId,
                    WinningOutcomeName = winningOutcomeId.HasValue ? pred.GetOutcomeById(winningOutcomeId.Value)?.Name : null,
                    TotalPool = pred.Bets.Values.Sum(b => b.Amount),
                    WasCancelled = wasCancelled,
                    CreatorId = pred.CreatorId,
                    Bets = new List<BetResult>()
                };

                // Рассчитываем результаты ставок
                foreach (var bet in pred.Bets.Values)
                {
                    long payout = 0;
                    bool won = false;
                    double coefficient = 1.0;
                    var outcomeCoefficient = pred.GetCoefficient(bet.OutcomeId);

                    if (wasCancelled)
                    {
                        payout = bet.Amount; // Возврат
                        coefficient = outcomeCoefficient;
                    }
                    else if (winningOutcomeId.HasValue && bet.OutcomeId == winningOutcomeId.Value)
                    {
                        var outcome = pred.GetOutcomeById(bet.OutcomeId);
                        if (outcome != null)
                        {
                            coefficient = pred.GetCoefficient(bet.OutcomeId);
                            payout = CalculatePayout(bet.Amount, coefficient);
                            won = true;
                        }
                    }

                    entry.Bets.Add(new BetResult
                    {
                        UserId = bet.UserId,
                        OutcomeId = bet.OutcomeId,
                        Amount = bet.Amount,
                        Payout = payout,
                        Won = won,
                        Coefficient = coefficient,
                        WasFavorite = outcomeCoefficient < 2,
                        WasUnderdog = outcomeCoefficient > 10
                    });
                }

                // ✅ ИСПРАВЛЕНО: TotalPayout = чистый выигрыш (прибыль без возврата ставки)
                entry.TotalPayout = entry.Bets.Where(b => b.Won).Sum(b => b.Payout - b.Amount);

                // Добавляем в начало списка (новые сверху)
                _history.History[pred.GuildId].Insert(0, entry);

                // Ограничиваем размер истории
                if (_history.History[pred.GuildId].Count > PredictionHistoryStore.MaxHistoryPerGuild)
                {
                    _history.History[pred.GuildId].RemoveAt(_history.History[pred.GuildId].Count - 1);
                }

                await SaveHistoryAsync();

                await LogAsync($"HISTORY_ADDED guild={pred.GuildId} title='{pred.Title}' cancelled={wasCancelled}");
            }
            catch (Exception ex)
            {
                await LogAsync($"HISTORY_ADD_ERROR: {ex.Message}");
            }
        }

        public List<PredictionHistoryEntry> GetHistory(ulong guildId, int page = 0, int pageSize = 10)
        {
            if (!_history.History.TryGetValue(guildId, out var entries))
                return new List<PredictionHistoryEntry>();

            return entries.Skip(page * pageSize).Take(pageSize).ToList();
        }

        public int GetHistoryPageCount(ulong guildId, int pageSize = 10)
        {
            if (!_history.History.TryGetValue(guildId, out var entries))
                return 0;

            return (int)Math.Ceiling((double)entries.Count / pageSize);
        }

        // ✅ НОВОЕ: Загрузка/сохранение статистики пользователей
        private async Task LoadStatsAsync()
        {
            await _statsGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(_statsFilePath))
                    return;

                var json = await File.ReadAllTextAsync(_statsFilePath).ConfigureAwait(false);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<ulong, Dictionary<ulong, UserPredictionStats>>>(json);
                if (loaded != null)
                {
                    _userStats.Clear();
                    foreach (var kvp in loaded)
                        _userStats[kvp.Key] = kvp.Value;
                }
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("LoadStatsAsync", ex, _statsFilePath).ConfigureAwait(false);
            }
            finally
            {
                _statsGate.Release();
            }
        }

        private async Task LoadAchievementsAsync()
        {
            await _achievementsGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(_achievementsFilePath))
                    return;

                var json = await File.ReadAllTextAsync(_achievementsFilePath).ConfigureAwait(false);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<ulong, List<UserAchievement>>>(json);
                if (loaded != null)
                {
                    _userAchievements.Clear();
                    foreach (var kvp in loaded)
                    {
                        _userAchievements[kvp.Key] = kvp.Value
                            .Where(a => AchievementDefinitions.All.ContainsKey(a.AchievementId))
                            .ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("LoadAchievementsAsync", ex, _achievementsFilePath).ConfigureAwait(false);
            }
            finally
            {
                _achievementsGate.Release();
            }
        }

        private async Task SaveStatsAsync()
        {
            await _statsGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var snapshot = _userStats.ToDictionary(kv => kv.Key, kv => kv.Value);
                var json = System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_statsFilePath, json).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("SaveStatsAsync", ex, _statsFilePath).ConfigureAwait(false);
            }
            finally
            {
                _statsGate.Release();
            }
        }

        // ✅ НОВОЕ: Обновление статистики пользователя после завершения прогноза
        private async Task UpdateUserStatsAfterResolution(
            ulong guildId,
            ActivePrediction prediction,
            int winningOutcomeId,
            double coef)
        {
            if (!_userStats.TryGetValue(guildId, out var guildStats))
            {
                guildStats = new Dictionary<ulong, UserPredictionStats>();
                _userStats[guildId] = guildStats;
            }

            // Обрабатываем всех участников прогноза
            foreach (var bet in prediction.Bets.Values)
            {
                if (!guildStats.TryGetValue(bet.UserId, out var stats))
                {
                    stats = new UserPredictionStats { UserId = bet.UserId };
                    guildStats[bet.UserId] = stats;
                }

                stats.TotalBets++;
                stats.TotalWagered += bet.Amount;
                stats.TotalParticipation++;

                var betCoefficient = prediction.GetCoefficient(bet.OutcomeId);
                var currentUtcDate = DateTime.UtcNow.Date;
                var currentWeekStart = GetWeekStartUtc(currentUtcDate);
                var currentMonthStart = new DateTime(currentUtcDate.Year, currentUtcDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);

                if (stats.WeekStartUtc != currentWeekStart)
                {
                    stats.WeekStartUtc = currentWeekStart;
                    stats.WeekHighCoeffWins = 0;
                }

                if (stats.MonthStartUtc != currentMonthStart)
                {
                    stats.MonthStartUtc = currentMonthStart;
                    stats.MonthProfit = 0;
                }

                if (bet.Amount > stats.LargestBet)
                    stats.LargestBet = bet.Amount;

                if (betCoefficient < 2)
                    stats.FavoriteBets++;

                if (bet.OutcomeId == winningOutcomeId)
                {
                    // Победитель
                    stats.WonBets++;

                    // Прибыль = (ставка * коэфф) - ставка
                    var totalReturn = (long)Math.Round(bet.Amount * coef, MidpointRounding.AwayFromZero);
                    var profit = totalReturn - bet.Amount;

                    stats.TotalWon += profit;
                    stats.NetProfit += profit;
                    stats.MonthProfit += profit;

                    if (profit > stats.HighestSingleWin)
                        stats.HighestSingleWin = profit;

                    if (betCoefficient > stats.HighestCoeffWin)
                        stats.HighestCoeffWin = betCoefficient;

                    if (betCoefficient > 10)
                    {
                        stats.HighCoeffWins++;
                        stats.WeekHighCoeffWins++;
                    }

                    if (betCoefficient > 5)
                        stats.CurrentHighCoeffStreak++;
                    else
                        stats.CurrentHighCoeffStreak = 0;

                    if (betCoefficient < 2)
                        stats.FavoriteWins++;

                    // Обновление серии
                    if (stats.CurrentStreak >= 0)
                        stats.CurrentStreak++;
                    else
                        stats.CurrentStreak = 1;

                    if (stats.CurrentStreak > stats.BestStreak)
                        stats.BestStreak = stats.CurrentStreak;
                }
                else
                {
                    // Проигравший
                    stats.LostBets++;
                    stats.TotalLost += bet.Amount;
                    stats.NetProfit -= bet.Amount;
                    stats.MonthProfit -= bet.Amount;
                    stats.CurrentHighCoeffStreak = 0;

                    // Обновление серии
                    if (stats.CurrentStreak <= 0)
                        stats.CurrentStreak--;
                    else
                        stats.CurrentStreak = -1;
                }
            }

            await SaveStatsAsync().ConfigureAwait(false);
        }

        private static DateTime GetWeekStartUtc(DateTime utcDate)
        {
            var diff = ((int)utcDate.DayOfWeek + 6) % 7;
            return utcDate.AddDays(-diff);
        }

        // ✅ НОВОЕ: Обновление достижений после завершения прогноза
        private async Task UpdateAchievementsAsync(ulong guildId, ActivePrediction prediction)
        {
            if (!_userStats.TryGetValue(guildId, out var guildStats))
                return;

            if (!_userAchievements.TryGetValue(guildId, out var guildAchievements))
            {
                guildAchievements = new List<UserAchievement>();
                _userAchievements[guildId] = guildAchievements;
            }

            var newAchievements = new List<UserAchievement>();

            // Проверяем достижения для каждого участника
            foreach (var bet in prediction.Bets.Values)
            {
                if (!guildStats.TryGetValue(bet.UserId, out var stats))
                    continue;

                var userAchievementIds = guildAchievements
                    .Where(a => a.UserId == bet.UserId)
                    .Select(a => a.AchievementId)
                    .ToHashSet();

                var wonCurrentBet = bet.OutcomeId == prediction.WinningOutcomeId;
                var totalReturn = wonCurrentBet
                    ? (long)Math.Round(bet.Amount * prediction.GetCoefficient(bet.OutcomeId), MidpointRounding.AwayFromZero)
                    : 0;
                var currentProfit = wonCurrentBet ? totalReturn - bet.Amount : 0;
                var currentWinningOutcomePool = prediction.WinningOutcomeId.HasValue
                    ? prediction.GetOutcomeById(prediction.WinningOutcomeId.Value)?.TotalStake ?? 0
                    : 0;
                var currentWinningBetsCount = prediction.WinningOutcomeId.HasValue
                    ? prediction.Bets.Values.Count(b => b.OutcomeId == prediction.WinningOutcomeId.Value)
                    : 0;
                var isSingleWinnerOnOutcome = wonCurrentBet && currentWinningBetsCount == 1;

                // Проверка каждого достижения
                CheckAchievement("newcomer", stats.TotalBets >= 1);
                CheckAchievement("student", stats.TotalParticipation >= 5);
                CheckAchievement("experienced", stats.TotalParticipation >= 25);
                CheckAchievement("versatile", stats.TotalParticipation >= 50);
                CheckAchievement("veteran", stats.TotalParticipation >= 100);

                CheckAchievement("first_blood", stats.TotalWon > 0);
                CheckAchievement("first_place", stats.TotalBets >= 1 && stats.FirstBets >= 1);
                CheckAchievement("rich", wonCurrentBet && currentProfit > 10000);
                CheckAchievement("millionaire", wonCurrentBet && currentProfit > 50000);
                CheckAchievement("banker", stats.NetProfit > 100000);
                CheckAchievement("highroller", bet.Amount > 1000);
                CheckAchievement("whale", bet.Amount > 10000);

                CheckAchievement("accurate", stats.WinRate > 70 && stats.TotalBets >= 20);
                CheckAchievement("lucky", stats.BestStreak >= 10);
                CheckAchievement("on_fire", stats.BestStreak >= 20);
                CheckAchievement("sniper", stats.HighCoeffWins >= 5);
                CheckAchievement("lightning", stats.CurrentHighCoeffStreak >= 3);

                CheckAchievement("risky", wonCurrentBet && prediction.GetCoefficient(bet.OutcomeId) > 10);
                CheckAchievement("madman", wonCurrentBet && prediction.GetCoefficient(bet.OutcomeId) > 20);
                CheckAchievement("legend", wonCurrentBet && prediction.GetCoefficient(bet.OutcomeId) > 50);
                CheckAchievement("hurricane", stats.WeekHighCoeffWins >= 3);

                CheckAchievement("analyst", stats.FavoriteBets >= 10 && stats.FavoriteWins > 0 && ((double)stats.FavoriteWins / stats.FavoriteBets * 100) > 80);
                CheckAchievement("strategist", stats.MonthProfit > 50000);
                CheckAchievement("mathematician", stats.CreatedPredictions >= 10);
                CheckAchievement("prediction_king", stats.CreatedPredictions >= 50);

                CheckAchievement("early_bird", stats.FirstBets >= 10);
                CheckAchievement("loner", isSingleWinnerOnOutcome);
                CheckAchievement("trickster", wonCurrentBet && prediction.TotalPool > 0 && currentWinningOutcomePool * 10 < prediction.TotalPool);
                CheckAchievement("perfectionist", stats.BestStreak >= 50);

                void CheckAchievement(string achievementId, bool condition)
                {
                    if (condition && AchievementDefinitions.All.ContainsKey(achievementId) && !userAchievementIds.Contains(achievementId))
                    {
                        var newAch = new UserAchievement
                        {
                            UserId = bet.UserId,
                            AchievementId = achievementId,
                            EarnedAt = DateTimeOffset.UtcNow
                        };
                        guildAchievements.Add(newAch);
                        newAchievements.Add(newAch);
                        userAchievementIds.Add(achievementId);
                    }
                }
            }

            if (newAchievements.Count > 0)
            {
                await SaveAchievementsAsync().ConfigureAwait(false);
            }
        }

        private async Task SaveAchievementsAsync()
        {
            await _achievementsGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var snapshot = _userAchievements.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
                var json = System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_achievementsFilePath, json).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("SaveAchievementsAsync", ex, _achievementsFilePath).ConfigureAwait(false);
            }
            finally
            {
                _achievementsGate.Release();
            }
        }

        // ✅ НОВОЕ: Создаёт Embed с новыми достижениями участников
        private Embed? BuildAchievementsEmbed(ulong guildId, string predictionTitle)
        {
            if (!_userAchievements.TryGetValue(guildId, out var guildAchievements))
                return null;

            // Получаем достижения за последние 10 секунд
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-10);
            var recentAchievements = guildAchievements
                .Where(a => a.EarnedAt >= cutoff)
                .GroupBy(a => a.UserId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(a => a.AchievementId).ToList()
                );

            if (recentAchievements.Count == 0)
                return null;

            var eb = new EmbedBuilder()
                .WithTitle("🎖️ НОВЫЕ ДОСТИЖЕНИЯ!")
                .WithColor(Color.Gold)
                .WithDescription($"Прогноз: **{predictionTitle}**\n\n");

            var sb = new StringBuilder();

            foreach (var kvp in recentAchievements.Take(10)) // Максимум 10 пользователей
            {
                var userId = kvp.Key;
                var achievementIds = kvp.Value;

                sb.AppendLine($"👤 <@{userId}>:");

                foreach (var achievementId in achievementIds)
                {
                    if (AchievementDefinitions.All.TryGetValue(achievementId, out var def))
                    {
                        sb.AppendLine($"  {def.Icon} **{def.Name}**");
                        sb.AppendLine($"     _{def.Description}_");
                        sb.AppendLine();
                    }
                }
            }

            eb.WithDescription(eb.Description + sb.ToString());
            eb.WithFooter($"Всего участников с новыми достижениями: {recentAchievements.Count}");

            return eb.Build();
        }

        // ✅ НОВОЕ: Публичные методы доступа к статистике
        public UserPredictionStats? GetUserStats(ulong guildId, ulong userId)
        {
            if (!_userStats.TryGetValue(guildId, out var guildStats))
                return null;

            return guildStats.TryGetValue(userId, out var stats) ? stats : null;
        }

        public IEnumerable<UserPredictionStats> GetAllUserStats(ulong guildId)
        {
            if (!_userStats.TryGetValue(guildId, out var guildStats))
                return Enumerable.Empty<UserPredictionStats>();

            return guildStats.Values;
        }

        public List<UserAchievement> GetUserAchievements(ulong guildId, ulong userId)
        {
            if (!_userAchievements.TryGetValue(guildId, out var guildAchievements))
                return new List<UserAchievement>();

            return guildAchievements
                .Where(a => a.UserId == userId && AchievementDefinitions.All.ContainsKey(a.AchievementId))
                .ToList();
        }

        public void Shutdown()
        {
            _cts.Cancel();
        }
    }
}







