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

        public PredictionService(DiscordSocketClient client, PointsService points, string logPath)
        {
            _client = client;
            _points = points;
            _logPath = logPath;

            // Фоновая задача для авто-блокировки ставок по истечении времени
            _ = Task.Run(() => MonitorLoopAsync(_cts.Token));
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
            if (_active.ContainsKey(guildId))
                return (false, "Уже есть активный прогноз на этом сервере.", null);

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
                Outcome1 = new PredictionOutcome { Id = 1, Name = outcome1Name },
                Outcome2 = new PredictionOutcome { Id = 2, Name = outcome2Name },
                CreatedAtUtc = now,
                BetsCloseAtUtc = closeAt,
                IsLocked = false,
                IsResolved = false
            };

            var embed = BuildEmbed(prediction, showLocked: false);
            var components = BuildComponents(prediction, showLocked: false);
            var message = await targetChannel.SendMessageAsync(embed: embed, components: components.Build()).ConfigureAwait(false);
            prediction.MessageId = message.Id;

            if (_active.TryAdd(guildId, prediction))
            {
                _activeChannels[guildId] = targetChannel;

                var totalPool = prediction.TotalPool;
                var outcome1Stake = prediction.Outcome1.TotalStake;
                var outcome2Stake = prediction.Outcome2.TotalStake;

                await LogAsync(
                    $"CREATE guild={guildId}({guildName}) channel={channelId}({channelName}) creator={creatorId}({creatorName}) title='{title}' dur={duration} outcomes=[1:'{outcome1Name}';2:'{outcome2Name}'] pool={totalPool} dist=1:{outcome1Stake} 2:{outcome2Stake}");

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
                return (false, "Время приёма ставок истекло.");
            }

            if (amount <= 0)
                return (false, "Сумма ставки должна быть положительной.");

            // Если пользователь уже ставил
            if (p.Bets.TryGetValue(userId, out var existing))
            {
                // Разрешаем только добавление на тот же исход
                if (existing.OutcomeId != outcomeId)
                    return (false, "Вы уже сделали ставку на другой исход — изменить её нельзя.");

                // Тратим дополнительные очки
                if (!_points.TrySpend(guildId, userId, amount))
                    return (false, "Недостаточно костяшек для этой ставки.");

                existing.Amount += amount;

                var outcome = outcomeId == 1 ? p.Outcome1 : p.Outcome2;
                outcome.TotalStake += amount;
                if (!outcome.TopUserId.HasValue || existing.Amount > outcome.TopUserStake)
                {
                    outcome.TopUserId = userId;
                    outcome.TopUserStake = existing.Amount;
                }

                await UpdateMessageAsync(p, showLocked: false).ConfigureAwait(false);
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

            var outcomeNew = outcomeId == 1 ? p.Outcome1 : p.Outcome2;
            outcomeNew.TotalStake += amount;
            if (!outcomeNew.TopUserId.HasValue || amount > outcomeNew.TopUserStake)
            {
                outcomeNew.TopUserId = userId;
                outcomeNew.TopUserStake = amount;
            }

            await UpdateMessageAsync(p, showLocked: false).ConfigureAwait(false);
            await LogAsync($"BET guild={guildId} user={userId} outcome={outcomeId} amount={amount}");
            return (true, string.Empty);
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

            var winningOutcome = winningOutcomeId == 1 ? p.Outcome1 : p.Outcome2;
            var losingOutcome = winningOutcomeId == 1 ? p.Outcome2 : p.Outcome1;

            var totalPool = p.TotalPool;
            var winningPool = winningOutcome.TotalStake;

            double rawOdds = winningPool <= 0 ? 1.0 : (double)totalPool / winningPool;
            double coef = rawOdds - 1.0;
            double coefRounded = Math.Round(coef, 1, MidpointRounding.AwayFromZero);

            long topWinnerUserId = 0;
            long topWinnerProfit = 0;

            foreach (var bet in p.Bets.Values.Where(b => b.OutcomeId == winningOutcomeId))
            {
                // ставка + ставка * coef
                var profitDouble = bet.Amount * coefRounded;
                var profit = (long)Math.Round(profitDouble, MidpointRounding.AwayFromZero);
                var totalReturn = bet.Amount + profit;
                _points.Add(guildId, bet.UserId, totalReturn);

                if (profit > topWinnerProfit)
                {
                    topWinnerProfit = profit;
                    topWinnerUserId = (long)bet.UserId;
                }
            }

            // Удаляем старое сообщение и публикуем новое с результатом
            try
            {
                var channel = GetActiveMessageChannel(p);
                if (channel != null)
                {
                    // Удаляем исходное сообщение прогноза
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

                    var resultEmbed = BuildResultEmbed(p, winningOutcome, losingOutcome, coef, totalPool, topWinnerUserId == 0 ? (ulong?)null : (ulong)topWinnerUserId, topWinnerProfit);
                    await channel.SendMessageAsync(embed: resultEmbed).ConfigureAwait(false);
                }
            }
            catch { }

            _active.TryRemove(guildId, out _);
            _activeChannels.TryRemove(guildId, out _);
            await LogAsync($"RESOLVE guild={guildId} resolver={resolverId} win={winningOutcomeId} coef={coefRounded:F1}");
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

            foreach (var bet in p.Bets.Values)
            {
                _points.Add(guildId, bet.UserId, bet.Amount);
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

            _active.TryRemove(guildId, out _);
            _activeChannels.TryRemove(guildId, out _);
            await LogAsync($"CANCEL guild={guildId} resolver={resolverId} refundedBets={p.Bets.Count} reason='{cancelReason ?? "manual"}'");
            return (true, string.Empty);
        }

        private Embed BuildCancelEmbed(ActivePrediction p, string? cancelReason = null)
        {
            var totalRefund = p.Bets.Values.Sum(b => b.Amount);
            var description = string.IsNullOrWhiteSpace(cancelReason)
                ? "Оба исхода не сыграли. Все ставки возвращены участникам в полном объёме."
                : cancelReason;

            return new EmbedBuilder()
                .WithTitle($"Прогноз отменён: {p.Title}")
                .WithDescription(description)
                .AddField("Возвращено костяшек", totalRefund.ToString(), false)
                .WithColor(Color.DarkGrey)
                .Build();
        }

        private Embed BuildEmbed(ActivePrediction p, bool showLocked)
        {
            var builder = new EmbedBuilder()
                .WithTitle($"Прогноз: {p.Title}")
                .WithColor(showLocked ? Color.Orange : Color.Blue);

            var totalPool = p.TotalPool;

            var field1 = new StringBuilder();
            field1.AppendLine($"Ставок всего: {p.Outcome1.TotalStake}");
            field1.AppendLine($"Коэффициент: {Math.Max(0, p.Coef1):F2}");
            if (p.Outcome1.TopUserId.HasValue)
            {
                field1.AppendLine($"Топ ставка: <@{p.Outcome1.TopUserId}> — {p.Outcome1.TopUserStake}");
            }

            var field2 = new StringBuilder();
            field2.AppendLine($"Ставок всего: {p.Outcome2.TotalStake}");
            field2.AppendLine($"Коэффициент: {Math.Max(0, p.Coef2):F2}");
            if (p.Outcome2.TopUserId.HasValue)
            {
                field2.AppendLine($"Топ ставка: <@{p.Outcome2.TopUserId}> — {p.Outcome2.TopUserStake}");
            }

            builder.AddField($"Исход 1: {p.Outcome1.Name}", field1.ToString(), true);
            builder.AddField($"Исход 2: {p.Outcome2.Name}", field2.ToString(), true);

            builder.AddField("Общий пул", totalPool.ToString(), false);

            // Время окончания показываем только пока приём ставок открыт
            if (!showLocked && TryGetMoscowTime(p.BetsCloseAtUtc.UtcDateTime, out var mskTime))
            {
                builder.AddField("Приём ставок до", $"{mskTime:dd.MM.yyyy HH:mm} по МСК", false);
            }

            builder.WithFooter(showLocked ? "Приём ставок завершён" : "Ставьте костяшки до указанного времени");

            return builder.Build();
        }

        private ComponentBuilder BuildComponents(ActivePrediction p, bool showLocked)
        {
            var mb = new ComponentBuilder();

            if (!p.IsLocked && !p.IsResolved)
            {
                // Пока приём ставок открыт: кнопки сделать ставку и отменить (общая доступность; проверка прав на сервере при обработке)
                mb.WithButton("Сделать ставку", customId: $"pred_bet:{p.GuildId}", style: ButtonStyle.Primary);
                mb.WithButton("Отменить прогноз", customId: $"pred_cancel:{p.GuildId}", style: ButtonStyle.Danger);
            }
            else if (p.IsLocked && !p.IsResolved)
            {
                // Приём завершён — показать выбор исхода и отмену
                mb.WithButton("Выбрать исход 1", customId: $"pred_resolve:{p.GuildId}:1", style: ButtonStyle.Success);
                mb.WithButton("Выбрать исход 2", customId: $"pred_resolve:{p.GuildId}:2", style: ButtonStyle.Success);
                mb.WithButton("Отменить прогноз", customId: $"pred_cancel:{p.GuildId}", style: ButtonStyle.Danger);
            }

            return mb;
        }

        private Embed BuildResultEmbed(
            ActivePrediction p,
            PredictionOutcome winningOutcome,
            PredictionOutcome losingOutcome,
            double coef,
            long totalPool,
            ulong? topWinnerUserId,
            long topWinnerProfit)
        {
            var builder = new EmbedBuilder()
                .WithTitle($"Результат прогноза: {p.Title}")
                .WithColor(Color.Green);

            builder.AddField("Победивший исход", winningOutcome.Name, false);
            builder.AddField("Ставки на победивший исход", winningOutcome.TotalStake.ToString(), true);
            builder.AddField("Ставки на другой исход", losingOutcome.TotalStake.ToString(), true);
            builder.AddField("Общий пул", totalPool.ToString(), false);
            builder.AddField("Коэффициент", Math.Max(0, coef).ToString("F2"), false);

            if (topWinnerUserId.HasValue && topWinnerProfit > 0)
            {
                builder.AddField("Топ выигрыш", $"<@{topWinnerUserId}> заработал {topWinnerProfit} костяшек", false);
            }

            return builder.Build();
        }

        private async Task UpdateMessageAsync(ActivePrediction p, bool showLocked)
        {
            try
            {
                var channel = GetActiveMessageChannel(p);
                if (channel == null || p.MessageId == 0)
                    return;

                var msg = await channel.GetMessageAsync(p.MessageId).ConfigureAwait(false) as IUserMessage;
                if (msg == null)
                    return;

                var embed = BuildEmbed(p, showLocked);
                await msg.ModifyAsync(props => props.Embed = embed).ConfigureAwait(false);
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
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var kv in _active)
                    {
                        var p = kv.Value;
                        if (!p.IsLocked && now >= p.BetsCloseAtUtc)
                        {
                            p.IsLocked = true;
                            await UpdateMessageAsync(p, showLocked: true).ConfigureAwait(false);
                            await LogAsync($"LOCK guild={p.GuildId} title='{p.Title}'");
                        }
                    }
                }
                catch
                {
                    // игнорируем ошибки цикла
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
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
            catch
            {
                // ignore
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

        public void Shutdown()
        {
            _cts.Cancel();
        }
    }
}




