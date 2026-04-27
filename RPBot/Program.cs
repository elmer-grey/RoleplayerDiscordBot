using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using RPBot;
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
    public class ServerConfig
    {
        public ulong GuildID { get; set; }
        public ulong ModerateChannelID { get; set; }
        public ulong WelcomeChannelID { get; set; }
        public ulong GeneralRGChannelID { get; set; }
        public ulong RollChannelID { get; set; }
        public ulong StatsChannelID { get; set; }
        public ulong RecordChannelID { get; set; }
        public string WelcomeMessage { get; set; } = string.Empty;
        public string LineMessage { get; set; } = string.Empty;
        public ulong DefaultRoleID { get; set; }
		// Роль "суперпользователя" на сервере, имеющая расширенные права управления ботом
		public ulong? SuperUserRoleId { get; set; } = null;
        // Включить фильтр мата для этого сервера
        public bool SwearFilterEnabled { get; set; } = false;
        // Доп. список слов для фильтрации на уровне сервера (если пуст — используются BotConfig.DefaultSwearWords)
        public List<string> SwearWords { get; set; } = new List<string>();
		// Включены ли игровые прогнозы/ставки и начисление костяшек на этом сервере
		public bool PredictionsEnabled { get; set; } = true;
		// Голосовой канал события (event), в котором начисляются костяшки
		public ulong EventVoiceChannelID { get; set; }

        // === TELEGRAM (уведомления о событиях) ===
        public bool TelegramEnabled { get; set; } = false;
        public string? TelegramBotToken { get; set; } = null;
        public long TelegramChatId { get; set; } = 0;
           public int TelegramMessageThreadId { get; set; } = 0;
    }

        // Modal handling moved inside Program class

    public enum StartupType
    {
        FirstStart,
        Restart,
        Reconnect
    }

	public interface IBotController
	{
		bool ShouldExit { get; }
		bool ShouldRestart { get; }
		Task RestartAsync();
		Task StopAsync();

		// Управление конфигурациями серверов (доступно из UI)
		Task<Dictionary<ulong, ServerConfig>> GetAllServerConfigsAsync();
		Task<ServerConfig?> GetServerConfigAsync(ulong guildId);
		Task SetServerConfigValueAsync(ulong guildId, string key, string? value = null, ulong? channelId = null, bool? toggle = null);
		Task ResetServerConfigAsync(ulong guildId);
		Task ReloadServerConfigsAsync();
	}

	/// <summary>
	/// Результат проверки здоровья системы
	/// </summary>
	public class SystemHealthCheck
	{
		public string SystemName { get; set; } = string.Empty;
		public bool IsHealthy { get; set; }
		public string Message { get; set; } = string.Empty;
	}
 public partial class Program : IDisposable, IBotController
    {
        private DiscordSocketClient _client;
        private CommandService _commandService;
        private IServiceProvider _services;
        private CommandHandler _commandHandler = null!;
        private Dictionary<string, string> _textBlocks = null!;
        private readonly SemaphoreSlim _restartLock = new(1, 1);
        private bool _isDisposed;
        private bool _shouldExit = false;
        private bool _shouldRestart = false;

        private ReconnectionService? _reconnectionService;
        private ConnectionPredictor? _connectionPredictor;
        private StatusNotifier? _statusNotifier;
		private PointsService _pointsService;
      private PointsUserIndex _pointsUserIndex;
		private PredictionService? _predictionService;
		private VoicePointsService? _voicePointsService;
        private TelegramNotifier? _telegramNotifier;
        private EventAnnouncementStore? _eventAnnouncementStore;

        private readonly ConcurrentDictionary<string, SocketMessageComponent> _pendingBetUi = new();

        // ✅ НОВОЕ: Хранение промежуточных данных для двухшагового создания прогноза
        private readonly ConcurrentDictionary<string, (string title, int minutes)> _pendingPredictionCreate = new();

        private Task? _backgroundMonitoringTask;
		private CancellationTokenSource? _dailyRestartCts;
		private Task? _dailyRestartTask;
        private string _restartInitiator = "console";

        private TextWriter? _originalOut;
        private TextWriter? _originalErr;

        public static Action<string>? CommandLogSink { get; private set; }

        // Centralized cleanup for services to avoid leaks when recreating
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
            // Show ephemeral balance and a confirm button before modal
            // Check active prediction exists to avoid showing intermediate UI when none
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

            // Remember the ephemeral balance interaction so we can delete it as soon as the modal is submitted.
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

                var outcomePlaceholder = outcomeCount == 2 ? "1 или 2" : $"1 до {outcomeCount}";

                // ✅ ИСПРАВЛЕНО БАГ: Если список исходов слишком длинный (>45 символов), не вставляем в label
                string label = "Исход";
                var fullLabelLength = $"Исход ({outcomesList})".Length;

                if (fullLabelLength <= 45)
                {
                    label = $"Исход ({outcomesList})";
                    Console.WriteLine($"[PREDICTION] Bet modal label: {fullLabelLength} символов - вмещается");
                }
                else
                {
                    // Если список слишком длинный, добавляем подсказку в placeholder
                    outcomePlaceholder = $"Выберите 1-{outcomeCount}";
                    Console.WriteLine($"[PREDICTION] Bet modal label: {fullLabelLength} символов - СЛИШКОМ ДЛИННЫЙ, используем компактный формат");
                    Console.WriteLine($"[PREDICTION] Outcomes list: {outcomesList}");
                }

                modal = new ModalBuilder()
                    .WithTitle("Сделать ставку")
                    .WithCustomId($"pred_bet_modal:{guildId}")
                    .AddTextInput(label, "outcome", TextInputStyle.Short, placeholder: outcomePlaceholder, maxLength: 2)
                    .AddTextInput("Сумма", "amount", TextInputStyle.Short, placeholder: "Количество костяшек")
                    .Build();
            }

            await component.RespondWithModalAsync(modal);
            // Note: original ephemeral balance message will be deleted by ScheduleDeleteOriginalResponse
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
                // ✅ НОВОЕ: Проверка наличия активного события на голосовом канале
                if (!IsActiveEventOnChannel(guildId, channelId))
                {
                    await component.RespondAsync("⚠️ Активное событие в этом голосовом канале завершено или отсутствует. Создание прогноза невозможно.", ephemeral: true);
                    return;
                }

                // ✅ БАГ 8: Двойная проверка наличия активного прогноза
                var existingPrediction = _predictionService.GetActive(guildId);
                if (existingPrediction != null)
                {
                    await component.RespondAsync("На этом сервере уже есть активный прогноз. Дождитесь его завершения или отмените.", ephemeral: true);
                    return;
                }

                if (outcomesCount == 3)
                {
                    // ✅ Модал для 3 исходов (обновлённый)
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
                    // ✅ Первый модал для 5 исходов (название + время)
                    var modal = new ModalBuilder()
                        .WithTitle("Создать прогноз (шаг 1/2)")
                        .WithCustomId($"pred_create_step1:{guildId}:{channelId}")
                        .AddTextInput("Заголовок", "title", TextInputStyle.Short, placeholder: "Название прогноза", maxLength: 100)
                        .AddTextInput("Время (мин)", "duration_minutes", TextInputStyle.Short, placeholder: "От 1 до 60 минут", value: "3")
                        .Build();

                    await component.RespondWithModalAsync(modal);
                }

                // ✅ Modal отправлен, ephemeral кнопки будут автоматически удалены через 20 сек
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

                // Булевые флаги трактуем как явные значения из serverconfig
                SwearFilterEnabled = overrides.SwearFilterEnabled,
                PredictionsEnabled = overrides.PredictionsEnabled,
                EventVoiceChannelID = overrides.EventVoiceChannelID != 0 ? overrides.EventVoiceChannelID : defaults.EventVoiceChannelID,

                SwearWords = (overrides.SwearWords != null && overrides.SwearWords.Count > 0)
                    ? overrides.SwearWords
                    : defaults.SwearWords
            };
        }

        // Сохранение/загрузка конфигураций серверов
        private void SaveServerConfigs()
        {
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
                File.WriteAllText(resolved, json);
            }
            catch (Exception ex)
            {
                _ = LogError($"Ошибка сохранения serverconfigs: {ex.Message}");
            }
        }

        private void LoadServerConfigs()
        {
            try
            {
                var path = _serverConfigsPath ?? BotConfig.ResolvePath("serverconfigs.json");
                var resolved = BotConfig.ResolvePath(path);

                if (!File.Exists(resolved))
                    return;

                var json = File.ReadAllText(resolved);
                var options = new JsonSerializerOptions();
              var dict = JsonSerializer.Deserialize<Dictionary<ulong, ServerConfig>>(json, options);
                if (dict != null)
                {
                    // Рабочие конфиги серверов теперь целиком берём из файла
                    _serverConfigs = dict;

                    // Автодополнение serverconfigs.json новыми полями: если ключей не было в json,
                    // пересохраняем, чтобы они появились в файле.
                    var needsResave = false;
                    if (!json.Contains("\"TelegramEnabled\"", StringComparison.Ordinal) ||
                        !json.Contains("\"TelegramBotToken\"", StringComparison.Ordinal) ||
                     !json.Contains("\"TelegramChatId\"", StringComparison.Ordinal) ||
                        !json.Contains("\"TelegramMessageThreadId\"", StringComparison.Ordinal))
                    {
                        needsResave = true;
                    }

                    if (needsResave)
                    {
                        SaveServerConfigs();
                    }
                }

            }
            catch (Exception ex)
            {
                _ = LogError($"Ошибка загрузки serverconfigs: {ex.Message}");
            }
        }

        public Program()
        {
			var configRelativePath = Path.Combine("Settings", "config.json");
			var configResolvedPath = BotConfig.ResolvePath(configRelativePath);
			_config = BotConfig.Load(configRelativePath);
			_serverConfigsPath = BotConfig.ResolvePath(Path.Combine("Settings", "serverconfigs.json"));
            LoadServerConfigs();

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
			_reconnectionService = new ReconnectionService(_client);
			_connectionPredictor = new ConnectionPredictor(_reconnectionService, BotConfig.Current?.Prediction);
			_statusNotifier = new StatusNotifier(_client, _serverConfigs);
          _telegramNotifier = new TelegramNotifier(guildId =>
            {
                return _serverConfigs != null && _serverConfigs.TryGetValue(guildId, out var sc) ? sc : null;
            });
            _eventAnnouncementStore = new EventAnnouncementStore(Path.Combine(BotConfig.SettingsFolderName, "event_announcements.json"));

			// Сервисы для костяшек и игровых прогнозов
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

            // ПОДПИСКА НА СОБЫТИЯ СЕРВИСОВ
            _reconnectionService.OnDisconnectDetected += OnDisconnectDetected;
            _reconnectionService.OnReconnectStarted += OnReconnectStarted;
            _reconnectionService.OnReconnectCompleted += OnReconnectCompleted;
            // Подписываемся на запрос полного перезапуска, когда реконнекты зашкаливают
            _reconnectionService.OnFullRestartRequested += OnFullRestartRequested;
            _connectionPredictor.OnPredictionMade += OnPredictionMade;

            _services = new ServiceCollection()
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
                .AddSingleton<ModerationCommands>()
                .BuildServiceProvider();
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

			// Stop Discord client (best-effort)
			try { await _client.StopAsync(); } catch { }

			// Await background monitoring task to finish (with timeout)
			if (_backgroundMonitoringTask != null)
			{
				try
				{
					var t = await Task.WhenAny(_backgroundMonitoringTask, Task.Delay(5000));
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

			try { await _client.StopAsync(); } catch { }

			if (_backgroundMonitoringTask != null)
			{
				try
				{
					var t = await Task.WhenAny(_backgroundMonitoringTask, Task.Delay(5000));
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

			// Время перезагрузки берём только из config.json
			if (!TryParseTime(_config?.DailyRestartLocalTime, out var localTarget))
				throw new InvalidOperationException("Daily restart time is not configured. Set DailyRestartLocalTime in config.json.");

			// Если локальная TZ = Москва и включено предпочтение московского времени — используем его.
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

		// Утилита для перевода UTC-времени в локальное время по МСК.
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

		// Вспомогательный метод для внутренних сервисов (VoicePointsService, PredictionCommand)
		// для получения ServerConfig без копирования словаря.
		private ServerConfig? GetServerConfigInternal(ulong guildId)
		{
			if (_serverConfigs.TryGetValue(guildId, out var cfg))
				return cfg;
			return null;
		}

		public Task ReloadServerConfigsAsync()
		{
			// Перечитываем serverconfigs.json и пересобираем эффективные ServerConfigs
			LoadServerConfigs();
			return Task.CompletedTask;
		}

        public Task SetServerConfigValueAsync(ulong guildId, string key, string? value = null, ulong? channelId = null, bool? toggle = null)
        {
            if (!_serverConfigs.TryGetValue(guildId, out var sconfig))
            {
                sconfig = new ServerConfig { GuildID = guildId };
                _serverConfigs[guildId] = sconfig;
            }

            switch (key.ToLowerInvariant())
            {
                case "moderation_channel":
                    if (channelId.HasValue) sconfig.ModerateChannelID = channelId.Value;
                    else if (!string.IsNullOrWhiteSpace(value) && ulong.TryParse(value, out var mc)) sconfig.ModerateChannelID = mc;
                    break;
                case "roll_channel":
                    if (channelId.HasValue) sconfig.RollChannelID = channelId.Value;
                    else if (!string.IsNullOrWhiteSpace(value) && ulong.TryParse(value, out var rc)) sconfig.RollChannelID = rc;
                    break;
                case "stats_channel":
                    if (channelId.HasValue) sconfig.StatsChannelID = channelId.Value;
                    else if (!string.IsNullOrWhiteSpace(value) && ulong.TryParse(value, out var sc)) sconfig.StatsChannelID = sc;
                    break;
                case "record_channel":
                    if (channelId.HasValue) sconfig.RecordChannelID = channelId.Value;
                    else if (!string.IsNullOrWhiteSpace(value) && ulong.TryParse(value, out var rec)) sconfig.RecordChannelID = rec;
                    break;
                case "welcome_channel":
                    if (channelId.HasValue) sconfig.WelcomeChannelID = channelId.Value;
                    else if (!string.IsNullOrWhiteSpace(value) && ulong.TryParse(value, out var wc)) sconfig.WelcomeChannelID = wc;
                    break;
                case "general_rg_channel":
                    if (channelId.HasValue) sconfig.GeneralRGChannelID = channelId.Value;
                    else if (!string.IsNullOrWhiteSpace(value) && ulong.TryParse(value, out var grg)) sconfig.GeneralRGChannelID = grg;
                    break;
                case "welcome_message":
                    sconfig.WelcomeMessage = value ?? "";
                    break;
                case "line_message":
                    sconfig.LineMessage = value ?? "";
                    break;
                case "default_role":
                    if (!string.IsNullOrWhiteSpace(value) && ulong.TryParse(value, out var dr)) sconfig.DefaultRoleID = dr;
                    break;
				case "super_user_role":
					if (!string.IsNullOrWhiteSpace(value) && ulong.TryParse(value, out var su)) sconfig.SuperUserRoleId = su;
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
                case "event_voice_channel":
                    if (channelId.HasValue) sconfig.EventVoiceChannelID = channelId.Value;
                    else if (!string.IsNullOrWhiteSpace(value) && ulong.TryParse(value, out var evc)) sconfig.EventVoiceChannelID = evc;
                    break;
				default:
					break;
			}

			// Сразу сохраняем изменения serverconfig и пересобираем эффективные ServerConfigs
			SaveServerConfigs();

			// Валидация: если указаны channel/role - проверим, что они есть на сервере и залогируем предупреждения
            try
            {
                var guild = _client.GetGuild(guildId);
                if (guild != null)
                {
                    if (sconfig.ModerateChannelID != 0)
                    {
                        var ch = guild.GetTextChannel(sconfig.ModerateChannelID);
                        if (ch == null)
                            _ = LogInfo($"Предупреждение: moderation_channel {sconfig.ModerateChannelID} не найден на сервере {guildId}.");
                    }

                    if (sconfig.RollChannelID != 0)
                    {
                        var ch = guild.GetTextChannel(sconfig.RollChannelID);
                        if (ch == null)
                            _ = LogInfo($"Предупреждение: roll_channel {sconfig.RollChannelID} не найден на сервере {guildId}.");
                    }

                    if (sconfig.StatsChannelID != 0)
                    {
                        var ch = guild.GetTextChannel(sconfig.StatsChannelID);
                        if (ch == null)
                            _ = LogInfo($"Предупреждение: stats_channel {sconfig.StatsChannelID} не найден на сервере {guildId}.");
                    }

                    if (sconfig.WelcomeChannelID != 0)
                    {
                        var ch = guild.GetTextChannel(sconfig.WelcomeChannelID);
                        if (ch == null)
                            _ = LogInfo($"Предупреждение: welcome_channel {sconfig.WelcomeChannelID} не найден на сервере {guildId}.");
                    }


                    if (sconfig.GeneralRGChannelID != 0)
                    {
                        var ch = guild.GetTextChannel(sconfig.GeneralRGChannelID);
                        if (ch == null)
                            _ = LogInfo($"Предупреждение: general_rg_channel {sconfig.GeneralRGChannelID} не найден на сервере {guildId}.");
                    }

                    if (sconfig.DefaultRoleID != 0)
                    {
                        var role = guild.Roles.FirstOrDefault(r => r.Id == sconfig.DefaultRoleID);
                        if (role == null)
                            _ = LogInfo($"Предупреждение: роль {sconfig.DefaultRoleID} не найдена на сервере {guildId}.");
                    }

					if (sconfig.SuperUserRoleId.HasValue && sconfig.SuperUserRoleId.Value != 0)
					{
						var suRole = guild.Roles.FirstOrDefault(r => r.Id == sconfig.SuperUserRoleId.Value);
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
            return Task.CompletedTask;
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

                        // Корректно очистим старые сервисы (отпишем, shutdown, dispose)
                        CleanupServices();

                        // ПЕРЕСОЗДАЕМ СЕРВИСЫ С НОВЫМ КЛИЕНТОМ
                        _reconnectionService = new ReconnectionService(_client)
                        {
                            LogSink = msg => _ui?.AddLog(msg)
                        };
                        _connectionPredictor = new ConnectionPredictor(_reconnectionService, _config.Prediction);
                        _statusNotifier = new StatusNotifier(_client, ServerConfigs)
                        {
                            LogSink = msg => _ui?.AddLog(msg)
                        };
                        _statusNotifier.SetStartupContext(_currentStartupType, _startupReason);

                        var predictionsLogPath = BotConfig.ResolvePath(Path.Combine(_config.LogDirectory ?? "Logs", "predictions.log"));
                        _predictionService = new PredictionService(_client, _pointsService, predictionsLogPath);
                        _voicePointsService = new VoicePointsService(_client, _pointsService, GetServerConfigInternal, predictionsLogPath);


                        // ПЕРЕПОДПИСЫВАЕМСЯ
                        _reconnectionService.OnDisconnectDetected += OnDisconnectDetected;
                        _reconnectionService.OnReconnectStarted += OnReconnectStarted;
                        _reconnectionService.OnReconnectCompleted += OnReconnectCompleted;
                        _connectionPredictor.OnPredictionMade += OnPredictionMade;

                        // ОБНОВЛЯЕМ UI С НОВЫМ КЛИЕНТОМ
                        _ui?.UpdateServices(
                            _client,
                            _reconnectionService,
                            _connectionPredictor,
                            _statusNotifier
                        );
                    }

                    _commandHandler = new CommandHandler(_client, _config.GuildIDs);
                    CommandHandler.SetUI(_ui);

                    //await SetupDiscordEvents();
                    try
                    {
                        await _client.LoginAsync(TokenType.Bot, GetBotToken());
                        await _client.StartAsync();
                        await LogStartup(" Вход выполнен успешно.");

                        // Ждем готовности
                        await WaitForReadyAsync();

                        // Запускаем инициализацию с опросом
                        await InitializeBotWithProgress();

						// Ежедневный плановый перезапуск (время задаётся в config.json)
						StartDailyRestartScheduler();

                        // Запускаем фоновый мониторинг (с обёрткой для логирования ошибок)
                        _backgroundMonitoringTask = Task.Run(BackgroundMonitoringLoopWrapper);

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

        private async Task SetupDiscordEvents()
        {
            // Отписываемся от всего
            _client.Ready -= OnReady;
            _client.Disconnected -= OnDisconnected;
            _client.UserJoined -= UserJoined;
            _client.MessageReceived -= HandleCommandAsync;
            _client.SlashCommandExecuted -= OnSlashCommandExecuted;
            _client.SlashCommandExecuted -= BwonkCommand;
            _client.ModalSubmitted -= HandleModalSubmitted;
            _client.ButtonExecuted -= HandleButtonExecuted;
         _client.GuildScheduledEventCreated -= OnGuildScheduledEventCreated;
            _client.GuildScheduledEventUpdated -= OnGuildScheduledEventUpdated;
            _client.GuildScheduledEventStarted -= OnGuildScheduledEventStarted;
            _client.GuildScheduledEventCancelled -= OnGuildScheduledEventCancelled;
            _client.GuildScheduledEventCompleted -= OnGuildScheduledEventCompleted;

            // Подписываемся заново
            _client.Ready += OnReady;
            _client.Disconnected += OnDisconnected;
            _client.UserJoined += UserJoined;
            _client.MessageReceived += HandleCommandAsync;
            _client.SlashCommandExecuted += OnSlashCommandExecuted;
            _client.SlashCommandExecuted += BwonkCommand;
            _client.ModalSubmitted += HandleModalSubmitted;
            _client.ButtonExecuted += HandleButtonExecuted;
         _client.GuildScheduledEventCreated += OnGuildScheduledEventCreated;
            _client.GuildScheduledEventUpdated += OnGuildScheduledEventUpdated;
            _client.GuildScheduledEventStarted += OnGuildScheduledEventStarted;
            _client.GuildScheduledEventCancelled += OnGuildScheduledEventCancelled;
            _client.GuildScheduledEventCompleted += OnGuildScheduledEventCompleted;

            await LogStartup($"│   События Discord настроены    │");
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
                await GameSessionCommands.OnGuildScheduledEventCompleted(guildEvent, _client);
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
            if (entries.Count == 0)
                return;

            await LogStartup($"[EVENT][RESYNC] Начало синхронизации сохранённых анонсов: {entries.Count} записей.");

            var updated = 0;
            var removed = 0;
            var failed = 0;

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
                        _eventAnnouncementStore.Remove(entry.GuildId, entry.EventId);
                        removed++;
                        continue;
                    }

                    var status = guildEvent.Status switch
                    {
                        GuildScheduledEventStatus.Active => "started",
                        GuildScheduledEventStatus.Completed => "completed",
                        GuildScheduledEventStatus.Cancelled => "cancelled",
                          _ => "scheduled"
                    };

                     if (status == "scheduled")
                        {
                            continue;
                        }

                    await AnnounceGuildScheduledEventStatusChanged(guildEvent, status);
                    updated++;
                }
                catch (Exception ex)
                {
                    failed++;
                    await LogError($"[EVENT][RESYNC] Ошибка синхронизации guild={entry.GuildId}, event={entry.EventId}: {ex.Message}");
                }
            }

            await LogStartup($"[EVENT][RESYNC] Завершено: updated={updated}, removed={removed}, failed={failed}.");
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
                        $"📍 Где: {whereTextPlain}\n" +
                        (guildEvent.Creator != null ? $"👤 Создал: {guildEvent.Creator.Username}\n" : string.Empty) +
                        $"ℹ️ {mark}\n" +
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
                _client.Ready -= OnReadyOnce;
                readyTcs.TrySetResult(true);
                return Task.CompletedTask;
            }

            _client.Ready += OnReadyOnce;

            await Task.WhenAny(readyTcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        }

        private async Task BackgroundMonitoringLoop()
        {
            while (!_shouldExit)
            {
                // Ожидание между итерациями; прерываемся, если приложение завершает работу.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await LogStartup($"⚠️ BackgroundMonitoring: delay error: {ex.Message}");
                    await Task.Delay(500);
                    continue;
                }

                // Локальные копии ссылок — чтобы избежать гонок с очищением полей в другом потоке
                var predictor = _connectionPredictor;
                var client = _client;
                var recon = _reconnectionService;

                // Анализ/прогноз — только если predictor доступен
                if (predictor != null)
                {
                    try
                    {
                        var prediction = await predictor.AnalyzeAndPredict();
                        // TODO: использовать prediction при необходимости
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

                // Проверка состояния клиента — только если client доступен
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

        private async Task BackgroundMonitoringLoopWrapper()
        {
            try
            {
                await BackgroundMonitoringLoop();
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
            }

            // Ensure file exists even if dictionary is still empty at first ready tick.
            if (changed || !File.Exists(_serverConfigsPath))
                SaveServerConfigs();
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
            foreach (var line in BuildStartupBox(title, lines))
            {
                await LogStartup(line);
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
                    File.Exists(configPath) ? "config.json already exists" : "config.json created by BotConfig.Load",
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

                foreach (var line in BuildStartupBox("ЭТАП 0/4: ПЕРВИЧНАЯ ИНИЦИАЛИЗАЦИЯ SETTINGS", lines))
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
                await LogStartupBoxAsync("ЭТАП 1/4: РЕГИСТРАЦИЯ КОМАНД", stage1Lines);

                // ЭТАП 2: Активация обработчиков
                await SetupDiscordEvents();
                await LogStartupBoxAsync("ЭТАП 2/4: АКТИВАЦИЯ ОБРАБОТЧИКОВ", new[]
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
                await LogStartupBoxAsync("ЭТАП 3/4: СИНХРОНИЗАЦИЯ", stage3Lines);

                // ЭТАП 4: ПРОВЕРКА СИСТЕМ И ОТПРАВКА СТАТУСОВ
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

                await LogStartupBoxAsync("ЭТАП 4/4: ПРОВЕРКА И ОТПРАВКА СТАТУСОВ", stage4Lines);

                // ФИНАЛ
                _fullReadyTime = DateTime.UtcNow;
                // Защита: если событие Ready не сработало и _readyTime остался MinValue,
                // используем время старта инициализации как начало, чтобы не получить отрицательное время.
                var startTime = _readyTime == DateTime.MinValue ? _startupTime : _readyTime;
                var initTime = (_fullReadyTime - startTime).TotalSeconds;
                _initializationCompleted = true;

                _ui?.EnableInput();

                // УВЕДОМЛЕНИЕ В UI
                _ui?.ShowSystemReady(
                    _client.CurrentUser.Username,
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
                        try { _client.GuildScheduledEventCreated -= OnGuildScheduledEventCreated; } catch (Exception ex) { Console.WriteLine($"Error unsubscribing GuildScheduledEventCreated: {ex}"); }
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
                    await new GameSessionCommands(_client).HandleEditModal(modal);
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
                    await new GameSessionCommands(_client).HandleControlButton(component);
                    break;

                case "no_stats":
                case "general_stats":
                case "detailed_stats":
                    await new GameSessionCommands(_client).HandleStatsButton(component);
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

            var (fludChannelId, rollChannelId, generalRGChannelID, responseMessage, emoji, lineMessages, emoteKappa, emoteAga) = GetResponseData(message);

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

            if (message.Content.ToLower().Contains("привет, ролевой бот") && message.Channel.Id == fludChannelId)
            {
                await HandleGreetingCommand(user, message, responseMessage, emoji);
            }

            if ((message.Content.ToLower() == "line" || message.Content.ToLower() == "ход") && message.Channel.Id == rollChannelId)
            {
                await HandleLineCommand(user, message, lineMessages);
            }

            // Управление ботом через чат: только участники с ролью "Хранители"
            if (user is SocketGuildUser guildUser)
            {
                var hasKeeperRole = guildUser.Roles.Any(r => string.Equals(r.Name, "Хранители", StringComparison.OrdinalIgnoreCase));

                if (hasKeeperRole)
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

        private (ulong welcomeChannelId, ulong rollChannelId, ulong generalRGChannelID, string? responseMessage, string? emoji, string? lineMessages, string? emoteKappa, string? emoteAga) GetResponseData(SocketMessage message)
        {
            var channel = message.Channel as SocketGuildChannel;
			if (channel == null || !_serverConfigs.TryGetValue(channel.Guild.Id, out var config))
            {
                return (0, 0, 0, null, null, null, null, null);
            }

            // Для тестового сервера
            if (channel.Guild.Id == 1288192593137635359)
            {
                return (config.WelcomeChannelID, config.RollChannelID, config.GeneralRGChannelID, config.WelcomeMessage, "👋", config.LineMessage,
                    "<:kappa:1333879110602326046>", "<:agakakskagesh:1333878999977431174>");
            }
            // Для основного сервера
            else if (channel.Guild.Id == 295189463376855040)
            {
                return (config.WelcomeChannelID, config.RollChannelID, config.GeneralRGChannelID, config.WelcomeMessage, "👋", config.LineMessage,
                    "<:kappa:1100150992428871720>", "<:Agakakskagesh:1316461730569916557>");
            }

            return (0, 0, 0, null, null, null, null, null);
        }

        private async Task HandleGreetingCommand(SocketGuildUser user, SocketMessage message, string responseMessage, string emoji)
        {

            await message.AddReactionAsync(new Emoji(emoji));

            if (user.Username == "perekrestok_mirov" || user.Username == "domen_")
            {
                int reportCount = GetBugReportCounter();
                await message.Channel.SendMessageAsync($"Приветствую тебя, Админ. Все системы в норме. Отчётов о неисправности: {reportCount}");
            }
            else
            {
                var formattedMessage = responseMessage.Replace("{user.Mention}", user.Mention);
                var responseMessageObj = await message.Channel.SendMessageAsync(formattedMessage);
                await responseMessageObj.AddReactionAsync(new Emoji("✅"));
            }
            await LogInfo("Приветствие с ботом");
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
						sb.AppendLine("  используйте либо параметр channel (выбор канала из списка), либо value с числовым ID канала.");
						sb.AppendLine("  Если указаны оба, приоритет у channel.");
						sb.AppendLine("- Для ролей (default_role, super_user_role) указывайте ID роли в value.");
						sb.AppendLine("- Для ролей (default_role, super_user_role) указывайте ID роли в value.");
						sb.AppendLine("- для логических переключателей (swear_filter, predictions) используйте toggle:true/false или value:true/false.");
						sb.AppendLine("- Для event_voice_channel укажите ID голосового канала в value.");
						sb.AppendLine("- Для текстовых параметров (welcome_message, line_message) используйте value с текстом.");
						sb.AppendLine();
						sb.AppendLine("Примеры:");
						sb.AppendLine("/settings action:set key:moderation_channel channel:#модерация");
						sb.AppendLine("/settings action:set key:moderation_channel value:123456789012345678");
						sb.AppendLine("/settings action:set key:default_role value:123456789012345678");
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
						sb.AppendLine($"super_user_role: {(sconfig.SuperUserRoleId.HasValue ? sconfig.SuperUserRoleId.Value.ToString() : "null")}");
						sb.AppendLine($"swear_filter: {sconfig.SwearFilterEnabled}");
						sb.AppendLine($"swear_words: {(sconfig.SwearWords != null ? string.Join(',', sconfig.SwearWords) : "")}");
						sb.AppendLine($"predictions: {sconfig.PredictionsEnabled}");
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
							"super_user_role" => sconfig.SuperUserRoleId.HasValue ? sconfig.SuperUserRoleId.Value.ToString() : "",
							"swear_filter" => sconfig.SwearFilterEnabled.ToString(),
							"swear_words" => (sconfig.SwearWords != null ? string.Join(',', sconfig.SwearWords) : ""),
							"predictions" => sconfig.PredictionsEnabled.ToString(),
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
                                {
                                    ulong id = 0;
                                    if (channelOpt != null)
                                        id = Convert.ToUInt64(channelOpt);
                                    else if (!string.IsNullOrWhiteSpace(valueOpt) && ulong.TryParse(valueOpt, out var v))
                                        id = v;

                                    // Валидация: канал существует и принадлежит данной гильдии
                                    var guild = _client.GetGuild(guildId);
                                    if (id != 0)
                                    {
                                        var chan = await _client.GetChannelAsync(id) as SocketGuildChannel;
                                        if (chan == null || chan.Guild.Id != guildId)
                                        {
                                            await command.RespondAsync($"Ошибка: указанный канал не найден или не принадлежит этому серверу: {id}", ephemeral: true);
                                            return;
                                        }
                                    }

                                    sconfig.ModerateChannelID = id;
                                    SaveServerConfigs();
                                    await command.RespondAsync($"moderation_channel установлен: {id}", ephemeral: true);
                                }
                                break;
                            case "roll_channel":
                                {
                                    ulong id = 0;
                                    if (channelOpt != null)
                                        id = Convert.ToUInt64(channelOpt);
                                    else if (!string.IsNullOrWhiteSpace(valueOpt) && ulong.TryParse(valueOpt, out var v))
                                        id = v;
                                    var guild = _client.GetGuild(guildId);
                                    if (id != 0)
                                    {
                                        var chan = await _client.GetChannelAsync(id) as SocketGuildChannel;
                                        if (chan == null || chan.Guild.Id != guildId)
                                        {
                                            await command.RespondAsync($"Ошибка: указанный канал не найден или не принадлежит этому серверу: {id}", ephemeral: true);
                                            return;
                                        }
                                    }

                                    sconfig.RollChannelID = id;
                                    SaveServerConfigs();
                                    await command.RespondAsync($"roll_channel установлен: {id}", ephemeral: true);
                                }
                                break;
                            case "stats_channel":
                                {
                                    ulong id = 0;
                                    if (channelOpt != null)
                                        id = Convert.ToUInt64(channelOpt);
                                    else if (!string.IsNullOrWhiteSpace(valueOpt) && ulong.TryParse(valueOpt, out var v))
                                        id = v;
                                    var guild = _client.GetGuild(guildId);
                                    if (id != 0)
                                    {
                                        var chan = await _client.GetChannelAsync(id) as SocketGuildChannel;
                                        if (chan == null || chan.Guild.Id != guildId)
                                        {
                                            await command.RespondAsync($"Ошибка: указанный канал не найден или не принадлежит этому серверу: {id}", ephemeral: true);
                                            return;
                                        }
                                    }

                                    sconfig.StatsChannelID = id;
                                    SaveServerConfigs();
                                    await command.RespondAsync($"stats_channel установлен: {id}", ephemeral: true);
                                }
                                break;
                            case "welcome_channel":
                                {
                                    ulong id = 0;
                                    if (channelOpt != null)
                                        id = Convert.ToUInt64(channelOpt);
                                    else if (!string.IsNullOrWhiteSpace(valueOpt) && ulong.TryParse(valueOpt, out var v))
                                        id = v;
                                    var guild = _client.GetGuild(guildId);
                                    if (id != 0)
                                    {
                                        var chan = await _client.GetChannelAsync(id) as SocketGuildChannel;
                                        if (chan == null || chan.Guild.Id != guildId)
                                        {
                                            await command.RespondAsync($"Ошибка: указанный канал не найден или не принадлежит этому серверу: {id}", ephemeral: true);
                                            return;
                                        }
                                    }

                                    sconfig.WelcomeChannelID = id;
                                    SaveServerConfigs();
                                    await command.RespondAsync($"welcome_channel установлен: {id}", ephemeral: true);
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
                            case "general_rg_channel":
                                {
                                    ulong id = 0;
                                    if (channelOpt != null)
                                        id = Convert.ToUInt64(channelOpt);
                                    else if (!string.IsNullOrWhiteSpace(valueOpt) && ulong.TryParse(valueOpt, out var v))
                                        id = v;

                                    var guild = _client.GetGuild(guildId);
                                    if (id != 0)
                                    {
                                        var chan = await _client.GetChannelAsync(id) as SocketGuildChannel;
                                        if (chan == null || chan.Guild.Id != guildId)
                                        {
                                            await command.RespondAsync($"Ошибка: указанный канал не найден или не принадлежит этому серверу: {id}", ephemeral: true);
                                            return;
                                        }
                                    }

                                    sconfig.GeneralRGChannelID = id;
                                    SaveServerConfigs();
                                    await command.RespondAsync($"general_rg_channel установлен: {id}", ephemeral: true);
                                }
                                break;
                            case "default_role":
                                {
                                    if (!string.IsNullOrWhiteSpace(valueOpt) && ulong.TryParse(valueOpt, out var v))
                                    {
                                        var guild = _client.GetGuild(guildId);
                                        var role = guild?.Roles.FirstOrDefault(r => r.Id == v);
                                        if (role == null)
                                        {
                                            await command.RespondAsync($"Ошибка: роль с ID {v} не найдена на этом сервере.", ephemeral: true);
                                            return;
                                        }

                                        sconfig.DefaultRoleID = v;
                                        SaveServerConfigs();
                                        await command.RespondAsync($"default_role установлен: {v}", ephemeral: true);
                                    }
                                    else
                                    {
                                        await command.RespondAsync("Ошибка: укажите ID роли числом.", ephemeral: true);
                                    }
                                }
                                break;
							case "super_user_role":
								{
									if (!string.IsNullOrWhiteSpace(valueOpt) && ulong.TryParse(valueOpt, out var v))
									{
										var guild = _client.GetGuild(guildId);
										var role = guild?.Roles.FirstOrDefault(r => r.Id == v);
										if (role == null)
										{
											await command.RespondAsync($"Ошибка: роль с ID {v} не найдена на этом сервере.", ephemeral: true);
											return;
										}

										sconfig.SuperUserRoleId = v;
										SaveServerConfigs();
										await command.RespondAsync($"super_user_role установлен: {v}", ephemeral: true);
									}
									else
									{
										await command.RespondAsync("Ошибка: укажите ID роли числом.", ephemeral: true);
									}
								}
								break;
                            case "event_voice_channel":
                                {
                                    ulong id = 0;
                                    if (channelOpt != null)
                                        id = Convert.ToUInt64(channelOpt);
                                    else if (!string.IsNullOrWhiteSpace(valueOpt) && ulong.TryParse(valueOpt, out var v))
                                        id = v;

                                    if (id != 0)
                                    {
                                        var chan = await _client.GetChannelAsync(id) as SocketGuildChannel;
                                        if (chan == null || chan.Guild.Id != guildId || chan is not SocketVoiceChannel)
                                        {
                                            await command.RespondAsync($"Ошибка: укажите голосовой канал этого сервера: {id}", ephemeral: true);
                                            return;
                                        }
                                    }

                                    sconfig.EventVoiceChannelID = id;
                                    SaveServerConfigs();
                                    await command.RespondAsync($"event_voice_channel установлен: {id}", ephemeral: true);
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

    public class RollDiceCommands : ModuleBase<SocketCommandContext>
    {
        private static readonly SemaphoreSlim _sessionSemaphore = new(1, 1);
        private string ValidateRollInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "Ввод не может быть пустым.";

            if (!input.Contains('d', StringComparison.OrdinalIgnoreCase))
                return $"Введено `{input}`. Ввод должен содержать символ `d` (например, `2d6`).";

            var parts = input.Split(new[] { 'd', '+', '-' }, StringSplitOptions.RemoveEmptyEntries);

            // Проверяем количество частей
            if (parts.Length < 1 || parts.Length > 3)
                return $"Введено `{input}`. Некорректное количество параметров. Используйте формат `XdY`, `dY`, `XdY+Z`, `XdY-Z`, `Xd[min,max]+Z` или `d[min,max]-Z`.";

            // Проверяем, что все части являются числами
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out _) && !part.StartsWith('[') && !part.EndsWith(']'))
                    return $"Некорректное значение: `{part}`. Ожидается число или диапазон в формате `[min,max]`.";
            }

            // Проверяем диапазоны (если есть)
            if (input.Contains('[') || input.Contains(']'))
            {
                var rangePattern = @"\[\d+,\d+\]";
                if (!Regex.IsMatch(input, rangePattern))
                    return $"Введено `{input}`. Некорректный формат диапазона. Используйте `[min,max]`, где `min` и `max` — числа.";
            }

            // Если всё в порядке, возвращаем null
            return null;
        }

        /// <summary>
        /// Извлекает тип куба из строки ввода (например, из "2d6" получает "d6", из "3d20" получает "d20")
        /// </summary>
        private string ExtractDiceType(string input)
        {
            try
            {
                // Ищем паттерн "dX" где X - число
                var match = Regex.Match(input, @"d(\d+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var diceValue = match.Groups[1].Value; // Извлекаем число после "d"
                    return $"d{diceValue}"; // Возвращаем "d6", "d20", и т.д.
                }

                // Если паттерн не найден, возвращаем "d6" по умолчанию
                return "d6";
            }
            catch
            {
                return "d6"; // По умолчанию d6 при любой ошибке
            }
        }

        [Command("roll")]
        public async Task RollDice(SocketSlashCommand command, string input)
        {
            await command.DeferAsync();
            var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
            var channelId = command.Channel.Id;

            // Получаем ID канала статистики из конфига
           // Получаем ID канала статистики из конфига
            var statsChannelId = Program.ServerConfigs.TryGetValue(guildId.Value, out var cfg)
                ? cfg.StatsChannelID
                : 0UL;

          // Проверяем, сделан ли бросок в канале статистики
            bool isStatsChannel = channelId == statsChannelId && statsChannelId != 0;

            Console.WriteLine($"\nБыло введено условие: {input}");
            var user = command.User as SocketGuildUser;
            if (user == null)
            {
                await command.FollowupAsync("Не удалось получить информацию о пользователе.");
                return;
            }

            var _input = input.Trim();

            // Проверяем ввод
            var errorMessage = ValidateRollInput(_input);
            if (errorMessage != null)
            {
                await command.FollowupAsync($"Ошибка: {errorMessage}");
                Console.WriteLine($"Предупреждение: Был введён неверный формат. Ошибка: {errorMessage}");
                return;
            }

            // АВТОМАТИЧЕСКОЕ ОПРЕДЕЛЕНИЕ ТИПА КУБА ИЗ INPUT
            // Извлекаем тип куба (например, из "2d6" получаем "d6", из "1d20" получаем "d20")
            string diceType = ExtractDiceType(_input);

            var match = Regex.Match(_input, @"^(?:(?:(\d*)d(\d+)|d(\d+))([+-]\d+)?$)", RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                await command.FollowupAsync("Неверный формат! Используйте `XdY`, `dY`, `XdY+Z` или `XdY-Z`, где `X`, `Y`, `Z` — строго больше 0.");
                Console.WriteLine("Предупреждение: Был введён неверный формат.");
                return;
            }

            int count = 1;
            int max = 1;
            int modifier = 0;

            if (match.Groups[1].Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                count = int.Parse(match.Groups[1].Value);
                max = int.Parse(match.Groups[2].Value);
                if (match.Groups[4].Success) modifier = int.Parse(match.Groups[4].Value);
            }
            else if (match.Groups[2].Success)
            {
                max = int.Parse(match.Groups[2].Value);
                if (match.Groups[4].Success) modifier = int.Parse(match.Groups[4].Value);
            }

            if (count <= 0 || max <= 0)
            {
                await command.FollowupAsync("Количество бросков и верхняя граница должны быть больше нуля.", ephemeral: true);
                return;
            }
            if (count > 10)
            {
                await command.FollowupAsync("Давайте сильно не наглеть? 10 бросков - это максимум.", ephemeral: false);
                return;
            }

            // Если бросок в канале статистики, проверяем активные сессии
            if (isStatsChannel)
            {
                await _sessionSemaphore.WaitAsync();
                try
                {
                    if (GameSessionCommands._sessions.TryGetValue(guildId.Value, out var sessions))
                    {
                        // Находим все сессии с включенной записью бросков
                        var activeSessions = sessions.Where(s =>
                            !s.Value.IsStopped &&
                            s.Value.TrackRolls).ToList();

                        if (activeSessions.Any())
                        {
                            // Проверяем, есть ли сессии на паузе
                            var pausedSessions = activeSessions.Where(s => s.Value.IsPaused).ToList();
                            if (pausedSessions.Any())
                            {
                                await command.FollowupAsync(
                                    $"Игра **{pausedSessions.First().Value.GameName}** на паузе. Броски не учитываются.",
                                    ephemeral: false);
                                _ = Task.Delay(5000).ContinueWith(async _ => await command.DeleteOriginalResponseAsync());
                                return;
                            }
                        }
                    }
                }
                finally
                {
                    _sessionSemaphore.Release();
                }
            }

            Random random = new Random();
            List<int> results = Enumerable.Range(0, count)
                .Select(_ => random.Next(1, max + 1))
                .ToList();

            // ✅ УЛУЧШЕНО: Попытка найти картинку для ЛЮБОГО куба (не только d20)
            // Сначала пытаемся вывести с картинками, если не найдём - fallback на plaintext

            var numbersDir = BotConfig.ResolvePath(BotConfig.Current?.NumbersDirectory ?? "Numbers");
            var diceSubfolder = Path.Combine(numbersDir, diceType);
            bool hasImages = Directory.Exists(diceSubfolder);

            // Если бросок в канале статистики, проверяем активные сессии и записываем броски
            if (isStatsChannel)
            {
                await _sessionSemaphore.WaitAsync();
                try
                {
                    if (GameSessionCommands._sessions.TryGetValue(guildId.Value, out var sessions))
                    {
                        var activeSessions = sessions.Where(s =>
                            !s.Value.IsStopped &&
                            !s.Value.IsPaused &&
                            s.Value.TrackRolls).ToList();

                        foreach (var session in activeSessions)
                        {
                            foreach (var result in results)
                            {
                                session.Value.Rolls.Add(new RollStatistic
                                {
                                    PlayerName = command.User.GlobalName,
                                    RollValue = result,
                                    DiceType = diceType  // ✅ ДОБАВЛЕНО: Тип куба
                                });
                            }
                        }
                    }
                }
                finally
                {
                    _sessionSemaphore.Release();
                }
            }

            // ✅ НОВОЕ: Попытка вывести с картинками для ЛЮБОГО куба
            if (hasImages && modifier == 0)  // Картинки только для чистых бросков без модификаторов
            {
                if (count == 1)
                {
                    var result = results[0];
                    var filePath = Path.Combine(diceSubfolder, $"{result}.png");

                    if (File.Exists(filePath))
                    {
                        var embed = new EmbedBuilder()
                            .WithImageUrl($"attachment://{Path.GetFileName(filePath)}")
                            .WithColor(GetGradientColor(result, 1, max))
                            .Build();
                        await command.FollowupWithFileAsync(filePath, embed: embed);
                        return;
                    }
                }
                else
                {
                    var embeds = new List<Embed>();
                    var files = new List<FileAttachment>();
                    var textResults = new List<string>();

                    for (int i = 0; i < results.Count; i++)
                    {
                        var result = results[i];
                        var filePath = Path.Combine(diceSubfolder, $"{result}.png");

                        if (File.Exists(filePath))
                        {
                            var uniqueFileName = $"{result}_{i + 1}.png";
                            files.Add(new FileAttachment(filePath, uniqueFileName));
                            embeds.Add(new EmbedBuilder()
                                .WithImageUrl($"attachment://{uniqueFileName}")
                                .WithColor(GetGradientColor(result, 1, max))
                                .Build());
                        }
                        else
                        {
                            textResults.Add(result.ToString());
                        }
                    }

                    if (files.Count > 0)
                    {
                        var combinedMessage = files.Count switch
                        {
                            2 => "Результаты броска с помехой/преимуществом:",
                            _ => $"Результаты {files.Count} бросков:"
                        };

                        if (textResults.Count > 0)
                        {
                            combinedMessage += $"\n(Без картинок: {string.Join(", ", textResults)})";
                        }

                        await command.FollowupWithFilesAsync(
                            attachments: files,
                            text: combinedMessage,
                            embeds: embeds.ToArray());
                        return;
                    }
                }
            }

            // ✅ Fallback: Plaintext вывод (если нет картинок или есть модификатор)
            var resultMessage = new StringBuilder();
            var consoleMessage = new StringBuilder();

            if (count == 1)
            {
                var rolledValue = results[0];
                int finalValue = rolledValue + modifier;

                resultMessage.AppendLine("__**Результат броска**__");
                resultMessage.AppendLine("```plaintext");
                if (modifier != 0)
                {
                    resultMessage.AppendLine($"Выпавшее значение: {rolledValue}");
                    resultMessage.AppendLine($"Модификатор: {modifier}");
                    resultMessage.AppendLine($"Полученное значение: {finalValue}\n");
                }
                else
                {
                    resultMessage.AppendLine($"Полученное значение: {rolledValue}\n");
                }
                resultMessage.Append("```");
            }
            else
            {
                resultMessage.AppendLine("__**Результаты бросков**__");
                resultMessage.AppendLine("```plaintext");
                for (int i = 0; i < results.Count; i++)
                {
                    var rolledValue = results[i];
                    int finalValue = rolledValue + modifier;
                    if (modifier != 0)
                    {
                        resultMessage.AppendLine($"Бросок {i + 1}: Выпавшее значение: {rolledValue}");
                        resultMessage.AppendLine($"Модификатор: {modifier}");
                        resultMessage.AppendLine($"Полученное значение: {finalValue} \n");
                    }
                    else
                    {
                        resultMessage.AppendLine($"Бросок {i + 1}: Полученное значение: {rolledValue}\n");
                    }
                }
                resultMessage.Append("```");
            }

            await command.FollowupAsync(resultMessage.ToString());
            Console.WriteLine(resultMessage.ToString());
        }

        [Command("roll20")]
        public async Task Roll20(SocketSlashCommand command)
        {
            await command.DeferAsync();
            var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
            var channelId = command.Channel.Id;

            if (guildId == null)
            {
                await command.FollowupAsync("Команда доступна только на сервере.");
                return;
            }

            // Получаем ID канала статистики из конфига
            var statsChannelId = Program.ServerConfigs.TryGetValue(guildId.Value, out var config)
                ? config.StatsChannelID
                : 0;

            // Если бросок сделан в канале статистики
            bool isStatsChannel = channelId == statsChannelId;

            Random random = new Random();
            int result = random.Next(1, 21);

            // Если это канал статистики, проверяем сессии
            if (isStatsChannel)
            {
                await _sessionSemaphore.WaitAsync();
                try
                {
                    if (GameSessionCommands._sessions.TryGetValue(guildId.Value, out var sessions))
                    {
                        // Находим ВСЕ сессии с включённой записью бросков (TrackRolls = true)
                        var sessionsWithRolls = sessions.Where(s =>
                            s.Value.TrackRolls &&
                            !s.Value.IsStopped).ToList();

                        if (sessionsWithRolls.Any())
                        {
                            // Проверяем, есть ли сессии на паузе
                            var pausedSessions = sessionsWithRolls.Where(s => s.Value.IsPaused).ToList();
                            if (pausedSessions.Any())
                            {
                                // Выводим уведомление о паузе с названием первой найденной сессии
                                await command.FollowupAsync(
                                    $"Игра **{pausedSessions.First().Value.GameName}** на паузе. Броски не учитываются.",
                                    ephemeral: false
                                );
                                _ = Task.Delay(5000).ContinueWith(async _ => await command.DeleteOriginalResponseAsync());
                                return;
                            }

                            foreach (var session in sessionsWithRolls.Where(s => !s.Value.IsPaused))
                            {
                                session.Value.Rolls.Add(new RollStatistic
                                {
                                    PlayerName = command.User.GlobalName,
                                    RollValue = result,
                                    DiceType = "d20"  // ✅ Roll20 всегда d20
                                });
                            }
                        }
                    }
                }
                finally
                {
                    _sessionSemaphore.Release();
                }
            }

            // Показываем результат броска (в любом случае)
            var numbersDir = BotConfig.ResolvePath(BotConfig.Current?.NumbersDirectory ?? "Numbers");
            var diceSubfolder = Path.Combine(numbersDir, "d20");
            var filePath = Path.Combine(diceSubfolder, $"{result}.png");

            if (Directory.Exists(diceSubfolder) && File.Exists(filePath))
            {
                var embed = new EmbedBuilder()
                    .WithImageUrl($"attachment://{Path.GetFileName(filePath)}")
                    .WithColor(GetGradientColor(result, 1, 20))
                    .Build();
                await command.FollowupWithFileAsync(filePath, embed: embed);
            }
            else
            {
                await command.FollowupAsync($"Выпало: **{result}**");
            }
            Console.WriteLine($"Результат броска (d20): {result}");
        }

        private Color GetGradientColor(int value, int minValue, int maxValue)
        {
            float normalizedValue = (float)(value - minValue) / (maxValue - minValue);

            int r, g, b;

            if (normalizedValue < 0.5f) // от 1 до 10
            {
                r = 255;
                g = (int)(255 * (normalizedValue * 2));
                b = 0;
            }
            else // от 10 до 20
            {
                r = (int)(255 * (1 - (normalizedValue - 0.5f) * 2));
                g = 255;
                b = 0;
            }

            return new Color(r, g, b);
        }
    }

    public class InfoCommands : ModuleBase<SocketCommandContext>
    {
        [Command("help")]
        public async Task Help(SocketSlashCommand command)
        {
            var helpMessage = new EmbedBuilder()
                .WithTitle("ℹ️ Список доступных команд")
                .WithColor(Color.DarkBlue)
                .WithDescription("Основные команды бота для управления сервером и игровых механик")
                .AddField("📚 Основные команды",
                    "> `/help` - Показывает это сообщение\n" +
                    "> `/help_r` - Помощь по системе бросков кубиков\n" +
                    "> `/help_gs` - Помощь по управлению игровыми сессиями\n" +
                    "> `/help_predict` - Помощь по прогнозам и костяшкам\n" +
                    "> `/serverinfo` - Показывает информацию о сервере\n" +
                    "> `/bug_report [сообщение]` - Отправка отчета об ошибке или предложения")
                .AddField("🛠 Модерация и настройки",
                    "> `/open_chat [категория]` - Открывает доступ писать и перемещает в указанную категорию *(только для мастеров)*\n" +
                    "> `/close_chat [причина]` - Архивирует чат и закрывает доступ писать *(только для мастеров)*\n" +
                    "> `/clr X` - Удаляет X сообщений (1-100) *(для модераторов)*\n" +
                    "> `/settings ...` - Настройки сервера *(администратор/суперпользователь)*")
                .AddField("🎮 Игровые команды",
                    "> `/roll ...`, `/roll20` - Броски кубиков\n" +
                    "> `/start` - Запуск игровой сессии\n" +
                    "> `/prediction ...` - Прогнозы и ставки (см. `/help_predict`)")
                .WithFooter("При проблемах используйте /bug_report")
                .Build();

            await command.RespondAsync(embed: helpMessage);
        }

        [Command("serverinfo")]
        public async Task ServerInfo(SocketSlashCommand command)
        {
            await command.DeferAsync();
            var server = (command.Channel as SocketGuildChannel)?.Guild;

            if (server == null)
            {
                await command.FollowupAsync("Не удалось получить информацию о сервере.");
                return;
            }

            try
            {
                // Получаем актуальные данные о пользователях
                await server.DownloadUsersAsync();

                int totalMembers = server.MemberCount;
                // Если нет GuildPresences, статус может быть Offline для всех — учитываем голосовые каналы как онлайн
                int onlineMembers = server.Users.Count(u => u.Status != UserStatus.Offline || u.VoiceChannel != null);
                int botCount = server.Users.Count(u => u.IsBot);
                int humanCount = totalMembers - botCount;

                // Получаем информацию о каналах
                int textChannels = server.TextChannels.Count;
                int voiceChannels = server.VoiceChannels.Count;
                int categories = server.CategoryChannels.Count;

                var embed = new EmbedBuilder()
                    .WithTitle($"ℹ️ Информация о сервере {server.Name}")
                    .WithThumbnailUrl(server.IconUrl)
                    .AddField("📅 Создан", server.CreatedAt.ToString("dd.MM.yyyy"), true)
                    .AddField("👑 Владелец", server.Owner?.Mention ?? "Неизвестно", true)
                    .AddField("📊 Участники",
                        $"👥 Всего: {totalMembers}\n" +
                        $"🟢 Онлайн: {onlineMembers}\n" +
                        $"🤖 Ботов: {botCount}\n" +
                        $"👤 Людей: {humanCount}", true)
                    .AddField("🌐 Каналы",
                        $"📝 Текстовые: {textChannels}\n" +
                        $"🎤 Голосовые: {voiceChannels}\n" +
                        $"📂 Категории: {categories}", true)
                    .AddField("📌 Важные даты",
                        "• 20.11.2020 - Основание КнР\n" +
                        "• 01.02.2025 - Первый запуск бота на сервере")
                    .WithColor(Color.Blue)
                    //.WithFooter("Статистика участников временно недоступна")
                    .Build();

                await command.FollowupAsync(embed: embed);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка в serverinfo: {ex.Message}");
                await command.FollowupAsync("Произошла ошибка при обработке команды.");
            }
        }

        [Command("help_r")]
        public async Task Help_R(SocketSlashCommand command)
        {
            var helpMessage = new EmbedBuilder()
                .WithTitle("🎲 Помощь по системе бросков")
                .WithColor(Color.DarkPurple)
                .WithDescription("Система позволяет совершать броски кубиков с различными параметрами и модификаторами.")
                .AddField("🔹 Основные команды бросков",
                    "> `/roll XdY` - Будет совершён бросок кубика, или кубиков _(если_ `X`_ > 1)_, с номиналом `Y`.\n" +
                    "> `/roll XdY+Z` - Будет совершён бросок кубика, или кубиков _(если_ `X`_ > 1)_, " +
                    "с номиналом `Y`. Также будет добавлен модификатор в +/-`Z`.\n" +
                    "> `/roll Xd[min,max]` - Будет совершён бросок кубика, или кубиков _(если_ `X`_ > 1)_, " +
                    "с границами от`min` до `max`.\n" +
                    "> `/roll Xd[min,max]+Z` - Будет совершён бросок кубика, или кубиков _(если_ `X`_ > 1)_, с границами от`min` до `max`. " +
                    "Также будет добавлен модификатор в +/-`Z`.\n" +
                    "> `/roll20` - Быстрый бросок d20 (аналогично 1d20)")
                .AddField("🚪 Команды очереди действий",
                    "> `/queue Z` - Запуск очереди с `Z` персонажами в сцене *(только для мастеров)*\n" +
                    "> `/q dY` - Совершается бросок кубика выбранного номинала `Y`. Добавляет вас в очередь на выполнение действия. " +
                    "Может быть использована несколько раз одним человеком. **(только при активной очереди)**\n" +
                    "> `/stop_q` - Остановка текущей очереди, если она активна *(только для мастеров)*")
                .AddField("📊 Особенности системы",
                    "• Автоматическая запись бросков d20 в активных сессиях\n" +
                    "• Визуализация результатов d20 (изображения кубиков)\n" +
                    "• Градиентная цветовая индикация результатов\n" +
                    "• Ограничение на 10 бросков за раз")
                .AddField("⚠ Ограничения",
                    "• Броски в канале статистики учитываются только в активных сессиях\n" +
                    "• Броски не записываются, если сессия на паузе\n" +
                    "• Только мастера могут управлять очередями")
                .WithFooter("*При обнаружении некорректной работы бота сообщите об этом в `/bug_report`*")
                .Build();

            await command.RespondAsync(embed: helpMessage);
        }

        [Command("help_predict")]
        public async Task Help_Predict(SocketSlashCommand command)
        {
            var helpMessage = new EmbedBuilder()
                .WithTitle("📈 Прогнозы и ставки — помощь")
                .WithColor(Color.DarkTeal)
                .WithDescription("Прогнозы позволяют ставить **костяшки** на один из 2-5 исходов. Создание — через модал, ставки — через кнопки.")
                .AddField("✅ Где работает",
                    "Создание, ставки, завершение и отмена доступны **только в чате голосового канала**.\n" +
                    "Для создания прогноза нужно: **быть в голосовом канале** и чтобы на нём было **активное событие**.")
                .AddField("🧩 Основные команды",
                    "> `/prediction action:create` — создать прогноз с 2-5 исходами (мастер НРИ/админ)\n" +
                    "> `/prediction action:info` — ваш баланс и текущий прогноз\n" +
                    "> `/prediction action:bet outcome:<№> amount:<N>` — сделать ставку\n" +
                    "> `/prediction action:resolve outcome:<№>` — завершить (создатель/админ)\n" +
                    "> `/prediction action:cancel` — отменить с возвратом ставок (создатель/админ)")
                .AddField("📊 Статистика и достижения",
                    "> `/prediction action:history [page:<№>]` — история завершённых прогнозов\n" +
                    "> `/prediction action:profile [user:<@>]` — профиль игрока с полной статистикой\n" +
                    "> `/prediction action:achievements` — все 29 достижений + кто получил")
                .AddField("🖱️ Ставки через кнопки",
                    "1) `Сделать ставку` → показывается баланс + кнопка `Продолжить`\n" +
                    "2) `Продолжить` → модал с выбором исхода и суммы\n" +
                    "3) Если ставка уже была → модал **увеличения** без выбора исхода")
                .AddField("🎯 Множественные исходы",
                    "• При создании можно указать от 2 до 5 исходов\n" +
                    "• Коэффициенты рассчитываются автоматически: `общий банк / ставки на исход`\n" +
                    "• Прогресс-бары показывают распределение ставок\n" +
                    "• Пример: 70% → исход 1 (коэфф. 1.40x), 30% → исход 2 (коэфф. 3.33x)")
                .AddField("🏅 Система достижений",
                    "Автоматически присваиваются 29 достижений:\n" +
                    "• 🌟 Новичковые (5): прогресс участия\n" +
                    "• 💰 Финансовые (6): крупные выигрыши\n" +
                    "• 🎯 Точность (5): винрейт и серии\n" +
                    "• 🚀 Риск (4): высокие коэффициенты\n" +
                    "• 📈 Стратегия (4): создание и прибыль\n" +
                    "• ⭐ Специальные (5): уникальные условия")
                .AddField("⏳ Таймер",
                    "Приём ставок показывает **оставшееся время** (обновление ~10 сек).\n" +
                    "После закрытия приёма кнопки меняются на **выбор победителя**.")
                .AddField("ℹ️ Важно",
                    "• История: сохраняется до 100 прогнозов на сервер\n" +
                    "• Профиль: отслеживает все ставки, выигрыши, прибыль\n" +
                    "• Достижения: получаются автоматически после каждого прогноза\n" +
                    "• Если событие завершилось — прогноз закрывается с возвратом ставок")
                .WithFooter("Используйте /bug_report при проблемах | Помощь: /help_predict")
                .Build();

            await command.RespondAsync(embed: helpMessage);
        }

        [Command("help_gs")]
        public async Task Help_GS(SocketSlashCommand command)
        {
            var helpMessage = new EmbedBuilder()
                .WithTitle("📚 Помощь по управлению игровыми сессиями")
                .WithColor(Color.Blue)
                .WithDescription("Система позволяет отслеживать время игровых сессий, делать паузы и собирать статистику бросков.")
                .AddField("🔹 Основные действия",
                    "> `/start [название игры]` - Начинает новую сессию\n" +
                    "> `/help_gs` - Показывает это сообщение\n")
                .AddField("🕹 Управление сессией (через кнопки)",
                    "> `⏸ Пауза` - Приостановить отсчёт времени\n" +
                    "> `▶ Продолжить` - Возобновить сессию после паузы\n" +
                    "> `✏ Изменить` - Редактировать параметры сессии\n" +
                    "> `🎲 Броски` - Вкл/выкл сбор статистики бросков\n" +
                    "> `⏹ Завершить` - Остановить сессию и показать статистику")
                .AddField("📊 Статистика бросков",
                    "После завершения сессии вы можете:\n" +
                    "• Просмотреть общую статистику по значениям\n" +
                    "• Получить детальную статистику по игрокам\n" +
                    "• Отказаться от просмотра статистики")
                .AddField("ℹ Особенности",
                    "• Сессии автоматически создаются при старте ивентов\n" +
                    "• Напоминания о паузе каждые 10 минут\n" +
                    "• Время пауз не учитывается в общей статистике\n" +
                    "• Только мастера могут управлять сессиями")
                .WithFooter("*При обнаружении некорректной работы бота сообщите об этом в `/bug_report`*")
                .Build();

            await command.RespondAsync(embed: helpMessage);
        }

        [Command("bug_report")]
        public async Task Bug_Report(SocketSlashCommand command, string input)
        {
            var user = command.User as SocketGuildUser;

            if (user == null)
            {
                await command.RespondAsync("Произошла ошибка. Пожалуйста, попробуйте снова.");
                return;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                await command.RespondAsync("Пожалуйста, укажите сообщение для отчета об ошибке.");
                return;
            }

            string directoryPath = BotConfig.ResolvePath(BotConfig.Current?.BugReportDirectory ?? Path.Combine("Logs"));
            Directory.CreateDirectory(directoryPath);

            string filePath = Path.Combine(directoryPath, $"{user.Username}.txt");

            using (StreamWriter writer = new StreamWriter(filePath, true))
            {
                string reportTime = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
                await writer.WriteLineAsync($"[{reportTime}] {input}");
            }

            IncrementBugReportCounter();

            await command.RespondAsync("Ваш отчет об ошибке был успешно отправлен!", ephemeral: true);
        }

        private void IncrementBugReportCounter()
        {
            var logDir = BotConfig.ResolvePath(BotConfig.Current?.BugReportDirectory ?? Path.Combine("Logs"));
            Directory.CreateDirectory(logDir);
            string counterFilePath = Path.Combine(logDir, "bug_report_counter.txt");

            if (!File.Exists(counterFilePath))
            {
                File.WriteAllText(counterFilePath, "1");
            }
            else
            {
                if (int.TryParse(File.ReadAllText(counterFilePath), out var currentCount))
                {
                    currentCount++;
                    File.WriteAllText(counterFilePath, currentCount.ToString());
                }
                else
                {
                    File.WriteAllText(counterFilePath, "1");
                }
            }
        }
    }

    public class QueueModule : ModuleBase<SocketCommandContext>
    {
        private static Dictionary<SocketGuildUser, List<int>> userRolls = new();
        private static List<IUserMessage> messagesToDelete = new();
        private static int maxRolls;
        private static int rollCount;
        private static Timer rollTimer;
        private static bool isQueueActive;
        private static IUserMessage queueStartMessage;
        private static ITextChannel channel;

        private static Task LogStartup(string message)
        {
            Program.CommandLogSink?.Invoke(message);
            return Task.CompletedTask;
        }

        [Command("queue")]
        public async Task QueueCommand(SocketSlashCommand command, int count)
        {
            if (command.User is SocketGuildUser guildUser)
            {
                var roleNameToCheck = "Мастер НРИ";
                var hasRole = guildUser.Roles.Any(role => role.Name.Equals(roleNameToCheck, StringComparison.OrdinalIgnoreCase));

                if (!hasRole)
                {
                    await command.RespondAsync("У вас нет необходимой роли для выполнения этой команды.", ephemeral: true);
                    Console.WriteLine($"Ошибка: У пользователя {guildUser.DisplayName} недостаточно прав для выполнения команды");
                    var userRoles = guildUser.Roles.Select(r => r.Name).ToList();
                    Console.WriteLine($"Роли пользователя: {string.Join(", ", userRoles)}");
                    return;
                }
            }
            if (isQueueActive)
            {
                Console.WriteLine($"Предупреждение: Попытка создания новой очереди, когда одна уже активна.");
                await command.RespondAsync($"Очередь уже создана на {maxRolls} бросков. Если вы хотите её остановить принудительно, введите `/stop_q`.", ephemeral: true);
                return;
            }

            if (count <= 0)
            {
                await command.RespondAsync("Пожалуйста, укажите положительное число.");
                Console.WriteLine($"Ошибка: при создании очереди указано не положительное число ({count}) участников!");
                return;
            }

            maxRolls = count;
            rollCount = 0;
            userRolls.Clear();
            messagesToDelete.Clear();
            isQueueActive = true;

            await command.RespondAsync("Вы запустили создание очереди. Уведомьте об этом своих игроков. Бот остальную информацию уже сообщил." +
                "\nДанное сообщение можно скрыть или оно удалится автоматически.", ephemeral: true);
            _ = Task.Run(async () =>
            {
                await Task.Delay(7000);
                await command.DeleteOriginalResponseAsync();
            });

            channel = (ITextChannel)command.Channel;
            queueStartMessage = await channel.SendMessageAsync($"Очередь активирована. Ожидаем {count} бросков. " +
                $"Вводите значения в формате `/q dY`. Таймер на минуту ожидания запущен.");

            messagesToDelete.Add(queueStartMessage);

            rollTimer = new Timer(ResetQueue, null, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
            await LogStartup($"Очередь создана с заданным числом ({count}) участников. Таймер запущен.");
        }

        [Command("q")]
        public async Task QIn_RollDice(SocketSlashCommand command, string input)
        {
            if (!isQueueActive)
            {
                await LogStartup("Предупреждение: Очередь не активна.");
                await command.RespondAsync("Пожалуйста, запустите очередь перед выполнением этой команды.", ephemeral: true);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    await command.DeleteOriginalResponseAsync();
                });
                return;
            }

            if (!input.StartsWith("d") || !int.TryParse(input[1..], out int y) || y <= 0)
            {
                await LogStartup("Ошибка: Введены некорректные данные.");
                await command.RespondAsync("Пожалуйста, укажите корректные данные.", ephemeral: true);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    await command.DeleteOriginalResponseAsync();
                });
                return;
            }

            var user = command.User as SocketGuildUser;
            if (user == null || (userRolls.ContainsKey(user) && userRolls[user].Count >= maxRolls))
            {
                return;
            }

            rollTimer.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);

            int result = new Random().Next(1, y + 1);
            if (!userRolls.ContainsKey(user))
            {
                userRolls[user] = new List<int>();
            }

            userRolls[user].Add(result);
            rollCount++;

            await command.RespondAsync("Вывод результата:", ephemeral: false);
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                await command.DeleteOriginalResponseAsync();
            });

            var waitingMessage = await channel.SendMessageAsync($"{user.DisplayName}, ваш бросок d{y}: {result}. Ещё {maxRolls - rollCount} бросков. Ожидание следующего броска...");
            messagesToDelete.Add(waitingMessage);

            await LogStartup($"Успех: Совершён бросок пользователем {user.DisplayName}. Его результат - {result}. Таймер обновлён.");

            if (rollCount >= maxRolls)
            {
                await DisplayResults();
            }
        }

        private async Task DisplayResults()
        {
            rollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            isQueueActive = false;

            foreach (var msg in messagesToDelete)
            {
                try
                {
                    if (msg != null)
                    {
                        await msg.DeleteAsync();
                        await Task.Delay(100);
                    }
                }
                catch (Exception ex)
                {
                    await LogStartup($"Ошибка (вывод результатов): При попытке удалить сообщения произошла ошибка. Причина: {ex.Message}");
                }
            }

            var embed = new EmbedBuilder()
                .WithTitle("Последовательность ходов")
                .WithColor(Color.Green);

            if (userRolls == null || userRolls.Count == 0)
            {
                await LogStartup("userRolls is null or empty");
                return;
            }

            var sortedResults = userRolls
                .SelectMany(userRoll => userRoll.Value.Select((roll, index) => new
                {
                    User = userRoll.Key,
                    Roll = roll,
                    Index = index + 1
                }))
                .OrderByDescending(x => x.Roll)
                .ToList();

            var resultString = new StringBuilder();
            int sequence = 1;

            foreach (var result in sortedResults)
            {
                var userNickname = result.User.DisplayName;
                if (sortedResults.Count(r => r.User == result.User) > 1)
                {
                    resultString.AppendLine($"{sequence} - {userNickname} - {result.Roll} (# {result.Index})");
                }
                else
                {
                    resultString.AppendLine($"{sequence} - {userNickname} - {result.Roll}");
                }
                sequence++;
            }

            await LogStartup("Успех: Произведён вывод результатов. Сообщения удалены. Таймер остановлён.");
            embed.AddField("Результаты", resultString.ToString(), false);
            if (embed != null)
            {
                await channel.SendMessageAsync(embed: embed.Build());
            }
            else
            {
                await LogStartup("Ошибка: embed не был создан.");
            }

            maxRolls = 0;
            rollCount = 0;
            userRolls.Clear();
            messagesToDelete.Clear();
        }

        private async void ResetQueue(object state)
        {
            isQueueActive = false;

            var timeoutMessage = await channel.SendMessageAsync(":exclamation: Время ожидания истекло, " +
                "введите команду для создания очереди по новой. :exclamation:");
            messagesToDelete.Add(timeoutMessage);

            _ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(async t =>
            {
                foreach (var msg in messagesToDelete)
                {
                    try
                    {
                        await msg.DeleteAsync();
                    }
                    catch (Exception ex)
                    {
                        await LogStartup($"Ошибка (очистка очереди): При попытке удалить сообщения произошла ошибка. Причина: {ex.Message}");
                    }
                }
            });
            await LogStartup("Предупреждение: Таймер истёк. Сообщения и очередь удалены.");
        }

        [Command("stop_q")]
        public async Task StopQueue(SocketSlashCommand command)
        {
            if (command.User is SocketGuildUser guildUser)
            {
                var roleNameToCheck = "Мастер НРИ";
                var hasRole = guildUser.Roles.Any(role => role.Name.Equals(roleNameToCheck, StringComparison.OrdinalIgnoreCase));

                if (!hasRole)
                {
                    await command.RespondAsync("У вас нет необходимой роли для выполнения этой команды.", ephemeral: true);
                    await LogStartup($"Ошибка: У пользователя {guildUser.DisplayName} недостаточно прав для выполнения команды");
                    var userRoles = guildUser.Roles.Select(r => r.Name).ToList();
                    await LogStartup($"Роли пользователя: {string.Join(", ", userRoles)}");
                    return;
                }
            }
            if (!isQueueActive)
            {
                await command.RespondAsync("Очередь не активна.", ephemeral: false);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(7000);
                    await command.DeleteOriginalResponseAsync();
                });
                return;
            }

            // Остановка таймера
            rollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            isQueueActive = false;

            // Удаляем все сообщения, связанные с очередью
            foreach (var msg in messagesToDelete)
            {
                try
                {
                    await msg.DeleteAsync();
                }
                catch (Exception ex) { Console.WriteLine($"Ошибка (остановка очереди): При попытке удалить сообщения произошла ошибка. Причина: {ex.Message}"); }
            }

            // Уведомление об отмене очереди
            await command.RespondAsync("Запись очереди принудительно отменена мастером.");
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                await command.DeleteOriginalResponseAsync();
            });
        }

        // Graceful shutdown for static resources used by QueueModule
        public static void ShutdownQueue()
        {
            try
            {
                lock (typeof(QueueModule))
                {
                    try { rollTimer?.Dispose(); } catch { }
                    rollTimer = null;

                    try { messagesToDelete?.Clear(); } catch { }
                    try { userRolls?.Clear(); } catch { }

                    isQueueActive = false;
                    queueStartMessage = null;
                    channel = null;
                    maxRolls = 0;
                    rollCount = 0;
                }
            }
            catch (Exception ex)
            {
				try
				{
					var logDirRaw = BotConfig.Current?.LogDirectory;
					var logDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDirRaw) ? "Logs" : logDirRaw);
					Directory.CreateDirectory(logDir);
					File.AppendAllText(Path.Combine(logDir, "ErrorLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ShutdownQueue error: {ex.Message}\n");
				}
				catch { }
            }
        }
    }

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

        public GameSessionCommands(DiscordSocketClient client) => _client = client;

        private async void Log(string message)
        {
            Program.CommandLogSink?.Invoke(message);
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

    public class ModerationCommands : ModuleBase<SocketCommandContext>
    {
        private readonly DiscordSocketClient _client;

        private static Task LogStartup(string message)
        {
            Program.CommandLogSink?.Invoke(message);
            return Task.CompletedTask;
        }

        public ModerationCommands(DiscordSocketClient client)
        {
            _client = client;
        }

        [Command("close_chat")]
        public async Task CloseChat(SocketSlashCommand command)
        {
            // Отложим ответ, чтобы Discord не считал команду "зависшей"
            await command.DeferAsync();

            await LogStartup($"Команда '/close_chat' вызвана пользователем {command.User.Username} ({command.User.Id}).");

            // Проверяем, что команду выполняет "perekrestok_mirov" или "domen_"
            var allowedUsers = new[] { "perekrestok_mirov", "domen_" };
            var user = command.User as SocketGuildUser;

            if (user == null || !allowedUsers.Contains(user.Username))
            {
                await LogStartup($"Отказ в доступе: пользователь {command.User.Username} не имеет прав на выполнение команды.");
                await command.FollowupAsync("У вас нет прав на выполнение этой команды. Администратор оповещён.", ephemeral: true);
                return;
            }

            var channel = command.Channel as SocketGuildChannel;
            var guild = channel?.Guild;

            if (guild == null)
            {
                await command.FollowupAsync("Эта команда может быть выполнена только на сервере.", ephemeral: true);
                return;
            }

            var reason = command.Data.Options.FirstOrDefault(opt => opt.Name == "reason")?.Value?.ToString() ?? "Причина не указана.";

            if (channel is SocketThreadChannel threadChannel)
            {
                // Если это ветка, выводим информацию
                await LogStartup($"Обработка ветки {threadChannel.Name} ({threadChannel.Id}).");

                // Отправляем сообщение в ветке перед её закрытием
                await threadChannel.SendMessageAsync("Тема закрыта. Сбор на игры перешёл в отдельный чат.");

                // Закрываем ветку, если это поддерживается
                try
                {
                    // Здесь можно использовать метод ArchiveAsync, если он доступен
                    await threadChannel.ModifyAsync(prop =>
                    {
                        prop.Locked = true;
                        prop.Archived = true; // Убедитесь, что это свойство поддерживается
                    });

                    Console.WriteLine($"[{DateTime.UtcNow}] Ветка {threadChannel.Name} закрыта. Причина: {reason}");
                    await command.FollowupAsync($"Ветка {threadChannel.Mention} была закрыта. Причина: {reason}");
                }
                catch (NotSupportedException ex)
                {
                    Console.WriteLine($"Ошибка при закрытии ветки: {ex.Message}");
                    await command.FollowupAsync("Не удалось закрыть ветку. Пожалуйста, проверьте права доступа или тип канала.");
                }
            }
            else if (channel is SocketTextChannel textChannel)
            {
                // Если это текстовый канал, перемещаем его в архив и закрываем доступ
                SocketCategoryChannel archiveCategory = guild.CategoryChannels.FirstOrDefault(cat => cat.Name == "Архив");

                /*if (archiveCategory != null && textChannel.CategoryId == archiveCategory.Id)
                {
                    Console.WriteLine($"[{DateTime.UtcNow}] Чат {textChannel.Name} уже находится в архиве.");
                    await command.FollowupAsync($"Чат {textChannel.Mention} уже находится в архиве.", ephemeral: true);
                    return;
                }*/

                if (archiveCategory == null)
                {
                    Console.WriteLine($"[{DateTime.UtcNow}] Категория 'Архив' не найдена. Создание новой категории.");
                    var restCategory = await guild.CreateCategoryChannelAsync("Архив");

                    if (restCategory == null)
                    {
                        Console.WriteLine($"[{DateTime.UtcNow}] Ошибка: не удалось создать категорию 'Архив'.");
                        await command.FollowupAsync("Не удалось создать категорию 'Архив'.", ephemeral: true);
                        return;
                    }

                    // Используем restCategory напрямую
                    await textChannel.ModifyAsync(prop =>
                    {
                        prop.CategoryId = restCategory.Id;
                    });

                    Console.WriteLine($"[{DateTime.UtcNow}] Канал {textChannel.Name} перемещён в категорию 'Архив'.");
                }
                else
                {
                    // Используем существующую категорию
                    await textChannel.ModifyAsync(prop =>
                    {
                        prop.CategoryId = archiveCategory.Id;
                    });

                    Console.WriteLine($"[{DateTime.UtcNow}] Канал {textChannel.Name} перемещён в категорию 'Архив'.");
                }

                // Закрываем доступ на отправку сообщений для всех пользователей
                // Получаем всех пользователей на сервере
                var users = await guild.GetUsersAsync().Flatten().ToListAsync();

                // Закрываем доступ на отправку сообщений для пользователей, у которых есть доступ к каналу
                foreach (var guildUser in users)
                {
                    // Проверяем, есть ли у пользователя доступ к каналу
                    var permissions = guildUser.GetPermissions(textChannel);

                    if (permissions.ViewChannel) // Если пользователь имеет доступ к каналу
                    {
                        // Получаем текущие переопределения прав для пользователя
                        var overwrite = textChannel.GetPermissionOverwrite(guildUser);

                        if (overwrite != null)
                        {
                            // Получаем текущие разрешения и запреты
                            var allow = overwrite.Value.AllowValue; // Разрешения
                            var deny = overwrite.Value.DenyValue;  // Запреты

                            // Убираем разрешение на отправку сообщений
                            allow &= ~(ulong)Discord.ChannelPermission.SendMessages;

                            // Добавляем запрет на отправку сообщений
                            deny |= (ulong)Discord.ChannelPermission.SendMessages;

                            // Создаём новые переопределения
                            var newOverwrite = new OverwritePermissions(allow, deny);

                            // Обновляем переопределение прав
                            await textChannel.AddPermissionOverwriteAsync(guildUser, newOverwrite);
                            await LogStartup($"Запрещена отправка сообщений для пользователя {guildUser.Username}.");
                        }
                        else
                        {
                            // Если переопределения нет, создаём новое с запретом на отправку сообщений
                            await textChannel.AddPermissionOverwriteAsync(guildUser, new OverwritePermissions(sendMessages: PermValue.Deny));
                            await LogStartup($"Запрещена отправка сообщений для пользователя {guildUser.Username}.");
                        }
                    }
                    await Task.Delay(100);
                }

                await LogStartup($"Чат {textChannel.Name} перемещён в архив и закрыт. Причина: {reason}");
                await command.FollowupAsync($"Чат {textChannel.Mention} был перемещён в архив и закрыт. Причина: {reason}");
            }
            else
            {
                await LogStartup("Ошибка: команда выполнена в неподдерживаемом типе канала.");
                await command.FollowupAsync("Эта команда может быть выполнена только в текстовом канале или ветке на форуме.", ephemeral: true);
            }
        }

        [Command("open_chat")]
        public async Task OpenChat(SocketSlashCommand command)
        {
            // Отложим ответ, чтобы Discord не считал команду "зависшей"
            await command.DeferAsync();

            await LogStartup($"Команда '/open_chat' вызвана пользователем {command.User.Username} ({command.User.Id}).");

            // Проверяем, что команду выполняет "perekrestok_mirov" или "domen_"
            var allowedUsers = new[] { "perekrestok_mirov", "domen_" };
            var user = command.User as SocketGuildUser;

            if (user == null || !allowedUsers.Contains(user.Username))
            {
                await LogStartup($"Отказ в доступе: пользователь {command.User.Username} не имеет прав на выполнение команды.");
                await command.FollowupAsync("У вас нет прав на выполнение этой команды. Администратор оповещён.", ephemeral: true);
                return;
            }

            var channel = command.Channel as SocketGuildChannel;
            var guild = channel?.Guild;

            if (guild == null)
            {
                await command.FollowupAsync("Эта команда может быть выполнена только на сервере.", ephemeral: true);
                return;
            }

            if (channel is not SocketTextChannel textChannel)
            {
                await LogStartup("Ошибка: команда выполнена в неподдерживаемом типе канала.");
                await command.FollowupAsync("Эта команда может быть выполнена только в текстовом канале.", ephemeral: true);
                return;
            }

            // Получаем название категории из аргумента команды
            var categoryName = command.Data.Options.FirstOrDefault(opt => opt.Name == "category")?.Value?.ToString();

            if (string.IsNullOrEmpty(categoryName))
            {
                await LogStartup("Ошибка: не указана категория для перемещения.");
                await command.FollowupAsync("Не указана категория для перемещения.", ephemeral: true);
                return;
            }

            // Ищем категорию по имени
            var targetCategory = guild.CategoryChannels.FirstOrDefault(cat => cat.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));

            if (targetCategory == null)
            {
                await LogStartup($"Ошибка: категория '{categoryName}' не найдена.");
                await command.FollowupAsync($"Категория с именем '{categoryName}' не найдена.", ephemeral: true);
                return;
            }

            // Получаем всех пользователей на сервере
            var users = await guild.GetUsersAsync().Flatten().ToListAsync();

            // Возвращаем доступ на запись только тем, кто уже есть в чате
            foreach (var guildUser in users)
            {
                // Проверяем, есть ли у пользователя доступ к каналу
                var permissions = guildUser.GetPermissions(textChannel);

                if (permissions.ViewChannel) // Если пользователь имеет доступ к каналу
                {
                    // Получаем текущие переопределения прав для пользователя
                    var overwrite = textChannel.GetPermissionOverwrite(guildUser);

                    if (overwrite != null)
                    {
                        // Получаем текущие разрешения и запреты
                        var allow = overwrite.Value.AllowValue; // Разрешения
                        var deny = overwrite.Value.DenyValue;  // Запреты

                        // Убираем запрет на отправку сообщений
                        deny &= ~(ulong)Discord.ChannelPermission.SendMessages;

                        // Добавляем разрешение на отправку сообщений
                        allow |= (ulong)Discord.ChannelPermission.SendMessages;

                        // Создаём новые переопределения
                        var newOverwrite = new OverwritePermissions(allow, deny);

                        // Обновляем переопределение прав
                        await textChannel.AddPermissionOverwriteAsync(guildUser, newOverwrite);
                        await LogStartup($"Возвращена возможность отправки сообщений для пользователя {guildUser.Username}.");
                    }
                    else
                    {
                        // Если переопределения нет, создаём новое с разрешением на отправку сообщений
                        await textChannel.AddPermissionOverwriteAsync(guildUser, new OverwritePermissions(sendMessages: PermValue.Allow));
                        await LogStartup($"Возвращена возможность отправки сообщений для пользователя {guildUser.Username}.");
                    }
                }
                await Task.Delay(100);
            }

            // Перемещаем канал в указанную категорию
            await LogStartup($"Перемещение канала {textChannel.Name} в категорию '{targetCategory.Name}'.");
            await textChannel.ModifyAsync(prop =>
            {
                prop.CategoryId = targetCategory.Id;
            });

            await LogStartup($"Чат {textChannel.Name} открыт и перемещён в категорию '{targetCategory.Name}'.");
            await command.FollowupAsync($"Чат {textChannel.Mention} был открыт и перемещён в категорию '{targetCategory.Name}'.");
        }

        [Command("clr")]
        public async Task ClearMessages(SocketSlashCommand command, int count)
        {
            var user = command.User as SocketGuildUser;

            var rolesToCheck = new List<string> { "Технический гуру", "Модератор", "Хранители" };
            var hasRole = user.Roles.Any(role => rolesToCheck.Contains(role.Name, StringComparer.OrdinalIgnoreCase));

            if (user == null || !hasRole)
            {
                await command.RespondAsync("У вас нет необходимой роли для выполнения этой команды.", ephemeral: true);
                await LogStartup($"Ошибка: У пользователя {user.DisplayName} недостаточно прав для выполнения команды");
                return;
            }

            if (count < 1 || count > 100)
            {
                await command.RespondAsync("Пожалуйста, укажите число от 1 до 100.", ephemeral: true);
                await LogStartup($"Ошибка: Пользователь {user.DisplayName} ввёл некорректное число сообщений - {count}");
                return;
            }

            var messagesToDeleteList = await command.Channel.GetMessagesAsync(count).FlattenAsync();

            if (command.Channel is ITextChannel textChannel)
            {
                await textChannel.DeleteMessagesAsync(messagesToDeleteList);

                await command.RespondAsync(GetMessageCountString(count), ephemeral: true);
                    await LogStartup(GetMessageCountString(count));
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    await command.DeleteOriginalResponseAsync();
                });
            }
            else
            {
                await command.RespondAsync("Эта команда может быть выполнена только в текстовом канале.", ephemeral: true);
            }
        }

        private string GetMessageCountString(int count)
        {
            if (count % 10 == 1 && count % 100 != 11)
                return $"{count} сообщение удалено.";
            else if ((count % 10 >= 2 && count % 10 <= 4) && (count % 100 < 10 || count % 100 >= 20))
                return $"{count} сообщения удалено.";
            else
                return $"{count} сообщений удалено.";
        }
    }
}
