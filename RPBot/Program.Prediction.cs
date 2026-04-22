using Discord;
using Discord.WebSocket;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RPBot
{
    public partial class Program
    {
        internal async Task PredictionCommand(SocketSlashCommand command)
        {
            try
            {
                var guildChannel = command.Channel as SocketGuildChannel;
                if (guildChannel == null)
                {
                    await command.RespondAsync("Эта команда доступна только на сервере.", ephemeral: true);
                    return;
                }

                var guildId = guildChannel.Guild.Id;
                var actionOpt = command.Data.Options.FirstOrDefault(o => o.Name == "action")?.Value?.ToString();
                var titleOpt = command.Data.Options.FirstOrDefault(o => o.Name == "title")?.Value?.ToString();
                var outcome1Opt = command.Data.Options.FirstOrDefault(o => o.Name == "outcome1")?.Value?.ToString();
                var outcome2Opt = command.Data.Options.FirstOrDefault(o => o.Name == "outcome2")?.Value?.ToString();
                var durationOpt = command.Data.Options.FirstOrDefault(o => o.Name == "duration_minutes")?.Value;
                var outcomeNumberOpt = command.Data.Options.FirstOrDefault(o => o.Name == "outcome")?.Value;
                var amountOpt = command.Data.Options.FirstOrDefault(o => o.Name == "amount")?.Value;
                var userOpt = command.Data.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser;

                var sconfig = GetServerConfigInternal(guildId);
                if (sconfig == null || !sconfig.PredictionsEnabled)
                {
                    await command.RespondAsync("Игровые прогнозы и костяшки на этом сервере отключены.", ephemeral: true);
                    return;
                }

                var user = command.User as SocketGuildUser;
                var action = (actionOpt ?? string.Empty).ToLowerInvariant();
                var isVoiceChannelChat = command.Channel is SocketVoiceChannel;

                bool IsActiveEventOnChannel(ulong channelId) => guildChannel.Guild.Events.Any(e =>
                    e.Status == GuildScheduledEventStatus.Active &&
                    e.Channel != null &&
                    e.Channel.Id == channelId);

                if (action is "create" or "bet" or "resolve" or "cancel")
                {
                    if (!isVoiceChannelChat)
                    {
                        await command.RespondAsync("Эта команда доступна только в чате голосового канала.", ephemeral: true);
                        return;
                    }
                }

                // Если прогноз активен, но событие на его канале уже не активно — принудительно отменяем с возвратом ставок
                var activePrediction = _predictionService.GetActive(guildId);
                if (activePrediction != null && !IsActiveEventOnChannel(activePrediction.ChannelId))
                {
                    var resolverId = _client.CurrentUser?.Id ?? 0;
                    var (closed, closeError) = await _predictionService.CancelAsync(
                        guildId,
                        resolverId,
                        isAdminOverride: true,
                        cancelReason: "Событие завершено. Прогноз принудительно закрыт, все ставки возвращены участникам.");

                    if (closed)
                    {
                        await command.RespondAsync("Событие завершено: активный прогноз автоматически закрыт, ставки возвращены.", ephemeral: true);
                    }
                    else
                    {
                        await command.RespondAsync($"Авто-закрытие прогноза не выполнено: {closeError}", ephemeral: true);
                    }
                    return;
                }

                switch (action)
                {
                    case "create":
                    {
                        if (user == null)
                        {
                            await command.RespondAsync("Не удалось определить пользователя.", ephemeral: true);
                            return;
                        }

                        var isAdmin = user.GuildPermissions.Administrator;
                        var isMaster = user.Roles.Any(r => r.Name.Equals("Мастер НРИ", StringComparison.OrdinalIgnoreCase));
                        if (!isAdmin && !isMaster)
                        {
                            await command.RespondAsync("Создавать прогнозы могут только мастера НРИ или администраторы.", ephemeral: true);
                            return;
                        }

                        // Open modal for creating prediction (friendly UI)
                        // Require user to be in a voice channel and that channel has an active scheduled event
                        var guildUser = command.User as SocketGuildUser;
                        var userVoiceChannel = guildUser?.VoiceChannel;
                        var commandVoiceChannel = command.Channel as SocketVoiceChannel;
                        var eventVoiceChannelId = sconfig.EventVoiceChannelID;

                        if (eventVoiceChannelId == 0)
                        {
                            await command.RespondAsync("Для этого сервера не настроен `EventVoiceChannelID`. Укажите голосовой канал события в настройках сервера.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        if (commandVoiceChannel == null || commandVoiceChannel.Id != eventVoiceChannelId)
                        {
                            var expectedChannel = guildChannel.Guild.GetVoiceChannel(eventVoiceChannelId);
                            var expectedText = expectedChannel != null ? $"{expectedChannel.Mention}" : $"канал с ID {eventVoiceChannelId}";
                            await command.RespondAsync($"Создавать предикты можно только из чата канала события: {expectedText}.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        if (userVoiceChannel == null || userVoiceChannel.Id != eventVoiceChannelId)
                        {
                            var expectedChannel = guildChannel.Guild.GetVoiceChannel(eventVoiceChannelId);
                            var expectedText = expectedChannel != null ? $"{expectedChannel.Mention}" : $"канал с ID {eventVoiceChannelId}";
                            await command.RespondAsync($"Чтобы создать прогноз, нужно находиться в голосовом канале события: {expectedText}.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        // Diagnostic logging to help understand why modal may be shown unexpectedly
                        try
                        {
                            await LogInfo($"Prediction create invoked: guild={guildId} user={command.User.Id} action=create commandChannel={command.Channel.Id} userVoiceChannelId={(userVoiceChannel?.Id.ToString() ?? "null")} eventVoiceChannelId={eventVoiceChannelId}");
                            var evs = guildChannel.Guild.Events.Select(e => new { e.Id, e.Name, e.Status, ChannelId = (e.Channel != null ? e.Channel.Id : 0UL), e.Location }).ToList();
                            await LogInfo($"Guild events count: {evs.Count}");
                            foreach (var ev in evs)
                            {
                                await LogInfo($"Event: id={ev.Id} name='{ev.Name}' status={ev.Status} channelId={ev.ChannelId} location='{ev.Location}'");
                            }
                        }
                        catch { }

                        if (!IsActiveEventOnChannel(userVoiceChannel.Id))
                        {
                            await command.RespondAsync("Чтобы создать прогноз, вы должны находиться в голосовом канале с активным событием.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        var targetMessageChannel = command.Channel as ISocketMessageChannel;
                        if (targetMessageChannel == null)
                        {
                            await command.RespondAsync("Не удалось получить чат канала для публикации прогноза.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        // ✅ НОВОЕ: Показываем кнопки выбора количества исходов
                        var buttonsBuilder = new ComponentBuilder()
                            .WithButton("До трёх исходов", customId: $"pred_outcomes:3:{guildId}:{targetMessageChannel.Id}", style: ButtonStyle.Primary)
                            .WithButton("До пяти исходов", customId: $"pred_outcomes:5:{guildId}:{targetMessageChannel.Id}", style: ButtonStyle.Success);

                        await command.RespondAsync("Сколько исходов вы хотите создать?", components: buttonsBuilder.Build(), ephemeral: true);
                        ScheduleDeleteOriginalResponse(command);
                        break;
                    }

                    case "bet":
                    {
                        if (user == null)
                        {
                            await command.RespondAsync("Не удалось определить пользователя.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        if (outcomeNumberOpt == null || amountOpt == null)
                        {
                            await command.RespondAsync("Укажите outcome (номер исхода) и amount (количество костяшек).", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        if (!int.TryParse(outcomeNumberOpt.ToString(), out var outcomeNum) || outcomeNum < 1)
                        {
                            await command.RespondAsync("Неверный номер исхода.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        // ✅ Проверка: исход существует
                        var activePred = _predictionService.GetActive(guildId);
                        if (activePred == null || activePred.GetOutcomeById(outcomeNum) == null)
                        {
                            var maxOutcome = activePred?.Outcomes.Count ?? 2;
                            await command.RespondAsync($"Исход должен быть от 1 до {maxOutcome}.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        if (!long.TryParse(amountOpt.ToString(), out var amount) || amount <= 0)
                        {
                            await command.RespondAsync("Сумма должна быть положительным числом.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        var (ok, error) = await _predictionService.PlaceBetAsync(guildId, user.Id, outcomeNum, amount);
                        await command.RespondAsync(ok ? $"Ставка {amount} костяшек на исход {outcomeNum} принята." : error, ephemeral: true);
                        ScheduleDeleteOriginalResponse(command);
                        break;
                    }

                    case "resolve":
                    {
                        if (user == null)
                        {
                            await command.RespondAsync("Не удалось определить пользователя.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        if (outcomeNumberOpt == null || !int.TryParse(outcomeNumberOpt.ToString(), out var outcomeNum) || outcomeNum < 1)
                        {
                            await command.RespondAsync("Укажите корректный номер исхода для завершения прогноза.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        // ✅ Проверка: исход существует
                        var activePred = _predictionService.GetActive(guildId);
                        if (activePred == null || activePred.GetOutcomeById(outcomeNum) == null)
                        {
                            var maxOutcome = activePred?.Outcomes.Count ?? 2;
                            await command.RespondAsync($"Исход должен быть от 1 до {maxOutcome}.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        var isAdmin = user.GuildPermissions.Administrator;
                        var (ok, error) = await _predictionService.ResolveAsync(guildId, user.Id, isAdmin, outcomeNum);
                        await command.RespondAsync(ok ? "Прогноз завершён." : error, ephemeral: true);
                        ScheduleDeleteOriginalResponse(command);
                        break;
                    }

                    case "cancel":
                    {
                        if (user == null)
                        {
                            await command.RespondAsync("Не удалось определить пользователя.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        var isAdmin = user.GuildPermissions.Administrator;
                        var (ok, error) = await _predictionService.CancelAsync(guildId, user.Id, isAdmin);
                        await command.RespondAsync(ok ? "Прогноз отменён. Все ставки возвращены." : error, ephemeral: true);
                        ScheduleDeleteOriginalResponse(command);
                        break;
                    }

                    case "status":
                    {
                        if (user == null)
                        {
                            await command.RespondAsync("Не удалось определить пользователя.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        try
                        {
                            var idx = _services.GetService(typeof(PointsUserIndex)) as PointsUserIndex;
                            idx?.UpsertFromUser(guildId, user);
                            if (idx != null) _ = Task.Run(() => idx.SaveAsync());
                        }
                        catch { }

                        var prediction = _predictionService.GetActive(guildId);
                        var balance = _pointsService.GetBalance(guildId, user.Id);
                        var sb = new StringBuilder();

                        sb.AppendLine($"Ваш баланс костяшек: {balance}");

                        if (prediction != null)
                        {
                            sb.AppendLine($"Текущий прогноз: {prediction.Title}");

                            if (prediction.Bets.TryGetValue(user.Id, out var bet))
                            {
                                var outcomeName = bet.OutcomeId == 1 ? prediction.Outcome1.Name : prediction.Outcome2.Name;
                                sb.AppendLine($"Ваша ставка: {bet.Amount} костяшек на исход '" +
                                              $"{outcomeName}' (#{bet.OutcomeId})");
                            }
                            else
                            {
                                sb.AppendLine(prediction.IsLocked
                                    ? "Вы не принимали участия в данном прогнозе."
                                    : "Вы ещё не участвовали в этом прогнозе.");
                            }

                            if (!prediction.IsLocked && TryGetMoscowTime(prediction.BetsCloseAtUtc.UtcDateTime, out var mskTime))
                                sb.AppendLine($"Приём ставок до: {mskTime:dd.MM.yyyy HH:mm} по МСК");
                            else if (prediction.IsLocked)
                                sb.AppendLine("Приём ставок завершён.");
                        }
                        else
                        {
                            sb.AppendLine("Сейчас нет активного прогноза.");
                        }

                        await command.RespondAsync(sb.ToString(), ephemeral: true);
                        ScheduleDeleteOriginalResponse(command);
                        break;
                    }

                    case "adjust_points":
                    {
                        if (user == null)
                        {
                            await command.RespondAsync("Не удалось определить пользователя.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        var isAdmin = user.GuildPermissions.Administrator;
                        if (!isAdmin)
                        {
                            await command.RespondAsync("Ручная корректировка баланса доступна только администраторам.", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        if (amountOpt == null || !long.TryParse(amountOpt.ToString(), out var delta) || delta == 0)
                        {
                            await command.RespondAsync("Укажите amount (целое число, можно отрицательное, но не 0).", ephemeral: true);
                            ScheduleDeleteOriginalResponse(command);
                            return;
                        }

                        var targetUserId = userOpt?.Id ?? user.Id;
                        _pointsService.Add(guildId, targetUserId, delta);
                        await _pointsService.SaveAsync();

                        var newBalance = _pointsService.GetBalance(guildId, targetUserId);
                        await command.RespondAsync($"Баланс пользователя <@{targetUserId}> изменён на {delta}. Текущий баланс: {newBalance}", ephemeral: true);
                        ScheduleDeleteOriginalResponse(command);
                        break;
                    }

                    case "history":
                    {
                        var pageOpt = command.Data.Options.FirstOrDefault(o => o.Name == "page")?.Value;
                        int page = 0;
                        if (pageOpt != null && int.TryParse(pageOpt.ToString(), out var p))
                        {
                            page = Math.Max(0, p - 1); // Пользователи вводят 1-based, храним 0-based
                        }

                        var embed = BuildHistoryEmbed(guildId, page);
                        var components = BuildHistoryComponents(guildId, page);

                        await command.RespondAsync(embed: embed, components: components?.Build(), ephemeral: true);
                        ScheduleDeleteOriginalResponse(command);
                        break;
                    }

                    case "stats":
                    {
                        var targetUser = userOpt ?? command.User;
                        var embed = BuildStatsEmbed(guildId, targetUser.Id, targetUser.Username);

                        // Статистика видна всем (не ephemeral)
                        await command.RespondAsync(embed: embed);
                        break;
                    }

                    case "achievements":
                    {
                        var embed = BuildAchievementsListEmbed(guildId);
                        await command.RespondAsync(embed: embed, ephemeral: true);
                        ScheduleDeleteOriginalResponse(command);
                        break;
                    }

                    default:
                        await command.RespondAsync("Неизвестное действие для /prediction.", ephemeral: true);
                        ScheduleDeleteOriginalResponse(command);
                        break;
                }
            }
            catch (Exception ex)
            {
                try { await PredictionErrorLogger.LogAsync("PredictionCommand", ex).ConfigureAwait(false); } catch { }
                try { await LogError($"PredictionCommand exception: {ex}"); } catch { }
                try { await command.RespondAsync("Ошибка обработки команды прогноза.", ephemeral: true); } catch { }
            }
        }

        private Embed BuildHistoryEmbed(ulong guildId, int page)
        {
            const int pageSize = 10;
            var history = _predictionService.GetHistory(guildId, page, pageSize);
            var totalPages = _predictionService.GetHistoryPageCount(guildId, pageSize);

            var eb = new EmbedBuilder()
                .WithTitle("📜 История прогнозов")
                .WithColor(Color.Blue)
                .WithFooter($"Страница {page + 1} из {Math.Max(1, totalPages)}");

            if (history.Count == 0)
            {
                eb.WithDescription("История пуста.");
                return eb.Build();
            }

            var sb = new StringBuilder();
            foreach (var entry in history)
            {
                sb.AppendLine($"**📅 {entry.EndTime:dd.MM.yyyy HH:mm}**");
                sb.AppendLine($"🎯 **{entry.Title}**");

                if (entry.WasCancelled)
                {
                    sb.AppendLine("❌ **Отменён** (все ставки возвращены)");
                    sb.AppendLine($"💎 Общий банк: {entry.TotalPool:N0} костяшек");
                }
                else if (entry.WinningOutcomeName != null)
                {
                    var winners = entry.Bets.Count(b => b.Won);
                    sb.AppendLine($"✅ **Победивший исход:** {entry.WinningOutcomeName}");
                    sb.AppendLine($"💰 **Выигрыш:** {entry.TotalPayout:N0} костяшек");
                    sb.AppendLine($"💎 **Общий банк:** {entry.TotalPool:N0} костяшек");
                    sb.AppendLine($"👥 **Участников:** {entry.Bets.Count} ({winners} выиграли)");
                }

                sb.AppendLine("───────────────────");
                sb.AppendLine();
            }

            eb.WithDescription(sb.ToString());
            return eb.Build();
        }

        private ComponentBuilder? BuildHistoryComponents(ulong guildId, int page)
        {
            const int pageSize = 10;
            var totalPages = _predictionService.GetHistoryPageCount(guildId, pageSize);

            if (totalPages <= 1)
                return null;

            var cb = new ComponentBuilder();

            if (page > 0)
            {
                cb.WithButton("◄ Назад", $"pred_history_page:{guildId}:{page - 1}", ButtonStyle.Secondary);
            }

            if (page < totalPages - 1)
            {
                cb.WithButton("Вперёд ►", $"pred_history_page:{guildId}:{page + 1}", ButtonStyle.Secondary);
            }

            return cb;
        }

        private Embed BuildStatsEmbed(ulong guildId, ulong userId, string username)
        {
            var stats = _predictionService.GetUserStats(guildId, userId);

            var eb = new EmbedBuilder()
                .WithTitle($"📊 Статистика игрока: {username}")
                .WithColor(Color.Gold);

            if (stats == null || stats.TotalBets == 0)
            {
                eb.WithDescription("Статистика отсутствует. Участвуйте в прогнозах!");
                return eb.Build();
            }

            var sb = new StringBuilder();

            // Общая статистика
            sb.AppendLine("**🎲 Общая статистика:**");
            sb.AppendLine($"  • Всего прогнозов: {stats.TotalPredictions}");
            sb.AppendLine($"  • Всего ставок: {stats.TotalBets}");
            sb.AppendLine($"  • Средняя ставка: {stats.AverageBet:N0} костяшек");
            sb.AppendLine();

            // Результаты
            sb.AppendLine("**💹 Результаты:**");
            sb.AppendLine($"  ✅ Выигрышей: {stats.Wins} ({stats.WinRate:F1}%)");
            sb.AppendLine($"  ❌ Проигрышей: {stats.Losses} ({(100 - stats.WinRate):F1}%)");

            if (stats.CurrentStreak > 0)
                sb.AppendLine($"  📈 Текущая серия: {stats.CurrentStreak} побед");
            else if (stats.CurrentStreak < 0)
                sb.AppendLine($"  📉 Текущая серия: {Math.Abs(stats.CurrentStreak)} поражений");

            sb.AppendLine($"  🔥 Лучшая серия: {stats.BestWinStreak} побед");
            sb.AppendLine();

            // Финансы
            sb.AppendLine("**💰 Финансы:**");
            sb.AppendLine($"  📥 Поставлено: {stats.TotalWagered:N0} костяшек");
            sb.AppendLine($"  📤 Выиграно: {stats.TotalWon:N0} костяшек");

            var profitSign = stats.NetProfit >= 0 ? "+" : "";
            var profitEmoji = stats.NetProfit >= 0 ? "💎" : "💔";
            sb.AppendLine($"  {profitEmoji} Чистая прибыль: {profitSign}{stats.NetProfit:N0} ({profitSign}{stats.ROI:F1}%)");

            if (stats.BiggestWin > 0)
                sb.AppendLine($"  🏆 Лучший выигрыш: +{stats.BiggestWin:N0} (коэфф. {stats.HighestCoeffWin:F2}x)");
            sb.AppendLine();

            // По типам ставок
            if (stats.FavoriteBets > 0)
            {
                var favoriteWinRate = (double)stats.FavoriteWins / stats.FavoriteBets * 100;
                sb.AppendLine("**🎯 По типам ставок:**");
                sb.AppendLine($"  📊 Фавориты (коэфф. <2x): {favoriteWinRate:F0}% побед");

                if (stats.HighCoeffWins > 0)
                    sb.AppendLine($"  🚀 Аутсайдеры (>10x): {stats.HighCoeffWins} побед");

                sb.AppendLine();
            }

            // Достижения
            var achievements = stats.Achievements.OrderBy(a => a.UnlockedAt).ToList();
            if (achievements.Count > 0)
            {
                sb.AppendLine($"**🏅 ДОСТИЖЕНИЯ ({achievements.Count}/29):**");
                sb.AppendLine();

                var grouped = achievements
                    .Select(a => (ach: a, def: RPBot.Predictions.AchievementDefinitions.All.GetValueOrDefault(a.AchievementId)))
                    .Where(x => x.def != null)
                    .GroupBy(x => x.def!.Type);

                foreach (var group in grouped)
                {
                    foreach (var (ach, def) in group.Take(5)) // Показываем первые 5 из каждой категории
                    {
                        var countStr = ach.Count > 1 ? $" (×{ach.Count})" : "";
                        sb.AppendLine($"  {def!.Icon} **{def.Name}**{countStr}");
                        sb.AppendLine($"     {def.Description}");
                        sb.AppendLine();
                    }
                }
            }
            else
            {
                sb.AppendLine("**🏅 ДОСТИЖЕНИЯ (0/29):**");
                sb.AppendLine("Участвуйте в прогнозах чтобы получать достижения!");
            }

            eb.WithDescription(sb.ToString());
            return eb.Build();
        }

        private Embed BuildAchievementsListEmbed(ulong guildId)
        {
            var eb = new EmbedBuilder()
                .WithTitle("🏅 Все достижения (29)")
                .WithColor(Color.Purple);

            var sb = new StringBuilder();

            // Группируем по типам
            var grouped = RPBot.Predictions.AchievementDefinitions.All.Values
                .GroupBy(a => a.Type)
                .OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                // Заголовок категории
                var categoryName = group.Key switch
                {
                    RPBot.Predictions.AchievementType.Beginner => "🌟 НОВИЧКОВЫЕ",
                    RPBot.Predictions.AchievementType.Financial => "💰 ФИНАНСОВЫЕ",
                    RPBot.Predictions.AchievementType.Accuracy => "🎯 ТОЧНОСТЬ",
                    RPBot.Predictions.AchievementType.Risk => "🚀 РИСК",
                    RPBot.Predictions.AchievementType.Strategy => "📈 СТРАТЕГИЯ",
                    RPBot.Predictions.AchievementType.Special => "⭐ СПЕЦИАЛЬНЫЕ",
                    _ => "ДРУГОЕ"
                };

                sb.AppendLine($"**{categoryName}**");
                sb.AppendLine();

                foreach (var def in group.OrderBy(a => a.Rarity))
                {
                    var rarityIcon = RPBot.Predictions.AchievementDefinitions.GetRarityIcon(def.Rarity);

                    // Считаем сколько людей получили
                    int ownersCount = 0;
                    foreach (var userStats in _predictionService.GetAllUserStats(guildId))
                    {
                        if (userStats.Achievements.Any(a => a.AchievementId == def.Id))
                        {
                            ownersCount++;
                        }
                    }

                    var repeatableStr = def.Repeatable ? " 🔄" : "";
                    var ownersStr = ownersCount > 0 
                        ? $"({ownersCount} {(ownersCount == 1 ? "игрок" : ownersCount < 5 ? "игрока" : "игроков")})"
                        : "_(Это достижение ещё никому не поддалось)_";

                    sb.AppendLine($"{rarityIcon} {def.Icon} **{def.Name}**{repeatableStr}");
                    sb.AppendLine($"   {def.Description}");
                    sb.AppendLine($"   {ownersStr}");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("**Легенда:**");
            sb.AppendLine("⚪ Обычное | 🟢 Редкое | 🔵 Эпик | 🟣 Легендарное");
            sb.AppendLine("🔄 - можно получить многократно");

            eb.WithDescription(sb.ToString());
            return eb.Build();
        }
    }
}
