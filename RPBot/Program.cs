using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using RPBot;
using RPBot.Music;
using RPBot.EventOps;
using RPBot.Startup;
using RPBot.Util;
using RPBot.Web;
using RPBot.Common;
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
using System.IO;
using Discord.Rest;

namespace RPBot
{
    public partial class Program : IDisposable, IBotController
    {
        public static Program? Instance { get; private set; }

        static Program()
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            try
            {
                var ex = args.ExceptionObject as Exception;
                var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
                var crashDir = Path.Combine(BotConfig.GetDataDirectory(), "crashes");
                Directory.CreateDirectory(crashDir);
                var crashFile = Path.Combine(crashDir, $"crash_{timestamp}.txt");
                var content = $"{ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}\n";
                File.WriteAllText(crashFile, content);
                BotLogger.Error(LogCategory.System, $"[Crash] Необработанное исключение записано в {crashFile}");
            }
            catch
            {
                // Последний рубеж — ничего не делаем, чтобы не усугублять
            }
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            try
            {
                BotLogger.Error(LogCategory.System, $"[Task] Unobserved: {args.Exception?.GetType().Name}: {args.Exception?.Message}");
                args.SetObserved();
            }
            catch { }
        }
        private DiscordSocketClient? _client;
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
        private LogDayRolloverService? _logDayRollover;
private PointsService _pointsService;
        private PointsUserIndex _pointsUserIndex;
private PredictionService? _predictionService;
private VoicePointsService? _voicePointsService;
        private TelegramNotifier? _telegramNotifier;
        private EventAnnouncementStore? _eventAnnouncementStore;
        private EventOpsOrchestrator? _eventOpsOrchestrator;
        private RPBot.EventOps.EventOpsRemigrationService? _eventOpsRemigrator;
        private RPBot.EventOps.EventOpsLifecycleService? _eventOpsLifecycle;
        private EventAnnouncer? _eventAnnouncer;
        private IWebDashboard? _webDashboard;
private GoogleSheetsService? _googleSheetsService;
private LavalinkService? _lavalinkService;
private MusicCommands? _musicCommands;
private MusicPlaylistStore? _playlistStore;
private MusicQueueStore? _musicQueueStore;
private MusicStats? _musicStats;

        private readonly ConcurrentDictionary<string, SocketMessageComponent?> _pendingBetUi = new();

        private readonly ConcurrentDictionary<string, (string title, int minutes)> _pendingPredictionCreate = new();
        private readonly ConcurrentDictionary<string, DateTimeOffset> _masterGuideSentAt = new();

        // Счётчики для дашборда /api/stats
        private int _rollsTodayCount;
        private int _chatMessagesTodayCount;
        private int _usersInVoiceCount;
        private DateTimeOffset _startupTimeUtc = DateTimeOffset.UtcNow;
        private DateTime _lastCountersResetDate = DateTime.UtcNow.Date;
        // Скользящее окно активности (последний час, по минутам)
        private readonly ConcurrentQueue<ActivityBucket> _activityBuckets = new();
        private readonly object _activityLock = new();
        private const int ActivityWindowMinutes = 60;

        // Простая версия бота для /api/health (читается из атрибута сборки при наличии, иначе — константа)
        public const string BotVersion = "dev";

private Task? _backgroundMonitoringTask;
private CancellationTokenSource? _backgroundMonitoringCts;
private CancellationTokenSource? _dailyRestartCts;
private Task? _dailyRestartTask;
        private string _restartInitiator = "console";

        public static Action<string>? CommandLogSink { get; private set; }
        public static Func<ulong, ServerConfig?>? ServerConfigResolver { get; private set; }

        /// <summary>
        /// Команды, для которых <see cref="Discord.WebSocket.SocketSlashCommand.DeferAsync"/>
        /// выполняется заранее — в самом начале <see cref="OnSlashCommandExecuted"/>,
        /// чтобы исключить «Cannot defer an interaction after 3 seconds!» на проде.
        /// </summary>
        private static readonly HashSet<string> _preDeferCommands = new(StringComparer.Ordinal)
        {
                    // ВНИМАНИЕ: легкие команды сюда НЕ добавлять — PreDefer сам идёт
                    // в Discord API за 200-500мс (Cloudflare), что отъедает 1/6 от окна ACK.
                    // Если команда укладывается в 3с без PreDefer — оставляем обычный путь.
                    // Текущий список пуст: roll/roll20 и подобные обрабатываются быстро и
                    // PreDefer для них создаёт SLOW-предупреждения и риск таймаута ACK.
                };

        /// <summary>
        /// Interaction-токены, для которых уже выполнен предварительный defer.
        /// Нужен, чтобы сам обработчик команды (<see cref="RollDiceCommands"/>) не звал
        /// <c>DeferAsync</c> повторно. Хранится недолго — до прихода в обработчик.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _preDeferDone = new();

        /// <summary>Отметить interaction-токен как предварительно задеференный.</summary>
        internal static void MarkPreDeferDone(ulong interactionId) => _preDeferDone[interactionId] = 1;

        /// <summary>Был ли предварительный defer выполнен для этого interaction.</summary>
        public static bool IsPreDeferDone(ulong interactionId) => _preDeferDone.ContainsKey(interactionId);

        /// <summary>Снять отметку после того, как обработчик её увидел.</summary>
        public static bool ClearPreDeferDone(ulong interactionId) => _preDeferDone.TryRemove(interactionId, out _);

                /// <summary>
                /// Interaction-токены, для которых предварительный defer провалился (10062 / TimeoutException).
                /// Если PreDefer упал — interaction уже мёртв, повторный DeferAsync в обработчике
                /// только заставит пользователя ждать 3 секунды до «Приложение не отвечает».
                /// </summary>
                private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _preDeferFailed = new();

                /// <summary>Отметить interaction как «PreDefer упал».</summary>
                internal static void MarkPreDeferFailed(ulong interactionId) => _preDeferFailed[interactionId] = 1;

                /// <summary>Упал ли предварительный defer для этого interaction.</summary>
                public static bool IsPreDeferFailed(ulong interactionId) => _preDeferFailed.ContainsKey(interactionId);

                /// <summary>Снять отметку после того, как обработчик её увидел.</summary>
                public static bool ClearPreDeferFailed(ulong interactionId) => _preDeferFailed.TryRemove(interactionId, out _);

                /// <summary>
                /// Момент последнего успешного реконнекта (UTC). Используется, чтобы дать Discord шанс
                /// синхронизировать сессию после Gateway Reconnect — иначе первый defer может уйти
                /// до того, как Discord успел зарегистрировать нашу сессию, и он ответит 10062.
                /// </summary>
                private static DateTime _lastReconnectUtc = DateTime.MinValue;

                /// <summary>Сколько времени (UTC) прошло с последнего реконнекта.</summary>
                private static TimeSpan TimeSinceLastReconnect => DateTime.UtcNow - _lastReconnectUtc;

                /// <summary>Время последнего успешного REST keep-alive пинга (UTC).</summary>
                private static DateTime _lastKeepAliveUtc = DateTime.MinValue;

                /// <summary>Отметить момент реконнекта; вызывается из ReconnectionService / OnReady.</summary>
                internal static void MarkReconnectCompleted() { _lastReconnectUtc = DateTime.UtcNow; }

                                // 🩹 perf: Manual GC. По умолчанию .NET запускает GC.Collect() редко, и
                                // когда память наконец собирается — пауза может быть >3 сек, что
                                                                // приводит к "Приложение не отвечает" в Discord. Стратегия:
                                                                //  • каждые 30 с — мягкая сборка (Optimized, blocking:false, compacting:false)
                                                                //    в фоне — короткие паузы <100 мс.
                                                                //  • каждые 5 минут — ПОЛНАЯ сборка с compaction LOH (blocking:true),
                                                                //    устраняет фрагментацию LOH от больших PNG (>85 КБ) и предотвращает
                                                                //    внезапные Gen2-паузы >3 с.
                                                                //  • замер паpz GC (Total_Pause / кол-во сборок / алоцированные байты) —
                                                                //    пишется в лог каждую итерацию, чтобы видеть реальную картину.
                                                                private static CancellationTokenSource? _gcLoopCts;
                                                                private static Task? _gcLoopTask;
                                                                private const int GcLoopPeriodSeconds = 1800; // 30 мин — период замера и компактизации (было 5 мин)
                                                                                                                                private const int GcCompactIntervalSeconds = 1800; // 30 мин — между полными compaction-прогонами
                                                                // Состояние для замеров.
                                                                private static long _lastAllocatedBytes;
                                                                private static int _lastGen0, _lastGen1, _lastGen2;
                                                                private static TimeSpan _lastTotalPause;
                                                                private static readonly System.Diagnostics.Stopwatch _gcWatch = new();

                                                                internal static void StartManualGcLoop()
                                                                {
                                                                    if (_gcLoopTask != null) return;
                                                                                                                                    try
                                                                                                                                    {
                                                                                                                                        BotLogger.Info(LogCategory.System, $"[GC] StartManualGcLoop called");
                                                                                                                                        _gcLoopCts = new CancellationTokenSource();
                                                                                                                                        var ct = _gcLoopCts.Token;
                                                                                                                                        _gcWatch.Restart();
                                                                                                                                        _lastAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
                                                                                                                                        _lastGen0 = GC.CollectionCount(0);
                                                                                                                                        _lastGen1 = GC.CollectionCount(1);
                                                                                                                                        _lastGen2 = GC.CollectionCount(2);
                                                                                                                                        _lastTotalPause = GC.GetTotalPauseDuration();
                                                                    _gcLoopTask = Task.Run(async () =>
                                                                    {
                                                                        var compactInterval = TimeSpan.FromSeconds(GcCompactIntervalSeconds);
                                                                        DateTime nextCompactAt = DateTime.UtcNow + compactInterval;
                                                                        try
                                                                        {
                                                                            while (!ct.IsCancellationRequested)
                                                                            {
                                                                                try { await Task.Delay(TimeSpan.FromSeconds(GcLoopPeriodSeconds), ct).ConfigureAwait(false); }
                                                                                catch (OperationCanceledException) { return; }

                                                                                bool doCompact = DateTime.UtcNow >= nextCompactAt;
                                                                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                                                                try
                                                                                {
                                                                                    if (doCompact)
                                                                                    {
                                                                                        // Полная сборка с компактизацией LOH — изредка,
                                                                                        // в фоне. Укладываемся обычно в 100-400 мс, но
                                                                                        // без неё LOH фрагментируется и рано или поздно
                                                                                        // случается пауза >3 сек.
                                                                                        GC.Collect(2, GCCollectionMode.Default, blocking: true, compacting: true);
                                                                                    }
                                                                                    else
                                                                                    {
                                                                                        GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
                                                                                    }
                                                                                }
                                                                                catch (Exception ex)
                                                                                {
                                                                                    BotLogger.Warn(LogCategory.System, $"[GC] manual loop failed: {ex.Message}");
                                                                                    continue;
                                                                                }
                                                                                sw.Stop();

                                                                                // Замер после сборки.
                                                                                var afterPause = GC.GetTotalPauseDuration();
                                                                                var afterGen0 = GC.CollectionCount(0);
                                                                                var afterGen1 = GC.CollectionCount(1);
                                                                                var afterGen2 = GC.CollectionCount(2);
                                                                                var allocatedNow = GC.GetTotalAllocatedBytes(precise: true);
                                                                                var heap0 = GC.GetTotalMemory(forceFullCollection: false);
                                                                                _gcWatch.Restart();

                                                                                var pauseDelta = afterPause - _lastTotalPause;
                                                                                var allocDeltaMb = (allocatedNow - _lastAllocatedBytes) / 1024d / 1024d;
                                                                                var heapMb = heap0 / 1024d / 1024d;
                                                                                BotLogger.Info(LogCategory.System,
                                                                                    $"[GC] iter compact={doCompact} allocDelta={allocDeltaMb:F1}MB " +
                                                                                    $"gen0={afterGen0 - _lastGen0} gen1={afterGen1 - _lastGen1} gen2={afterGen2 - _lastGen2} " +
                                                                                    $"heap={heapMb:F1}MB " +
                                                                                    $"pauseDelta={pauseDelta.TotalMilliseconds:F0}ms totalPause={afterPause.TotalMilliseconds:F0}ms " +
                                                                                    $"iterMs={sw.Elapsed.TotalMilliseconds:F0}");

                                                                                _lastAllocatedBytes = allocatedNow;
                                                                                _lastGen0 = afterGen0;
                                                                                _lastGen1 = afterGen1;
                                                                                _lastGen2 = afterGen2;
                                                                                _lastTotalPause = afterPause;

                                                                                if (doCompact) nextCompactAt = DateTime.UtcNow + compactInterval;
                                                                            }
                                                                        }
                                                                        catch (Exception ex)
                                                                        {
                                                                            BotLogger.Error(LogCategory.System, $"[GC] loop crashed: {ex}");
                                                                        }
                                                                    });
                                                                    BotLogger.Info(LogCategory.System, $"[GC] manual loop запущен, период={GcLoopPeriodSeconds}с, compaction={GcCompactIntervalSeconds}с");
                                                                                                                                        }
                                                                                                                                        catch (Exception ex)
                                                                                                                                        {
                                                                                                                                            BotLogger.Error(LogCategory.System, $"[GC] StartManualGcLoop crashed: {ex.GetType().Name}: {ex.Message}");
                                                                                                                                        }
                                                                                                                                    }

                                internal static void StopManualGcLoop()
                                {
                                    try { _gcLoopCts?.Cancel(); } catch { }
                                    _gcLoopCts = null;
                                    _gcLoopTask = null;
                                }

                // Флаг, что мы внутри Program.Main — это первый запуск, и AttachSink
        // к StartupRenderer.Instance должен произойти один раз. На рестарте
        // мы НЕ добавляем sinks повторно — иначе в UI одна и та же строка
        // появляется N раз (по разу на каждый сохранённый UiSink-экземпляр).
        private static bool _startupSinksAttached = false;
        // Флаг, что BotLogger уже инициализирован (в constructor Program). Используется,
        // чтобы RunBotAsync() не вызывал Initialize() повторно — иначе будет две сессионные
        // папки с одним именем и маркеры restart побьются.
        private static bool _loggerInitialized = false;
        private static readonly List<RPBot.Startup.IStartupSink> _attachedSinks = new();

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

        /// <summary>
        /// Отписывает все обработчики от DiscordSocketClient и утилизирует его.
        /// Безопасна для null и для уже disposed client.
        /// До финального Dispose останавливает фоновые таймеры автообновления
        /// control message — иначе они будут долбиться в disposed HttpClient.
        /// </summary>
        private void DisposeClientSafely(DiscordSocketClient? client)
        {
            if (client == null) return;
            try
            {
                client.Ready -= OnReady;
                client.Disconnected -= OnDisconnected;
                client.UserJoined -= UserJoined;
                client.MessageReceived -= HandleCommandAsync;
                client.SlashCommandExecuted -= OnSlashCommandExecuted;
                client.SlashCommandExecuted -= BwonkCommand;
                client.ModalSubmitted -= HandleModalSubmitted;
                client.ButtonExecuted -= HandleButtonExecuted;
                client.SelectMenuExecuted -= HandleSelectMenuExecuted;
                client.GuildScheduledEventCreated -= OnGuildScheduledEventCreated;
                client.GuildScheduledEventUpdated -= OnGuildScheduledEventUpdated;
                client.GuildScheduledEventStarted -= OnGuildScheduledEventStarted;
                client.GuildScheduledEventCancelled -= OnGuildScheduledEventCancelled;
                client.GuildScheduledEventCompleted -= OnGuildScheduledEventCompleted;
                client.GuildMemberUpdated -= OnGuildMemberUpdated;
            }
            catch { }
            try { RPBot.GameSessionCommands.StopAllAutoRefresh(); } catch { }
            try { client.Dispose(); } catch { }
        }

        private Task SetupDiscordEvents()
        {
            if (_client != null)
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
                _client.GuildMemberUpdated -= OnGuildMemberUpdated;

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
                _client.GuildMemberUpdated += OnGuildMemberUpdated;
            }

            return LogStartup("События Discord настроены");
        }
        private async Task HandlePredictionBetButton(SocketMessageComponent component, string[] parts)
        {
            // customId: pred_bet:<guildId>:<channelId>
                        // ✅ pred-parallelization: добавлен channelId, т.к. кнопка привязана к конкретному
                        // prediction-каналу, а не ко всей гильдии.
                        if (parts.Length < 3) return;
                        if (!ulong.TryParse(parts[1], out var guildId)) return;
                        if (!ulong.TryParse(parts[2], out var channelId)) return;

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
                                // 🩹 perf: PreDefer с ретраем — раньше кнопка делала RespondAsync напрямую,
                                // и при сетевом джиттере >3с пользователь не получал ответа.
                                if (!await Common.ComponentPreDefer.TryDeferAsync(component, "pred_bet"))
                                    return;

                                var predictionService = _predictionService;
                                var active = predictionService?.GetActive(guildId, channelId);
                                if (active == null || active.IsResolved)
                                {
                                    try { await component.FollowupAsync("Сейчас нет активного прогноза в этом канале.", ephemeral: true); } catch { }
                                    ScheduleDeleteOriginalResponse(component);
                                    return;
                                }

                                var balance = _pointsService.GetBalance(guildId, component.User.Id);
                                var cb = new ComponentBuilder()
                                    .WithButton($"Продолжить (баланс: {balance})", customId: $"pred_bet_confirm:{guildId}:{channelId}", style: ButtonStyle.Primary);

                                try { await component.FollowupAsync($"Ваш текущий баланс: {balance}.", ephemeral: true, components: cb.Build()); } catch { }
                                ScheduleDeleteOriginalResponse(component);
                            }

        private async Task HandlePredictionBetConfirmButton(SocketMessageComponent component, string[] parts)
                {
                    // customId: pred_bet_confirm:<guildId>:<channelId>
                                // ✅ pred-parallelization: добавлен channelId — кнопка живёт в конкретном канале,
                                // и обработчик modal'а должен знать, в каком именно.
                                if (parts.Length < 3) return;
                                if (!ulong.TryParse(parts[1], out var guildId)) return;
                                if (!ulong.TryParse(parts[2], out var channelId)) return;

                                var user = component.User as SocketGuildUser;
                                if (user == null)
                                {
                                    await component.RespondAsync("Только участники сервера могут ставить.", ephemeral: true);
                                    return;
                                }

                                // Note: PreDefer не делаем здесь — кнопка открывает Modal через
                                // RespondWithModalAsync, а после Defer это запрещено Discord API.

                                _pendingBetUi[$"{guildId}:{component.User.Id}"] = component;

                        var active = _predictionService?.GetActive(guildId, channelId);
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
                                .WithCustomId($"pred_bet_add_modal:{guildId}:{channelId}")
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
                                .WithCustomId($"pred_bet_modal:{guildId}:{channelId}")
                                .AddTextInput(label, "outcome", TextInputStyle.Short, placeholder: outcomePlaceholder, maxLength: 2)
                                .AddTextInput("Сумма", "amount", TextInputStyle.Short, placeholder: "Количество костяшек")
                                .Build();
                        }

                        await component.RespondWithModalAsync(modal);
                    }

        /// <summary>
        /// Проверяет наличие активного события на указанном голосовом канале
        /// и возвращает его EventId, чтобы привязать к нему создаваемый прогноз.
        /// </summary>
        private ulong? GetActiveEventIdOnChannel(ulong guildId, ulong channelId)
        {
            var guild = _client?.GetGuild(guildId);
            if (guild == null) return null;

            var ev = guild.Events.FirstOrDefault(e =>
                e.Status == GuildScheduledEventStatus.Active &&
                e.Channel != null &&
                e.Channel.Id == channelId);
            return ev?.Id;
        }

        /// <summary>
        /// Проверяет наличие активного события на указанном голосовом канале
        /// </summary>
        private bool IsActiveEventOnChannel(ulong guildId, ulong channelId)
        {
            return GetActiveEventIdOnChannel(guildId, channelId).HasValue;
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

                // ✅ pred-parallelization: ищем прогноз именно в канале команды (а не любой на гильдии).
                                var existingPrediction = _predictionService?.GetActive(guildId, channelId);
                                if (existingPrediction != null)
                                {
                                    await component.RespondAsync("В этом голосовом канале уже есть активный прогноз. Дождитесь его завершения или отмените.", ephemeral: true);
                                    return;
                                }

                if (outcomesCount == 3)
                {
                                    // ✅ Round 7-C7: перенесли "Время (мин)" на 2-ю строку (сразу после
                                    // заголовка), чтобы модальное окно выглядело сбалансированно:
                                    //   1) Заголовок
                                    //   2) Время (мин)
                                    //   3) Исход 1
                                    //   4) Исход 2
                                    //   5) Исход 3 (опц.)
                                    var modal = new ModalBuilder()
                                        .WithTitle("Создать прогноз")
                                        .WithCustomId($"pred_create_modal_3:{guildId}:{channelId}")
                                        .AddTextInput("Заголовок", "title", TextInputStyle.Short, placeholder: "Название прогноза", maxLength: 100)
                                        .AddTextInput("Время (мин)", "duration_minutes", TextInputStyle.Short, placeholder: "От 1 до 60 минут", value: "3")
                                        .AddTextInput("Исход 1", "outcome1", TextInputStyle.Short, placeholder: "Название исхода 1", maxLength: 80)
                                        .AddTextInput("Исход 2", "outcome2", TextInputStyle.Short, placeholder: "Название исхода 2", maxLength: 80)
                                        .AddTextInput("Исход 3 (опц.)", "outcome3", TextInputStyle.Short, placeholder: "Оставьте пустым для 2 исходов", required: false, maxLength: 80)
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

                    // 🩹 perf: PreDefer с ретраем — раньше UpdateAsync падал с TimeoutException,
                    // если история грузится долго (большой объём).
                    if (!await Common.ComponentPreDefer.TryDeferAsync(component, "pred_history_page"))
                        return;

                    try
                    {
                        // Используем метод из Program.Prediction.cs (partial class)
                        var embed = BuildHistoryEmbed(guildId, page);
                        var components = BuildHistoryComponents(guildId, page);

                        await component.ModifyOriginalResponseAsync(msg =>
                        {
                            msg.Embed = embed;
                            msg.Components = components?.Build();
                        });
            }
                    catch (Exception ex)
                    {
                        await PredictionErrorLogger.LogAsync("HandlePredictionHistoryPageButton", ex, $"guild={guildId} page={page}").ConfigureAwait(false);
                        try { await component.FollowupAsync("Ошибка при переключении страницы.", ephemeral: true); } catch { }
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
                        var ch = _client?.GetChannel(message.Channel.Id) as IMessageChannel;
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
            // customId: pred_cancel:<guildId>:<channelId>
                        // ✅ pred-parallelization: добавлен channelId — кнопка привязана к конкретному прогнозу в канале.
                        if (parts.Length < 3) return;
                        if (!ulong.TryParse(parts[1], out var guildId)) return;
                        if (!ulong.TryParse(parts[2], out var channelId)) return;

                                // 🩹 perf: PreDefer с ретраем.
                                if (!await Common.ComponentPreDefer.TryDeferAsync(component, "pred_cancel"))
                                    return;

                                var user = component.User as SocketGuildUser;
                                var isAdmin = user?.GuildPermissions.Administrator ?? false;

                                var resolverId = user?.Id ?? 0UL;
                                var predictionService = _predictionService;
                                if (predictionService == null)
                                {
                                    try { await component.FollowupAsync("Сервис прогнозов недоступен.", ephemeral: true); } catch { }
                                    return;
                                }

                                var activePrediction = predictionService.GetActive(guildId, channelId);
                                if (activePrediction == null)
                                {
                                    try { await component.FollowupAsync("В этом канале нет активного прогноза.", ephemeral: true); } catch { }
                                    return;
                                }

                                var (ok, error) = await predictionService.CancelAsync(activePrediction, resolverId, isAdmin);
                                            if (ok)
                                    {
                                                try { await component.ModifyOriginalResponseAsync(msg => { msg.Content = "Прогноз отменён"; msg.Components = new ComponentBuilder().Build(); }); } catch { }
                                    }
                                            else
                                            {
                                                try { await component.FollowupAsync(error, ephemeral: true); } catch { }
                                            }
                                        }

                    private async Task HandlePredictionResolveButton(SocketMessageComponent component, string[] parts)
                    {
                        // customId: pred_resolve:<guildId>:<channelId>:<outcomeId>
                        if (parts.Length < 4) return;
                        if (!ulong.TryParse(parts[1], out var guildId)) return;
                        if (!ulong.TryParse(parts[2], out var channelId)) return;
                        if (!int.TryParse(parts[3], out var outcomeId)) return;

                                            // 🩹 perf: PreDefer с ретраем — раньше ResolveAsync мог тормозить и
                                            // RespondAsync падал с TimeoutException. Теперь ACK первым.
                                            if (!await Common.ComponentPreDefer.TryDeferAsync(component, "pred_resolve"))
                                                return;

                                            var user = component.User as SocketGuildUser;
                                            var isAdmin = user?.GuildPermissions.Administrator ?? false;

                                            var resolverId = user?.Id ?? 0UL;
                                            var predictionService = _predictionService;
                                            if (predictionService == null)
                                            {
                                                try { await component.FollowupAsync("Сервис прогнозов недоступен.", ephemeral: true); } catch { }
                                                return;
                                            }

                                            var activePrediction = predictionService.GetActive(guildId, channelId);
                                            if (activePrediction == null)
                                            {
                                                try { await component.FollowupAsync("В этом канале нет активного прогноза.", ephemeral: true); } catch { }
                                                return;
                                            }

                                            var (ok, error) = await predictionService.ResolveAsync(activePrediction, resolverId, isAdmin, outcomeId);
                                            if (!ok)
                        {
                                                // Avoid responding if the original message was deleted — try update quietly
                                                try { await component.FollowupAsync(error, ephemeral: true); } catch { }
                                            }
                                            else
                                            {
                                                try { await component.ModifyOriginalResponseAsync(msg => msg.Components = new ComponentBuilder().Build()); } catch { }
                                            }
                                        }

                    // ✅ R6 fix: чистит _pendingBetUi для конкретной гильдии (все пользователи).
                    // Вызывается на cancel/resolve/autocancel.
                    private void ClearPendingBetUiForGuild(ulong guildId)
                    {
                        var prefix = $"{guildId}:";
                        foreach (var key in _pendingBetUi.Keys)
                        {
                            if (key.StartsWith(prefix, StringComparison.Ordinal))
                            {
                                if (_pendingBetUi.TryRemove(key, out var stale) && stale != null)
                                {
                                    try { stale.DeleteOriginalResponseAsync().GetAwaiter().GetResult(); } catch { }
                                }
                            }
                        }
                    }

                    // ✅ R6 fix: полная очистка _pendingBetUi (для Disconnected/Shutdown).
                    private void ClearAllPendingBetUi()
                    {
                        foreach (var key in _pendingBetUi.Keys.ToList())
                        {
                            if (_pendingBetUi.TryRemove(key, out var stale) && stale != null)
                            {
                                try { stale.DeleteOriginalResponseAsync().GetAwaiter().GetResult(); } catch { }
                            }
                        }
                    }

                    // ✅ R6 fix: обработчики событий PredictionService об окончании/отмене прогноза.
                    // У PendingBetUi-preview кнопки нет исходного сообщения (его ещё не отправили),
                    // поэтому просто удаляем запись из словаря — никакой DeleteOriginalResponseAsync.
                    private void OnPredictionResolvedForUi(ulong guildId) => ClearPendingBetUiForGuild(guildId);
                    private void OnPredictionCancelledForUi(ulong guildId) => ClearPendingBetUiForGuild(guildId);

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
                                FileStream? lockHandle = SafeJsonIO.AcquireLock(_bwonkFilePath, retries: 5, retryDelayMs: 50);
                                try
                                {
                                    SafeJsonIO.WriteAtomic(_bwonkFilePath, json);
                                }
                                finally
                                {
                                    SafeJsonIO.ReleaseLock(lockHandle, _bwonkFilePath);
                                }
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
private string _bwonkFilePath = BotConfig.ResolvePath(Path.Combine(BotConfig.DataFolderName, "bwonks.json"));

private EventNotificationService? _eventNotifications;
private string _eventNotificationsPath = BotConfig.ResolvePath(Path.Combine(BotConfig.DataFolderName, "event-notify.json"));

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
                #pragma warning disable CS0618 // ✅ pred-parallelization: EventVoiceChannelID obsolete, но используется в fallback-логике конфига
                                EventVoiceChannelID = overrides.EventVoiceChannelID != 0 ? overrides.EventVoiceChannelID : defaults.EventVoiceChannelID,
                #pragma warning restore CS0618

                MasterGuideEnabled = overrides.MasterGuideEnabled,
                MasterGuideTemplate = !string.IsNullOrWhiteSpace(overrides.MasterGuideTemplate)
                    ? overrides.MasterGuideTemplate
                    : defaults.MasterGuideTemplate,
                MasterGuideCooldownHours = overrides.MasterGuideCooldownHours > 0
                    ? overrides.MasterGuideCooldownHours
                    : defaults.MasterGuideCooldownHours,

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
    var options = new JsonSerializerOptions
    {
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    var json = JsonSerializer.Serialize(_serverConfigs, options);
    // SafeJsonIO делает .tmp → File.Move(overwrite:true); межпроцессный
    // лок и .bak-фолбэк для serverconfigs всё ещё держим локально (это было
    // до появления SafeJsonIO и не сломано — только упрощаем запись).
    SafeJsonIO.WriteAtomic(resolved, json);
    if (File.Exists(resolved))
    SafeJsonIO.WriteAtomic(resolved + ".bak", json);
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
    Instance = this;
    var configRelativePath = Path.Combine("Settings", "config.json");
    var configResolvedPath = BotConfig.ResolvePath(configRelativePath);
    _config = BotConfig.Load(configRelativePath);

    // Автоматически мигрируем файлы данных из Settings/ в Data/ (один раз)
    BotConfig.MigrateDataFiles();

    // ✅ Инициализируем BotLogger как можно раньше — в constructor Program, ДО создания
    // любых сервисов (в т.ч. WebDashboardService). Иначе логи из constructor и Start()
    // сервисов уходят в пустоту, потому что WriteUnifiedLineAsync пишет только когда
    // задан _unifiedLogPath, а _paths заполняется именно в Initialize().
    //
    // Раньше Initialize() жил в RunBotAsync() — это слишком поздно: WebDashboard уже
    // создаётся и запускается в constructor Program, и любые его логи (включая ошибки
    // HttpListener) терялись, бот продолжал работу без видимой диагностики.
    //
    // Чтобы не плодить две сессионные папки, RunBotAsync() проверит флаг
    // _loggerInitialized (см. ниже) и не будет звать Initialize повторно.
    if (!_loggerInitialized)
    {
        var logDirRawEarly = _config?.LogDirectory;
        var logDirEarly = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDirRawEarly) ? "Logs" : logDirRawEarly);
        BotLogger.Initialize(logDirEarly, DateTime.Now);
        _loggerInitialized = true;
        BotLogger.Info(LogCategory.Boot, "=== Бот запускается ===");
        // ✅ Суточные папки логов: планировщик запускаем сразу после Initialize,
        // чтобы он работал даже если RunBotAsync() не дойдёт (например, фатальная ошибка
        // конфига до конструктора UI). Это гарантирует, что в 06:00 логи переключатся
        // на новую папку независимо от того, в какой стадии инициализации находится бот.
        _logDayRollover?.Dispose();
        _logDayRollover = new LogDayRolloverService(logDirEarly);
        _logDayRollover.Start();
    }

    // Критическая проверка: GuildIDs должен быть задан в config.json
                if (_config?.GuildIDs is null || _config.GuildIDs.Count == 0)
            {
            // Цвет только если stdout — реальный TTY. На headless VPS / под systemd
            // / при редиректе в файл ConsoleColor всё равно не отрендерится; хуже того —
            // под `nohup` (Windows-VPS-сценарий) оставит ANSI-коды в run.log.
            // Подробнее см. ConsoleSink.IsStdoutATty() — здесь дублируем инлайн,
            // потому что это критический pre-logger старт (BotLogger ещё не активен).
            bool stdoutIsTty = !Console.IsOutputRedirected
                && (OperatingSystem.IsWindows()
                    || Environment.GetEnvironmentVariable("TERM") != "dumb");
            if (stdoutIsTty) Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ОШИБКА: GuildIDs не задан в Settings/config.json.");
            Console.WriteLine("Укажите список ID серверов, например:");
            Console.WriteLine("  \"GuildIDs\": [ 123456789012345678 ]");
            if (stdoutIsTty) Console.ResetColor();
            throw new InvalidOperationException("GuildIDs не задан в config.json. Бот не может запуститься без указания серверов.");
            }

    _serverConfigsPath = BotConfig.ResolvePath(Path.Combine("Settings", "serverconfigs.json"));
            LoadServerConfigs();
            ServerConfigResolver = GetServerConfigInternal;

    // Создаём файлы шаблонов памятки мастера для всех загруженных серверов при первом запуске
    try
    {
        if (_serverConfigs != null && _serverConfigs.Count > 0)
        {
            MasterGuideService.EnsureAllTemplates(_serverConfigs.Keys);
        }
        else if (_config?.GuildIDs != null)
        {
            MasterGuideService.EnsureAllTemplates(_config.GuildIDs);
        }
    }
    catch (Exception ex)
    {
        BotLogger.Warn(LogCategory.System, $"[MasterGuide] Ошибка при массовом создании шаблонов: {ex.Message}");
    }

    // Загружаем историю отправленных памяток (для кулдауна между перезагрузками)
    LoadMasterGuideHistory();

    // Диагностика: куда именно мы загрузили конфиг и видим ли токен (не печатаем сам токен)
    try
    {
    Console.WriteLine($"Config: {configResolvedPath}");
    Console.WriteLine($"Config BotToken present: {!string.IsNullOrWhiteSpace(_config?.BotToken)}");
    }
    catch { }

            _client = CreateDiscordClient();
            VoiceChannelCommands.SetClient(_client);
            _commandService = new CommandService();

    // Load persisted DM event-notification subscriptions BEFORE EventAnnouncer,
    // иначе _eventNotifications остаётся null и AnnounceCreatedInternalAsync
    // падает с NRE на строке GetActiveSubscribers(...).
    try
    {
    _eventNotifications = new EventNotificationService(_eventNotificationsPath);
    }
    catch
    {
    _eventNotifications = new EventNotificationService(_eventNotificationsPath);
    }

    // ИНИЦИАЛИЗАЦИЯ НОВЫХ СЕРВИСОВ
    _reconnectionService = new ReconnectionService(_client!) { LogSink = ServiceLogSink };
    _connectionPredictor = new ConnectionPredictor(_reconnectionService, BotConfig.Current?.Prediction);
    _statusNotifier = new StatusNotifier(_client!, GetServerConfigInternal) { LogSink = ServiceLogSink };
        _telegramNotifier = new TelegramNotifier(guildId =>
            {
                return _serverConfigs != null && _serverConfigs.TryGetValue(guildId, out var sc) ? sc : null;
            });
    _eventAnnouncementStore = new EventAnnouncementStore(Path.Combine(BotConfig.GetDataDirectory(), "event_announcements.json"));
    var eventOpsRenderer = new EventOpsRenderer();
    _eventOpsOrchestrator = new EventOpsOrchestrator(eventOpsRenderer, _eventAnnouncementStore);
        _eventOpsRemigrator = new RPBot.EventOps.EventOpsRemigrationService(_eventAnnouncementStore);
    _eventAnnouncer = new EventAnnouncer(
    () => _client!,
    () => _serverConfigs!,
    _telegramNotifier,
    _eventAnnouncementStore,
    _eventNotifications!);
    _eventOpsOrchestrator.OnCreatedAsync = AnnouncerOnCreatedAsync;
        _eventOpsOrchestrator.OnUpdatedAsync = AnnouncerOnUpdatedAsync;
        _eventOpsOrchestrator.OnStartedAsync = AnnouncerOnStartedAsync;
    // ВАЖНО: Announcer назначается первым (=), а Lifecycle подключается ПОСЛЕ через +=.
    // Тогда multicast-делегат = [Announcer → Lifecycle]. Announcer успевает
    // проставить LastUpdatedMark и сохранить запись, потом Lifecycle читает её и
    // планирует cleanup24h. Если Lifecycle вызвался бы раньше — store.TryGet вернул
    // бы ещё-не-обновлённую запись и таймер бы не встал.
        _eventOpsOrchestrator.OnCancelledAsync = AnnouncerOnCancelledAsync;
        _eventOpsOrchestrator.OnCompletedAsync = AnnouncerOnCompletedAsync;

    // EventOpsLifecycleService — отложенные напоминания и автоудаление анонсов.
    // Подключается к тому же оркестратору (его хэндлеры добавляются ПОСЛЕ базовых).
    _eventOpsLifecycle = new RPBot.EventOps.EventOpsLifecycleService(
        _eventAnnouncementStore,
        () => _client!,
        _telegramNotifier,
        _eventNotifications!,
        () => _serverConfigs!);
    _eventOpsLifecycle.Attach(_eventOpsOrchestrator);

        // 🩹 safeinvoke-lambda-name: раньше OnCreatedAsync/OnUpdatedAsync/...
        // назначались лямбдами `e => _eventAnnouncer!.AnnounceCreatedAsync(e)`.
        // Multicast `GetInvocationList()` в EventOpsOrchestrator.SafeInvokeAsync
        // логирует `single.Method.DeclaringType.Name.single.Method.Name` —
        // для лямбды это `<<>c__DisplayClass…>`, в логах полная муть.
        // Теперь подписчики — локальные функции с человеко-читаемыми именами:
        //   AnnouncerOnCreatedAsync / AnnouncerOnUpdatedAsync / ...
        //   (методы добавлены рядом с OnGuildScheduledEventCreated).
        // Orchestrator для остальных подписчиков выводит их Method.Name.
        // Для Lifecycle подписчиков он работает так же (HandleCreatedAsync/HandleUpdatedAsync/...),
        // потому что Attach делает += на методы напрямую.

    BotLogger.Info(LogCategory.System, $"[WebDashboard] Program constructor: создаю web dashboard на 0.0.0.0:5057 (платформа={(OperatingSystem.IsWindows() ? "Windows → WebDashboardService/HttpListener" : "Linux → WebDashboardHost/Kestrel")})");
        // ✅ Linux-деплой использует WebDashboardHost (Kestrel) — HttpListener в .NET 8 на Linux
        // имеет хронические баги (ErrorCode=400 на loopback-префиксах, ломаный Authorization) и
        // требует root для не-loopback. Kestrel корректно работает на 127.0.0.1 без urlacl.
        // На Windows продолжает использоваться WebDashboardService (HttpListener) — там Kestrel не нужен.
        if (OperatingSystem.IsWindows())
        {
            _webDashboard = new WebDashboardService(
            host: "0.0.0.0",
    port: 5057,
    healthProvider: () => new
    {
    Connected = _client?.ConnectionState == ConnectionState.Connected,
    Guilds = _client?.Guilds?.Count ?? 0,
    StartupType = GetStartupTypeDisplay(),
    UtcNow = DateTimeOffset.UtcNow,
    Version = BotVersion,
    Uptime = DateTimeOffset.UtcNow - _startupTimeUtc,
    },
    serverConfigsProvider: () => _serverConfigs!,
    sessionsProvider: () =>
        {
        var client = _client;
        return GameSessionCommands._sessions
        .ToDictionary(
        g => g.Key,
        g =>
        {
            SocketGuild? sguild = null;
            try { sguild = client?.GetGuild(g.Key); } catch { }
            var guildName = sguild?.Name;
            // Считаем "сбор бросков активен" по двум признакам:
            // 1) в GameSession включён TrackRolls (мастер не отключал);
            // 2) в QueueModule для этой гильдии есть живая очередь (IsActive=true, MaxRolls>0).
            // Иначе флаг TrackRolls=true на сессии может висеть без реального сбора.
            bool queueActive = false;
            int queueRollsCount = 0;
            try
            {
            if (QueueModule._guildQueues != null
                && QueueModule._guildQueues.TryGetValue(g.Key, out var q)
                && q != null)
            {
            queueActive = q.IsActive && q.MaxRolls > 0;
            if (q.UserRolls != null)
                queueRollsCount = q.UserRolls.Sum(kv => kv.Value?.Count ?? 0);
            }
            }
            catch { }
        return g.Value.Values.Select(s => new
        {
        s.SessionId,
        GuildId = g.Key,
        GuildName = guildName,
        Name = s.GameName,
        Status = s.IsStopped
        ? "завершена"
        : s.IsPaused
        ? "на паузе"
        : "активна",
        // ✅ Bug 2/4: каждый переключатель привязан к КОНКРЕТНОЙ сессии.
        // Раньше здесь считали «любая сессия в гильдии собирает броски»
        // и отдавали одну булку на всю гильдию — дашборд менял флаг
        // для всех сессий при переключении в одной. Теперь TrackRolls
        // живёт в GameSession, и здесь мы просто прокидываем его.
        RollCollecting = !s.IsStopped && !s.IsPaused && s.TrackRolls,
        // ✅ Bug 2: счётчик бросков — строго по конкретной сессии
        // (session.Rolls). Очередь не суммируется сюда, чтобы не
        // «размазывать» счётчик по сессиям.
        RollsCount = s.Rolls?.Count ?? 0,
        CreatedAt = s.StartTime,
        LeaderId = s.MasterId,
        MasterName = s.MasterName,
        }).ToList();
        });
    },
        eventsProvider: () =>
        {
        if (_eventAnnouncementStore == null) return "event announcements not ready";
        try
        {
        var entries = _eventAnnouncementStore.GetEntriesSnapshot();
        var client = _client;
        return entries.Select(e =>
        {
            SocketGuild? g = null;
            try { g = client?.GetGuild(e.GuildId); } catch { }
            // Локализованная подпись статуса: берём из LastUpdatedMark («▶️ Событие началось: 12.08.2026 14:00»),
            // оставляя только первую часть строки (смайл + статус) — без даты, она выводится отдельно.
            string? statusLabel = null;
            if (!string.IsNullOrEmpty(e.LastUpdatedMark))
            {
            var mark = e.LastUpdatedMark!;
            var colon = mark.IndexOf(':');
            statusLabel = colon > 0 ? mark.Substring(0, colon).Trim() : mark;
            }
            string? whenMsk = null;
            try
            {
            if (e.LastStartTimeUtc.HasValue)
            whenMsk = e.LastStartTimeUtc.Value.UtcDateTime.AddHours(3).ToString("dd.MM.yyyy HH:mm");
            } catch { }
            return new
            {
                GuildId = e.GuildId,
                GuildName = g?.Name,
                EventId = e.EventId,
                Name = e.LastName ?? "(без названия)",
                Description = e.LastDescription,
                StartsAtMsk = whenMsk,
                Location = e.LastLocation,
                ChannelId = e.LastChannelId,
                CoverImageUrl = e.LastCoverImageUrl,
                StatusLabel = statusLabel,
                LastUpdatedAtUtc = e.LastUpdatedAtUtc,
            };
        }).ToList();
        }
        catch (Exception ex) { return ex.Message; }
        },
    clientProvider: () => _client,
    rollsTodayProvider: () => _rollsTodayCount,
    activeSessionsProvider: () => GameSessionCommands._sessions.Sum(g => g.Value.Count(s => !s.Value.IsPaused)),
    chatMessagesTodayProvider: () => _chatMessagesTodayCount,
    usersInVoiceProvider: () => _usersInVoiceCount,
    activityProvider: () => GetActivityBuckets(),
    versionProvider: () => BotVersion,
    uptimeProvider: () => DateTimeOffset.UtcNow - _startupTimeUtc,
    systemsProvider: () =>
    {
    var checks = new List<object>();
    try { checks.Add(new { Name = "Discord Gateway",  Healthy = _client?.ConnectionState == Discord.ConnectionState.Connected, Kind = (_client?.ConnectionState == Discord.ConnectionState.Connected) ? "ok" : "err", Message = _client?.ConnectionState == Discord.ConnectionState.Connected ? $"Подключено ({_client.Latency} мс)" : $"Не подключено ({_client?.ConnectionState})" }); } catch { }
    try { var guildsCount = _client?.Guilds?.Count ?? 0; checks.Add(new { Name = "Серверы Discord", Healthy = guildsCount > 0, Kind = guildsCount > 0 ? "ok" : "err", Message = guildsCount > 0 ? $"Доступно: {guildsCount}" : "Нет доступных серверов" }); } catch { }
    try { var cfgN = _serverConfigs?.Count ?? 0; checks.Add(new { Name = "Конфигурация", Healthy = cfgN > 0, Kind = cfgN > 0 ? "ok" : "err", Message = cfgN > 0 ? $"Настроено: {cfgN}" : "Нет настроенных серверов" }); } catch { }
    try
    {
        var predEnabled = _serverConfigs?.Values?.Count(c => c.PredictionsEnabled) ?? 0;
        checks.Add(new { Name = "Прогнозы", Healthy = predEnabled > 0, Kind = predEnabled > 0 ? "ok" : "mute", Message = predEnabled > 0 ? $"Включены на {predEnabled} серверах" : "Не включены ни на одном сервере" });
    }
    catch { }
    try
    {
        if (_telegramNotifier != null)
        {
        var probes = new List<string>();
        foreach (var kvp in _serverConfigs ?? new Dictionary<ulong, ServerConfig>())
        {
        if (!kvp.Value.TelegramEnabled) continue;
            var r = _telegramNotifier.ProbeAsync(kvp.Key).GetAwaiter().GetResult();
        probes.Add($"{kvp.Key}: {(r.Success ? "OK" : r.Message)}");
        }
            var ok = probes.Count > 0 && probes.All(s => s.EndsWith("OK"));
        checks.Add(new { Name = "Telegram", Healthy = ok, Kind = ok ? "ok" : (probes.Count == 0 ? "mute" : "err"), Message = probes.Count > 0 ? string.Join("\n", probes) : "Telegram-интеграция выключена на всех серверах" });
        }
        else
        {
        checks.Add(new { Name = "Telegram", Healthy = false, Kind = "mute", Message = "Telegram-нотификатор отключён в конфиге" });
        }
    }
    catch (Exception ex) { checks.Add(new { Name = "Telegram", Healthy = false, Kind = "err", Message = ex.Message }); }
    try { checks.Add(new { Name = "Голосовые поинты", Healthy = _voicePointsService != null, Kind = (_voicePointsService != null) ? "ok" : "err", Message = _voicePointsService != null ? "OK" : "Сервис не инициализирован" }); } catch { }
    try { checks.Add(new { Name = "Хранилище поинтов", Healthy = _pointsService != null, Kind = (_pointsService != null) ? "ok" : "err", Message = _pointsService != null ? "OK" : "Сервис не инициализирован" }); } catch { }
    try
    {
        if (_googleSheetsService == null)
        {
        // google_credentials.json не задан — это сознательное выключение,
        // а не рабочее состояние сервиса. Healthy=false, Kind=mute.
        checks.Add(new { Name = "Google Sheets", Healthy = false, Kind = "mute", Message = "Отключено (google_credentials.json не задан)" });
        }
        else
        {
        var probe = _googleSheetsService.ProbeAsync().GetAwaiter().GetResult();
        checks.Add(new { Name = "Google Sheets", Healthy = probe.Success, Kind = probe.Success ? "ok" : "err", Message = probe.Message });
        }
    }
    catch (Exception ex) { checks.Add(new { Name = "Google Sheets", Healthy = false, Kind = "err", Message = ex.Message }); }
    try
    {
        if (_lavalinkService == null)
        {
        checks.Add(new { Name = "Музыка (Lavalink)", Healthy = false, Kind = "mute", Message = "Отключено (Music.Enabled=false)" });
        }
        else
        {
        // LavalinkService.ProbeAsync() дёргает /version по HTTP с таймаутом 3с —
        // гораздо надёжнее, чем Process.HasExited (процесс может быть жив,
        // но HTTP-сервер ещё не поднялся, или упасть сразу после старта).
        var probeErr = _lavalinkService.ProbeAsync().GetAwaiter().GetResult();
        var procOk = string.IsNullOrEmpty(probeErr);
        checks.Add(new { Name = "Музыка (Lavalink)", Healthy = procOk, Kind = procOk ? "ok" : "err", Message = procOk ? $"Отвечает на {_config!.Music.Host}:{_config!.Music.Port}/version" : $"Не отвечает: {probeErr}" });
        }
    }
    catch (Exception ex) { checks.Add(new { Name = "Музыка (Lavalink)", Healthy = false, Kind = "err", Message = ex.Message }); }
    try
    {
        var loaded = _textBlocks?.Count ?? 0;
        // Пустой файл — не «работает», а «нет шаблонов». Нейтральный статус info
        // показывает это без ложной зелёной галочки.
        var kind = loaded > 0 ? "ok" : "info";
        checks.Add(new { Name = "Текстовые блоки (Pastes.txt)", Healthy = loaded > 0, Kind = kind, Message = loaded > 0 ? $"Загружено {loaded} шаблонов" : "Файл отсутствует — шаблоны пустые (не критично)" });
    }
    catch (Exception ex) { checks.Add(new { Name = "Текстовые блоки (Pastes.txt)", Healthy = false, Message = ex.Message }); }
    return checks;
    });
        }
        else
        {
        // ───── Linux-ветка (VPS-деплой) ─────
        // Kestrel-реализация WebDashboardHost. Сигнатура конструктора совпадает с
        // WebDashboardService 1:1 (тот же набор Func-провайдеров), поэтому мы
        // дублируем только список аргументов. Если меняется состав провайдеров —
        // синхронизируй обе ветки.
        _webDashboard = new WebDashboardHost(
        host: "0.0.0.0",
        port: 5057,
        healthProvider: () => new
        {
        Connected = _client?.ConnectionState == ConnectionState.Connected,
        Guilds = _client?.Guilds?.Count ?? 0,
        StartupType = GetStartupTypeDisplay(),
        UtcNow = DateTimeOffset.UtcNow,
        Version = BotVersion,
        Uptime = DateTimeOffset.UtcNow - _startupTimeUtc,
        },
        serverConfigsProvider: () => _serverConfigs!,
        sessionsProvider: () =>
            {
            var client = _client;
            return GameSessionCommands._sessions
            .ToDictionary(
            g => g.Key,
            g =>
            {
                SocketGuild? sguild = null;
                try { sguild = client?.GetGuild(g.Key); } catch { }
                var guildName = sguild?.Name;
                return g.Value.Values.Select(s => new
                {
                s.SessionId,
                GuildId = g.Key,
                GuildName = guildName,
                Name = s.GameName,
                Status = s.IsStopped ? "завершена" : (s.IsPaused ? "на паузе" : "активна"),
                RollCollecting = !s.IsStopped && !s.IsPaused && s.TrackRolls,
                RollsCount = s.Rolls?.Count ?? 0,
                CreatedAt = s.StartTime,
                LeaderId = s.MasterId,
                MasterName = s.MasterName,
                }).ToList();
            });
            },
            eventsProvider: () =>
            {
            if (_eventAnnouncementStore == null) return "event announcements not ready";
            try
            {
            var entries = _eventAnnouncementStore.GetEntriesSnapshot();
            var client = _client;
            return entries.Select(e =>
            {
                SocketGuild? g = null;
                try { g = client?.GetGuild(e.GuildId); } catch { }
                string? statusLabel = null;
                if (!string.IsNullOrEmpty(e.LastUpdatedMark))
                {
                var mark = e.LastUpdatedMark!;
                var colon = mark.IndexOf(':');
                statusLabel = colon > 0 ? mark.Substring(0, colon).Trim() : mark;
                }
                string? whenMsk = null;
                try
                {
                if (e.LastStartTimeUtc.HasValue)
                whenMsk = e.LastStartTimeUtc.Value.UtcDateTime.AddHours(3).ToString("dd.MM.yyyy HH:mm");
                } catch { }
                return new
                {
                    GuildId = e.GuildId,
                    GuildName = g?.Name,
                    EventId = e.EventId,
                    Name = e.LastName ?? "(без названия)",
                    Description = e.LastDescription,
                    StartsAtMsk = whenMsk,
                    Location = e.LastLocation,
                    ChannelId = e.LastChannelId,
                    CoverImageUrl = e.LastCoverImageUrl,
                    StatusLabel = statusLabel,
                    LastUpdatedAtUtc = e.LastUpdatedAtUtc,
                };
            }).ToList();
            }
            catch (Exception ex) { return ex.Message; }
            },
        clientProvider: () => _client,
        rollsTodayProvider: () => _rollsTodayCount,
        activeSessionsProvider: () => GameSessionCommands._sessions.Sum(g => g.Value.Count(s => !s.Value.IsPaused)),
        chatMessagesTodayProvider: () => _chatMessagesTodayCount,
        usersInVoiceProvider: () => _usersInVoiceCount,
        activityProvider: () => GetActivityBuckets(),
        versionProvider: () => BotVersion,
        uptimeProvider: () => DateTimeOffset.UtcNow - _startupTimeUtc,
        systemsProvider: () =>
        {
        var checks = new List<object>();
        try { checks.Add(new { Name = "Discord Gateway",  Healthy = _client?.ConnectionState == Discord.ConnectionState.Connected, Kind = (_client?.ConnectionState == Discord.ConnectionState.Connected) ? "ok" : "err", Message = _client?.ConnectionState == Discord.ConnectionState.Connected ? $"Подключено ({_client.Latency} мс)" : $"Не подключено ({_client?.ConnectionState})" }); } catch { }
        try { var guildsCount = _client?.Guilds?.Count ?? 0; checks.Add(new { Name = "Серверы Discord", Healthy = guildsCount > 0, Kind = guildsCount > 0 ? "ok" : "err", Message = guildsCount > 0 ? $"Доступно: {guildsCount}" : "Нет доступных серверов" }); } catch { }
        try { var cfgN = _serverConfigs?.Count ?? 0; checks.Add(new { Name = "Конфигурация", Healthy = cfgN > 0, Kind = cfgN > 0 ? "ok" : "err", Message = cfgN > 0 ? $"Настроено: {cfgN}" : "Нет настроенных серверов" }); } catch { }
        try
        {
            var predEnabled = _serverConfigs?.Values?.Count(c => c.PredictionsEnabled) ?? 0;
            checks.Add(new { Name = "Прогнозы", Healthy = predEnabled > 0, Kind = predEnabled > 0 ? "ok" : "mute", Message = predEnabled > 0 ? $"Включены на {predEnabled} серверах" : "Не включены ни на одном сервере" });
        }
        catch { }
        try
        {
            if (_telegramNotifier != null)
            {
            var probes = new List<string>();
            foreach (var kvp in _serverConfigs ?? new Dictionary<ulong, ServerConfig>())
            {
            if (!kvp.Value.TelegramEnabled) continue;
                var r = _telegramNotifier.ProbeAsync(kvp.Key).GetAwaiter().GetResult();
            probes.Add($"{kvp.Key}: {(r.Success ? "OK" : r.Message)}");
            }
                var ok = probes.Count > 0 && probes.All(s => s.EndsWith("OK"));
            checks.Add(new { Name = "Telegram", Healthy = ok, Kind = ok ? "ok" : (probes.Count == 0 ? "mute" : "err"), Message = probes.Count > 0 ? string.Join("\n", probes) : "Telegram-интеграция выключена на всех серверах" });
            }
            else
            {
            checks.Add(new { Name = "Telegram", Healthy = false, Kind = "mute", Message = "Telegram-нотификатор отключён в конфиге" });
            }
        }
        catch (Exception ex) { checks.Add(new { Name = "Telegram", Healthy = false, Kind = "err", Message = ex.Message }); }
        try { checks.Add(new { Name = "Голосовые поинты", Healthy = _voicePointsService != null, Kind = (_voicePointsService != null) ? "ok" : "err", Message = _voicePointsService != null ? "OK" : "Сервис не инициализирован" }); } catch { }
        try { checks.Add(new { Name = "Хранилище поинтов", Healthy = _pointsService != null, Kind = (_pointsService != null) ? "ok" : "err", Message = _pointsService != null ? "OK" : "Сервис не инициализирован" }); } catch { }
        try
        {
            if (_googleSheetsService == null)
            {
            checks.Add(new { Name = "Google Sheets", Healthy = false, Kind = "mute", Message = "Отключено (google_credentials.json не задан)" });
            }
            else
            {
            var probe = _googleSheetsService.ProbeAsync().GetAwaiter().GetResult();
            checks.Add(new { Name = "Google Sheets", Healthy = probe.Success, Kind = probe.Success ? "ok" : "err", Message = probe.Message });
            }
        }
        catch (Exception ex) { checks.Add(new { Name = "Google Sheets", Healthy = false, Kind = "err", Message = ex.Message }); }
        try
        {
            if (_lavalinkService == null)
            {
            checks.Add(new { Name = "Музыка (Lavalink)", Healthy = false, Kind = "mute", Message = "Отключено (Music.Enabled=false)" });
            }
            else
            {
            var probeErr = _lavalinkService.ProbeAsync().GetAwaiter().GetResult();
            var procOk = string.IsNullOrEmpty(probeErr);
            checks.Add(new { Name = "Музыка (Lavalink)", Healthy = procOk, Kind = procOk ? "ok" : "err", Message = procOk ? $"Отвечает на {_config!.Music.Host}:{_config!.Music.Port}/version" : $"Не отвечает: {probeErr}" });
            }
        }
        catch (Exception ex) { checks.Add(new { Name = "Музыка (Lavalink)", Healthy = false, Kind = "err", Message = ex.Message }); }
        try
        {
            var loaded = _textBlocks?.Count ?? 0;
            var kind = loaded > 0 ? "ok" : "info";
            checks.Add(new { Name = "Текстовые блоки (Pastes.txt)", Healthy = loaded > 0, Kind = kind, Message = loaded > 0 ? $"Загружено {loaded} шаблонов" : "Файл отсутствует — шаблоны пустые (не критично)" });
        }
        catch (Exception ex) { checks.Add(new { Name = "Текстовые блоки (Pastes.txt)", Healthy = false, Message = ex.Message }); }
        return checks;
        });
        }
        BotLogger.Info(LogCategory.System, "[WebDashboard] Program constructor: вызываю _webDashboard.Start()");
    try
    {
        _webDashboard.Start();
        BotLogger.Info(LogCategory.System, "[WebDashboard] Program constructor: _webDashboard.Start() вернул управление");
    }
    catch (Exception ex)
    {
        BotLogger.Error(LogCategory.System,
            $"[WebDashboard] Program constructor: _webDashboard.Start() БРОСИЛ исключение: {ex.GetType().Name}: {ex.Message}");
        BotLogger.Error(LogCategory.System, $"[WebDashboard] Stack: {ex.StackTrace}");
        throw;
    }

    _googleSheetsService = GoogleSheetsService.TryCreate(_config!);
    if (_googleSheetsService != null)
    _googleSheetsService.LogSink = msg => BotLogger.Info(LogCategory.Sheets, msg);

    // Сервисы для костяшек
    var pointsPath = BotConfig.ResolvePath(Path.Combine(BotConfig.DataFolderName, "points.json"));
    _pointsService = new PointsService(pointsPath);
    // Загрузка балансов костяшек из файла
    _pointsService.LoadAsync().GetAwaiter().GetResult();

    var pointsUsersPath = BotConfig.ResolvePath(Path.Combine(BotConfig.DataFolderName, "points_users.json"));
    _pointsUserIndex = new PointsUserIndex(pointsUsersPath);
    _pointsUserIndex.LoadAsync().GetAwaiter().GetResult();

    _predictionService = new PredictionService(_client!, _pointsService);
        // ✅ R6 fix: подписываемся на resolve/cancel прогноза, чтобы почистить _pendingBetUi
        // для затронутой гильдии (удаляем «висящие» кнопки «Продолжить»).
        if (_predictionService != null)
        {
        _predictionService.PredictionResolved += OnPredictionResolvedForUi;
        _predictionService.PredictionCancelled += OnPredictionCancelledForUi;
            // ✅ Round 7-C6: убрали преждевременный LoadStateOnStartupAsync.
            // Раньше он звался здесь ДО BootstrapFirstRunSettingsAsync и
            // Race-конкурировал с SaveStateAsync. Теперь вся загрузка
            // проходит ОДИН раз, синхронно, в ЭТАП 3/4 — через
            // _predictionService.RunStage3RestoreAsync().
            }
            // ✅ Round 7-C8: подключаем мост из GameSession → PredictionService,
            // чтобы cleanup осиротевших сессий мог отменить связанный
            // с событием прогноз.
            GameSessionPredictionBridge.PredictionServiceAccessor = () => _predictionService;
        _voicePointsService = new VoicePointsService(_client!, _pointsService, GetServerConfigInternal, IsActiveEventOnChannel);

    // Инициализация музыкального сервиса (задел: запуск будет выполнен в OnReady)
    if (_config.Music.Enabled)
    {
    _lavalinkService = new LavalinkService(() => _client!, _config.Music);
    _lavalinkService.LogSink = msg => BotLogger.Info(LogCategory.Music, msg);
    _playlistStore = new MusicPlaylistStore(AppContext.BaseDirectory);
        _ = _playlistStore.LoadAsync();
        _musicQueueStore = new MusicQueueStore(AppContext.BaseDirectory);
        _musicStats = MusicStats.LoadAsync(AppContext.BaseDirectory).GetAwaiter().GetResult();
        _musicCommands = new MusicCommands(_lavalinkService, _client, _playlistStore, _musicQueueStore, _musicStats);
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
    .AddSingleton(_predictionService!)
    .AddSingleton(_voicePointsService)
    .AddSingleton<QueueModule>()
    .AddSingleton<InfoCommands>()
    .AddSingleton<RollDiceCommands>()
    .AddSingleton<GameSessionCommands>()
    .AddSingleton<VoiceChannelCommands>()
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
                /// <summary>Источник текущего старта/рестарта: "console" | "chat" | "scheduler" | "discord" | null.</summary>
                private string? _startupInitiator;
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

    /// <summary>
    /// ✅ Bug 6 / Round 7-A2: помечает «следующее отключение — это рестарт»,
    /// чтобы PredictionService.OnClientDisconnected отличил его от «настоящего» offline.
    /// Файл удаляется в PredictionService при первом успешном Ready после рестарта.
    /// </summary>
    public static void WriteRestartPendingFlag()
    {
    try
    {
        var path = BotConfig.ResolvePath(Path.Combine(BotConfig.DataFolderName, ".restart_pending"));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
    }
    catch
    {
        // Не критично: если флаг не записан — следующий Disconnected будет «offline».
    }
    }

    /// <summary>
    /// Подчищает застрявшие *.lock sidecar'ы в Data/. Удаляем только файлы старше 30 мин
    /// (свежий .lock = живой параллельный процесс, его нельзя трогать). Безопасно даже
    /// если файл лежит легитимно — после 30 мин простоя считаем процесс мёртвым.
    /// </summary>
    public static void CleanupStaleSidecarLocks()
    {
        try
        {
            var dataDir = BotConfig.ResolvePath(BotConfig.DataFolderName);
            if (!Directory.Exists(dataDir)) return;

            var nowUtc = DateTimeOffset.UtcNow;
            var staleThreshold = TimeSpan.FromMinutes(30);
            int removed = 0;
            foreach (var lockPath in Directory.EnumerateFiles(dataDir, "*.lock", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(lockPath);
                    if (nowUtc - info.LastWriteTimeUtc < staleThreshold) continue;

                    // На всякий случай: проверяем, не держит ли файл кто-то прямо сейчас.
                    // Если занят — кто-то живой, пропускаем. Иначе удаляем.
                    using (var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite,
                                                       FileShare.None, 0, FileOptions.DeleteOnClose))
                    {
                        // Если мы смогли открыть с FileShare.None — значит никто другой не держит.
                        // Dispose закроет; DeleteOnClose удалит файл.
                    }
                    removed++;
                }
                catch (IOException)
                {
                    // Занят другим процессом — пропускаем, он живой.
                }
                catch { }
            }
            if (removed > 0)
            {
                BotLogger.Warn(LogCategory.System,
                    $"[Program] CleanupStaleSidecarLocks: удалено {removed} застрявших *.lock старше 30 мин в {dataDir}");
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn(LogCategory.System,
                $"[Program] CleanupStaleSidecarLocks: ошибка обхода: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Снимает флаг «идёт рестарт» после успешного Ready.
    /// Вызывается из PredictionService и OnReady.
    /// </summary>
    public static void ClearRestartPendingFlag()
    {
        var path = BotConfig.ResolvePath(Path.Combine(BotConfig.DataFolderName, ".restart_pending"));
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (Exception ex)
            {
                // 🩹 prod-sidecar-stuck: раньше проглатывали молча, и если файл
                // был занят другим процессом (например, параллельной dev-машиной
                // или антивирусом), он висел вечно. Теперь ретраим 3 раза и
                // логируем, если так и не получилось.
                if (attempt == 3)
                {
                    BotLogger.Warn(LogCategory.System,
                        $"[Program] ClearRestartPendingFlag: не удалось удалить {path} после 3 попыток: {ex.GetType().Name}: {ex.Message}");
                }
                else
                {
                    try { Thread.Sleep(200); } catch { }
                }
            }
        }
    }

                    /// <summary>
                    /// Снимает одноразовый флаг «запрошен полный старт» (.full_start_request).
                    /// Создаётся вручную или через env RPBOT_FULL_START=1, чтобы при следующем запуске
                    /// бот прошёл полную регистрацию команд (StartupType=FirstStart).
                    /// </summary>
                    public static void ClearFullStartRequestFlag()
                    {
                        try
                        {
                            var path = BotConfig.ResolvePath(Path.Combine(BotConfig.DataFolderName, ".full_start_request"));
                            if (File.Exists(path)) File.Delete(path);
                        }
                        catch (Exception ex)
                        {
                            BotLogger.Warn(LogCategory.System,
                                $"[Program] ClearFullStartRequestFlag: не удалось удалить файл: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

        private async Task RestartWithReasonAsync(string initiator, string reason)
        {
        // Идемпотентность, чтобы не запускать рестарт повторно из разных потоков
        if (_shouldExit)
        return;

        // Останавливаем планировщик, чтобы он не сработал повторно во время выключения
        StopDailyRestartScheduler();

        // ✅ Bug 6 / Round 7-A2: сообщаем PredictionService, что это restart, а не offline.
        WriteRestartPendingFlag();

        // ✅ Round 7-C7: отправляем «Бот ушёл…» в каналы прогнозов ДО _client.StopAsync(),
        // пока Discord-клиент ещё живой. Иначе OnClientDisconnected попадёт в disposed
        // HttpClient и сообщение в канал не уйдёт. kind="restart" даёт текст
        // «Бот ушёл на перезагрузку» (а не «…завершил работу»).
        try
        {
        if (_predictionService != null)
        await _predictionService.AnnounceShutdownAsync("restart").ConfigureAwait(false);
        }
        catch { }

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
        _startupInitiator = initiator;
        _nextStartupType = StartupType.Restart;
        _nextStartupReason = _startupReason;
        _statusNotifier?.SetStartupContext(StartupType.Restart, _startupReason);
    _reconnectionService?.Shutdown();

    // При ежедневной перезагрузке — очищаем висящие сессии со статистикой
    if (string.Equals(reason, "Ежедневная перезагрузка", StringComparison.OrdinalIgnoreCase))
    {
    try { await GameSessionCommands.ClearSessionsOnDailyRestartAsync(_client!); } catch { }
    }

    // Отменяем фоновый мониторинг и ждём его завершения
    try { _backgroundMonitoringCts?.Cancel(); } catch { }

    // Stop Discord client (best-effort)
    try { if (_client != null) await _client.StopAsync(); } catch { }

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

    // ✅ Round 7-C7: отправляем «Бот ушёл…» в каналы прогнозов ДО _client.StopAsync(),
    // пока Discord-клиент ещё живой. Иначе OnClientDisconnected попадёт в disposed
    // HttpClient и сообщение в канал не уйдёт. kind="stop" даёт текст
    // «Бот завершил работу» (а не «…на перезагрузку»).
    try
    {
        if (_predictionService != null)
        await _predictionService.AnnounceShutdownAsync("stop").ConfigureAwait(false);
    }
    catch { }

    if (_ui != null && _uiStarted)
    {
        _ui.AddLog(startupLogMessage);
    }

    _shouldExit = true;
        StopDailyRestartScheduler();
        _reconnectionService?.Shutdown();

        // Останавливаем планировщик суточных папок логов
        try { _logDayRollover?.Dispose(); _logDayRollover = null; } catch { }

        // Отменяем фоновый мониторинг
        try { _backgroundMonitoringCts?.Cancel(); } catch { }

        try { if (_client != null) await _client.StopAsync(); } catch { }

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

/// <summary>
/// Корректная остановка без выхода из процесса (graceful shutdown без Environment.Exit).
/// Используется в качестве хука на сигналы Windows (Ctrl+C / logoff) — дашборд и фоновые
/// таймеры гасятся в том же порядке, что и в DisposeAsync, но процесс продолжает жить.
/// </summary>
public async Task GracefulShutdownAsync(string reason)
{
    _shouldExit = true;
    try { await LogStartup($"[SHUTDOWN] graceful shutdown: {reason}"); } catch { }

    // Сообщаем в Discord о завершении работы — пользовательские сценарии:
    // крестик в окне консоли, диспетчер задач "Завершить", taskkill /F,
    // logoff, SIGTERM. До рефакторинга этого не было — пропало.
    try
    {
    if (_statusNotifier != null)
    await _statusNotifier.SendShutdownNotification(reason);
    }
    catch { }

        // ✅ Round 7-C7: для graceful shutdown (Ctrl+C, SIGTERM, logoff) тоже отправляем
        // «Бот ушёл…» в каналы прогнозов ДО того, как _client будет отключен.
        // Это решает гонку с disposed HttpClient. kind="stop" даёт текст
        // «Бот завершил работу» (это полное завершение, не рестарт).
        try
        {
        if (_predictionService != null)
        await _predictionService.AnnounceShutdownAsync("stop").ConfigureAwait(false);
        }
        catch { }

        try { _backgroundMonitoringCts?.Cancel(); } catch { }
        StopDailyRestartScheduler();
                StopPersistenceFlushLoop();

            try { _reconnectionService?.Shutdown(); } catch { }

                        // ✅ Persistence-fix: синхронный флаш рантайм-сторов на shutdown,
                        // чтобы Ctrl+C / SIGTERM / logoff не теряли финальное состояние.
                        try { await GameSessionCommands.SaveSessionsAsync().ConfigureAwait(false); } catch { }
                        try { if (_pointsService != null) await _pointsService.SaveAsync().ConfigureAwait(false); } catch { }
                        try { if (_predictionService != null) _predictionService.Shutdown(); } catch { }

            try { if (_webDashboard != null) await _webDashboard.StopAsync(); } catch { }
            try { _webDashboard?.Dispose(); } catch { }
            _webDashboard = null;

            try { if (_ui != null && _uiStarted) { _ui.Dispose(); _ui = null; _uiStarted = false; } } catch { }
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

// ✅ Persistence-fix: периодический флаш runtime-сторов каждые 5 минут
// как страховка от kill/BSOD между изменениями и fire-and-forget записью.
private CancellationTokenSource? _persistenceFlushCts;
private Task? _persistenceFlushTask;

private void StartPersistenceFlushLoop()
{
    if (_persistenceFlushTask != null && !_persistenceFlushTask.IsCompleted) return;
    StopPersistenceFlushLoop();
    _persistenceFlushCts = new CancellationTokenSource();
    _persistenceFlushTask = Task.Run(() => PersistenceFlushLoopAsync(_persistenceFlushCts.Token));
}

private void StopPersistenceFlushLoop()
{
    try { _persistenceFlushCts?.Cancel(); } catch { }
    try { _persistenceFlushCts?.Dispose(); } catch { }
    _persistenceFlushCts = null;
}

private async Task PersistenceFlushLoopAsync(CancellationToken ct)
{
    try
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _ = GameSessionCommands.SaveSessionsAsync();
                if (_pointsService != null)
                    _ = _pointsService.SaveAsync();
            }
            catch (Exception ex)
            {
                try { await LogStartup($"[PersistLoop] {ex.GetType().Name}: {ex.Message}"); } catch { }
            }

            await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        }
    }
    catch (TaskCanceledException) { /* нормальный shutdown */ }
    catch (Exception ex)
    {
        try { await LogStartup($"[PersistLoop] {ex.GetType().Name}: {ex.Message}"); } catch { }
    }
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
        MoscowTime.TryGetTimeZone(out var mskTz) && mskTz != null &&
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

            var guild = _client?.GetGuild(guildId);

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
                        if (value.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
                            sconfig.SwearWords = new List<string>();
                        else
                            sconfig.SwearWords = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(x => x.Trim().ToLowerInvariant())
                                .Where(x => x.Length > 0)
                                .ToList();
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
                #pragma warning disable CS0618 // ✅ pred-parallelization: deprecated setter, сохраняем для обратной совместимости /config set
                                    {
                                        var resolved = await ResolveChannelIdAsync(guildId, channelId, value, requireVoice: true);
                                        if (resolved.HasValue) sconfig.EventVoiceChannelID = resolved.Value;
                                    }
                #pragma warning restore CS0618
                                    break;
    default:
    break;
    }

    SaveServerConfigs();

            try
            {
                var validationGuild = _client?.GetGuild(guildId);
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

            SaveServerConfigs();
            return Task.CompletedTask;
        }

        public void SetStartupType(StartupType type)
        {
            SetStartupContext(type, null);
        }

        public void SetStartupContext(StartupType type, string? reason)
        {
                    SetStartupContext(type, reason, initiator: null);
                }

                public void SetStartupContext(StartupType type, string? reason, string? initiator)
                {
                    _currentStartupType = type;
                    _startupReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
                    _startupInitiator = string.IsNullOrWhiteSpace(initiator) ? null : initiator;
                    _statusNotifier?.SetStartupContext(type, _startupReason);
                }

        static async Task Main(string[] args)
                {
                    // 🩹 perf: AboveNormal priority — Windows не отдаёт CPU боту фоновым
                    // процессам (Windows Update, антивирус, OneDrive). При нормальных GC
                    // паузах в десятки мс это не спасёт, но против микрофризов диспетчера
                    // (10-50 мс на фоне) — помогает не пропустить Discord ACK.
                    try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; } catch { }

                    // 🩹 perf: ServicePointManager tweaks. По умолчанию .NET держит только 2
                    // соединения на хост — это узкое место, когда несколько slash-команд
                    // приходят параллельно. 50 — типичный рекомендуемый предел.
                    try
                    {
                        System.Net.ServicePointManager.DefaultConnectionLimit = 50;
                        // Nagle отключаем — маленькие ACK-пакеты не склеиваются и уходят сразу.
                        System.Net.ServicePointManager.UseNagleAlgorithm = false;
                        // Expect100Continue добавляет RTT к каждому POST, бесполезно для Discord API.
                        System.Net.ServicePointManager.Expect100Continue = false;
                    }
                    catch { }

                    // 🩹 perf: DNS prewarm. При первом обращении к discord.com через HttpClient
                    // .NET резолвит DNS синхронно (10-100 мс на холодную систему). Резолвим заранее
                    // — на свежем процессе, когда бот ещё не принял ни одной команды.
                    try
                    {
                        _ = System.Net.Dns.GetHostAddressesAsync("discord.com");
                        _ = System.Net.Dns.GetHostAddressesAsync("gateway.discord.gg");
                        _ = System.Net.Dns.GetHostAddressesAsync("cdn.discordapp.com");
                    }
                    catch { }

                    // Гарантированное завершение Lavalink при любом способе остановки (VS Stop, taskkill и т.д.)
                    // ⚠️ ТОЛЬКО в Windows-режиме (бот сам поднимает Lavalink через StartLavalinkProcessAsync,
                    //    и должен его убить, иначе останется orphan java-процесс).
                    // На Linux-деплое Lavalink поднимается отдельным lavalink.service, и KillOrphanedLavalink
                    // убьёт его при systemctl restart rpbot → следующий rpbot стартанёт без Lavalink и упадёт
                    // на инициализации музыки (Connection refused).
                    bool isLinuxDeployment = !OperatingSystem.IsWindows();
                    if (!isLinuxDeployment)
                    {
                        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillOrphanedLavalink();
                        Console.CancelKeyPress += (_, e) => { e.Cancel = true; KillOrphanedLavalink(); };
                    }

            // Хук на корректную остановку без Environment.Exit — при SIGINT/SIGTERM
            // (taskkill, Ctrl+C, диспетчер задач "Завершить") бот успевает погасить
            // дашборд и UI до того, как процесс начнут снимать принудительно.
            Program? activeProgram = null;
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                var prog = activeProgram;
                if (prog == null) return;
                try { _ = prog.GracefulShutdownAsync("Ctrl+C"); } catch { }
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                var prog = activeProgram;
                if (prog == null) return;
                // ProcessExit рубит процесс через ~2 секунды. Оборачиваем уведомление
                // в fire-and-forget с жёстким ожиданием, чтобы Discord успел
                // получить embed «⏹️ БОТ ОСТАНОВЛЕН» (это то, что пропало).
                try
                {
                    var task = prog.GracefulShutdownAsync("ProcessExit");
                    if (!task.Wait(1500))
                        BotLogger.Warn(LogCategory.System, "Shutdown notification timed out in ProcessExit");
                }
                catch { }
            };

            bool restart;
            int restartCount = 0;
            var pendingStartupType = StartupType.FirstStart;
            string? pendingStartupReason = null;

                        // Политика регистрации команд: регистрируем только при «полном старте».
                        // Любой рестарт (systemd, ручной, авто) — НЕ трогаем Discord.
                        // Полный старт запрашивается явно через:
                        //   1) env RPBOT_FULL_START=1 — для первого деплоя / принудительной перерегистрации;
                        //   2) файл-флаг {DataFolder}/.full_start_request — удобно для отладки
                        //      (создаётся вручную, удаляется автоматически после успешного старта).
                        // Внутрипроцессный рестарт через RestartWithReasonAsync дополнительно
                        // проставляет .restart_pending — это второй источник истины.
                        bool isFullStartRequested = false;
                        try
                        {
                            var envFlag = Environment.GetEnvironmentVariable("RPBOT_FULL_START");
                            if (!string.IsNullOrEmpty(envFlag) && envFlag != "0" && envFlag.ToLowerInvariant() != "false")
                            {
                                isFullStartRequested = true;
                                BotLogger.Info(LogCategory.Boot, "Запрошен полный старт через env RPBOT_FULL_START=1");
                            }
                            else
                            {
                                var fullStartFlag = BotConfig.ResolvePath(Path.Combine(BotConfig.DataFolderName, ".full_start_request"));
                                if (File.Exists(fullStartFlag))
                                {
                                    isFullStartRequested = true;
                                    BotLogger.Info(LogCategory.Boot, "Запрошен полный старт через флаг .full_start_request");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            BotLogger.Warn(LogCategory.Boot,
                                $"Ошибка проверки полного-старта флага: {ex.GetType().Name}: {ex.Message}");
                        }

                        // Определяем «FirstStart vs Restart»:
                        //   - явный запрос полного старта     → FirstStart;
                        //   - флаг .restart_pending из сессии → Restart (внутрипроцессный рестарт);
                        //   - иначе                            → Restart (systemd/systemd restart, после первого старта).
                        try
                        {
                            var flagPath = BotConfig.ResolvePath(Path.Combine(BotConfig.DataFolderName, ".restart_pending"));
                            if (!isFullStartRequested)
                            {
                                pendingStartupType = StartupType.Restart;
                                pendingStartupReason = "Restart после перезапуска процесса";
                                if (File.Exists(flagPath))
                                    BotLogger.Info(LogCategory.Boot, "Найден .restart_pending → StartupType = Restart");
                                else
                                    BotLogger.Info(LogCategory.Boot, "Дефолт: StartupType = Restart (пропускаем регистрацию команд)");
                            }
                            else
                            {
                                pendingStartupType = StartupType.FirstStart;
                                pendingStartupReason = null;
                                BotLogger.Info(LogCategory.Boot, "Запрошен полный старт → StartupType = FirstStart (регистрация команд)");
                            }
                        }
                        catch (Exception ex)
                        {
                            BotLogger.Warn(LogCategory.Boot,
                                $"Не удалось проверить .restart_pending: {ex.GetType().Name}: {ex.Message}");
                        }

            do
            {
                restart = false;

                if (restartCount > 0)
                {
                // Очистка консоли отключена; уведомление через UI
                _ = Task.Run(() => BotLogger.Info(LogCategory.Boot, $"ПЕРЕЗАПУСК #{restartCount} в {DateTime.Now:HH:mm:ss}"));
                }

                using (var program = new Program())
                {
                    program.SetStartupContext(pendingStartupType, pendingStartupReason);
                    activeProgram = program;

                    await program.RunBotAsync();
                    restart = program.ShouldRestart;
                    pendingStartupType = restart ? program.NextStartupType : StartupType.FirstStart;
                    pendingStartupReason = restart ? program.NextStartupReason : null;
                    restartCount++;
                    activeProgram = null;
                }

                if (restart)
                {
                    BotLogger.Info(LogCategory.Boot, "Подготовка к перезапуску...");
                    await Task.Delay(2000); // Небольшая пауза перед перезапуском
                }

            } while (restart);

    BotLogger.Info(LogCategory.Boot, "Бот остановлен.");
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

/// <summary>
/// Простой классификатор сообщения лога по категории (по подстроке в тексте).
/// Используется только для подсветки в дашборде — не влияет на содержимое.
/// </summary>
public static LogCategory? DetectCategory(string msg)
{
        foreach (LogCategory cat in Enum.GetValues<LogCategory>())
        {
        var token = cat.ToString();
        if (msg.Contains(token, StringComparison.OrdinalIgnoreCase))
                return cat;
        }
        return null;
}

/// <summary>Маппинг категории на CSS-класс для дашборда.</summary>
public static string CategoryCssClass(LogCategory category) => category switch
{
        LogCategory.Rolls   => "cat-rolls",
        LogCategory.Music   => "cat-music",
        LogCategory.Predict => "cat-predict",
        LogCategory.Points  => "cat-points",
        LogCategory.Session => "cat-session",
        LogCategory.System  => "cat-system",
        LogCategory.Boot    => "cat-boot",
        LogCategory.Discord => "cat-discord",
        LogCategory.Sheets  => "cat-sheets",
        LogCategory.Config  => "cat-config",
        LogCategory.Cmd     => "cat-cmd",
        _                   => "cat-other",
};

/// <summary>Маппинг уровня лога на CSS-класс для дашборда.</summary>
public static string LevelCssClass(LogLevel level) => level switch
{
        LogLevel.Debug => "lv-debug",
        LogLevel.Info  => "lv-info",
        LogLevel.Warn  => "lv-warn",
        LogLevel.Error => "lv-error",
        _              => "lv-other",
};

private static BotUI? _ui;
        private static bool _uiStarted = false;

        /// <summary>
        /// Регистрирует событие для счётчиков дашборда.
        /// Вызывается из обработчиков бросков / сообщений / голосовых событий.
        /// </summary>
        public void RegisterActivity()
        {
            // Скользящее окно: добавляем текущую минуту + чистим старые
            var minute = new DateTimeOffset(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day,
                                            DateTime.UtcNow.Hour, DateTime.UtcNow.Minute, 0, TimeSpan.Zero);
            lock (_activityLock)
            {
                _activityBuckets.Enqueue(new ActivityBucket(minute, 1));
                var cutoff = DateTimeOffset.UtcNow.AddMinutes(-ActivityWindowMinutes);
                while (_activityBuckets.TryPeek(out var oldest) && oldest.Minute < cutoff)
                    _activityBuckets.TryDequeue(out _);
            }

            // Сброс дневных счётчиков при переходе через полночь UTC
            var today = DateTime.UtcNow.Date;
            if (today != _lastCountersResetDate)
            {
                Interlocked.Exchange(ref _rollsTodayCount, 0);
                Interlocked.Exchange(ref _chatMessagesTodayCount, 0);
                Interlocked.Exchange(ref _usersInVoiceCount, 0);
                _lastCountersResetDate = today;
            }
        }

        public void IncrementRollsToday() { RegisterActivity(); Interlocked.Increment(ref _rollsTodayCount); }
        public void IncrementChatToday()   { RegisterActivity(); Interlocked.Increment(ref _chatMessagesTodayCount); }

        private void RefreshUsersInVoice()
        {
            try
            {
                if (_client == null) return;
                int total = 0;
                foreach (var g in _client.Guilds)
                    total += g.Users.Count(u => !u.IsBot && u.VoiceChannel != null);
                Interlocked.Exchange(ref _usersInVoiceCount, total);
            }
            catch { /* для счётчика безопаснее молча */ }
        }

        private IReadOnlyList<ActivityBucket> GetActivityBuckets()
        {
            // Обновляем счётчик пользователей в голосе раз в обращение
            RefreshUsersInVoice();
            // Склеиваем корзинки по минутам и отдаём в порядке возрастания времени
            Dictionary<DateTimeOffset, int> agg;
            lock (_activityLock)
            {
                var cutoff = DateTimeOffset.UtcNow.AddMinutes(-ActivityWindowMinutes);
                agg = _activityBuckets
                    .Where(b => b.Minute >= cutoff)
                    .GroupBy(b => b.Minute)
                    .ToDictionary(g => g.Key, g => g.Sum(b => b.Count));
            }
            return agg
                .OrderBy(kv => kv.Key)
                .Select(kv => new ActivityBucket(kv.Key, kv.Value))
                .ToList();
        }

        public async Task RunBotAsync()
{
    _startupTime = DateTime.UtcNow;
    _startupTimeUtc = DateTimeOffset.UtcNow;
    _rollsTodayCount = 0;
    _chatMessagesTodayCount = 0;
    _usersInVoiceCount = 0;
    _lastCountersResetDate = DateTime.UtcNow.Date;
    lock (_activityLock) _activityBuckets.Clear();

    if (_ui == null)
    {
    var logDirRaw = _config?.LogDirectory;
    var logDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDirRaw) ? "Logs" : logDirRaw);
    // ✅ BotLogger теперь инициализируется раньше — в constructor Program, чтобы логи
    // WebDashboardService.Start() и других сервисов constructor-уровня не терялись.
    // Здесь — только ленивый Initialize на случай, если constructor был пропущен
    // (например, юнит-тест, который не идёт через Program()).
    if (!_loggerInitialized)
    {
        BotLogger.Initialize(logDir, DateTime.Now);
        _loggerInitialized = true;
    }
    // Возвращаем строку-маркер: заголовок «=== Бот запускается: … ===»
    // пишется только в run.log (в самом файле), в Logs Panel он не виден,
    // поэтому для пользователя в терминале нужна явная запись отсюда.
    BotLogger.Info(LogCategory.Boot, "=== Бот запускается ===");

    // ✅ Суточные папки логов: планировщик уже запущен в constructor-е Program
    // сразу после BotLogger.Initialize (см. RunBotAsync preamble). Здесь только
    // подстраховываемся на случай, если первый Initialize не отработал
    // (например, тестовый путь выполнения).
    if (_logDayRollover == null)
    {
        _logDayRollover = new LogDayRolloverService(logDir);
        _logDayRollover.Start();
    }

    _ui = new BotUI(
    _client!,
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

                // Подключаем BotLogger к UI-терминалу (цветная индикация — в дашборде)
                BotLogger.SetUiSink(msg => _ui?.AddLog(msg));

                // Подключаем StartupRenderer к sinks (один раз за процесс).
                // FileSink пишет в единый logs/run.log (BotLogger.UnifiedLogPath),
                // чтобы всё (старт + рантайм) было в одном файле.
                var startupRenderer = StartupRenderer.Instance;
                if (!_startupSinksAttached)
                {
                    // Первый запуск процесса — снимаем всё, что могло накопиться (на случай если).
                    startupRenderer.ClearSinks();
                    // Единственный sink для StartupRenderer. Он пишет в observer
                    // (дашборд), в run.log (FileSink-часть внутри BotLogger.WriteStartupFull),
                    // и в UI-sink. Никаких ConsoleSink/UiSink/FileSink/BotLogger-Info
                    // параллельно — это и был источник дублей в терминале.
                    var bootLoggerSink = new BotLoggerSink();
                    startupRenderer.AttachSink(bootLoggerSink);
                    _attachedSinks.Clear();
                    _attachedSinks.Add(bootLoggerSink);
                    _startupSinksAttached = true;
                }

                CommandLogSink = msg => BotLogger.Info(LogCategory.Cmd, msg);

                // Перенаправляем Errors в UI-панель (без Console — это и был источник дублей)
                Console.SetError(new UiTextWriter(() => _ui));
            }
            else
            {
                // При рестарте просто обновляем сервисы
                            _ui!.UpdateServices(_client!, _reconnectionService!, _connectionPredictor!, _statusNotifier!, isRestart: true);
                _ui.AddLog("Перезапуск бота...");

                BotLogger.Info(LogCategory.Boot, "=== Рестарт ===");
                BotLogger.SetUiSink(msg => _ui?.AddLog(msg));
                CommandLogSink = msg => BotLogger.Info(LogCategory.Cmd, msg);
            }

    // Загрузка текстовых блоков из пути конфига (относительные пути считаем от каталога приложения)
    _textBlocks = LoadTextFromFile(BotConfig.ResolvePath(_config!.TextBlocksPath));

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
                            StartupRenderer.Instance.WriteHeader(_currentStartupType == StartupType.Restart
                                ? "ЗАПУСК ПОСЛЕ ПЕРЕЗАГРУЗКИ"
                                : "ЗАПУСК");
                            if (_currentStartupType == StartupType.Restart)
                            {
                                StartupRenderer.Instance.WriteLine($"Инициализация бота после перезапуска... Версия {version}");
                            }
                            else
                            {
                                StartupRenderer.Instance.WriteLine($"Инициализация бота... Версия {version}");
                            }

                    if (_client == null || _client.ConnectionState == ConnectionState.Disconnected)
                    {
                        // Фоновая задача на этом интервале могла ещё использовать старый клиент.
                        // Останавливаем её, чтобы BackgroundMonitoringLoop не дёргал disposed семафор.
                        try { _backgroundMonitoringCts?.Cancel(); } catch { }
                        try { _backgroundMonitoringCts?.Dispose(); } catch { }
                        _backgroundMonitoringCts = null;

                        // Отписываем ВСЕ обработчики от старого клиента и утилизируем
                        // его ОТДЕЛЬНО от CleanupServices — там это делать поздно.
                        VoiceChannelCommands.ResetForNewClient();
                        DisposeClientSafely(_client);
                        _client = CreateDiscordClient();
                        VoiceChannelCommands.SetClient(_client);

                        CleanupServices();

                        _reconnectionService = new ReconnectionService(_client!)
                        {
                            LogSink = ServiceLogSink
                        };
                        _connectionPredictor = new ConnectionPredictor(_reconnectionService, _config!.Prediction);
                        _statusNotifier = new StatusNotifier(_client!, GetServerConfigInternal)
                        {
                            LogSink = ServiceLogSink
                        };
                        _statusNotifier.SetStartupContext(_currentStartupType, _startupReason);

                        // TelegramNotifier не зависит от DiscordSocketClient напрямую, но пересоздаём его
                        // вместе с остальными сервисами при рестарте/реконнекте — на случай "протухшего"
                        // HttpClient или устаревшего замыкания на _serverConfigs.
                        try { _telegramNotifier?.Dispose(); } catch { }
                        _telegramNotifier = new TelegramNotifier(guildId =>
                        {
                            return _serverConfigs != null && _serverConfigs.TryGetValue(guildId, out var sc) ? sc : null;
                        });
                                                // ✅ bug-fix: EventAnnouncer._telegramNotifier был readonly,
                                                // поэтому при реконнекте (или при первом запуске, когда
                                                // блок if(_client.ConnectionState==Disconnected) сработал)
                                                // оставался указатель на уже-disposed notifier, и resync на
                                                // старте падал с ObjectDisposedException на каждой Telegram-публикации.
                                                _eventAnnouncer?.SetTelegramNotifier(_telegramNotifier);
                                                _eventOpsLifecycle?.SetTelegramNotifier(_telegramNotifier);

                                                _predictionService = new PredictionService(_client!, _pointsService);
                                                // ✅ R6 fix: см. первичную инициализацию — обработчики тоже подписываем.
                                                if (_predictionService != null)
                                                {
                                                    _predictionService.PredictionResolved += OnPredictionResolvedForUi;
                                                    _predictionService.PredictionCancelled += OnPredictionCancelledForUi;
                                                    // ✅ Round 7-C6: убрали преждевременный LoadStateOnStartupAsync
                                                    // и в restart-loop. Вся загрузка теперь живёт в ЭТАП 3/4 —
                                                    // см. _predictionService.RunStage3RestoreAsync() в Stage 3/4.
                                                }
                                                // ✅ Round 7-C8: подключаем мост из GameSession → PredictionService,
                                                // чтобы cleanup осиротевших сессий мог отменить связанный
                                                // с событием прогноз.
                                                GameSessionPredictionBridge.PredictionServiceAccessor = () => _predictionService;
                        _voicePointsService = new VoicePointsService(_client!, _pointsService, GetServerConfigInternal, IsActiveEventOnChannel);
                        _reconnectionService.OnDisconnectDetected += OnDisconnectDetected;
                        _reconnectionService.OnReconnectStarted += OnReconnectStarted;
                        _reconnectionService.OnReconnectCompleted += OnReconnectCompleted;
                        _reconnectionService.OnFullRestartRequested += OnFullRestartRequested;
                        _connectionPredictor.OnPredictionMade += OnPredictionMade;

                        _ui?.UpdateServices(
                            _client,
                            _reconnectionService,
                            _connectionPredictor,
                                                    _statusNotifier,
                                                    isRestart: _currentStartupType == StartupType.Restart
                                                );

                        // Переподписываем MusicCommands на новый клиент
                        if (_musicCommands is not null)
                            _musicCommands.UpdateDiscordClient(_client);
                    }

                    _commandHandler = new CommandHandler(_client!, _config!.GuildIDs);
                    CommandHandler.SetUI(_ui);

                                        // Discord-подписки (Ready/MessageReceived/GuildScheduledEvent*) теперь
                                        // живут в Этапе 2/4: СИНХРОНИЗАЦИЯ — там, где идёт работа с эвентами.
                                                                                // Исключение: OnReady нужен ДО StartAsync, иначе Discord шлёт READY раньше,
                                                                                // чем мы успеваем подписаться (Ready — одноразовое событие). Двойной
                                                                                // подписки не будет: SetupDiscordEvents начинается с `_client.Ready -= OnReady`.
                                                                                _client!.Ready += OnReady;
                                                                                try
                                                                                {
                                                                                    await _client.LoginAsync(TokenType.Bot, GetBotToken());
                                                                                    await _client.StartAsync();
                                                                                    StartupRenderer.Instance.WriteLine($"Вход выполнен успешно. Состояние: {_client.ConnectionState}, логин: {_client.LoginState}");

                                            // PrepareAsync ПОСЛЕ LoginAsync — CurrentUser уже установлен,
                                            // DiscordClientWrapper подпишется на Ready до того как оно сработает.
                                            // Сам лог "[Music] DI-контейнер собран" не выводится здесь —
                                            // он появится внутри Этапа 4 (ИНИЦИАЛИЗАЦИЯ МУЗЫКИ).
                                                                                        //
                                                                                        // ✅ R7 fix: раннее NRE на чистом клоне приходилось на эту строку.
                                                                                        // Guard выше/ниже проверял _lavalinkService is not null только для
                                                                                        // SuppressPrepareLog, но сам PrepareAsync() вызывался без проверки.
                                                                                        // На машинах, где LavalinkService не создался (нет jar/нет конфига),
                                                                                        // это падало с NullReferenceException. Теперь — пропускаем.
                                                                                        if (_lavalinkService is not null)
                                                                                        {
                                                                                            _lavalinkService.SuppressPrepareLog = true;
                                                                                            await _lavalinkService.PrepareAsync();
                                                                                            _lavalinkService.SuppressPrepareLog = false;
                                                                                        }
                                                                                        else
                                                                                        {
                                                                                            StartupRenderer.Instance.WriteLine("⚠️ LavalinkService не инициализирован — музыка будет недоступна.");
                                                                                        }

                                                                                    // Ждем готовности. После успешного WaitForReadyAsync
                                                                                    // статус гарантированно Connected/LoggedIn — фиксируем
                                                                                    // это отдельной строкой, чтобы в Logs Panel было видно
                                                                                    // окончательное состояние подключения.
                                                                                    await WaitForReadyAsync();

                                                                                    var readyState = $"{_client.ConnectionState}/{_client.LoginState}";
                                                                                    if (_client.ConnectionState == ConnectionState.Connected && _client.LoginState == LoginState.LoggedIn)
                                                                                    {
                                                                                        StartupRenderer.Instance.WriteLine($"Discord: подключён ({readyState}).");
                                                                                    }
                                                                                    else
                                                                                    {
                                                                                        StartupRenderer.Instance.WriteLine($"⚠️ Discord не в Connected: состояние={readyState}. Блок ЗАПУСК не закрывается — проверь сеть/токен.");
                                                                                        // Не закрываем блок ЗАПУСК — чтобы в логах было видно,
                                                                                        // что дальше идёт уже попытка продолжения инициализации
                                                                                        // на нестабильном соединении.
                                                                                    }

                                                                                    StartupRenderer.Instance.WriteFooter(_currentStartupType == StartupType.Restart
                                                                                        ? "ЗАПУСК ПОСЛЕ ПЕРЕЗАГРУЗКИ — ЗАВЕРШЁН"
                                                                                        : "ЗАПУСК — ЗАВЕРШЁН");

                                                                                    // Запускаем инициализацию с опросом (Lavalink запускается внутри на Этапе 4)
                                                                                    await InitializeBotWithProgress();

                                                                                                                                                                        // ✅ R7 fix: после инициализации проверяем что ключевые сервисы живы.
                                                                                                                                                                        // На чистом клоне (нет файлов / повреждён config.json) часть сервисов
                                                                                                                                                                        // может быть null — без этой проверки они упадут позже молча
                                                                                                                                                                        // с NullReferenceException где-то глубоко в обработчике.
                                                                                                                                                                        var missingServices = new List<string>();
                                                                                                                                                                        if (_predictionService == null)  missingServices.Add(nameof(_predictionService));
                                                                                                                                                                        if (_voicePointsService == null)  missingServices.Add(nameof(_voicePointsService));
                                                                                                                                                                        if (_statusNotifier == null)      missingServices.Add(nameof(_statusNotifier));
                                                                                                                                                                        if (_reconnectionService == null) missingServices.Add(nameof(_reconnectionService));
                                                                                                                                                                        if (_connectionPredictor == null) missingServices.Add(nameof(_connectionPredictor));
                                                                                                                                                                        if (missingServices.Count > 0)
                                                                                                                                                                        {
                                                                                                                                                                            await LogStartup($" ⚠️ После инициализации не созданы сервисы: {string.Join(", ", missingServices)}. Бот продолжит работу, но часть функций будет недоступна.");
                                                                                                                                                                        }

        // Ежедневный плановый перезапуск (время задаётся в config.json)
        StartDailyRestartScheduler();

                // ✅ Persistence-fix: периодический флаш runtime-сторов каждые 60с.
                // Защищает от потери данных при внезапном kill/BSOD/выключении —
                // даже если fire-and-forget Task.Run(...) от обработчиков не успел
                // записать, периодический loop гарантирует снимок состояния на диске
                // с задержкой не более минуты.
                StartPersistenceFlushLoop();

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
                                            // Полный стек печатаем только в Debug-сборке, чтобы не шуметь в проде.
                                            // В Release — только сообщение, как раньше. Это помогает диагностировать
                                            // NullReferenceException на чистом клоне: stack сразу покажет место.
                                            var msg = ex.Message;
                    #if DEBUG
                                            msg = ex.ToString();
                    #endif
                                            await LogStartup($" Ошибка запуска: {msg}");
                                            if (!_shouldExit)
                                            {
                                                await LogStartup(" Повторная попытка через 10 секунд...");
                                                await Task.Delay(10000);
                                                // ✅ R7 fix: после неудачной попытки _client может остаться
                                                // в частично инициализированном состоянии. Сбрасываем его,
                                                // чтобы на следующей итерации цикла не упасть на "already running client".
                                                try
                                                {
                                                    if (_client != null)
                                                    {
                                                        DisposeClientSafely(_client);
                                                        _client = null;
                                                    }
                                                }
                                                catch (Exception resetEx)
                                                {
                                                    await LogStartup($" Сброс клиента после ошибки: {resetEx.Message}");
                                                }
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

                if (_eventOpsOrchestrator != null)
                {
                    await _eventOpsOrchestrator.HandleCreatedAsync(guildEvent);
                }
                else
                {
                    if (_eventAnnouncer == null) return;
                    await _eventAnnouncer.AnnounceCreatedAsync(guildEvent);
                }
            }
            catch (Exception ex)
            {
                await LogError($"Ошибка в OnGuildScheduledEventCreated: {ex}");
                try { Console.WriteLine($"[EVENT] OnGuildScheduledEventCreated FULL: {ex}"); } catch { }
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

                SocketGuildEvent? beforeEvent = null;
                try { beforeEvent = await before.GetOrDownloadAsync(); } catch { }

                if (_eventOpsOrchestrator != null)
                {
                    // Подменяем делегат на лету, чтобы передать beforeCache — иначе теряется previous.
                    // Само назначение OnUpdatedAsync безопасно: orchestrator не лезет в это поле параллельно.
                    var capturedBefore = before;
                    _eventOpsOrchestrator.OnUpdatedAsync = (current, previous) => _eventAnnouncer!.AnnounceUpdatedAsync(capturedBefore, current);
                    await _eventOpsOrchestrator.HandleUpdatedAsync(after, beforeEvent);
                }
                else
                {
                    if (_eventAnnouncer == null) return;
                    await _eventAnnouncer.AnnounceUpdatedAsync(before, after);
                }
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

                // ✅ Round 7-C8: отменяем связанный с событием прогноз (если он был создан
                // во время этого события). CancelPredictionForEventAsync ищет прогноз по
                // eventId среди всех активных — это важно в параллельном режиме, когда
                // на гильдии может одновременно существовать несколько прогнозов в разных
                // голосовых каналах (по одному на каждое активное событие).
                if (guildEvent.Guild != null && _predictionService != null)
                {
                    var (ok, error) = await _predictionService.CancelPredictionForEventAsync(
                        guildEvent.Guild.Id, guildEvent.Id, "⚠️ Событие было отменено. Все ставки возвращены.");
                    if (!ok && !string.IsNullOrEmpty(error))
                        Console.WriteLine($"[PREDICTION] Cancel-on-cancel failed for guild={guildEvent.Guild.Id} event={guildEvent.Id}: {error}");
                }

                if (_eventOpsOrchestrator != null)
                {
                    await _eventOpsOrchestrator.HandleCancelledAsync(guildEvent);
                }
                else
                {
                    if (_eventAnnouncer == null) return;
                    await _eventAnnouncer.AnnounceStatusChangedAsync(guildEvent, "cancelled");
                }
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
                await GameSessionCommands.OnGuildScheduledEventStarted(guildEvent, _client!);
                try
                {
                    Console.WriteLine($"[EVENT] started guild={guildEvent.Guild?.Id} event={guildEvent.Id} name='{guildEvent.Name}'");
                }
                catch { }

                if (_eventOpsOrchestrator != null)
                {
                    await _eventOpsOrchestrator.HandleStartedAsync(guildEvent);
                }
                else
                {
                    if (_eventAnnouncer == null) return;
                    await _eventAnnouncer.AnnounceStatusChangedAsync(guildEvent, "started");
                }
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
                await GameSessionCommands.OnGuildScheduledEventCompleted(guildEvent, _client!, _googleSheetsService, CommandLogSink);
                try
                {
                    Console.WriteLine($"[EVENT] completed guild={guildEvent.Guild?.Id} event={guildEvent.Id} name='{guildEvent.Name}'");
                }
                catch { }

                // ✅ Round 7-C8: отменяем связанный с событием прогноз (если он был создан
                // во время этого события). CancelPredictionForEventAsync ищет прогноз по
                // eventId среди всех активных — это важно в параллельном режиме, когда
                // на гильдии может одновременно существовать несколько прогнозов в разных
                // голосовых каналах (по одному на каждое активное событие).
                if (guildEvent.Guild != null && _predictionService != null)
                {
                    var (ok, error) = await _predictionService.CancelPredictionForEventAsync(
                        guildEvent.Guild.Id, guildEvent.Id, "⚠️ Событие завершено. Все ставки возвращены.");
                    if (!ok && !string.IsNullOrEmpty(error))
                        Console.WriteLine($"[PREDICTION] Cancel-on-complete failed for guild={guildEvent.Guild.Id} event={guildEvent.Id}: {error}");
                }

                if (_eventOpsOrchestrator != null)
                {
                    await _eventOpsOrchestrator.HandleCompletedAsync(guildEvent);
                }
                else
                {
                    if (_eventAnnouncer == null) return;
                    await _eventAnnouncer.AnnounceStatusChangedAsync(guildEvent, "completed");
                }
            }
            catch (Exception ex)
            {
                await LogError($"Ошибка в OnGuildScheduledEventCompleted: {ex.Message}");
            }
        }

                        // 🩹 safeinvoke-lambda-name: именованные обёртки для multicast-подписчиков
                        // EventOpsOrchestrator. В логах SafeInvokeAsync печатает Method.Name — для
                        // лямбд это что-то вроде <<>9__0_0>, для этих методов — человекопонятные
                        // имена (см. комментарий в Program ctor).
                        private Task AnnouncerOnCreatedAsync(SocketGuildEvent e)
                        {
                            if (_eventAnnouncer == null) return Task.CompletedTask;
                            return _eventAnnouncer.AnnounceCreatedAsync(e);
                        }

                        private Task AnnouncerOnUpdatedAsync(SocketGuildEvent current, SocketGuildEvent? previous)
                        {
                            if (_eventAnnouncer == null) return Task.CompletedTask;
                            return _eventAnnouncer.AnnounceUpdatedAsync(default, current);
                        }

                        private Task AnnouncerOnStartedAsync(SocketGuildEvent e)
                        {
                            if (_eventAnnouncer == null) return Task.CompletedTask;
                            return _eventAnnouncer.AnnounceStatusChangedAsync(e, "started");
                        }

                        private Task AnnouncerOnCancelledAsync(SocketGuildEvent e)
                        {
                            if (_eventAnnouncer == null) return Task.CompletedTask;
                            return _eventAnnouncer.AnnounceStatusChangedAsync(e, "cancelled");
                        }

                        private Task AnnouncerOnCompletedAsync(SocketGuildEvent e)
                        {
                            if (_eventAnnouncer == null) return Task.CompletedTask;
                            return _eventAnnouncer.AnnounceStatusChangedAsync(e, "completed");
                        }

        private async Task OnGuildMemberUpdated(Cacheable<SocketGuildUser, ulong> before, SocketGuildUser after)
        {
            try
            {
                var beforeUser = await before.GetOrDownloadAsync();
                if (beforeUser == null)
                    return;

                if (!_serverConfigs.TryGetValue(after.Guild.Id, out var config))
                    return;

                if (!config.MasterRoleId.HasValue || config.MasterRoleId.Value == 0)
                    return;

                if (!config.MasterGuideEnabled)
                    return;

                var masterRoleId = config.MasterRoleId.Value;
                var hadRoleBefore = beforeUser.Roles.Any(r => r.Id == masterRoleId);
                var hasRoleNow = after.Roles.Any(r => r.Id == masterRoleId);

                if (hadRoleBefore || !hasRoleNow)
                    return;

                var cooldownHours = config.MasterGuideCooldownHours <= 0 ? 168 : config.MasterGuideCooldownHours;
                var key = $"{after.Guild.Id}:{after.Id}";
                if (_masterGuideSentAt.TryGetValue(key, out var lastSentAt))
                {
                    if (DateTimeOffset.UtcNow - lastSentAt < TimeSpan.FromHours(cooldownHours))
                        return;
                }

                // Проверяем, можем ли открыть DM-канал до отправки
                IDMChannel? dmChannel = null;
                try
                {
                    dmChannel = await after.CreateDMChannelAsync();
                }
                catch (Exception ex)
                {
                    BotLogger.Info(LogCategory.Discord,
                        $"[MasterGuide] DM закрыт у пользователя {after.Id} на сервере {after.Guild.Id}: {ex.Message}");
                    return;
                }

                if (dmChannel == null)
                {
                    BotLogger.Info(LogCategory.Discord,
                        $"[MasterGuide] DM-канал не создан для {after.Id} на сервере {after.Guild.Id}");
                    return;
                }

                // Гарантируем наличие файла шаблона (создаётся при первом запуске для всех серверов)
                var template = MasterGuideService.LoadTemplate(after.Guild.Id);
                var guide = MasterGuideService.Render(template, after.Guild, after, config);

                try
                {
                    await dmChannel.SendMessageAsync(guide);
                    _masterGuideSentAt[key] = DateTimeOffset.UtcNow;
                    SaveMasterGuideHistory();
                    BotLogger.Info(LogCategory.Discord, $"[MasterGuide] Отправлена памятка пользователю {after.Id} на сервере {after.Guild.Id}");
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.Discord, $"[MasterGuide] Не удалось отправить ЛС пользователю {after.Id}: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                await LogError($"Ошибка в OnGuildMemberUpdated: {ex.Message}");
            }
        }

        // BuildMasterGuideMessage перенесён в MasterGuideService.GetBuiltinTemplate/Render

        private const string MasterGuideHistoryFile = "master_guide_sent.json";

        private void LoadMasterGuideHistory()
        {
            try
            {
                var path = BotConfig.ResolvePath(Path.Combine(BotConfig.SettingsFolderName, MasterGuideHistoryFile));
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;
                var dict = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(json);
                if (dict == null) return;
                foreach (var kv in dict)
                    _masterGuideSentAt[kv.Key] = kv.Value;
                BotLogger.Info(LogCategory.System, $"[MasterGuide] Загружена история отправок: {dict.Count} записей");
            }
            catch (Exception ex)
            {
                BotLogger.Warn(LogCategory.System, $"[MasterGuide] Не удалось загрузить историю отправок: {ex.Message}");
            }
        }

        private void SaveMasterGuideHistory()
        {
            try
            {
                var dir = BotConfig.GetSettingsDirectory();
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, MasterGuideHistoryFile);
                var json = JsonSerializer.Serialize(_masterGuideSentAt, new JsonSerializerOptions { WriteIndented = true });
                // SafeJsonIO.WriteAtomic: .tmp → File.Move(overwrite:true). Сначала
                // обновляем .bak-фолбэк, потом основной файл — чтобы при первом
                // запуске не было момента, когда основной файл уже новый, а .bak
                // остался от прошлой записи.
                SafeJsonIO.WriteAtomic(path + ".bak", json);
                SafeJsonIO.WriteAtomic(path, json);
            }
            catch (Exception ex)
            {
                BotLogger.Warn(LogCategory.System, $"[MasterGuide] Не удалось сохранить историю отправок: {ex.Message}");
            }
        }

        private async Task ResyncEventAnnouncementsOnStartupAsync()
        {
            if (_eventAnnouncementStore == null)
                return;

            // Подождём, пока клиент реально войдёт — иначе REST падает с
            // "Client is not logged in" и мы тихо пропускаем все анонсы.
            var loginDeadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < loginDeadline
                && _client != null
                && (_client.ConnectionState != ConnectionState.Connected
                    || _client.LoginState != LoginState.LoggedIn))
            {
                await LogStartup($"[EVENT][RESYNC] Waiting for client login: state={_client?.ConnectionState} login={_client?.LoginState}");
                await Task.Delay(500);
            }
            await LogStartup($"[EVENT][RESYNC] Login state at resync: state={_client?.ConnectionState} login={_client?.LoginState}");

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
                    var guild = _client?.GetGuild(entry.GuildId);
                    if (guild == null)
                    {
                        failed++;
                        continue;
                    }

                    var guildEvent = guild.Events.FirstOrDefault(e => e.Id == entry.EventId);
                                // Кэш guild.Events НЕ содержит события в статусе Completed/Cancelled
                                // (Discord архивирует их через ~1 час). Чтобы не помечать их как
                                // «удалённые» на каждом RESYNC, пробуем достать актуальные данные
                                // через REST по eventId.
                                if (guildEvent == null)
                                {
                                    RestGuildEvent? restEvent = null;
                                    string? restError = null;
                                    try
                                    {
                                        var restGuild = await _client!.Rest.GetGuildAsync(entry.GuildId).ConfigureAwait(false);
                                        if (restGuild != null)
                                        {
                                            restEvent = await restGuild.GetEventAsync(entry.EventId).ConfigureAwait(false);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        restError = ex.Message;
                                    }

                                    if (restEvent is not null)
                                    {
                                        // REST говорит, что событие существует — Completed/Cancelled/Active/Scheduled.
                                        var restStatus = restEvent.Status switch
                                        {
                                            GuildScheduledEventStatus.Active    => "started",
                                            GuildScheduledEventStatus.Completed => "completed",
                                            GuildScheduledEventStatus.Cancelled => "cancelled",
                                            _                                   => "scheduled"
                                        };

                                        await LogStartup($"[EVENT][RESYNC] Запись guild={entry.GuildId} event={entry.EventId} не найдена в кэше, но REST вернул status={restEvent.Status} (применяем {restStatus})");
                                        // В кэше клиента нет SocketGuildEvent, но REST вернул данные —
                                        // обновим наш анонс (Discord embed + Telegram + DM), чтобы
                                        // старый «Новое событие» не висел в канале как актуальный.
                                        // Только для финальных статусов (started/completed/cancelled) —
                                        // для scheduled обработает обычный AnnounceCreatedAsync ниже.
                                        if (restStatus != "scheduled" && _eventAnnouncer != null)
                                        {
                                            await _eventAnnouncer.AnnounceStatusChangedFromRestAsync(restEvent, entry, restStatus);
                                        }
                                        else
                                        {
                                            entry.LastUpdatedMark = restStatus;
                                            entry.LastUpdatedAt = DateTime.UtcNow;
                                            _eventAnnouncementStore.UpdateEntry(entry);
                                        }
                                        continue;
                                    }

                                    // REST тоже пуст — реальное удаление. Помечаем только если
                                    // запись ни разу не была переведена в финальный статус
                                    // (событие создали и тут же удалили, до того как бот успел
                                    // обработать завершение). Иначе оставляем запись, чтобы
                                    // embed «удалено» не появлялся на архивных событиях.
                                    var everFinalized = !string.IsNullOrEmpty(entry.LastUpdatedMark)
                                        && (entry.LastUpdatedMark.StartsWith("completed")
                                         || entry.LastUpdatedMark.StartsWith("cancelled")
                                         || entry.LastUpdatedMark.StartsWith("started"));

                                    if (everFinalized)
                                    {
                                        await LogStartup($"[EVENT][RESYNC] Пропуск пометки «удалено» для guild={entry.GuildId} event={entry.EventId}: REST пуст, но LastUpdatedMark='{entry.LastUpdatedMark}'. restError={restError ?? "<none>"}");
                                        continue;
                                    }

                                    // Событие исчезло с сервера (удалено).
                                    // Перед удалением записи — обновим сообщение в Discord,
                                    // чтобы оно не висело как "Новое событие".
                                    await TryAnnounceDeletedOrCancelledAsync(entry, "удалено");
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
                        if (_eventAnnouncer != null)
                            await _eventAnnouncer.AnnounceUpdatedAsync(default, guildEvent);
                        updated++;
                        continue;
                    }

                    // Для отменённых/завершённых/начатых — обновляем embed на нужный статус.
                    // AnnounceStatusChangedAsync сам решит, надо ли редактировать.
                    if (_eventAnnouncer != null)
                        await _eventAnnouncer.AnnounceStatusChangedAsync(guildEvent, status);
                    updated++;
                }
                catch (Exception ex)
                {
                    failed++;
                    await LogError($"[EVENT][RESYNC] Ошибка обновления guild={entry.GuildId}, event={entry.EventId}: {ex}");
                }
            }

            // ── Шаг 2: анонсировать события, созданные пока бот был оффлайн ─────
            foreach (var guild in _client!.Guilds)
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
                                                // Идём через Orchestrator, чтобы HandleCreatedAsync lifecycle-сервиса
                                                // поставил reminder1h. Раньше звали Announcer напрямую — reminder
                                                // для оффлайн-события не ставился до следующего Update/Ready
                                                // (audit bug #4).
                                                if (_eventOpsOrchestrator != null)
                                                    await _eventOpsOrchestrator.HandleCreatedAsync(guildEvent);
                                                else if (_eventAnnouncer != null)
                                                    await _eventAnnouncer.AnnounceCreatedAsync(guildEvent);
                                                announced++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    await LogError($"[EVENT][RESYNC] Ошибка скана событий guild={guild.Id}: {ex}");
                }
            }

            await LogStartup($"[EVENT][RESYNC] Завершено: updated={updated}, removed={removed}, announced={announced}, failed={failed}.");
        }

        // ── Вспомогательное: если запись имеет сохранённые ID сообщений, но
        // событие исчезло — превращаем embed в "Событие удалено/отменено".
        private async Task TryAnnounceDeletedOrCancelledAsync(EventAnnouncementEntry entry, string reason)
        {
            try
            {
                if (_eventAnnouncer == null) return;
                if (entry.AnnounceChannelId == 0 || entry.AnnounceMessageId == 0) return;
                var announceChannel = _client?.GetChannel(entry.AnnounceChannelId) as IMessageChannel;
                if (announceChannel == null) return;
                var msg = await announceChannel.GetMessageAsync(entry.AnnounceMessageId) as IUserMessage;
                if (msg == null) return;

                // Имя события берём из сохранённого LastName. Если snapshot ещё
                // не успел сохраниться (например, событие создано оффлайн и бот
                // увидел его уже удалённым при первом RESYNC) — пишем
                // «без названия», чтобы в ленте Telegram/Discord не висело
                // безымянное событие.
                var eventName = string.IsNullOrWhiteSpace(entry.LastName)
                    ? "без названия"
                    : entry.LastName;

                var title = reason == "удалено"
                                    ? $"❌ Событие удалено: {eventName}"
                                    : $"⚠️ Событие отменено: {eventName}";

                                var mskNow = TryGetMoscowTime(DateTime.UtcNow, out var msk) ? msk : DateTime.Now;
                                var mskMark = reason == "удалено"
                                    ? $"Удалено: {mskNow:dd.MM.yyyy HH:mm} (по МСК)"
                                    : $"Отменено: {mskNow:dd.MM.yyyy HH:mm} (по МСК)";

                                var description = reason == "удалено"
                                    ? "Это событие было удалено с сервера Discord."
                                    : "Это событие было отменено.";

                                var embed = new EmbedBuilder()
                                    .WithTitle(title)
                                    .WithDescription(description)
                                    .WithColor(reason == "удалено" ? Color.DarkRed : Color.Red)
                                    .WithCurrentTimestamp()
                                    .AddField("Статус", $"<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:F>", false)
                                    .Build();
                await msg.ModifyAsync(m => m.Embed = embed);

                // Telegram: добавляем доп. строку, если сохранён TelegramMessageId
                await TryEditTelegramForDeletedOrCancelledAsync(entry, mskMark, eventName);
            }
            catch (Exception ex)
            {
                await LogError($"[EVENT][RESYNC] Не удалось обновить embed для удалённого/отменённого event={entry.EventId}: {ex}");
            }
        }

        private async Task TryEditTelegramForDeletedOrCancelledAsync(EventAnnouncementEntry entry, string mskMark, string eventName)
        {
            // Изоляция: падение Telegram не должно прокидываться вверх в Discord async-chain,
            // иначе один зависший HTTP-запрос к api.telegram.org роняет обработку resync-цикла.
            if (_telegramNotifier == null) return;
            if (entry.TelegramMessageId == 0) return;
            try
            {
                int msgId = (int)entry.TelegramMessageId;
                // Имя события обязательно показываем — иначе в Telegram-ленте
                // появляется «безымянное» событие, и непонятно, что именно пропало.
                var text = $"❌ {mskMark}\nСобытие «{eventName}» больше недоступно.";
                await _telegramNotifier.EditMessageTextAsync(entry.GuildId, msgId, text).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Глотаем всё — Telegram-нотификации best-effort.
                try { await LogError($"[EVENT][RESYNC] Telegram edit fail (event={entry.EventId}): {ex.Message}"); } catch { }
            }
        }

        private static bool TryGetMoscowTime(DateTime utc, out DateTime msk)
            => MoscowTime.TryConvertFromUtc(utc, out msk);

        // Метод-переходник: вся логика унесена в EventAnnouncer.AnnounceUpdatedAsync.
        // Сохраняем сигнатуру для редких случаев, когда требуется обновить embed записи
        // (оффлайн-правки названия/времени/описания) без отдельной логики.
        private Task AnnounceGuildScheduledEventUpdatedAsync(SocketGuildEvent guildEvent)
        {
            if (guildEvent?.Guild == null) return Task.CompletedTask;
            if (_eventAnnouncer == null) return Task.CompletedTask;
            return _eventAnnouncer.AnnounceUpdatedAsync(default, guildEvent);
        }
        private async Task WaitForReadyAsync()
        {
            var readyTcs = new TaskCompletionSource<bool>();

            Task OnReadyOnce()
            {
                readyTcs.TrySetResult(true);
                return Task.CompletedTask;
            }

            _client!.Ready += OnReadyOnce;

            try
            {
                if (_client!.CurrentUser != null)
                {
                    readyTcs.TrySetResult(true);
                }

                var completedTask = await Task.WhenAny(readyTcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                if (completedTask == readyTcs.Task)
                    return;

                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && !_shouldExit)
                {
                    if (_client!.CurrentUser != null)
                        return;

                    await Task.Delay(250);
                }
            }
            finally
            {
                _client!.Ready -= OnReadyOnce;
            }
        }

        private async Task BackgroundMonitoringLoop(CancellationToken ct = default)
        {
                    // 🩹 perf: heartbeat-лог каждые 30 мин. Показывает, что бот жив и
                    // обрабатывает цикл мониторинга. Если TimeoutException приходят,
                    // а heartbeat-строки идут — значит, проблема НЕ в зависании бота,
                    // а в IO/Discord-сокете. Без этого в логе непонятно, был бот жив
                                        // или нет в момент падения. Интервал увеличен с 5 мин до 30 мин —
                                        // на проде запись логов каждые 5 минут засоряла Logs/yyyyMMdd,
                                        // а полезной информации в этих строках мало (tick=N, ws=NNMB).
                                        int heartbeatTick = 0;
                                        DateTime loopStart = DateTime.UtcNow;
                                        while (!_shouldExit && !ct.IsCancellationRequested)
                                        {
                                            try
                                            {
                                                                    await Task.Delay(TimeSpan.FromMinutes(30), ct);
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

                        heartbeatTick++;
                        try
                        {
                            var p = System.Diagnostics.Process.GetCurrentProcess();
                            var elapsed = (DateTime.UtcNow - loopStart).TotalSeconds;
                            BotLogger.Info(LogCategory.System,
                                $"[Heartbeat] tick={heartbeatTick} elapsed={elapsed:F0}s threads={p.Threads.Count} " +
                                $"ws={p.WorkingSet64 / 1024d / 1024d:F1}MB " +
                                $"cpu={(p.TotalProcessorTime.TotalMilliseconds / Math.Max(elapsed * 10, 1)):F1}% " +
                                $"conn={_client?.ConnectionState} ready={TimeSinceLastReconnect.TotalSeconds:F0}s_ago");
                        }
                        catch { /* heartbeat — best-effort */ }

                                                // 🩹 perf: REST keep-alive ping. Discord.NET HttpClient по умолчанию
                                                // не пингует REST API — соединение в пуле протухает после 60-100 сек
                                                // idle, и следующий запрос (наш DeferAsync!) делает TCP+SSL handshake
                                                // заново. 100-600 мс задержки. Пинг REST каждые 60 сек держит
                                                                                                // соединение прогретым. Heartbeat-цикл 30 мин — keep-alive
                                                                                                // работает по собственному счётчику времени.
                                                                                                if (_client?.Rest != null && _client.ConnectionState == ConnectionState.Connected && DateTime.UtcNow - _lastKeepAliveUtc >= TimeSpan.FromSeconds(60))
                                                                                                {
                                                                                                    try
                                                                                                    {
                                                                                                        _lastKeepAliveUtc = DateTime.UtcNow;
                                                                                                        var sw = System.Diagnostics.Stopwatch.StartNew();
                                                                                                        var user = await _client.Rest.GetCurrentUserAsync();
                                                                                                        sw.Stop();
                                                                                                        BotLogger.Debug(LogCategory.Discord,
                                                                                                            $"[KeepAlive] rest ping ok={user?.Id != null} took={sw.ElapsedMilliseconds}ms");
                                                                                                    }
                                                                                                    catch (Exception kaEx)
                                                                                                    {
                                                                                                        BotLogger.Warn(LogCategory.Discord,
                                                                                                            $"[KeepAlive] rest ping FAILED: {kaEx.GetType().Name}: {kaEx.Message}");
                                                                                                    }
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
            var recon = _reconnectionService;
            if (recon == null) return;
            var reason = recon.ConnectionInfo.LastDisconnectReason;

            if (exception is not GatewayReconnectException)
            {
                            // Дисконнект от Discord — это ВСЕГДА инцидент (либо сеть,
                            // либо токен, либо серверный реконнект). Помечаем как WARN
                            // через префикс ⚠️, чтобы LogStartup направил это в правильный
                            // канал (а не в Stage/Info, как было до этого фикса).
                            await LogStartup($"⚠️ Отключение: {reason}");

                            if (_statusNotifier != null)
                                await _statusNotifier.SendConnectionIssue(
                                    reason,
                                    recon.ConnectionInfo.ReconnectAttempts + 1
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
                var recon = _reconnectionService;
                if (recon == null) return;
                var info = recon.ConnectionInfo;

                        // 🩹 reminder-survives-reconnect: метим момент реконнекта, чтобы PreDefer
                        // после Gateway Reconnect не падал с 10062 (interaction ещё не синхронизирован
                        // с новой сессией у Discord). Время хранится до ~5 секунд.
                        MarkReconnectCompleted();

                                                            // 🩹 reminder-survives-restart: после OnDisconnected → Cancel() все per-event
                            // CTS в EventOpsLifecycleService умерли, а вместе с ними — Task.Delay для
                            // reminder1h и deleteReminder15m. В сторе остались абсолютные моменты
                            // (Reminder1hAtUtc / DeleteReminder15mAtUtc), теперь переставляем таймеры
                            // обратно в _timers и досылаем/дочищаем всё, что пропустили за время даунтайма.
                            // Запускаем в фоне, чтобы OnReconnectCompleted быстро завершился.
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false); // даём клиенту полностью устаканиться
                                    if (_eventOpsLifecycle != null)
                                        await _eventOpsLifecycle.RehydrateTimersAsync().ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    BotLogger.Warn(LogCategory.Discord,
                                        $"[Reconnect] RehydrateTimersAsync failed: {ex.GetType().Name}: {ex.Message}");
                                }
                            });

                            // Отправляем уведомление об успешном реконнекте
                            try
                            {
                                if (_statusNotifier != null)
                                    await _statusNotifier.SendReconnectSuccess(
                                        info.ReconnectAttempts,
                                        info.LastDisconnectReason
                                    );
                            }
                catch (Exception ex)
                {
                    await LogStartup($"Ошибка при отправке уведомления о переподключении: {ex.Message}");
                }

                // Обновляем контекст запуска (используется командами вроде !status),
                // но повторное сообщение "ВСЕ СИСТЕМЫ АКТИВНЫ" не шлём — SendReconnectSuccess уже сообщил об этом
                try
                {
                    var reconnectReason = $"Переподключение после: {info.LastDisconnectReason}";
                    _currentStartupType = StartupType.Reconnect;
                    _startupReason = reconnectReason;
                    _statusNotifier?.SetStartupContext(StartupType.Reconnect, reconnectReason);
                }
                catch (Exception ex)
                {
                    await LogStartup($"Ошибка при обновлении контекста после реконнекта: {ex.Message}");
                }

                // Восстанавливаем музыкальные очереди после переподключения.
                // Если Lavalink не поднялся — откладываем попытку, не падаем в Task.Run.
                if (_musicCommands is not null && _lavalinkService is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Даём Lavalink шанс подняться после реконнекта (с retry).
                            var lavalinkOk = await WaitForLavalinkWithRetryAsync(
                                attempts: 6, delay: TimeSpan.FromSeconds(5), ct: CancellationToken.None);

                            if (!lavalinkOk)
                            {
                                BotLogger.Warn(LogCategory.Music,
                                    "[Reconnect] Lavalink не поднялся за отведённое время, очереди не восстановлены — будут подхвачены при следующем успешном probe.");
                                return;
                            }

                            await Task.Delay(TimeSpan.FromSeconds(2)); // стабилизация
                            await _musicCommands.TryRestoreQueuesAsync();
                        }
                        catch (Exception ex)
                        {
                            BotLogger.Error(LogCategory.Music,
                                $"[Reconnect] Ошибка при восстановлении музыкальных очередей: {ex.GetType().Name}: {ex.Message}");
                        }
                    });
                }
            }
        }

        /// <summary>
        /// Ждёт готовности Lavalink (через /version) с ретраями. Если не поднялся —
        /// возвращает false вместо throw, чтобы восстановление очередей не падало.
        /// </summary>
        private async Task<bool> WaitForLavalinkWithRetryAsync(int attempts, TimeSpan delay, CancellationToken ct)
        {
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    var err = await _lavalinkService!.ProbeAsync();
                    if (err is null)
                    {
                        BotLogger.Info(LogCategory.Music, $"[Reconnect] Lavalink готов (попытка {i + 1}/{attempts})");
                        return true;
                    }
                    BotLogger.Debug(LogCategory.Music, $"[Reconnect] Lavalink probe попытка {i + 1}: {err}");
                }
                catch (Exception ex)
                {
                    BotLogger.Debug(LogCategory.Music, $"[Reconnect] Lavalink probe исключение: {ex.Message}");
                }

                if (i < attempts - 1)
                {
                    try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return false; }
                }
            }
            return false;
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

        /// <summary>
        /// Срабатывает, когда кэш юзеров гильдии готов (GuildAvailable).
        /// Здесь можно безопасно обращаться к guild.GetVoiceChannel(...).
        /// </summary>
        private async Task OnGuildAvailableForVoice(SocketGuild guild)
        {
            BotLogger.Info(LogCategory.Discord, $"[Voice] GuildAvailable: {guild.Name} ({guild.Id}), users={guild.Users.Count}, каналов={guild.Channels.Count}.");
            try { await VoiceChannelCommands.LoadPersistedAsync(_client!).ConfigureAwait(false); }
            catch (Exception ex) { BotLogger.Warn(LogCategory.Discord, $"[Voice] LoadPersistedAsync (GuildAvailable): {ex.GetType().Name}: {ex.Message}"); }
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

    foreach (var guild in _client!.Guilds)
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

        private DateTime _readyTime = DateTime.MinValue; // Инициализируем MinValue
                private DateTime _fullReadyTime;

        private async Task OnReady()
                        {
                                    _readyTime = DateTime.UtcNow;

                                    // 🩹 prod-sidecar-10062: метим момент готовности, чтобы PreDefer
                                    // в первые секунды после старта не падал с 10062 (Discord ещё не
                                    // синхронизировал сессию с момента выхода Gateway). Защищает
                                    // ситуацию "простоял бот всю ночь, потом /roll → 10062".
                                    MarkReconnectCompleted();

                                                                                            // 🩹 perf: запуск фонового GC-цикла. После холодного старта
                                                                                            // в LOH/L2 накапливается мусор от инициализации — пусть соберёт
                                                                                            // маленькими порциями, а не большой паузой во время первого /roll.
                                                                                            StartManualGcLoop();

                            // ✅ Bug audit: ClearRestartPendingFlag вызывался ТОЛЬКО из
                    // PredictionService.AnnounceOnlineAsync при условии, что у гильдии
                    // есть активный prediction channel. На гильдиях без predictions (и в
                    // первые секунды после Ready, до того как успеет отработать
                    // AnnounceOnlineAsync) флаг .restart_pending лежал вечно, и watchdog
                    // мог некорректно интерпретировать состояние. Снимаем здесь сразу
                    // после первого Ready — гарантирует очистку независимо от наличия
                    // predictions и активных каналов. Идемпотентно: если файла нет —
                    // File.Exists/File.Delete просто ничего не сделают.
                    try { ClearRestartPendingFlag(); } catch { }
                    try { ClearFullStartRequestFlag(); } catch { }

                    // 🩹 prod-sidecar-stuck: за прошлые сессии иногда остаются
                    // висящие sidecar'ы (sessions_state.json.lock, *.json.lock) —
                    // типично, когда предыдущий процесс был убит (kill / OOM / BSOD)
                    // до ReleaseLock(). SafeJsonIO.ReleaseLock этот .lock обычно
                    // снимает, но если процесс убит — никто не вызвал ReleaseLock.
                    // Подчищаем «подозрительные» sidecar'ы: только файлы старше 30 мин
                    // и только .lock (никогда не трогаем *.json), чтобы не удалить
                    // легитимный лок параллельного живого процесса.
                    try { CleanupStaleSidecarLocks(); } catch { }

                    // Подписка на GuildAvailable — кэш юзеров гильдии готов, можно грузить persistence.
                    // Загрузка persistence идёт через OnGuildAvailableForVoice.
                    try
                    {
                        _client.GuildAvailable -= OnGuildAvailableForVoice;
                        _client.GuildAvailable += OnGuildAvailableForVoice;
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Warn(LogCategory.Discord, $"[Voice] Не удалось подписаться на GuildAvailable: {ex.GetType().Name}: {ex.Message}");
                    }

                    VoiceChannelCommands.SetClient(_client);

                                // ОТПРАВЛЯЕМ В UI. LogInfo-вариант ("Ready: connected as X") удалён —
                                // дубль, в run.log писался и через рендерер (Инициализация бота...) и тут.
                                BotLogger.Info(LogCategory.Discord, $"БОТ ПОДКЛЮЧЕН К DISCORD: {_client!.CurrentUser?.Username} в {DateTime.Now:HH:mm:ss}");

// ✅ Загрузка сохранённых сессий игр переехала в ЭТАП 3 — СИНХРОНИЗАЦИЯ.
            // После RESYNC и prediction snapshot дёргаем LoadSessionsAsync, который
            // прогонит CleanupStaleSessionsAsync и RecreateControlMessagesAsync.
            // Вне этапа 3 этот вызов был фоновым Task.Run, из-за чего cleanup
            // пропадал из визуализации.

await Task.CompletedTask;
        }

        private void EnsureServerConfigsForConnectedGuilds()
        {
            var changed = false;
            foreach (var g in _client!.Guilds)
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

        private Task LogStartupBoxAsync(string title, IEnumerable<string> lines)
        {
            // Перенос логики в рендерер: строки по одной, без рамки-коробки.
            using (var stage = StartupRenderer.Instance.BeginStage(title))
            {
                foreach (var line in lines)
                    StartupRenderer.Instance.WriteLine(line);
            }
            return Task.CompletedTask;
        }

        private Task LogStartupBatch(IEnumerable<string> messages)
        {
            // Используется прежде всего в местах, где список строк уже сформирован.
            foreach (var msg in messages)
                StartupRenderer.Instance.WriteLine(msg);
            return Task.CompletedTask;
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
                    foreach (var guild in _client!.Guilds)
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
                                try { u = await _client!.Rest.GetUserAsync(userId).ConfigureAwait(false); } catch { }
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

                foreach (var guild in _client!.Guilds)
                {
                    if (!_serverConfigs.TryGetValue(guild.Id, out var sc) || !sc.TelegramEnabled)
                    {
                        lines.Add($"Telegram startup probe skipped for {guild.Name}: disabled or missing serverconfig");
                        continue;
                    }

                    if (_telegramNotifier != null)
                    {
                        var probe = await _telegramNotifier.ProbeAsync(guild.Id).ConfigureAwait(false);
                        lines.Add($"Telegram startup probe for {guild.Name}: {(probe.Success ? "OK" : "FAIL")} - {probe.Message}");
                    }
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
                        var stage1Lines = new List<string>();

                        // Заголовок открываем ДО регистрации/списка — иначе ломается порядок,
                        // когда ListSlashCommandsAsync пишет в UI через BotLogger.SetUiSink.
                        StartupRenderer.Instance.WriteHeader("ЭТАП 1/4: РЕГИСТРАЦИЯ КОМАНД");

                        if (_currentStartupType == StartupType.FirstStart)
                        {
                            // Полноценный (первый) запуск — регистрируем команды обязательно.
                            await _commandHandler.InitializeAsync();
                            // При регистрации список уже описан пошагово — печатать его повторно не нужно.
                        }
                        else
                        {
                            // Любой рестарт или реконнект — НЕ трогаем Discord,
                            // просто печатаем текущий список зарегистрированных команд.
                            var reason = _currentStartupType == StartupType.Restart
                                ? $"перезапуск ({_startupReason ?? "без варианта"})"
                                : "реконнект";
                            StartupRenderer.Instance.WriteLine($"Регистрация команд пропущена ({reason}).");
                            await _commandHandler.ListSlashCommandsAsync();
                        }

                    // Закрывающая линия этапа 1 — симметрично заголовку.
                    StartupRenderer.Instance.WriteFooter("ЭТАП 1/4: РЕГИСТРАЦИЯ КОМАНД — ЗАВЕРШЁН");

                    // ЭТАП 2: Синхронизация Discord-событий и активных сессий.
                    // Здесь же подписываемся на Discord-эвенты (Ready/MessageReceived/
                    // GuildScheduledEventCreated|Updated|Started|Cancelled|Completed/
                    // GuildMemberUpdated/ButtonExecuted/SelectMenuExecuted).
                    // Раньше эти подписки делались в отдельном «Этапе 2: АКТИВАЦИЯ
                    // ОБРАБОТЧИКОВ» — теперь они естественно живут рядом с RESYNC,
                    // потому что дальше идёт работа именно с эвентами.
                    StartupRenderer.Instance.WriteHeader("ЭТАП 2/4: СИНХРОНИЗАЦИЯ");
                    // SetupDiscordEvents сам пишет «События Discord настроены»
                    // — повторять это явно не нужно.
                    await SetupDiscordEvents();
                    await ResyncEventAnnouncementsOnStartupAsync();
                    // Подтягиваем сессии из файла и прогоняем cleanup + recreate.
                    // LoadSessionsAsync сам пишет в визуализацию через WriteStageLine.
                    await GameSessionCommands.LoadSessionsAsync(_client!);
                    StartupRenderer.Instance.WriteFooter("ЭТАП 2/4: СИНХРОНИЗАЦИЯ — ЗАВЕРШЕНА");

                    // ЭТАП 3: Снимок активных прогнозов (выводится по гильдиям).
                    StartupRenderer.Instance.WriteHeader("ЭТАП 3/4: АКТИВНЫЕ ПРОГНОЗЫ");
                    var stage3Lines = new List<string>();
                    try
                    {
                        static string Trunc(string? s, int max)
                        {
                            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                            s = s.Trim();
                            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
                        }

                        // ✅ Round 7-C6: вся загрузка прогнозов живёт ЗДЕСЬ, в ЭТАП 3/4.
                        //   1) RunStage3RestoreAsync → LoadStateAsync (без Discord API,
                        //      синхронно из файла), затем ValidateActiveAfterReadyAsync
                        //      (проверка канала/сообщения через Discord API — к этому
                        //      моменту клиент уже в Connected, см. WaitForReadyAsync выше),
                        //      затем AnnounceOnlineForRestoredAsync (сдвиг таймера,
                        //      "Бот снова в сети", чистка старых offline-сообщений).
                        //   2) После возврата — печать активных прогнозов по гильдиям,
                        //      как и раньше.
                        // Никаких гонок с Ready event, никакого фонового Task.Run:
                        // всё последовательно, в одном месте, с гарантированным
                        // порядком строк.
                        if (_predictionService != null)
                        {
                            try
                            {
                                await _predictionService.RunStage3RestoreAsync().ConfigureAwait(false);
                            }
                            catch (Exception restoreEx)
                            {
                                stage3Lines.Add($"[PRED] RESTORE_ERROR {restoreEx.GetType().Name}: {restoreEx.Message}");
                            }
                        }

                        var anyPred = false;
                        foreach (var g in _client!.Guilds)
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
                    // ✅ Round 7-C5: отчёт о восстановлении/проверке попадает
                    // в ЭТАП 3/4 через DrainRestoreReport(). Строки печатаются
                    // ПОСЛЕ активных прогнозов, чтобы блок шёл в порядке:
                    //   - снимок по гильдиям
                    //   - потом детальный отчёт восстановления
                    if (_predictionService != null)
                    {
                        var report = _predictionService.DrainRestoreReport();
                        if (report.Count > 0)
                        {
                            stage3Lines.AddRange(report);
                        }
                    }
                    if (stage3Lines.Count == 0)
                        stage3Lines.Add("Синхронизация завершена без дополнительных данных.");
                    foreach (var line in stage3Lines)
                        StartupRenderer.Instance.WriteLine(line);
                    StartupRenderer.Instance.WriteFooter("ЭТАП 3/4: АКТИВНЫЕ ПРОГНОЗЫ — ЗАВЕРШЁН");

                    // ЭТАП 4: ИНИЦИАЛИЗАЦИЯ МУЗЫКИ
                                    if (_config.Music.Enabled && _lavalinkService is not null)
                                    {
                                        // Перенаправляем весь поток логов LavalinkService в рендерер,
                                        // чтобы строки появлялись по одной внутри этапа, без буферизации.
                                        // ВАЖНО: BotLogger.Info(Music) игнорируем в startup-режиме, иначе UI получит дубль.
                                        _lavalinkService.StartupLogSink = msg =>
                                        {
                                            StartupRenderer.Instance.WriteLine(msg);
                                        };
                                        StartupRenderer.Instance.WriteHeader("ЭТАП 4/4: ИНИЦИАЛИЗАЦИЯ МУЗЫКИ");
                                    try
                                    {
                                        var lavalinkReady = await _lavalinkService.LaunchProcessAsync();

                                        if (!lavalinkReady)
                                        {
                                            // WaitUntilReadyAsync исчерпал таймаут — даём ещё до 15 секунд
                                            // (Lavalink может ещё грузить JVM или плагины)
                                            const int extraRetries = 15;
                                            const int retryDelayMs = 1000;
                                            StartupRenderer.Instance.WriteLine($"⏳ Lavalink не ответил за основной таймаут, ждём ещё до {extraRetries}с...");

                                            for (int i = 0; i < extraRetries; i++)
                                            {
                                                await Task.Delay(retryDelayMs);
                                                var err = await _lavalinkService.ProbeAsync();
                                                if (err is null)
                                                {
                                                    lavalinkReady = true;
                                                    StartupRenderer.Instance.WriteLine($"✅ Lavalink поднялся на попытке {i + 1} — готов ({_config.Music.Host}:{_config.Music.Port})");
                                                    break;
                                                }
                                            }

                                            if (!lavalinkReady)
                                            {
                                                var finalErr = await _lavalinkService.ProbeAsync();
                                                if (finalErr is null)
                                                    StartupRenderer.Instance.WriteLine($"✅ Lavalink готов ({_config.Music.Host}:{_config.Music.Port})");
                                                else
                                                    StartupRenderer.Instance.WriteLine($"⚠️ Lavalink так и не ответил: {finalErr}");
                                            }
                                        }
                                        else
                                        {
                                            StartupRenderer.Instance.WriteLine($"✅ yt-cipher и Lavalink запущены и отвечают ({_config.Music.Host}:{_config.Music.Port})");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        StartupRenderer.Instance.WriteLine($"❌ Ошибка запуска музыкального стека: {ex.Message}");
                                    }
                                    finally
                                    {
                                        // Снимаем sink — дальнейшие логи идут обратно в обычный путь
                                        _lavalinkService.StartupLogSink = null;
                                                            StartupRenderer.Instance.WriteFooter("ЭТАП 4/4: ИНИЦИАЛИЗАЦИЯ МУЗЫКИ — ЗАВЕРШЕНА");
                                                        }
                                }

                // ЭТАП 5: ПРОВЕРКА СИСТЕМ И ОТПРАВКА СТАТУСОВ
                var stage4Lines = new List<string>();

                // Выполняем проверку здоровья систем
                var healthChecks = await PerformSystemHealthCheckAsync();

                // Подзаголовок: проверка систем
                StartupRenderer.Instance.WriteHeader("ПРОВЕРКА СИСТЕМ");
                foreach (var check in healthChecks)
                {
                    var icon = check.IsHealthy ? "✅" : "❌";
                    var firstLine = check.Message.Split('\n')[0];
                    var systemNamePart = $"{icon} {check.SystemName}: ";

                    // Не обрезаем - пусть WrapStartupBoxContent сам переносит
                    StartupRenderer.Instance.WriteLine($"{systemNamePart}{firstLine}");

                    // Если сообщение многострочное (Telegram), добавляем только первые 2 детальные строки
                    if (check.Message.Contains("\n"))
                    {
                        var lines = check.Message.Split('\n').Skip(1).Take(2);
                        foreach (var line in lines)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                StartupRenderer.Instance.WriteLine($"  {line.Trim()}");
                            }
                        }
                    }
                }

                // Определяем, все ли системы здоровы
                var allHealthy = healthChecks.All(c => c.IsHealthy);
                var overallStatus = allHealthy ? "✅ ВСЕ СИСТЕМЫ РАБОТАЮТ" : "⚠️ ОБНАРУЖЕНЫ ПРОБЛЕМЫ";

                StartupRenderer.Instance.WriteLine($"ИТОГ: {overallStatus}");
                // Закрывающая линия подзаголовка проверки
                StartupRenderer.Instance.WriteFooter("ПРОВЕРКА СИСТЕМ");

                // Подзаголовок: отправка статусов
                StartupRenderer.Instance.WriteHeader("ОТПРАВКА СТАТУСОВ");
                var guildsList = _client!.Guilds.ToList();
                for (int i = 0; i < guildsList.Count; i++)
                {
                    var guild = guildsList[i];
                    if (_serverConfigs.TryGetValue(guild.Id, out var config))
                    {
                        if (config.ModerateChannelID == 0)
                        {
                            StartupRenderer.Instance.WriteLine($"⊘ {guild.Name}: канал не настроен");
                        }
                        else
                        {
                            if (_statusNotifier != null)
                            {
                                // Передаём результаты проверки в StatusNotifier
                                var ok = await _statusNotifier.SendSystemsActiveToGuild(guild, config,
                                    $"Тип запуска: {GetStartupTypeDisplay()}",
                                    healthChecks);

                                if (ok)
                                    StartupRenderer.Instance.WriteLine($"✅ {guild.Name}");
                                else
                                    StartupRenderer.Instance.WriteLine($"❌ {guild.Name}: ошибка отправки");
                            }
                        }
                    }
                    await Task.Delay(200);
                }

                if (guildsList.Count == 0)
                    StartupRenderer.Instance.WriteLine("Нет серверов для отправки стартовых уведомлений.");

                // Закрывающая линия подзаголовка отправки
                StartupRenderer.Instance.WriteFooter("ОТПРАВКА СТАТУСОВ");

                // Пустая строка — чтобы последующие плановые логи не лежали впритык
                // к закрывающей линии подблока.
                StartupRenderer.Instance.WriteLine(string.Empty);

                // Завершаем стартовый рендерер — дальше идут обычные логи бота
                StartupRenderer.Instance.EndStartup();

                                // ФИНАЛ
                _fullReadyTime = DateTime.UtcNow;
                // Защита: если событие Ready не сработало и _readyTime остался MinValue,
                // используем время старта инициализации как начало, чтобы не получить отрицательное время.
                var startTime = _readyTime == DateTime.MinValue ? _startupTime : _readyTime;
                var initTime = (_fullReadyTime - startTime).TotalSeconds;

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

                                // Одноразовая перерисовка старых анонсов EventOps новым форматированием.
                                // Выполняется только если флаг RemigratedOnce не выставлен. После
                                // успешного прохода флаг сохраняется в event_announcements.json,
                                // и при следующих рестартах этот блок будет пропускаться.
                                                                if (_eventAnnouncementStore is { } store && !store.IsRemigratedOnce())
                                {
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            // Дать шлюзу Discord догнать загрузку кэша каналов/сообщений
                                            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                                            await _eventOpsRemigrator!.RunAsync(_client!, CancellationToken.None).ConfigureAwait(false);
                                        }
                                        catch (Exception ex)
                                        {
                                            BotLogger.Error(LogCategory.Discord,
                                                $"[EventOpsRemigrate] фоновый запуск упал: {ex.GetType().Name}: {ex.Message}");
                                        }
                                    });
                                }

                                // EventOpsLifecycleService: восстановление отложенных таймеров
                                // (reminder1h / cleanup24h) для уже опубликованных событий.
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        // Чуть позже, чтобы клиент успел законнектиться.
                                        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                                        await _eventOpsLifecycle!.RebuildFromStoreAsync(CancellationToken.None).ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        BotLogger.Error(LogCategory.Discord,
                                            $"[EventOpsLifecycle] RebuildFromStoreAsync упал: {ex.GetType().Name}: {ex.Message}");
                                    }
                                });
                            }
                            catch (Exception ex)
                            {
                                            StartupRenderer.Instance.WriteError($"❌ КРИТИЧЕСКАЯ ОШИБКА ИНИЦИАЛИЗАЦИИ: {ex.Message}");
                                            StartupRenderer.Instance.WriteError($"   Стек: {ex.StackTrace}");
                            }
                        }

        private async Task OnDisconnected(Exception exception)
        {
                    // 🩹 stress-monitor: маркер потери соединения для внешнего монитора.
                    // Парсер ждёт эту подстроку. Сама причина (exception) нам не нужна —
                    // мы только знаем, что сессия потеряна.
                    var disconnectMsg = exception?.Message ?? "<no-exception>";

                                        // Останавливаем ремиграцию: цикл ModifyAsync после logout нам больше не нужен.
                            try { _eventOpsRemigrator?.Cancel(); } catch { }

                    // Останавливаем таймеры lifecycle — после дисконнекта Task.Delay внутри
                    // них всё равно сработает, но делать там нечего (REST не ответит).
                    try { _eventOpsLifecycle?.Cancel(); } catch { }

                    // ✅ R6 fix: при отключении от Discord все pending-UI кнопки
                    // «Продолжить» теряют смысл — клиент их не видит. Чистим.
                    try { ClearAllPendingBetUi(); } catch { }

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
                try { if (_webDashboard != null) await _webDashboard.StopAsync(); } catch (Exception ex) { Console.WriteLine($"Error stopping web dashboard: {ex}"); }
                try { _webDashboard?.Dispose(); } catch { }
                _webDashboard = null;
                            try { _eventOpsRemigrator?.Cancel(); } catch { }
                            CleanupServices();

                // Остановим UI корректно
                try
                {
                    _ui?.Dispose();
                    _ui = null;
                    _uiStarted = false;
                }
                catch (Exception ex) { Console.WriteLine($"Error disposing UI: {ex}"); }

                // ✅ R6 fix: очищаем все pending-UI кнопки «Продолжить» —
                // Discord-клиент сейчас уходит в shutdown, отвечать на них всё равно некому.
                try { ClearAllPendingBetUi(); } catch { }

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
                        try { _client.GuildMemberUpdated -= OnGuildMemberUpdated; } catch (Exception ex) { Console.WriteLine($"Error unsubscribing GuildMemberUpdated: {ex}"); }

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
                    await new GameSessionCommands(_client!, _googleSheetsService).HandleEditModal(modal);
                    return;
                }

                if (parts[0] == "music_goto_modal")
                {
                    if (_musicCommands is not null)
                        await _musicCommands.HandleGoToModalAsync(modal);
                    return;
                }

                // Handle bet modal: pred_bet_modal:<guildId>:<channelId>
                                // ✅ pred-parallelization: добавлен channelId — модал привязан к конкретному прогнозу.
                                if (parts[0] == "pred_bet_modal")
                                {
                                    if (parts.Length < 3)
                                    {
                                        await modal.RespondAsync("Неверный модал.", ephemeral: true);
                                        return;
                                    }

                                    if (!ulong.TryParse(parts[1], out var guildId))
                                    {
                                        await modal.RespondAsync("Неверный идентификатор сервера.", ephemeral: true);
                                        return;
                                    }

                                    if (!ulong.TryParse(parts[2], out var channelId))
                    {
                                        await modal.RespondAsync("Неверный идентификатор канала.", ephemeral: true);
                                        return;
                                    }

                                    // Remove the earlier ephemeral "balance + continue" UI right after modal submit.
                                    try
                                    {
                                        if (modal.User != null && _pendingBetUi.TryRemove($"{guildId}:{modal.User.Id}", out var pending))
                                        {
                                            try { await pending!.DeleteOriginalResponseAsync().ConfigureAwait(false); } catch { }
                                        }
                                    }
                                    catch { }

                                    // Extract fields from modal components (flat)
                                    string outcomeStr = string.Empty;
                                    string amountStr = string.Empty;
                                    foreach (var comp in modal.Data.Components)
                                    {
                                        if (string.Equals(comp.CustomId, "outcome", StringComparison.OrdinalIgnoreCase)) outcomeStr = comp.Value ?? string.Empty;
                                        else if (string.Equals(comp.CustomId, "amount", StringComparison.OrdinalIgnoreCase)) amountStr = comp.Value ?? string.Empty;
                    }

                                    if (!int.TryParse(outcomeStr, out var outcomeNum) || outcomeNum < 1)
                                    {
                                        await modal.FollowupAsync("Неверный номер исхода.", ephemeral: true).ConfigureAwait(false);
                                        ScheduleDeleteOriginalResponse(modal);
                                        return;
                                    }

                                    // ✅ Проверка: исход существует в активном прогнозе В этом канале.
                                    var activePrediction = _predictionService!.GetActive(guildId, channelId);
                                    if (activePrediction == null || activePrediction.GetOutcomeById(outcomeNum) == null)
                                    {
                                        var maxOutcome = activePrediction?.Outcomes.Count ?? 2;
                                        await modal.FollowupAsync($"Исход должен быть от 1 до {maxOutcome}.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                                    // ✅ pred-parallelization: проверяем, что юзер сидит в канале прогноза.
                                    var guildUserForBet = modal.User as SocketGuildUser;
                                    var userVoice = guildUserForBet?.VoiceChannel;
                                    if (userVoice == null || userVoice.Id != activePrediction.ChannelId)
                                    {
                                        var expectedChannel = _client?.GetGuild(guildId)?.GetVoiceChannel(activePrediction.ChannelId);
                                        var expectedText = expectedChannel != null ? $"{expectedChannel.Mention}" : $"канал с ID {activePrediction.ChannelId}";
                                        await modal.FollowupAsync($"Ставить можно только находясь в голосовом канале прогноза: {expectedText}.", ephemeral: true).ConfigureAwait(false);
                                        ScheduleDeleteOriginalResponse(modal);
                                        return;
                                    }

                                    if (!long.TryParse(amountStr, out var amount) || amount <= 0)
                                    {
                                        await modal.FollowupAsync("Сумма должна быть положительна.", ephemeral: true).ConfigureAwait(false);
                                        ScheduleDeleteOriginalResponse(modal);
                                        return;
                                    }

                                    var userId = modal.User?.Id ?? 0;
                                    if (modal.User != null)
                                    {
                                        try
                                        {
                                            _pointsUserIndex.UpsertFromUser(guildId, modal.User);
                                            _ = Task.Run(() => _pointsUserIndex.SaveAsync());
                                        }
                                        catch { }
                                    }
                                    if (userId != 0)
                                    {
                                        var res = await _predictionService!.PlaceBetAsync(activePrediction, userId, outcomeNum, amount);
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
                                    }

                                    return;
                                }

                                // Handle bet add modal: pred_bet_add_modal:<guildId>:<channelId>
                                // ✅ pred-parallelization: добавлен channelId, чтобы найти именно тот прогноз, к которому добавлена ставка.
                                if (parts[0] == "pred_bet_add_modal")
                                {
                                    if (parts.Length < 3)
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

                                    if (!ulong.TryParse(parts[2], out var channelId))
                                    {
                                        await modal.FollowupAsync("Неверный идентификатор канала.", ephemeral: true).ConfigureAwait(false);
                                        ScheduleDeleteOriginalResponse(modal);
                                        return;
                                    }

                                    // Remove the earlier ephemeral "balance + continue" UI right after modal submit.
                                    try
                                    {
                                        if (_pendingBetUi.TryRemove($"{guildId}:{modal.User?.Id}", out var pending) && pending != null)
                                        {
                                            try { await pending.DeleteOriginalResponseAsync().ConfigureAwait(false); } catch { }
                                        }
                                    }
                                    catch { }

                                    var active = _predictionService?.GetActive(guildId, channelId);
                                    if (active == null || active.IsResolved)
                                    {
                                        await modal.FollowupAsync("Сейчас нет активного прогноза в этом канале.", ephemeral: true).ConfigureAwait(false);
                                        ScheduleDeleteOriginalResponse(modal);
                                        return;
                                    }

                                    if (modal.User == null || !active.Bets.TryGetValue(modal.User.Id, out var existingBet) || existingBet == null)
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
                    if (modal.User != null)
                    {
                        try
                        {
                            _pointsUserIndex.UpsertFromUser(guildId, modal.User);
                            _ = Task.Run(() => _pointsUserIndex.SaveAsync());
                        }
                        catch { }
                    }
                    if (_predictionService != null && modal.User != null)
                    {
                                            var res = await _predictionService.PlaceBetAsync(active, modal.User.Id, outcomeNum, amount);
                        await LogInfo($"PlaceBet(add) result: ok={res.ok} error={res.error}");
                        if (res.ok)
                        {
                            try { await modal.RespondAsync($"Ставка увеличена на {amount} (исход {outcomeNum}).", ephemeral: true).ConfigureAwait(false); } catch { }
                        }
                        else
                        {
                            try { await modal.RespondAsync(res.error, ephemeral: true).ConfigureAwait(false); } catch { }
                        }
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
                        if (string.Equals(comp.CustomId, "title", StringComparison.OrdinalIgnoreCase)) title = comp.Value ?? string.Empty;
                        else if (string.Equals(comp.CustomId, "outcome1", StringComparison.OrdinalIgnoreCase)) oc1 = comp.Value ?? string.Empty;
                        else if (string.Equals(comp.CustomId, "outcome2", StringComparison.OrdinalIgnoreCase)) oc2 = comp.Value ?? string.Empty;
                        else if (string.Equals(comp.CustomId, "outcome3", StringComparison.OrdinalIgnoreCase)) oc3 = comp.Value ?? string.Empty;
                        else if (string.Equals(comp.CustomId, "duration_minutes", StringComparison.OrdinalIgnoreCase)) durationStr = comp.Value ?? string.Empty;
                    }

                    await LogInfo($"Create modal values: title='{title}' oc1='{oc1}' oc2='{oc2}' oc3='{oc3}' duration='{durationStr}' user={modal.User?.Id}");

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

                    try { await modal.DeferAsync(ephemeral: true).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        DeferFailureLogger.Log("PredictionCreate3", ex, modal, modal.Data.CustomId);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                    if (modal.User == null)
                    {
                        try { await modal.FollowupAsync("Не удалось определить пользователя.", ephemeral: true).ConfigureAwait(false); } catch { }
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }
                    var creatorId = modal.User.Id;

                    // ✅ Обновлено: создание прогноза с N исходами
                    var outcomeNames = new List<string> { oc1, oc2 };
                    if (!string.IsNullOrWhiteSpace(oc3))
                    {
                        outcomeNames.Add(oc3);
                    }

                    var channel = _client!.GetChannel(channelId) as ISocketMessageChannel;
                    if (channel == null)
                    {
                        await modal.FollowupAsync("Не удалось найти канал для создания прогноза.", ephemeral: true).ConfigureAwait(false);
                        ScheduleDeleteOriginalResponse(modal);
                        return;
                    }

                    var createRes = await _predictionService!.CreateAsync(guildId, creatorId, channel, title, outcomeNames.ToArray(), TimeSpan.FromMinutes(minutes), eventId: GetActiveEventIdOnChannel(guildId, channelId));
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
                        var key = $"{guildId}:{channelId}:{modal.User?.Id}";
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
                        var key = $"{guildId}:{channelId}:{modal.User?.Id}";
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
                            else if (string.Equals(comp.CustomId, "outcome2", StringComparison.OrdinalIgnoreCase)) oc2 = comp.Value ?? string.Empty;
                            else if (string.Equals(comp.CustomId, "outcome3", StringComparison.OrdinalIgnoreCase)) oc3 = comp.Value ?? string.Empty;
                            else if (string.Equals(comp.CustomId, "outcome4", StringComparison.OrdinalIgnoreCase)) oc4 = comp.Value ?? string.Empty;
                            else if (string.Equals(comp.CustomId, "outcome5", StringComparison.OrdinalIgnoreCase)) oc5 = comp.Value ?? string.Empty;
                        }

                        if (string.IsNullOrWhiteSpace(oc1) || string.IsNullOrWhiteSpace(oc2))
                        {
                            await modal.FollowupAsync("Заполните минимум 2 исхода.", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }

                        try { await modal.DeferAsync(ephemeral: true).ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            DeferFailureLogger.Log("PredictionCreate5", ex, modal, modal.Data.CustomId);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }

                        if (modal.User == null)
                        {
                            try { await modal.FollowupAsync("Не удалось определить пользователя.", ephemeral: true).ConfigureAwait(false); } catch { }
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }
                        var creatorId = modal.User.Id;

                        // Собираем исходы
                        var outcomeNames = new List<string> { oc1, oc2 };
                        if (!string.IsNullOrWhiteSpace(oc3)) outcomeNames.Add(oc3);
                        if (!string.IsNullOrWhiteSpace(oc4)) outcomeNames.Add(oc4);
                        if (!string.IsNullOrWhiteSpace(oc5)) outcomeNames.Add(oc5);

                        var channel = _client!.GetChannel(channelId) as ISocketMessageChannel;
                        if (channel == null)
                        {
                            await modal.FollowupAsync("Не удалось найти канал для создания прогноза.", ephemeral: true).ConfigureAwait(false);
                            ScheduleDeleteOriginalResponse(modal);
                            return;
                        }

                        var createRes = await _predictionService!.CreateAsync(guildId, creatorId, channel, title, outcomeNames.ToArray(), TimeSpan.FromMinutes(minutes), eventId: GetActiveEventIdOnChannel(guildId, channelId));
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
                BotLogger.Error(LogCategory.Discord, $"[ModalSubmitted:{customId}] {ex.GetType().Name}: {ex.Message}");
                try { await LogError($"HandleModalSubmitted exception for CustomId={customId}: {ex}"); } catch { }
                try { await modal.RespondAsync("Что-то пошло не так. Подробности в логах бота.", ephemeral: true).ConfigureAwait(false); } catch { }
                try { ScheduleDeleteOriginalResponse(modal); } catch { }
            }
        }

        public async Task HandleButtonExecuted(SocketMessageComponent component)
        {
            await Task.Yield(); // Сразу освобождаем поток шлюза

            try
            {
                BotLogger.Info(LogCategory.Discord, $"[ButtonExecuted] customId={component.Data.CustomId} user={component.User.Id}");
                await ProcessButtonAsync(component).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { await PredictionErrorLogger.LogAsync("HandleButtonExecuted", ex, $"customId={component.Data.CustomId}").ConfigureAwait(false); } catch { }
                BotLogger.Error(LogCategory.Discord, $"[ButtonExecuted:{component.Data.CustomId}] {ex.GetType().Name}: {ex.Message}");
                await LogError($"Ошибка обработки кнопки: {ex.Message}");
                try
                {
                    if (!component.HasResponded)
                        await component.RespondAsync("Ошибка обработки кнопки. Подробности в логах.", ephemeral: true).ConfigureAwait(false);
                    else
                        await component.FollowupAsync("Ошибка обработки кнопки. Подробности в логах.", ephemeral: true).ConfigureAwait(false);
                }
                catch { }
            }
        }

        public async Task HandleSelectMenuExecuted(SocketMessageComponent component)
                {
                    await Task.Yield();
                    // Note: PreDefer для music_search_select делает _musicCommands.HandleButtonAsync внутри.
                    // Не дублируем Defer тут, чтобы не получить "Cannot respond or defer twice".
                    try
                    {
                        var cid = component.Data.CustomId;
                        if (_musicCommands is not null && cid.StartsWith("music_search_select:"))
                            await _musicCommands.HandleButtonAsync(component);
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Error(LogCategory.Discord, $"[SelectMenuExecuted:{component.Data.CustomId}] {ex.GetType().Name}: {ex.Message}");
                        await LogError($"Ошибка обработки SelectMenu: {ex.Message}");
                        try
                        {
                            if (!component.HasResponded)
                                await component.RespondAsync("Ошибка взаимодействия. Подробности в логах.", ephemeral: true).ConfigureAwait(false);
                            else
                                await component.FollowupAsync("Ошибка взаимодействия. Подробности в логах.", ephemeral: true).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }

        private async Task ProcessButtonAsync(SocketMessageComponent component)
        {
            var parts = component.Data.CustomId.Split(':');
            var buttonType = parts.Length > 0 ? parts[0] : component.Data.CustomId;

            BotLogger.Info(LogCategory.Discord, $"[ProcessButton] buttonType={buttonType} customId={component.Data.CustomId}");

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
                case "force_stop":
                    await new GameSessionCommands(_client!, _googleSheetsService).HandleControlButton(component);
                    break;

                case "no_stats":
                case "general_stats":
                case "detailed_stats":
                    await new GameSessionCommands(_client!, _googleSheetsService).HandleStatsButton(component);
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

                case var s when s == "voice_limit" && component.Data.CustomId.StartsWith(VoiceChannelCommands.ButtonPrefix, StringComparison.Ordinal):
                    await new VoiceChannelCommands().HandleLimitButtonAsync(component);
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
                    else
                    {
                        BotLogger.Info(LogCategory.Discord, $"[ProcessButton:default] no handler for cid={cid}");
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

    if (_eventNotifications == null)
    return false;

    if (normalized is "стоп" or "хватит" or "stop")
    {
    _eventNotifications.Pause(userId);
    await message.AddReactionAsync(new Emoji("✅"));
    await message.Channel.SendMessageAsync("[Сохранено] Отключил личные уведомления о новых событиях. Чтобы включить обратно — напиши мне «хочу» или подпишись заново через /event_notify subscribe на сервере.");
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
                                var modChan = await _client!.GetChannelAsync(sconfig.ModerateChannelID) as ITextChannel;
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

            // Обработка сообщений, начинающихся с "!" (только если после ! сразу идёт буква).
                        // На "!", "!!", "! " (с пробелом), "!.", "!?", "!/", "!123" и т.п. — НЕ реагируем,
                        // иначе бот отвечает "Неизвестная команда" на любой мусор после "!", что раздражает.
                        if (message.Content.Length >= 2
                            && message.Content[0] == '!'
                            && char.IsLetter(message.Content[1]))
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

        private (ulong welcomeChannelId, ulong rollChannelId, ulong generalRGChannelID, string lineMessages, string emoteKappa, string emoteAga) GetResponseData(SocketMessage message)
        {
            var channel = message.Channel as SocketGuildChannel;
    if (channel == null || !_serverConfigs.TryGetValue(channel.Guild.Id, out var config))
            {
            return (0, 0, 0, string.Empty, string.Empty, string.Empty);
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

        return (0, 0, 0, string.Empty, string.Empty, string.Empty);
        }

        private async Task HandleDictatorCommand(SocketGuildUser? user, SocketMessage message)
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

        private async Task HandleMasteryArbitrarinessCommand(SocketGuildUser? user, SocketMessage message, string emoteKappa, string emoteAga)
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

        private async Task HandleLineCommand(SocketGuildUser? user, SocketMessage message, string lineMessages)
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

        /// <summary>
        /// Команды, для которых <see cref="SocketSlashCommand.DeferAsync"/> вызывается заранее,
        /// в самом начале <see cref="OnSlashCommandExecuted"/>, чтобы исключить
        /// «Cannot defer an interaction after 3 seconds!» на проде.
        /// Внутри самих команд двойной defer подавляется — повторно <c>DeferAsync</c> не зовём.
        /// Список определён на уровне класса (см. <see cref="_preDeferCommands"/>).
        /// </summary>
        private async Task OnSlashCommandExecuted(SocketSlashCommand command)
        {
            // Сразу освобождаем шлюз, чтобы не блокировать обработку других взаимодействий,
            // пока мы идём в switch и далее в обработчик команды.
            await Task.Yield();

                        if (command == null)
                                                                                                {
                                                                                                    BotLogger.Warn(LogCategory.Cmd, "[OnSlashCommandExecuted] Получен пустой command — пропускаю.");
                                                                                                    return;
                                                                                                }
                                                                                                var name = command.Data?.Name ?? "<null>";
                                                                                                                                                if (command.Data is null)
                                                                                                {
                                                                                                                                                    BotLogger.Warn(LogCategory.Cmd, $"[OnSlashCommandExecuted] command.Data is null, name={name} — пропускаю.");
                                                                                                    return;
                                                                                                }

                                                                                                // Предварительный ACK для «тяжёлых» команд — должен пройти
                                                                                                // ДО switch и до получения сервиса из DI. Discord требует
                                                                                                // ответ в течение 3 секунд; раньше при GC/IO-паузах
                                                                                                // _services.GetRequiredService<RollDiceCommands>() не укладывался.
                                                                                                                                                                                                //
                                                                                                                                                                                                // 🩹 10062-retry: после Gateway Reconnect или сразу после старта
                                                                                                                                                                                                // interaction ещё не синхронизирован с нашей сессией, и первый
                                                                                                                                                                                                // defer возвращает 10062. Делаем до 2 ретраев с короткой паузой
                                                                                                                                                                                                // (100 мс между попытками). В сумме тратим не больше ~300 мс —
                                                                                                                                                                                                // укладываемся в 3-секундный Discord-окно.
                                                                                                                                                                                                if (_preDeferCommands.Contains(name))
                                                                                                                                                                                                {
                                                                                                                                                                                                    Exception? lastEx = null;
                                                                                                                                                                                                    bool deferred = false;
                                                                                                                                                                                                    // Если недавно был реконнект (<2 сек) — подождём чуть перед первой попыткой,
                                                                                                                                                                                                    // чтобы Discord успел зарегистрировать сессию.
                                                                                                                                                                                                    if (TimeSinceLastReconnect < TimeSpan.FromSeconds(2))
                                                                                                                                                                                                    {
                                                                                                                                                                                            try { await Task.Delay(200).ConfigureAwait(false); }
                                                                                                                                                                                            catch { }
                                                                                                                                                                                                    }
                                                                                                                                                                                                    for (int attempt = 0; attempt <= 2 && !deferred; attempt++)
                                                                                                                                                                                                    {
                                                                                                                                                                                                        try
                                                                                                                                                                                                        {
                                                                                                                                                                                                            // 🩹 perf: замер времени DeferAsync — если увидим >100мс,
                                                                                                                                                                                                            // значит, виноват IO/SSL/GC, а не 10062 (NotFound).
                                                                                                                                                                                                            var sw = System.Diagnostics.Stopwatch.StartNew();
                                                                                                                                                                                                            await command.DeferAsync().ConfigureAwait(false);
                                                                                                                                                                                                            sw.Stop();
                                                                                                                                                                                                            MarkPreDeferDone(command.Id);
                                                                                                                                                                                                            deferred = true;
                                                                                                                                                                                                            if (attempt > 0)
                                                                                                                                                                                                            {
                                                                                                                                                                                                                BotLogger.Info(LogCategory.Cmd,
                                                                                                                                                                                                                    $"[PreDefer:{name}] succeeded на попытке {attempt + 1}/{3} для interaction={command.Id} (был 10062 / SSL / TimeoutException ранее).");
                                                                                                                                                                                                            }
                                                                                                                                                                                                            else if (sw.Elapsed.TotalMilliseconds > 100)
                                                                                                                                                                                                            {
                                                                                                                                                                                                                                                                                                                                                                                                                            // 🩹 diag: снимаем GC-счётчики прямо в момент SLOW — если
                                                                                                                                                                                                                                                                                                                                                                                                                            // gcGen>=2 >0 или pauseDeltaMs > sw.Elapsed, значит виновата GC-пауза.
                                                                                                                                                                                                                                                                                                                                                                                                                            long totalPauseMs = (long)System.GC.GetTotalPauseDuration().TotalMilliseconds;
                                                                                                                                                                                                                                                                                                                                                                                                                            int gen0 = System.GC.CollectionCount(0);
                                                                                                                                                                                                                                                                                                                                                                                                                            int gen1 = System.GC.CollectionCount(1);
                                                                                                                                                                                                                                                                                                                                                                                                                            int gen2 = System.GC.CollectionCount(2);
                                                                                                                                                                                                                                                                                                                                                                                                                            long heapMB = (long)(System.GC.GetTotalMemory(false) / 1024d / 1024d);
                                                                                                                                                                                                                                                                                                                                                                                                                            BotLogger.Warn(LogCategory.Cmd,
                                                                                                                                                                                                                                                                                                                                                                                                                                $"[PreDefer:{name}] SLOW attempt=1/3 took={sw.Elapsed.TotalMilliseconds:F0}ms " +
                                                                                                                                                                                                                                                                                                                                                                                                                                $"interaction={command.Id} " +
                                                                                                                                                                                                                                                                                                                                                                                                                                $"gcPause={totalPauseMs}ms gen0={gen0} gen1={gen1} gen2={gen2} heap={heapMB:F1}MB " +
                                                                                                                                                                                                                                                                                                                                                                                                                                $"— возможна IO/GC пауза.");
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    }
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    // 🩹 perf: если DeferAsync занял >1500мс — мы почти уложились, но
                                                                                                                                                                                                                                                                                                                                                                                                                                            // находимся на грани 3-сек Discord-окна. Бросаем искусственное
                                                                                                                                                                                                                                                                                                                                                                                                                                            // HttpRequestException, чтобы зайти в ретрай-цикл ниже: попробуем ещё раз
                                                                                                                                                                                                                                                                                                                                                                                                                                            // с новым соединением из пула (текущий сокет в REST пуле медленный).
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    if (sw.Elapsed.TotalMilliseconds > 1500)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    {
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        throw new System.Net.Http.HttpRequestException(
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            $"DeferAsync slow={sw.Elapsed.TotalMilliseconds:F0}ms — retrying with new connection",
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            new System.Net.Sockets.SocketException(10060 /* ETIMEDOUT */));
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    }
                                                                                                                                                                                                        }
                                                                                                                                                                                                        catch (Exception ex)
                                                                                                                                                                                                        {
                                                                                                                                                                                                            lastEx = ex;
                                                                                                                                                                                                                                                                                                                                                                                                                    // Ретраим ТОЛЬКО 10062 (сессия не синхронизирована после Gateway Reconnect)
                                                                                                                                                                                                                                                                                                                                                                                                                    // и HttpRequestException с SSL/TLS (истёк keep-alive в пуле HTTP-соединений:
                                                                                                                                                                                                                                                                                                                                                                                                                    // Discord/Cloudflare закрыл idle-сокет, а .NET-клиент пытается писать в
                                                                                                                                                                                                                                                                                                                                                                                                                    // мёртвый поток; лечится короткой паузой и новым соединением из пула).
                                                                                                                                                                                                                                                                                                                                                                                                                    //
                                                                                                                                                                                                                                                                                                                                                                                                                    // TimeoutException = «мы реально опоздали с ответом» — бесполезен
                                                                                                                                                                                                                                                                                                                                                                                                                    // ретраить, потому что повторный DeferAsync на это же interaction тоже
                                                                                                                                                                                                                                                                                                                                                                                                                    // опоздает (Discord уже не ждёт ACK).
                                                                                                                                                                                                                                                                                                                                                                                                                    bool isHttpNotFound = ex is Discord.Net.HttpException httpEx
                                                                                                                                                                                                                                                                                                                                                                                                                        && httpEx.HttpCode == System.Net.HttpStatusCode.NotFound;
                                                                                                                                                                                                                                                                                                                                                                                                                    bool isSslBroken = ex is System.Net.Http.HttpRequestException httpReq
                                                                                                                                                                                                                                                                                                                                                                                                                        && (httpReq.InnerException is System.IO.IOException
                                                                                                                                                                                                                                                                                                                                                                                                                            || httpReq.InnerException is System.Net.Sockets.SocketException
                                                                                                                                                                                                                                                                                                                                                                                                                            || (httpReq.InnerException?.Message?.Contains("SSL") ?? false)
                                                                                                                                                                                                                                                                                                                                                                                                                            || (httpReq.Message?.Contains("SSL") ?? false));
                                                                                                                                                                                                                                                                                                                                                                                                                    bool retriable = isHttpNotFound || isSslBroken;
                                                                                                                                                                                                                                                                                                                                                                                                                    if (!retriable || attempt == 2)
                                                                                                                                                                                                                                                                                                                                                                                                                    {
                                                                                                                                                                                                                                                                                                                                                                                                                        DeferFailureLogger.Log($"PreDefer:{name}", ex, command, input: null);
                                                                                                                                                                                                                                                                                                                                                                                                                        break;
                                                                                                                                                                                                                                                                                                                                                                                                                    }
                                                                                                                                                                                                                                                                                                                                                                                                                    // Пауза между попытками: 150, 350 мс.
                                                                                                                                                                                                                                                                                                                                                                                                                    var delayMs = attempt == 0 ? 150 : 350;
                                                                                                                                                                                                                                                                                                                                                                                                                    try { await Task.Delay(delayMs).ConfigureAwait(false); } catch { }
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        // 🩹 perf: при сетевой ошибке форсируем прогрев нового соединения,
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        // чтобы следующая попытка не упёрлась в TLS handshake.
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        if (isSslBroken && _client?.Rest != null)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        {
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            _ = Task.Run(async () =>
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  {
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                try { await _client.Rest.GetCurrentUserAsync().ConfigureAwait(false); } catch { }
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            }).ConfigureAwait(false);
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        }
                                                                                                                                                                                                                                                                                                                                                                                                                }
                                                                                                                                                                                                    }
                                                                                                                                                                                                    if (!deferred)
                                                                                                                                                                                                    {
                                                                                                                                                                                                        // Все попытки исчерпаны. Interaction уже мёртв,
                                                                                                                                                                                                        // повторный DeferAsync в обработчике только заставит
                                                                                                                                                                                                        // пользователя ждать 3 секунды до "Приложение не отвечает".
                                                                                                                                                                                                        MarkPreDeferFailed(command.Id);
                                                                                                                                                                                                                                                                                                        if (lastEx != null)
                                                                                                                                                                                                                          DeferFailureLogger.Log($"PreDefer:{name}:gave-up", lastEx, command, input: null);
                                                                                                                                                                                                                                                                                                        return;
                                                                                                                                                                                                                                                                                                    }
                                                                                                                                                                                                                                                                                                }

                                                                                                try
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
                    case "voice":
                        await VoiceCommand(command);
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
                            await command.RespondAsync("🎵 Музыкальный модуль временно отключён.", ephemeral: true);
                        break;
                    case "music-playlist":
                        if (_musicCommands is not null)
                            await _musicCommands.HandleMusicPlaylistAsync(command);
                        else
                            await command.RespondAsync("🎵 Музыкальный модуль временно отключён.", ephemeral: true);
                        break;
                    default:
                        await command.RespondAsync("Команда не распознана.");
                        break;
                }
            }
            catch (Exception ex)
            {
                // Пишем в общий логгер (категория Predict подходит — она уже используется для всех
                // нештатных ситуаций взаимодействий) и дублируем в Cmd, чтобы было видно в обоих фильтрах.
                try { await PredictionErrorLogger.LogAsync("OnSlashCommandExecuted", ex, $"cmd={name} user={command.User?.Id} guild={command.GuildId}").ConfigureAwait(false); } catch { }
                BotLogger.Error(LogCategory.Cmd, $"[SlashCommand:{name}] {ex.GetType().Name}: {ex.Message}");

                // Пытаемся ответить пользователю, чтобы Discord не показывал «Приложение не отвечает».
                // RespondAsync можно вызвать только один раз — поэтому пробуем и через Respond, и через Followup.
                // Если уже был Defer — Respond упадёт, тогда Followup.
                try
                {
                    if (!command.HasResponded)
                        await command.RespondAsync("⚠️ Внутренняя ошибка при обработке команды. Подробности в логах бота.", ephemeral: true).ConfigureAwait(false);
                    else
                        await command.FollowupAsync("⚠️ Внутренняя ошибка при обработке команды. Подробности в логах бота.", ephemeral: true).ConfigureAwait(false);
                }
                catch { /* если и Respond, и Followup упали — Discord всё равно покажет таймаут, но лог останется */ }
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

    if (_eventNotifications == null)
    {
    await command.RespondAsync("Сервис уведомлений не инициализирован.", ephemeral: true);
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
            try
            {
                var queueModule = _services.GetRequiredService<QueueModule>();
                await queueModule.StopQueue(command);
                await LogInfo("Очередь остановлена.");
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[StopQueue] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("StopQueue", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в StopQueue: {ex.Message}");
            }
        }

        private async Task QueueCommand(SocketSlashCommand command)
        {
            try
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
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[QueueCommand] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("QueueCommand", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в QueueCommand: {ex.Message}");
            }
        }

        private async Task Q_InCommand(SocketSlashCommand command)
        {
            try
            {
                var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
                var input = inputOption?.Value?.ToString() ?? string.Empty;

                var queueModule = _services.GetRequiredService<QueueModule>();
                await queueModule.QIn_RollDice(command, input);
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[Q_InCommand] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("Q_InCommand", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в Q_InCommand: {ex.Message}");
            }
        }

        private async Task CloseChatCommand(SocketSlashCommand command)
        {
            try
            {
                var moderationModule = _services.GetRequiredService<ModerationCommands>();
                await moderationModule.CloseChat(command);
                await LogInfo("Чат или ветка закрыты.");
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[CloseChatCommand] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("CloseChatCommand", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в CloseChatCommand: {ex.Message}");
            }
        }

        private async Task OpenChatCommand(SocketSlashCommand command)
        {
            try
            {
                var moderationModule = _services.GetRequiredService<ModerationCommands>();
                await moderationModule.OpenChat(command);
                await LogInfo("Чат открыт и перемещён в указанную категорию.");
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[OpenChatCommand] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("OpenChatCommand", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в OpenChatCommand: {ex.Message}");
            }
        }

        private async Task ClearMessage(SocketSlashCommand command)
        {
            try
            {
                var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
                if (int.TryParse(inputOption?.Value?.ToString(), out int messagesToDelete))
                {
                    var mm = _services.GetRequiredService<ModerationCommands>();
                    await mm.ClearMessages(command, messagesToDelete);
                }
                else
                {
                    await LogInfo("Ошибка: неверный формат ввода при удалении сообщения. Пожалуйста, введите целое число.");
                    await command.RespondAsync("Ошибка: неверный формат ввода. Пожалуйста, введите целое число.", ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[ClearMessage] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("ClearMessage", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в ClearMessage: {ex.Message}");
            }
        }

        private async Task RollCommand(SocketSlashCommand command)
        {
            try
            {
                var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
                var input = inputOption?.Value?.ToString() ?? string.Empty;

                var diceModule = _services.GetRequiredService<RollDiceCommands>();
                await diceModule.RollDice(command, input);
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[RollCommand] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("RollCommand", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в RollCommand: {ex.Message}");
            }
        }

        private async Task VoiceCommand(SocketSlashCommand command)
        {
            try
            {
                var voiceModule = _services.GetRequiredService<VoiceChannelCommands>();
                await voiceModule.VoiceAsync(command);
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[VoiceCommand] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("VoiceCommand", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в VoiceCommand: {ex.Message}");
            }
        }

        private async Task Roll20Command(SocketSlashCommand command)
        {
            try
            {
                var diceModule = _services.GetRequiredService<RollDiceCommands>();
                await diceModule.Roll20(command);
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[Roll20Command] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("Roll20Command", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в Roll20Command: {ex.Message}");
            }
        }

        private async Task RollPicturesCommand(SocketSlashCommand command)
        {
            try
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
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[RollPicturesCommand] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("RollPicturesCommand", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в RollPicturesCommand: {ex.Message}");
            }
        }

        private async Task ServerInfoCommand(SocketSlashCommand command)
        {
            try
            {
                var infoModule = _services.GetRequiredService<InfoCommands>();
                await infoModule.ServerInfo(command);
                await LogInfo("Выведена информация о сервере.");
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[ServerInfoCommand] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("ServerInfoCommand", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в ServerInfoCommand: {ex.Message}");
            }
        }

        private async Task HelpCommand(SocketSlashCommand command)
        {
            try
            {
                var infoModule = _services.GetRequiredService<InfoCommands>();
                await infoModule.Help(command);
                await LogInfo("Выведена подсказка о командах.");
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[HelpCommand] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("HelpCommand", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в HelpCommand: {ex.Message}");
            }
        }

        private async Task Help_RollCommand(SocketSlashCommand command)
        {
            try
            {
                var infoModule = _services.GetRequiredService<InfoCommands>();
                await infoModule.Help_R(command);
                await LogInfo("Выведена подсказка о командах для бросков кубов.");
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[Help_RollCommand] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("Help_RollCommand", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в Help_RollCommand: {ex.Message}");
            }
        }

        private async Task Help_PredictCommand(SocketSlashCommand command)
        {
            try
            {
                var infoModule = _services.GetRequiredService<InfoCommands>();
                await infoModule.Help_Predict(command);
                await LogInfo("Выведена подсказка по прогнозам и ставкам.");
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[Help_PredictCommand] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("Help_PredictCommand", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в Help_PredictCommand: {ex.Message}");
            }
        }

        private async Task Help_GameSessionCommand(SocketSlashCommand command)
        {
            try
            {
                var infoModule = _services.GetRequiredService<InfoCommands>();
                await infoModule.Help_GS(command);
                await LogInfo("Выведена подсказка о командах для статистики.");
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[Help_GameSessionCommand] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("Help_GameSessionCommand", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в Help_GameSessionCommand: {ex.Message}");
            }
        }

        private async Task Help_MusicCommand(SocketSlashCommand command)
        {
            try
            {
                var infoModule = _services.GetRequiredService<InfoCommands>();
                await infoModule.Help_Music(command);
                await LogInfo("Выведена подсказка о музыкальных командах.");
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[Help_MusicCommand] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("Help_MusicCommand", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в Help_MusicCommand: {ex.Message}");
            }
        }

        private async Task Bug_ReportCommand(SocketSlashCommand command)
        {
            try
            {
                var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
                var input = inputOption?.Value?.ToString() ?? string.Empty;

                var infoModule = _services.GetRequiredService<InfoCommands>();
                await infoModule.Bug_Report(command, input);
                await LogInfo("Использовано уведомление администратора о баге.");
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[Bug_ReportCommand] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("Bug_ReportCommand", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в Bug_ReportCommand: {ex.Message}");
            }
        }

        private async Task StartGameSession(SocketSlashCommand command)
        {
            try
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
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Cmd, $"[StartGameSession] {ex.GetType().Name}: {ex.Message}");
                try { await PredictionErrorLogger.LogAsync("StartGameSession", ex).ConfigureAwait(false); } catch { }
                await LogError($"Ошибка в StartGameSession: {ex.Message}");
            }
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
            var guild = _client!.GetGuild(guildId);
            if (guild == null)
                return null;

            if (channelOpt != null)
            {
                // Discord.Net передаёт объект SocketChannel, а не ulong
                ulong directId = channelOpt is Discord.IChannel ch
                    ? ch.Id
                    : Convert.ToUInt64(channelOpt);
                var directChannel = guild.GetChannel(directId) ?? await _client.GetChannelAsync(directId) as SocketGuildChannel;
                var sgc = directChannel as SocketGuildChannel;
                if (sgc != null && sgc.Guild.Id == guildId && (!requireVoice || sgc is SocketVoiceChannel))
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
        sb.AppendLine("- Для логических переключателей (swear_filter, predictions, roll_pictures) используйте toggle:true/false или value:true/false.");
        sb.AppendLine("- Для event_voice_channel укажите ID или название голосового канала в value.");
        sb.AppendLine("- Для текстовых параметров (welcome_message, line_message) используйте value с текстом.");
        sb.AppendLine("- Для swear_words укажите слова через запятую в value (например: слово1,слово2). value:clear — очистить список.");
        sb.AppendLine();
        sb.AppendLine("Примеры:");
        sb.AppendLine("/settings action:set key:moderation_channel channel:#модерация");
        sb.AppendLine("/settings action:set key:moderation_channel value:123456789012345678");
        sb.AppendLine("/settings action:set key:default_role value:@Игрок");
        sb.AppendLine("/settings action:set key:master_role value:Мастер НРИ");
        sb.AppendLine("/settings action:set key:swear_filter toggle:true");
        sb.AppendLine("/settings action:set key:swear_words value:слово1,слово2,слово3");
        sb.AppendLine("/settings action:set key:swear_words value:clear");
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
                        #pragma warning disable CS0618 // ✅ pred-parallelization: EventVoiceChannelID obsolete, но поле отображается в info о сервере
                                sb.AppendLine($"event_voice_channel (deprecated): {sconfig.EventVoiceChannelID}");
                        #pragma warning restore CS0618
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
        #pragma warning disable CS0618 // ✅ pred-parallelization: deprecated, но /config get продолжает работать
                                    "event_voice_channel" => sconfig.EventVoiceChannelID.ToString() + " (deprecated)",
        #pragma warning restore CS0618
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
                                        #pragma warning disable CS0618 // ✅ pred-parallelization: deprecated setter, сохраняем для обратной совместимости /config set
                                                                                    sconfig.EventVoiceChannelID = id.Value;
                                        #pragma warning restore CS0618
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
                                var guild = _client!.GetGuild(guildId);
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
                                    var guild = _client!.GetGuild(guildId);
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
                            case "swear_words":
                                {
                                    if (string.IsNullOrWhiteSpace(valueOpt))
                                    {
                                        // Без значения — показываем текущий список
                                        var current = sconfig.SwearWords != null && sconfig.SwearWords.Count > 0
                                            ? string.Join(", ", sconfig.SwearWords)
                                            : "(список пуст — используются слова по умолчанию из конфига бота)";
                                        await command.RespondAsync($"Текущие слова фильтра: {current}", ephemeral: true);
                                        return;
                                    }
                                    // Значение: через запятую или «clear» для очистки
                                    if (valueOpt.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
                                    {
                                        sconfig.SwearWords = new List<string>();
                                        SaveServerConfigs();
                                        await command.RespondAsync("Список матерных слов очищен (будут использоваться слова по умолчанию).", ephemeral: true);
                                    }
                                    else
                                    {
                                        var words = valueOpt
                                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                            .Select(w => w.ToLowerInvariant())
                                            .Where(w => !string.IsNullOrWhiteSpace(w))
                                            .ToList();
                                        sconfig.SwearWords = words;
                                        SaveServerConfigs();
                                        await command.RespondAsync($"Список матерных слов обновлён ({words.Count} шт.): {string.Join(", ", words)}", ephemeral: true);
                                    }
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
if (_config!.Music.Enabled)
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

// 10. Проверка файла с текстовыми блоками (!правила/!ссылки/!запись)
// Файл может отсутствовать — но если его нет, шаблоны не загрузятся,
// и команды !правила/!ссылки/!запись будут без текста. Поэтому считаем
// отсутствие файла ошибкой (❌), а не OK (✅).
try
{
    var resolvedTextPath = BotConfig.ResolvePath(_config!.TextBlocksPath);
    if (File.Exists(resolvedTextPath))
    {
        var loaded = _textBlocks?.Count ?? 0;
        checks.Add(new SystemHealthCheck
        {
            SystemName = "Текстовые блоки (Pastes.txt)",
            IsHealthy = loaded > 0,
            Message = loaded > 0
                ? $"Загружено блоков: {loaded}"
                : "Файл пуст — шаблоны команд отсутствуют",
        });
    }
    else
    {
        checks.Add(new SystemHealthCheck
        {
            SystemName = "Текстовые блоки (Pastes.txt)",
            IsHealthy = false,
            Message = $"Файл не найден ({resolvedTextPath}) — команды !правила/!ссылки/!запись без шаблонов",
        });
    }
}
catch (Exception ex)
{
    checks.Add(new SystemHealthCheck
    {
        SystemName = "Текстовые блоки (Pastes.txt)",
        IsHealthy = false,
        Message = $"Ошибка проверки: {ex.Message}",
    });
}

            return checks;
        }

private Task LogStartup(string message)
    {
        // Маршрутизация в правильный канал по эмодзи-префиксу:
        //   • "⚠️ …" → WriteWarn  (жёлтый уровень, WARN);
        //   • "❌ …" → WriteError (красный уровень, ERROR);
        //   • всё остальное → WriteLine (Info/Stage).
        // Это восстанавливает уровень семантики в run.log — раньше
        // ВСЁ шло через Stage, и в логе получалось "[INFO] Отключение:"
        // без сигнальной отметки, по которой можно было бы заметить
        // инцидент глазами.
        if (message.Contains("❌"))
            StartupRenderer.Instance.WriteError(message);
        else if (message.Contains("⚠️"))
            StartupRenderer.Instance.WriteWarn(message);
        else
            StartupRenderer.Instance.WriteLine(message);
        return Task.CompletedTask;
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
        private void ServiceLogSink(string message) => BotLogger.Info(LogCategory.System, message);

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
            => await BotLogger.ErrorAsync(LogCategory.System, errorMessage).ConfigureAwait(false);

        private async Task LogInfo(string infoMessage)
            => await BotLogger.InfoAsync(LogCategory.Discord, infoMessage).ConfigureAwait(false);

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

                    var welcomeChannel = _client!.GetChannel(config.WelcomeChannelID) as IMessageChannel;
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
                        // Pastes.txt — опциональный файл с шаблонами команд (!правила/!ссылки/!запись).
                        // Если файла нет, не выкидываем в run.log красный ERROR при каждом старте —
                        // просто отмечаем это в DEBUG. Факт отсутствия всплывёт в ПРОВЕРКЕ СИСТЕМ
                        // (Этап 4) и в финальном embed статуса.
                        try { BotLogger.Debug(LogCategory.Boot, $"LoadTextFromFile: файл '{filePath}' отсутствует, текстовые шаблоны будут пустыми."); } catch { }
                        return textBlocks;
                    }

            try
            {
                var lines = File.ReadAllLines(filePath);
                                string? currentKey = null;
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
