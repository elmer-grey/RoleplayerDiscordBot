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

                        // ✅ Обновлённый модал: поддержка до 4 исходов
                        var modal = new ModalBuilder()
                            .WithTitle("Создать прогноз")
                            .WithCustomId($"pred_create_modal:{guildId}:{targetMessageChannel.Id}")
                            .AddTextInput("Заголовок", "title", TextInputStyle.Short, placeholder: "Название прогноза", maxLength: 100)
                            .AddTextInput("Исход 1", "outcome1", TextInputStyle.Short, placeholder: "Название исхода 1", maxLength: 80)
                            .AddTextInput("Исход 2", "outcome2", TextInputStyle.Short, placeholder: "Название исхода 2", maxLength: 80)
                            .AddTextInput("Исход 3 (опционально)", "outcome3", TextInputStyle.Short, placeholder: "Оставьте пустым для 2 исходов", required: false, maxLength: 80)
                            .AddTextInput("Длительность (мин)", "duration_minutes", TextInputStyle.Short, placeholder: "Например: 30", value: "30")
                            .Build();

                        await command.RespondWithModalAsync(modal);
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
    }
}
