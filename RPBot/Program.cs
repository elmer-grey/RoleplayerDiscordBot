using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using RPBot;
using RPBot.Music;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RPBot
{
 public partial class Program : IDisposable, IBotController
    {
        private DiscordSocketClient _client;
        private CommandService _commandService;
        private IServiceProvider _services;
        private CommandHandler _commandHandler = null!;
        private Dictionary<string, string> _textBlocks = null!;
        private readonly SemaphoreSlim _restartLock = new(1, 1);
        private bool _isDisposed;
        private volatile bool _shouldExit = false;
        private volatile bool _shouldRestart = false;

        private ReconnectionService? _reconnectionService;
        private ConnectionPredictor? _connectionPredictor;
        private StatusNotifier? _statusNotifier;
		private PointsService _pointsService;
      private PointsUserIndex _pointsUserIndex;
		private PredictionService? _predictionService;
		private VoicePointsService? _voicePointsService;
        private TelegramNotifier? _telegramNotifier;
        private EventAnnouncementStore? _eventAnnouncementStore;
private GoogleSheetsService? _googleSheetsService;
private LavalinkService? _lavalinkService;
private MusicCommands? _musicCommands;
private MusicPlaylistStore? _playlistStore;
private MusicQueueStore? _musicQueueStore;
private MusicStats? _musicStats;

        private readonly ConcurrentDictionary<string, SocketMessageComponent> _pendingBetUi = new();

        private readonly ConcurrentDictionary<string, (string title, int minutes)> _pendingPredictionCreate = new();

		private Task? _backgroundMonitoringTask;
		private CancellationTokenSource? _backgroundMonitoringCts;
		private CancellationTokenSource? _dailyRestartCts;
		private Task? _dailyRestartTask;
        private string _restartInitiator = "console";

        private TextWriter? _originalOut;
        private TextWriter? _originalErr;

        public static Action<string>? CommandLogSink { get; private set; }
        public static Func<ulong, ServerConfig?>? ServerConfigResolver { get; private set; }

        private void CleanupServices()
        {
            try
            {
                if (_reconnectionService != null)
                {
                    try
                    {
                        _reconnectionService.OnDisconnectDetected -= OnDisconnectDetected;
                        _reconnectionService.OnReconnectStarted -= OnReconnectStarted;
                        _reconnectionService.OnReconnectCompleted -= OnReconnectCompleted;
                    }
                    catch { }

                    try { _reconnectionService.Shutdown(); } catch { }
                    try { (_reconnectionService as IDisposable)?.Dispose(); } catch { }
                    _reconnectionService = null;
                }

                if (_connectionPredictor != null)
                {
                    try { _connectionPredictor.OnPredictionMade -= OnPredictionMade; } catch { }
                    _connectionPredictor = null;
                }

                if (_statusNotifier != null)
                {
                    try { (_statusNotifier as IDisposable)?.Dispose(); } catch { }
                    _statusNotifier = null;
                }

                if (_predictionService != null)
                {
                    try { _predictionService.Shutdown(); } catch { }
                    _predictionService = null;
                }

                if (_voicePointsService != null)
                {
                    try { _voicePointsService.Shutdown(); } catch { }
                    _voicePointsService = null;
                }

}
            catch (Exception ex)
            {
                Console.WriteLine($"CleanupServices error: {ex.Message}");
            }
        }

        private Task SetupDiscordEvents()
        {
            _client.Ready -= OnReady;
            _client.Disconnected -= OnDisconnected;
            _client.UserJoined -= UserJoined;
            _client.MessageReceived -= HandleCommandAsync;
            _client.SlashCommandExecuted -= OnSlashCommandExecuted;
            _client.SlashCommandExecuted -= BwonkCommand;
            _client.ModalSubmitted -= HandleModalSubmitted;
            _client.ButtonExecuted -= HandleButtonExecuted;
            _client.SelectMenuExecuted -= HandleSelectMenuExecuted;
            _client.GuildScheduledEventCreated -= OnGuildScheduledEventCreated;
            _client.GuildScheduledEventUpdated -= OnGuildScheduledEventUpdated;
            _client.GuildScheduledEventStarted -= OnGuildScheduledEventStarted;
            _client.GuildScheduledEventCancelled -= OnGuildScheduledEventCancelled;
            _client.GuildScheduledEventCompleted -= OnGuildScheduledEventCompleted;

            _client.Ready += OnReady;
            _client.Disconnected += OnDisconnected;
            _client.UserJoined += UserJoined;
            _client.MessageReceived += HandleCommandAsync;
            _client.SlashCommandExecuted += OnSlashCommandExecuted;
            _client.SlashCommandExecuted += BwonkCommand;
            _client.ModalSubmitted += HandleModalSubmitted;
            _client.ButtonExecuted += HandleButtonExecuted;
            _client.SelectMenuExecuted += HandleSelectMenuExecuted;
            _client.GuildScheduledEventCreated += OnGuildScheduledEventCreated;
            _client.GuildScheduledEventUpdated += OnGuildScheduledEventUpdated;
            _client.GuildScheduledEventStarted += OnGuildScheduledEventStarted;
            _client.GuildScheduledEventCancelled += OnGuildScheduledEventCancelled;
            _client.GuildScheduledEventCompleted += OnGuildScheduledEventCompleted;

            return LogStartup("│   События Discord настроены    │");
        }
        private async Task HandlePredictionBetButton(SocketMessageComponent component, string[] parts)
        {
            // customId: pred_bet:<guildId>
            if (parts.Length < 2) return;
            if (!ulong.TryParse(parts[1], out var guildId)) return;

            var user = component.User as SocketGuildUser;
            if (user == null)
            {
                await component.RespondAsync("Только участники сервера могут ставить.", ephemeral: true);
                return;
            }
         try
            {
                _pointsUserIndex.UpsertFromUser(guildId, user);
                _ = Task.Run(() => _pointsUserIndex.SaveAsync());
            }
            catch { }
            var active = _predictionService.GetActive(guildId);
            if (active == null || active.IsResolved)
            {
                try { await component.RespondAsync("Сейчас нет активного прогноза.", ephemeral: true); } catch { }
                ScheduleDeleteOriginalResponse(component);
                return;
            }

            var balance = _pointsService.GetBalance(guildId, component.User.Id);
            var cb = new ComponentBuilder()
                .WithButton($"Продолжить (баланс: {balance})", customId: $"pred_bet_confirm:{guildId}", style: ButtonStyle.Primary);

            try { await component.RespondAsync($"Ваш текущий баланс: {balance}.", ephemeral: true, components: cb.Build()); } catch { }
            ScheduleDeleteOriginalResponse(component);
        }

        private async Task HandlePredictionBetConfirmButton(SocketMessageComponent component, string[] parts)
        {
            // customId: pred_bet_confirm:<guildId>
            if (parts.Length < 2) return;
            if (!ulong.TryParse(parts[1], out var guildId)) return;

            var user = component.User as SocketGuildUser;
            if (user == null)
            {
                await component.RespondAsync("Только участники сервера могут ставить.", ephemeral: true);
                return;
            }

            _pendingBetUi[$"{guildId}:{component.User.Id}"] = component;

            var active = _predictionService?.GetActive(guildId);
            PredictionBet? existingBet = null;
            var hasExistingBet = active != null && active.Bets.TryGetValue(component.User.Id, out existingBet);

            // Open modal to input bet
            Modal modal;
            if (hasExistingBet && existingBet != null)
            {
                // ✅ Обновлено: динамический поиск имени исхода
                var existingOutcome = active!.GetOutcomeById(existingBet!.OutcomeId);
                var existingOutcomeName = existingOutcome?.Name ?? $"Исход {existingBet.OutcomeId}";

                modal = new ModalBuilder()
                    .WithTitle("Увеличить ставку")
                    .WithCustomId($"pred_bet_add_modal:{guildId}")
                    .AddTextInput($"Ваш исход: {existingOutcomeName}", "amount", TextInputStyle.Short, placeholder: "Сколько ещё поставить")
                    .Build();
            }
            else
            {
                // ✅ Обновлено: показываем список исходов с названиями
                var outcomeCount = active?.Outcomes.Count ?? 2;
                var outcomesList = active != null 
                    ? string.Join(", ", active.Outcomes.Select(o => $"{o.Id}: {o.Name}"))
                    : "1: Исход 1, 2: Исход 2";

                var useCompactOutcomeLabels = active?.UseCompactOutcomeLabels ?? false;
                var outcomePlaceholder = useCompactOutcomeLabels
                    ? $"Выберите 1-{outcomeCount}"
                    : outcomeCount == 2 ? "1 или 2" : $"1 до {outcomeCount}";

                var label = useCompactOutcomeLabels
                    ? "Исход"
                    : $"Исход ({outcomesList})";

                modal = new ModalBuilder()
                    .WithTitle("Сделать ставку")
                    .WithCustomId($"pred_bet_modal:{guildId}")
                    .AddTextInput(label, "outcome", TextInputStyle.Short, placeholder: outcomePlaceholder, maxLength: 2)
                    .AddTextInput("Сумма", "amount", TextInputStyle.Short, placeholder: "Количество костяшек")
                    .Build();
            }

            await component.RespondWithModalAsync(modal);
        }

        /// <summary>
        /// Проверяет наличие активного события на указанном голосовом канале
        /// </summary>
        private bool IsActiveEventOnChannel(ulong guildId, ulong channelId)
        {
            var guild = _client.GetGuild(guildId);
            if (guild == null) return false;

            return guild.Events.Any(e =>
                e.Status == GuildScheduledEventStatus.Active &&
                e.Channel != null &&
                e.Channel.Id == channelId);
        }

        private async Task HandlePredictionOutcomesButton(SocketMessageComponent component, string[] parts)
        {
            // customId: pred_outcomes:<count>:<guildId>:<channelId>
            // count: 3 или 5
            if (parts.Length < 4) return;
            if (!int.TryParse(parts[1], out var outcomesCount)) return;
            if (!ulong.TryParse(parts[2], out var guildId)) return;
            if (!ulong.TryParse(parts[3], out var channelId)) return;

            try
            {
                if (!IsActiveEventOnChannel(guildId, channelId))
                {
                    await component.RespondAsync("⚠️ Активное событие в этом голосовом канале завершено или отсутствует. Создание прогноза невозможно.", ephemeral: true);
                    return;
                }

                var existingPrediction = _predictionService.GetActive(guildId);
                if (existingPrediction != null)
                {
                    await component.RespondAsync("На этом сервере уже есть активный прогноз. Дождитесь его завершения или отмените.", ephemeral: true);
                    return;
                }

                if (outcomesCount == 3)
                {
                    var modal = new ModalBuilder()
                        .WithTitle("Создать прогноз")
                        .WithCustomId($"pred_create_modal_3:{guildId}:{channelId}")
                        .AddTextInput("Заголовок", "title", TextInputStyle.Short, placeholder: "Название прогноза", maxLength: 100)
                        .AddTextInput("Исход 1", "outcome1", TextInputStyle.Short, placeholder: "Название исхода 1", maxLength: 80)
                        .AddTextInput("Исход 2", "outcome2", TextInputStyle.Short, placeholder: "Название исхода 2", maxLength: 80)
                        .AddTextInput("Исход 3 (опц.)", "outcome3", TextInputStyle.Short, placeholder: "Оставьте пустым для 2 исходов", required: false, maxLength: 80)
                        .AddTextInput("Время (мин)", "duration_minutes", TextInputStyle.Short, placeholder: "От 1 до 60 минут", value: "3")
                        .Build();

                    await component.RespondWithModalAsync(modal);
                }
                else if (outcomesCount == 5)
                {
                    var modal = new ModalBuilder()
                        .WithTitle("Создать прогноз (шаг 1/2)")
                        .WithCustomId($"pred_create_step1:{guildId}:{channelId}")
                        .AddTextInput("Заголовок", "title", TextInputStyle.Short, placeholder: "Название прогноза", maxLength: 100)
                        .AddTextInput("Время (мин)", "duration_minutes", TextInputStyle.Short, placeholder: "От 1 до 60 минут", value: "3")
                        .Build();

                    await component.RespondWithModalAsync(modal);
                }
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("HandlePredictionOutcomesButton", ex, $"guild={guildId} channel={channelId} count={outcomesCount}").ConfigureAwait(false);
                try { await component.RespondAsync("Произошла ошибка при открытии формы. Попробуйте ещё раз.", ephemeral: true); } catch { }
            }
        }

        private async Task HandlePredictionContinueStep2Button(SocketMessageComponent component, string[] parts)
        {
            // customId: pred_continue_step2:<guildId>:<channelId>
            if (parts.Length < 3) return;
            if (!ulong.TryParse(parts[1], out var guildId)) return;
            if (!ulong.TryParse(parts[2], out var channelId)) return;

            try
            {
                // Читаем данные из словаря
                var key = $"{guildId}:{channelId}:{component.User.Id}";
                if (!_pendingPredictionCreate.TryGetValue(key, out var data))
                {
                    await component.RespondAsync("Данные первого шага не найдены. Начните сначала.", ephemeral: true);
                    return;
                }

                var title = data.title;

                // Показываем второй модал с 5 исходами
                var step2Modal = new ModalBuilder()
                    .WithTitle($"Прогноз: {(title.Length > 20 ? title.Substring(0, 20) + "..." : title)} (2/2)")
                    .WithCustomId($"pred_create_step2:{guildId}:{channelId}")
                    .AddTextInput("Исход 1", "outcome1", TextInputStyle.Short, placeholder: "Обязательно", maxLength: 80)
                    .AddTextInput("Исход 2", "outcome2", TextInputStyle.Short, placeholder: "Обязательно", maxLength: 80)
                    .AddTextInput("Исход 3 (опц.)", "outcome3", TextInputStyle.Short, placeholder: "Необязательно", required: false, maxLength: 80)
                    .AddTextInput("Исход 4 (опц.)", "outcome4", TextInputStyle.Short, placeholder: "Необязательно", required: false, maxLength: 80)
                    .AddTextInput("Исход 5 (опц.)", "outcome5", TextInputStyle.Short, placeholder: "Необязательно", required: false, maxLength: 80)
                    .Build();

                await component.RespondWithModalAsync(step2Modal);
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("HandlePredictionContinueStep2Button", ex, $"guild={guildId} channel={channelId} user={component.User.Id}").ConfigureAwait(false);
                try { await component.RespondAsync("Произошла ошибка. Попробуйте начать сначала.", ephemeral: true); } catch { }
            }
        }

        private async Task HandlePredictionHistoryPageButton(SocketMessageComponent component, string[] parts)
        {
            // customId: pred_history_page:<guildId>:<page>
            if (parts.Length < 3) return;
            if (!ulong.TryParse(parts[1], out var guildId)) return;
            if (!int.TryParse(parts[2], out var page)) return;

            try
            {
                // Используем метод из Program.Prediction.cs (partial class)
                var embed = BuildHistoryEmbed(guildId, page);
                var components = BuildHistoryComponents(guildId, page);

                await component.UpdateAsync(msg =>
                {
                    msg.Embed = embed;
                    msg.Components = components?.Build();
                });
            }
            catch (Exception ex)
            {
                await PredictionErrorLogger.LogAsync("HandlePredictionHistoryPageButton", ex, $"guild={guildId} page={page}").ConfigureAwait(false);
                try { await component.RespondAsync("Ошибка при переключении страницы.", ephemeral: true); } catch { }
            }
        }

        private void ScheduleDeleteOriginalResponse(SocketInteraction interaction, int delaySeconds = 30)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                    try { await interaction.DeleteOriginalResponseAsync(); } catch { }
                }
                catch { }
            });
        }

        private void ScheduleDeleteMessage(IUserMessage? message, int seconds = 30)
        {
            if (message == null) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds));
                    try { await message.DeleteAsync().ConfigureAwait(false); } catch { }
                }
                catch { }
            });
        }

        private void ScheduleDeleteFollowup(SocketInteraction interaction, IMessage? message, int seconds = 30)
        {
            if (interaction == null || message == null) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds));
                  try
                    {
                        // Followups are real messages; delete them via channel REST fetch.
                        var ch = _client.GetChannel(message.Channel.Id) as IMessageChannel;
                        if (ch != null)
                        {
                            var msg = await ch.GetMessageAsync(message.Id).ConfigureAwait(false) as IUserMessage;
                            if (msg != null)
                                await msg.DeleteAsync().ConfigureAwait(false);
                        }
                    }
                    catch { }
                }
                catch { }
            });
        }

        private async Task HandlePredictionCancelButton(SocketMessageComponent component, string[] parts)
        {
            // customId: pred_cancel:<guildId>
            if (parts.Length < 2) return;
            if (!ulong.TryParse(parts[1], out var guildId)) return;

            var user = component.User as SocketGuildUser;
            var isAdmin = user?.GuildPermissions.Administrator ?? false;

            var resolverId = user?.Id ?? 0UL;
            var (ok, error) = await _predictionService.CancelAsync(guildId, resolverId, isAdmin);
            if (ok)
            {
                try { await component.UpdateAsync(msg => { msg.Content = "Прогноз отменён"; msg.Components = new ComponentBuilder().Build(); }); } catch { }
            }
            else
            {
                try { await component.RespondAsync(error, ephemeral: true); } catch { }
            }
        }

        private async Task HandlePredictionResolveButton(SocketMessageComponent component, string[] parts)
        {
            // customId: pred_resolve:<guildId>:<outcomeId>
            if (parts.Length < 3) return;
            if (!ulong.TryParse(parts[1], out var guildId)) return;
            if (!int.TryParse(parts[2], out var outcomeId)) return;

            var user = component.User as SocketGuildUser;
            var isAdmin = user?.GuildPermissions.Administrator ?? false;

            var resolverId = user?.Id ?? 0UL;
            var (ok, error) = await _predictionService.ResolveAsync(guildId, resolverId, isAdmin, outcomeId);
            if (!ok)
            {
                // Avoid responding if the original message was deleted — try update quietly
                try { await component.RespondAsync(error, ephemeral: true); } catch { }
            }
            else
            {
                try { await component.UpdateAsync(msg => { msg.Components = new ComponentBuilder().Build(); }); } catch { }
            }
        }

        // --- Bwonk persistence helpers (inside Program class) ---
        private Dictionary<ulong, int> LoadBwonkCounts()
        {
            try
            {
                if (!File.Exists(_bwonkFilePath)) return new Dictionary<ulong, int>();
                var json = File.ReadAllText(_bwonkFilePath);
                var options = new JsonSerializerOptions();
                var dict = JsonSerializer.Deserialize<Dictionary<ulong, int>>(json, options);
                return dict ?? new Dictionary<ulong, int>();
            }
            catch
            {
                return new Dictionary<ulong, int>();
            }
        }

        private void SaveBwonkCounts()
        {
            try
            {
                var dir = Path.GetDirectoryName(_bwonkFilePath) ?? AppContext.BaseDirectory;
                Directory.CreateDirectory(dir);
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_bwonkCounts, options);
                File.WriteAllText(_bwonkFilePath, json);
            }
            catch { }
        }

        private async Task BwonkCommand(SocketSlashCommand command)
        {
            if (command.Data.Name != "bwonk") return;
            try
            {
                var action = command.Data.Options.FirstOrDefault(o => o.Name == "action")?.Value?.ToString();
                action = string.IsNullOrWhiteSpace(action) ? "bonk" : action;

                var targetOption = command.Data.Options.FirstOrDefault(o => o.Name == "target");

                static bool TryGetUserId(SocketSlashCommandDataOption? opt, out ulong userId)
                {
                    userId = 0;
                    if (opt?.Value is IUser iu) { userId = iu.Id; return true; }
                    if (opt?.Value is long l) { userId = (ulong)l; return true; }
                    if (opt?.Value is ulong ul) { userId = ul; return true; }
                    return ulong.TryParse(opt?.Value?.ToString() ?? "", out userId);
                }

                if (string.Equals(action, "stats", StringComparison.OrdinalIgnoreCase))
                {
                    var targetId = command.User.Id;
                    if (targetOption != null && TryGetUserId(targetOption, out var parsed))
                        targetId = parsed;

                    int total;
                    lock (_bwonkCounts) { total = _bwonkCounts.TryGetValue(targetId, out var v) ? v : 0; }

                    await command.RespondAsync($"{MentionUtils.MentionUser(targetId)} был бонькнут {total} раз.", ephemeral: true);
                    return;
                }

                // default: bonk
                if (targetOption == null || !TryGetUserId(targetOption, out var targetId2) || targetId2 == command.User.Id)
                {
                    await command.RespondAsync("ну у каждого свои приколы... ты бонькнул сам себя, поздравляю", ephemeral: true);
                    return;
                }

                int newTotal;
                lock (_bwonkCounts)
                {
                    if (!_bwonkCounts.TryGetValue(targetId2, out var c)) c = 0;
                    c++;
                    _bwonkCounts[targetId2] = c;
                    newTotal = c;
                    SaveBwonkCounts();
                }

                await command.RespondAsync($"Вы бонькнули {MentionUtils.MentionUser(targetId2)}", ephemeral: true);

                try
                {
                    if (command.Channel is IMessageChannel channel)
                    {
                        await channel.SendMessageAsync($"{MentionUtils.MentionUser(targetId2)}, Вас бонькнули по делу или просто так. Вас уже бонькнули {newTotal} раз, задумайтесь :kappa:");
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                try { await command.RespondAsync($"Ошибка выполнения команды: {ex.Message}", ephemeral: true); } catch { }
            }
        }

        private BotConfig _config;
        private Dictionary<ulong, ServerConfig> _serverConfigs = new();
        private string _serverConfigsPath;
        // Bwonk counts persisted between runs
        private Dictionary<ulong, int> _bwonkCounts = new Dictionary<ulong, int>();
		private string _bwonkFilePath = BotConfig.ResolvePath(Path.Combine("Settings", "bwonks.json"));

		private EventNotificationService _eventNotifications;
		private string _eventNotificationsPath = BotConfig.ResolvePath(Path.Combine("Settings", "event-notify.json"));

        // Слияние конфигурации сервера из статического словаря (дефолты)
        // и конфигурации из файла/памяти (переопределения).
        // Логика: если в serverconfig поле 0/null/пустое, используем значение из словаря.
        private static ServerConfig MergeServerConfig(ServerConfig defaults, ServerConfig overrides)
        {
            if (defaults == null) return overrides;
            if (overrides == null) return defaults;

            return new ServerConfig
            {
                GuildID = overrides.GuildID != 0 ? overrides.GuildID : defaults.GuildID,

                ModerateChannelID = overrides.ModerateChannelID != 0
                    ? overrides.ModerateChannelID
                    : defaults.ModerateChannelID,

                WelcomeChannelID = overrides.WelcomeChannelID != 0
                    ? overrides.WelcomeChannelID
                    : defaults.WelcomeChannelID,

                GeneralRGChannelID = overrides.GeneralRGChannelID != 0
                    ? overrides.GeneralRGChannelID
                    : defaults.GeneralRGChannelID,

                RollChannelID = overrides.RollChannelID != 0
                    ? overrides.RollChannelID
                    : defaults.RollChannelID,

                StatsChannelID = overrides.StatsChannelID != 0
                    ? overrides.StatsChannelID
                    : defaults.StatsChannelID,

                RecordChannelID = overrides.RecordChannelID != 0
                    ? overrides.RecordChannelID
                    : defaults.RecordChannelID,

                WelcomeMessage = !string.IsNullOrWhiteSpace(overrides.WelcomeMessage)
                    ? overrides.WelcomeMessage
                    : defaults.WelcomeMessage,

                LineMessage = !string.IsNullOrWhiteSpace(overrides.LineMessage)
                    ? overrides.LineMessage
                    : defaults.LineMessage,

                DefaultRoleID = overrides.DefaultRoleID != 0
                    ? overrides.DefaultRoleID
                    : defaults.DefaultRoleID,

                MasterRoleId = overrides.MasterRoleId.HasValue && overrides.MasterRoleId.Value != 0
                    ? overrides.MasterRoleId
                    : defaults.MasterRoleId,

                SuperUserRoleId = overrides.SuperUserRoleId.HasValue && overrides.SuperUserRoleId.Value != 0
                    ? overrides.SuperUserRoleId
                    : defaults.SuperUserRoleId,

                // Булевые флаги трактуем как явные значения из serverconfig
                SwearFilterEnabled = overrides.SwearFilterEnabled,
                PredictionsEnabled = overrides.PredictionsEnabled,
                RollPicturesEnabled = overrides.RollPicturesEnabled,
                EventVoiceChannelID = overrides.EventVoiceChannelID != 0 ? overrides.EventVoiceChannelID : defaults.EventVoiceChannelID,

                SwearWords = (overrides.SwearWords != null && overrides.SwearWords.Count > 0)
                    ? overrides.SwearWords
                    : defaults.SwearWords
            };
        }

		// Сохранение/загрузка конфигураций серверов
		private void SaveServerConfigs()
		{
			// Защита: не перезаписываем файл пустым словарём
			if (_serverConfigs == null || _serverConfigs.Count == 0)
			{
				_ = LogError("[ServerConfig] SaveServerConfigs: словарь пуст — сохранение отменено.");
				return;
			}
			try
			{
				var path = _serverConfigsPath ?? BotConfig.ResolvePath("serverconfigs.json");
				var resolved = BotConfig.ResolvePath(path);
				var dir = Path.GetDirectoryName(resolved) ?? AppContext.BaseDirectory;
				Directory.CreateDirectory(dir);
				var options = new JsonSerializerOptions
				{
					WriteIndented = true,
					Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
				};
				var json = JsonSerializer.Serialize(_serverConfigs, options);
				// Атомарная запись: .tmp -> .bak -> основной
				var tmpPath = resolved + ".tmp";
				File.WriteAllText(tmpPath, json, System.Text.Encoding.UTF8);
				if (File.Exists(resolved))
					File.Copy(resolved, resolved + ".bak", overwrite: true);
				File.Move(tmpPath, resolved, overwrite: true);
			}
			catch (Exception ex)
			{
				_ = LogError($"[ServerConfig] Ошибка сохранения serverconfigs: {ex.Message}");
			}
		}

        private void LoadServerConfigs()
        {
            var path     = _serverConfigsPath ?? BotConfig.ResolvePath("serverconfigs.json");
            var resolved = BotConfig.ResolvePath(path);
            var bakPath  = resolved + ".bak";

            for (int attempt = 0; attempt < 2; attempt++)
            {
                var targetPath = attempt == 0 ? resolved : bakPath;
                if (!File.Exists(targetPath)) continue;
                try
                {
                    var json = File.ReadAllText(targetPath, System.Text.Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        _ = LogError($"[ServerConfig] {(attempt == 0 ? "Основной файл" : ".bak")} пуст.");
                        continue;
                    }
                    // Файл должен начинаться и заканчиваться на { }
                    var trimmed = json.Trim();
                    if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
                    {
                        _ = LogError($"[ServerConfig] {(attempt == 0 ? "Основной файл" : ".bak")} повреждён (нет внешних скобок).");
                        continue;
                    }
                    // Проверка парности скобок — поймает обрезанный файл и незакрытые объекты
                    int depth = 0; bool inStr = false; bool esc = false;
                    foreach (var ch in trimmed)
                    {
                        if (esc)                    { esc = false; continue; }
                        if (ch == '\\' && inStr)   { esc = true;  continue; }
                        if (ch == '"')             { inStr = !inStr; continue; }
                        if (inStr)                 continue;
                        if (ch == '{' || ch == '[') depth++;
                        else if (ch == '}' || ch == ']') depth--;
                    }
                    if (depth != 0)
                    {
                        _ = LogError($"[ServerConfig] {(attempt == 0 ? "Основной файл" : ".bak")} повреждён: незакрытые скобки (depth={depth}). Не хватает запятой или скобки?");
                        continue;
                    }
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var dict = JsonSerializer.Deserialize<Dictionary<ulong, ServerConfig>>(json, opts);
                    if (dict != null)
                    {
                        _serverConfigs = dict;
                        if (attempt == 1)
                        {
                            _ = LogError($"[ServerConfig] Загружено из .bak ({dict.Count} серверов). Восстанавливаем основной файл.");
                            File.Copy(bakPath, resolved, overwrite: true);
                        }
                        // Успешная загрузка — НЕ пересохраняем автоматически.
                        return;
                    }
                    _ = LogError($"[ServerConfig] Десериализация вернула null из {(attempt == 0 ? "основного файла" : ".bak")}.");
                }
                catch (JsonException jex)
                {
                    _ = LogError($"[ServerConfig] JSON-ошибка ({(attempt == 0 ? "main" : "bak")}): {jex.Message} — не хватает запятой или скобки?");
                }
                catch (Exception ex)
                {
                    _ = LogError($"[ServerConfig] Ошибка чтения ({(attempt == 0 ? "main" : "bak")}): {ex.Message}");
                }
            }
            _ = LogError("[ServerConfig] Не удалось загрузить ни основной файл, ни .bak. Начинаем с пустого конфига.");
        }

        public Program()
        {
			var configRelativePath = Path.Combine("Settings", "config.json");
			var configResolvedPath = BotConfig.ResolvePath(configRelativePath);
			_config = BotConfig.Load(configRelativePath);
			_serverConfigsPath = BotConfig.ResolvePath(Path.Combine("Settings", "serverconfigs.json"));
            LoadServerConfigs();
            ServerConfigResolver = GetServerConfigInternal;

			// Диагностика: куда именно мы загрузили конфиг и видим ли токен (не печатаем сам токен)
			try
			{
				Console.WriteLine($"Config: {configResolvedPath}");
				Console.WriteLine($"Config BotToken present: {!string.IsNullOrWhiteSpace(_config?.BotToken)}");
			}
			catch { }

            _client = CreateDiscordClient();
            _commandService = new CommandService();

			// ИНИЦИАЛИЗАЦИЯ НОВЫХ СЕРВИСОВ
			_reconnectionService = new ReconnectionService(_client) { LogSink = ServiceLogSink };
			_connectionPredictor = new ConnectionPredictor(_reconnectionService, BotConfig.Current?.Prediction);
			_statusNotifier = new StatusNotifier(_client, _serverConfigs) { LogSink = ServiceLogSink };
          _telegramNotifier = new TelegramNotifier(guildId =>
            {
                return _serverConfigs != null && _serverConfigs.TryGetValue(guildId, out var sc) ? sc : null;
            });
			_eventAnnouncementStore = new EventAnnouncementStore(Path.Combine(BotConfig.SettingsFolderName, "event_announcements.json"));
			_googleSheetsService = GoogleSheetsService.TryCreate(_config);
			if (_googleSheetsService != null)
				_googleSheetsService.LogSink = msg => CommandLogSink?.Invoke(msg);

			// Сервисы для костяшек
			var pointsPath = BotConfig.ResolvePath(Path.Combine(BotConfig.SettingsFolderName, "points.json"));
			_pointsService = new PointsService(pointsPath);
			// Загрузка балансов костяшек из файла
			_pointsService.LoadAsync().GetAwaiter().GetResult();

            var pointsUsersPath = BotConfig.ResolvePath(Path.Combine(BotConfig.SettingsFolderName, "points_users.json"));
            _pointsUserIndex = new PointsUserIndex(pointsUsersPath);
            _pointsUserIndex.LoadAsync().GetAwaiter().GetResult();

			var predictionsLogPath = BotConfig.ResolvePath(Path.Combine(_config.LogDirectory ?? "Logs", "predictions.log"));
			_predictionService = new PredictionService(_client, _pointsService, predictionsLogPath);
			_voicePointsService = new VoicePointsService(_client, _pointsService, GetServerConfigInternal, predictionsLogPath);

			// Инициализация музыкального сервиса (задел: запуск будет выполнен в OnReady)
			if (_config.Music.Enabled)
			{
				_lavalinkService = new LavalinkService(() => _client, _config.Music);
				_lavalinkService.LogSink = msg => _ui?.AddLog(msg);
				_lavalinkService.FileSink = msg =>
				{
					var logDir = BotConfig.ResolvePath("Logs");
					Directory.CreateDirectory(logDir);
					var path = System.IO.Path.Combine(logDir, $"MusicDebug_{DateTime.Now:yyyyMMdd}.txt");
					File.AppendAllText(path, $"[{DateTime.Now:dd-MM-yyyy HH:mm:ss}] {msg}\n");
				};
				_playlistStore = new MusicPlaylistStore(AppContext.BaseDirectory);
					_ = _playlistStore.LoadAsync();
					_musicQueueStore = new MusicQueueStore(AppContext.BaseDirectory);
					_musicStats = MusicStats.LoadAsync(AppContext.BaseDirectory).GetAwaiter().GetResult();
					_musicCommands = new MusicCommands(_lavalinkService, _client, _playlistStore, _musicQueueStore, _musicStats,
						logSink: msg => _ui?.AddLog(msg));
			}

			// ПОДПИСКА НА СОБЫТИЯ СЕРВИСОВ
            _reconnectionService.OnDisconnectDetected += OnDisconnectDetected;
            _reconnectionService.OnReconnectStarted += OnReconnectStarted;
            _reconnectionService.OnReconnectCompleted += OnReconnectCompleted;
            // Подписываемся на запрос полного перезапуска, когда реконнекты зашкаливают
            _reconnectionService.OnFullRestartRequested += OnFullRestartRequested;
            _connectionPredictor.OnPredictionMade += OnPredictionMade;

			var serviceCollection = new ServiceCollection()
				.AddSingleton(_client)
				.AddSingleton(_commandService)
				.AddSingleton(_reconnectionService)
				.AddSingleton(_connectionPredictor)
				.AddSingleton(_statusNotifier)
				.AddSingleton(_pointsService)
				.AddSingleton(_pointsUserIndex)
				.AddSingleton(_predictionService)
				.AddSingleton(_voicePointsService)
				.AddSingleton<QueueModule>()
				.AddSingleton<InfoCommands>()
				.AddSingleton<RollDiceCommands>()
				.AddSingleton<GameSessionCommands>()
				.AddSingleton<ModerationCommands>();

			if (_googleSheetsService != null)
				serviceCollection.AddSingleton(_googleSheetsService);

			_services = serviceCollection.BuildServiceProvider();
            // Load persisted bwonk counts
            try
            {
                _bwonkCounts = LoadBwonkCounts();
            }
			catch { _bwonkCounts = new Dictionary<ulong, int>(); }

			// Load persisted DM event-notification subscriptions
			try
			{
				_eventNotifications = new EventNotificationService(_eventNotificationsPath);
			}
			catch
			{
				_eventNotifications = new EventNotificationService(_eventNotificationsPath);
			}
        }

        private DiscordSocketClient CreateDiscordClient()
        {
            var config = new DiscordSocketConfig
            {
			GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers |
                                   GatewayIntents.GuildMessages | GatewayIntents.MessageContent |
                                   GatewayIntents.GuildScheduledEvents | GatewayIntents.DirectMessages |
                                   GatewayIntents.GuildVoiceStates | GatewayIntents.GuildPresences,
                ConnectionTimeout = _config.Connection.ConnectionTimeout,
                MessageCacheSize = _config.Connection.MessageCacheSize,
                LogLevel = LogSeverity.Info,
                AlwaysDownloadUsers = _config.Connection.AlwaysDownloadUsers,
                HandlerTimeout = _config.Connection.HandlerTimeout,
                TotalShards = 1,
                LargeThreshold = _config.Connection.LargeThreshold,
                UseSystemClock = false,
                DefaultRetryMode = _config.Connection.DefaultRetryMode
                //ConnectionTimeout = 30000,
                //MessageCacheSize = 50,
                //LogLevel = LogSeverity.Info,
                //AlwaysDownloadUsers = true,
                //HandlerTimeout = 15000,
                //TotalShards = 1,
                //LargeThreshold = 250,
                //UseSystemClock = false,
                //DefaultRetryMode = RetryMode.AlwaysRetry
            };
            return new DiscordSocketClient(config);
        }

        private StartupType _currentStartupType = StartupType.FirstStart;
        private string? _startupReason;
        private StartupType _nextStartupType = StartupType.FirstStart;
        private string? _nextStartupReason;
        private DateTime _startupTime;

        public bool ShouldExit => _shouldExit;
        public bool ShouldRestart => _shouldRestart;
        public StartupType NextStartupType => _nextStartupType;
        public string? NextStartupReason => _nextStartupReason;

		public Task RestartAsync()
		{
			// Перезапуск по команде из консоли
			return RestartWithReasonAsync(
				initiator: "console",
				reason: "Перезапуск по команде из консоли");
		}

		private async Task RestartWithReasonAsync(string initiator, string reason)
		{
			// Идемпотентность, чтобы не запускать рестарт повторно из разных потоков
			if (_shouldExit)
				return;

			// Останавливаем планировщик, чтобы он не сработал повторно во время выключения
			StopDailyRestartScheduler();

			// Signal UI and background tasks to prepare for restart
			_restartInitiator = initiator;
			if (_ui != null && _uiStarted)
			{
				// Требование: логировать "Ежедневная перезагрузка" при плановом рестарте
				if (string.Equals(reason, "Ежедневная перезагрузка", StringComparison.OrdinalIgnoreCase))
					_ui.AddLog("Ежедневная перезагрузка");

				_ui.AddLog($"Перезапуск... Инициатор: {_restartInitiator}");
				_ui.ClearForRestart();
				// Do not dispose UI here — the persistent UI thread will remain active
			}

			// Отправляем уведомление в Discord о перезапуске (best-effort)
			try
			{
				if (_statusNotifier != null)
					await _statusNotifier.SendRestartNotification(reason);
			}
			catch { }

			_shouldRestart = true;
			_shouldExit = true;
			_currentStartupType = StartupType.Restart;
			_startupReason = reason;
			_nextStartupType = StartupType.Restart;
			_nextStartupReason = _startupReason;
			_statusNotifier?.SetStartupContext(StartupType.Restart, _startupReason);
			_reconnectionService?.Shutdown();

				// Отменяем фоновый мониторинг и ждём его завершения
				try { _backgroundMonitoringCts?.Cancel(); } catch { }

				// Stop Discord client (best-effort)
				try { await _client.StopAsync(); } catch { }

				// Await background monitoring task to finish (with timeout)
				if (_backgroundMonitoringTask != null)
				{
					try
					{
						var t = await Task.WhenAny(_backgroundMonitoringTask, Task.Delay(3000));
						if (t != _backgroundMonitoringTask)
						{
							await LogStartup("Background tasks did not complete within timeout before restart.");
						}
					}
					catch { }
				}

			await LogShutdownState(isRestart: true, initiator: _restartInitiator);
		}

		public Task StopAsync()
		{
			// Остановка по команде из консоли с общей логикой выключения
			return StopInternalAsync(
				initiator: "console",
				startupLogMessage: "Остановка из консоли...",
				shutdownNotificationReason: "Остановка по команде из консоли");
		}

		private async Task StopInternalAsync(string initiator, string startupLogMessage, string shutdownNotificationReason)
		{
			await LogStartup(startupLogMessage);

			try
			{
				if (_statusNotifier != null)
					await _statusNotifier.SendShutdownNotification(shutdownNotificationReason);
			}
			catch { }

			if (_ui != null && _uiStarted)
			{
				_ui.AddLog(startupLogMessage);
			}

			_shouldExit = true;
				StopDailyRestartScheduler();
				_reconnectionService?.Shutdown();

				// Отменяем фоновый мониторинг
				try { _backgroundMonitoringCts?.Cancel(); } catch { }

				try { await _client.StopAsync(); } catch { }

				if (_backgroundMonitoringTask != null)
				{
					try
					{
						var t = await Task.WhenAny(_backgroundMonitoringTask, Task.Delay(3000));
						if (t != _backgroundMonitoringTask)
						{
							await LogStartup("Background tasks did not complete within timeout before stop.");
						}
					}
					catch { }
				}

			await LogShutdownState(isRestart: false, initiator: initiator);

			// Dispose and exit
			try { await DisposeAsync(); } catch { }
			Environment.Exit(0);
		}

		private void StartDailyRestartScheduler()
		{
			if (_config != null && !_config.DailyRestartEnabled)
				return;

			if (_dailyRestartTask != null && !_dailyRestartTask.IsCompleted)
				return;

			StopDailyRestartScheduler();
			_dailyRestartCts = new CancellationTokenSource();
			_dailyRestartTask = Task.Run(() => DailyRestartLoopAsync(_dailyRestartCts.Token));
		}

		private void StopDailyRestartScheduler()
		{
			try { _dailyRestartCts?.Cancel(); } catch { }
			try { _dailyRestartCts?.Dispose(); } catch { }
			_dailyRestartCts = null;
		}

		private async Task DailyRestartLoopAsync(CancellationToken ct)
		{
			try
			{
				if (_config != null && !_config.DailyRestartEnabled)
					return;

				var (nextUtc, planText) = GetNextDailyRestartUtc();
				var delay = nextUtc - DateTimeOffset.UtcNow;
				if (delay < TimeSpan.Zero)
					delay = TimeSpan.Zero;

				await LogStartup($"Ежедневная перезагрузка: запланирована на {planText}");

				await Task.Delay(delay, ct);

				if (ct.IsCancellationRequested || _shouldExit)
					return;

				await RestartWithReasonAsync(
					initiator: "scheduler",
					reason: "Ежедневная перезагрузка");
			}
			catch (TaskCanceledException)
			{
				// normal
			}
			catch (Exception ex)
			{
				try { await LogStartup($"⚠️ DailyRestartLoop error: {ex.Message}"); } catch { }
			}
		}

		private (DateTimeOffset NextUtc, string PlanText) GetNextDailyRestartUtc()
		{
			var nowUtc = DateTimeOffset.UtcNow;
			var localTz = TimeZoneInfo.Local;

			static bool TryParseTime(string? value, out TimeSpan time)
			{
				time = default;
				if (string.IsNullOrWhiteSpace(value))
					return false;

				var trimmed = value.Trim();
				if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out var parsed) || TimeSpan.TryParse(trimmed, out parsed))
				{
					time = new TimeSpan(parsed.Hours, parsed.Minutes, parsed.Seconds);
					return true;
				}

				return false;
			}

			static DateTime BuildUnspecifiedDateTime(DateTime date, TimeSpan time)
			{
				return new DateTime(date.Year, date.Month, date.Day, time.Hours, time.Minutes, time.Seconds, DateTimeKind.Unspecified);
			}

			static DateTimeOffset NextInZoneUtc(TimeZoneInfo tz, TimeSpan targetTime, DateTimeOffset currentUtc)
			{
				var nowInZone = TimeZoneInfo.ConvertTime(currentUtc, tz);
				var nextDate = nowInZone.Date;
				if (nowInZone.TimeOfDay >= targetTime)
					nextDate = nextDate.AddDays(1);

				var nextLocal = BuildUnspecifiedDateTime(nextDate, targetTime);
				var nextUtc = TimeZoneInfo.ConvertTimeToUtc(nextLocal, tz);

				// Safety: гарантируем, что время действительно в будущем.
				if (nextUtc <= currentUtc.UtcDateTime)
				{
					nextDate = nextDate.AddDays(1);
					nextLocal = BuildUnspecifiedDateTime(nextDate, targetTime);
					nextUtc = TimeZoneInfo.ConvertTimeToUtc(nextLocal, tz);
				}

				return new DateTimeOffset(nextUtc, TimeSpan.Zero);
			}

			if (!TryParseTime(_config?.DailyRestartLocalTime, out var localTarget))
				throw new InvalidOperationException("Daily restart time is not configured. Set DailyRestartLocalTime in config.json.");

			if (_config?.DailyRestartPreferMoscowTimeWhenLocalIsMoscow == true &&
				TryGetMoscowTimeZone(out var mskTz) && mskTz != null &&
				string.Equals(TimeZoneInfo.Local.Id, mskTz.Id, StringComparison.OrdinalIgnoreCase) &&
				TryParseTime(_config?.DailyRestartMoscowTime, out var mskTarget))
			{
				var nextUtc = NextInZoneUtc(mskTz, mskTarget, nowUtc);
				var nextMsk = TimeZoneInfo.ConvertTime(nextUtc, mskTz);
				return (nextUtc, $"{nextMsk:dd.MM.yyyy HH:mm:ss} (МСК)");
			}

			var nextLocalUtc = NextInZoneUtc(localTz, localTarget, nowUtc);
			var nextLocal = TimeZoneInfo.ConvertTime(nextLocalUtc, localTz);
			return (nextLocalUtc, $"{nextLocal:dd.MM.yyyy HH:mm:ss} (локальное)");
		}

		private static bool TryGetMoscowTimeZone(out TimeZoneInfo? mskTz)
		{
			var candidates = new[] { "Europe/Moscow", "Russian Standard Time" };
			foreach (var id in candidates)
			{
				try
				{
					mskTz = TimeZoneInfo.FindSystemTimeZoneById(id);
					return true;
				}
				catch { }
			}

			mskTz = null;
			return false;
		}

		private static bool TryGetMoscowTime(DateTime utc, out DateTime msk)
		{
			if (TryGetMoscowTimeZone(out var tz) && tz != null)
			{
				msk = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
				return true;
			}

			msk = utc;
			return false;
		}

        // Методы для доступа из UI (реализация IBotController)
        public Task<Dictionary<ulong, ServerConfig>> GetAllServerConfigsAsync()
        {
			// Клонируем текущий словарь _serverConfigs, чтобы избежать внешней модификации.
			return Task.FromResult(new Dictionary<ulong, ServerConfig>(_serverConfigs));
        }

        public Task<ServerConfig?> GetServerConfigAsync(ulong guildId)
        {
			if (_serverConfigs.TryGetValue(guildId, out var cfg))
				return Task.FromResult<ServerConfig?>(cfg);
			
			return Task.FromResult<ServerConfig?>(null);
        }

		private ServerConfig? GetServerConfigInternal(ulong guildId)
		{
			if (_serverConfigs.TryGetValue(guildId, out var cfg))
				return cfg;
			return null;
		}

		public Task ReloadServerConfigsAsync()
		{
			LoadServerConfigs();
			return Task.CompletedTask;
		}

        public async Task SetServerConfigValueAsync(ulong guildId, string key, string? value = null, ulong? channelId = null, bool? toggle = null)
        {
            if (!_serverConfigs.TryGetValue(guildId, out var sconfig))
            {
                sconfig = new ServerConfig { GuildID = guildId };
                _serverConfigs[guildId] = sconfig;
            }

            var guild = _client.GetGuild(guildId);

            switch (key.ToLowerInvariant())
            {
                case "moderation_channel":
                    {
                        var resolved = await ResolveChannelIdAsync(guildId, channelId, value);
                        if (resolved.HasValue) sconfig.ModerateChannelID = resolved.Value;
                    }
                    break;
                case "roll_channel":
                    {
                        var resolved = await ResolveChannelIdAsync(guildId, channelId, value);
                        if (resolved.HasValue) sconfig.RollChannelID = resolved.Value;
                    }
                    break;
                case "stats_channel":
                    {
                        var resolved = await ResolveChannelIdAsync(guildId, channelId, value);
                        if (resolved.HasValue) sconfig.StatsChannelID = resolved.Value;
                    }
                    break;
                case "record_channel":
                    {
                        var resolved = await ResolveChannelIdAsync(guildId, channelId, value);
                        if (resolved.HasValue) sconfig.RecordChannelID = resolved.Value;
                    }
                    break;
                case "welcome_channel":
                    {
                        var resolved = await ResolveChannelIdAsync(guildId, channelId, value);
                        if (resolved.HasValue) sconfig.WelcomeChannelID = resolved.Value;
                    }
                    break;
                case "general_rg_channel":
                    {
                        var resolved = await ResolveChannelIdAsync(guildId, channelId, value);
                        if (resolved.HasValue) sconfig.GeneralRGChannelID = resolved.Value;
                    }
                    break;
                case "welcome_message":
                    sconfig.WelcomeMessage = value ?? "";
                    break;
                case "line_message":
                    sconfig.LineMessage = value ?? "";
                    break;
                case "default_role":
                    if (guild != null)
                    {
                        var resolved = ResolveRoleId(guild, value);
                        if (resolved.HasValue) sconfig.DefaultRoleID = resolved.Value;
                    }
                    break;
                case "master_role":
                    if (guild != null)
                    {
                        var resolved = ResolveRoleId(guild, value);
                        if (resolved.HasValue) sconfig.MasterRoleId = resolved.Value;
                    }
                    break;
				case "super_user_role":
                   if (guild != null)
                    {
                        var resolved = ResolveRoleId(guild, value);
                        if (resolved.HasValue) sconfig.SuperUserRoleId = resolved.Value;
                    }
					break;
                case "swear_filter":
                    if (toggle.HasValue) sconfig.SwearFilterEnabled = toggle.Value;
                    else if (!string.IsNullOrWhiteSpace(value) && bool.TryParse(value, out var b)) sconfig.SwearFilterEnabled = b;
                    break;
                case "swear_words":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        sconfig.SwearWords = value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    }
                    break;
                case "predictions":
                    if (toggle.HasValue) sconfig.PredictionsEnabled = toggle.Value;
                    else if (!string.IsNullOrWhiteSpace(value) && bool.TryParse(value, out var p)) sconfig.PredictionsEnabled = p;
                    break;
                case "roll_pictures":
                    if (toggle.HasValue) sconfig.RollPicturesEnabled = toggle.Value;
                    else if (!string.IsNullOrWhiteSpace(value) && bool.TryParse(value, out var rp)) sconfig.RollPicturesEnabled = rp;
                    break;
                case "event_voice_channel":
                    {
                        var resolved = await ResolveChannelIdAsync(guildId, channelId, value, requireVoice: true);
                        if (resolved.HasValue) sconfig.EventVoiceChannelID = resolved.Value;
                    }
                    break;
				default:
					break;
			}

			SaveServerConfigs();

            try
            {
                var validationGuild = _client.GetGuild(guildId);
                if (validationGuild != null)
                {
                    if (sconfig.ModerateChannelID != 0)
                    {
                        var ch = validationGuild.GetTextChannel(sconfig.ModerateChannelID);
                        if (ch == null)
                            _ = LogInfo($"Предупреждение: moderation_channel {sconfig.ModerateChannelID} не найден на сервере {guildId}.");
                    }

                    if (sconfig.RollChannelID != 0)
                    {
                        var ch = validationGuild.GetTextChannel(sconfig.RollChannelID);
                        if (ch == null)
                            _ = LogInfo($"Предупреждение: roll_channel {sconfig.RollChannelID} не найден на сервере {guildId}.");
                    }

                    if (sconfig.StatsChannelID != 0)
                    {
                        var ch = validationGuild.GetTextChannel(sconfig.StatsChannelID);
                        if (ch == null)
                            _ = LogInfo($"Предупреждение: stats_channel {sconfig.StatsChannelID} не найден на сервере {guildId}.");
                    }

                    if (sconfig.WelcomeChannelID != 0)
                    {
                        var ch = validationGuild.GetTextChannel(sconfig.WelcomeChannelID);
                        if (ch == null)
                            _ = LogInfo($"Предупреждение: welcome_channel {sconfig.WelcomeChannelID} не найден на сервере {guildId}.");
                    }


                    if (sconfig.GeneralRGChannelID != 0)
                    {
                        var ch = validationGuild.GetTextChannel(sconfig.GeneralRGChannelID);
                        if (ch == null)
                            _ = LogInfo($"Предупреждение: general_rg_channel {sconfig.GeneralRGChannelID} не найден на сервере {guildId}.");
                    }

                    if (sconfig.DefaultRoleID != 0)
                    {
                        var role = validationGuild.Roles.FirstOrDefault(r => r.Id == sconfig.DefaultRoleID);
                        if (role == null)
                            _ = LogInfo($"Предупреждение: роль {sconfig.DefaultRoleID} не найдена на сервере {guildId}.");
                    }

                    if (sconfig.MasterRoleId.HasValue && sconfig.MasterRoleId.Value != 0)
                    {
                       var masterRole = validationGuild.Roles.FirstOrDefault(r => r.Id == sconfig.MasterRoleId.Value);
                        if (masterRole == null)
                            _ = LogInfo($"Предупреждение: MasterRoleId {sconfig.MasterRoleId.Value} не найдена на сервере {guildId}.");
                    }

					if (sconfig.SuperUserRoleId.HasValue && sconfig.SuperUserRoleId.Value != 0)
					{
                        var suRole = validationGuild.Roles.FirstOrDefault(r => r.Id == sconfig.SuperUserRoleId.Value);
						if (suRole == null)
							_ = LogInfo($"Предупреждение: SuperUserRoleId {sconfig.SuperUserRoleId.Value} не найдена на сервере {guildId}.");
					}
                }

            }
            catch (Exception ex)
            {
                _ = LogError($"Ошибка валидации конфигурации при установке: {ex.Message}");
            }

            SaveServerConfigs();
        }

        public Task ResetServerConfigAsync(ulong guildId)
        {
            if (_serverConfigs.ContainsKey(guildId))
                _serverConfigs.Remove(guildId);

            if (ServerConfigs.ContainsKey(guildId))
                ServerConfigs.Remove(guildId);

            SaveServerConfigs();
            return Task.CompletedTask;
        }

        public void SetStartupType(StartupType type)
        {
            SetStartupContext(type, null);
        }

        public void SetStartupContext(StartupType type, string? reason)
        {
            _currentStartupType = type;
            _startupReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
            _statusNotifier?.SetStartupContext(type, _startupReason);
        }

        static async Task Main(string[] args)
        {
            // Гарантированное завершение Lavalink при любом способе остановки (VS Stop, taskkill и т.д.)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => KillOrphanedLavalink();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; KillOrphanedLavalink(); };

            bool restart;
            int restartCount = 0;
            var pendingStartupType = StartupType.FirstStart;
            string? pendingStartupReason = null;

            do
            {
                restart = false;

                if (restartCount > 0)
                {
                // Очистка консоли отключена; уведомление через UI
                _ = Task.Run(() => _ui?.AddLog($"ПЕРЕЗАПУСК #{restartCount} в {DateTime.Now:HH:mm:ss}"));
                }

                using (var program = new Program())
                {
                    program.SetStartupContext(pendingStartupType, pendingStartupReason);

                    await program.RunBotAsync();
                    restart = program.ShouldRestart;
                    pendingStartupType = restart ? program.NextStartupType : StartupType.FirstStart;
                    pendingStartupReason = restart ? program.NextStartupReason : null;
                    restartCount++;
                }

                if (restart)
                {
                    _ = Task.Run(() => _ui?.AddLog("Подготовка к перезапуску..."));
                    await Task.Delay(2000); // Небольшая пауза перед перезапуском
                }

            } while (restart);

			_ = Task.Run(() => _ui?.AddLog("Бот остановлен."));
		}

		/// <summary>
		/// Убивает процессы Lavalink (java) занимающие порт 2333.
		/// Вызывается при любом завершении — штатном или через VS Stop/taskkill.
		/// </summary>
		private static void KillOrphanedLavalink()
		{
			try
			{
				var connections = System.Net.NetworkInformation.IPGlobalProperties
					.GetIPGlobalProperties()
					.GetActiveTcpListeners()
					.Where(ep => ep.Port == 2333)
					.ToArray();

				if (connections.Length == 0) return;

				// Убиваем все java-процессы слушающие порт 2333
				foreach (var proc in Process.GetProcessesByName("java"))
				{
					try { proc.Kill(entireProcessTree: true); } catch { }
				}
			}
			catch { }
		}

		private static BotUI? _ui;
        private static bool _uiStarted = false;

        public async Task RunBotAsync()
        {
            _startupTime = DateTime.UtcNow;

			if (_ui == null)
			{
				_ui = new BotUI(
					_client,
					this,
					_reconnectionService!,
					_connectionPredictor!,
					_statusNotifier!,
					_pointsService
				);

                // Запускаем UI в отдельном потоке
                var uiThread = new Thread(() =>
                {
                    try
                    {
                        _ui.Start();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Критическая ошибка UI: {ex.Message}");
                        Environment.Exit(1);
                    }
                })
                {
                    IsBackground = true,
                    Name = "BotUI"
                };
                uiThread.Start();

                // Даем UI время на инициализацию
                await Task.Delay(2000);
                _uiStarted = true;

                CommandLogSink = msg => _ui?.AddLog(msg);

                // Перенаправляем весь Console в UI-панель логов
                if (_originalOut == null) _originalOut = Console.Out;
                if (_originalErr == null) _originalErr = Console.Error;
                 var terminalLogDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(_config?.LogDirectory) ? "Logs" : _config!.LogDirectory);
                    var terminalLogPath = Path.Combine(terminalLogDir, $"TerminalLog_{DateTime.Now:yyyyMMdd}.txt");
                    var uiWriter = new UiTextWriter(() => _ui, terminalLogPath);
                Console.SetOut(uiWriter);
                Console.SetError(uiWriter);
            }
            else
            {
                // При рестарте просто обновляем сервисы
                _ui.UpdateServices(_client, _reconnectionService, _connectionPredictor, _statusNotifier);
                _ui.AddLog("Перезапуск бота...");

                CommandLogSink = msg => _ui?.AddLog(msg);
            }

			// Загрузка текстовых блоков из пути конфига (относительные пути считаем от каталога приложения)
			_textBlocks = LoadTextFromFile(BotConfig.ResolvePath(_config.TextBlocksPath));

            while (!_isDisposed && !_shouldExit)
            {
                await _restartLock.WaitAsync();
                try
                {
                    if (_currentStartupType == StartupType.Reconnect)
                    {
                        _startupReason ??= "Восстановление соединения после ошибки подключения";
                    }

                    // Показываем специальное сообщение при рестарте
         var version = _config?.BotVersion ?? BotConfig.Current?.BotVersion ?? "?";
                    if (_currentStartupType == StartupType.Restart)
                    {
                        await LogStartup($"Инициализация бота после перезапуска... Версия {version}");
                    }
                    else
                    {
                        await LogStartup($"Инициализация бота... Версия {version}");
                    }

                    if (_client == null || _client.ConnectionState == ConnectionState.Disconnected)
                    {
                        _client?.Dispose();
                        _client = CreateDiscordClient();

                        CleanupServices();

                        _reconnectionService = new ReconnectionService(_client)
                        {
                            LogSink = ServiceLogSink
                        };
                        _connectionPredictor = new ConnectionPredictor(_reconnectionService, _config.Prediction);
                        _statusNotifier = new StatusNotifier(_client, ServerConfigs)
                        {
                            LogSink = ServiceLogSink
                        };
                        _statusNotifier.SetStartupContext(_currentStartupType, _startupReason);

                        var predictionsLogPath = BotConfig.ResolvePath(Path.Combine(_config.LogDirectory ?? "Logs", "predictions.log"));
                        _predictionService = new PredictionService(_client, _pointsService, predictionsLogPath);
                        _voicePointsService = new VoicePointsService(_client, _pointsService, GetServerConfigInternal, predictionsLogPath);
                        _reconnectionService.OnDisconnectDetected += OnDisconnectDetected;
                        _reconnectionService.OnReconnectStarted += OnReconnectStarted;
                        _reconnectionService.OnReconnectCompleted += OnReconnectCompleted;
                        _reconnectionService.OnFullRestartRequested += OnFullRestartRequested;
                        _connectionPredictor.OnPredictionMade += OnPredictionMade;

                        _ui?.UpdateServices(
                            _client,
                            _reconnectionService,
                            _connectionPredictor,
                            _statusNotifier
                        );

                        // Переподписываем MusicCommands на новый клиент
                        if (_musicCommands is not null)
                            _musicCommands.UpdateDiscordClient(_client);
                    }

                    _commandHandler = new CommandHandler(_client, _config.GuildIDs);
                    CommandHandler.SetUI(_ui);

                    await SetupDiscordEvents();
                    try
                    {
                        await _client.LoginAsync(TokenType.Bot, GetBotToken());
                        await _client.StartAsync();
                        await LogStartup(" Вход выполнен успешно.");

                        // PrepareAsync ПОСЛЕ LoginAsync — CurrentUser уже установлен,
                        // DiscordClientWrapper подпишется на Ready до того как оно сработает
                        if (_lavalinkService is not null)
                            await _lavalinkService.PrepareAsync();

                        // Ждем готовности
                        await WaitForReadyAsync();

                        // Запускаем инициализацию с опросом (Lavalink запускается внутри на Этапе 3.5)
                        await InitializeBotWithProgress();

						// Ежедневный плановый перезапуск (время задаётся в config.json)
						StartDailyRestartScheduler();

                        // Запускаем фоновый мониторинг (с обёрткой для логирования ошибок)
                        _backgroundMonitoringCts?.Cancel();
                        _backgroundMonitoringCts?.Dispose();
                        _backgroundMonitoringCts = new CancellationTokenSource();
                        _backgroundMonitoringTask = Task.Run(() => BackgroundMonitoringLoopWrapper(_backgroundMonitoringCts.Token));

                        while (!_shouldExit)
                        {
                            await Task.Delay(1000);
                        }
                    }
                    catch (Exception ex) when (ex.Message.Contains("FULL_RESTART_REQUIRED"))
                    {
                        await LogStartup(" Требуется полная перезагрузка клиента...");
                    }
                    catch (Exception ex)
                    {
                        await LogStartup($" Ошибка запуска: {ex.Message}");
                        if (!_shouldExit)
                        {
                            await LogStartup(" Повторная попытка через 10 секунд...");
                            await Task.Delay(10000);
                        }
                    }
                }

                catch (Exception ex)
                {
                    await LogStartup($" Критическая ошибка: {ex.Message}");
                    if (!_shouldExit)
                    {
                        await Task.Delay(10000);
                    }
                }
                finally
                {
                    _restartLock.Release();
                }
            }
        }

// Отдельные обработчики для событий
        private async Task OnGuildScheduledEventCreated(SocketGuildEvent guildEvent)
        {
            try
            {
               try
                {
                    Console.WriteLine($"[EVENT] created guild={guildEvent.Guild?.Id} event={guildEvent.Id} name='{guildEvent.Name}'");
                }
                catch { }

                await AnnounceGuildScheduledEventCreated(guildEvent);
            }
            catch (Exception ex)
            {
                await LogError($"Ошибка в OnGuildScheduledEventCreated: {ex.Message}");
            }
        }

        private async Task OnGuildScheduledEventUpdated(Cacheable<SocketGuildEvent, ulong> before, SocketGuildEvent after)
        {
            try
            {
                try
                {
                    Console.WriteLine($"[EVENT] updated guild={after.Guild?.Id} event={after.Id} name='{after.Name}'");
                }
                catch { }

                await AnnounceGuildScheduledEventUpdated(before, after);
            }
            catch (Exception ex)
            {
                await LogError($"Ошибка в OnGuildScheduledEventUpdated: {ex.Message}");
            }
        }

        private async Task OnGuildScheduledEventCancelled(SocketGuildEvent guildEvent)
        {
            try
            {
                try
                {
                    Console.WriteLine($"[EVENT] cancelled guild={guildEvent.Guild?.Id} event={guildEvent.Id} name='{guildEvent.Name}'");
                }
                catch { }

                // ✅ НОВОЕ: Автоматическая отмена прогноза при отмене события
                if (guildEvent.Guild != null)
                {
                    await AutoCancelPredictionOnEventEnd(guildEvent.Guild.Id, "Событие было отменено");
                }

                await AnnounceGuildScheduledEventStatusChanged(guildEvent, status: "cancelled");
            }
            catch (Exception ex)
            {
                await LogError($"Ошибка в OnGuildScheduledEventCancelled: {ex.Message}");
            }
        }

        private async Task OnGuildScheduledEventStarted(SocketGuildEvent guildEvent)
        {
            try
            {
                await GameSessionCommands.OnGuildScheduledEventStarted(guildEvent, _client);
               try
                {
                    Console.WriteLine($"[EVENT] started guild={guildEvent.Guild?.Id} event={guildEvent.Id} name='{guildEvent.Name}'");
                }
                catch { }

                await AnnounceGuildScheduledEventStatusChanged(guildEvent, status: "started");
            }
            catch (Exception ex)
            {
                await LogError($"Ошибка в OnGuildScheduledEventStarted: {ex.Message}");
            }
        }

        private async Task OnGuildScheduledEventCompleted(SocketGuildEvent guildEvent)
        {
            try
            {
                await GameSessionCommands.OnGuildScheduledEventCompleted(guildEvent, _client, _googleSheetsService, CommandLogSink);
               try
                {
                    Console.WriteLine($"[EVENT] completed guild={guildEvent.Guild?.Id} event={guildEvent.Id} name='{guildEvent.Name}'");
                }
                catch { }

                // ✅ НОВОЕ: Автоматическая отмена прогноза при завершении события
                if (guildEvent.Guild != null)
                {
                    await AutoCancelPredictionOnEventEnd(guildEvent.Guild.Id, "Событие завершено");
                }

                await AnnounceGuildScheduledEventStatusChanged(guildEvent, status: "completed");
            }
            catch (Exception ex)
            {
                await LogError($"Ошибка в OnGuildScheduledEventCompleted: {ex.Message}");
            }
        }

        /// <summary>
        /// Автоматическая отмена активного прогноза при завершении/отмене события
        /// </summary>
        private async Task AutoCancelPredictionOnEventEnd(ulong guildId, string reason)
        {
            try
            {
                var prediction = _predictionService.GetActive(guildId);
                if (prediction != null && !prediction.IsResolved)
                {
                    // Отменяем прогноз от имени системы с административным доступом
                    var (ok, error) = await _predictionService.CancelAsync(
                        guildId,
                        resolverId: prediction.CreatorId, // используем ID создателя
                        isAdminOverride: true, // административная отмена
                        cancelReason: $"⚠️ {reason}. Все ставки возвращены.");

                    if (ok)
                    {
                        Console.WriteLine($"[PREDICTION] Auto-cancelled prediction for guild={guildId} reason='{reason}'");
                    }
                    else
                    {
                        Console.WriteLine($"[PREDICTION] Failed to auto-cancel prediction for guild={guildId}: {error}");
                    }
                }
            }
            catch (Exception ex)
            {
                await LogError($"Ошибка в AutoCancelPredictionOnEventEnd: {ex.Message}");
            }
        }

        private async Task ResyncEventAnnouncementsOnStartupAsync()
        {
            if (_eventAnnouncementStore == null)
                return;

            var entries = _eventAnnouncementStore.GetEntriesSnapshot();

            // Индекс сохранённых анонсов для быстрого поиска
            var announcedKeys = new HashSet<(ulong guildId, ulong eventId)>(
                entries.Select(e => (e.GuildId, e.EventId)));

            await LogStartup($"[EVENT][RESYNC] Начало синхронизации: сохранённых анонсов={entries.Count}.");

            var updated = 0;
            var removed = 0;
            var announced = 0;
            var failed = 0;

            // ── Шаг 1: обновить / удалить уже известные анонсы ──────────────────
            foreach (var entry in entries)
            {
                try
                {
                    var guild = _client.GetGuild(entry.GuildId);
                    if (guild == null)
                    {
                        failed++;
                        continue;
                    }

                    var guildEvent = guild.Events.FirstOrDefault(e => e.Id == entry.EventId);
                    if (guildEvent == null)
                    {
                        // Событие исчезло с сервера — удаляем запись
                        _eventAnnouncementStore.Remove(entry.GuildId, entry.EventId);
                        removed++;
                        continue;
                    }

                    var status = guildEvent.Status switch
                    {
                        GuildScheduledEventStatus.Active    => "started",
                        GuildScheduledEventStatus.Completed => "completed",
                        GuildScheduledEventStatus.Cancelled => "cancelled",
                        _                                   => "scheduled"
                    };

                    // Для запланированных событий только обновляем embed, если данные изменились
                    if (status == "scheduled")
                    {
                        // Обновляем анонс на случай, если описание/время изменилось оффлайн
                        await AnnounceGuildScheduledEventUpdatedAsync(guildEvent);
                        updated++;
                        continue;
                    }

                    await AnnounceGuildScheduledEventStatusChanged(guildEvent, status);
                    updated++;
                }
                catch (Exception ex)
                {
                    failed++;
                    await LogError($"[EVENT][RESYNC] Ошибка обновления guild={entry.GuildId}, event={entry.EventId}: {ex.Message}");
                }
            }

            // ── Шаг 2: анонсировать события, созданные пока бот был оффлайн ─────
            foreach (var guild in _client.Guilds)
            {
                try
                {
                    foreach (var guildEvent in guild.Events)
                    {
                        // Пропускаем уже анонсированные и завершённые/отменённые события
                        if (announcedKeys.Contains((guild.Id, guildEvent.Id)))
                            continue;
                        if (guildEvent.Status == GuildScheduledEventStatus.Completed ||
                            guildEvent.Status == GuildScheduledEventStatus.Cancelled)
                            continue;

                        await LogStartup($"[EVENT][RESYNC] Обнаружено новое событие (оффлайн): guild={guild.Id} event={guildEvent.Id} name='{guildEvent.Name}'");
                        await AnnounceGuildScheduledEventCreated(guildEvent);
                        announced++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    await LogError($"[EVENT][RESYNC] Ошибка скана событий guild={guild.Id}: {ex.Message}");
                }
            }

            await LogStartup($"[EVENT][RESYNC] Завершено: updated={updated}, removed={removed}, announced={announced}, failed={failed}.");
        }

        /// <summary>
        /// Обновляет embed уже анонсированного события (изменения оффлайн: название, описание, время).
        /// </summary>
        private async Task AnnounceGuildScheduledEventUpdatedAsync(SocketGuildEvent guildEvent)
        {
            if (guildEvent?.Guild == null || _eventAnnouncementStore == null)
                return;

            var guild = guildEvent.Guild;
            var entry = _eventAnnouncementStore.TryGet(guild.Id, guildEvent.Id);
            if (entry == null)
                return;

            if (!_serverConfigs.TryGetValue(guild.Id, out var config) || config.GeneralRGChannelID == 0)
                return;

            static string Truncate(string? value, int max)
            {
                if (string.IsNullOrWhiteSpace(value)) return string.Empty;
                value = value.Trim();
                return value.Length <= max ? value : value.Substring(0, max - 1) + "…";
            }

            var eventUrl  = $"https://discord.com/events/{guild.Id}/{guildEvent.Id}";
            var startLocal = guildEvent.StartTime.ToLocalTime();
            var imageUrl   = guildEvent.GetCoverImageUrl();

            string whereText;
            string whereTextPlain;
            if (guildEvent.Channel != null)
            {
                whereText = $"<#{guildEvent.Channel.Id}>";
                whereTextPlain = guildEvent.Channel.Name;
            }
            else if (!string.IsNullOrWhiteSpace(guildEvent.Location))
            {
                whereText = Truncate(guildEvent.Location, 256);
                whereTextPlain = whereText;
            }
            else
            {
                whereText = "не указано";
                whereTextPlain = whereText;
            }

            // Имя создателя: сначала MasterNameMap, потом Username
            string? createdByPlain = null;
            if (guildEvent.Creator != null)
            {
                if (_serverConfigs.TryGetValue(guild.Id, out var scUpd)
                    && scUpd.MasterNameMap?.TryGetValue(guildEvent.Creator.Id.ToString(), out var mappedUpd) == true
                    && !string.IsNullOrWhiteSpace(mappedUpd))
                    createdByPlain = mappedUpd;
                else
                    createdByPlain = guildEvent.Creator.Username;
            }

            var embedBuilder = new EmbedBuilder()
                .WithTitle($"📅 Событие: {Truncate(guildEvent.Name, 100)}")
                .WithUrl(eventUrl)
                .WithColor(Color.Blue);

            if (!string.IsNullOrWhiteSpace(imageUrl))
                embedBuilder.WithThumbnailUrl(imageUrl);

            if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                embedBuilder.WithDescription(Truncate(guildEvent.Description, 2048));

            embedBuilder.AddField("🏰 Сервер", guild.Name, true);
            embedBuilder.AddField("🕒 Когда", startLocal.ToString("dd.MM.yyyy HH:mm"), true);
            embedBuilder.AddField("📍 Где", whereText, true);

            if (guildEvent.Creator != null)
                embedBuilder.AddField("👤 Создал", MentionUtils.MentionUser(guildEvent.Creator.Id), true);

            embedBuilder.WithFooter("Чтобы приходило в личку: /event_notify subscribe • Выкл: напиши «стоп» • Вкл: «хочу»");

            var embed = embedBuilder;
            _ = createdByPlain; // используется ниже в Telegram

            // Обновляем сообщение в канале
            if (entry.AnnounceMessageId != 0)
            {
                try
                {
                    var announceChannel = await _client.GetChannelAsync(config.GeneralRGChannelID) as ITextChannel;
                    if (announceChannel != null)
                    {
                        var msg = await announceChannel.GetMessageAsync(entry.AnnounceMessageId) as IUserMessage;
                        if (msg != null)
                            await msg.ModifyAsync(p => p.Embed = embed.Build());
                    }
                }
                catch { /* сообщение могло быть удалено */ }
            }

            // Обновляем DM-сообщения у подписчиков
            if (entry.DmMessageIdsByUserId?.Count > 0)
            {
                var subscribers = _eventNotifications.GetActiveSubscribers(guild.Id);
                foreach (var userId in subscribers)
                {
                    if (!entry.DmMessageIdsByUserId.TryGetValue(userId, out var dmMsgId) || dmMsgId == 0)
                        continue;
                    try
                    {
                        var user = await _client.GetUserAsync(userId);
                        if (user == null) continue;
                        var dmChannel = await user.CreateDMChannelAsync();
                        var dmMsg = await dmChannel.GetMessageAsync(dmMsgId) as IUserMessage;
                        if (dmMsg != null)
                            await dmMsg.ModifyAsync(p => p.Embed = embed.Build());
                    }
                    catch { }
                }
            }

            // Обновляем Telegram
            if (_telegramNotifier != null && entry.TelegramChatId != 0 && entry.TelegramMessageId != 0)
            {
                try
                {
                    var tgText = $"📅 Событие: {guildEvent.Name}\n" +
                        $"🏰 Сервер: {guild.Name}\n" +
                        $"🕒 Когда: {startLocal:dd.MM.yyyy HH:mm}\n" +
                        $"📍 Где: {whereTextPlain}\n" +
                        (createdByPlain != null ? $"👤 Создал: {createdByPlain}\n" : string.Empty) +
                        $"🔗 {eventUrl}";
                    if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                    {
                        var desc = guildEvent.Description.Trim();
                        if (desc.Length > 800) desc = desc.Substring(0, 799) + "…";
                        tgText += $"\n\nОписание события:\n{desc}";
                    }
                    await _telegramNotifier.EditMessageTextAsync(guild.Id, entry.TelegramMessageId, tgText);
                }
                catch { }
            }

            entry.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            _eventAnnouncementStore.Upsert(entry);
        }

        private async Task AnnounceGuildScheduledEventStatusChanged(SocketGuildEvent guildEvent, string status)
        {
            if (guildEvent?.Guild == null)
                return;
            if (_eventAnnouncementStore == null)
                return;

            var guild = guildEvent.Guild;
            var entry = _eventAnnouncementStore.TryGet(guild.Id, guildEvent.Id);
            if (entry == null)
                return;

                var eventUrl = $"https://discord.com/events/{guild.Id}/{guildEvent.Id}";
            var startLocal = guildEvent.StartTime.ToLocalTime();
            var imageUrl = guildEvent.GetCoverImageUrl();

           var isCancelled = string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);
            var isStarted = string.Equals(status, "started", StringComparison.OrdinalIgnoreCase);
            var isCompleted = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);
            var prefix = isCancelled ? "❌" : isStarted ? "▶️" : isCompleted ? "✅" : "ℹ️";
            var statusText = isCancelled ? "Событие отменено/удалено" : isStarted ? "Событие началось" : isCompleted ? "Событие завершено" : "Событие обновлено";
            var mark = $"{prefix} {statusText}: {DateTime.Now:dd.MM.yyyy HH:mm}";

            string whereText;
            string whereTextPlain;
            if (guildEvent.Channel != null)
            {
                whereText = $"<#{guildEvent.Channel.Id}>";
                whereTextPlain = guildEvent.Channel.Name;
            }
            else if (!string.IsNullOrWhiteSpace(guildEvent.Location))
            {
                whereText = guildEvent.Location;
                whereTextPlain = whereText;
            }
            else
            {
                whereText = "не указано";
                whereTextPlain = whereText;
            }

           var embedBuilder = new EmbedBuilder()
                .WithTitle($"{prefix} {statusText}: {guildEvent.Name}")
                .WithUrl(eventUrl)
                .WithColor(isCancelled ? Color.DarkRed : isStarted ? Color.Green : isCompleted ? Color.DarkGreen : Color.Orange)
                .WithCurrentTimestamp();
            if (!string.IsNullOrWhiteSpace(imageUrl))
                embedBuilder.WithThumbnailUrl(imageUrl);
            if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                embedBuilder.WithDescription(guildEvent.Description.Length <= 2048 ? guildEvent.Description : guildEvent.Description.Substring(0, 2047) + "…");
            embedBuilder.AddField("🏰 Сервер", guild.Name, true);
            embedBuilder.AddField("🕒 Когда", startLocal.ToString("dd.MM.yyyy HH:mm"), true);
            embedBuilder.AddField("📍 Где", whereText, true);
            if (guildEvent.Creator != null)
                embedBuilder.AddField("👤 Создал", MentionUtils.MentionUser(guildEvent.Creator.Id), true);
               embedBuilder.AddField("Статус", mark, false);
            var embed = embedBuilder.Build();

            // Discord channel
            try
            {
                var ch = await _client.GetChannelAsync(entry.AnnounceChannelId) as ITextChannel;
                var msg = ch != null ? await ch.GetMessageAsync(entry.AnnounceMessageId) as IUserMessage : null;
                if (msg != null)
                {
                    await msg.ModifyAsync(m => m.Embed = embed);
                    try { Console.WriteLine($"[EVENT] status {status} discord_channel ok guild={guild.Id} event={guildEvent.Id} msg={entry.AnnounceMessageId}"); } catch { }
                }
            }
            catch (Exception ex)
            {
                try { Console.WriteLine($"[EVENT] status {status} discord_channel error guild={guild.Id} event={guildEvent.Id}: {ex.Message}"); } catch { }
            }

            // Discord DMs
            var subscriberIds = _eventNotifications.GetActiveSubscribers(guild.Id);
            foreach (var userId in subscriberIds)
            {
                try
                {
                    if (!entry.DmMessageIdsByUserId.TryGetValue(userId, out var dmMessageId) || dmMessageId == 0)
                        continue;
                    var user = guild.GetUser(userId) as IUser ?? _client.GetUser(userId);
                    if (user == null)
                        continue;
                    var dm = await user.CreateDMChannelAsync();
                    var dmMsg = await dm.GetMessageAsync(dmMessageId) as IUserMessage;
                    if (dmMsg != null)
                    {
                        await dmMsg.ModifyAsync(m => m.Embed = embed);
                        try { Console.WriteLine($"[EVENT] status {status} discord_dm ok guild={guild.Id} event={guildEvent.Id} user={userId} msg={dmMessageId}"); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    try { Console.WriteLine($"[EVENT] status {status} discord_dm error guild={guild.Id} event={guildEvent.Id} user={userId}: {ex.Message}"); } catch { }
                }
            }

            // Telegram
            try
            {
                if (_telegramNotifier != null)
                {
                    // Keep entry telegram routing synced with current server config.
                    if (_serverConfigs.TryGetValue(guild.Id, out var liveCfg))
                    {
                        entry.TelegramChatId = liveCfg.TelegramChatId;
                        entry.TelegramMessageThreadId = liveCfg.TelegramMessageThreadId;
                        _eventAnnouncementStore.Upsert(entry);
                    }

                    var tgText = $"{prefix} {statusText}: {guildEvent.Name}\n" +
                        $"🏰 Сервер: {guild.Name}\n" +
                        $"🕒 Когда: {startLocal:dd.MM.yyyy HH:mm}\n" +
                        $"📍 Где: {whereTextPlain}\n";

                    // При старте события подставляем имя мастера из активной сессии (учитывает MasterNameMap и ручные правки)
                    if (isStarted && guildEvent.Creator != null)
                    {
                        string masterDisplayName = null;
                        if (GameSessionCommands._sessions.TryGetValue(guild.Id, out var guildSessions))
                        {
                            var linkedSession = guildSessions.Values.FirstOrDefault(s => s.EventId == guildEvent.Id && !s.IsStopped);
                            if (linkedSession != null)
                                masterDisplayName = linkedSession.MasterName;
                        }
                        // Fallback: MasterNameMap по Creator.Id
                        if (masterDisplayName == null && _serverConfigs.TryGetValue(guild.Id, out var sc))
                        {
                            sc.MasterNameMap?.TryGetValue(guildEvent.Creator.Id.ToString(), out masterDisplayName);
                        }
                        masterDisplayName ??= guildEvent.Creator.Username;
                        tgText += $"👤 Мастер: {masterDisplayName}\n";
                    }
                    else if (guildEvent.Creator != null)
                    {
                        string creatorName = null;
                        if (_serverConfigs.TryGetValue(guild.Id, out var scSt)
                            && scSt.MasterNameMap?.TryGetValue(guildEvent.Creator.Id.ToString(), out var mappedSt) == true
                            && !string.IsNullOrWhiteSpace(mappedSt))
                            creatorName = mappedSt;
                        else
                            creatorName = guildEvent.Creator.Username;
                        tgText += $"👤 Создал: {creatorName}\n";
                    }

                    tgText += $"ℹ️ {mark}\n" +
                        $"🔗 {eventUrl}";

                    if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                    {
                        var desc = guildEvent.Description.Trim();
                        if (desc.Length > 800) desc = desc.Substring(0, 799) + "…";
                        tgText += $"\n\nОписание события:\n{desc}";
                    }

                    bool ok;
                    if (entry.TelegramMessageId > 0)
                    {
                        ok = entry.TelegramHasPhoto
                            ? await _telegramNotifier.EditMessageCaptionAsync(guild.Id, entry.TelegramMessageId, tgText)
                            : await _telegramNotifier.EditMessageTextAsync(guild.Id, entry.TelegramMessageId, tgText);
                        try { Console.WriteLine($"[EVENT] status {status} telegram {(ok ? "ok" : "fail")} guild={guild.Id} event={guildEvent.Id} msg={entry.TelegramMessageId}"); } catch { }
                    }
                    else
                    {
                        var sentId = await _telegramNotifier.SendMessageReturningMessageIdAsync(guild.Id, tgText);
                        ok = sentId.HasValue;
                        if (sentId.HasValue)
                        {
                            entry.TelegramMessageId = sentId.Value;
                            entry.TelegramHasPhoto = false;
                            _eventAnnouncementStore.Upsert(entry);
                        }
                        try { Console.WriteLine($"[EVENT] status {status} telegram {(ok ? "sent" : "skip/fail")} guild={guild.Id} event={guildEvent.Id} msg={(sentId ?? 0)}"); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                try { Console.WriteLine($"[EVENT] status {status} telegram error guild={guild.Id} event={guildEvent.Id}: {ex.Message}"); } catch { }
            }

                if (isCancelled || isCompleted)
                {
                    try { _eventAnnouncementStore.Remove(guild.Id, guildEvent.Id); } catch { }
                }
        }

		private async Task AnnounceGuildScheduledEventCreated(SocketGuildEvent guildEvent)
		{
			if (guildEvent?.Guild == null)
				return;

			var guild = guildEvent.Guild;
			if (!_serverConfigs.TryGetValue(guild.Id, out var config))
				return;

			if (config.GeneralRGChannelID == 0)
				return;

			var announceChannel = await _client.GetChannelAsync(config.GeneralRGChannelID) as ITextChannel;
			if (announceChannel == null)
				return;

			static string Truncate(string? value, int max)
			{
				if (string.IsNullOrWhiteSpace(value))
					return string.Empty;
				value = value.Trim();
				return value.Length <= max ? value : value.Substring(0, max - 1) + "…";
			}

			var eventUrl = $"https://discord.com/events/{guild.Id}/{guildEvent.Id}";
			var startLocal = guildEvent.StartTime.ToLocalTime();
			var endLocal = guildEvent.EndTime?.ToLocalTime();

           // Определяем, в каком канале будет проходить событие
            string whereText;
            string whereTextPlain;
            if (guildEvent.Channel != null)
            {
                // Discord: делаем упоминание канала; Telegram: показываем читаемое имя
                whereText = $"<#{guildEvent.Channel.Id}>";
                whereTextPlain = guildEvent.Channel.Name;
            }
            else if (!string.IsNullOrWhiteSpace(guildEvent.Location))
            {
                whereText = Truncate(guildEvent.Location, 256);
                whereTextPlain = whereText;
            }
            else
            {
                whereText = "не указано";
                whereTextPlain = whereText;
            }

			var embedBuilder = new EmbedBuilder()
				.WithTitle($"📅 Новое событие: {guildEvent.Name}")
				.WithUrl(eventUrl)
				.WithColor(Color.Blue)
				.WithCurrentTimestamp();

         var imageUrl = guildEvent.GetCoverImageUrl();
            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                embedBuilder.WithThumbnailUrl(imageUrl);
            }

			if (!string.IsNullOrWhiteSpace(guildEvent.Description))
			{
				embedBuilder.WithDescription(Truncate(guildEvent.Description, 2048));
			}

			embedBuilder.AddField("🏰 Сервер", guild.Name, true);
			embedBuilder.AddField("🕒 Когда", startLocal.ToString("dd.MM.yyyy HH:mm"), true);
			embedBuilder.AddField("📍 Где", whereText, true);

         string? createdByPlain = null;
            if (guildEvent.Creator != null)
            {
                embedBuilder.AddField("👤 Создал", MentionUtils.MentionUser(guildEvent.Creator.Id), true);
                // Telegram: сначала MasterNameMap, потом Username
                if (_serverConfigs.TryGetValue(guild.Id, out var scCr)
                    && scCr.MasterNameMap?.TryGetValue(guildEvent.Creator.Id.ToString(), out var mappedCr) == true
                    && !string.IsNullOrWhiteSpace(mappedCr))
                    createdByPlain = mappedCr;
                else
                    createdByPlain = guildEvent.Creator.Username;
            }

			embedBuilder.WithFooter("Чтобы приходило в личку: /event_notify subscribe • Выкл: напиши «стоп» • Вкл: «хочу»");
			var embed = embedBuilder.Build();

          var announceMsg = await announceChannel.SendMessageAsync(embed: embed);
            try { Console.WriteLine($"[EVENT] announce sent discord_channel guild={guild.Id} event={guildEvent.Id} channel={announceChannel.Id} msg={announceMsg.Id}"); } catch { }

           // Дублируем уведомление в Telegram (если включено в serverconfigs.json для этого сервера)
         int? tgMessageId = null;
            var tgHasPhoto = false;
            try
            {
                if (_telegramNotifier != null)
                {
                 var tgText = $"📅 Новое событие: {guildEvent.Name}\n" +
                        $"🏰 Сервер: {guild.Name}\n" +
                        $"🕒 Когда: {startLocal:dd.MM.yyyy HH:mm}\n" +
                        $"📍 Где: {whereTextPlain}\n" +
                        (createdByPlain != null ? $"👤 Создал: {createdByPlain}\n" : string.Empty) +
                        $"🔗 {eventUrl}";

                    if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                    {
                        var desc = guildEvent.Description.Trim();
                        if (desc.Length > 800) desc = desc.Substring(0, 799) + "…";
                            tgText += $"\n\nОписание события:\n{desc}";
                    }

                  if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        tgMessageId = await _telegramNotifier.SendPhotoReturningMessageIdAsync(guild.Id, imageUrl, tgText);
                        tgHasPhoto = tgMessageId.HasValue;
                    }
                    else
                    {
                        tgMessageId = await _telegramNotifier.SendMessageReturningMessageIdAsync(guild.Id, tgText);
                    }
                    try { Console.WriteLine($"[EVENT] announce sent telegram guild={guild.Id} event={guildEvent.Id} msg={(tgMessageId.HasValue ? tgMessageId.Value : 0)} hasPhoto={tgHasPhoto}"); } catch { }

                }
            }
           catch (Exception ex)
            {
                try { Console.WriteLine($"[EVENT] announce telegram error guild={guild.Id} event={guildEvent.Id}: {ex.Message}"); } catch { }
                   try { await LogError($"[EVENT] announce telegram error guild={guild.Id} event={guildEvent.Id}: {ex}"); } catch { }
            }

                if (_eventAnnouncementStore != null)
                {
                    var entry = _eventAnnouncementStore.TryGet(guild.Id, guildEvent.Id) ?? new EventAnnouncementEntry
                    {
                        GuildId = guild.Id,
                        EventId = guildEvent.Id
                    };
                    entry.AnnounceChannelId = announceChannel.Id;
                    entry.AnnounceMessageId = announceMsg.Id;
                    if (_serverConfigs.TryGetValue(guild.Id, out var sc2))
                    {
                        entry.TelegramChatId = sc2.TelegramChatId;
                        entry.TelegramMessageThreadId = sc2.TelegramMessageThreadId;
                    }
                    if (entry.TelegramMessageId == 0 && tgMessageId.HasValue)
                    {
                        entry.TelegramMessageId = tgMessageId.Value;
                        entry.TelegramHasPhoto = tgHasPhoto;
                    }
                    entry.DmMessageIdsByUserId ??= new Dictionary<ulong, ulong>();
                    _eventAnnouncementStore.Upsert(entry);
                }

			var subscriberIds = _eventNotifications.GetActiveSubscribers(guild.Id);
			if (subscriberIds.Count == 0)
				return;

           var dmMap = new Dictionary<ulong, ulong>();
            foreach (var userId in subscriberIds)
			{
				try
				{
					var user = guild.GetUser(userId) as IUser ?? _client.GetUser(userId);
					if (user == null)
					{
						try { user = await _client.Rest.GetUserAsync(userId); } catch { }
					}

					if (user == null)
						continue;

                 var dm = await user.CreateDMChannelAsync();
                    var dmMsg = await dm.SendMessageAsync(embed: embed);
                    dmMap[userId] = dmMsg.Id;
                   try { Console.WriteLine($"[EVENT] announce sent discord_dm guild={guild.Id} event={guildEvent.Id} user={userId} msg={dmMsg.Id}"); } catch { }
				}
				catch (Exception ex)
				{
                  try { Console.WriteLine($"[EVENT] announce discord_dm error guild={guild.Id} event={guildEvent.Id} user={userId}: {ex.Message}"); } catch { }
					// Логируем сбой доставки в ЛС, но не прерываем рассылку остальным подписчикам
					try
					{
						await LogError($"Не удалось отправить DM о событии пользователю {userId} на сервере {guild.Id}: {ex.Message}");
					}
					catch { }
				}
			}

                if (_eventAnnouncementStore != null)
                {
                    var entry = _eventAnnouncementStore.TryGet(guild.Id, guildEvent.Id) ?? new EventAnnouncementEntry
                    {
                        GuildId = guild.Id,
                        EventId = guildEvent.Id
                    };
                    entry.AnnounceChannelId = announceChannel.Id;
                    entry.AnnounceMessageId = announceMsg.Id;
                    entry.DmMessageIdsByUserId = dmMap;
                    if (_serverConfigs.TryGetValue(guild.Id, out var sc2))
                    {
                        entry.TelegramChatId = sc2.TelegramChatId;
                        entry.TelegramMessageThreadId = sc2.TelegramMessageThreadId;
                    }
                    if (tgMessageId.HasValue)
                    {
                        entry.TelegramMessageId = tgMessageId.Value;
                        entry.TelegramHasPhoto = tgHasPhoto;
                    }
                    _eventAnnouncementStore.Upsert(entry);
                }
		}

        private async Task AnnounceGuildScheduledEventUpdated(Cacheable<SocketGuildEvent, ulong> beforeCache, SocketGuildEvent guildEvent)
        {
            if (guildEvent?.Guild == null)
                return;

            SocketGuildEvent? before = null;
            try { before = await beforeCache.GetOrDownloadAsync(); } catch { }

            var guild = guildEvent.Guild;
            if (_eventAnnouncementStore == null)
                return;

            var entry = _eventAnnouncementStore.TryGet(guild.Id, guildEvent.Id);
            if (entry == null)
                return;

         List<string> changes = new();
            try
            {
                if (before != null)
                {
                    if (!string.Equals(before.Name, guildEvent.Name, StringComparison.Ordinal))
                        changes.Add($"Название: '{before.Name}' → '{guildEvent.Name}'");
                    if (!string.Equals(before.Description ?? string.Empty, guildEvent.Description ?? string.Empty, StringComparison.Ordinal))
                        changes.Add("Описание изменено");
                    if (before.StartTime != guildEvent.StartTime)
                        changes.Add($"Начало: {before.StartTime.ToLocalTime():dd.MM.yyyy HH:mm} → {guildEvent.StartTime.ToLocalTime():dd.MM.yyyy HH:mm}");
                    if (before.EndTime != guildEvent.EndTime)
                    {
                        var bEnd = before.EndTime?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "—";
                        var aEnd = guildEvent.EndTime?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "—";
                        changes.Add($"Окончание: {bEnd} → {aEnd}");
                    }
                    if ((before.Channel?.Id ?? 0) != (guildEvent.Channel?.Id ?? 0) ||
                        !string.Equals(before.Location ?? string.Empty, guildEvent.Location ?? string.Empty, StringComparison.Ordinal))
                        changes.Add("Место проведения изменено");
                    if (!string.Equals(before.GetCoverImageUrl() ?? string.Empty, guildEvent.GetCoverImageUrl() ?? string.Empty, StringComparison.Ordinal))
                        changes.Add("Изображение изменено");
                }
            }
            catch { }

             var updatedMark = $"Обновлено: {DateTime.Now:dd.MM.yyyy HH:mm}";
            var eventUrl = $"https://discord.com/events/{guild.Id}/{guildEvent.Id}";
            var startLocal = guildEvent.StartTime.ToLocalTime();

            static string Truncate(string? value, int max)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return string.Empty;
                value = value.Trim();
                return value.Length <= max ? value : value.Substring(0, max - 1) + "…";
            }

            string whereText;
            string whereTextPlain;
            if (guildEvent.Channel != null)
            {
                whereText = $"<#{guildEvent.Channel.Id}>";
                whereTextPlain = guildEvent.Channel.Name;
            }
            else if (!string.IsNullOrWhiteSpace(guildEvent.Location))
            {
                whereText = Truncate(guildEvent.Location, 256);
                whereTextPlain = whereText;
            }
            else
            {
                whereText = "не указано";
                whereTextPlain = whereText;
            }

            var imageUrl = guildEvent.GetCoverImageUrl();
            var embedBuilder = new EmbedBuilder()
                .WithTitle($"📅 Событие обновлено: {guildEvent.Name}")
                .WithUrl(eventUrl)
                .WithColor(Color.Orange)
                .WithCurrentTimestamp();
            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                embedBuilder.WithThumbnailUrl(imageUrl);
            }
            if (!string.IsNullOrWhiteSpace(guildEvent.Description))
            {
                embedBuilder.WithDescription(Truncate(guildEvent.Description, 2048));
            }
            embedBuilder.AddField("🏰 Сервер", guild.Name, true);
            embedBuilder.AddField("🕒 Когда", startLocal.ToString("dd.MM.yyyy HH:mm"), true);
            embedBuilder.AddField("📍 Где", whereText, true);
            if (guildEvent.Creator != null)
                embedBuilder.AddField("👤 Создал", MentionUtils.MentionUser(guildEvent.Creator.Id), true);
            if (changes.Count > 0)
                embedBuilder.AddField("✏️ Изменения", string.Join("\n", changes.Take(10)), false);
                embedBuilder.AddField("Статус", updatedMark, false);
            var embed = embedBuilder.Build();

            // Update announce message in channel
            try
            {
                if (entry.AnnounceChannelId != 0 && entry.AnnounceMessageId != 0)
                {
                    var ch = await _client.GetChannelAsync(entry.AnnounceChannelId) as ITextChannel;
                    if (ch != null)
                    {
                        var msg = await ch.GetMessageAsync(entry.AnnounceMessageId) as IUserMessage;
                        if (msg != null)
                        {
                            await msg.ModifyAsync(m => m.Embed = embed);
                            try { Console.WriteLine($"[EVENT] update discord_channel ok guild={guild.Id} event={guildEvent.Id} channel={entry.AnnounceChannelId} msg={entry.AnnounceMessageId}"); } catch { }
                        }
                    }
                }
            }
           catch (Exception ex)
            {
                try { Console.WriteLine($"[EVENT] update discord_channel error guild={guild.Id} event={guildEvent.Id} msg={entry.AnnounceMessageId}: {ex.Message}"); } catch { }
            }

            // Update DMs to current active subscribers only
            var subscriberIds = _eventNotifications.GetActiveSubscribers(guild.Id);
            foreach (var userId in subscriberIds)
            {
                try
                {
                    if (!entry.DmMessageIdsByUserId.TryGetValue(userId, out var dmMessageId) || dmMessageId == 0)
                        continue;
                    var user = guild.GetUser(userId) as IUser ?? _client.GetUser(userId);
                    if (user == null)
                    {
                        try { user = await _client.Rest.GetUserAsync(userId); } catch { }
                    }
                    if (user == null)
                        continue;
                    var dm = await user.CreateDMChannelAsync();
                    var dmMsg = await dm.GetMessageAsync(dmMessageId) as IUserMessage;
                  if (dmMsg != null)
                    {
                        await dmMsg.ModifyAsync(m => m.Embed = embed);
                        try { Console.WriteLine($"[EVENT] update discord_dm ok guild={guild.Id} event={guildEvent.Id} user={userId} msg={dmMessageId}"); } catch { }
                    }
                }
               catch (Exception ex)
                {
                    try { Console.WriteLine($"[EVENT] update discord_dm error guild={guild.Id} event={guildEvent.Id} user={userId}: {ex.Message}"); } catch { }
                       try { await LogError($"[EVENT] update discord_dm error guild={guild.Id} event={guildEvent.Id} user={userId}: {ex}"); } catch { }
                }
            }

            // Update Telegram message
            try
            {
                if (_telegramNotifier != null && entry.TelegramMessageId > 0)
                {
                 var tgText = $"📅 Событие обновлено: {guildEvent.Name}\n" +
                        $"🏰 Сервер: {guild.Name}\n" +
                        $"🕒 Когда: {startLocal:dd.MM.yyyy HH:mm}\n" +
                        $"📍 Где: {whereTextPlain}\n" +
                        (guildEvent.Creator != null ? $"👤 Создал: {guildEvent.Creator.Username}\n" : string.Empty) +
                     (changes.Count > 0 ? $"\n✏️ Изменения:\n- {string.Join("\n- ", changes.Take(10))}\n" : string.Empty) +
                        $"ℹ️ {updatedMark}\n" +
                        $"🔗 {eventUrl}";

                    if (!string.IsNullOrWhiteSpace(guildEvent.Description))
                    {
                        var desc = guildEvent.Description.Trim();
                        if (desc.Length > 800) desc = desc.Substring(0, 799) + "…";
                        tgText += $"\n\nОписание события:\n{desc}";
                    }

                 var ok = entry.TelegramHasPhoto
                        ? await _telegramNotifier.EditMessageCaptionAsync(guild.Id, entry.TelegramMessageId, tgText)
                        : await _telegramNotifier.EditMessageTextAsync(guild.Id, entry.TelegramMessageId, tgText);

                    try { Console.WriteLine($"[EVENT] update telegram {(ok ? "ok" : "fail")} guild={guild.Id} event={guildEvent.Id} msg={entry.TelegramMessageId} hasPhoto={entry.TelegramHasPhoto}"); } catch { }
                }
            }
           catch (Exception ex)
            {
                try { Console.WriteLine($"[EVENT] update telegram error guild={guild.Id} event={guildEvent.Id} msg={entry.TelegramMessageId}: {ex.Message}"); } catch { }
                   try { await LogError($"[EVENT] update telegram error guild={guild.Id} event={guildEvent.Id} msg={entry.TelegramMessageId}: {ex}"); } catch { }
            }
        }

        private async Task WaitForReadyAsync()
        {
            var readyTcs = new TaskCompletionSource<bool>();

            Task OnReadyOnce()
            {
                readyTcs.TrySetResult(true);
                return Task.CompletedTask;
            }

            _client.Ready += OnReadyOnce;

            try
            {
                if (_client.CurrentUser != null)
                {
                    readyTcs.TrySetResult(true);
                }

                var completedTask = await Task.WhenAny(readyTcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                if (completedTask == readyTcs.Task)
                    return;

                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && !_shouldExit)
                {
                    if (_client.CurrentUser != null)
                        return;

                    await Task.Delay(250);
                }
            }
            finally
            {
                _client.Ready -= OnReadyOnce;
            }
        }

        private async Task BackgroundMonitoringLoop(CancellationToken ct = default)
        {
            while (!_shouldExit && !ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await LogStartup($"⚠️ BackgroundMonitoring: delay error: {ex.Message}");
                    await Task.Delay(500);
                    continue;
                }

                var predictor = _connectionPredictor;
                var client = _client;
                var recon = _reconnectionService;

                if (predictor != null)
                {
                    try
                    {
                        await predictor.AnalyzeAndPredict();
                    }
                    catch (Exception ex)
                    {
                        await LogStartup($"⚠️ BackgroundMonitoring: prediction error: {ex.Message}");
                    }
                }
                else
                {
                    await LogStartup("⚠️ BackgroundMonitoring: predictor is null, skipping prediction");
                }

                if (client != null)
                {
                    try
                    {
                        if (recon != null && recon.ShouldPauseBackgroundDisconnectChecks)
                        {
                            continue;
                        }

                        if (client.ConnectionState == ConnectionState.Disconnected && !_shouldExit)
                        {
                            await LogStartup("⚠️ Фоновая проверка: обнаружено отключение");

                            if (recon != null)
                            {
                                try
                                {
                                    await recon.HandleDisconnect(new BackgroundDisconnectException());
                                }
                                catch (Exception ex)
                                {
                                    await LogStartup($"⚠️ BackgroundMonitoring: recon.HandleDisconnect failed: {ex.Message}");
                                }
                            }
                            else
                            {
                                await LogStartup("⚠️ BackgroundMonitoring: reconnection service is null, cannot handle disconnect");
                            }
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        await LogStartup("⚠️ BackgroundMonitoring: encountered disposed object while checking connection");
                    }
                    catch (Exception ex)
                    {
                        await LogStartup($"⚠️ BackgroundMonitoring: error checking connection: {ex.Message}");
                    }
                }
                else
                {
                    await LogStartup("⚠️ BackgroundMonitoring: client is null, skipping connection check");
                }
            }
        }

        private async Task BackgroundMonitoringLoopWrapper(CancellationToken ct)
        {
            try
            {
                await BackgroundMonitoringLoop(ct);
            }
            catch (Exception ex)
            {
                await LogStartup($"⚠️ BackgroundMonitoringLoop failed: {ex}");
            }
        }

        private async Task OnDisconnectDetected(Exception exception)
        {
            var reason = _reconnectionService.ConnectionInfo.LastDisconnectReason;

            if (exception is not GatewayReconnectException)
            {
                await LogStartup($"Отключение: {reason}");

                await _statusNotifier.SendConnectionIssue(
                    reason,
                    _reconnectionService.ConnectionInfo.ReconnectAttempts + 1
                );
            }
        }

        private async Task OnReconnectStarted(string message)
        {
            await LogStartup(message);
        }

        private async Task OnReconnectCompleted(bool success)
        {
            if (success)
            {
                var info = _reconnectionService.ConnectionInfo;

                // Отправляем уведомление об успешном реконнекте
                try
                {
                    await _statusNotifier.SendReconnectSuccess(
                        info.ReconnectAttempts,
                        info.LastDisconnectReason
                    );
                }
                catch (Exception ex)
                {
                    await LogStartup($"Ошибка при отправке уведомления о переподключении: {ex.Message}");
                }

                // Попытка уведомить все сервера о восстановлении; логируем результат
                try
                {
                    var reconnectReason = $"Переподключение после: {info.LastDisconnectReason}";
                    _currentStartupType = StartupType.Reconnect;
                    _startupReason = reconnectReason;
                    _statusNotifier?.SetStartupContext(StartupType.Reconnect, reconnectReason);

                    var ok = await _statusNotifier.SendAllSystemsActive($"Переподключение после: {info.LastDisconnectReason}");
                    if (!ok)
                        await LogStartup("SendAllSystemsActive завершился с ошибками. Смотрите подробности в логах.");
                }
                catch (Exception ex)
                {
                    await LogStartup($"Ошибка при массовой отправке статусов после реконнекта: {ex.Message}");
                }

                // Восстанавливаем музыкальные очереди после переподключения
                if (_musicCommands is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10)); // ждём стабилизации Lavalink
                        await _musicCommands.TryRestoreQueuesAsync();
                    });
                }
            }
        }

        /// <summary>
        /// Обработчик запроса полного перезапуска от ReconnectionService
        /// </summary>
        private async Task OnFullRestartRequested()
        {
            try
            {
                await LogStartup("Авто-перезапуск: превышено число попыток реконнекта, инициируем полный перезапуск клиента...");
				await RestartWithReasonAsync(
					initiator: "discord",
					reason: "Авто-перезапуск из-за множества попыток переподключения");
            }
            catch (Exception ex)
            {
                await LogStartup($"Ошибка при обработке OnFullRestartRequested: {ex.Message}");
            }
        }

        private async Task OnPredictionMade(ConnectionPredictor.PredictionResult prediction)
        {
            await LogStartup($"Прогноз: {prediction.Reason} в {prediction.PredictedTime:HH:mm:ss}");
            await SendPredictionMessage(prediction);
        }

		private async Task SendPredictionMessage(ConnectionPredictor.PredictionResult prediction)
		{
			try
			{
				// Глобальная проверка: если в конфиге отключены прогнозы ОТКЛЮЧЕНИЙ соединения — не отправляем сообщения
				if (_config?.Prediction != null && !_config.Prediction.EnableConnectionPredictions)
					return;

				foreach (var guild in _client.Guilds)
				{
					if (!_serverConfigs.TryGetValue(guild.Id, out var config))
						continue;
					if (config.ModerateChannelID == 0)
						continue;

					var channel = await _client.GetChannelAsync(config.ModerateChannelID) as ITextChannel;
					if (channel != null)
					{
						var embed = StatusMessageBuilder.BuildPredictionEmbed(prediction);
						await channel.SendMessageAsync(embed: embed);
					}
				}
            }
            catch (Exception ex)
            {
                await LogStartup($"Ошибка отправки прогноза: {ex.Message}");
            }
        }

        private string GetBotToken()
        {
            // Сначала пробуем переменную окружения (безопаснее для деплоя)
            var env = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();

            // Затем конфиг
            if (!string.IsNullOrWhiteSpace(_config?.BotToken))
                return _config.BotToken.Trim();

            // Если токен не найден — бросаем, чтобы не пытаться залогиниться пустым токеном
			var cfgPath = BotConfig.ResolvePath(Path.Combine("Settings", "config.json"));
			throw new InvalidOperationException($"Discord bot token not provided. Set DISCORD_BOT_TOKEN env or BotToken in '{cfgPath}'.");
        }

        private bool _readyCompleted = false;
        private bool _initializationStarted = false;
        private bool _initializationCompleted = false;
        private DateTime _readyTime = DateTime.MinValue; // Инициализируем MinValue
        private DateTime _fullReadyTime;

        private async Task OnReady()
        {
            _readyTime = DateTime.UtcNow;
            _readyCompleted = true;

            try { await LogInfo($"Ready: connected as {_client.CurrentUser?.Username}"); } catch { }

            // ОТПРАВЛЯЕМ В UI
            _ui?.AddLog($"БОТ ПОДКЛЮЧЕН К DISCORD: {_client.CurrentUser.Username} в {DateTime.Now:HH:mm:ss}");

// ✅ НОВОЕ: Загружаем сохранённые сессии игр
_ = Task.Run(() => GameSessionCommands.LoadSessionsAsync(_client));

await Task.CompletedTask;
        }

        private void EnsureServerConfigsForConnectedGuilds()
        {
            var changed = false;
            foreach (var g in _client.Guilds)
            {
                if (!_serverConfigs.ContainsKey(g.Id))
                {
                    _serverConfigs[g.Id] = new ServerConfig { GuildID = g.Id };
                    changed = true;
                }
                // Существующие конфиги не трогаем — только добавляем отсутствующие серверы.
            }

            if (changed)
            {
                SaveServerConfigs();
            }
            else if (!File.Exists(BotConfig.ResolvePath(_serverConfigsPath ?? "serverconfigs.json")))
            {
                // Файл исчез, но данные в памяти есть — восстанавливаем.
                SaveServerConfigs();
            }
        }

        // Фиксированная ширина логов для стабильного форматирования
        // 118 символов контента + 2 рамки = 120 символов итого
        // При консоли 160x45 и Logs Panel 70% (112 символов) логи прокручиваются горизонтально
        // См. Docs/UI_Layout_Sizes.md для деталей
        private const int StartupBoxContentWidth = 118;

        private static string BuildStartupBoxTop(string title)
        {
            var normalized = NormalizeStartupBoxLine(title);
            return $"┌{normalized.PadRight(StartupBoxContentWidth, '─')}┐";
        }

        private static string BuildStartupBoxBottom() => $"└{new string('─', StartupBoxContentWidth)}┘";

        private static string BuildStartupBoxLine(string text)
        {
            return $"│{text.PadRight(StartupBoxContentWidth)}│";
        }

        private static string NormalizeStartupBoxLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            return text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static IEnumerable<string> WrapStartupBoxContent(string? text)
        {
            var normalized = NormalizeStartupBoxLine(text);
            if (string.IsNullOrEmpty(normalized))
            {
                yield return string.Empty;
                yield break;
            }

            // Если строка влезает - возвращаем как есть
            if (normalized.Length <= StartupBoxContentWidth)
            {
                yield return normalized;
                yield break;
            }

            // Перенос длинных строк с сохранением слов
            var remaining = normalized;
            var firstLine = true;

            while (remaining.Length > 0)
            {
                var maxLen = firstLine ? StartupBoxContentWidth : StartupBoxContentWidth - 2; // отступ для переноса

                if (remaining.Length <= maxLen)
                {
                    // Последняя часть
                    yield return firstLine ? remaining : "  " + remaining;
                    break;
                }

                // Ищем позицию для разрыва по пробелу
                var breakPos = maxLen;
                var lastSpace = remaining.LastIndexOf(' ', maxLen - 1, maxLen);

                if (lastSpace > maxLen / 2) // Если пробел найден не слишком близко к началу
                    breakPos = lastSpace;

                var chunk = remaining.Substring(0, breakPos).TrimEnd();
                yield return firstLine ? chunk : "  " + chunk;

                remaining = remaining.Substring(breakPos).TrimStart();
                firstLine = false;
            }
        }

        private static List<string> BuildStartupBox(string title, IEnumerable<string> lines)
        {
            var result = new List<string> { BuildStartupBoxTop(title) };
            foreach (var line in lines)
            {
                // Если строка уже содержит рамки (вложенный блок), добавляем как есть
                if (line.TrimStart().StartsWith("┌") || line.TrimStart().StartsWith("└") || line.TrimStart().StartsWith("│"))
                {
                    result.Add(BuildStartupBoxLine(line));
                }
                else
                {
                    // Обычная строка - оборачиваем
                    foreach (var wrapped in WrapStartupBoxContent(line))
                    {
                        result.Add(BuildStartupBoxLine(wrapped));
                    }
                }
            }
            result.Add(BuildStartupBoxBottom());
            return result;
        }

        private async Task LogStartupBoxAsync(string title, IEnumerable<string> lines)
        {
            await LogStartupBatch(BuildStartupBox(title, lines));
        }

        private async Task LogStartupBatch(IEnumerable<string> messages)
        {
            var lines = messages is IList<string> l ? l : messages.ToList();
            if (lines.Count == 0) return;

            var logDirRaw = _config?.LogDirectory;
            var logDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDirRaw) ? "Logs" : logDirRaw);
            Directory.CreateDirectory(logDir);
            var dateSuffix = DateTime.Now.ToString("yyyyMMdd");
            var path = Path.Combine(logDir, $"StartupLog_{dateSuffix}.txt");
            var timestamp = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");

            await _logSemaphore.WaitAsync();
            try
            {
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    if (fi.Length > 5 * 1024 * 1024)
                        File.Move(path, Path.Combine(logDir, $"StartupLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt"));
                }

                var sb = new StringBuilder();
                foreach (var msg in lines)
                    sb.Append($"[{timestamp}] {msg}\n");
                await File.AppendAllTextAsync(path, sb.ToString());

                if (_uiStarted && _ui != null)
                {
                    foreach (var msg in lines)
                        _ui.AddLog(msg);
                }
            }
            finally
            {
                _logSemaphore.Release();
            }
        }

        /// <summary>
        /// Проверяет config.json на наличие новых полей и дописывает недостающие строки.
        /// Вызывается и при первом запуске, и при перезапуске.
        /// </summary>
        private async Task EnsureConfigFieldsAsync()
        {
            try
            {
                var configPath = BotConfig.ResolvePath(Path.Combine(BotConfig.SettingsFolderName, "config.json"));
                if (!File.Exists(configPath)) return;

                var json = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
                var needsSave = false;
                var addedFields = new List<string>();

                // Поля MusicConfig, добавленные позже — проверяем наличие в JSON
                if (!json.Contains("\"YtCipherAutoStart\"", StringComparison.Ordinal))
                {
                    addedFields.Add("Music.YtCipherAutoStart = false");
                    needsSave = true;
                }
                if (!json.Contains("\"YtCipherPath\"", StringComparison.Ordinal))
                {
                    addedFields.Add("Music.YtCipherPath = \"yt-cipher\"");
                    needsSave = true;
                }
                if (!json.Contains("\"YtCipherPort\"", StringComparison.Ordinal))
                {
                    addedFields.Add("Music.YtCipherPort = 8001");
                    needsSave = true;
                }

                // Нормализация путей: если JarPath / YtCipherPath / ConfigPath абсолютные —
                // заменяем на относительные к AppContext.BaseDirectory.
                // Это случается когда пути были заданы вручную или сохранены на старой машине.
                if (needsSave || _config?.Music is not null)
                {
                    var cfg = BotConfig.Load(Path.Combine(BotConfig.SettingsFolderName, "config.json"));
                    var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    var music = cfg.Music;
                    bool pathsFixed = false;

                    string Relativize(string path)
                    {
                        if (string.IsNullOrWhiteSpace(path)) return path;
                        if (!Path.IsPathRooted(path)) return path;
                        if (path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                        {
                            var rel = path.Substring(baseDir.Length).Replace('\\', '/');
                            return rel;
                        }
                        return path;
                    }

                    var relJar = Relativize(music.JarPath);
                    if (relJar != music.JarPath) { music.JarPath = relJar; pathsFixed = true; addedFields.Add($"Music.JarPath → {relJar}"); }

                    var relYtCipher = Relativize(music.YtCipherPath);
                    if (relYtCipher != music.YtCipherPath) { music.YtCipherPath = relYtCipher; pathsFixed = true; addedFields.Add($"Music.YtCipherPath → {relYtCipher}"); }

                    var relConfig = Relativize(music.ConfigPath);
                    if (relConfig != music.ConfigPath) { music.ConfigPath = relConfig; pathsFixed = true; addedFields.Add($"Music.ConfigPath → {relConfig}"); }

                    if (needsSave || pathsFixed)
                    {
                        cfg.Save(Path.Combine(BotConfig.SettingsFolderName, "config.json"));
                        await LogStartup($"[CONFIG] Обновлён config.json: {string.Join(", ", addedFields)}");
                    }
                    return;
                }

                if (needsSave)
                {
                    var freshConfig = BotConfig.Load(Path.Combine(BotConfig.SettingsFolderName, "config.json"));
                    freshConfig.Save(Path.Combine(BotConfig.SettingsFolderName, "config.json"));
                    await LogStartup($"[CONFIG] Добавлены новые поля в config.json: {string.Join(", ", addedFields)}");
                }
            }
            catch (Exception ex)
            {
                await LogStartup($"[CONFIG] Ошибка проверки полей конфига: {ex.Message}");
            }
        }

        private async Task BootstrapFirstRunSettingsAsync()
        {
            if (_currentStartupType != StartupType.FirstStart)
                return;

            void Write(string message)
            {
                try { Console.WriteLine(message); } catch { }
            }

            try
            {
                var settingsDir = BotConfig.GetSettingsDirectory();
                Directory.CreateDirectory(settingsDir);

                var configPath = BotConfig.ResolvePath(Path.Combine(BotConfig.SettingsFolderName, "config.json"));

                // Проверяем и дополняем config.json новыми полями
                await EnsureConfigFieldsAsync().ConfigureAwait(false);

                var serverConfigsCreated = false;
                var serverConfigsExisted = File.Exists(_serverConfigsPath);
                EnsureServerConfigsForConnectedGuilds();
                serverConfigsCreated = !serverConfigsExisted && File.Exists(_serverConfigsPath);

                var pointsCreated = false;
                if (!File.Exists(BotConfig.ResolvePath(Path.Combine(BotConfig.SettingsFolderName, "points.json"))))
                {
                    await _pointsService.SaveAsync().ConfigureAwait(false);
                    pointsCreated = File.Exists(BotConfig.ResolvePath(Path.Combine(BotConfig.SettingsFolderName, "points.json")));
                }

                var pointsUsersPath = BotConfig.ResolvePath(Path.Combine(BotConfig.SettingsFolderName, "points_users.json"));
                var pointsUsersCreated = false;
                if (!File.Exists(pointsUsersPath))
                {
                    await _pointsUserIndex.SaveAsync().ConfigureAwait(false);
                    pointsUsersCreated = File.Exists(pointsUsersPath);
                }

                var backfilledAnyNames = false;
                var backfillLine = string.Empty;
                try
                {
                    var balancesByGuild = _pointsService.GetSnapshot();
                    foreach (var guild in _client.Guilds)
                    {
                        if (!balancesByGuild.TryGetValue(guild.Id, out var guildBalances))
                            continue;

                        foreach (var userId in guildBalances.Keys)
                        {
                            if (!string.IsNullOrWhiteSpace(_pointsUserIndex.GetName(guild.Id, userId)))
                                continue;

                            IUser? u = guild.GetUser(userId) as IUser;
                            if (u == null)
                            {
                                try { u = await _client.Rest.GetUserAsync(userId).ConfigureAwait(false); } catch { }
                            }

                            if (u != null)
                            {
                                _pointsUserIndex.UpsertFromUser(guild.Id, u);
                                backfilledAnyNames = true;
                            }
                        }
                    }

                    if (backfilledAnyNames)
                        await _pointsUserIndex.SaveAsync().ConfigureAwait(false);

                    backfillLine = backfilledAnyNames
                        ? "points_users.json backfilled from points.json"
                        : "points_users.json backfill not needed";
                }
                catch (Exception ex)
                {
                    backfillLine = $"points_users.json backfill skipped: {ex.Message}";
                }

                var annCreated = _eventAnnouncementStore?.EnsureFileExists() == true;

                var notifCreated = _eventNotifications?.EnsureFileExists() == true;

                var predCreated = _predictionService != null && await _predictionService.EnsureStateFileAsync().ConfigureAwait(false);

                var lines = new List<string>
                {
                    $"Settings directory: {settingsDir}",
                    File.Exists(configPath) ? "config.json present (fields verified)" : "config.json created by BotConfig.Load",
                    serverConfigsCreated ? "serverconfigs.json created and seeded for connected guilds" : "serverconfigs.json already exists or was updated",
                    pointsCreated ? "points.json created" : "points.json already exists",
                    pointsUsersCreated ? "points_users.json created" : "points_users.json already exists",
                    backfillLine,
                    annCreated ? "event_announcements.json created" : "event_announcements.json already exists",
                    notifCreated ? "event-notify.json created" : "event-notify.json already exists",
                    predCreated ? "predictions_state.json created" : "predictions_state.json already exists",
                    "Telegram startup probe: begin"
                };

                foreach (var guild in _client.Guilds)
                {
                    if (!_serverConfigs.TryGetValue(guild.Id, out var sc) || !sc.TelegramEnabled)
                    {
                        lines.Add($"Telegram startup probe skipped for {guild.Name}: disabled or missing serverconfig");
                        continue;
                    }

                    var probe = await _telegramNotifier.ProbeAsync(guild.Id).ConfigureAwait(false);
                    lines.Add($"Telegram startup probe for {guild.Name}: {(probe.Success ? "OK" : "FAIL")} - {probe.Message}");
                }

                foreach (var line in BuildStartupBox("ЭТАП 0/5: ПЕРВИЧНАЯ ИНИЦИАЛИЗАЦИЯ SETTINGS", lines))
                {
                    Write(line);
                }
            }
            catch (Exception ex)
            {
                try { Console.WriteLine($"[SETTINGS-BOOTSTRAP] error: {ex}"); } catch { }
            }
        }

        private async Task InitializeBotWithProgress()
        {
            try
            {
               if (_currentStartupType == StartupType.FirstStart)
                {
                    await BootstrapFirstRunSettingsAsync().ConfigureAwait(false);
                }
                else
                {
                    // При перезапуске тоже проверяем/дополняем конфиг новыми полями
                    await EnsureConfigFieldsAsync().ConfigureAwait(false);
                }

				// ЭТАП 1: Регистрация команд
				var isDailyRestart =
					_currentStartupType == StartupType.Restart &&
					string.Equals(_startupReason, "Ежедневная перезагрузка", StringComparison.OrdinalIgnoreCase);

                var stage1Lines = new List<string>();

				if (isDailyRestart)
				{
					// После плановой ежедневной перезагрузки не спрашиваем про переинициализацию команд
                 stage1Lines.Add("Регистрация команд пропущена (ежедневная перезагрузка).");
					await _commandHandler.ListSlashCommandsAsync();
				}
				else
				{
					if (await _ui.AskYesNoQuestion(
							"Нужно ли перерегистрировать команды?",
							"Y - Да, N - Нет, таймаут 60 секунд",
							60
						) == true)
					{
						await _commandHandler.InitializeAsync();
						await _commandHandler.ListSlashCommandsAsync();
                        stage1Lines.Add("Команды зарегистрированы.");
					}
					else
					{
                       stage1Lines.Add("Регистрация команд пропущена.");
						await _commandHandler.ListSlashCommandsAsync();
					}
				}
                if (stage1Lines.Count == 0)
                    stage1Lines.Add("Регистрация команд завершена.");
                await LogStartupBoxAsync("ЭТАП 1/5: РЕГИСТРАЦИЯ КОМАНД", stage1Lines);

                // ЭТАП 2: Активация обработчиков
                await SetupDiscordEvents();
                await LogStartupBoxAsync("ЭТАП 2/5: АКТИВАЦИЯ ОБРАБОТЧИКОВ", new[]
                {
                    "Подписки на события Discord обновлены и активированы."
                });

                // ЭТАП 3: Синхронизация (эвенты/прогнозы)
                await ResyncEventAnnouncementsOnStartupAsync();
                var stage3Lines = new List<string>();
                try
                {
                    static string Trunc(string? s, int max)
                    {
                        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                        s = s.Trim();
                        return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
                    }

                    // Best-effort log: PredictionService restores state on start; here we log a snapshot.
                    var anyPred = false;
                    foreach (var g in _client.Guilds)
                    {
                        var ap = _predictionService?.GetActive(g.Id);
                        if (ap == null || ap.IsResolved)
                            continue;

                        anyPred = true;
                        var lockText = ap.IsLocked ? "LOCK" : "OPEN";
                        var closes = ap.BetsCloseAtUtc.ToLocalTime();
                        stage3Lines.Add($"[PRED] {lockText} | {g.Name} | ставок: {ap.Bets.Count} | пул: {ap.TotalPool}");
                        stage3Lines.Add($"title: {Trunc(ap.Title, 60)}");
                        stage3Lines.Add($"closes: {closes:dd.MM HH:mm:ss} | o1={ap.Outcome1.TotalStake} | o2={ap.Outcome2.TotalStake}");

                        // Print up to N bets to keep startup log compact.
                        var betLines = ap.Bets.Values
                            .OrderByDescending(b => b.Amount)
                            .Take(6)
                            .Select(b => $"{b.UserId}:{b.Amount}#{b.OutcomeId}")
                            .ToList();

                        if (betLines.Count == 0)
                        {
                            stage3Lines.Add("bets: (нет ставок)");
                        }
                        else
                        {
                            var joined = string.Join(" | ", betLines);
                            stage3Lines.Add($"bets: {Trunc(joined, 60)}");
                        }
                    }

                    if (!anyPred)
                    {
                        stage3Lines.Add("[PRED] активных прогнозов не найдено");
                    }
                }
                catch { }
                if (stage3Lines.Count == 0)
                    stage3Lines.Add("Синхронизация завершена без дополнительных данных.");
                await LogStartupBoxAsync("ЭТАП 3/5: СИНХРОНИЗАЦИЯ", stage3Lines);

                // ЭТАП 4: ИНИЦИАЛИЗАЦИЯ МУЗЫКИ
                if (_config.Music.Enabled && _lavalinkService is not null)
                {
                    var musicLines = new List<string>();
                    // Перехватываем весь вывод LavalinkService в буфер,
                    // чтобы он отобразился внутри бокса этапа, а не до него.
                    _lavalinkService.StartupLogBuffer = musicLines;
                    try
                    {
                        var lavalinkReady = await _lavalinkService.LaunchProcessAsync();

                        if (!lavalinkReady)
                        {
                            // WaitUntilReadyAsync исчерпал таймаут — даём ещё до 15 секунд
                            // (Lavalink может ещё грузить JVM или плагины)
                            const int extraRetries = 15;
                            const int retryDelayMs = 1000;
                            musicLines.Add($"⏳ Lavalink не ответил за основной таймаут, ждём ещё до {extraRetries}с...");

                            for (int i = 0; i < extraRetries; i++)
                            {
                                await Task.Delay(retryDelayMs);
                                var err = await _lavalinkService.ProbeAsync();
                                if (err is null)
                                {
                                    lavalinkReady = true;
                                    musicLines.Add($"✅ Lavalink поднялся на попытке {i + 1} — готов ({_config.Music.Host}:{_config.Music.Port})");
                                    break;
                                }
                            }

                            if (!lavalinkReady)
                            {
                                var finalErr = await _lavalinkService.ProbeAsync();
                                musicLines.Add(finalErr is null
                                    ? $"✅ Lavalink готов ({_config.Music.Host}:{_config.Music.Port})"
                                    : $"⚠️ Lavalink так и не ответил: {finalErr}");
                            }
                        }
                        else
                        {
                            musicLines.Add($"✅ yt-cipher и Lavalink запущены и отвечают ({_config.Music.Host}:{_config.Music.Port})");
                        }
                    }
                    catch (Exception ex)
                    {
                        musicLines.Add($"❌ Ошибка запуска музыкального стека: {ex.Message}");
                    }
                    finally
                    {
                        // Снимаем буфер — дальнейшие логи идут обратно в обычный sink
                        _lavalinkService.StartupLogBuffer = null;
                    }
                    await LogStartupBoxAsync("ЭТАП 4/5: ИНИЦИАЛИЗАЦИЯ МУЗЫКИ", musicLines);
                }

                // ЭТАП 5: ПРОВЕРКА СИСТЕМ И ОТПРАВКА СТАТУСОВ
                var stage4Lines = new List<string>();

                // Выполняем проверку здоровья систем
                var healthChecks = await PerformSystemHealthCheckAsync();

                stage4Lines.Add("═══ ПРОВЕРКА СИСТЕМ ═══");
                foreach (var check in healthChecks)
                {
                    var icon = check.IsHealthy ? "✅" : "❌";
                    var firstLine = check.Message.Split('\n')[0];
                    var systemNamePart = $"{icon} {check.SystemName}: ";

                    // Не обрезаем - пусть WrapStartupBoxContent сам переносит
                    stage4Lines.Add($"{systemNamePart}{firstLine}");

                    // Если сообщение многострочное (Telegram), добавляем только первые 2 детальные строки
                    if (check.Message.Contains("\n"))
                    {
                        var lines = check.Message.Split('\n').Skip(1).Take(2);
                        foreach (var line in lines)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                // Добавляем с отступом, WrapStartupBoxContent обработает
                                stage4Lines.Add($"  {line.Trim()}");
                            }
                        }
                    }
                }

                // Определяем, все ли системы здоровы
                var allHealthy = healthChecks.All(c => c.IsHealthy);
                var overallStatus = allHealthy ? "✅ ВСЕ СИСТЕМЫ РАБОТАЮТ" : "⚠️ ОБНАРУЖЕНЫ ПРОБЛЕМЫ";

                stage4Lines.Add("");
                stage4Lines.Add($"═══ ИТОГ: {overallStatus} ═══");
                stage4Lines.Add("");

                // Отправляем статусы на серверы
                stage4Lines.Add("═══ ОТПРАВКА СТАТУСОВ ═══");
                var guildsList = _client.Guilds.ToList();
                for (int i = 0; i < guildsList.Count; i++)
                {
                    var guild = guildsList[i];
                    if (_serverConfigs.TryGetValue(guild.Id, out var config))
                    {
                        if (config.ModerateChannelID == 0)
                        {
                            stage4Lines.Add($"⊘ {guild.Name}: канал не настроен");
                        }
                        else
                        {
                            // Передаём результаты проверки в StatusNotifier
                            var ok = await _statusNotifier.SendSystemsActiveToGuild(guild, config,
                                $"Тип запуска: {GetStartupTypeDisplay()}",
                                healthChecks);

                            if (ok)
                                stage4Lines.Add($"✅ {guild.Name}");
                            else
                                stage4Lines.Add($"❌ {guild.Name}: ошибка отправки");
                        }
                    }
                    await Task.Delay(200);
                }

                if (stage4Lines.Count == 0)
                    stage4Lines.Add("Нет серверов для отправки стартовых уведомлений.");

                await LogStartupBoxAsync("ЭТАП 5/5: ПРОВЕРКА И ОТПРАВКА СТАТУСОВ", stage4Lines);

                // ФИНАЛ
                _fullReadyTime = DateTime.UtcNow;
                // Защита: если событие Ready не сработало и _readyTime остался MinValue,
                // используем время старта инициализации как начало, чтобы не получить отрицательное время.
                var startTime = _readyTime == DateTime.MinValue ? _startupTime : _readyTime;
                var initTime = (_fullReadyTime - startTime).TotalSeconds;
                _initializationCompleted = true;

                _ui?.EnableInput();

                var botName = _client.CurrentUser?.Username;
                if (string.IsNullOrWhiteSpace(botName))
                {
                    await LogStartup("⚠️ UI startup: имя бота недоступно после инициализации клиента.");
                    botName = "Discord Bot";
                }

                _ui?.ShowSystemReady(
                    botName,
                    _client.Guilds.Count,
                    initTime
                );

                // If we performed a restart, surface recent ErrorLog lines and notify user in UI
                try
                {
                    if (_currentStartupType == StartupType.Restart)
                    {
                        _ui?.NotifyRestartCompleted(_restartInitiator);
                    }
                }
                catch { }

                // LogStartup теперь отправляет в UI
            }
            catch (Exception ex)
            {
                await LogStartup($"❌ КРИТИЧЕСКАЯ ОШИБКА ИНИЦИАЛИЗАЦИИ: {ex.Message}");
                await LogStartup($"   Стек: {ex.StackTrace}");
            }
        }

        private async Task OnDisconnected(Exception exception)
        {
            if (_reconnectionService == null)
            {
                await LogStartup("Предупреждение: _reconnectionService == null в OnDisconnected — пропускаем обработку отключения.");
                return;
            }

            try
            {
                await _reconnectionService.HandleDisconnect(exception);
            }
            catch (Exception ex)
            {
                await LogStartup($"Ошибка в OnDisconnected при вызове HandleDisconnect: {ex.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposed = true;

			_shouldExit = true;
			StopDailyRestartScheduler();
			try { _backgroundMonitoringCts?.Cancel(); } catch { }
			try { _backgroundMonitoringCts?.Dispose(); _backgroundMonitoringCts = null; } catch { }

            try
            {
                // Останавливаем реконнект-сервис корректно и затем очищаем
                try { _reconnectionService?.Shutdown(); } catch (Exception ex) { Console.WriteLine($"Error shutting reconnection service: {ex}"); }
                CleanupServices();

                // Остановим UI корректно
                try
                {
                    _ui?.Dispose();
                    _ui = null;
                    _uiStarted = false;
                }
                catch (Exception ex) { Console.WriteLine($"Error disposing UI: {ex}"); }

                // Останавливаем клиента
                try
                {
                    if (_client != null)
                    {
                        try { _client.Ready -= OnReady; } catch (Exception ex) { Console.WriteLine($"Error unsubscribing Ready: {ex}"); }
                        try { _client.Disconnected -= OnDisconnected; } catch (Exception ex) { Console.WriteLine($"Error unsubscribing Disconnected: {ex}"); }
                        try { _client.UserJoined -= UserJoined; } catch (Exception ex) { Console.WriteLine($"Error unsubscribing UserJoined: {ex}"); }
                        try { _client.MessageReceived -= HandleCommandAsync; } catch (Exception ex) { Console.WriteLine($"Error unsubscribing MessageReceived: {ex}"); }
                        try { _client.SlashCommandExecuted -= OnSlashCommandExecuted; } catch (Exception ex) { Console.WriteLine($"Error unsubscribing SlashCommandExecuted: {ex}"); }
                        try { _client.SlashCommandExecuted -= BwonkCommand; } catch (Exception ex) { Console.WriteLine($"Error unsubscribing BwonkCommand: {ex}"); }
                        try { _client.ModalSubmitted -= HandleModalSubmitted; } catch (Exception ex) { Console.WriteLine($"Error unsubscribing ModalSubmitted: {ex}"); }
                        try { _client.ButtonExecuted -= HandleButtonExecuted; } catch (Exception ex) { Console.WriteLine($"Error unsubscribing ButtonExecuted: {ex}"); }
                        try { _client.SelectMenuExecuted -= HandleSelectMenuExecuted; } catch (Exception ex) { Console.WriteLine($"Error unsubscribing SelectMenuExecuted: {ex}"); }
                        try { _client.GuildScheduledEventStarted -= OnGuildScheduledEventStarted; } catch (Exception ex) { Console.WriteLine($"Error unsubscribing GuildScheduledEventStarted: {ex}"); }
                        try { _client.GuildScheduledEventCompleted -= OnGuildScheduledEventCompleted; } catch (Exception ex) { Console.WriteLine($"Error unsubscribing GuildScheduledEventCompleted: {ex}"); }

                        try { await _client.StopAsync(); } catch (Exception ex) { Console.WriteLine($"Error stopping client: {ex}"); }
                        try { _client.Dispose(); } catch (Exception ex) { Console.WriteLine($"Error disposing client: {ex}"); }
                        _client = null;
                    }
                }
                catch (Exception ex) { Console.WriteLine($"Error during client shutdown: {ex}"); }

                // Очистка модулей (таймеры/статические данные)
                try { QueueModule.ShutdownQueue(); } catch (Exception ex) { Console.WriteLine($"Error shutting down QueueModule: {ex}"); }

                // Освобождение лог-семафора
                try { _logSemaphore?.Dispose(); } catch (Exception ex) { Console.WriteLine($"Error disposing log semaphore: {ex}"); }

                // Освобождение локального семафора рестарта
                try { _restartLock?.Dispose(); } catch (Exception ex) { Console.WriteLine($"Error disposing restart lock: {ex}"); }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DisposeAsync error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            DisposeAsync().GetAwaiter().GetResult();
        }

        private async Task HandleModalSubmitted(SocketModal modal)
        {
            var customId = modal.Data.CustomId ?? string.Empty;
            try
            {
                await LogInfo($"Modal submitted: CustomId={customId} User={modal.User?.Id} Username={modal.User?.Username}");
               // Respond directly (ephemeral) so we can delete the original response reliably.
                var parts = customId.Split(':');
                if (parts.Length == 0) return;

                // Existing edit modal handling
                if (parts.Length >= 2 && parts[0] == "edit_modal")
                {
                    await new GameSessionCommands(_client, _googleSheetsService).HandleEditModal(modal);
                    return;
                }

                if (parts[0] == "music_goto_modal")
                {
                    if (_musicCommands is not null)
                        await _musicCommands.HandleGoToModalAsync(modal);
                    return;
                }

                // Handle bet modal: pred_bet_modal:<guildId>
                if (parts[0] == "pred_bet_modal")
                {
                    if (parts.Length < 2)
                    {
                        await modal.RespondAsync("Неверный модал.", ephemeral: true);
                        return;
                    }

                    if (!ulong.TryParse(parts[1], out var guildId))
                    {
                        await modal.RespondAsync("Неверный идентификатор сервера.", ephemeral: true);
                        return;
                    }

                    // Remove the earlier ephemeral "balance + continue" UI right after modal submit.
                    try
                    {
                        if (_pendingBetUi.TryRemove($"{guildId}:{modal.User.Id}", out var pending))
                        {
                            try { await pending.DeleteOriginalResponseAsync().ConfigureAwait(false); } catch { }
                        }
                    }
                    catch { }

                    // Extract fields from modal components (flat)
                    string outcomeStr = string.Empty;
                    string amountStr = string.Empty;
                    foreach (var comp in modal.Data.Components)
                    {
                        try { await LogInfo($"Modal field: id={comp.CustomId} value={comp.Value}"); } catch { }
                        if (string.Equals(comp.CustomId, "outcome", StringComparison.OrdinalIgnoreCase)) outcomeStr = comp.Value ?? string.Empty;
                        if (string.Equals(comp.CustomId, "amount", StringComparison.OrdinalIgnoreCase)) amountStr = comp.Value ?? string.Empty;
                    }

                    if (!int.TryParse(outcomeStr, out var outcomeNum) || outcomeNum < 1)
                    {
                        await modal.FollowupAsync("Неверный номер исхода.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                    // ✅ Проверка: исход существует в активном прогнозе
                    var activePrediction = _predictionService.GetActive(guildId);
                    if (activePrediction == null || activePrediction.GetOutcomeById(outcomeNum) == null)
                    {
                        var maxOutcome = activePrediction?.Outcomes.Count ?? 2;
                        await modal.FollowupAsync($"Исход должен быть от 1 до {maxOutcome}.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                    if (!long.TryParse(amountStr, out var amount) || amount <= 0)
                    {
                        await modal.FollowupAsync("Сумма должна быть положительна.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                    var userId = modal.User.Id;
                  try
                    {
                        _pointsUserIndex.UpsertFromUser(guildId, modal.User);
                        _ = Task.Run(() => _pointsUserIndex.SaveAsync());
                    }
                    catch { }
                    var res = await _predictionService.PlaceBetAsync(guildId, userId, outcomeNum, amount);
                    await LogInfo($"PlaceBet result: ok={res.ok} error={res.error}");
                    if (res.ok)
                    {
                        try { await modal.RespondAsync($"Ставка {amount} на исход {outcomeNum} принята.", ephemeral: true).ConfigureAwait(false); } catch { }
                    }
                    else
                    {
                        try { await modal.RespondAsync(res.error, ephemeral: true).ConfigureAwait(false); } catch { }
                    }

                  ScheduleDeleteOriginalResponse(modal);

                    return;
                }

                // Handle bet add modal: pred_bet_add_modal:<guildId>
                if (parts[0] == "pred_bet_add_modal")
                {
                    if (parts.Length < 2)
                    {
                        await modal.FollowupAsync("Неверный модал.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                    if (!ulong.TryParse(parts[1], out var guildId))
                    {
                        await modal.FollowupAsync("Неверный идентификатор сервера.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                    // Remove the earlier ephemeral "balance + continue" UI right after modal submit.
                    try
                    {
                        if (_pendingBetUi.TryRemove($"{guildId}:{modal.User.Id}", out var pending))
                        {
                            try { await pending.DeleteOriginalResponseAsync().ConfigureAwait(false); } catch { }
                        }
                    }
                    catch { }

                    var active = _predictionService?.GetActive(guildId);
                    if (active == null || active.IsResolved)
                    {
                        await modal.FollowupAsync("Сейчас нет активного прогноза.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                    if (!active.Bets.TryGetValue(modal.User.Id, out var existingBet))
                    {
                        await modal.FollowupAsync("Вы ещё не делали ставку. Используйте обычную ставку.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                    string amountStr = string.Empty;
                    foreach (var comp in modal.Data.Components)
                    {
                        if (string.Equals(comp.CustomId, "amount", StringComparison.OrdinalIgnoreCase))
                            amountStr = comp.Value ?? string.Empty;
                    }

                    if (!long.TryParse(amountStr, out var amount) || amount <= 0)
                    {
                        await modal.FollowupAsync("Сумма должна быть положительна.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                    var outcomeNum = existingBet.OutcomeId;
                  try
                    {
                        _pointsUserIndex.UpsertFromUser(guildId, modal.User);
                        _ = Task.Run(() => _pointsUserIndex.SaveAsync());
                    }
                    catch { }
                    var res = await _predictionService!.PlaceBetAsync(guildId, modal.User.Id, outcomeNum, amount);
                    await LogInfo($"PlaceBet(add) result: ok={res.ok} error={res.error}");
                    if (res.ok)
                    {
                        try { await modal.RespondAsync($"Ставка увеличена на {amount} (исход {outcomeNum}).", ephemeral: true).ConfigureAwait(false); } catch { }
                    }
                    else
                    {
                        try { await modal.RespondAsync(res.error, ephemeral: true).ConfigureAwait(false); } catch { }
                    }

                    ScheduleDeleteOriginalResponse(modal);
                    return;
                }

                // Handle create modal (3 outcomes): pred_create_modal_3:<guildId>:<channelId>
                if (parts[0] == "pred_create_modal_3")
                {
                    if (parts.Length < 3)
                    {
                        await modal.FollowupAsync("Неверный модал.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }
                    if (!ulong.TryParse(parts[1], out var guildId))
                    {
                        await modal.FollowupAsync("Неверный guildId.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }
                    if (!ulong.TryParse(parts[2], out var channelId))
                    {
                        await modal.FollowupAsync("Неверный channelId.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                    // ✅ Обновлено: поддержка до 3 исходов
                    string title = string.Empty, oc1 = string.Empty, oc2 = string.Empty, oc3 = string.Empty, durationStr = string.Empty;
                    foreach (var comp in modal.Data.Components)
                    {
                        try { await LogInfo($"Modal field: id={comp.CustomId} value={comp.Value}"); } catch { }
                        if (string.Equals(comp.CustomId, "title", StringComparison.OrdinalIgnoreCase)) title = comp.Value ?? string.Empty;
                        if (string.Equals(comp.CustomId, "outcome1", StringComparison.OrdinalIgnoreCase)) oc1 = comp.Value ?? string.Empty;
                        if (string.Equals(comp.CustomId, "outcome2", StringComparison.OrdinalIgnoreCase)) oc2 = comp.Value ?? string.Empty;
                        if (string.Equals(comp.CustomId, "outcome3", StringComparison.OrdinalIgnoreCase)) oc3 = comp.Value ?? string.Empty;
                        if (string.Equals(comp.CustomId, "duration_minutes", StringComparison.OrdinalIgnoreCase)) durationStr = comp.Value ?? string.Empty;
                    }

                    await LogInfo($"Create modal values: title='{title}' oc1='{oc1}' oc2='{oc2}' oc3='{oc3}' duration='{durationStr}' user={modal.User.Id}");

                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(oc1) || string.IsNullOrWhiteSpace(oc2))
                    {
                        await modal.FollowupAsync("Заполните заголовок и минимум 2 исхода.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                    if (!int.TryParse(durationStr, out var minutes) || minutes < 1 || minutes > 60)
                    {
                        await modal.FollowupAsync("Время должно быть от 1 до 60 минут. Попробуйте ещё раз.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                    await modal.DeferAsync(ephemeral: true).ConfigureAwait(false);

                    var creatorId = modal.User.Id;

                    // ✅ Обновлено: создание прогноза с N исходами
                    var outcomeNames = new List<string> { oc1, oc2 };
                    if (!string.IsNullOrWhiteSpace(oc3))
                    {
                        outcomeNames.Add(oc3);
                    }

                    var channel = _client.GetChannel(channelId) as ISocketMessageChannel;
                    if (channel == null)
                    {
                        await modal.FollowupAsync("Не удалось найти канал для создания прогноза.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                    var createRes = await _predictionService.CreateAsync(guildId, creatorId, channel, title, outcomeNames.ToArray(), TimeSpan.FromMinutes(minutes));
                    await LogInfo($"CreateAsync result: ok={createRes.ok} error={createRes.error}");
                    if (createRes.ok)
                    {
                        await modal.FollowupAsync($"Прогноз создан: {title}", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                    }
                    else
                    {
                        await modal.FollowupAsync(createRes.error, ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                    }

                    return;
                }

                // ✅ НОВОЕ: Handle step 1 modal (5 outcomes): pred_create_step1:<guildId>:<channelId>
                if (parts[0] == "pred_create_step1")
                {
                    try
                    {
                        if (parts.Length < 3)
                        {
                            await modal.FollowupAsync("Неверный модал.", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }
                        if (!ulong.TryParse(parts[1], out var guildId))
                        {
                            await modal.FollowupAsync("Неверный guildId.", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }
                        if (!ulong.TryParse(parts[2], out var channelId))
                        {
                            await modal.FollowupAsync("Неверный channelId.", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }

                        string title = string.Empty, durationStr = string.Empty;
                        foreach (var comp in modal.Data.Components)
                        {
                            if (string.Equals(comp.CustomId, "title", StringComparison.OrdinalIgnoreCase)) title = comp.Value ?? string.Empty;
                            if (string.Equals(comp.CustomId, "duration_minutes", StringComparison.OrdinalIgnoreCase)) durationStr = comp.Value ?? string.Empty;
                        }

                        if (string.IsNullOrWhiteSpace(title))
                        {
                            await modal.FollowupAsync("Заполните заголовок.", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }

                        if (!int.TryParse(durationStr, out var minutes) || minutes < 1 || minutes > 60)
                        {
                            await modal.FollowupAsync("Время должно быть от 1 до 60 минут. Попробуйте ещё раз.", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }

                        // ✅ Сохраняем данные в словарь для второго шага
                        var key = $"{guildId}:{channelId}:{modal.User.Id}";
                        if (_pendingPredictionCreate == null)
                        {
                            await PredictionErrorLogger.LogAsync("pred_create_step1", new Exception("_pendingPredictionCreate is null"), $"guild={guildId}").ConfigureAwait(false);
                            await modal.FollowupAsync("Внутренняя ошибка. Попробуйте ещё раз.", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }

                        _pendingPredictionCreate[key] = (title, minutes);

                        // ✅ Отправляем кнопку для перехода ко второму шагу (нельзя модал → модал)
                        var continueButton = new ComponentBuilder()
                            .WithButton("➡️ Продолжить (шаг 2/2)", customId: $"pred_continue_step2:{guildId}:{channelId}", style: ButtonStyle.Primary);

                        await modal.RespondAsync($"✅ Прогноз: **{title}** ({minutes} мин)\n\nНажмите кнопку для ввода исходов:",
                            components: continueButton.Build(), ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }
                    catch (Exception ex)
                    {
                        await PredictionErrorLogger.LogAsync("pred_create_step1", ex, $"customId={customId} user={modal.User?.Id}").ConfigureAwait(false);
                        try { await modal.FollowupAsync("Произошла ошибка. Попробуйте начать сначала.", ephemeral: true).ConfigureAwait(false); } catch { }
                        try { ScheduleDeleteOriginalResponse(modal); } catch { }
                        return;
                    }
                }

                // ✅ НОВОЕ: Handle step 2 modal (5 outcomes): pred_create_step2:<guildId>:<channelId>
                if (parts[0] == "pred_create_step2")
                {
                    try
                    {
                        if (parts.Length < 3)
                        {
                            await modal.FollowupAsync("Неверный модал.", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }
                        if (!ulong.TryParse(parts[1], out var guildId))
                        {
                            await modal.FollowupAsync("Неверный guildId.", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }
                        if (!ulong.TryParse(parts[2], out var channelId))
                        {
                            await modal.FollowupAsync("Неверный channelId.", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }

                        // ✅ Читаем данные из словаря
                        var key = $"{guildId}:{channelId}:{modal.User.Id}";
                        if (!_pendingPredictionCreate.TryRemove(key, out var data))
                        {
                            await modal.FollowupAsync("Данные первого шага не найдены. Начните сначала.", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }

                        var title = data.title;
                        var minutes = data.minutes;

                        string oc1 = string.Empty, oc2 = string.Empty, oc3 = string.Empty, oc4 = string.Empty, oc5 = string.Empty;
                        foreach (var comp in modal.Data.Components)
                        {
                            if (string.Equals(comp.CustomId, "outcome1", StringComparison.OrdinalIgnoreCase)) oc1 = comp.Value ?? string.Empty;
                            if (string.Equals(comp.CustomId, "outcome2", StringComparison.OrdinalIgnoreCase)) oc2 = comp.Value ?? string.Empty;
                            if (string.Equals(comp.CustomId, "outcome3", StringComparison.OrdinalIgnoreCase)) oc3 = comp.Value ?? string.Empty;
                            if (string.Equals(comp.CustomId, "outcome4", StringComparison.OrdinalIgnoreCase)) oc4 = comp.Value ?? string.Empty;
                            if (string.Equals(comp.CustomId, "outcome5", StringComparison.OrdinalIgnoreCase)) oc5 = comp.Value ?? string.Empty;
                        }

                        if (string.IsNullOrWhiteSpace(oc1) || string.IsNullOrWhiteSpace(oc2))
                        {
                            await modal.FollowupAsync("Заполните минимум 2 исхода.", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }

                        await modal.DeferAsync(ephemeral: true).ConfigureAwait(false);

                        var creatorId = modal.User.Id;

                        // Собираем исходы
                        var outcomeNames = new List<string> { oc1, oc2 };
                        if (!string.IsNullOrWhiteSpace(oc3)) outcomeNames.Add(oc3);
                        if (!string.IsNullOrWhiteSpace(oc4)) outcomeNames.Add(oc4);
                        if (!string.IsNullOrWhiteSpace(oc5)) outcomeNames.Add(oc5);

                        var channel = _client.GetChannel(channelId) as ISocketMessageChannel;
                        if (channel == null)
                        {
                            await modal.FollowupAsync("Не удалось найти канал для создания прогноза.", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }

                        var createRes = await _predictionService.CreateAsync(guildId, creatorId, channel, title, outcomeNames.ToArray(), TimeSpan.FromMinutes(minutes));
                        await LogInfo($"CreateAsync result: ok={createRes.ok} error={createRes.error}");
                        if (createRes.ok)
                        {
                            await modal.FollowupAsync($"Прогноз создан: {title}", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                        }
                        else
                        {
                            await modal.FollowupAsync(createRes.error, ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                        }

                        return;
                    }
                    catch (Exception ex)
                    {
                        await PredictionErrorLogger.LogAsync("pred_create_step2", ex, $"customId={customId} user={modal.User?.Id}").ConfigureAwait(false);
                        try { await modal.FollowupAsync("Произошла ошибка при создании прогноза. Попробуйте ещё раз.", ephemeral: true).ConfigureAwait(false); } catch { }
                        try { ScheduleDeleteOriginalResponse(modal); } catch { }
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                try { await PredictionErrorLogger.LogAsync("HandleModalSubmitted", ex, $"customId={customId}").ConfigureAwait(false); } catch { }
                try { await LogError($"HandleModalSubmitted exception for CustomId={customId}: {ex}"); } catch { }
                try { await modal.FollowupAsync("Что-то пошло не так. Повторите попытку.", ephemeral: true).ConfigureAwait(false); } catch { }
                try { ScheduleDeleteOriginalResponse(modal); } catch { }
            }
        }

        public async Task HandleButtonExecuted(SocketMessageComponent component)
        {
            await Task.Yield(); // Сразу освобождаем поток шлюза

            try
            {
                await ProcessButtonAsync(component).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { await PredictionErrorLogger.LogAsync("HandleButtonExecuted", ex, $"customId={component.Data.CustomId}").ConfigureAwait(false); } catch { }
                await LogError($"Ошибка обработки кнопки: {ex.Message}");
                try { await component.RespondAsync("Ошибка обработки", ephemeral: true); } catch { }
            }
        }

        public async Task HandleSelectMenuExecuted(SocketMessageComponent component)
        {
            await Task.Yield();
            try
            {
                var cid = component.Data.CustomId;
                if (_musicCommands is not null && cid.StartsWith("music_search_select:"))
                    await _musicCommands.HandleButtonAsync(component);
            }
            catch (Exception ex)
            {
                await LogError($"Ошибка обработки SelectMenu: {ex.Message}");
                try { await component.RespondAsync("Ошибка взаимодействия", ephemeral: true); } catch { }
            }
        }

        private async Task ProcessButtonAsync(SocketMessageComponent component)
        {
            var parts = component.Data.CustomId.Split(':');
            var buttonType = parts.Length > 0 ? parts[0] : component.Data.CustomId;

            switch (buttonType)
            {
                case "pred_outcomes":
                    await HandlePredictionOutcomesButton(component, parts);
                    break;
                case "pred_continue_step2":
                    await HandlePredictionContinueStep2Button(component, parts);
                    break;
                case "pred_bet":
                    await HandlePredictionBetButton(component, parts);
                    break;
                case "pred_bet_confirm":
                    await HandlePredictionBetConfirmButton(component, parts);
                    break;
                case "pred_cancel":
                    await HandlePredictionCancelButton(component, parts);
                    break;
                case "pred_resolve":
                    await HandlePredictionResolveButton(component, parts);
                    break;
                case "pred_history_page":
                    await HandlePredictionHistoryPageButton(component, parts);
                    break;
                case "pause_session":
                case "resume_session":
                case "edit_session":
                case "stop_session":
                case "confirm_stop":
                case "cancel_stop":
                case "toggle_rolls":
                    await new GameSessionCommands(_client, _googleSheetsService).HandleControlButton(component);
                    break;

                case "no_stats":
                case "general_stats":
                case "detailed_stats":
                    await new GameSessionCommands(_client, _googleSheetsService).HandleStatsButton(component);
                    break;

                case "music_prev":
                case "music_pauseplay":
                case "music_skip":
                case "music_stop":
                case "music_loop":
                case "music_shuffle":
                case "music_vol_down":
                case "music_vol_up":
                case "music_queue":
                case "music_queue_prev":
                case "music_queue_next":
                case "music_queue_goto":
                case "music_autopause_resume":
                case "music_autopause_skip":
                    if (_musicCommands is not null)
                        await _musicCommands.HandleButtonAsync(component);
                    break;

                default:
                    var cid = component.Data.CustomId;
                    if (_musicCommands is not null &&
                        (cid.StartsWith("music_search_") ||
                         cid.StartsWith("playlist_public_yes_") ||
                         cid.StartsWith("playlist_public_no_") ||
                         cid.StartsWith("playlist_overwrite_yes_") ||
                         cid.StartsWith("playlist_overwrite_no_")))
                    {
                        await _musicCommands.HandleButtonAsync(component);
                    }
					break;
			}
		}

		private async Task<bool> TryHandleEventNotifyDirectMessageAsync(SocketUserMessage message)
		{
			var text = (message.Content ?? string.Empty).Trim();
			if (text.Length == 0)
				return false;

			// Нормализуем пробелы и приводим к нижнему регистру
			var normalized = Regex.Replace(text, "\\s+", " ").Trim().ToLowerInvariant();
			var userId = message.Author.Id;

			if (normalized is "стоп" or "хватит" or "stop")
			{
				_eventNotifications.Pause(userId);
				await message.AddReactionAsync(new Emoji("✅"));
				await message.Channel.SendMessageAsync("[Сохранено] Отключил личные уведомления о новых событиях. Чтобы включить обратно — напиши «хочу» или подпишись заново через /event_notify subscribe на сервере.");
				return true;
			}

			if (normalized is "хочу" or "включи" or "start")
			{
				_eventNotifications.Unpause(userId);
				await message.AddReactionAsync(new Emoji("✅"));
				await message.Channel.SendMessageAsync("[Сохранено] Личные уведомления снова включены (если ты был подписан на сервере). Проверить/подписаться: /event_notify status или /event_notify subscribe в нужном сервере.");
				return true;
			}

			if (normalized is "статус" or "status")
			{
				var paused = _eventNotifications.IsPaused(userId);
				await message.AddReactionAsync(new Emoji("✅"));
				await message.Channel.SendMessageAsync(paused
					? "[Статус] Сейчас личные уведомления поставлены на паузу. Чтобы вернуть — напиши «хочу»."
					: "[Статус] Сейчас личные уведомления не на паузе. Подписка на конкретный сервер проверяется командой /event_notify status на сервере.");
				return true;
			}

			if (normalized is "подписка" or "subscribe" or "отписка" or "unsubscribe")
			{
				await message.Channel.SendMessageAsync("Подписка/отписка делается на конкретном сервере: используй /event_notify subscribe или /event_notify unsubscribe в нужном сервере.");
				return true;
			}

			return false;
		}

        private async Task HandleCommandAsync(SocketMessage arg)
        {
            if (arg is not SocketUserMessage message || message.Author.IsBot) return;

			// DM команды для управления уведомлениями о событиях
			if (message.Channel is IDMChannel)
			{
				if (await TryHandleEventNotifyDirectMessageAsync(message))
					return;
			}

            var context = new SocketCommandContext(_client, message);
            var user = message.Author as SocketGuildUser;

            // Простая фильтрация мата: если включена для сервера — удаляем сообщение и логируем
            try
            {
                if (message.Channel is SocketTextChannel textChannel)
                {
                    var guildId = textChannel.Guild.Id;
                    if (_serverConfigs.TryGetValue(guildId, out var sconfig) && sconfig.SwearFilterEnabled)
                    {
                        var swearWords = (sconfig.SwearWords != null && sconfig.SwearWords.Count > 0)
                            ? sconfig.SwearWords
                            : (_config != null ? (BotConfig.Current?.DefaultSwearWords ?? new List<string>()) : new List<string>());

                        var lower = message.Content.ToLowerInvariant();
                        if (swearWords.Any(sw => !string.IsNullOrWhiteSpace(sw) && lower.Contains(sw.ToLowerInvariant())))
                        {
                            try { await message.DeleteAsync(); } catch { }
                            // логируем в модерационный канал
                            if (sconfig.ModerateChannelID != 0)
                            {
                                var modChan = await _client.GetChannelAsync(sconfig.ModerateChannelID) as ITextChannel;
                                if (modChan != null)
                                {
                                    await modChan.SendMessageAsync($"Сообщение пользователя {message.Author.Username} удалено — найдено запрещённое слово.");
                                }
                            }
                            else
                            {
                                await LogInfo($"Удалено сообщение пользователя {message.Author.Username} (сработал фильтр мата)");
                            }
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await LogError($"Ошибка в фильтре мата: {ex.Message}");
            }

            var (fludChannelId, rollChannelId, generalRGChannelID, lineMessages, emoteKappa, emoteAga) = GetResponseData(message);

            // Определяем, является ли канал голосовым
            bool isVoiceChannel = message.Channel is SocketVoiceChannel;

            // Определяем разрешённые текстовые каналы для команд
            var allowedTextChannels = new ulong[] { fludChannelId, generalRGChannelID };
            bool isAllowedTextChannel = allowedTextChannels.Contains(message.Channel.Id);

            // Приветствие
            var lowerContent = message.Content.ToLowerInvariant();
            var greetings = new[] { "привет", "приветствую", "здравствуйте", "здравствуй", "hello", "hi", "хай", "ку", "здрасте" };

            // Разбиваем сообщение на отдельные слова
            var messageWords = lowerContent.Split(new[] { ' ', ',', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);

            // Проверяем, содержит ли сообщение приветствие как отдельное слово
            if (messageWords.Any(word => greetings.Contains(word)))
            {
                await message.AddReactionAsync(new Emoji("👋"));
                return;
            }

            // Обработка команды "!команды" (доступна везде)
            if (message.Content.ToLowerInvariant() == "!команды")
            {
                var commandsList = new StringBuilder();
                commandsList.AppendLine("Доступные команды:");
                commandsList.AppendLine("\n**--Во всех чатах--**");
                commandsList.AppendLine("`!правила` - правила сервера");
                commandsList.AppendLine("`!ссылки` - полезные ссылки");
                commandsList.AppendLine("`!запись` - документ для записи игр");
                commandsList.AppendLine("\n**--Для голосовых каналов--**");
                commandsList.AppendLine("`!бегу` - бегу с сыном");
                commandsList.AppendLine("`!гусь` - паста гуся");
                commandsList.AppendLine("`!гусь-гидра` - паста гидры гуся");
                commandsList.AppendLine("`!гусь-связь` - паста с гусём-связистом");
                commandsList.AppendLine("`!начинается` - AFK");
                commandsList.AppendLine("`!перекур` - перерыв");
                commandsList.AppendLine("`!подсказка` - Чят, пляшем!");
                commandsList.AppendLine("`!страх` - атата");
                commandsList.AppendLine("`!убери` - ненавижу модеров");

                await message.Channel.SendMessageAsync(commandsList.ToString());
                return;
            }

            // Обработка сообщений, начинающихся с "!" (только если после ! сразу идёт буква)
            if (message.Content.StartsWith("!") && message.Content.Length > 1 && !char.IsWhiteSpace(message.Content[1]))
            {
                var key = message.Content.Split(' ')[0].ToLower();

                var voiceCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "!бегу",
                    "!гусь",
                    "!гусь-гидра",
                    "!гусь-связь",
                    "!начинается",
                    "!перекур",
                    "!подсказка",
                    "!страх",
                    "!убери"
                };

                // ВРЕМЕННАЯ ЗАПЛАТКА: эти команды должны работать во всех чатах
                // ("правила", "ссылки", "запись").
                if (key is "!правила" or "!ссылки" or "!запись")
                {
                    if (_textBlocks.ContainsKey(key))
                    {
                        await message.Channel.SendMessageAsync(_textBlocks[key]);
                        return;
                    }

                    await message.Channel.SendMessageAsync("Текст для этой команды не настроен.");
                    return;
                }

                // Голосовые команды
                if (voiceCommands.Contains(key))
                {
                    if (!isVoiceChannel)
                    {
                        await message.Channel.SendMessageAsync("Эта команда доступна только в чате голосового канала.");
                        return;
                    }

                    if (_textBlocks.TryGetValue(key, out var text))
                    {
                        await message.Channel.SendMessageAsync(text);
                        return;
                    }

                    await message.Channel.SendMessageAsync("Текст для этой команды не настроен.");
                    return;
                }

                // Неизвестная команда
                await message.Channel.SendMessageAsync("Неизвестная команда. Введите `!команды` для списка.");
                return;


            }

            if (message.Content.ToLower() == "👏")
            {
                if (user != null)
                {
                    await message.DeleteAsync();
                    await message.Channel.SendMessageAsync("КРАСИВО 🔥 ВЕЛИКОЛЕПНО 🔥 ЗАМЕЧАТЕЛЬНО 🔥 ПРЕКРАСНО 🔥 СУПЕР 🔥 УМОПОМРАЧИТЕЛЬНО 🔥 СНОГШИБАТЕЛЬНО 🔥 ПРЕВОСХОДНО 🔥 ШИКАРНО");
                }
                await LogInfo("Хлопание");
            }

            if (message.Content.ToLower().Contains("диктатор"))
            {
                await HandleDictatorCommand(user, message);
            }

            if (message.Content.ToLower().Contains("мастерский произвол"))
            {
                await HandleMasteryArbitrarinessCommand(user, message, emoteKappa, emoteAga);
            }

            if ((message.Content.ToLower() == "line" || message.Content.ToLower() == "ход") && message.Channel.Id == rollChannelId)
            {
                await HandleLineCommand(user, message, lineMessages);
            }

            // Управление ботом через чат: администратор или суперпользователь сервера
            if (user is SocketGuildUser guildUser)
            {
                if (CanUseSuperUserActions(guildUser, guildUser.Guild.Id))
                {
                    var content = message.Content.ToLower();

                    if (content.Contains("бот, спокойной ночи"))
                    {
                        await message.Channel.SendMessageAsync("Отключение всех систем...");
                        await LogStartup($"Бот отключен по команде из чата пользователем {message.Author.Username} в {DateTime.Now}.");

                        // Остановка с той же логикой, что и при команде из консоли, но с пометкой об инициаторе
                        await StopInternalAsync(
                            initiator: "chat",
                            startupLogMessage: "Остановка по команде из чата...",
                            shutdownNotificationReason: "Остановка по команде из чата");
                        return;
                    }

                    if (content.Contains("бот, перезагрузка"))
                    {
                        await message.Channel.SendMessageAsync("Бот будет перезагружен. Пожалуйста, подождите... Примерное время ожидания от 10 секунд до 3 минут.");
                        await LogStartup($"Инициализация перезагрузки по команде из чата пользователем {message.Author.Username} в {DateTime.Now}.");

                        // Перезапуск через общую логику RestartWithReasonAsync
                        await RestartWithReasonAsync(
                            initiator: "chat",
                            reason: $"Перезапуск по команде из чата пользователем {message.Author.Username}");
                        return;
                    }
                }
            }
        }

        public static readonly Dictionary<ulong, ServerConfig> ServerConfigs = new Dictionary<ulong, ServerConfig>
        {
            {
                1288192593137635359, // ID тестового сервера
                new ServerConfig
                {
                    GuildID = 1288192593137635359,
                    ModerateChannelID = 1433623049147514981, // спам-от-бота
                    WelcomeChannelID = 1288192593137635362, // основной
                    RollChannelID = 1400042149470539837, // броски-кубов
                    StatsChannelID = 1400042149470539837, // броски-кубов
                    RecordChannelID = 1333559817045807176, // 1(архив)
                    GeneralRGChannelID = 1333559817045807176, // 1(архив)
                    WelcomeMessage = "Приветствую тебя, {user.Mention}! О всех проблемах, которые могут со мной возникнуть, передай, пожалуйста, администратору, или воспользуйся командой `/bug_report`. Хорошо? \nСоветую первой командой использовать `/help`",
                    LineMessage = "<:begin:1333879098488918098><:middle1:1333879112510472254><:middle2:1333879114440118334><:middle3:1333879116281151550><:end1:1333879106747633735>",
                    DefaultRoleID = 776013522370560031
                }
            },
            {
                295189463376855040, // ID основного сервера (КнР)
                new ServerConfig
                {
                    GuildID = 295189463376855040,
                    ModerateChannelID = 1433622718926028820, // спам-от-бота
                    WelcomeChannelID = 373788351246893056, // флудилка
                    RollChannelID = 710471746108784691, // броски-кубов
                    StatsChannelID = 710471746108784691, // броски-кубов
                    RecordChannelID = 1345036014519058464, // запись-времени
                    GeneralRGChannelID = 890295184577937418, // общий-ролевой-чат
                    WelcomeMessage = "Приветствую тебя, {user.Mention}! О всех проблемах, которые могут со мной возникнуть, передай, пожалуйста, администратору, или воспользуйся командой `/bug_report`. Хорошо? \nСоветую первой командой использовать `/help`",
                    LineMessage = "<:1begin:1151822634250686504><:2middle1:1151822618677219328><:3middle2:1151822625216155649><:4middle3:1151822621198000169><:5end:1151822629381087292>",
                    DefaultRoleID = 776013522370560031
                }
            }
        };

        private (ulong welcomeChannelId, ulong rollChannelId, ulong generalRGChannelID, string? lineMessages, string? emoteKappa, string? emoteAga) GetResponseData(SocketMessage message)
        {
            var channel = message.Channel as SocketGuildChannel;
			if (channel == null || !_serverConfigs.TryGetValue(channel.Guild.Id, out var config))
            {
             return (0, 0, 0, null, null, null);
            }

            // Для тестового сервера
            if (channel.Guild.Id == 1288192593137635359)
            {
                return (config.WelcomeChannelID, config.RollChannelID, config.GeneralRGChannelID, config.LineMessage,
                    "<:kappa:1333879110602326046>", "<:agakakskagesh:1333878999977431174>");
            }
            // Для основного сервера
            else if (channel.Guild.Id == 295189463376855040)
            {
                return (config.WelcomeChannelID, config.RollChannelID, config.GeneralRGChannelID, config.LineMessage,
                    "<:kappa:1100150992428871720>", "<:Agakakskagesh:1316461730569916557>");
            }

         return (0, 0, 0, null, null, null);
        }

        private async Task HandleDictatorCommand(SocketGuildUser user, SocketMessage message)
        {
            await message.DeleteAsync();

            if (user != null)
            {
                var guildChannel = message.Channel as SocketGuildChannel;
                var guild = guildChannel?.Guild;

                if (guild != null)
                {
                    var muteRole = guild.Roles.FirstOrDefault(r => r.Name == "Mute");
                    if (muteRole != null)
                    {
                        await user.AddRoleAsync(muteRole);

                        var punishmentMessage = await message.Channel.SendMessageAsync($"Ахаха, {user.Mention} досанабился!");
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(3000);
                            await punishmentMessage.DeleteAsync();
                            await user.RemoveRoleAsync(muteRole);
                        });
                    }
                }
            }
        }

        private async Task HandleMasteryArbitrarinessCommand(SocketGuildUser user, SocketMessage message, string emoteKappa, string emoteAga)
        {
            if (user != null)
            {
                var emote = Emote.Parse(emoteAga);
                await message.AddReactionAsync(emote);
                var messageReference = new MessageReference(message.Id);

                var responseMessage = await message.Channel.SendMessageAsync($"Ууу, сука! Скажи, да?!",
                    messageReference: messageReference);

                emote = Emote.Parse(emoteKappa);
                await responseMessage.AddReactionAsync(emote);
            }
            await LogInfo("Произволит");
        }

        private async Task HandleLineCommand(SocketGuildUser user, SocketMessage message, string lineMessages)
        {
            if (user != null)
            {
                await message.DeleteAsync();

                await Task.Delay(500);

                await message.Channel.SendMessageAsync(lineMessages);
            }
            await LogInfo("Линия отправлена");
        }

        private int GetBugReportCounter()
        {
			var logDirRaw = _config?.LogDirectory;
			var logDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDirRaw) ? "Logs" : logDirRaw);
			Directory.CreateDirectory(logDir);
			string counterFilePath = Path.Combine(logDir, "bug_report_counter.txt");

            if (File.Exists(counterFilePath))
            {
                if (int.TryParse(File.ReadAllText(counterFilePath), out var value))
                    return value;
            }

            return 0;
        }

        private async Task OnSlashCommandExecuted(SocketSlashCommand command)
        {
            switch (command.Data.Name)
            {
                case "stop_q":
                    await StopQueue(command);
                    break;
                case "queue":
                    await QueueCommand(command);
                    break;
                case "q":
                    await Q_InCommand(command);
                    break;
                case "clr":
                    await ClearMessage(command);
                    break;
                case "roll":
                    await RollCommand(command);
                    break;
                case "roll20":
                    await Roll20Command(command);
                    break;
                case "roll_pictures":
                    await RollPicturesCommand(command);
                    break;
                case "serverinfo":
                    await ServerInfoCommand(command);
                    break;
                case "help":
                    await HelpCommand(command);
                    break;
                case "help_r":
                    await Help_RollCommand(command);
                    break;
                case "help_gs":
                    await Help_GameSessionCommand(command);
                    break;
                case "help_music":
                    await Help_MusicCommand(command);
                    break;
                case "help_predict":
                    await Help_PredictCommand(command);
                    break;
                case "bug_report":
                    await Bug_ReportCommand(command);
                    break;
				case "start":
                    await StartGameSession(command);
                    break;
                case "settings":
                    await SettingsCommand(command);
                    break;
                case "prediction":
                    await PredictionCommand(command);
                    break;
                case "close_chat":
                    await CloseChatCommand(command);
                    break;
                case "open_chat":
                    await OpenChatCommand(command);
                    break;
                case "event_notify":
                    await EventNotifyCommand(command);
                    break;
                case "bwonk":
                    // handled by BwonkCommand (subscribed handler)
                    break;
                case "music":
                    if (_musicCommands is not null)
                        await _musicCommands.HandleMusicAsync(command);
                    else
                        await command.RespondAsync("❌ Музыкальный модуль отключён (Music.Enabled = false).", ephemeral: true);
                    break;
                case "music-playlist":
                    if (_musicCommands is not null)
                        await _musicCommands.HandleMusicPlaylistAsync(command);
                    else
                        await command.RespondAsync("❌ Музыкальный модуль отключён (Music.Enabled = false).", ephemeral: true);
                    break;
                default:
                    await command.RespondAsync("Команда не распознана.");
                    break;
            }
        }

		private async Task EventNotifyCommand(SocketSlashCommand command)
		{
			var guildId = command.GuildId;
			if (!guildId.HasValue)
			{
				await command.RespondAsync("Эта команда доступна только на сервере.", ephemeral: true);
				return;
			}

			try
			{
				var action = command.Data.Options.FirstOrDefault(o => o.Name == "action")?.Value?.ToString();
				action = string.IsNullOrWhiteSpace(action) ? "status" : action;

				switch (action.ToLowerInvariant())
				{
					case "subscribe":
					{
						_eventNotifications.Subscribe(guildId.Value, command.User.Id);
						await command.RespondAsync("Готово. Буду присылать в личные сообщения уведомления о новых событиях на этом сервере. Чтобы отключить — /event_notify unsubscribe или напиши мне «стоп».", ephemeral: true);
						break;
					}
					case "unsubscribe":
					{
						var removed = _eventNotifications.Unsubscribe(guildId.Value, command.User.Id);
						await command.RespondAsync(removed
							? "Ок, отписал от уведомлений по этому серверу."
							: "Вы и так не были подписаны на уведомления по этому серверу.", ephemeral: true);
						break;
					}
					case "status":
					default:
					{
						var subscribed = _eventNotifications.IsSubscribed(guildId.Value, command.User.Id);
						var paused = _eventNotifications.IsPaused(command.User.Id);
						var txt = $"Подписка на этот сервер: {(subscribed ? "✅ да" : "❌ нет")}. Пауза личных уведомлений: {(paused ? "⏸️ да" : "▶️ нет")}.";
						await command.RespondAsync(txt, ephemeral: true);
						break;
					}
				}
			}
			catch (Exception ex)
			{
				await LogError($"Ошибка в EventNotifyCommand: {ex.Message}");
				try
				{
					await command.RespondAsync("Произошла ошибка при работе с подпиской. Попробуйте ещё раз позже или сообщите администратору.", ephemeral: true);
				}
				catch { }
			}
		}

        private async Task StopQueue(SocketSlashCommand command)
        {
            var queueModule = _services.GetRequiredService<QueueModule>();
            await queueModule.StopQueue(command);
            await LogInfo("Очередь остановлена.");
        }

        private async Task QueueCommand(SocketSlashCommand command)
        {
            var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
            if (int.TryParse(inputOption?.Value?.ToString(), out int participantsCount))
            {
                var qm = _services.GetRequiredService<QueueModule>();
                await qm.QueueCommand(command, participantsCount);
            }
            else
            {
                await command.RespondAsync("Ошибка: неверный формат ввода. Пожалуйста, введите целое число.");
                await LogError("Ошибка: неверный формат ввода. Пожалуйста, введите целое число.");
            }
        }

        private async Task Q_InCommand(SocketSlashCommand command)
        {
            var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
            var input = inputOption?.Value?.ToString() ?? string.Empty;

            var queueModule = _services.GetRequiredService<QueueModule>();
            await queueModule.QIn_RollDice(command, input);
        }

        private async Task CloseChatCommand(SocketSlashCommand command)
        {
            var moderationModule = _services.GetRequiredService<ModerationCommands>();
            await moderationModule.CloseChat(command);
            await LogInfo("Чат или ветка закрыты.");
        }

        private async Task OpenChatCommand(SocketSlashCommand command)
        {
            var moderationModule = _services.GetRequiredService<ModerationCommands>();
            await moderationModule.OpenChat(command);
            await LogInfo("Чат открыт и перемещён в указанную категорию.");
        }

        private async Task ClearMessage(SocketSlashCommand command)
        {
            var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
            if (int.TryParse(inputOption?.Value?.ToString(), out int messagesToDelete))
            {
                var moderationModule = _services.GetService<ModerationCommands>();
                var mm = _services.GetRequiredService<ModerationCommands>();
                await mm.ClearMessages(command, messagesToDelete);
            }
            else
            {
                await LogInfo("Ошибка: неверный формат ввода при удалении сообщения. Пожалуйста, введите целое число.");
                await command.RespondAsync("Ошибка: неверный формат ввода. Пожалуйста, введите целое число.", ephemeral: true);
            }
        }

        private async Task RollCommand(SocketSlashCommand command)
        {
            var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
            var input = inputOption?.Value?.ToString() ?? string.Empty;

            var diceModule = _services.GetRequiredService<RollDiceCommands>();
            await diceModule.RollDice(command, input);
        }

        private async Task Roll20Command(SocketSlashCommand command)
        {
            var diceModule = _services.GetRequiredService<RollDiceCommands>();
            await diceModule.Roll20(command);
        }

        private async Task RollPicturesCommand(SocketSlashCommand command)
        {
            if (command.GuildId == null)
            {
                await command.RespondAsync("Эта команда доступна только на сервере.", ephemeral: true);
                return;
            }

            var guildId = command.GuildId.Value;
            var config = ServerConfigResolver?.Invoke(guildId);

            var enabledOpt = command.Data.Options.FirstOrDefault(o => o.Name == "enabled")?.Value;
            bool newValue;
            if (enabledOpt != null)
            {
                newValue = Convert.ToBoolean(enabledOpt);
            }
            else
            {
                var current = config?.RollPicturesEnabled ?? true;
                newValue = !current;
            }

            await SetServerConfigValueAsync(guildId, "roll_pictures", toggle: newValue);
            await command.RespondAsync($"Картинки для бросков {(newValue ? "включены" : "выключены")}.", ephemeral: true);
        }

        private async Task ServerInfoCommand(SocketSlashCommand command)
        {
            var infoModule = _services.GetRequiredService<InfoCommands>();
            await infoModule.ServerInfo(command);
            await LogInfo("Выведена информация о сервере.");
        }

        private async Task HelpCommand(SocketSlashCommand command)
        {
            var infoModule = _services.GetRequiredService<InfoCommands>();
            await infoModule.Help(command);
            await LogInfo("Выведена подсказка о командах.");
        }

        private async Task Help_RollCommand(SocketSlashCommand command)
        {
            var infoModule = _services.GetRequiredService<InfoCommands>();
            await infoModule.Help_R(command);
            await LogInfo("Выведена подсказка о командах для бросков кубов.");
        }

        private async Task Help_PredictCommand(SocketSlashCommand command)
        {
            var infoModule = _services.GetRequiredService<InfoCommands>();
            await infoModule.Help_Predict(command);
            await LogInfo("Выведена подсказка по прогнозам и ставкам.");
        }

        private async Task Help_GameSessionCommand(SocketSlashCommand command)
        {
            var infoModule = _services.GetRequiredService<InfoCommands>();
            await infoModule.Help_GS(command);
            await LogInfo("Выведена подсказка о командах для статистики.");
        }

        private async Task Help_MusicCommand(SocketSlashCommand command)
        {
            var infoModule = _services.GetRequiredService<InfoCommands>();
            await infoModule.Help_Music(command);
            await LogInfo("Выведена подсказка о музыкальных командах.");
        }

        private async Task Bug_ReportCommand(SocketSlashCommand command)
        {
            var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
            var input = inputOption?.Value?.ToString() ?? string.Empty;

            var infoModule = _services.GetRequiredService<InfoCommands>();
            await infoModule.Bug_Report(command, input);
            await LogInfo("Использовано уведомление администратора о баге.");
        }

        private async Task StartGameSession(SocketSlashCommand command)
        {
            var gameNameOption = command.Data.Options.FirstOrDefault(o => o.Name == "game_name");
            var gameName = gameNameOption?.Value?.ToString() ?? string.Empty;

            var masterOption = command.Data.Options.FirstOrDefault(o => o.Name == "master");
            var masterUser = masterOption?.Value as SocketUser;

            var gameCommentOption = command.Data.Options.FirstOrDefault(o => o.Name == "comment");
            var gameComment = gameCommentOption?.Value?.ToString();

            var gameSessionModule = _services.GetRequiredService<GameSessionCommands>();
            await gameSessionModule.StartGameSession(command, gameName, masterUser, gameComment);
        }

        private static bool HasServerRole(SocketGuildUser? user, ulong? roleId)
        {
            if (user == null || !roleId.HasValue || roleId.Value == 0)
                return false;

            return user.Roles.Any(r => r.Id == roleId.Value);
        }

        private bool CanUseSuperUserActions(SocketGuildUser? user, ulong guildId)
        {
            if (user == null)
                return false;

            if (user.GuildPermissions.Administrator)
                return true;

            return _serverConfigs.TryGetValue(guildId, out var cfg) && HasServerRole(user, cfg.SuperUserRoleId);
        }

        private bool CanUseMasterActions(SocketGuildUser? user, ulong guildId)
        {
            if (user == null)
                return false;

            if (user.GuildPermissions.Administrator)
                return true;

            return _serverConfigs.TryGetValue(guildId, out var cfg) && HasServerRole(user, cfg.MasterRoleId);
        }

        private ulong? ResolveRoleId(SocketGuild guild, string? value)
        {
            if (guild == null || string.IsNullOrWhiteSpace(value))
                return null;

            var trimmed = value.Trim();
            if (ulong.TryParse(trimmed, out var id))
            {
                return guild.Roles.Any(r => r.Id == id) ? id : null;
            }

            var normalized = trimmed.TrimStart('@');
            var role = guild.Roles.FirstOrDefault(r => string.Equals(r.Name, normalized, StringComparison.OrdinalIgnoreCase));
            return role?.Id;
        }

        private async Task<ulong?> ResolveChannelIdAsync(ulong guildId, object? channelOpt, string? value, bool requireVoice = false)
        {
            var guild = _client.GetGuild(guildId);
            if (guild == null)
                return null;

            if (channelOpt != null)
            {
                var directId = Convert.ToUInt64(channelOpt);
                var directChannel = await _client.GetChannelAsync(directId) as SocketGuildChannel;
                if (directChannel != null && directChannel.Guild.Id == guildId && (!requireVoice || directChannel is SocketVoiceChannel))
                    return directId;
            }

            if (string.IsNullOrWhiteSpace(value))
                return null;

            var trimmed = value.Trim();
            if (ulong.TryParse(trimmed, out var id))
            {
                var idChannel = await _client.GetChannelAsync(id) as SocketGuildChannel;
                if (idChannel != null && idChannel.Guild.Id == guildId && (!requireVoice || idChannel is SocketVoiceChannel))
                    return id;
            }

            var normalized = trimmed.TrimStart('#');
            SocketGuildChannel? namedChannel = requireVoice
                ? guild.VoiceChannels.FirstOrDefault(c => string.Equals(c.Name, normalized, StringComparison.OrdinalIgnoreCase))
                : guild.Channels.FirstOrDefault(c => string.Equals(c.Name, normalized, StringComparison.OrdinalIgnoreCase));

            return namedChannel?.Id;
        }

        private async Task SettingsCommand(SocketSlashCommand command)
        {
            // Проверяем, что команда запущена в гильдии
            if (command.GuildId == null)
            {
                await command.RespondAsync("Эта команда должна выполняться в контексте сервера (guild).", ephemeral: true);
                return;
            }

			var guildId = command.GuildId.Value;
			var user = command.User as SocketGuildUser;
			if (user == null)
			{
				await command.RespondAsync("Не удалось определить пользователя.", ephemeral: true);
				return;
			}

			// Проверка прав: администратор/ManageGuild или наличие роли суперпользователя (если она настроена на сервере)
			var isAdmin = user.GuildPermissions.Administrator || user.GuildPermissions.ManageGuild;
			ulong? superUserRoleId = null;
			if (_serverConfigs.TryGetValue(guildId, out var existingCfg))
			{
				superUserRoleId = existingCfg.SuperUserRoleId;
			}

			var hasSuperUserRole = superUserRoleId.HasValue && superUserRoleId.Value != 0 &&
				user.Roles.Any(r => r.Id == superUserRoleId.Value);

			if (!isAdmin && !hasSuperUserRole)
			{
				await command.RespondAsync("У вас нет прав для управления настройками (требуется роль суперпользователя или права Manage Guild/Admin).", ephemeral: true);
				return;
			}

            var actionOpt = command.Data.Options.FirstOrDefault(o => o.Name == "action")?.Value?.ToString()?.ToLowerInvariant();
            var keyOpt = command.Data.Options.FirstOrDefault(o => o.Name == "key")?.Value?.ToString()?.ToLowerInvariant();
            var valueOpt = command.Data.Options.FirstOrDefault(o => o.Name == "value")?.Value?.ToString();
            var channelOpt = command.Data.Options.FirstOrDefault(o => o.Name == "channel")?.Value;
            var toggleOpt = command.Data.Options.FirstOrDefault(o => o.Name == "toggle")?.Value;

			if (string.IsNullOrWhiteSpace(actionOpt))
			{
				await command.RespondAsync("Укажите действие: get/set/list/reset/reload/help", ephemeral: true);
				return;
			}

            if (!_serverConfigs.TryGetValue(guildId, out var sconfig))
            {
                sconfig = new ServerConfig { GuildID = guildId };
                _serverConfigs[guildId] = sconfig;
            }

			switch (actionOpt)
			{
				case "help":
					{
						var sb = new StringBuilder();
						sb.AppendLine("Справка по /settings:");
						sb.AppendLine("/settings action:list — показать все текущие настройки сервера.");
						sb.AppendLine("/settings action:get key:<ключ> — показать значение одного параметра.");
						sb.AppendLine("/settings action:set key:<ключ> value:<значение> — изменить параметр.");
						sb.AppendLine("/settings action:reset — сбросить настройки этого сервера.");
						sb.AppendLine("/settings action:reload — перечитать настройки всех серверов из serverconfigs.json.");
						sb.AppendLine();
						sb.AppendLine("Передача значений:");
                      sb.AppendLine("- Для каналов (moderation_channel, welcome_channel, general_rg_channel, roll_channel, stats_channel, record_channel)");
                        sb.AppendLine("  используйте либо параметр channel (выбор канала из списка), либо value с ID или названием канала.");
						sb.AppendLine("  Если указаны оба, приоритет у channel.");
                       sb.AppendLine("- Для ролей (default_role, master_role, super_user_role) указывайте ID роли или её название в value.");
                       sb.AppendLine("- для логических переключателей (swear_filter, predictions, roll_pictures) используйте toggle:true/false или value:true/false.");
                       sb.AppendLine("- Для event_voice_channel укажите ID или название голосового канала в value.");
						sb.AppendLine("- Для текстовых параметров (welcome_message, line_message) используйте value с текстом.");
						sb.AppendLine();
						sb.AppendLine("Примеры:");
						sb.AppendLine("/settings action:set key:moderation_channel channel:#модерация");
						sb.AppendLine("/settings action:set key:moderation_channel value:123456789012345678");
                        sb.AppendLine("/settings action:set key:default_role value:@Игрок");
                        sb.AppendLine("/settings action:set key:master_role value:Мастер НРИ");
						sb.AppendLine("/settings action:set key:swear_filter toggle:true");
						sb.AppendLine("/settings action:set key:welcome_message value:Добро пожаловать!");
						await command.RespondAsync(sb.ToString(), ephemeral: true);
						ScheduleDeleteOriginalResponse(command, delaySeconds: 60); // Увеличено время для чтения справки
					}
					break;

				case "reload":
					{
						LoadServerConfigs();
						await command.RespondAsync("Конфигурации серверов перезагружены из файла serverconfigs.json.", ephemeral: true);
					}
					break;

				case "list":
					{
						var sb = new StringBuilder();
						sb.AppendLine($"Настройки для сервера {guildId}:");
						sb.AppendLine($"moderation_channel: {sconfig.ModerateChannelID}");
						sb.AppendLine($"welcome_channel: {sconfig.WelcomeChannelID}");
						sb.AppendLine($"roll_channel: {sconfig.RollChannelID}");
						sb.AppendLine($"stats_channel: {sconfig.StatsChannelID}");
						sb.AppendLine($"record_channel: {sconfig.RecordChannelID}");
						sb.AppendLine($"general_rg_channel: {sconfig.GeneralRGChannelID}");
						sb.AppendLine($"welcome_message: {sconfig.WelcomeMessage}");
						sb.AppendLine($"line_message: {sconfig.LineMessage}");
						sb.AppendLine($"default_role: {sconfig.DefaultRoleID}");
                        sb.AppendLine($"master_role: {(sconfig.MasterRoleId.HasValue ? sconfig.MasterRoleId.Value.ToString() : "null")}");
						sb.AppendLine($"super_user_role: {(sconfig.SuperUserRoleId.HasValue ? sconfig.SuperUserRoleId.Value.ToString() : "null")}");
						sb.AppendLine($"swear_filter: {sconfig.SwearFilterEnabled}");
						sb.AppendLine($"swear_words: {(sconfig.SwearWords != null ? string.Join(',', sconfig.SwearWords) : "")}");
						sb.AppendLine($"predictions: {sconfig.PredictionsEnabled}");
                       sb.AppendLine($"roll_pictures: {sconfig.RollPicturesEnabled}");
						sb.AppendLine($"event_voice_channel: {sconfig.EventVoiceChannelID}");
						await command.RespondAsync(sb.ToString(), ephemeral: true);
						ScheduleDeleteOriginalResponse(command, delaySeconds: 60); // Увеличено время для чтения списка настроек
					}
					break;

                case "get":
					{
						if (string.IsNullOrWhiteSpace(keyOpt))
						{
							await command.RespondAsync("Укажите ключ настройки (например: moderation_channel).", ephemeral: true);
							return;
						}

						string result = keyOpt switch
						{
							"moderation_channel" => sconfig.ModerateChannelID.ToString(),
							"welcome_channel" => sconfig.WelcomeChannelID.ToString(),
							"roll_channel" => sconfig.RollChannelID.ToString(),
							"stats_channel" => sconfig.StatsChannelID.ToString(),
							"record_channel" => sconfig.RecordChannelID.ToString(),
							"welcome_message" => sconfig.WelcomeMessage ?? "",
							"line_message" => sconfig.LineMessage ?? "",
							"general_rg_channel" => sconfig.GeneralRGChannelID.ToString(),
							"default_role" => sconfig.DefaultRoleID.ToString(),
                          "master_role" => sconfig.MasterRoleId.HasValue ? sconfig.MasterRoleId.Value.ToString() : "",
							"super_user_role" => sconfig.SuperUserRoleId.HasValue ? sconfig.SuperUserRoleId.Value.ToString() : "",
							"swear_filter" => sconfig.SwearFilterEnabled.ToString(),
							"swear_words" => (sconfig.SwearWords != null ? string.Join(',', sconfig.SwearWords) : ""),
							"predictions" => sconfig.PredictionsEnabled.ToString(),
                            "roll_pictures" => sconfig.RollPicturesEnabled.ToString(),
                            "event_voice_channel" => sconfig.EventVoiceChannelID.ToString(),
							_ => "Неизвестный ключ"
						};

						await command.RespondAsync(result, ephemeral: true);
					}
					break;

                case "set":
                    {
                        if (string.IsNullOrWhiteSpace(keyOpt))
                        {
                            await command.RespondAsync("Укажите ключ настройки для установки.", ephemeral: true);
                            return;
                        }

                        switch (keyOpt)
                        {
                            case "moderation_channel":
                            case "roll_channel":
                            case "stats_channel":
                            case "welcome_channel":
                            case "general_rg_channel":
                            case "record_channel":
                            case "event_voice_channel":
                                {
                                    var requireVoice = keyOpt == "event_voice_channel";
                                    var id = await ResolveChannelIdAsync(guildId, channelOpt, valueOpt, requireVoice);
                                    if (!id.HasValue)
                                    {
                                        await command.RespondAsync(
                                            requireVoice
                                                ? $"Ошибка: не удалось найти голосовой канал на этом сервере по значению '{valueOpt ?? channelOpt?.ToString() ?? "(пусто)"}'."
                                                : $"Ошибка: не удалось найти канал на этом сервере по значению '{valueOpt ?? channelOpt?.ToString() ?? "(пусто)"}'.",
                                            ephemeral: true);
                                        return;
                                    }

                                    switch (keyOpt)
                                    {
                                        case "moderation_channel":
                                            sconfig.ModerateChannelID = id.Value;
                                            break;
                                        case "roll_channel":
                                            sconfig.RollChannelID = id.Value;
                                            break;
                                        case "stats_channel":
                                            sconfig.StatsChannelID = id.Value;
                                            break;
                                        case "welcome_channel":
                                            sconfig.WelcomeChannelID = id.Value;
                                            break;
                                        case "general_rg_channel":
                                            sconfig.GeneralRGChannelID = id.Value;
                                            break;
                                        case "record_channel":
                                            sconfig.RecordChannelID = id.Value;
                                            break;
                                        case "event_voice_channel":
                                            sconfig.EventVoiceChannelID = id.Value;
                                            break;
                                    }

                                    SaveServerConfigs();
                                    await command.RespondAsync($"{keyOpt} установлен: {id.Value}", ephemeral: true);
                                }
                                break;
                            case "welcome_message":
                                {
                                    sconfig.WelcomeMessage = valueOpt ?? "";
                                    SaveServerConfigs();
                                    await command.RespondAsync($"welcome_message установлен.", ephemeral: true);
                                }
                                break;
                            case "line_message":
                                {
                                    sconfig.LineMessage = valueOpt ?? "";
                                    SaveServerConfigs();
                                    await command.RespondAsync($"line_message установлен.", ephemeral: true);
                                }
                                break;
                            case "default_role":
                            case "master_role":
                                {
                                var guild = _client.GetGuild(guildId);
                                var roleId = guild == null ? null : ResolveRoleId(guild, valueOpt);
                                if (!roleId.HasValue)
                                {
                                    await command.RespondAsync($"Ошибка: не удалось найти роль на этом сервере по значению '{valueOpt ?? "(пусто)"}'.", ephemeral: true);
                                    return;
                                }

                                if (keyOpt == "default_role")
                                    sconfig.DefaultRoleID = roleId.Value;
                                else
                                    sconfig.MasterRoleId = roleId.Value;

                                SaveServerConfigs();
                                await command.RespondAsync($"{keyOpt} установлен: {roleId.Value}", ephemeral: true);
                                }
                                break;
							case "super_user_role":
								{
                                    var guild = _client.GetGuild(guildId);
                                    var roleId = guild == null ? null : ResolveRoleId(guild, valueOpt);
                                    if (!roleId.HasValue)
                                    {
                                        await command.RespondAsync($"Ошибка: не удалось найти роль на этом сервере по значению '{valueOpt ?? "(пусто)"}'.", ephemeral: true);
                                        return;
                                    }

                                    sconfig.SuperUserRoleId = roleId.Value;
                                    SaveServerConfigs();
                                    await command.RespondAsync($"super_user_role установлен: {roleId.Value}", ephemeral: true);
								}
								break;
                            case "swear_filter":
                                {
                                    bool state = false;
                                    if (toggleOpt != null)
                                        state = Convert.ToBoolean(toggleOpt);
                                    else if (!string.IsNullOrWhiteSpace(valueOpt) && bool.TryParse(valueOpt, out var b))
                                        state = b;

                                    sconfig.SwearFilterEnabled = state;
                                    SaveServerConfigs();
                                    await command.RespondAsync($"swear_filter установлен: {state}", ephemeral: true);
                                }
                                break;
                            case "predictions":
                            case "roll_pictures":
                                {
                                    bool state = false;
                                    if (toggleOpt != null)
                                        state = Convert.ToBoolean(toggleOpt);
                                    else if (!string.IsNullOrWhiteSpace(valueOpt) && bool.TryParse(valueOpt, out var b))
                                        state = b;

                                    if (keyOpt == "predictions")
                                        sconfig.PredictionsEnabled = state;
                                    else
                                        sconfig.RollPicturesEnabled = state;

                                    SaveServerConfigs();
                                    await command.RespondAsync($"{keyOpt} установлен: {state}", ephemeral: true);
                                }
                                break;
                            default:
                                await command.RespondAsync("Неизвестный ключ для установки.", ephemeral: true);
                                break;
                        }
                    }
                    break;

                case "reset":
                    {
                        if (_serverConfigs.ContainsKey(guildId))
                        {
                            _serverConfigs.Remove(guildId);
                            SaveServerConfigs();
                            await command.RespondAsync("Настройки сервера сброшены до значений по умолчанию.", ephemeral: true);
                        }
                        else
                        {
                            await command.RespondAsync("Настройки для этого сервера не найдены.", ephemeral: true);
                        }
                    }
                    break;

                default:
                    await command.RespondAsync("Неизвестное действие.", ephemeral: true);
                    break;
            }
        }

        private readonly SemaphoreSlim _logSemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Выполняет комплексную проверку всех систем бота
        /// </summary>
        private async Task<List<SystemHealthCheck>> PerformSystemHealthCheckAsync()
        {
            var checks = new List<SystemHealthCheck>();

            // 1. Проверка подключения к Discord
            checks.Add(new SystemHealthCheck
            {
                SystemName = "Discord Gateway",
                IsHealthy = _client?.ConnectionState == Discord.ConnectionState.Connected,
                Message = _client?.ConnectionState == Discord.ConnectionState.Connected 
                    ? $"Подключено ({_client.Latency}мс)" 
                    : $"Не подключено ({_client?.ConnectionState})"
            });

            // 2. Проверка доступности гильдий
            var guildsCount = _client?.Guilds?.Count ?? 0;
            checks.Add(new SystemHealthCheck
            {
                SystemName = "Серверы Discord",
                IsHealthy = guildsCount > 0,
                Message = guildsCount > 0 
                    ? $"Доступно: {guildsCount}" 
                    : "Нет доступных серверов"
            });

            // 3. Проверка конфигурации серверов
            var configuredServers = _serverConfigs?.Count ?? 0;
            checks.Add(new SystemHealthCheck
            {
                SystemName = "Конфигурация",
                IsHealthy = configuredServers > 0,
                Message = configuredServers > 0 
                    ? $"Настроено: {configuredServers}" 
                    : "Не настроено"
            });

            // 4. Проверка системы прогнозов
            var predEnabled = _predictionService != null;
            checks.Add(new SystemHealthCheck
            {
                SystemName = "Прогнозы",
                IsHealthy = predEnabled,
                Message = predEnabled 
                    ? "Активна" 
                    : "Не инициализирована"
            });

            // 5. Проверка Telegram для каждого сервера
            if (_telegramNotifier != null && _serverConfigs != null)
            {
                var telegramChecks = new List<string>();
                var anyEnabled = false;

                foreach (var config in _serverConfigs.Values)
                {
                    if (config.TelegramEnabled)
                    {
                        anyEnabled = true;
                        var guild = _client?.GetGuild(config.GuildID);
                        var guildName = guild?.Name ?? $"Guild{config.GuildID}";

                        try
                        {
                            var probeResult = await _telegramNotifier.ProbeAsync(config.GuildID, default);
                            // Сокращаем название сервера если слишком длинное
                            var shortName = guildName.Length > 20 ? guildName.Substring(0, 17) + "..." : guildName;

                            if (probeResult.Success)
                                telegramChecks.Add($"  • {shortName}: OK");
                            else
                            {
                                // Берём только первые 40 символов сообщения об ошибке
                                var errMsg = probeResult.Message.Length > 40 
                                    ? probeResult.Message.Substring(0, 37) + "..." 
                                    : probeResult.Message;
                                telegramChecks.Add($"  • {shortName}: {errMsg}");
                            }
                        }
                        catch (Exception ex)
                        {
                            var shortName = guildName.Length > 20 ? guildName.Substring(0, 17) + "..." : guildName;
                            var errMsg = ex.Message.Length > 30 ? ex.Message.Substring(0, 27) + "..." : ex.Message;
                            telegramChecks.Add($"  • {shortName}: Err: {errMsg}");
                        }
                    }
                }

                var telegramOverallHealthy = !anyEnabled || telegramChecks.Any(c => c.Contains("OK"));
                checks.Add(new SystemHealthCheck
                {
                    SystemName = "Telegram",
                    IsHealthy = telegramOverallHealthy,
                    Message = telegramChecks.Count == 0 
                        ? "Не настроено" 
                        : string.Join("\n", telegramChecks)
                });
            }

            // 6. Проверка голосовой системы начисления поинтов
            checks.Add(new SystemHealthCheck
            {
                SystemName = "Голосовые поинты",
                IsHealthy = _voicePointsService != null,
                Message = _voicePointsService != null 
                    ? "Активна" 
                    : "Не инициализирована"
            });

            // 7. Проверка хранилища поинтов
            checks.Add(new SystemHealthCheck
            {
                SystemName = "Хранилище поинтов",
                IsHealthy = _pointsService != null,
                Message = _pointsService != null 
                    ? "Активно" 
                    : "Не инициализировано"
            });

// 8. Проверка Google Sheets
{
    if (_config?.GoogleSheetsEnabled != true)
    {
        checks.Add(new SystemHealthCheck
        {
            SystemName = "Google Sheets",
            IsHealthy = true,
            Message = "Отключено в конфиге"
        });
    }
    else if (_googleSheetsService == null)
    {
        checks.Add(new SystemHealthCheck
        {
            SystemName = "Google Sheets",
            IsHealthy = false,
            Message = "Не инициализирован (проверь credentials и SpreadsheetId)"
        });
    }
    else
    {
        try
        {
            var probeResult = await _googleSheetsService.ProbeAsync().ConfigureAwait(false);
            checks.Add(new SystemHealthCheck
            {
                SystemName = "Google Sheets",
                IsHealthy = probeResult.Success,
                Message = probeResult.Message
            });
        }
        catch (Exception ex)
        {
            checks.Add(new SystemHealthCheck
            {
                SystemName = "Google Sheets",
                IsHealthy = false,
                Message = $"Ошибка проверки: {ex.Message}"
            });
        }
    }
}

// 9. Проверка музыкального сервиса (Lavalink)
if (_config.Music.Enabled)
{
    string musicMsg;
    bool musicHealthy;
    if (_lavalinkService is null)
    {
        musicHealthy = false;
        musicMsg = "Сервис не инициализирован";
    }
    else
    {
        var probeErr = await _lavalinkService.ProbeAsync();
        musicHealthy = probeErr is null;
        musicMsg = probeErr is null
            ? $"Lavalink доступен ({_config.Music.Host}:{_config.Music.Port})"
            : $"Недоступен: {probeErr}";
    }
    checks.Add(new SystemHealthCheck
    {
        SystemName = "Музыка (Lavalink)",
        IsHealthy = musicHealthy,
        Message = musicMsg,
    });
}

            return checks;
        }

        private async Task LogStartup(string message)
        {
            var logDirRaw = _config?.LogDirectory;
            var logDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDirRaw) ? "Logs" : logDirRaw);
            Directory.CreateDirectory(logDir);
			// Ежедневный лог запуска: StartupLog_yyyyMMdd.txt
			var dateSuffix = DateTime.Now.ToString("yyyyMMdd");
			string path = Path.Combine(logDir, $"StartupLog_{dateSuffix}.txt");

            await _logSemaphore.WaitAsync();
            try
            {
				// Ротация логов по размеру (5 МБ)
                if (File.Exists(path))
                {
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.Length > 5 * 1024 * 1024)
                    {
                        string archivedPath = Path.Combine(logDir, $"StartupLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                        File.Move(path, archivedPath);
                    }
                }

                await File.AppendAllTextAsync(path, $"[{DateTime.Now:dd-MM-yyyy HH:mm:ss}] {message}\n");

                // ТОЛЬКО В UI, не в консоль
                if (_uiStarted && _ui != null)
                {
                    _ui.AddLog(message);
                }
            }
            finally
            {
                _logSemaphore.Release();
            }
        }

        private async Task LogShutdownState(bool isRestart, string initiator)
        {
            var version = _config?.BotVersion ?? "0.0.0.0";
            var mode = isRestart ? "перезапуск" : "завершение работы";
            var message = isRestart
                ? $"Бот завершил текущий цикл работы. Режим: {mode}. Инициатор: {initiator}. Версия: {version}"
                : $"Бот завершил работу. Режим: {mode}. Инициатор: {initiator}. Версия: {version}";

            await LogStartup(message);
        }

        /// <summary>
        /// Синхронный sink для сервисов (ReconnectionService и др.).
        /// Запускает LogStartup в фоне — гарантирует запись в StartupLog и показ в UI.
        /// </summary>
        private void ServiceLogSink(string message) => _ = LogStartup(message);

        /// <summary>
        /// Возвращает отображаемый текст типа запуска
        /// </summary>
        private string GetStartupTypeDisplay()
        {
            return _currentStartupType switch
            {
                StartupType.Restart => "Перезапуск",
                StartupType.Reconnect => "Переподключение",
                _ => "Первичный запуск"
            };
        }

        private async Task LogError(string errorMessage)
        {
            var logDirRaw = _config?.LogDirectory;
            var logDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDirRaw) ? "Logs" : logDirRaw);
            Directory.CreateDirectory(logDir);

            // Логи ошибок теперь пишутся в ежедневные файлы вида ErrorLog_yyyyMMdd.txt,
            // чтобы после каждого ежедневного рестарта начинался новый лог.
            var dateSuffix = DateTime.Now.ToString("yyyyMMdd");
            string path = Path.Combine(logDir, $"ErrorLog_{dateSuffix}.txt");

            await _logSemaphore.WaitAsync();
            try
            {
                if (File.Exists(path))
                {
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.Length > 5 * 1024 * 1024)
                    {
                        string archivedPath = Path.Combine(logDir, $"ErrorLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                        File.Move(path, archivedPath);
                    }
                }

                await File.AppendAllTextAsync(path, $"[{DateTime.Now:dd-MM-yyyy HH:mm:ss}] {errorMessage}\n");

                if (_uiStarted && _ui != null)
                {
                    _ui.AddLog($"❌ {errorMessage}");
                }
            }
            finally
            {
                _logSemaphore.Release();
            }
        }

        private async Task LogInfo(string infoMessage)
        {
            var logDirRaw = _config?.LogDirectory;
            var logDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDirRaw) ? "Logs" : logDirRaw);
            Directory.CreateDirectory(logDir);

            // Информационные логи также разделяем по дням: InfoLog_yyyyMMdd.txt
            var dateSuffix = DateTime.Now.ToString("yyyyMMdd");
            string path = Path.Combine(logDir, $"InfoLog_{dateSuffix}.txt");

            await _logSemaphore.WaitAsync();
            try
            {
                if (File.Exists(path))
                {
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.Length > 5 * 1024 * 1024)
                    {
                        string archivedPath = Path.Combine(logDir, $"InfoLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                        File.Move(path, archivedPath);
                    }
                }

                await File.AppendAllTextAsync(path, $"[{DateTime.Now:dd-MM-yyyy HH:mm:ss}] {infoMessage}\n");

                if (_uiStarted && _ui != null)
                {
                    _ui.AddLog(infoMessage);
                }
            }
            finally
            {
                _logSemaphore.Release();
            }
        }

        private async Task UserJoined(SocketGuildUser user)
        {
            await LogInfo($"{user.Username} присоединился к серверу {user.Guild.Name}.");
            try
            {
                // Получаем конфигурацию сервера
                if (!_serverConfigs.TryGetValue(user.Guild.Id, out var config) || config.DefaultRoleID == 0)
                {
                    await LogInfo($"Для сервера {user.Guild.Name} не настроена роль по умолчанию");
                    return;
                }

                // Получаем роль
                var defaultRole = user.Guild.GetRole(config.DefaultRoleID);
                if (defaultRole == null)
                {
                    await LogInfo($"Роль с ID {config.DefaultRoleID} не найдена на сервере {user.Guild.Name}");
                    return;
                }

                // Пытаемся выдать роль
                try
                {
                    await user.AddRoleAsync(defaultRole);
                    await LogInfo($"Пользователю {user.Mention} выдана роль {defaultRole.Name}");

                    var welcomeChannel = _client.GetChannel(config.WelcomeChannelID) as IMessageChannel;
                    if (welcomeChannel != null)
                    {
                        await LogInfo($"Отправка приветственного сообщения в {welcomeChannel.Name}");

                        // Создаем EmbedBuilder
                        var embed = new EmbedBuilder()
                            .WithAuthor(new EmbedAuthorBuilder()
                                .WithName("Смотрящий за костром")
                                .WithIconUrl("https://media.discordapp.net/attachments/710469293996769405/1241391979477205192/1.png?ex=664a07df&is=6648b65f&hm=b8a054e85315f85feb24a756fed87bfffd8a004e6e32bf1eb77db58f41f9b62e&=&format=webp&quality=lossless"))
                            /*.WithDescription($"**{user.Guild.Name}** приветствует тебя, Путник {user.Mention}, проходи, присаживайся к нашему тёплому огню да расскажи откуда к нам!\n\n" +
                                             "Если нужно очутиться в каком-то определённом мире ||принять участие в какой-либо настольно-ролевой игре||, то обратитесь __напрямую к мастеру__ и он выдаст необходимую роль.\n\n" +
                                             "На сервере также действует несколько команд через `!`, которые работают только в следующих чатах: **флудилка** и **общий-ролевой-чат**, с важной информацией:\n" +
                                             "1. `!правила` — здесь описан свод правил, который действует на данном сервере;\n" +
                                             "2. `!ссылки` — здесь представлены ссылки на все социальные сети, где можно найти \"Костёр на распутье\";\n" +
                                             "3. `!запись` — здесь находится ссылка на документ, в котором вся ||(или почти вся)|| информация о том, как можно записывать игры, начиная от установки и заканчивая настройкой. К тому же там описаны базовые правила для чистоты записи.")*/
                            .WithDescription($"**{user.Guild.Name}** приветствует тебя, Путник {user.Mention}, проходи, присаживайся к нашему тёплому огню да расскажи откуда к нам!\n\n" +
                                                "Если нужно очутиться в каком-то определённом мире ||принять участие в какой-либо настольно-ролевой игре||, то обратитесь __напрямую к мастеру__ и он выдаст необходимую роль.\n\n" +
                                                "На сервере помимо команд через `/` также действует несколько команд через `!` с важной информацией:\n" +
                                                "   • `!правила` — здесь описан свод правил, который действует на данном сервере;\n" +
                                                "   • `!ссылки` — здесь представлены ссылки на все социальные сети, где можно найти \"Костёр на распутье\";\n" +
                                                "   • `!запись` — здесь находится ссылка на документ, в котором вся ||(или почти вся)|| информация о том, как можно записывать игры, начиная от установки и заканчивая настройкой. К тому же там описаны базовые правила для чистоты записи;\n" +
                                                "   • `!команды` — здесь указаны все дополнительные пасты.\n\n" +
                                                "*При возникновении вопросов по серверу обратитесь к @domen_ или @perekrestok_mirov *")
                            .WithColor(new Color(0xE67E22)) // Оранжевый цвет, как у огня
                            .WithImageUrl("https://media.discordapp.net/attachments/710469293996769405/1241392720883482674/fe5ff45b6397f151c147b890c29eaa26af6741f60a592d7baf3594ff7b424f24.gif?ex=664a0890&is=6648b710&hm=4b766b8801c0ad863731c4f3")
                            .WithFooter(new EmbedFooterBuilder()
                                .WithText("Приятного времяпрепровождения у костра!")
                            )
                            .WithCurrentTimestamp();

                        await welcomeChannel.SendMessageAsync(embed: embed.Build());
                    }
                    else
                    {
                        await LogError("Канал для приветствий не был найден.");
                    }
                }
                catch (Discord.Net.HttpException ex) when (((int?)ex.DiscordCode) == 50013)
                {
                    await LogError($"Ошибка: Недостаточно прав для выдачи роли {defaultRole.Name}");

                    // Уведомляем в канал модерации
                    var modChannel = user.Guild.GetTextChannel(config.ModerateChannelID);
                    if (modChannel != null)
                    {
                        await modChannel.SendMessageAsync(
                            $"⚠️ Боту не хватает прав для выдачи роли {defaultRole.Mention}. " +
                            $"Пожалуйста, переместите роль {defaultRole.Mention} выше роли бота.");
                    }
                }
                catch (Exception ex)
                {
                    await LogError($"Ошибка при выдаче роли: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                await LogError($"Ошибка! Текст ошибки: {ex.Message}");
            }
        }

        private Dictionary<string, string> LoadTextFromFile(string filePath)
        {
            var textBlocks = new Dictionary<string, string>();

            if (!File.Exists(filePath))
            {
                // Синхронная обработка ошибки, так как метод не async
                try
                {
                    // Используем .GetAwaiter().GetResult() для синхронного вызова асинхронного метода
                    LogError("Файл с текстом не найден.").GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка при логировании: {ex.Message}");
                }
                return textBlocks;
            }

            try
            {
                var lines = File.ReadAllLines(filePath);
                string currentKey = null;
                var currentText = new StringBuilder();

                foreach (var line in lines)
                {
                    if (line.StartsWith("---"))
                    {
                        continue;
                    }

                    if (line.StartsWith("!"))
                    {
                        if (currentKey != null)
                        {
                            textBlocks[currentKey] = currentText.ToString().Trim();
                            currentText.Clear();
                        }

                        currentKey = line;
                    }
                    else
                    {
                        currentText.AppendLine(line);
                    }
                }

                if (currentKey != null)
                {
                    textBlocks[currentKey] = currentText.ToString().Trim();
                }
            }
            catch (Exception ex)
            {
                try
                {
                    LogError($"Ошибка при загрузке файла: {ex.Message}").GetAwaiter().GetResult();
                }
                catch (Exception logEx)
                {
                    Console.WriteLine($"Ошибка при логировании: {logEx.Message}");
                }
            }

            return textBlocks;
        }
    }
}
