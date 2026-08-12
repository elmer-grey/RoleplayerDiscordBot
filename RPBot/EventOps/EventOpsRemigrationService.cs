using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using Discord.WebSocket;

namespace RPBot.EventOps
{
    /// <summary>
    /// Одноразовая перерисовка старых анонсов о событиях Discord (EventOps)
    /// в каналах анонсов. Вызывается из Program.cs после Ready, если
    /// <see cref="EventAnnouncementStore.IsRemigratedOnce"/> == false.
    ///
    /// Discord не умеет "обновлять" уже отправленные сообщения автоматически,
    /// когда меняется форматирование в коде бота. Этот сервис проходит по
    /// сохранённым <c>AnnounceChannelId</c> + <c>AnnounceMessageId</c> и
    /// редактирует каждое сообщение: имя, описание, время, ссылку, обложку
    /// подтягиваются заново через Discord REST API, embed собирается по
    /// новой логике (без дублирования кода из EventAnnouncer — общий
    /// формат: «статус: название», сервер, описание, время, ссылка).
    ///
    /// После успешного прохода выставляет флаг RemigratedOnce, чтобы при
    /// последующих рестартах бот не правил embed-ы повторно.
    /// </summary>
    public sealed class EventOpsRemigrationService
    {
        private readonly EventAnnouncementStore _store;
            private readonly CancellationTokenSource _cts = new();
            // Single-flight guard: Interlocked защищает от двойного запуска при reconnect/Ready-ретраях.
            private int _running;

            public EventOpsRemigrationService(EventAnnouncementStore store)
            {
                _store = store;
            }

            /// <summary>
            /// Запрашивает отмену ремиграции (для вызова из <c>OnDisconnected</c>/shutdown).
            /// Безопасно вызывать многократно.
            /// </summary>
            public void Cancel()
            {
                try { _cts.Cancel(); } catch { }
            }

            /// <summary>
            /// Прогон по всем записям стора. Каждое сообщение:
            ///  • REST <c>RestGuild.GetEventAsync</c> для актуальных данных;
            ///  • <c>ModifyAsync</c> со свежим embed.
            /// Не падает на отдельных ошибках — логирует и идёт дальше.
            /// Если уже запущен (reconnect/Ready повтор) — вызов игнорируется.
            /// </summary>
            public async Task RunAsync(DiscordSocketClient client, CancellationToken externalCt = default)
            {
                if (client == null) throw new ArgumentNullException(nameof(client));

                // Single-flight: запрещаем параллельные прогоны.
                if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                {
                    BotLogger.Warn(LogCategory.Discord,
                        "[EventOpsRemigrate] RunAsync уже выполняется — повторный вызов игнорирован");
                    return;
                }

                try
                {
                    // Объединяем внешний токен и наш внутренний: shutdown/OnDisconnected
                    // вызовет Cancel(), и цикл по записям прервётся.
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _cts.Token);
                    var ct = linkedCts.Token;

                    var entries = _store.GetEntriesSnapshot();
                    if (entries.Count == 0)
                    {
                        BotLogger.Info(LogCategory.Discord, "[EventOpsRemigrate] нет записей в сторе, миграция не требуется");
                        _store.MarkRemigratedOnce();
                        return;
                    }

                    BotLogger.Info(LogCategory.Discord, $"[EventOpsRemigrate] старт, записей: {entries.Count}");

                    int updated = 0, missingMsg = 0, missingChannel = 0, deletedEvent = 0, errors = 0;

                    foreach (var entry in entries)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            BotLogger.Warn(LogCategory.Discord,
                                $"[EventOpsRemigrate] отменено на записи {updated + missingChannel + missingMsg + 1}/{entries.Count}");
                            break;
                        }

                        try
                        {
                            // 1. Достаём канал анонсов из кэша клиента
                            if (client.GetChannel(entry.AnnounceChannelId) is not SocketTextChannel channel)
                            {
                                BotLogger.Warn(LogCategory.Discord,
                                    $"[EventOpsRemigrate] пропуск: канал {entry.AnnounceChannelId} не найден (guild={entry.GuildId}, event={entry.EventId})");
                                missingChannel++;
                                continue;
                            }

                            // 2. Достаём само сообщение
                            var msg = await channel.GetMessageAsync(entry.AnnounceMessageId);
                            if (msg is not IUserMessage userMsg)
                            {
                                BotLogger.Warn(LogCategory.Discord,
                                    $"[EventOpsRemigrate] пропуск: сообщение {entry.AnnounceMessageId} в канале {entry.AnnounceChannelId} не найдено (вероятно удалено)");
                                missingMsg++;
                                continue;
                            }

                            // 3. Тянем актуальные данные события через REST (RestGuildEvent)
                            RestGuildEvent? liveEvent = null;
                            try
                            {
                                var restGuild = await client.Rest.GetGuildAsync(entry.GuildId);
                                if (restGuild != null)
                                {
                                    liveEvent = await restGuild.GetEventAsync(entry.EventId);
                                }
                            }
                            catch (Exception ex)
                            {
                                BotLogger.Warn(LogCategory.Discord,
                                    $"[EventOpsRemigrate] REST-проба события {entry.EventId} не удалась: {ex.Message}");
                            }
                            if (liveEvent == null) deletedEvent++;

                            // 4. Собираем embed: из живого события, иначе из сохранённого снимка
                            Embed embed = BuildEmbedFromSnapshot(entry, channel.Guild, liveEvent);

                            // 5. Редактируем
                            await userMsg.ModifyAsync(m => m.Embed = embed);
                            updated++;

                            BotLogger.Info(LogCategory.Discord,
                                $"[EventOpsRemigrate] обновлено: guild={entry.GuildId} event={entry.EventId} channel={entry.AnnounceChannelId} msg={entry.AnnounceMessageId}");

                            // Пауза между запросами, чтобы не упереться в rate limit
                            await Task.Delay(750, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            errors++;
                            BotLogger.Error(LogCategory.Discord,
                                $"[EventOpsRemigrate] ошибка обработки записи guild={entry.GuildId} event={entry.EventId}: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    _store.MarkRemigratedOnce();
                    BotLogger.Info(LogCategory.Discord,
                        $"[EventOpsRemigrate] завершено: обновлено={updated}, нет_канала={missingChannel}, нет_сообщения={missingMsg}, нет_события={deletedEvent}, ошибок={errors}");
                }
                finally
                {
                    Interlocked.Exchange(ref _running, 0);
                }
            }

        /// <summary>
        /// Собирает embed для уже отправленного анонса. Если живой
        /// <c>RestGuildEvent</c> доступен — берём актуальные поля,
        /// иначе используем сохранённый в сторе снимок (LastName и т.п.).
        /// </summary>
        private static Embed BuildEmbedFromSnapshot(EventAnnouncementEntry entry, SocketGuild guild, RestGuildEvent? liveEvent)
        {
            string eventName =
                liveEvent?.Name
                ?? entry.LastName
                ?? "(без названия)";

            string status =
                liveEvent != null ? StatusForEvent(liveEvent)
                : StatusFromLastUpdatedMark(entry.LastUpdatedMark);

            string? description =
                liveEvent?.Description
                ?? entry.LastDescription;

            ulong? channelId =
                liveEvent?.ChannelId
                ?? entry.LastChannelId;

            string? location =
                liveEvent?.Location
                ?? entry.LastLocation;

            DateTimeOffset? startTime =
                liveEvent?.StartTime
                ?? entry.LastStartTimeUtc;

            string? coverUrl =
                liveEvent?.CoverImageId != null
                    ? $"https://cdn.discordapp.com/guild-events/{liveEvent.Id}/{liveEvent.CoverImageId}.png?size=1024"
                    : entry.LastCoverImageUrl;

            var builder = new EmbedBuilder()
                .WithTitle($"{status}: {eventName}")
                .WithColor(liveEvent != null ? ColorForEvent(liveEvent) : Color.Orange)
                .WithCurrentTimestamp();

            if (!string.IsNullOrWhiteSpace(description))
            {
                builder.WithDescription(description.Length <= 2048
                    ? description
                    : description.Substring(0, 2047) + "…");
            }

            if (!string.IsNullOrWhiteSpace(coverUrl))
                builder.WithImageUrl(coverUrl);

            // Ссылка на событие (для ресинк-сценария всегда указываем текущий guildId/eventId)
            var eventUrl = $"https://discord.com/events/{entry.GuildId}/{entry.EventId}";
            builder.WithUrl(eventUrl);

            builder.AddField("🏰 Сервер", guild?.Name ?? $"#{entry.GuildId}", true);
            if (channelId.HasValue && channelId.Value != 0)
            {
                builder.AddField("📍 Где", $"<#{channelId.Value}>", true);
            }
            else if (!string.IsNullOrWhiteSpace(location))
            {
                builder.AddField("📍 Где", location, true);
            }
            if (startTime.HasValue)
            {
                builder.AddField("🕒 Когда", $"<t:{startTime.Value.ToUnixTimeSeconds()}:F>", true);
            }

            builder.WithFooter("Чтобы приходило в личку: /event_notify subscribe • Выкл: напиши «стоп» • Вкл: «хочу»");

            return builder.Build();
        }

        private static string StatusForEvent(RestGuildEvent ev)
        {
            switch (ev.Status)
            {
                case GuildScheduledEventStatus.Scheduled: return "📅 Событие запланировано";
                case GuildScheduledEventStatus.Active: return "▶️ Событие началось";
                case GuildScheduledEventStatus.Completed: return "✅ Событие завершено";
                case GuildScheduledEventStatus.Cancelled: return "❌ Событие отменено";
                default: return "ℹ️ Событие";
            }
        }

        private static Color ColorForEvent(RestGuildEvent ev)
        {
            return ev.Status switch
            {
                GuildScheduledEventStatus.Active => Color.Green,
                GuildScheduledEventStatus.Completed => Color.DarkGreen,
                GuildScheduledEventStatus.Cancelled => Color.DarkRed,
                _ => Color.Orange,
            };
        }

        private static string StatusFromLastUpdatedMark(string? mark)
        {
            // Если событие удалено из Discord и из стора мы знаем только прошлую метку —
            // показываем нейтральный заголовок.
            return mark switch
            {
                "started" => "▶️ Событие началось",
                "completed" => "✅ Событие завершено",
                "cancelled" => "❌ Событие отменено",
                _ => "📅 Событие (источник удалён)",
            };
        }
    }
}