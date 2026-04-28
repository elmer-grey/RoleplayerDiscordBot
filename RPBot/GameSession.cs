using Discord;
using Discord.Commands;
using Discord.WebSocket;
using RPBot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot
{
    public class RollStatistic
    {
        public string PlayerName { get; set; }
        public int RollValue { get; set; }
        public string DiceType { get; set; }  // ✅ НОВОЕ: Тип куба (d6, d12, d20 и т.д.)
    }

    public class GameSession
    {
        public ulong SessionId { get; set; }
        public ulong GuildId { get; set; }
        public string GameName { get; set; }
        public string GameComment { get; set; }
        public string MasterName { get; set; }
        public ulong MasterId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public List<(DateTime Start, DateTime? End)> PausePeriods { get; set; } = new();
        public bool IsPaused { get; set; }
        public bool IsStopped => EndTime.HasValue;
        public List<RollStatistic> Rolls { get; set; } = new();
        public string EventDescription { get; set; }
        public ulong ControlMessageId { get; set; }
        public ulong StatsMessageId { get; set; }
        public CancellationTokenSource PauseReminderCTS { get; set; }
        public bool TrackRolls { get; set; }
        public ulong? EventId { get; set; }
        public ulong ChannelId { get; set; }
        public ulong? PauseReminderMessageId { get; set; }
        public ulong? ConfirmationMessageId { get; set; }
        public bool StatsSent { get; set; }
        public object StatsSync { get; } = new();
    }

    public class GameSessionCommands : ModuleBase<SocketCommandContext>
    {
        private readonly DiscordSocketClient _client;
        public static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, GameSession>> _sessions = new();
        private static readonly SemaphoreSlim _sessionSemaphore = new(1, 1);

        // ✅ НОВОЕ: Сохранение/восстановление сессий
        private static readonly string _sessionsStatePath = Path.Combine(AppContext.BaseDirectory, "Data", "sessions_state.json");

        public GameSessionCommands(DiscordSocketClient client) => _client = client;

        private async void Log(string message)
        {
            Program.CommandLogSink?.Invoke(message);
        }

        // ✅ НОВОЕ: Сохранение активных сессий в файл
        private static async Task SaveSessionsAsync()
        {
            try
            {
                var dataDir = Path.GetDirectoryName(_sessionsStatePath);
                if (!Directory.Exists(dataDir))
                    Directory.CreateDirectory(dataDir);

                var sessionsToSave = new Dictionary<string, object>();

                foreach (var guild in _sessions)
                {
                    foreach (var session in guild.Value.Values.Where(s => !s.IsStopped))
                    {
                        var key = $"{guild.Key}:{session.SessionId}";
                        sessionsToSave[key] = new
                        {
                            session.SessionId,
                            session.GuildId,
                            session.ChannelId,
                            session.GameName,
                            session.MasterName,
                            session.MasterId,
                            session.StartTime,
                            session.EventDescription,
                            session.GameComment,
                            session.EventId,
                            session.ControlMessageId,
                            session.IsPaused,
                            // ✅ НОВОЕ: Сохраняем броски
                            Rolls = session.Rolls
                        };
                    }
                }

                var json = System.Text.Json.JsonSerializer.Serialize(sessionsToSave, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_sessionsStatePath, json).ConfigureAwait(false);

                if (sessionsToSave.Count > 0)
                {
                    var totalRolls = _sessions.Values.SelectMany(g => g.Values.Where(s => !s.IsStopped)).Sum(s => s.Rolls.Count);
                    Console.WriteLine($"[SESSIONS] Сохранено {sessionsToSave.Count} активных сессий ({totalRolls} бросков)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SESSIONS] Ошибка при сохранении сессий: {ex.Message}");
            }
        }

        // ✅ НОВОЕ: Загрузка сессий из файла при рестарте
        public static async Task LoadSessionsAsync(DiscordSocketClient client)
        {
            try
            {
                if (!File.Exists(_sessionsStatePath))
                {
                    Console.WriteLine("[SESSIONS] Файл сохранённых сессий не найден");
                    return;
                }

                var json = await File.ReadAllTextAsync(_sessionsStatePath).ConfigureAwait(false);
                var doc = System.Text.Json.JsonDocument.Parse(json);

                var commands = new GameSessionCommands(client);
                int restorCount = 0;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    try
                    {
                        var elem = prop.Value;
                        var sessionId = elem.GetProperty("SessionId").GetUInt64();
                        var guildId = elem.GetProperty("GuildId").GetUInt64();
                        var gameName = elem.GetProperty("GameName").GetString() ?? "Unknown";
                        var masterName = elem.GetProperty("MasterName").GetString() ?? "Unknown";
                        var masterId = elem.GetProperty("MasterId").GetUInt64();
                        var startTime = DateTime.Parse(elem.GetProperty("StartTime").GetString() ?? DateTime.Now.ToString());
                        var eventDescription = elem.TryGetProperty("EventDescription", out var ed) ? ed.GetString() : null;
                        var gameComment = elem.TryGetProperty("GameComment", out var gc) ? gc.GetString() : null;
                        var eventId = elem.TryGetProperty("EventId", out var eid) && eid.ValueKind != System.Text.Json.JsonValueKind.Null ? (ulong?)eid.GetUInt64() : null;
                        var controlMessageId = elem.GetProperty("ControlMessageId").GetUInt64();
                        var channelId = elem.GetProperty("ChannelId").GetUInt64();
                        var isPaused = elem.TryGetProperty("IsPaused", out var ip) && ip.GetBoolean();

                        // ✅ НОВОЕ: Загружаем броски
                        var rolls = new List<RollStatistic>();
                        if (elem.TryGetProperty("Rolls", out var rollsElem) && rollsElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var rollElem in rollsElem.EnumerateArray())
                            {
                                try
                                {
                                    var roll = new RollStatistic
                                    {
                                        PlayerName = rollElem.GetProperty("PlayerName").GetString() ?? "Unknown",
                                        RollValue = rollElem.GetProperty("RollValue").GetInt32(),
                                        DiceType = rollElem.TryGetProperty("DiceType", out var dt) ? dt.GetString() : "unknown"
                                    };
                                    rolls.Add(roll);
                                }
                                catch { }
                            }
                        }

                        var session = new GameSession
                        {
                            SessionId = sessionId,
                            GuildId = guildId,
                            ChannelId = channelId,
                            GameName = gameName,
                            MasterName = masterName,
                            MasterId = masterId,
                            StartTime = startTime,
                            EventDescription = eventDescription,
                            GameComment = gameComment,
                            EventId = eventId,
                            ControlMessageId = controlMessageId,
                            IsPaused = isPaused,
                            TrackRolls = false,
                            Rolls = rolls  // ✅ НОВОЕ: Добавляем загруженные броски
                        };

                        if (!_sessions.TryGetValue(guildId, out var guildSessions))
                        {
                            guildSessions = new ConcurrentDictionary<ulong, GameSession>();
                            _sessions[guildId] = guildSessions;
                        }

                        if (guildSessions.TryAdd(sessionId, session))
                        {
                            restorCount++;
                            // ✅ НОВОЕ: Логируем также количество восстановленных бросков
                            Console.WriteLine($"[SESSIONS] Восстановлена сессия {sessionId}: \"{gameName}\" (мастер: {masterName}, бросков: {rolls.Count})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SESSIONS] Ошибка при восстановлении сессии из {prop.Name}: {ex.Message}");
                    }
                }

                if (restorCount > 0)
                {
                    var totalRestorRolls = _sessions.Values.SelectMany(g => g.Values).Sum(s => s.Rolls.Count);
                    Console.WriteLine($"[SESSIONS] Восстановлено {restorCount} сессий ({totalRestorRolls} бросков)");

                    // Пересоздаём сообщения управления
                    _ = Task.Run(async () => await RecreateControlMessagesAsync(client));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SESSIONS] Ошибка при загрузке сессий: {ex.Message}");
            }
        }

        // ✅ НОВОЕ: Пересоздание сообщений управления для восстановленных сессий
        private static async Task RecreateControlMessagesAsync(DiscordSocketClient client)
        {
            try
            {
                var commands = new GameSessionCommands(client);
                int recreatedCount = 0;

                foreach (var guild in _sessions)
                {
                    foreach (var session in guild.Value.Values.ToList())
                    {
                        if (session.ControlMessageId != 0)
                            continue; // Уже есть сообщение

                        try
                        {
                            var channel = client.GetChannel(session.ChannelId) as ITextChannel
                                ?? client.GetGuild(session.GuildId)?.GetTextChannel(session.ChannelId);

                            if (channel == null)
                                continue;

                            var embed = new EmbedBuilder()
                                .WithTitle($"Сессия: \"{session.GameName}\"")
                                .WithDescription($"Мастер: {session.MasterName}\n" +
                                               $"Начало: {session.StartTime:dd.MM.yyyy HH:mm}\n" +
                                               $"Статус: {(session.IsPaused ? "⏸ На паузе" : "▶ В процессе")}\n" +
                                               $"Сбор бросков: {(session.TrackRolls ? "✅ Включен" : "❌ Выключен")}\n" +
                                               $"{(string.IsNullOrEmpty(session.EventDescription) ? "" : $"Описание: {session.EventDescription}")}")
                                .WithColor(session.IsPaused ? Color.Orange : Color.Green)
                                .Build();

                            var buttons = commands.CreateControlButtons(session);
                            var message = await channel.SendMessageAsync(embed: embed, components: buttons.Build());
                            session.ControlMessageId = message.Id;

                            recreatedCount++;
                            Console.WriteLine($"[SESSIONS] Пересоздано сообщение управления для сессии {session.SessionId}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[SESSIONS] Ошибка при пересоздании сообщения для сессии {session.SessionId}: {ex.Message}");
                        }
                    }
                }

                if (recreatedCount > 0)
                    Console.WriteLine($"[SESSIONS] Пересоздано {recreatedCount} сообщений управления");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SESSIONS] Ошибка при пересоздании сообщений: {ex.Message}");
            }
        }

        private async Task<GameSession> StartSessionInternal(
        ulong guildId,
        string gameName,
        SocketGuildUser master,
        string gameComment = null,
        string eventDescription = null,
        ulong? eventId = null,
        ulong channelId = 0)
        {
            await _sessionSemaphore.WaitAsync();
            try
            {
                Log($"Попытка создать сессию для гильдии {guildId}, игра: \"{gameName}\"");

                if (eventId.HasValue && _sessions.TryGetValue(guildId, out var guildSessions))
                {
                    var existingSession = guildSessions.Values.FirstOrDefault(s => s.EventId == eventId && !s.IsStopped);
                    if (existingSession != null)
                    {
                        Log($"Найдена существующая активная сессия для события {eventId}: ID {existingSession.SessionId}");
                        return existingSession;
                    }
                }

                var newSession = new GameSession
                {
                    SessionId = (ulong)DateTime.Now.Ticks,
                    GuildId = guildId,
                    ChannelId = channelId,
                    GameName = gameName,
                    MasterName = master?.DisplayName ?? "Неопознанный мастер",
                    MasterId = master?.Id ?? 0,
                    GameComment = gameComment,
                    EventDescription = eventDescription,
                    StartTime = DateTime.Now,
                    EventId = eventId,
                    TrackRolls = false
                };

                if (!_sessions.TryGetValue(guildId, out var sessions))
                {
                    sessions = new ConcurrentDictionary<ulong, GameSession>();
                    _sessions[guildId] = sessions;
                    Log($"Создан новый словарь сессий для гильдии {guildId}");
                }

                if (sessions.TryAdd(newSession.SessionId, newSession))
                {
                    Log($"Успешно создана новая сессия: ID {newSession.SessionId}, игра: \"{gameName}\"");
                }
                else
                {
                    Log($"Ошибка при создании сессии для игры \"{gameName}\"");
                }

                return newSession;
            }
            catch (Exception ex)
            {
                Log($"Ошибка при создании сессии: {ex.Message}");
                throw;
            }
            finally
            {
                _sessionSemaphore.Release();
            }
        }

        private ComponentBuilder CreateControlButtons(GameSession session)
        {
            if (session.IsStopped)
                return new ComponentBuilder();

            var builder = new ComponentBuilder()
                .WithButton(session.IsPaused ? "▶️ Продолжить" : "⏸ Пауза",
                    session.IsPaused ? $"resume_session:{session.SessionId}" : $"pause_session:{session.SessionId}",
                    ButtonStyle.Secondary)
                .WithButton("✏️ Изменить", $"edit_session:{session.SessionId}", ButtonStyle.Primary)
                .WithButton("⏹ Завершить", $"stop_session:{session.SessionId}", ButtonStyle.Danger)
                .WithButton(session.TrackRolls ? "🎲 Броски: ✅" : "🎲 Броски: ❌",
                    $"toggle_rolls:{session.SessionId}",
                    ButtonStyle.Success);

            return builder;
        }

        private async Task UpdateControlMessage(GameSession session, IMessageChannel channel)
        {
            try
            {
                Log($"Попытка обновить сообщение управления для сессии {session.SessionId}");

                if (session.ControlMessageId == 0)
                {
                    Log($"ControlMessageId = 0 для сессии {session.SessionId}");
                    return;
                }

                var message = await channel.GetMessageAsync(session.ControlMessageId) as IUserMessage;
                if (message == null)
                {
                    Log($"Сообщение {session.ControlMessageId} не найдено, попытка найти по содержимому...");
                    var messages = await channel.GetMessagesAsync(10).FlattenAsync();
                    message = messages.FirstOrDefault(m =>
                        m.Embeds.FirstOrDefault()?.Title?.Contains(session.GameName) == true) as IUserMessage;

                    if (message == null)
                    {
                        Log($"Сообщение для сессии {session.SessionId} не найдено");
                        return;
                    }
                    session.ControlMessageId = message.Id;
                    Log($"Найдено сообщение по содержимому: ID {message.Id}");
                }

                // Получаем последний период паузы (текущий)
                var currentPause = session.PausePeriods.LastOrDefault();
                var pauseTimeInfo = currentPause.Start != DateTime.MinValue ?
                    $"\nНа паузе с: {currentPause.Start:HH:mm}" : "";

                var statusText = session.IsStopped
                    ? "✅ Завершена"
                    : session.IsPaused
                        ? $"⏸ На паузе{pauseTimeInfo}"
                        : "▶ В процессе";

                var descriptionLines = new List<string>
                {
                    $"Мастер: {session.MasterName}",
                    $"Начало: {session.StartTime:dd.MM.yyyy HH:mm}",
                    $"Статус: {statusText}",
                    $"Сбор бросков: {(session.TrackRolls ? "✅ Включен" : "❌ Выключен")}",
                };

                if (!string.IsNullOrWhiteSpace(session.EventDescription))
                    descriptionLines.Add($"Описание: {session.EventDescription}");

                if (!string.IsNullOrWhiteSpace(session.GameComment))
                    descriptionLines.Add($"Комментарий: {session.GameComment}");

                var embed = new EmbedBuilder()
                    .WithTitle($"Сессия: \"{session.GameName}\"")
                    .WithDescription(string.Join("\n", descriptionLines))
                    .WithColor(session.IsStopped ? Color.DarkGrey : session.IsPaused ? Color.Orange : Color.Green)
                    .Build();

                await message.ModifyAsync(m =>
                {
                    m.Embed = embed;
                    m.Components = CreateControlButtons(session).Build();
                });

                Log($"Сообщение управления для сессии {session.SessionId} успешно обновлено");
            }
            catch (Exception ex)
            {
                Log($"Ошибка при обновлении сообщения управления: {ex.Message}");
            }
        }

        public static async Task OnGuildScheduledEventStarted(SocketGuildEvent guildEvent, DiscordSocketClient client)
        {
            var guildId = guildEvent.Guild.Id;
            var creator = guildEvent.Creator as SocketGuildUser;

            var commands = new GameSessionCommands(client);
            var session = await commands.StartSessionInternal(
                guildId,
                guildEvent.Name,
                creator,
                eventDescription: guildEvent.Description,
                eventId: guildEvent.Id);

            if (session == null)
            {
                commands.Log("Не удалось создать сессию для события");
                return;
            }

            ulong channelId = Program.ServerConfigs.TryGetValue(guildId, out var config)
                ? config.RecordChannelID
                : guildEvent.Guild.SystemChannel.Id;

            if (client.GetChannel(channelId) is ITextChannel channel)
            {
                var embed = new EmbedBuilder()
                    .WithTitle($"Сессия: \"{session.GameName}\"")
                    .WithDescription($"Мастер: {session.MasterName}\n" +
                                   $"Начало: {session.StartTime:dd.MM.yyyy HH:mm}\n" +
                                   $"Статус: ▶ В процессе\n" +
                                   $"Сбор бросков: ❌ Выключен\n" +
                                   $"{(string.IsNullOrEmpty(session.EventDescription) ? "" : $"Описание: {session.EventDescription}")}")
                    .WithColor(Color.Green)
                    .Build();

                var buttons = commands.CreateControlButtons(session);
                var message = await channel.SendMessageAsync(embed: embed, components: buttons.Build());
                session.ControlMessageId = message.Id;

                commands.Log($"Создано сообщение управления для сессии {session.SessionId} (ID сообщения: {message.Id})");
            }
            else
            {
                commands.Log($"Не удалось найти канал {channelId} для создания сообщения управления");
            }
        }

        [Command("start")]
        public async Task StartGameSession(
            SocketSlashCommand command,
            string gameName,
            SocketUser? masterUser = null,
            string? gameComment = null)
        {
            await command.DeferAsync();
            var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId == null) return;

            var user = command.User as SocketGuildUser;
            if (!user.Roles.Any(r => r.Name.Equals("Мастер НРИ", StringComparison.OrdinalIgnoreCase)))
            {
                await SendTemporaryEphemeralResponse(command, "Только мастера могут запускать игру.");
                return;
            }

            var master = masterUser as SocketGuildUser ?? user;
            var session = await StartSessionInternal(
                guildId.Value,
                gameName,
                master,
                gameComment,
                channelId: command.Channel.Id);

            if (session == null)
            {
                await SendTemporaryEphemeralResponse(command, "Не удалось создать сессию.");
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle($"Сессия: \"{gameName}\"")
                .WithDescription($"Мастер: {master.DisplayName}\n" +
                               $"Начало: {session.StartTime:dd.MM.yyyy HH:mm}\n" +
                               $"Статус: ▶ В процессе\n" +
                               $"Сбор бросков: ❌ Выключен\n" +
                               $"{(string.IsNullOrEmpty(gameComment) ? "" : $"Комментарий: {gameComment}")}")
                .WithColor(Color.Green)
                .Build();

            var buttons = CreateControlButtons(session);
            var message = await command.FollowupAsync(embed: embed, components: buttons.Build());
            session.ControlMessageId = message.Id;
        }

        public async Task HandleControlButton(SocketMessageComponent component)
        {
            var guildId = (component.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId == null) return;

            var channelId = component.Channel.Id;

            var parts = component.Data.CustomId.Split(':');
            if (parts.Length < 2 || !ulong.TryParse(parts[1], out var sessionId))
            {
                await SendTemporaryEphemeralResponse(component, "Не удалось определить сессию.");
                return;
            }

            try
            {
                if (!_sessions.TryGetValue(guildId.Value, out var guildSessions))
                {
                    Log($"[RESTART] Сессии для сервера {guildId} не найдены (вероятно бот был перезагружен)");
                    await SendTemporaryEphemeralResponse(component, "❌ Сессия больше не активна. Это может произойти если бот был перезагружен.\n\nПожалуйста, создайте новую сессию командой `/start`");
                    return;
                }

                if (!guildSessions.TryGetValue(sessionId, out var session))
                {
                    Log($"[RESTART] Сессия {sessionId} не найдена (вероятно бот был перезагружен)");
                    await SendTemporaryEphemeralResponse(component, "❌ Эта сессия больше не активна. Это может произойти если бот был перезагружен.\n\nПожалуйста, создайте новую сессию командой `/start`");
                    return;
                }

                switch (parts[0])
                {
                    case "pause_session":
                    case "resume_session":
                    case "stop_session":
                    case "confirm_stop":
                    case "cancel_stop":
                    case "toggle_rolls":
                        await component.DeferAsync();
                        break;
                }

                switch (parts[0])
                {
                    case "pause_session":
                        await HandlePauseSession(component, session);
                        break;
                    case "resume_session":
                        await HandleResumeSession(component, session);
                        break;
                    case "edit_session":
                        await HandleEditSession(component, session);
                        break;
                    case "stop_session":
                        await HandleStopSession(component, session);
                        break;
                    case "confirm_stop":
                        await HandleConfirmStop(component, session);
                        break;
                    case "cancel_stop":
                        await HandleCancelStop(component, session);
                        break;
                    case "toggle_rolls":
                        await HandleToggleRolls(component, session);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка обработки кнопки: {ex.Message}");
            }
        }

        private async Task HandlePauseSession(SocketMessageComponent component, GameSession session)
        {
            if (session.IsPaused)
            {
                Log($"Сессия {session.SessionId} уже на паузе");
                await SendTemporaryEphemeralResponse(component, "Игра уже на паузе.");
                return;
            }

            var pauseStartTime = DateTime.Now;
            session.PausePeriods.Add((pauseStartTime, null));
            session.IsPaused = true;

            Log($"Сессия {session.SessionId} поставлена на паузу в {pauseStartTime:HH:mm:ss}");

            await UpdateControlMessage(session, component.Channel);
            await SendTemporaryEphemeralResponse(component, $"Игра приостановлена в {pauseStartTime:HH:mm}");

            // ✅ НОВОЕ: Сохраняем состояние
            _ = Task.Run(() => SaveSessionsAsync());

            var channel = component.Channel;
            var userId = component.User.Id;

            if (session.PauseReminderCTS != null)
            {
                try { session.PauseReminderCTS.Cancel(); } catch { }
                try { session.PauseReminderCTS.Dispose(); } catch { }
                session.PauseReminderCTS = null;
            }
            session.PauseReminderCTS = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                // Первое напоминание через 10 минут от начала паузы
                var nextReminder = TimeSpan.FromMinutes(10);

                while (!session.PauseReminderCTS.IsCancellationRequested)
                {
                    // Ждем до следующего напоминания
                    var delay = nextReminder - (DateTime.Now - pauseStartTime);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, session.PauseReminderCTS.Token);
                    }

                    try
                    {
                        var duration = DateTime.Now - pauseStartTime;
                        var totalMinutes = (int)duration.TotalMinutes;

                        // Проверяем, что прошло ровное количество 10-минутных интервалов
                        if (totalMinutes % 10 == 0)
                        {
                            Log($"Напоминание о паузе для сессии {session.SessionId} (длительность: {totalMinutes} мин)");

                            var user = await channel.GetUserAsync(userId) as IUser;
                            if (user != null)
                            {
                                var reminderMessage = await channel.SendMessageAsync(
                                    $"{user.Mention}, игра на паузе с {pauseStartTime:HH:mm} (уже {totalMinutes} мин)");

                                session.PauseReminderMessageId = reminderMessage.Id;

                                await Task.Delay(TimeSpan.FromMinutes(2));
                                try
                                {
                                    await reminderMessage.DeleteAsync();
                                }
                                catch { }
                            }
                        }

                        nextReminder += TimeSpan.FromMinutes(10);
                    }
                    catch (Exception ex)
                    {
                        Log($"Ошибка в напоминании о паузе: {ex.Message}");
                        nextReminder += TimeSpan.FromMinutes(10);
                    }
                }
            }, session.PauseReminderCTS.Token);
        }

        private async Task HandleResumeSession(SocketMessageComponent component, GameSession session)
        {
            if (!session.IsPaused)
            {
                Log($"Попытка возобновить сессию {session.SessionId}, которая не на паузе");
                await SendTemporaryEphemeralResponse(component, "Игра не на паузе.");
                return;
            }

            session.PauseReminderCTS?.Cancel();
            try { session.PauseReminderCTS?.Dispose(); } catch { }
            session.PauseReminderCTS = null;
            var lastPause = session.PausePeriods.Last();
            session.PausePeriods[^1] = (lastPause.Start, DateTime.Now);
            session.IsPaused = false;

            Log($"Сессия {session.SessionId} возобновлена после паузы");

            await UpdateControlMessage(session, component.Channel);
            await SendTemporaryEphemeralResponse(component, "Игра продолжена.");

            // ✅ НОВОЕ: Сохраняем состояние
            _ = Task.Run(() => SaveSessionsAsync());
        }

        private async Task HandleEditSession(SocketMessageComponent component, GameSession session)
        {
            var modal = new ModalBuilder()
                .WithTitle("Изменение параметров игры")
                .WithCustomId($"edit_modal:{session.SessionId}")
                .AddTextInput("Название игры", "game_name", TextInputStyle.Short, value: session.GameName)
                .AddTextInput("Мастер", "game_master", TextInputStyle.Short, value: session.MasterName)
                .AddTextInput("Комментарий", "game_comment", TextInputStyle.Paragraph, value: session.GameComment ?? "", required: false)
                .Build();

            await component.RespondWithModalAsync(modal);
        }

        public async Task HandleEditModal(SocketModal modal)
        {
            await modal.DeferAsync();
            var guildId = (modal.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId == null) return;

            var parts = modal.Data.CustomId.Split(':');
            if (parts.Length < 2 || !ulong.TryParse(parts[1], out var sessionId))
            {
                await SendTemporaryEphemeralResponse(modal, "Не удалось определить сессию.");
                return;
            }

            await _sessionSemaphore.WaitAsync();
            try
            {
                if (!_sessions.TryGetValue(guildId.Value, out var guildSessions))
                {
                    await SendTemporaryEphemeralResponse(modal, "Активные сессии не найдены.");
                    return;
                }

                if (!guildSessions.TryGetValue(sessionId, out var session))
                {
                    await SendTemporaryEphemeralResponse(modal, "Сессия не найдена.");
                    return;
                }

                var newName = (modal.Data.Components.First(x => x.CustomId == "game_name").Value ?? string.Empty).Trim();
                var newMaster = (modal.Data.Components.First(x => x.CustomId == "game_master").Value ?? string.Empty).Trim();
                var newCommentRaw = modal.Data.Components.First(x => x.CustomId == "game_comment").Value;
                var newComment = NormalizeOptionalText(newCommentRaw);
                var oldComment = NormalizeOptionalText(session.GameComment);

                var changes = new List<string>();
                if (session.GameName != newName) changes.Add($"Название: {session.GameName} → {newName}");
                if (session.MasterName != newMaster) changes.Add($"Мастер: {session.MasterName} → {newMaster}");
                if (!string.Equals(oldComment, newComment, StringComparison.Ordinal))
                    changes.Add($"Комментарий: {(oldComment ?? "(пусто)")} → {(newComment ?? "(пусто)")}");

                if (changes.Count == 0)
                {
                    await modal.FollowupAsync("Изменений не внесено.", ephemeral: true);
                    return;
                }

                session.GameName = newName;
                session.MasterName = newMaster;
                session.GameComment = newComment;

                await UpdateControlMessage(session, modal.Channel);
                await SendTemporaryEphemeralResponse(modal, $"Изменения сохранены:\n{string.Join("\n", changes)}");
            }
            finally
            {
                _sessionSemaphore.Release();
            }
        }

        private static string? NormalizeOptionalText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return value.Trim();
        }

        private async Task HandleStopSession(SocketMessageComponent component, GameSession session)
        {
            Log($"Пользователь {component.User.Id} запросил остановку сессии {session.SessionId} ({session.GameName})");

            var confirmBuilder = new ComponentBuilder()
                .WithButton("✅ Да", $"confirm_stop:{session.SessionId}", ButtonStyle.Danger)
                .WithButton("❌ Нет", $"cancel_stop:{session.SessionId}", ButtonStyle.Secondary);

            try
            {
                await component.Message.ModifyAsync(m =>
                {
                    m.Components = confirmBuilder.Build();
                });
                Log($"Кнопки подтверждения остановки для сессии {session.SessionId} успешно обновлены");
            }
            catch (Exception ex)
            {
                Log($"Ошибка при обновлении кнопок подтверждения остановки: {ex.Message}");
            }

            var confirmMessage = await component.FollowupAsync("Вы уверены, что хотите завершить игру?", ephemeral: true);
            session.ConfirmationMessageId = confirmMessage.Id;

            // Удаляем через 10 секунд
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                try
                {
                    await confirmMessage.DeleteAsync();
                }
                catch { }
            });
        }

        private async Task HandleConfirmStop(SocketMessageComponent component, GameSession session)
        {
            // ✅ DeferAsync уже вызван в HandleControlButton (строка 6079), не дублируем!
            Log($"Попытка подтверждения остановки сессии {session.SessionId}. Текущий счётчик семафора: {_sessionSemaphore.CurrentCount}");

            try
            {
                // Убедитесь, что сессия существует
                if (session == null)
                {
                    await SendTemporaryEphemeralResponse(component, "Сессия не найдена.");
                    return;
                }

                Log($"Начало обработки остановки сессии {session.SessionId}...");

                if (session.IsPaused)
                {
                    Log($"Снятие паузы для сессии {session.SessionId}...");
                    session.PauseReminderCTS?.Cancel();
                    var lastPause = session.PausePeriods.Last();
                    session.PausePeriods[^1] = (lastPause.Start, DateTime.Now);
                    session.IsPaused = false;
                }

                session.EndTime = DateTime.Now;
                Log($"Время окончания установлено: {session.EndTime}, сессия помечена как остановленная (IsStopped={session.IsStopped})");


                if (session.EventId.HasValue)
                {
                    try
                    {
                        var guild = _client.GetGuild(session.GuildId);
                        var guildEvent = guild != null ? await guild.GetEventAsync(session.EventId.Value) : null;
                        if (guildEvent?.Status == GuildScheduledEventStatus.Active)
                        {
                            await guildEvent.ModifyAsync(props => props.Status = GuildScheduledEventStatus.Completed);
                            Log($"Связанное событие {session.EventId} помечено как завершённое");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Ошибка завершения события: {ex.Message}");
                    }
                }

                // ВАЖНО: Сначала выводим статистику
                await SendSessionStats(session, component.Channel);

                // ПОТОМ удаляем контрольное сообщение
                await DeleteControlMessageAsync(session, component.Channel);

                // Очистка сессии происходит:
                // 1. В SendSessionStats → RemoveSession() если нет бросков (строка 6621)
                // 2. В обработке кнопок статистики (no_stats, general_stats, detailed_stats)
                //    после вывода статистики - там вызывается RemoveSession()
            }
            catch (Exception ex)
            {
                // ✅ ДОБАВЛЕНО: логирование ошибки
                Log($"КРИТИЧЕСКАЯ ОШИБКА в HandleConfirmStop для сессии {session.SessionId}: {ex.Message}");
                Log($"StackTrace: {ex.StackTrace}");
            }
            finally
            {
                //_sessionSemaphore.Release();
                //Log($"Семафор освобожден. Текущий счётчик: {_sessionSemaphore.CurrentCount}");
            }
        }

        private IMessageChannel? ResolveControlChannel(GameSession session, IMessageChannel? overrideChannel = null)
        {
            if (overrideChannel != null)
                return overrideChannel;

            if (Program.ServerConfigs.TryGetValue(session.GuildId, out var config) && config.RecordChannelID != 0)
            {
                var channel = _client.GetChannel(config.RecordChannelID) as IMessageChannel
                    ?? _client.GetGuild(session.GuildId)?.GetTextChannel(config.RecordChannelID);
                if (channel != null)
                    return channel;
            }

            if (session.ChannelId != 0)
            {
                var channel = _client.GetChannel(session.ChannelId) as IMessageChannel
                    ?? _client.GetGuild(session.GuildId)?.GetTextChannel(session.ChannelId);
                if (channel != null)
                    return channel;
            }

            return null;
        }

        private async Task DeleteControlMessageAsync(GameSession session, IMessageChannel? channel = null)
        {
            if (session.ControlMessageId == 0)
                return;

            var targetChannel = ResolveControlChannel(session, channel);
            if (targetChannel == null)
            {
                Log($"Не удалось определить канал управления для удаления message_id={session.ControlMessageId}");
                session.ControlMessageId = 0;
                return;
            }

            try
            {
                var message = await targetChannel.GetMessageAsync(session.ControlMessageId).ConfigureAwait(false);
                if (message != null)
                {
                    await message.DeleteAsync().ConfigureAwait(false);
                    Log($"Сообщение управления {session.ControlMessageId} удалено");
                }
                else
                {
                    Log($"Сообщение управления {session.ControlMessageId} не найдено в канале при удалении");
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка удаления сообщения управления: {ex.Message}");
            }
            finally
            {
                session.ControlMessageId = 0;
            }
        }

        private async Task HandleCancelStop(SocketMessageComponent component, GameSession session)
        {
            Log($"Пользователь {component.User.Id} отменил остановку сессии {session.SessionId}");

            try
            {
                await UpdateControlMessage(session, component.Channel);
                Log($"Сообщение управления сессии {session.SessionId} успешно восстановлено");
            }
            catch (Exception ex)
            {
                Log($"Ошибка при восстановлении сообщения управления: {ex.Message}");
            }

            await SendTemporaryEphemeralResponse(component, "Отмена завершения игры.");
        }

        private async Task HandleToggleRolls(SocketMessageComponent component, GameSession session)
        {
            session.TrackRolls = !session.TrackRolls;
            await UpdateControlMessage(session, component.Channel);
            await SendTemporaryEphemeralResponse(component, $"Сбор статистики бросков {(session.TrackRolls ? "включен" : "выключен")}.");

            // ✅ НОВОЕ: Сохраняем состояние
            _ = Task.Run(() => SaveSessionsAsync());
        }

        public static async Task OnGuildScheduledEventCompleted(SocketGuildEvent guildEvent, DiscordSocketClient client)
        {
            var commands = new GameSessionCommands(client);
            commands.Log($"Событие {guildEvent.Id} завершено - обработка связанной сессии...");

            try
            {
                var guildId = guildEvent.Guild.Id;

                await _sessionSemaphore.WaitAsync();
                try
                {
                    commands.Log($"Поиск сессий для гильдии {guildId} и события {guildEvent.Id}...");

                    if (_sessions.TryGetValue(guildId, out var guildSessions))
                    {
                        var session = guildSessions.Values.FirstOrDefault(s => s.EventId == guildEvent.Id);
                        if (session != null)
                        {
                            commands.Log($"Найдена сессия {session.SessionId} для завершения");

                    if (session.IsPaused)
                    {
                        commands.Log($"Снятие паузы для сессии {session.SessionId}...");
                        try { session.PauseReminderCTS?.Cancel(); } catch { }
                        try { session.PauseReminderCTS?.Dispose(); } catch { }
                        session.PauseReminderCTS = null;
                        var lastPause = session.PausePeriods.Last();
                        session.PausePeriods[^1] = (lastPause.Start, DateTime.Now);
                        session.IsPaused = false;
                    }

                            session.EndTime = DateTime.Now;
                            commands.Log($"Установлено время окончания для сессии {session.SessionId}");

                            var channel = client.GetChannel(Program.ServerConfigs[guildId].RecordChannelID) as SocketTextChannel;
                            if (channel != null)
                            {
                                commands.Log($"Отправка статистики для сессии {session.SessionId}...");
                                await commands.SendSessionStats(session, channel);
                            }
                            else
                            {
                                commands.Log($"Канал для статистики не найден");
                            }
                            await commands.DeleteControlMessageAsync(session);
                        }
                        else
                        {
                            commands.Log($"Активная сессия для события {guildEvent.Id} не найдена");
                        }
                    }
                    else
                    {
                        commands.Log($"Активные сессии для гильдии {guildId} не найдены");
                    }
                }
                finally
                {
                    _sessionSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                commands.Log($"Критическая ошибка при обработке завершения события: {ex}");
            }
        }

        private string BuildSessionStats(GameSession session)
        {
            var totalDuration = session.EndTime.Value - session.StartTime;
            var pauseDuration = session.PausePeriods
                .Where(p => p.End.HasValue)
                .Sum(p => (p.End.Value - p.Start).TotalSeconds);
            var activeDuration = totalDuration.TotalSeconds - pauseDuration;

            var message = new StringBuilder();
            message.AppendLine($"# Игра **\"{session.GameName}\"** завершена");
            message.AppendLine($"- **Мастер:** {session.MasterName}");
            message.AppendLine($"- **Начало:** {session.StartTime:dd.MM.yyyy HH:mm}");
            message.AppendLine($"- **Конец:** {session.EndTime:dd.MM.yyyy HH:mm}");
            message.AppendLine($"- **Общее время:** {FormatTimeSpan(totalDuration)}");
            if (session.PausePeriods.Any())
            {
                message.AppendLine($"- **Активное время:** {FormatTimeSpan(TimeSpan.FromSeconds(activeDuration))}");

                var totalPauseDuration = TimeSpan.FromSeconds(pauseDuration);
                var pauseCount = session.PausePeriods.Count(p => p.End.HasValue);

                if (pauseCount == 1)
                {
                    message.AppendLine($"- **Продолжительность перерыва:** {FormatTimeSpan(totalPauseDuration)}");
                }
                else if (pauseCount > 1)
                {
                    message.AppendLine($"- **Продолжительность перерывов:** {FormatTimeSpan(totalPauseDuration)}");
                }
            }

            if (!string.IsNullOrEmpty(session.EventDescription))
                message.AppendLine($"- **Описание события:** {session.EventDescription}");

            if (!string.IsNullOrEmpty(session.GameComment))
                message.AppendLine($"- **Комментарий:** {session.GameComment}");

            if (session.PausePeriods.Any())
            {
                message.AppendLine("## Перерывы:");
                foreach (var pause in session.PausePeriods)
                    message.AppendLine($"- {pause.Start:HH:mm} — {pause.End?.ToString("HH:mm") ?? "не завершён"}");
            }

            return message.ToString();
        }

        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            var hours = (int)timeSpan.TotalHours;
            var minutes = timeSpan.Minutes;
            //var seconds = timeSpan.Seconds;

            var hoursText = hours > 0 ? $"{hours} час{(hours == 1 ? "" : hours < 5 ? "а" : "ов")}" : "";
            var minutesText = minutes > 0 ? $"{minutes} минут{(minutes == 1 ? "а" : minutes < 5 ? "ы" : "")}" : "";
            //var secondsText = seconds > 0 ? $"{seconds} секунд{(seconds == 1 ? "а" : seconds < 5 ? "ы" : "")}" : "";

            if (string.IsNullOrEmpty(hoursText) && string.IsNullOrEmpty(minutesText))
                return "0 минут";
            
            return string.Join(" ", new[] { hoursText, minutesText }.Where(s => !string.IsNullOrEmpty(s)));
            //return string.Join(" ", new[] { hoursText, minutesText, secondsText }.Where(s => !string.IsNullOrEmpty(s)));
        }

        private async Task SendSessionStats(GameSession session, ISocketMessageChannel channel)
        {
            lock (session.StatsSync)
            {
                if (session.StatsSent)
                {
                    Log($"Статистика для сессии {session.SessionId} уже была отправлена");
                    return;
                }

                session.StatsSent = true;
            }

          Log($"Формирование статистики для сессии {session.SessionId}...");

            try
            {
                var statsMessage = BuildSessionStats(session);
                await channel.SendMessageAsync(statsMessage);
                Log($"Статистика по времени для сессии {session.SessionId} отправлена");

                if (session.Rolls.Count > 0)
                {
                    Log($"Сессия {session.SessionId} содержит {session.Rolls.Count} бросков - подготовка кнопок статистики");

                    if (Program.ServerConfigs.TryGetValue(session.GuildId, out var config))
                    {
                        Log($"Конфигурация сервера {session.GuildId} найдена");

                        if (_client.GetChannel(config.StatsChannelID) is ITextChannel statsChannel)
                        {
                            var buttons = new ComponentBuilder()
                                .WithButton("Не надо", "no_stats", ButtonStyle.Secondary)
                                .WithButton("Общая", "general_stats", ButtonStyle.Primary)
                                .WithButton("Подробная", "detailed_stats", ButtonStyle.Primary)
                                .Build();

                            var masterMention = session.MasterId != 0
                                ? MentionUtils.MentionUser(session.MasterId)
                                : session.MasterName;

                            var buttonsMsg = await statsChannel.SendMessageAsync(
                                $"{masterMention}, какую статистику бросков вывести для игры `{session.GameName}`? Нажми на одну из кнопок ниже",
                                components: buttons);

                            session.StatsMessageId = buttonsMsg.Id;
                            Log($"Кнопки статистики отправлены в канал {statsChannel.Id}, ID сообщения: {buttonsMsg.Id}");
                        }
                        else
                        {
                            Log($"Канал статистики {config.StatsChannelID} не найден");
                        }
                    }
                    else
                    {
                        Log($"Конфигурация сервера {session.GuildId} не найдена");
                    }
                }
                else
                {
                    Log($"Сессия {session.SessionId} не содержит бросков - немедленное удаление");
                    RemoveSession(session);
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка при отправке статистики: {ex.Message}");
            }
        }

        private void RemoveSession(GameSession session)
        {
            try
            {
                // Отменяем все pending операции
                session.PauseReminderCTS?.Cancel();
                session.PauseReminderCTS?.Dispose();

                if (_sessions.TryGetValue(session.GuildId, out var guildSessions))
                {
                    if (guildSessions.TryRemove(session.SessionId, out _))
                    {
                        Log($"Сессия {session.SessionId} успешно удалена из словаря");
                    }
                    else
                    {
                        Log($"Не удалось удалить сессию {session.SessionId} из словаря");
                    }

                    if (guildSessions.IsEmpty)
                    {
                        if (_sessions.TryRemove(session.GuildId, out _))
                        {
                            Log($"Словарь сессий для гильдии {session.GuildId} удален (пуст)");
                        }
                    }
                }
                else
                {
                    Log($"Не найден словарь сессий для гильдии {session.GuildId} при удалении");
                }

                try
                {
                    var guild = _client.GetGuild(session.GuildId);
                    if (guild == null) return;

                    var channelId = Program.ServerConfigs[session.GuildId].RecordChannelID;
                    var channel = guild.GetTextChannel(channelId);
                    if (channel == null) return;

                    // Удаляем последнее напоминание о паузе
                    if (session.PauseReminderMessageId.HasValue)
                    {
                        try
                        {
                            channel.DeleteMessageAsync(session.PauseReminderMessageId.Value);
                        }
                        catch (Exception ex)
                        {
                            Log($"Ошибка при удалении сообщения напоминания о паузе: {ex.Message}");
                        }
                    }

                    // Удаляем сообщение подтверждения остановки
                    if (session.ConfirmationMessageId.HasValue)
                    {
                        try
                        {
                            channel.DeleteMessageAsync(session.ConfirmationMessageId.Value);
                        }
                        catch (Exception ex)
                        {
                            Log($"Ошибка при удалении сообщения подтверждения остановки: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Ошибка при очистке сообщений сессии: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка при очистке ресурсов сессии: {ex.Message}");
            }
        }

        public async Task HandleStatsButton(SocketMessageComponent component)
        {
            var guildId = (component.Channel as SocketGuildChannel)?.Guild.Id;
            if (guildId == null)
            {
                Log("Не удалось получить ID гильдии при обработке кнопки статистики");
                return;
            }

            await _sessionSemaphore.WaitAsync();
            try
            {
                Log($"Обработка кнопки статистики для гильдии {guildId}");

                if (!_sessions.TryGetValue(guildId.Value, out var guildSessions))
                {
                    Log($"[RESTART] Активные сессии для гильдии {guildId} не найдены (вероятно бот был перезагружен)");
                    await component.RespondAsync("❌ Сессия больше не активна.\n\nЭто может произойти если бот был перезагружен. Статистика была потеряна.", ephemeral: true);
                    return;
                }

                var session = guildSessions.Values.FirstOrDefault(s => s.StatsMessageId == component.Message.Id);
                if (session == null)
                {
                    Log($"[RESTART] Сессия для сообщения статистики {component.Message.Id} не найдена");
                    await component.RespondAsync("❌ Сессия не найдена.\n\nЭто может произойти если бот был перезагружен.", ephemeral: true);
                    return;
                }

                Log($"Найдена сессия {session.SessionId} для обработки статистики");

                switch (component.Data.CustomId)
                {
                    case "no_stats":
                        await component.Message.DeleteAsync();
                        RemoveSession(session);
                        Log($"Статистика для сессии {session.SessionId} отклонена, сессия удалена");
                        break;

                    case "general_stats":
                        await ShowGeneralStats(component, session);
                        RemoveSession(session);
                        Log($"Показана общая статистика для сессии {session.SessionId}, сессия удалена");
                        break;

                    case "detailed_stats":
                        await ShowDetailedStats(component, session);
                        RemoveSession(session);
                        Log($"Показана детальная статистика для сессии {session.SessionId}, сессия удалена");
                        break;
                }
            }
            finally
            {
                _sessionSemaphore.Release();
            }
        }

        private async Task ShowGeneralStats(SocketMessageComponent component, GameSession session)
        {
            Log($"Формирование общей статистики для сессии {session.SessionId}");

            // ✅ УЛУЧШЕНО: Группируем по типам кубиков
            var rollsByDiceType = session.Rolls
                .GroupBy(r => r.DiceType ?? "unknown")
                .OrderBy(g => g.Key)
                .ToList();

            var message = new StringBuilder("**Общая статистика бросков:**\n");

            // Если только один тип куба - показываем просто по значениям
            if (rollsByDiceType.Count == 1)
            {
                var diceType = rollsByDiceType[0].Key;
                var rolls = rollsByDiceType[0]
                    .GroupBy(r => r.RollValue)
                    .Select(g => new { Value = g.Key, Count = g.Count() })
                    .OrderBy(g => g.Value);

                var averageValue = rollsByDiceType[0].Average(r => r.RollValue);

                message.AppendLine($"**{diceType}:**\n");
                foreach (var roll in rolls)
                {
                    message.AppendLine($"  - {roll.Value}: {roll.Count} раз");
                }
                message.AppendLine($"  **Среднее:** {averageValue:F2}\n");
            }
            else
            {
                // Если несколько типов - группируем по типам
                foreach (var diceGroup in rollsByDiceType)
                {
                    var diceType = diceGroup.Key;
                    var rolls = diceGroup
                        .GroupBy(r => r.RollValue)
                        .Select(g => new { Value = g.Key, Count = g.Count() })
                        .OrderBy(g => g.Value);

                    var averageValue = diceGroup.Average(r => r.RollValue);

                    message.AppendLine($"**{diceType}:**");
                    foreach (var roll in rolls)
                    {
                        message.AppendLine($"  - {roll.Value}: {roll.Count} раз");
                    }
                    message.AppendLine($"  Среднее: {averageValue:F2}\n");
                }
            }

            await component.Channel.SendMessageAsync(message.ToString());
            await component.Message.DeleteAsync();
        }

        private async Task ShowDetailedStats(SocketMessageComponent component, GameSession session)
        {
            Log($"Формирование детальной статистики для сессии {session.SessionId}");

            // ✅ УЛУЧШЕНО: Группируем по типам кубиков для каждого игрока
            var players = session.Rolls
                .GroupBy(r => r.PlayerName)
                .Select(g => new {
                    Player = g.Key,
                    RollsByDiceType = g.GroupBy(r => r.DiceType ?? "unknown")
                        .OrderBy(dg => dg.Key)
                        .Select(dg => new {
                            DiceType = dg.Key,
                            Rolls = dg.GroupBy(r => r.RollValue)
                                .Select(r => new { Value = r.Key, Count = r.Count() })
                                .OrderBy(r => r.Value)
                                .ToList(),
                            Average = dg.Average(r => r.RollValue)
                        })
                        .ToList(),
                    OverallAverage = g.Average(r => r.RollValue)
                });

            var message = new StringBuilder("**Подробная статистика бросков:**\n");
            foreach (var player in players)
            {
                message.AppendLine($"*{player.Player}:*");

                // Если только один тип куба - не показываем тип
                if (player.RollsByDiceType.Count == 1)
                {
                    var diceStats = player.RollsByDiceType[0];
                    foreach (var roll in diceStats.Rolls)
                    {
                        message.AppendLine($"  - {roll.Value}: {roll.Count} раз");
                    }
                    message.AppendLine($"  **Среднее:** {diceStats.Average:F2}\n");
                }
                else
                {
                    // Если несколько типов - показываем с разделением
                    foreach (var diceStats in player.RollsByDiceType)
                    {
                        message.AppendLine($"  **{diceStats.DiceType}:**");
                        foreach (var roll in diceStats.Rolls)
                        {
                            message.AppendLine($"    - {roll.Value}: {roll.Count} раз");
                        }
                        message.AppendLine($"    Среднее: {diceStats.Average:F2}");
                    }
                    message.AppendLine($"  **Общее среднее:** {player.OverallAverage:F2}\n");
                }
            }

            await component.Channel.SendMessageAsync(message.ToString());
            await component.Message.DeleteAsync();
        }

        private async Task SendTemporaryEphemeralResponse(SocketInteraction interaction, string message)
        {
            var response = await interaction.FollowupAsync(message, ephemeral: true);
            _ = Task.Run(async () =>
            {
                await Task.Delay(10000);
                try { await response.DeleteAsync(); } catch { }
            });
        }
    }


}