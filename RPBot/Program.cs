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
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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
        public string WelcomeMessage { get; set; }
        public string LineMessage { get; set; }
        public ulong DefaultRoleID { get; set; }
    }

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
    }

    class Program : IDisposable, IBotController
    {
        private DiscordSocketClient _client;
        private CommandService _commandService;
        private IServiceProvider _services;
        private CommandHandler _commandHandler;
        private Dictionary<string, string> _textBlocks;
        private readonly SemaphoreSlim _restartLock = new(1, 1);
        private bool _isDisposed;
        private bool _shouldExit = false;
        private bool _shouldRestart = false;

        private ReconnectionService _reconnectionService;
        private ConnectionPredictor _connectionPredictor;
        private StatusNotifier _statusNotifier;

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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CleanupServices error: {ex.Message}");
            }
        }

        private BotConfig _config;
        private Dictionary<ulong, ServerConfig> _serverConfigs = new();

        public Program()
        {
            _config = BotConfig.Load("config.json");

            _client = CreateDiscordClient();
            _commandService = new CommandService();

            // ИНИЦИАЛИЗАЦИЯ НОВЫХ СЕРВИСОВ
            _reconnectionService = new ReconnectionService(_client);
            _connectionPredictor = new ConnectionPredictor(_reconnectionService);
            _statusNotifier = new StatusNotifier(_client, ServerConfigs);

            // ПОДПИСКА НА СОБЫТИЯ СЕРВИСОВ
            _reconnectionService.OnDisconnectDetected += OnDisconnectDetected;
            _reconnectionService.OnReconnectStarted += OnReconnectStarted;
            _reconnectionService.OnReconnectCompleted += OnReconnectCompleted;
            _connectionPredictor.OnPredictionMade += OnPredictionMade;

            _services = new ServiceCollection()
                .AddSingleton(_client)
                .AddSingleton(_commandService)
                .AddSingleton(_reconnectionService)
                .AddSingleton(_connectionPredictor)
                .AddSingleton(_statusNotifier)
                .AddSingleton<QueueModule>()
                .AddSingleton<InfoCommands>()
                .AddSingleton<RollDiceCommands>()
                .AddSingleton<GameSessionCommands>()
                .AddSingleton<ModerationCommands>()
                .BuildServiceProvider();
        }

        private DiscordSocketClient CreateDiscordClient()
        {
            var config = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers |
                               GatewayIntents.GuildMessages | GatewayIntents.MessageContent |
                               GatewayIntents.GuildScheduledEvents,
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
        private DateTime _startupTime;

        public bool ShouldExit => _shouldExit;
        public bool ShouldRestart => _shouldRestart;

        public async Task RestartAsync()
        {
            // Убираем дублирование - оставляем только одно сообщение
            if (_ui != null && _uiStarted)
            {
                _ui.AddLog("Перезапуск из консоли...");
                _ui.ClearForRestart();
                _ui.Dispose();
                _ui = null;
                _uiStarted = false;
            }

            _shouldRestart = true;
            _shouldExit = true;
            _currentStartupType = StartupType.Restart;
            _statusNotifier?.SetStartupType(StartupType.Restart);
            _reconnectionService?.Shutdown();
            await _client.StopAsync();
        }

        public async Task StopAsync()
        {
            await LogStartup("Остановка из консоли...");
            _shouldExit = true;
            _reconnectionService?.Shutdown();
            await _client.StopAsync();
            Environment.Exit(0);
        }

        public void SetStartupType(StartupType type)
        {
            _currentStartupType = type;
            _statusNotifier?.SetStartupType(type);
        }

        static async Task Main(string[] args)
        {
            bool restart;
            int restartCount = 0;

            do
            {
                restart = false;

                if (restartCount > 0)
                {
                    Console.Clear();
                    Console.WriteLine($" ПЕРЕЗАПУСК #{restartCount}");
                    Console.WriteLine($"Время: {DateTime.Now:HH:mm:ss}");
                    Console.WriteLine("================================================");
                    Console.WriteLine();
                }

                using (var program = new Program())
                {
                    if (restartCount > 0)
                    {
                        program.SetStartupType(StartupType.Restart);
                    }

                    await program.RunBotAsync();
                    restart = program.ShouldRestart;
                    restartCount++;
                }

                if (restart)
                {
                    Console.WriteLine("\n Подготовка к перезапуску...");
                    await Task.Delay(2000); // Небольшая пауза перед перезапуском
                }

            } while (restart);

            Console.WriteLine("Бот остановлен.");
        }

        private BotUI _ui;
        private bool _uiStarted = false;

        public async Task RunBotAsync()
        {
            _startupTime = DateTime.UtcNow;

            if (_ui == null)
            {
                _ui = new BotUI(
                    _client,
                    this,
                    _reconnectionService,
                    _connectionPredictor,
                    _statusNotifier
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
            }
            else
            {
                // При рестарте просто обновляем сервисы
                _ui.UpdateServices(_client, _reconnectionService, _connectionPredictor, _statusNotifier);
                _ui.AddLog("Перезапуск бота...");
            }

            _textBlocks = LoadTextFromFile(_config.TextBlocksPath);

            while (!_isDisposed && !_shouldExit)
            {
                await _restartLock.WaitAsync();
                try
                {
                    await LogStartup(" Инициализация бота... Версия 0.6.0.0 (с системой прогнозов)");

                    if (_client == null || _client.ConnectionState == ConnectionState.Disconnected)
                    {
                        _client?.Dispose();
                        _client = CreateDiscordClient();

                        // Корректно очистим старые сервисы (отпишем, shutdown, dispose)
                        CleanupServices();

                        // ПЕРЕСОЗДАЕМ СЕРВИСЫ С НОВЫМ КЛИЕНТОМ
                        _reconnectionService = new ReconnectionService(_client);
                        _connectionPredictor = new ConnectionPredictor(_reconnectionService, _config.Prediction);
                        _statusNotifier = new StatusNotifier(_client, ServerConfigs);
                        _statusNotifier.SetStartupType(_currentStartupType);

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

                        // Запускаем фоновый мониторинг
                        _ = Task.Run(BackgroundMonitoringLoop);

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
            _client.ModalSubmitted -= HandleModalSubmitted;
            _client.ButtonExecuted -= HandleButtonExecuted;
            _client.GuildScheduledEventStarted -= OnGuildScheduledEventStarted;
            _client.GuildScheduledEventCompleted -= OnGuildScheduledEventCompleted;

            // Подписываемся заново
            _client.Ready += OnReady;
            _client.Disconnected += OnDisconnected;
            _client.UserJoined += UserJoined;
            _client.MessageReceived += HandleCommandAsync;
            _client.SlashCommandExecuted += OnSlashCommandExecuted;
            _client.ModalSubmitted += HandleModalSubmitted;
            _client.ButtonExecuted += HandleButtonExecuted;
            _client.GuildScheduledEventStarted += OnGuildScheduledEventStarted;
            _client.GuildScheduledEventCompleted += OnGuildScheduledEventCompleted;

            await LogStartup($"│   События Discord настроены    │");
        }

        // Отдельные обработчики для событий
        private async Task OnGuildScheduledEventStarted(SocketGuildEvent guildEvent)
        {
            try
            {
                await GameSessionCommands.OnGuildScheduledEventStarted(guildEvent, _client);
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
            }
            catch (Exception ex)
            {
                await LogError($"Ошибка в OnGuildScheduledEventCompleted: {ex.Message}");
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
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));

                    // Анализируем и делаем прогнозы
                    var prediction = await _connectionPredictor.AnalyzeAndPredict();

                    // Проверяем, не зависло ли соединение
                    if (_client.ConnectionState == ConnectionState.Disconnected &&
                        !_shouldExit)
                    {
                        await LogStartup("⚠️ Фоновая проверка: обнаружено отключение");
                        await _reconnectionService.HandleDisconnect(new Exception("Background check"));
                    }
                }
                catch (Exception ex)
                {
                    await LogStartup($"⚠️ Ошибка мониторинга: {ex.Message}");
                }
            }
        }

        private async Task OnDisconnectDetected(Exception exception)
        {
            var reason = _reconnectionService.ConnectionInfo.LastDisconnectReason;
            await LogStartup($"Отключение: {reason}");

            await _statusNotifier.SendConnectionIssue(
                reason,
                _reconnectionService.ConnectionInfo.ReconnectAttempts + 1
            );
        }

        private async Task OnReconnectStarted(string message)
        {
            await LogStartup($"{message}");
        }

        private async Task OnReconnectCompleted(bool success)
        {
            if (success)
            {
                var info = _reconnectionService.ConnectionInfo;

                // Отправляем уведомление об успешном реконнекте
                await _statusNotifier.SendReconnectSuccess(
                    info.ReconnectAttempts,
                    info.LastDisconnectReason
                );

                // И ВАЖНОЕ СООБЩЕНИЕ "ВСЕ СИСТЕМЫ АКТИВНЫ"
                await _statusNotifier.SendAllSystemsActive(
                    $"Переподключение после: {info.LastDisconnectReason}"
                );
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
                foreach (var guild in _client.Guilds)
                {
                    if (ServerConfigs.TryGetValue(guild.Id, out var config))
                    {
                        var channel = await _client.GetChannelAsync(config.ModerateChannelID) as ITextChannel;
                        if (channel != null)
                        {
                            var embed = StatusMessageBuilder.BuildPredictionEmbed(prediction);
                            await channel.SendMessageAsync(embed: embed);
                        }
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
            return "MTMzMTYyODkxMDE1MjEyMjM4OA.GJutjl.wTDw8Tp1wI8ZNehE4TnFNkNKTFRJQ2Q0DPG4JI";
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

            // ОТПРАВЛЯЕМ В UI
            _ui?.AddLog($"БОТ ПОДКЛЮЧЕН К DISCORD: {_client.CurrentUser.Username} в {DateTime.Now:HH:mm:ss}");
            await Task.CompletedTask;
        }

        private async Task InitializeBotWithProgress()
        {
            try
            {
                // ЭТАП 1: Регистрация команд
                await LogStartup($"┌──────────── ЭТАП 1/4: РЕГИСТРАЦИЯ КОМАНД ─────────────┐");


                if (await _ui.AskYesNoQuestion(
                        "Нужно ли перерегистрировать команды?",
                        "Y - Да, N - Нет, таймаут 60 секунд",
                        60
                    ) == true)
                {
                    await _commandHandler.InitializeAsync();
                    await _commandHandler.ListSlashCommandsAsync();

                    // УВЕДОМЛЕНИЕ В UI
                    _ui?.AddLog($"├───────────────────────────────────────────────────────┤");
                    _ui?.AddLog($"│      Команды зарегистрированы                         │");
                    _ui?.AddLog($"└───────────────────────────────────────────────────────┘");
                }
                else
                {
                    _ui?.AddLog($"├───────────────────────────────────────────────────────┤");
                    _ui?.AddLog($"│      Регистрация команд пропущена                     │");
                    _ui?.AddLog($"└───────────────────────────────────────────────────────┘");
                    await _commandHandler.ListSlashCommandsAsync();
                }

                // ЭТАП 2: Активация обработчиков
                await LogStartup($"┌──────────── ЭТАП 2/4: АКТИВАЦИЯ ОБРАБОТЧИКОВ ─────────┐");
                await SetupDiscordEvents();
                await LogStartup($"└───────────────────────────────────────────────────────┘");

                // ЭТАП 3: Отправка статусов
                await LogStartup($"┌──────────── ЭТАП 3/4: ОТПРАВКА СТАТУСОВ ──────────────┐");

                var guildsList = _client.Guilds.ToList();
                for (int i = 0; i < guildsList.Count; i++)
                {
                    var guild = guildsList[i];
                    if (ServerConfigs.TryGetValue(guild.Id, out var config))
                    {
                        await _statusNotifier.SendSystemsActiveToGuild(guild, config,
                            $" Первичный запуск. Версия: 0.6.0.0");
                        await LogStartup($"│   Статус отправлен на {guild.Name,-32}│");
                    }
                    await Task.Delay(200);
                }

                await LogStartup($"└───────────────────────────────────────────────────────┘");

                // ЭТАП 4: ФИНАЛ
                _fullReadyTime = DateTime.UtcNow;
                var initTime = (_fullReadyTime - _readyTime).TotalSeconds;
                _initializationCompleted = true;

                _ui?.EnableInput();

                // УВЕДОМЛЕНИЕ В UI
                _ui?.ShowSystemReady(
                    _client.CurrentUser.Username,
                    _client.Guilds.Count,
                    initTime
                );

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
            await _reconnectionService.HandleDisconnect(exception);
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _shouldExit = true;

            try
            {
                // Останавливаем реконнект-сервис корректно и затем очищаем
                try { _reconnectionService?.Shutdown(); } catch { }
                CleanupServices();

                // Остановим UI корректно
                try
                {
                    _ui?.Dispose();
                    _ui = null;
                    _uiStarted = false;
                }
                catch { }

                // Останавливаем клиента
                try
                {
                    if (_client != null)
                    {
                        try { _client.Ready -= OnReady; } catch { }
                        try { _client.Disconnected -= OnDisconnected; } catch { }
                        try { _client.UserJoined -= UserJoined; } catch { }
                        try { _client.MessageReceived -= HandleCommandAsync; } catch { }
                        try { _client.SlashCommandExecuted -= OnSlashCommandExecuted; } catch { }
                        try { _client.ModalSubmitted -= HandleModalSubmitted; } catch { }
                        try { _client.ButtonExecuted -= HandleButtonExecuted; } catch { }
                        try { _client.GuildScheduledEventStarted -= OnGuildScheduledEventStarted; } catch { }
                        try { _client.GuildScheduledEventCompleted -= OnGuildScheduledEventCompleted; } catch { }

                        try { await _client.StopAsync(); } catch { }
                        try { _client.Dispose(); } catch { }
                        _client = null;
                    }
                }
                catch { }

                // Очистка модулей (таймеры/статические данные)
                try { QueueModule.ShutdownQueue(); } catch { }

                // Освобождение лог-семафора
                try { _logSemaphore?.Dispose(); } catch { }

                // Освобождение локального семафора рестарта
                try { _restartLock?.Dispose(); } catch { }
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
            // Разбираем ID сессии из CustomId модального окна
            var parts = modal.Data.CustomId.Split(':');
            if (parts.Length >= 2 && parts[0] == "edit_modal")
            {
                await new GameSessionCommands(_client).HandleEditModal(modal);
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
                await LogError($"Ошибка обработки кнопки: {ex.Message}");
                _ = component.RespondAsync("Ошибка обработки", ephemeral: true)
                    .ConfigureAwait(false);
            }
        }

        private async Task ProcessButtonAsync(SocketMessageComponent component)
        {
            var parts = component.Data.CustomId.Split(':');
            var buttonType = parts.Length > 0 ? parts[0] : component.Data.CustomId;

            switch (buttonType)
            {
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

        private async Task HandleCommandAsync(SocketMessage arg)
        {
            if (arg is not SocketUserMessage message || message.Author.IsBot) return;

            var context = new SocketCommandContext(_client, message);
            var user = message.Author as SocketGuildUser;

            var (fludChannelId, rollChannelId, generalRGChannelID, responseMessage, emoji, lineMessages, emoteKappa, emoteAga) = GetResponseData(message);

            // Определяем, является ли канал голосовым
            bool isVoiceChannel = message.Channel is SocketVoiceChannel;

            // Определяем разрешённые текстовые каналы для команд
            var allowedTextChannels = new ulong[] { fludChannelId, generalRGChannelID };
            bool isAllowedTextChannel = allowedTextChannels.Contains(message.Channel.Id);

            // Приветствие
            var lowerContent = message.Content.ToLower();
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
            if (message.Content.ToLower() == "!команды")
            {
                var commandsList = new StringBuilder();
                commandsList.AppendLine("Доступные команды:");
                commandsList.AppendLine("\n**--Для (двух) текстовых чатов--**");
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

            // Обработка сообщений, начинающихся с "!"
            if (message.Content.StartsWith("!"))
            {
                var key = message.Content.Split(' ')[0].ToLower();

                // Разделение команд по каналам
                if (isVoiceChannel)
                {
                    var voiceCommands = new Dictionary<string, string>
                    {
                        {"!бегу", "бегу с сыном"},
                        {"!гусь", "паста гуся"},
                        {"!гусь-гидра", "паста гидры гуся"},
                        {"!гусь-связь", "паста с гусём-связистом"},
                        {"!начинается", "AFK"},
                        {"!перекур", "перерыв"},
                        {"!подсказка", "Чят, пляшем!"},
                        {"!страх", "атата"},
                        {"!убери", "ненавижу модеров"}
                    };

                    if (voiceCommands.ContainsKey(key) && _textBlocks.ContainsKey(key))
                    {
                        await message.Channel.SendMessageAsync(_textBlocks[key]);
                        return;
                    }
                }
                else if (isAllowedTextChannel)
                {
                    var textCommands = new Dictionary<string, string>
                    {
                        {"!правила", "правила сервера"},
                        {"!ссылки", "полезные ссылки"},
                        {"!запись", "документ для записи игр"}
                    };

                    if (textCommands.ContainsKey(key) && _textBlocks.ContainsKey(key))
                    {
                        await message.Channel.SendMessageAsync(_textBlocks[key]);
                        return;
                    }
                }

                await message.Channel.SendMessageAsync("Неизвестная команда или неправильный канал для этой команды.");
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

            if (user.Username == "perekrestok_mirov")
            {
                if (message.Content.ToLower().Contains("бот, спокойной ночи"))
                {
                    await message.Channel.SendMessageAsync("Отключение всех систем...");
                    await LogStartup($"Бот отключен пользователем {message.Author.Username} в {DateTime.Now}.");
                    _shouldExit = true;
                    await _client.StopAsync();
                    Environment.Exit(0);
                }

                if (message.Content.ToLower().Contains("бот, перезагрузка"))
                {
                    await message.Channel.SendMessageAsync("Бот будет перезагружен. Пожалуйста, подождите... Примерное время ожидания от 10 секунд до 3 минут.");
                    await LogStartup($"Инициализация перезагрузки пользователем {message.Author.Username} в {DateTime.Now}.");

                    var scriptPath = "C:/Favorites/Bot Discord/RPBot/RPBot/restart_bot.ps1";

                    var processStartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"",
                        RedirectStandardOutput = false,
                        RedirectStandardError = false,
                        UseShellExecute = true,
                        CreateNoWindow = false
                    };

                    try
                    {
                        using (var process = new Process { StartInfo = processStartInfo })
                        {
                            process.Start();
                            await process.WaitForExitAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        await LogStartup("Ошибка при запуске скрипта: " + ex.Message);
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
            if (channel == null || !ServerConfigs.TryGetValue(channel.Guild.Id, out var config))
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
            string counterFilePath = @"C:\Favorites\Desktop\НРИ\Discord_BR\bug_report_counter.txt";

            if (File.Exists(counterFilePath))
            {
                return int.Parse(File.ReadAllText(counterFilePath));
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
                case "bug_report":
                    await Bug_ReportCommand(command);
                    break;
                case "start":
                    await StartGameSession(command);
                    break;
                case "close_chat":
                    await CloseChatCommand(command);
                    break;
                case "open_chat":
                    await OpenChatCommand(command);
                    break;
                default:
                    await command.RespondAsync("Команда не распознана.");
                    break;
            }
        }

        private async Task StopQueue(SocketSlashCommand command)
        {
            var queueModule = _services.GetService<QueueModule>();
            await queueModule.StopQueue(command);
            await LogInfo("Очередь остановлена.");
        }

        private async Task QueueCommand(SocketSlashCommand command)
        {
            var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
            if (int.TryParse(inputOption?.Value?.ToString(), out int participantsCount))
            {
                var queueModule = _services.GetService<QueueModule>();
                await queueModule.QueueCommand(command, participantsCount);
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
            var input = inputOption?.Value?.ToString();

            var queueModule = new QueueModule();
            await queueModule.QIn_RollDice(command, input);
        }

        private async Task CloseChatCommand(SocketSlashCommand command)
        {
            var moderationModule = _services.GetService<ModerationCommands>();
            await moderationModule.CloseChat(command);
            await LogInfo("Чат или ветка закрыты.");
        }

        private async Task OpenChatCommand(SocketSlashCommand command)
        {
            var moderationModule = _services.GetService<ModerationCommands>();
            await moderationModule.OpenChat(command);
            await LogInfo("Чат открыт и перемещён в указанную категорию.");
        }

        private async Task ClearMessage(SocketSlashCommand command)
        {
            var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
            if (int.TryParse(inputOption?.Value?.ToString(), out int messagesToDelete))
            {
                var moderationModule = _services.GetService<ModerationCommands>();
                await moderationModule.ClearMessages(command, messagesToDelete);
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
            var input = inputOption?.Value?.ToString();

            var diceModule = _services.GetService<RollDiceCommands>();
            await diceModule.RollDice(command, input);
        }

        private async Task Roll20Command(SocketSlashCommand command)
        {
            var diceModule = _services.GetService<RollDiceCommands>();
            await diceModule.Roll20(command);
        }

        private async Task ServerInfoCommand(SocketSlashCommand command)
        {
            var infoModule = _services.GetService<InfoCommands>();
            await infoModule.ServerInfo(command);
            await LogInfo("Выведена информация о сервере.");
        }

        private async Task HelpCommand(SocketSlashCommand command)
        {
            var infoModule = _services.GetService<InfoCommands>();
            await infoModule.Help(command);
            await LogInfo("Выведена подсказка о командах.");
        }

        private async Task Help_RollCommand(SocketSlashCommand command)
        {
            var infoModule = _services.GetService<InfoCommands>();
            await infoModule.Help_R(command);
            await LogInfo("Выведена подсказка о командах для бросков кубов.");
        }

        private async Task Help_GameSessionCommand(SocketSlashCommand command)
        {
            var infoModule = _services.GetService<InfoCommands>();
            await infoModule.Help_GS(command);
            await LogInfo("Выведена подсказка о командах для статистики.");
        }

        private async Task Bug_ReportCommand(SocketSlashCommand command)
        {
            var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
            var input = inputOption?.Value?.ToString();

            var infoModule = _services.GetService<InfoCommands>();
            await infoModule.Bug_Report(command, input);
            await LogInfo("Использовано уведомление администратора о баге.");
        }

        private async Task StartGameSession(SocketSlashCommand command)
        {
            var gameNameOption = command.Data.Options.FirstOrDefault(o => o.Name == "game_name");
            var gameName = gameNameOption?.Value?.ToString();

            var masterOption = command.Data.Options.FirstOrDefault(o => o.Name == "master");
            var masterUser = masterOption?.Value as SocketUser;

            var gameCommentOption = command.Data.Options.FirstOrDefault(o => o.Name == "comment");
            var gameComment = gameCommentOption?.Value?.ToString();

            var gameSessionModule = _services.GetService<GameSessionCommands>();
            await gameSessionModule.StartGameSession(command, gameName, masterUser, gameComment);
        }

        private static readonly SemaphoreSlim _logSemaphore = new SemaphoreSlim(1, 1);

        private async Task LogStartup(string message)
        {
            string logDir = @"C:\Favorites\Desktop\НРИ\Discord_BR";
            string path = Path.Combine(logDir, "StartupLog.txt");

            await _logSemaphore.WaitAsync();
            try
            {
                // Ротация логов
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

        private async Task LogError(string errorMessage)
        {
            string logDir = @"C:\Favorites\Desktop\НРИ\Discord_BR";
            string path = Path.Combine(logDir, "ErrorLog.txt");

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
            string logDir = @"C:\Favorites\Desktop\НРИ\Discord_BR";
            string path = Path.Combine(logDir, "InfoLog.txt");

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
                if (!ServerConfigs.TryGetValue(user.Guild.Id, out var config) || config.DefaultRoleID == 0)
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
                                            "На сервере помимо команд через `/` в соответствующих чатах, также действует несколько команд через `!`:\n" +
                                            "1. В двух чатах (**флудилка** и **общий-ролевой-чат**) они с важной информацией:\n" +
                                            "   • `!правила` — здесь описан свод правил, который действует на данном сервере;\n" +
                                            "   • `!ссылки` — здесь представлены ссылки на все социальные сети, где можно найти \"Костёр на распутье\";\n" +
                                            "   • `!запись` — здесь находится ссылка на документ, в котором вся ||(или почти вся)|| информация о том, как можно записывать игры, начиная от установки и заканчивая настройкой. К тому же там описаны базовые правила для чистоты записи.\n" +
                                            "2. А также есть `!команды` — здесь указаны все пасты, которые можно использовать в голосовых каналах.\n\n" +
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

        [Command("roll")]
        public async Task RollDice(SocketSlashCommand command, string input)
        {
            await command.DeferAsync();
            var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;
            var channelId = command.Channel.Id;

            // Получаем ID канала статистики из конфига
            var statsChannelId = Program.ServerConfigs.TryGetValue(guildId.Value, out var config)
                ? config.StatsChannelID
                : 0;

            // Проверяем, сделан ли бросок в канале статистики
            bool isStatsChannel = channelId == statsChannelId;

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

            // Обработка бросков d20 без модификатора
            if (max == 20 && modifier == 0)
            {
                // Если бросок в канале статистики и есть активные сессии - записываем результаты
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
                                        RollValue = result
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

                if (count == 1)
                {
                    var result = results[0];
                    var filePath = Path.Combine("Numbers", $"{result}.png");
                    Color embedColor = GetGradientColor(result, 1, max);

                    Console.WriteLine($"Результат броска: {result}");

                    if (File.Exists(filePath))
                    {
                        var embed = new EmbedBuilder()
                            .WithImageUrl($"attachment://{Path.GetFileName(filePath)}")
                            .WithColor(embedColor)
                            .Build();
                        await command.FollowupWithFileAsync(filePath, embed: embed);
                    }
                    else
                    {
                        await command.FollowupAsync($"Выпало: **{result}** (изображение не найдено)");
                    }
                    return;
                }
                else
                {
                    var embeds = new List<Embed>();
                    var files = new List<FileAttachment>();

                    foreach (var result in results)
                    {
                        Console.WriteLine($"Результат броска: {result}");
                        var filePath = Path.Combine("Numbers", $"{result}.png");
                        if (File.Exists(filePath))
                        {
                            files.Add(new FileAttachment(filePath, Path.GetFileName(filePath)));
                            embeds.Add(new EmbedBuilder()
                                .WithImageUrl($"attachment://{Path.GetFileName(filePath)}")
                                .WithColor(GetGradientColor(result, 1, max))
                                .Build());
                        }
                        else
                        {
                            Console.WriteLine($"Ошибка: Не найдено изображение для значения \"{result}\"!");
                        }
                    }

                    if (files.Count > 0)
                    {
                        var combinedMessage = files.Count switch
                        {
                            2 => "Результаты броска с помехой/преимуществом:",
                            _ => $"Результаты {files.Count} бросков:"
                        };

                        await command.FollowupWithFilesAsync(
                            attachments: files,
                            text: combinedMessage,
                            embeds: embeds.ToArray());
                    }
                    else
                    {
                        var resultsText = string.Join(", ", results);
                        await command.FollowupAsync($"Результаты бросков: {resultsText}");
                    }
                    return;
                }
            }

            // Обработка всех остальных бросков (не d20 или с модификатором)
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
                                    RollValue = result
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
            var filePath = Path.Combine("Numbers", $"{result}.png");
            if (File.Exists(filePath))
            {
                var embed = new EmbedBuilder()
                    .WithImageUrl($"attachment://{Path.GetFileName(filePath)}")
                    .WithColor(GetGradientColor(result, 1, 20))
                    .Build();
                await command.FollowupWithFileAsync(filePath, embed: embed);
            }
            else
            {
                await command.FollowupAsync($"Выпало: **{result}** (изображение не найдено)");
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
                .WithDescription("Основные команды бота для управления сервером и игровыми процессами")
                .AddField("📚 Основные команды",
                    "> `/help` - Показывает это сообщение\n" +
                    "> `/help_r` - Помощь по системе бросков кубиков\n" +
                    "> `/help_gs` - Помощь по управлению игровыми сессиями\n" +
                    "> `/bug_report [сообщение]` - Отправка отчета об ошибке или предложения")
                .AddField("🛠 Модерация",
                    "> `/open_chat [категория]` - Открывает доступ писать и перемещает в указанную категорию *(только для мастеров)*\n" +
                    "> `/close_chat [причина]` - Архивирует чат и закрывает доступ писать *(только для мастеров)*\n" +
                    "> `/clr X` - Удаляет X сообщений (1-100) *(для модераторов)*")
                .AddField("📊 Информация",
                    "> `/serverinfo` - Показывает информацию о сервере\n" +
                    "> *(команда в разработке)*")
                .AddField("⚠️ Ограничения доступа",
                    "• Команды модерации доступны только уполномоченным пользователям\n" +
                    "• Некоторые команды работают только в определенных каналах\n" +
                    "• Бот находится в активной разработке")
                .WithFooter("*Некоторые пасхалки скрыты в коде. При проблемах используйте /bug_report*")
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
                int onlineMembers = server.Users.Count(u => u.Status != UserStatus.Offline);
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

        [Command("help_gs")]
        public async Task Help_GS(SocketSlashCommand command)
        {
            var helpMessage = new EmbedBuilder()
                .WithTitle("📚 Помощь по управлению игровыми сессиями")
                .WithColor(Color.Blue)
                .WithDescription("Система позволяет отслеживать время игровых сессий, делать паузы и собирать статистику бросков.")
                .AddField("🔹 Основные команды",
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

            string directoryPath = @"E:\НРИ\RoleplayerBotDiscord\Logs";
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
            string counterFilePath = @"E:\НРИ\RoleplayerBotDiscord\Logs\bug_report_counter.txt";

            if (!File.Exists(counterFilePath))
            {
                File.WriteAllText(counterFilePath, "1");
            }
            else
            {
                int currentCount = int.Parse(File.ReadAllText(counterFilePath));
                currentCount++;
                File.WriteAllText(counterFilePath, currentCount.ToString());
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
            Console.WriteLine($"Успех: Очередь создана с заданным числом ({count}) участников! Таймер запущен.");
        }

        [Command("q")]
        public async Task QIn_RollDice(SocketSlashCommand command, string input)
        {
            if (!isQueueActive)
            {
                Console.WriteLine($"Предупреждение: Очередь не активна.");
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
                Console.WriteLine($"Ошибка: Введены некорректные данные.");
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

            Console.WriteLine($"Успех: Совершён бросок пользователем {user.DisplayName}. Его результат - {result}. Таймер обновлён.");

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
                    Console.WriteLine($"Ошибка (вывод результатов): При попытке удалить сообщения произошла ошибка. Причина: {ex.Message}");
                }
            }

            var embed = new EmbedBuilder()
                .WithTitle("Последовательность ходов")
                .WithColor(Color.Green);

            if (userRolls == null || userRolls.Count == 0)
            {
                Console.WriteLine("userRolls is null or empty");
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

            Console.WriteLine($"Успех: Произведён вывод результатов. Сообщения удалены. Таймер остановлён.");
            embed.AddField("Результаты", resultString.ToString(), false);
            if (embed != null)
            {
                await channel.SendMessageAsync(embed: embed.Build());
            }
            else
            {
                Console.WriteLine("Ошибка: embed не был создан.");
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
                        Console.WriteLine($"Ошибка (очистка очереди): При попытке удалить сообщения произошла ошибка. Причина: {ex.Message}");
                    }
                }
            });
            Console.WriteLine($"Предупреждение: Таймер истёк. Сообщения и очередь удалены.");
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
                    Console.WriteLine($"Ошибка: У пользователя {guildUser.DisplayName} недостаточно прав для выполнения команды");
                    var userRoles = guildUser.Roles.Select(r => r.Name).ToList();
                    Console.WriteLine($"Роли пользователя: {string.Join(", ", userRoles)}");
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
                Console.WriteLine($"ShutdownQueue error: {ex.Message}");
            }
        }
    }

    public class RollStatistic
    {
        public string PlayerName { get; set; }
        public int RollValue { get; set; }
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
    }

    public class GameSessionCommands : ModuleBase<SocketCommandContext>
    {
        private readonly DiscordSocketClient _client;
        public static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, GameSession>> _sessions = new();
        private static readonly SemaphoreSlim _sessionSemaphore = new(1, 1);

        public GameSessionCommands(DiscordSocketClient client) => _client = client;

        private async void Log(string message)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
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
                Log($"Попытка создать сессию для гильдии {guildId}, игра: {gameName}");

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
                    Log($"Успешно создана новая сессия: ID {newSession.SessionId}, игра: {gameName}");
                }
                else
                {
                    Log($"Ошибка при создании сессии для игры {gameName}");
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

                var embed = new EmbedBuilder()
                    .WithTitle($"Сессия: {session.GameName}")
                    .WithDescription($"Мастер: {session.MasterName}\n" +
                                   $"Начало: {session.StartTime:dd.MM.yyyy HH:mm}\n" +
                                   $"Статус: {(session.IsPaused ? $"⏸ На паузе{pauseTimeInfo}" : "▶ В процессе")}\n" +
                                   $"Сбор бросков: {(session.TrackRolls ? "✅ Включен" : "❌ Выключен")}\n" +
                                   $"{(string.IsNullOrEmpty(session.GameComment) ? "" : $"Комментарий: {session.GameComment}")}")
                    .WithColor(session.IsPaused ? Color.Orange : Color.Green)
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
                    .WithTitle($"Сессия: {session.GameName}")
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
                .WithTitle($"Сессия: {gameName}")
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
                    await SendTemporaryEphemeralResponse(component, "Активные сессии не найдены.");
                    return;
                }

                if (!guildSessions.TryGetValue(sessionId, out var session))
                {
                    await SendTemporaryEphemeralResponse(component, "Сессия не найдена.");
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

                var newName = modal.Data.Components.First(x => x.CustomId == "game_name").Value;
                var newMaster = modal.Data.Components.First(x => x.CustomId == "game_master").Value;
                var newComment = modal.Data.Components.First(x => x.CustomId == "game_comment").Value;

                var changes = new List<string>();
                if (session.GameName != newName) changes.Add($"Название: {session.GameName} → {newName}");
                if (session.MasterName != newMaster) changes.Add($"Мастер: {session.MasterName} → {newMaster}");
                if (session.GameComment != newComment) changes.Add($"Комментарий: {session.GameComment} → {newComment}");

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
            Log($"Попытка подтверждения остановки сессии {session.SessionId}. Текущий счётчик семафора: {_sessionSemaphore.CurrentCount}");

            /*// Добавляем timeout для семафора
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _sessionSemaphore.WaitAsync(cts.Token);
                Log($"Семафор захвачен для сессии {session.SessionId}. Текущий счётчик: {_sessionSemaphore.CurrentCount}");
            }
            catch (OperationCanceledException)
            {
                Log($"Таймаут при ожидании семафора для сессии {session.SessionId}! Возможен дедлок");
                await SendTemporaryEphemeralResponse(component, "Ошибка: система занята. Попробуйте позже.");
                return;
            }
            catch (Exception ex)
            {
                Log($"Критическая ошибка при захвате семафора: {ex.Message}");
                await SendTemporaryEphemeralResponse(component, "Критическая ошибка системы.");
                return;
            }*/

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
                Log($"Время окончания установлено: {session.EndTime}");

                try
                {
                    await component.Message.DeleteAsync();
                    Log($"Сообщение управления удалено");
                }
                catch (Exception ex)
                {
                    Log($"Ошибка удаления сообщения: {ex.Message}");
                }

                await SendSessionStats(session, component.Channel);

                /*if (session.Rolls.Count == 0)
                {
                    Log($"Сессия без бросков - удаление");
                    RemoveSession(session);
                }
                else
                {
                    Log($"Сессия содержит броски - отложенное удаление");
                }*/

                if (session.EventId.HasValue)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var guild = _client.GetGuild(session.GuildId);
                            var guildEvent = await guild.GetEventAsync(session.EventId.Value);
                            if (guildEvent?.Status == GuildScheduledEventStatus.Active)
                            {
                                await guildEvent.DeleteAsync();
                                Log($"Связанное событие {session.EventId} завершено");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Ошибка завершения события: {ex.Message}");
                        }
                    });
                }
            }
            finally
            {
                //_sessionSemaphore.Release();
                //Log($"Семафор освобожден. Текущий счётчик: {_sessionSemaphore.CurrentCount}");
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

                            if (session.ControlMessageId != 0)
                            {
                                try
                                {
                                    var channelId = Program.ServerConfigs.TryGetValue(guildId, out var config)
                                        ? config.RecordChannelID
                                        : 0;

                                    if (channelId != 0 && client.GetChannel(channelId) is ITextChannel controlChannel)
                                    {
                                        await controlChannel.DeleteMessageAsync(session.ControlMessageId);
                                        commands.Log($"Сообщение управления {session.ControlMessageId} удалено");
                                    }
                                    else
                                    {
                                        commands.Log($"Не удалось найти канал для удаления сообщения управления");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    commands.Log($"Ошибка при удалении сообщения управления: {ex.Message}");
                                }
                            }

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
            message.AppendLine($"# Игра **{session.GameName}** завершена");
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

                            var buttonsMsg = await statsChannel.SendMessageAsync(
                                $"Статистика для игры `{session.GameName}`. Какую вывести?",
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
                    Log($"Активные сессии для гильдии {guildId} не найдены");
                    await component.RespondAsync("Активные сессии не найдены.", ephemeral: true);
                    return;
                }

                var session = guildSessions.Values.FirstOrDefault(s => s.StatsMessageId == component.Message.Id);
                if (session == null)
                {
                    Log($"Сессия для сообщения статистики {component.Message.Id} не найдена");
                    await component.RespondAsync("Сессия не найдена.", ephemeral: true);
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

            var rolls = session.Rolls
                .GroupBy(r => r.RollValue)
                .Select(g => new { Value = g.Key, Count = g.Count() })
                .OrderBy(g => g.Value);

            var averageValue = session.Rolls.Any() ? session.Rolls.Average(r => r.RollValue) : 0;

            var message = new StringBuilder("**Общая статистика бросков:**\n");
            foreach (var roll in rolls)
            {
                message.AppendLine($"- {roll.Value}: {roll.Count} раз");
            }
            message.AppendLine($"**Среднее значение:** {averageValue:F2}"); // Форматируем до 2 знаков после запятой

            await component.Channel.SendMessageAsync(message.ToString());
            await component.Message.DeleteAsync();
        }

        private async Task ShowDetailedStats(SocketMessageComponent component, GameSession session)
        {
            Log($"Формирование детальной статистики для сессии {session.SessionId}");

            var players = session.Rolls
                .GroupBy(r => r.PlayerName)
                .Select(g => new {
                    Player = g.Key,
                    Rolls = g.GroupBy(r => r.RollValue)
                        .Select(r => new { Value = r.Key, Count = r.Count() })
                        .ToList(),
                    Average = g.Average(r => r.RollValue)
                });

            var message = new StringBuilder("**Подробная статистика бросков:**\n");
            foreach (var player in players)
            {
                message.AppendLine($"*{player.Player}:*");
                foreach (var roll in player.Rolls.OrderBy(r => r.Value))
                {
                    message.AppendLine($"- {roll.Value}: {roll.Count} раз");
                }
                message.AppendLine($"**Среднее значение:** {player.Average:F2}");
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

        public ModerationCommands(DiscordSocketClient client)
        {
            _client = client;
        }

        [Command("close_chat")]
        public async Task CloseChat(SocketSlashCommand command)
        {
            // Отложим ответ, чтобы Discord не считал команду "зависшей"
            await command.DeferAsync();

            Console.WriteLine($"[{DateTime.UtcNow}] Команда '/close_chat' вызвана пользователем {command.User.Username} ({command.User.Id}).");

            // Проверяем, что команду выполняет "perekrestok_mirov" или "domen_"
            var allowedUsers = new[] { "perekrestok_mirov", "domen_" };
            var user = command.User as SocketGuildUser;

            if (user == null || !allowedUsers.Contains(user.Username))
            {
                Console.WriteLine($"[{DateTime.UtcNow}] Отказ в доступе: пользователь {command.User.Username} не имеет прав на выполнение команды.");
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
                Console.WriteLine($"[{DateTime.UtcNow}] Обработка ветки {threadChannel.Name} ({threadChannel.Id}).");

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
                            Console.WriteLine($"[{DateTime.UtcNow}] Запрещена отправка сообщений для пользователя {guildUser.Username}.");
                        }
                        else
                        {
                            // Если переопределения нет, создаём новое с запретом на отправку сообщений
                            await textChannel.AddPermissionOverwriteAsync(guildUser, new OverwritePermissions(sendMessages: PermValue.Deny));
                            Console.WriteLine($"[{DateTime.UtcNow}] Запрещена отправка сообщений для пользователя {guildUser.Username}.");
                        }
                    }
                    await Task.Delay(100);
                }

                Console.WriteLine($"[{DateTime.UtcNow}] Чат {textChannel.Name} перемещён в архив и закрыт. Причина: {reason}");
                await command.FollowupAsync($"Чат {textChannel.Mention} был перемещён в архив и закрыт. Причина: {reason}");
            }
            else
            {
                Console.WriteLine($"[{DateTime.UtcNow}] Ошибка: команда выполнена в неподдерживаемом типе канала.");
                await command.FollowupAsync("Эта команда может быть выполнена только в текстовом канале или ветке на форуме.", ephemeral: true);
            }
        }

        [Command("open_chat")]
        public async Task OpenChat(SocketSlashCommand command)
        {
            // Отложим ответ, чтобы Discord не считал команду "зависшей"
            await command.DeferAsync();

            Console.WriteLine($"[{DateTime.UtcNow}] Команда '/open_chat' вызвана пользователем {command.User.Username} ({command.User.Id}).");

            // Проверяем, что команду выполняет "perekrestok_mirov" или "domen_"
            var allowedUsers = new[] { "perekrestok_mirov", "domen_" };
            var user = command.User as SocketGuildUser;

            if (user == null || !allowedUsers.Contains(user.Username))
            {
                Console.WriteLine($"[{DateTime.UtcNow}] Отказ в доступе: пользователь {command.User.Username} не имеет прав на выполнение команды.");
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
                Console.WriteLine($"[{DateTime.UtcNow}] Ошибка: команда выполнена в неподдерживаемом типе канала.");
                await command.FollowupAsync("Эта команда может быть выполнена только в текстовом канале.", ephemeral: true);
                return;
            }

            // Получаем название категории из аргумента команды
            var categoryName = command.Data.Options.FirstOrDefault(opt => opt.Name == "category")?.Value?.ToString();

            if (string.IsNullOrEmpty(categoryName))
            {
                Console.WriteLine($"[{DateTime.UtcNow}] Ошибка: не указана категория для перемещения.");
                await command.FollowupAsync("Не указана категория для перемещения.", ephemeral: true);
                return;
            }

            // Ищем категорию по имени
            var targetCategory = guild.CategoryChannels.FirstOrDefault(cat => cat.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));

            if (targetCategory == null)
            {
                Console.WriteLine($"[{DateTime.UtcNow}] Ошибка: категория '{categoryName}' не найдена.");
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
                        Console.WriteLine($"[{DateTime.UtcNow}] Возвращена возможность отправки сообщений для пользователя {guildUser.Username}.");
                    }
                    else
                    {
                        // Если переопределения нет, создаём новое с разрешением на отправку сообщений
                        await textChannel.AddPermissionOverwriteAsync(guildUser, new OverwritePermissions(sendMessages: PermValue.Allow));
                        Console.WriteLine($"[{DateTime.UtcNow}] Возвращена возможность отправки сообщений для пользователя {guildUser.Username}.");
                    }
                }
                await Task.Delay(100);
            }

            // Перемещаем канал в указанную категорию
            Console.WriteLine($"[{DateTime.UtcNow}] Перемещение канала {textChannel.Name} в категорию '{targetCategory.Name}'.");
            await textChannel.ModifyAsync(prop =>
            {
                prop.CategoryId = targetCategory.Id;
            });

            Console.WriteLine($"[{DateTime.UtcNow}] Чат {textChannel.Name} открыт и перемещён в категорию '{targetCategory.Name}'.");
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
                Console.WriteLine($"Ошибка: У пользователя {user.DisplayName} недостаточно прав для выполнения команды");
                return;
            }

            if (count < 1 || count > 100)
            {
                await command.RespondAsync("Пожалуйста, укажите число от 1 до 100.", ephemeral: true);
                Console.WriteLine($"Ошибка: Пользователь {user.DisplayName} ввёл некорректное число сообщений - {count}");
                return;
            }

            var messagesToDeleteList = await command.Channel.GetMessagesAsync(count).FlattenAsync();

            if (command.Channel is ITextChannel textChannel)
            {
                await textChannel.DeleteMessagesAsync(messagesToDeleteList);

                await command.RespondAsync(GetMessageCountString(count), ephemeral: true);
                Console.WriteLine(GetMessageCountString(count));
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

/*
 * await _client.LoginAsync(TokenType.Bot, "MTMzMTYyODkxMDE1MjEyMjM4OA.GzWsZE.WJgvlfflP5wkFxFGqce6tK3mDYOygSvc0q2TBk"); * 
 */