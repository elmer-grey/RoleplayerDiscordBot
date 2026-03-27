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

                    if (string.IsNullOrWhiteSpace(titleOpt) || string.IsNullOrWhiteSpace(outcome1Opt) || string.IsNullOrWhiteSpace(outcome2Opt))
                    {
                        await command.RespondAsync("Для создания прогноза укажите title, outcome1 и outcome2.", ephemeral: true);
                        return;
                    }

                    var hasActiveEventInChannel = guildChannel.Guild.Events.Any(e =>
                        e.Status == GuildScheduledEventStatus.Active &&
                        e.Channel != null &&
                        e.Channel.Id == guildChannel.Id);

                    if (!hasActiveEventInChannel)
                    {
                        await command.RespondAsync("Создать прогноз можно только при активном событии в этом канале.", ephemeral: true);
                        return;
                    }

                    if (durationOpt == null || !int.TryParse(durationOpt.ToString(), out var parsed) || parsed <= 0)
                    {
                        await command.RespondAsync("Для создания прогноза укажите duration_minutes (> 0).", ephemeral: true);
                        return;
                    }

                    var minutes = parsed;

                    var targetMessageChannel = command.Channel as ISocketMessageChannel;
                    if (targetMessageChannel == null)
                    {
                        await command.RespondAsync("Не удалось получить чат канала для публикации прогноза.", ephemeral: true);
                        return;
                    }

                    var (ok, error, _) = await _predictionService.CreateAsync(
                        guildId,
                        user.Id,
                        targetMessageChannel,
                        titleOpt,
                        outcome1Opt,
                        outcome2Opt,
                        TimeSpan.FromMinutes(minutes));

                    await command.RespondAsync(ok ? $"Прогноз создан: {titleOpt}" : error, ephemeral: true);
                    break;
                }

                case "bet":
                {
                    if (user == null)
                    {
                        await command.RespondAsync("Не удалось определить пользователя.", ephemeral: true);
                        return;
                    }

                    if (outcomeNumberOpt == null || amountOpt == null)
                    {
                        await command.RespondAsync("Укажите outcome (1 или 2) и amount (количество костяшек).", ephemeral: true);
                        return;
                    }

                    if (!int.TryParse(outcomeNumberOpt.ToString(), out var outcomeNum) || (outcomeNum != 1 && outcomeNum != 2))
                    {
                        await command.RespondAsync("Исход должен быть 1 или 2.", ephemeral: true);
                        return;
                    }

                    if (!long.TryParse(amountOpt.ToString(), out var amount) || amount <= 0)
                    {
                        await command.RespondAsync("Сумма должна быть положительным числом.", ephemeral: true);
                        return;
                    }

                    var (ok, error) = await _predictionService.PlaceBetAsync(guildId, user.Id, outcomeNum, amount);
                    await command.RespondAsync(ok ? $"Ставка {amount} костяшек на исход {outcomeNum} принята." : error, ephemeral: true);
                    break;
                }

                case "resolve":
                {
                    if (user == null)
                    {
                        await command.RespondAsync("Не удалось определить пользователя.", ephemeral: true);
                        return;
                    }

                    if (outcomeNumberOpt == null || !int.TryParse(outcomeNumberOpt.ToString(), out var outcomeNum) || (outcomeNum != 1 && outcomeNum != 2))
                    {
                        await command.RespondAsync("Укажите outcome (1 или 2) для завершения прогноза.", ephemeral: true);
                        return;
                    }

                    var isAdmin = user.GuildPermissions.Administrator;
                    var (ok, error) = await _predictionService.ResolveAsync(guildId, user.Id, isAdmin, outcomeNum);
                    await command.RespondAsync(ok ? "Прогноз завершён." : error, ephemeral: true);
                    break;
                }

                case "cancel":
                {
                    if (user == null)
                    {
                        await command.RespondAsync("Не удалось определить пользователя.", ephemeral: true);
                        return;
                    }

                    var isAdmin = user.GuildPermissions.Administrator;
                    var (ok, error) = await _predictionService.CancelAsync(guildId, user.Id, isAdmin);
                    await command.RespondAsync(ok ? "Прогноз отменён. Все ставки возвращены." : error, ephemeral: true);
                    break;
                }

                case "status":
                {
                    if (user == null)
                    {
                        await command.RespondAsync("Не удалось определить пользователя.", ephemeral: true);
                        return;
                    }

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
                    break;
                }

                case "adjust_points":
                {
                    if (user == null)
                    {
                        await command.RespondAsync("Не удалось определить пользователя.", ephemeral: true);
                        return;
                    }

                    var isAdmin = user.GuildPermissions.Administrator;
                    if (!isAdmin)
                    {
                        await command.RespondAsync("Ручная корректировка баланса доступна только администраторам.", ephemeral: true);
                        return;
                    }

                    if (amountOpt == null || !long.TryParse(amountOpt.ToString(), out var delta) || delta == 0)
                    {
                        await command.RespondAsync("Укажите amount (целое число, можно отрицательное, но не 0).", ephemeral: true);
                        return;
                    }

                    var targetUserId = userOpt?.Id ?? user.Id;
                    _pointsService.Add(guildId, targetUserId, delta);
                    await _pointsService.SaveAsync();

                    var newBalance = _pointsService.GetBalance(guildId, targetUserId);
                    await command.RespondAsync($"Баланс пользователя <@{targetUserId}> изменён на {delta}. Текущий баланс: {newBalance}", ephemeral: true);
                    break;
                }

                default:
                    await command.RespondAsync("Неизвестное действие для /prediction.", ephemeral: true);
                    break;
            }
        }
    }
}
