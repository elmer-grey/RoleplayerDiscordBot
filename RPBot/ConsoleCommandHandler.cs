using Discord;
using Discord.WebSocket;
using DiscordBot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RPBot
{
    public class ConsoleCommandHandler
    {
        private readonly DiscordSocketClient _client;
        private readonly IBotController _botController;
        private readonly ReconnectionService _reconnectionService;
        private readonly ConnectionPredictor _connectionPredictor;
        private readonly StatusNotifier _statusNotifier;

        private bool _isListening = false;
        private bool _consoleInputEnabled = false;
        private bool _commandsEnabled = false;
        private bool _specialInputActive = false;
        private readonly object _lock = new object();

        private readonly ManualResetEventSlim _inputAvailableEvent = new ManualResetEventSlim(false);
        private Thread _inputThread;
        private string _pendingInput = null;

        public event Action<string> OnSpecialInput;

        public ConsoleCommandHandler(
            DiscordSocketClient client,
            IBotController botController,
            ReconnectionService reconnectionService,
            ConnectionPredictor connectionPredictor,
            StatusNotifier statusNotifier)
        {
            _client = client;
            _botController = botController;
            _reconnectionService = reconnectionService;
            _connectionPredictor = connectionPredictor;
            _statusNotifier = statusNotifier;
        }
        
        public void EnableCommands()
        {
            lock (_lock)
            {
                _commandsEnabled = true;
                _consoleInputEnabled = true;
                _specialInputActive = false;
            }
            _inputAvailableEvent.Set();
            Console.WriteLine("[CONSOLE] Команды консоли активированы");
        }

        public void EnableSpecialInput()
        {
            lock (_lock)
            {
                _consoleInputEnabled = true;
                _commandsEnabled = false;
                _specialInputActive = true;
            }
            _inputAvailableEvent.Set();
            Console.WriteLine("Специальный режим ввода активирован");
        }

        public void DisableInput()
        {
            lock (_lock)
            {
                _consoleInputEnabled = false;
                _commandsEnabled = false;
                _specialInputActive = false;
            }
            _inputAvailableEvent.Reset();
            Console.WriteLine("Ввод заблокирован");
        }

        public async Task StartListening()
        {
            if (_isListening) return;
            _isListening = true;

            Console.WriteLine("[CONSOLE] Ожидание полной инициализации бота...");
            Console.WriteLine("[CONSOLE] Команды будут доступны после статуса 'ВСЕ СИСТЕМЫ АКТИВНЫ'");

            _inputThread = new Thread(ProcessInputLoop)
            {
                IsBackground = true,
                Name = "ConsoleInputThread"
            };
            _inputThread.Start();

            await Task.CompletedTask;
        }

        /*var command = input.ToLower().Trim();
        Task.Run(async () => await ProcessCommand(command));*/
        private bool _waitingForInput = false;

        private void ProcessInputLoop()
        {
            while (!_botController.ShouldExit)
            {
                try
                {
                    _inputAvailableEvent.Wait(100);

                    bool commandsEnabled;
                    bool specialActive;
                    bool inputEnabled;

                    lock (_lock)
                    {
                        inputEnabled = _consoleInputEnabled;
                        commandsEnabled = _commandsEnabled;
                        specialActive = _specialInputActive;
                    }

                    if (!inputEnabled)
                    {
                        _waitingForInput = false;
                        Thread.Sleep(100);
                        continue;
                    }

                    // Если не ждем ввод и есть команды, показываем приглашение
                    if (!_waitingForInput && commandsEnabled && !specialActive)
                    {
                        Console.Write("> ");
                        _waitingForInput = true;
                    }

                    if (!Console.KeyAvailable)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    string input = Console.ReadLine();

                    // Сбрасываем флаг, ждем следующего приглашения
                    _waitingForInput = false;

                    if (string.IsNullOrEmpty(input))
                    {
                        continue;  // Enter просто переводит строку
                    }

                    if (specialActive)
                    {
                        OnSpecialInput?.Invoke(input);
                    }
                    else if (commandsEnabled)
                    {
                        // Обычная команда
                        var command = input.ToLower().Trim();
                        Task.Run(async () => await ProcessCommand(command));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка: {ex.Message}");
                    Thread.Sleep(1000);
                }
            }
        }

        private async Task ProcessCommand(string command)
        {
            try
            {
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return;

                var cmd = parts[0].ToLower();
                var args = parts.Skip(1).ToArray();

                switch (cmd)
                {
                    case "exit":
                    case "stop":
                        Console.WriteLine("[CONSOLE] Остановка бота...");
                        await _botController.StopAsync();
                        break;

                    case "restart":
                    case "reboot":
                        Console.WriteLine("[CONSOLE] Перезапуск бота...");
                        await _botController.RestartAsync();
                        break;

                    case "status":
                        ShowDetailedStatus();
                        break;

                    case "announce":
                    case "systems":
                        Console.WriteLine("[CONSOLE] Отправка статуса в Discord...");
                        try
                        {
                            var ok = await _statusNotifier.SendAllSystemsActive("📢 Ручная проверка систем");
                            if (ok) Console.WriteLine("[CONSOLE] Статус успешно отправлен во все каналы.");
                            else Console.WriteLine("[CONSOLE] Статус отправлен с ошибками. Смотрите логи.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CONSOLE] Ошибка при отправке статусов: {ex.Message}");
                        }
                        break;

                    case "test":
                        Console.WriteLine("[CONSOLE] Тестовое сообщение...");
                        try { await _statusNotifier.SendAllSystemsActive("🧪 Тестовое уведомление"); }
                        catch (Exception ex) { Console.WriteLine($"[CONSOLE] Ошибка тестового уведомления: {ex.Message}"); }
                        break;

                    case "reconnect":
                    case "force reconnect":
                        Console.WriteLine("[CONSOLE] Принудительный реконнект...");
                        try
                        {
                            if (_client.ConnectionState == ConnectionState.Connected)
                                await _statusNotifier.SendReconnectNotification("Ручной реконнект из консоли");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CONSOLE] Ошибка уведомления о реконнекте: {ex.Message}");
                        }
                        await _reconnectionService.RequestManualReconnectAsync("Ручной реконнект из консоли");
                        break;

                    case "predict":
                    case "forecast":
                        var prediction = await _connectionPredictor.AnalyzeAndPredict();
                        if (prediction != null)
                        {
                            Console.WriteLine($"[ПРОГНОЗ] {prediction.Reason} в {prediction.PredictedTime:HH:mm:ss} (уверенность: {prediction.Confidence}%)");
                        }
                        else
                        {
                            Console.WriteLine("[ПРОГНОЗ] Прогнозов нет, соединение стабильно");
                        }
                        break;

                    case "stats":
                    case "history":
                        ShowConnectionStats();
                        break;

                    case "clear":
                        Console.Clear();
                        ShowWelcome();
                        break;

                    case "help":
                        ShowHelp();
                        break;
                    case "servers":
                        await ListServers();
                        break;
                    case "channels":
                        await ListChannels(args);
                        break;
                    case "send":
                        await SendMessage(args);
                        break;
                    case "sessions":
                        await ListSessions();
                        break;
                    case "broadcast":
                        await BroadcastMessage(args);
                        break;
                    default:
                        lock (_lock)
                        {
                            if (_commandsEnabled)
                            {
                                Console.WriteLine($"Неизвестная команда: {cmd}. Введите 'help' для списка команд.");
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка выполнения команды: {ex.Message}");
            }
        }

        private void ShowHelp()
        {
            Console.WriteLine("=== Доступные команды ===");
            Console.WriteLine("  help                            - Показать эту справку");
            Console.WriteLine("  status                          - Статус бота");
            Console.WriteLine("  servers                         - Список серверов");
            Console.WriteLine("  announce                        - Ручная проверка систем");
            Console.WriteLine("  channels [server_id]            - Список каналов на сервере");
            Console.WriteLine("  send [channel_id] [message]     - Отправить сообщение в канал");
            Console.WriteLine("  broadcast [server_id] [message] - Отправить сообщение во все каналы сервера");
            Console.WriteLine("  sessions                        - Список активных игровых сессий");
            Console.WriteLine("  stats                           - История отключений");
            Console.WriteLine("  predict                         - Сделать прогноз");
            Console.WriteLine("  reconnect                       - Принудительный реконнект");
            Console.WriteLine("  restart                         - Перезапуск бота");
            Console.WriteLine("  stop/exit                       - Остановка бота");
            Console.WriteLine("  clear                           - Очистить консоль");
            Console.WriteLine("=========================");
        }

        private void ShowDetailedStatus()
        {
            Console.WriteLine("\n========== СТАТУС БОТА ==========");
            Console.WriteLine($"Состояние: {_client.ConnectionState}");
            Console.WriteLine($"Пользователь: {_client.CurrentUser?.Username ?? "N/A"}");
            Console.WriteLine($"Серверов: {_client.Guilds.Count}");
            Console.WriteLine($"Задержка: {_client.Latency} мс");

            var info = _reconnectionService.ConnectionInfo;
            Console.WriteLine($"\nСТАТИСТИКА ПОДКЛЮЧЕНИЙ:");
            Console.WriteLine($"  Успешных реконнектов: {info.SuccessfulReconnects}");
            Console.WriteLine($"  Неудачных попыток: {info.FailedReconnects}");
            Console.WriteLine($"  Стабильность: {info.ConnectionStabilityScore:F1}%");
            Console.WriteLine($"  Здоровье: {_connectionPredictor.GetConnectionHealthStatus()}");

            Console.WriteLine($"\nПОСЛЕДНЕЕ ОТКЛЮЧЕНИЕ:");
            Console.WriteLine($"  Время: {info.LastDisconnectTime:HH:mm:ss}");
            Console.WriteLine($"  Причина: {info.LastDisconnectReason}");

            if (info.DisconnectStats.Count > 0)
            {
                Console.WriteLine($"\nСТАТИСТИКА ОШИБОК:");
                foreach (var stat in info.DisconnectStats.OrderByDescending(kv => kv.Value).Take(5))
                {
                    Console.WriteLine($"  {stat.Key}: {stat.Value} раз");
                }
            }

            if (info.PredictedDisconnectTime.HasValue)
            {
                Console.WriteLine($"\nПРОГНОЗ:");
                Console.WriteLine($"  Возможное отключение: {info.PredictedDisconnectTime:HH:mm:ss}");
                Console.WriteLine($"  Причина: {info.PredictedReason}");
            }

            Console.WriteLine("======================================\n");
        }

        private void ShowConnectionStats()
        {
            var info = _reconnectionService.ConnectionInfo;

            Console.WriteLine("\n========== ИСТОРИЯ ==========");
            Console.WriteLine("ПОСЛЕДНИЕ 10 ОТКЛЮЧЕНИЙ:");

            if (info.RecentDisconnectReasons.Count == 0)
            {
                Console.WriteLine("  Нет записей");
            }
            else
            {
                foreach (var reason in info.RecentDisconnectReasons)
                {
                    Console.WriteLine($"  • {reason}");
                }
            }
            Console.WriteLine("================================\n");
        }

        private void ShowWelcome()
        {
            Console.WriteLine("================================================");
            Console.WriteLine("    Discord Bot - Продвинутая система");
            Console.WriteLine("================================================");
        }

        private async Task ListServers()
        {
            Console.WriteLine($"=== Серверы ({_client.Guilds.Count}) ===");
            foreach (var guild in _client.Guilds)
            {
                Console.WriteLine($"{guild.Id} - {guild.Name} (Участников: {guild.MemberCount})");
            }
            Console.WriteLine($"=======================");
        }

        private async Task ListChannels(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Использование: channels [server_id]");
                return;
            }

            if (!ulong.TryParse(args[0], out var serverId))
            {
                Console.WriteLine("Неверный ID сервера");
                return;
            }

            var guild = _client.GetGuild(serverId);
            if (guild == null)
            {
                Console.WriteLine("Сервер не найден");
                return;
            }

            Console.WriteLine($"=== Каналы сервера {guild.Name} ===");

            Console.WriteLine("\nТекстовые каналы:");
            foreach (var channel in guild.TextChannels.OrderBy(c => c.Position))
            {
                Console.WriteLine($"  #{channel.Name} ({channel.Id})");
            }

            Console.WriteLine("\nГолосовые каналы:");
            foreach (var channel in guild.VoiceChannels.OrderBy(c => c.Position))
            {
                Console.WriteLine($"  {channel.Name} ({channel.Id})");
            }
            Console.WriteLine($"================================");
        }

        private async Task SendMessage(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Использование: send [channel_id] [message]");
                return;
            }

            if (!ulong.TryParse(args[0], out var channelId))
            {
                Console.WriteLine("Неверный ID канала");
                return;
            }

            var channel = _client.GetChannel(channelId) as IMessageChannel;
            if (channel == null)
            {
                Console.WriteLine("Канал не найден");
                return;
            }

            var message = string.Join(" ", args.Skip(1));
            await channel.SendMessageAsync($"[Админ] {message}");
            Console.WriteLine($"Сообщение отправлено в #{channel.Name}");
        }

        private async Task BroadcastMessage(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Использование: broadcast [server_id] [message]");
                return;
            }

            if (!ulong.TryParse(args[0], out var serverId))
            {
                Console.WriteLine("Неверный ID сервера");
                return;
            }

            var guild = _client.GetGuild(serverId);
            if (guild == null)
            {
                Console.WriteLine("Сервер не найден");
                return;
            }

            var message = string.Join(" ", args.Skip(1));
            var sentCount = 0;

            foreach (var channel in guild.TextChannels.Where(c => c is ITextChannel))
            {
                try
                {
                    var textChannel = channel as ITextChannel;
                    if (textChannel != null)
                    {
                        await textChannel.SendMessageAsync($"[Объявление] {message}");
                        sentCount++;
                        await Task.Delay(200); // Чтобы не превысить лимиты Discord
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка отправки в #{channel.Name}: {ex.Message}");
                }
            }

            Console.WriteLine($"Сообщение отправлено в {sentCount} каналов сервера {guild.Name}");
        }

        private async Task ListSessions()
        {
            Console.WriteLine($"=== Активные игровые сессии ===");

            foreach (var guildSessions in GameSessionCommands._sessions)
            {
                var guild = _client.GetGuild(guildSessions.Key);
                Console.WriteLine($"\nСервер: {guild?.Name ?? "Unknown"} ({guildSessions.Key})");

                foreach (var session in guildSessions.Value.Values.Where(s => !s.IsStopped))
                {
                    var status = session.IsPaused ? "На паузе" : "Активна";
                    var rolls = session.TrackRolls ? $" (бросков: {session.Rolls.Count})" : "";
                    Console.WriteLine($"  {session.GameName} - {status}{rolls}");
                    Console.WriteLine($"    ID: {session.SessionId}, Мастер: {session.MasterName}");
                    Console.WriteLine($"    Начало: {session.StartTime:dd.MM.yyyy HH:mm}");
                }
            }

            if (!GameSessionCommands._sessions.Any())
            {
                Console.WriteLine("Активных сессий нет");
            }
            Console.WriteLine($"===============================");
        }

        private async Task RestartBot()
        {
            Console.WriteLine("Перезапуск бота...");
            await _botController.RestartAsync();
        }

        private async Task StopBot()
        {
            Console.WriteLine("Остановка бота...");
            await _botController.StopAsync();
        }
    }
}
