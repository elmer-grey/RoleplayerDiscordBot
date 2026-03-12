using Discord.WebSocket;
using Terminal.Gui;
using static Terminal.Gui.View;

namespace RPBot
{
    public class BotUI : IDisposable
    {
        private DiscordSocketClient _client;
        private IBotController _botController;
        private ReconnectionService _reconnectionService;
        private ConnectionPredictor _connectionPredictor;
        private StatusNotifier _statusNotifier;

        // Элементы интерфейса
        private Window _mainWindow;
        private TextView _logPanel;
        private TextView _commandPanel;
        private TextField _inputField;
        private Label _statusBar;

        private bool _isRunning = true;
        private List<string> _commandHistory = new List<string>();
        private int _historyIndex = -1;
        private bool _isInitialized = false;

        private bool _inputEnabled = false;
        private bool _isDisposed = false;

        private List<string> _pendingLogs = new List<string>();

        public BotUI(
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

        public void Start()
        {
            try
            {
                // ВАЖНО: Настройка консоли ДО инициализации Terminal.Gui
                Console.CursorVisible = false; // Скрываем курсор сразу
                Console.Title = "Discord Bot Control Panel";

                // Устанавливаем размер консоли, если нужно
                try
                {
                    if (Console.WindowWidth < 100 || Console.WindowHeight < 30)
                    {
                        Console.SetWindowSize(120, 35);
                        Console.SetBufferSize(120, 1000);
                    }
                }
                catch { }

                if (!_isInitialized)
                {
                    Application.Init();
                    _isInitialized = true;
                }

                CreateMainWindow();

                FlushPendingLogs();
                while (_isRunning)
                {
                    try
                    {
                        Application.Run();
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("key values must be beetween 0 and 255"))
                        {
                            continue;
                        }
                        Thread.Sleep(1000);
                    }
                }
            }
            catch (Exception ex)
            {
                try { Logger.LogError($"Критическая ошибка UI: {ex.Message}"); } catch { }
                Environment.Exit(1);
            }
        }

        private void CreateMainWindow()
        {
            Console.Clear();

            _mainWindow = new Window($"Discord Bot Control Panel v0.6.0 - {DateTime.Now:HH:mm:ss}")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                //CanFocus = false,
                //Enabled = false,
            };

            // ВЕРХНЯЯ ПАНЕЛЬ - СТАТУС БАР
            _statusBar = new Label("")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = 1,
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.BrightYellow, Color.Blue)
                },
                CanFocus = false,
                TabStop = false
            };
            _mainWindow.Add(_statusBar);

            // ЛЕВАЯ ПАНЕЛЬ - ЛОГИ (70% ширины)
            var logFrame = new FrameView("LOGS PANEL")
            {
                X = 0,
                Y = 1,
                Width = Dim.Percent(70),
                Height = Dim.Fill() - 3,
                CanFocus = false
            };

            _logPanel = new TextView
            {
                ReadOnly = true,
                WordWrap = true,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.White, Color.Green)
                },                
                DesiredCursorVisibility = CursorVisibility.Invisible,
                CanFocus = false,
                TabStop = false
            };

            logFrame.Add(_logPanel);

            // ПРАВАЯ ПАНЕЛЬ - КОМАНДЫ (30% ширины)
            var commandFrame = new FrameView("COMMANDS OUTPUT")
            {
                X = Pos.Percent(70),
                Y = 1,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 3,
                CanFocus = false
            };

            _commandPanel = new TextView
            {
                ReadOnly = true,
                WordWrap = true,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.Green, Color.Black)
                },
                DesiredCursorVisibility = CursorVisibility.Invisible,
                CanFocus = false,
                TabStop = false
            };

            commandFrame.Add(_commandPanel);

            // НИЖНЯЯ ПАНЕЛЬ - ВВОД
            var inputFrame = new FrameView("INPUT")
            {
                X = 0,
                Y = Pos.Bottom(logFrame),
                Width = Dim.Fill(),
                Height = 3,
                CanFocus = true
            };

            _inputField = new TextField("")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = 1,
                Enabled = false, // Изначально отключен
                CanFocus = true, // Может получать фокус когда включен
                DesiredCursorVisibility = CursorVisibility.Default
            };

            var hintLabel = new Label("Enter - отправить, ↑↓ - история, Ctrl+C - выход")
            {
                X = 0,
                Y = 1,
                Width = Dim.Fill(),
                Height = 1,
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.DarkGray, Color.Black)
                },
                CanFocus = false,
            };

            inputFrame.Add(_inputField, hintLabel);

            // Собираем всё вместе
            _mainWindow.Add(logFrame, commandFrame, inputFrame);
            Application.Top.Add(_mainWindow);

            // ВАЖНО: Устанавливаем фокус на главное окно, а потом на inputFrame
            Application.Top.FocusFirst();

            Application.Top.TabStop = false;

            // Подписываемся на события
            _inputField.KeyPress += OnInputKeyPress;

            // Добавляем обработчик для перехвата всех клавиш, чтобы они не уходили в консоль
            Application.RootKeyEvent += OnRootKeyEvent;

            // Запускаем обновление статуса
            Application.MainLoop.AddTimeout(TimeSpan.FromSeconds(1), UpdateStatusBar);

            Application.RootMouseEvent += OnRootMouseEvent;
        }

        // Добавьте этот метод для перехвата мыши
        private bool OnRootKeyEvent(KeyEvent keyEvent)
        {
            // Блокируем все клавиши, если ввод не разрешен и это не специальные комбинации
            if (!_inputEnabled)
            {
                // Разрешаем только Ctrl+C для выхода
                if (keyEvent.Key == (Key.C | Key.CtrlMask))
                {
                    Environment.Exit(0);
                    return true;
                }
                return true;
            }
            return false;
        }

        private void OnRootMouseEvent(MouseEvent mouseEvent)
        {
            try
            {
                // Проверяем, находится ли мышь в области логов
                if (mouseEvent.X < Console.WindowWidth * 0.7 && mouseEvent.Y < Console.WindowHeight - 3)
                {
                    // Прокрутка вверх
                    if (mouseEvent.Flags == MouseFlags.WheeledUp)
                    {
                        // Прокручиваем вверх, уменьшая позицию курсора
                        var newY = Math.Max(0, _logPanel.CursorPosition.Y - 3);
                        _logPanel.CursorPosition = new Point(_logPanel.CursorPosition.X, newY);
                        _logPanel.SetNeedsDisplay();
                        mouseEvent.Handled = true;
                    }
                    // Прокрутка вниз
                    else if (mouseEvent.Flags == MouseFlags.WheeledDown)
                    {
                        // Просто перемещаем курсор вниз - TextView сам обновится
                        var newY = _logPanel.CursorPosition.Y + 3;
                        _logPanel.CursorPosition = new Point(_logPanel.CursorPosition.X, newY);
                        _logPanel.SetNeedsDisplay();
                        mouseEvent.Handled = true;
                    }
                }
                // Проверяем, находится ли мышь в области команд
                else if (mouseEvent.X >= Console.WindowWidth * 0.7 && mouseEvent.Y < Console.WindowHeight - 3)
                {
                    // Прокрутка вверх
                    if (mouseEvent.Flags == MouseFlags.WheeledUp)
                    {
                        // Прокручиваем вверх, уменьшая позицию курсора
                        var newY = Math.Max(0, _commandPanel.CursorPosition.Y - 3);
                        _commandPanel.CursorPosition = new Point(_commandPanel.CursorPosition.X, newY);
                        _commandPanel.SetNeedsDisplay();
                        mouseEvent.Handled = true;
                    }
                    // Прокрутка вниз
                    else if (mouseEvent.Flags == MouseFlags.WheeledDown)
                    {
                        // Просто перемещаем курсор вниз
                        var newY = _commandPanel.CursorPosition.Y + 3;
                        _commandPanel.CursorPosition = new Point(_commandPanel.CursorPosition.X, newY);
                        _commandPanel.SetNeedsDisplay();
                        mouseEvent.Handled = true;
                    }
                }
            }
            catch (Exception ex)
            {                
            }
        }

        private void FlushPendingLogs()
        {
            if (_pendingLogs.Count > 0 && _logPanel != null)
            {
                string currentText = _logPanel.Text.ToString();
                foreach (var log in _pendingLogs)
                {
                    currentText += log;
                }
                _logPanel.Text = currentText;

                var lines = _logPanel.Text.ToString().Split('\n').Length;
                _logPanel.CursorPosition = new Point(0, lines - 1);

                _logPanel.SetNeedsDisplay();
                _pendingLogs.Clear();
                Application.Refresh();
            }
        }

        public void EnableInput()
        {
            if (Application.MainLoop != null && !_isDisposed)
            {
                Application.MainLoop.Invoke(() =>
                {
                    try
                    {
                        if (_inputField != null)
                        {
                            _inputEnabled = true; // Устанавливаем флаг
                            _inputField.Enabled = true;
                            Application.Top.FocusFirst();
                            _inputField.SetFocus();

                            AddLog("Ввод команд разблокирован");

                            // Принудительно обновляем экран
                            Application.Refresh();
                        }
                    }
                    catch (Exception ex)
                    {
                        try { Logger.LogError($"Ошибка разблокировки ввода: {ex.Message}"); } catch { }
                    }
                });
            }
        }

        public void DisableInput()
        {
            if (Application.MainLoop != null && !_isDisposed)
            {
                Application.MainLoop.Invoke(() =>
                {
                    try
                    {
                        if (_inputField != null)
                        {
                            _inputEnabled = false;
                            _inputField.Enabled = false;

                            // Убираем фокус с поля ввода
                            Application.Top.FocusFirst();

                            Application.Refresh();
                        }
                    }
                    catch (Exception ex)
                    {
                        try { Logger.LogError($"Ошибка блокировки ввода: {ex.Message}"); } catch { }
                    }
                });
            }
        }

        private bool UpdateStatusBar(MainLoop loop)
        {
            if (_isDisposed) return false;

            try
            {
                var status = _client?.ConnectionState.ToString() ?? "Unknown";
                var latency = _client?.Latency ?? 0;
                var guilds = _client?.Guilds.Count ?? 0;
                var health = _connectionPredictor?.GetConnectionHealthStatus() ?? "Unknown";

                _statusBar.Text = $" Состояние: {status} | Задержка: {latency}ms | Серверов: {guilds} | Здоровье: {health} | Время: {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                _statusBar.Text = $" Ошибка обновления статуса: {ex.Message}";
            }

            return true;
        }

        private void OnInputKeyPress(KeyEventEventArgs args)
        {
            // Проверяем разрешен ли ввод
            if (!_inputEnabled)
            {
                args.Handled = true;
                return;
            }

            if (args.KeyEvent.Key == Key.Enter)
            {
                var command = _inputField.Text.ToString();
                if (string.IsNullOrWhiteSpace(command)) return;

                _commandHistory.Add(command);
                _historyIndex = _commandHistory.Count;

                _inputField.Text = "";

                if (_commandPanel != null)
                {
                    _commandPanel.Text += $"> {command}\n";
                    _commandPanel.CursorPosition = new Point(0, _commandPanel.Text.Length);
                }

                Task.Run(() => ExecuteCommand(command));

                args.Handled = true;
            }
            else if (args.KeyEvent.Key == Key.CursorUp)
            {
                if (_commandHistory.Count > 0 && _historyIndex > 0)
                {
                    _historyIndex--;
                    _inputField.Text = _commandHistory[_historyIndex];
                }
                args.Handled = true;
            }
            else if (args.KeyEvent.Key == Key.CursorDown)
            {
                if (_historyIndex < _commandHistory.Count - 1)
                {
                    _historyIndex++;
                    _inputField.Text = _commandHistory[_historyIndex];
                }
                else if (_historyIndex == _commandHistory.Count - 1)
                {
                    _historyIndex = _commandHistory.Count;
                    _inputField.Text = "";
                }
                args.Handled = true;
            }
        }

        private async Task ExecuteCommand(string command)
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
                        AddCommandOutput("Остановка бота...");
                        await _botController.StopAsync();
                        break;

                    case "restart":
                        AddCommandOutput("Перезапуск бота...");
                        await _botController.RestartAsync();
                        break;

                    case "status":
                        ShowDetailedStatus();
                        break;

                    case "announce":
                    case "systems":
                        AddCommandOutput("Отправка статуса в Discord...");
                        await _statusNotifier.SendAllSystemsActive("Ручная проверка систем");
                        AddCommandOutput("Статус отправлен!");
                        break;

                    case "servers":
                        await ListServers();
                        break;

                    case "predict":
                        var prediction = await _connectionPredictor.AnalyzeAndPredict();
                        if (prediction != null)
                        {
                            AddCommandOutput($"Прогноз: {prediction.Reason} в {prediction.PredictedTime:HH:mm:ss} (уверенность: {prediction.Confidence}%)");
                        }
                        else
                        {
                            AddCommandOutput("Прогнозов нет, соединение стабильно");
                        }
                        break;

                    case "help":
                        ShowHelp();
                        break;

                    default:
                        AddCommandOutput($"Неизвестная команда: {cmd}");
                        break;
                }
            }
            catch (Exception ex)
            {
                AddCommandOutput($"Ошибка: {ex.Message}");
            }
        }

        public void AddLog(string message)
        {
            var logMessage = $"[{DateTime.Now:HH:mm:ss}] {message}\n";

            if (_logPanel != null && Application.MainLoop != null && !_isDisposed)
            {
                Application.MainLoop.Invoke(() =>
                {
                    try
                    {
                        // Преобразуем ustring в string для работы
                        string currentText = _logPanel.Text.ToString();
                        _logPanel.Text = currentText + logMessage;

                        // Прокручиваем вниз к новым сообщениям - устанавливаем курсор в конец
                        var lines = _logPanel.Text.ToString().Split('\n').Length;
                        _logPanel.CursorPosition = new Point(0, lines - 1);

                        _logPanel.SetNeedsDisplay();
                        Application.Refresh();
                    }
                    catch (Exception ex)
                    {
                        try { Logger.LogError($"Ошибка добавления лога: {ex.Message}"); } catch { }
                    }
                });
            }
            else
            {
                _pendingLogs.Add(logMessage);
            }
        }

        private void AddCommandOutput(string text)
        {
            if (_commandPanel != null && Application.MainLoop != null && !_isDisposed)
            {
                Application.MainLoop.Invoke(() =>
                {
                    try
                    {
                        // Преобразуем ustring в string для работы
                        string currentText = _commandPanel.Text.ToString();
                        _commandPanel.Text = currentText + text + "\n";

                        var lines = _commandPanel.Text.ToString().Split('\n').Length;
                        _commandPanel.CursorPosition = new Point(0, lines - 1);

                        _commandPanel.SetNeedsDisplay();
                        Application.Refresh();
                    }
                    catch (Exception ex)
                    {
                        try { Logger.LogError($"Ошибка добавления вывода команды: {ex.Message}"); } catch { }
                    }
                });
            }
            else
            {
                try { Logger.LogInfo(text); } catch { }
            }
        }

        public void ShowSystemReady(string botName, int serverCount, double initTime)
        {
            Application.MainLoop.Invoke(() =>
            {
                // Форматируем время в зависимости от величины
                string initTimeStr;
                if (initTime < 1)
                {
                    // Меньше секунды - показываем в миллисекундах
                    initTimeStr = $"{(initTime * 1000):F0} мс";
                }
                else if (initTime < 60)
                {
                    // Меньше минуты - показываем в секундах с одним знаком после запятой
                    initTimeStr = $"{initTime:F1} сек";
                }
                else
                {
                    // Больше минуты - показываем в минутах и секундах
                    int minutes = (int)initTime / 60;
                    double seconds = initTime % 60;
                    initTimeStr = $"{minutes} мин {seconds:F0} сек";
                }
                string currentTime = DateTime.Now.ToString("HH:mm:ss");
                var readyMessage = $"╔══════════════════════════════════════════════════════╗\n" +
                                  $"║             ВСЕ СИСТЕМЫ АКТИВНЫ!                 ║\n" +
                                  $"╠══════════════════════════════════════════════════════╣\n" +
                                  $"║  Бот:         {botName,-30} ║\n" +
                                  $"║  Серверов:    {serverCount,-30} ║\n" +
                                  $"║  Время:       {currentTime,-30} ║\n" +
                                  $"║  Инициализация: {initTimeStr,-29} ║\n" + // -29 для выравнивания
                                  $"╚══════════════════════════════════════════════════════╝\n";

                string currentLogText = _logPanel.Text.ToString();
                _logPanel.Text = currentLogText + readyMessage;

                var logLines = _logPanel.Text.ToString().Split('\n').Length;
                _logPanel.CursorPosition = new Point(0, logLines - 1);

                string currentCommandText = _commandPanel.Text.ToString();
                _commandPanel.Text = currentCommandText + "Консоль готова к приёму команд. Введите 'help'\n";

                var cmdLines = _commandPanel.Text.ToString().Split('\n').Length;
                _commandPanel.CursorPosition = new Point(0, cmdLines - 1);

                _logPanel.SetNeedsDisplay();
                _commandPanel.SetNeedsDisplay();
                Application.Refresh();
            });
        }

        public Task<bool?> AskYesNoQuestion(string question, string hint, int timeoutSeconds)
        {
            var tcs = new TaskCompletionSource<bool?>();

            // Сохраняем состояние ввода до диалога
            bool previousInputState = _inputEnabled;
            if (_inputEnabled)
            {
                DisableInput();
            }

            Application.MainLoop.Invoke(() =>
            {
                try
                {
                    Console.CursorVisible = false;
                    var dialog = new Dialog("Подтверждение", 50, 8);

                    var questionLabel = new Label(question)
                    {
                        X = Pos.Center(),
                        Y = 1,
                        Width = Dim.Fill() - 2,
                        TextAlignment = TextAlignment.Centered
                    };

                    var hintLabel = new Label(hint)
                    {
                        X = Pos.Center(),
                        Y = 2,
                        Width = Dim.Fill() - 2,
                        TextAlignment = TextAlignment.Centered,
                        ColorScheme = new ColorScheme
                        {
                            Normal = new Terminal.Gui.Attribute(Color.DarkGray, Color.Black)
                        }
                    };

                    var yesButton = new Button("Y - Да")
                    {
                        X = Pos.Percent(20),
                        Y = 4
                    };
                    yesButton.Clicked += () =>
                    {
                        Application.RequestStop(dialog);
                        tcs.TrySetResult(true);
                    };

                    var noButton = new Button("N - Нет")
                    {
                        X = Pos.Percent(65),
                        Y = 4
                    };
                    noButton.Clicked += () =>
                    {
                        Application.RequestStop(dialog);
                        tcs.TrySetResult(false);
                    };

                    dialog.Add(questionLabel, hintLabel, yesButton, noButton);

                    // Устанавливаем фокус на кнопку НЕТ
                    noButton.SetFocus();

                    // Обработчик закрытия диалога - ИСПРАВЛЕНО
                    dialog.Closed += (_) =>
                    {
                        // Возвращаем состояние ввода после закрытия диалога
                        if (previousInputState)
                        {
                            Application.MainLoop.Invoke(() => EnableInput());
                        }
                    };

                    var timeoutTimer = new System.Timers.Timer(timeoutSeconds * 1000);
                    timeoutTimer.AutoReset = false;
                    timeoutTimer.Elapsed += (s, e) =>
                    {
                        try { timeoutTimer.Stop(); } catch { }
                        try { timeoutTimer.Dispose(); } catch { }

                        if (!tcs.Task.IsCompleted)
                        {
                            Application.MainLoop.Invoke(() =>
                            {
                                try
                                {
                                    Application.RequestStop(dialog);
                                    tcs.TrySetResult(false);
                                }
                                catch { }
                            });
                        }
                    };
                    timeoutTimer.Start();

                    Application.Run(dialog);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                    // В случае ошибки обязательно разблокируем ввод
                    if (previousInputState)
                    {
                        Application.MainLoop.Invoke(() => EnableInput());
                    }
                }
            });

            return tcs.Task;
        }

        private void ShowHelp()
        {
            AddCommandOutput("\n=== ДОСТУПНЫЕ КОМАНДЫ ===");
            AddCommandOutput("help     - Эта справка");
            AddCommandOutput("status   - Статус бота");
            AddCommandOutput("servers  - Список серверов");
            AddCommandOutput("announce - Отправить статус в Discord");
            AddCommandOutput("predict  - Сделать прогноз подключения");
            AddCommandOutput("restart  - Перезапуск бота");
            AddCommandOutput("stop     - Остановка бота");
            AddCommandOutput("==========================");
        }

        private void ShowDetailedStatus()
        {
            try
            {
                var info = _reconnectionService.ConnectionInfo;

                AddCommandOutput("\n=== СТАТУС БОТА ===");
                AddCommandOutput($"Состояние: {_client.ConnectionState}");
                AddCommandOutput($"Пользователь: {_client.CurrentUser?.Username ?? "N/A"}");
                AddCommandOutput($"Серверов: {_client.Guilds.Count}");
                AddCommandOutput($"Задержка: {_client.Latency} мс");
                AddCommandOutput($"Стабильность: {info.ConnectionStabilityScore:F1}%");
                AddCommandOutput($"Здоровье: {_connectionPredictor.GetConnectionHealthStatus()}");
                AddCommandOutput("==================");
            }
            catch (Exception ex)
            {
                AddCommandOutput($"Ошибка получения статуса: {ex.Message}");
            }
        }
         
        private async Task ListServers()
        {
            try
            {
                AddCommandOutput($"\n=== СЕРВЕРЫ ({_client.Guilds.Count}) ===");
                foreach (var guild in _client.Guilds)
                {
                    AddCommandOutput($"{guild.Name} ({guild.Id}) - {guild.MemberCount} участников");
                }
            }
            catch (Exception ex)
            {
                AddCommandOutput($"Ошибка получения списка серверов: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        public void UpdateServices(
            DiscordSocketClient client,
            ReconnectionService reconnectionService,
            ConnectionPredictor connectionPredictor,
            StatusNotifier statusNotifier)
        {
            _client = client;
            _reconnectionService = reconnectionService;
            _connectionPredictor = connectionPredictor;
            _statusNotifier = statusNotifier;

            AddLog(" Сервисы бота обновлены");
        }

        public void ClearForRestart()
        {
            if (Application.MainLoop != null && !_isDisposed)
            {
                Application.MainLoop.Invoke(() =>
                {
                    try
                    {
                        if (_logPanel != null) _logPanel.Text = "";
                        if (_commandPanel != null) _commandPanel.Text = "";

                        string restartMsg = $"[{DateTime.Now:HH:mm:ss}] Бот перезапускается...\n";
                        _logPanel.Text = restartMsg;

                        _logPanel.CursorPosition = new Point(0, 1);
                        _logPanel.SetNeedsDisplay();
                        _commandPanel.SetNeedsDisplay();
                        Application.Refresh();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка очистки UI: {ex.Message}");
                    }
                });
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                // Отписываемся от UI-событий, чтобы убрать делегаты
                try { if (_inputField != null) _inputField.KeyPress -= OnInputKeyPress; } catch { }
                try { Application.RootKeyEvent -= OnRootKeyEvent; } catch { }
                try { Application.RootMouseEvent -= OnRootMouseEvent; } catch { }

                Application.MainLoop.Invoke(() =>
                {
                    try
                    {
                        Application.RequestStop();
                        Application.Shutdown();
                    }
                    catch { }
                });
            }
            catch { }

            // Даем время на завершение
            Thread.Sleep(500);
        }
    }
}