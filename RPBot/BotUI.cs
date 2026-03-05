using Discord.WebSocket;
using Terminal.Gui;
using static Terminal.Gui.View;
using System.Linq;
using System.Threading.Tasks;

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

        private async Task ShowSettingsInteractive()
        {
            try
            {
                var guilds = _client.Guilds.ToList();
                if (guilds.Count == 0)
                {
                    AddCommandOutput("Нет доступных серверов для выбора.");
                    return;
                }

                int selectedGuildIndex = -1;
                var tcsGuild = new TaskCompletionSource<int>();

                Application.MainLoop.Invoke(() =>
                {
                    var dlg = new Dialog("Выберите сервер", 60, 20);
                    var items = guilds.Select(g => $"{g.Name} ({g.Id})").ToList();
                    var list = new ListView(items)
                    {
                        X = 0,
                        Y = 0,
                        Width = Dim.Fill(),
                        Height = Dim.Fill() - 3
                    };

                    var ok = new Button("OK") { X = Pos.Percent(20), Y = Pos.Bottom(list) };
                    var cancel = new Button("Cancel") { X = Pos.Percent(65), Y = Pos.Bottom(list) };

                    ok.Clicked += () =>
                    {
                        selectedGuildIndex = list.SelectedItem;
                        Application.RequestStop(dlg);
                        tcsGuild.TrySetResult(selectedGuildIndex);
                    };
                    cancel.Clicked += () =>
                    {
                        Application.RequestStop(dlg);
                        tcsGuild.TrySetResult(-1);
                    };

                    dlg.Add(list, ok, cancel);
                    Application.Run(dlg);
                });

                selectedGuildIndex = await tcsGuild.Task;
                if (selectedGuildIndex < 0)
                {
                    AddCommandOutput("Операция отменена.");
                    return;
                }

                var selectedGuild = guilds[selectedGuildIndex];
                var guildId = selectedGuild.Id;

                // Выбор действия
                string[] actions = new[] { "get", "set", "list", "reset" };
                int actionIndex = await ShowSelectionDialog("Выберите действие", actions);
                if (actionIndex < 0) { AddCommandOutput("Операция отменена."); return; }
                var action = actions[actionIndex];

                if (action == "list")
                {
                    var cfg = await _botController.GetServerConfigAsync(guildId);
                    if (cfg == null)
                    {
                        AddCommandOutput($"Настройки для {guildId} не найдены.");
                    }
                    else
                    {
                        AddCommandOutput($"Настройки для {guildId}:");
                        AddCommandOutput($"moderation_channel: {cfg.ModerateChannelID}");
                        AddCommandOutput($"welcome_channel: {cfg.WelcomeChannelID}");
                        AddCommandOutput($"roll_channel: {cfg.RollChannelID}");
                        AddCommandOutput($"stats_channel: {cfg.StatsChannelID}");
                        AddCommandOutput($"record_channel: {cfg.RecordChannelID}");
                        AddCommandOutput($"welcome_message: {cfg.WelcomeMessage}");
                        AddCommandOutput($"line_message: {cfg.LineMessage}");
                        AddCommandOutput($"general_rg_channel: {cfg.GeneralRGChannelID}");
                        AddCommandOutput($"default_role: {cfg.DefaultRoleID}");
                        AddCommandOutput($"swear_filter: {cfg.SwearFilterEnabled}");
                        AddCommandOutput($"swear_words: {(cfg.SwearWords != null ? string.Join(',', cfg.SwearWords) : "")}");
                    }
                    return;
                }

                if (action == "reset")
                {
                    await _botController.ResetServerConfigAsync(guildId);
                    AddCommandOutput($"Настройки для {guildId} сброшены.");
                    return;
                }

                // Действия get/set требуют выбора ключа
                string[] keys = new[] {
                    "moderation_channel","welcome_channel","roll_channel","stats_channel","record_channel","general_rg_channel",
                    "welcome_message","line_message","default_role","swear_filter","swear_words"
                };

                int keyIndex = await ShowSelectionDialog("Выберите ключ", keys);
                if (keyIndex < 0) { AddCommandOutput("Операция отменена."); return; }
                var key = keys[keyIndex];

                if (action == "get")
                {
                    var cfg = await _botController.GetServerConfigAsync(guildId);
                    if (cfg == null) { AddCommandOutput($"Настройки для {guildId} не найдены."); return; }
                    string res = key switch
                    {
                        "moderation_channel" => cfg.ModerateChannelID.ToString(),
                        "welcome_channel" => cfg.WelcomeChannelID.ToString(),
                        "roll_channel" => cfg.RollChannelID.ToString(),
                        "stats_channel" => cfg.StatsChannelID.ToString(),
                        "record_channel" => cfg.RecordChannelID.ToString(),
                        "welcome_message" => cfg.WelcomeMessage ?? "",
                        "line_message" => cfg.LineMessage ?? "",
                        "default_role" => cfg.DefaultRoleID.ToString(),
                        "swear_filter" => cfg.SwearFilterEnabled.ToString(),
                        "swear_words" => (cfg.SwearWords != null ? string.Join(',', cfg.SwearWords) : ""),
                        _ => "Неизвестный ключ"
                    };
                    AddCommandOutput(res);
                    return;
                }

                // action == set
                ulong? channelId = null;
                bool? toggle = null;
                string value = null;

                if (key.EndsWith("_channel") )
                {
                    // Покажем список текстовых каналов сервера
                    var textChannels = selectedGuild.TextChannels.OrderBy(c => c.Position).ToList();
                    if (textChannels.Count > 0)
                    {
                        var items = textChannels.Select(c => $"{c.Name} ({c.Id})").ToArray();
                        int chIndex = await ShowSelectionDialog("Выберите канал", items);
                        if (chIndex < 0) { AddCommandOutput("Операция отменена."); return; }
                        channelId = textChannels[chIndex].Id;
                    }
                    else
                    {
                        AddCommandOutput("На сервере нет текстовых каналов для выбора.");
                        return;
                    }
                }
                else if (key == "default_role")
                {
                    var roles = selectedGuild.Roles.OrderBy(r => r.Position).ToList();
                    var items = roles.Select(r => $"{r.Name} ({r.Id})").ToArray();
                    int rIndex = await ShowSelectionDialog("Выберите роль", items);
                    if (rIndex < 0) { AddCommandOutput("Операция отменена."); return; }
                    value = roles[rIndex].Id.ToString();
                }
                else if (key == "swear_filter")
                {
                    var opts = new[] { "true", "false" };
                    int idx = await ShowSelectionDialog("Включить фильтр мата?", opts);
                    if (idx < 0) { AddCommandOutput("Операция отменена."); return; }
                    toggle = opts[idx] == "true";
                }
                else
                {
                    // Запросим текстовое значение
                    var input = await ShowInputDialog($"Введите значение для {key}");
                    if (input == null) { AddCommandOutput("Операция отменена."); return; }
                    value = input;
                }

                // Валидация перед отправкой
                if (channelId.HasValue)
                {
                    // проверим, что канал существует
                    var ch = selectedGuild.GetTextChannel(channelId.Value);
                    if (ch == null)
                    {
                        AddCommandOutput("Выбранный канал не найден на сервере.");
                        return;
                    }
                }

                await _botController.SetServerConfigValueAsync(guildId, key, value, channelId, toggle);
                AddCommandOutput($"Настройка {key} для {guildId} обновлена.");
            }
            catch (Exception ex)
            {
                AddCommandOutput($"Ошибка интерактивной настройки: {ex.Message}");
            }
        }

        private Task<int> ShowSelectionDialog(string title, IEnumerable<string> items)
        {
            var list = items.ToArray();
            var tcs = new TaskCompletionSource<int>();
            Application.MainLoop.Invoke(() =>
            {
                var dlg = new Dialog(title, 60, 20);
                var lv = new ListView(list)
                {
                    X = 0,
                    Y = 0,
                    Width = Dim.Fill(),
                    Height = Dim.Fill() - 3
                };
                var ok = new Button("OK") { X = Pos.Percent(20), Y = Pos.Bottom(lv) };
                var cancel = new Button("Cancel") { X = Pos.Percent(65), Y = Pos.Bottom(lv) };
                ok.Clicked += () => { tcs.TrySetResult(lv.SelectedItem); Application.RequestStop(dlg); };
                cancel.Clicked += () => { tcs.TrySetResult(-1); Application.RequestStop(dlg); };
                // Allow Enter to accept selection
                dlg.KeyPress += (e) =>
                {
                    try
                    {
                        if (e.KeyEvent.Key == Key.Enter)
                        {
                            tcs.TrySetResult(lv.SelectedItem);
                            Application.RequestStop(dlg);
                            e.Handled = true;
                        }
                        else if (e.KeyEvent.Key == Key.Esc)
                        {
                            tcs.TrySetResult(-1);
                            Application.RequestStop(dlg);
                            e.Handled = true;
                        }
                    }
                    catch { }
                };
                dlg.Add(lv, ok, cancel);
                Application.Run(dlg);
            });
            return tcs.Task;
        }

        private Task<string> ShowInputDialog(string title)
        {
            var tcs = new TaskCompletionSource<string>();
            Application.MainLoop.Invoke(() =>
            {
                var dlg = new Dialog(title, 60, 8);
                var tf = new TextField("") { X = 0, Y = 0, Width = Dim.Fill() };
                var ok = new Button("OK") { X = Pos.Percent(30), Y = 2 };
                var cancel = new Button("Cancel") { X = Pos.Percent(60), Y = 2 };
                ok.Clicked += () => { tcs.TrySetResult(tf.Text.ToString()); Application.RequestStop(dlg); };
                cancel.Clicked += () => { tcs.TrySetResult(null); Application.RequestStop(dlg); };
                dlg.KeyPress += (e) =>
                {
                    try
                    {
                        if (e.KeyEvent.Key == Key.Enter)
                        {
                            tcs.TrySetResult(tf.Text.ToString());
                            Application.RequestStop(dlg);
                            e.Handled = true;
                        }
                        else if (e.KeyEvent.Key == Key.Esc)
                        {
                            tcs.TrySetResult(null);
                            Application.RequestStop(dlg);
                            e.Handled = true;
                        }
                    }
                    catch { }
                };
                dlg.Add(tf, ok, cancel);
                Application.Run(dlg);
            });
            return tcs.Task;
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
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: unable to resize console: {ex.Message}");
                }

                // Инициализация Terminal.Gui. Иногда MainLoop может быть null после Shutdown(),
                // поэтому проверяем и инициализируем повторно при необходимости.
                if (!_isInitialized || Application.MainLoop == null)
                {
                    try
                    {
                        Application.Init();
                        _isInitialized = true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка инициализации UI: {ex.Message}");
                        _isInitialized = false;
                        throw;
                    }
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
                Console.WriteLine($"Критическая ошибка UI: {ex.Message}");
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
                        // Устанавливаем фокус на панель логов, затем прокручиваем вверх
                        try { _logPanel.SetFocus(); } catch { }
                        var newY = Math.Max(0, _logPanel.CursorPosition.Y - 3);
                        _logPanel.CursorPosition = new Point(_logPanel.CursorPosition.X, newY);
                        _logPanel.SetNeedsDisplay();
                        mouseEvent.Handled = true;
                    }
                    // Прокрутка вниз
                    else if (mouseEvent.Flags == MouseFlags.WheeledDown)
                    {
                        // Устанавливаем фокус на панель логов, затем прокручиваем вниз
                        try { _logPanel.SetFocus(); } catch { }
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
                        try { _commandPanel.SetFocus(); } catch { }
                        var newY = Math.Max(0, _commandPanel.CursorPosition.Y - 3);
                        _commandPanel.CursorPosition = new Point(_commandPanel.CursorPosition.X, newY);
                        _commandPanel.SetNeedsDisplay();
                        mouseEvent.Handled = true;
                    }
                    // Прокрутка вниз
                    else if (mouseEvent.Flags == MouseFlags.WheeledDown)
                    {
                        try { _commandPanel.SetFocus(); } catch { }
                        var newY = _commandPanel.CursorPosition.Y + 3;
                        _commandPanel.CursorPosition = new Point(_commandPanel.CursorPosition.X, newY);
                        _commandPanel.SetNeedsDisplay();
                        mouseEvent.Handled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"OnRootMouseEvent error: {ex.Message}");
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
                var height = Math.Max(1, _logPanel.Bounds.Height);
                var newTop = Math.Max(0, lines - height);
                _logPanel.CursorPosition = new Point(0, newTop);

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
                        Console.WriteLine($"Ошибка разблокировки ввода: {ex.Message}");
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
                        Console.WriteLine($"Ошибка блокировки ввода: {ex.Message}");
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

                    // Правильно прокручиваем к концу по строкам (не по символам)
                    var cmdLines = _commandPanel.Text.ToString().Split('\n').Length;
                    var cmdHeight = Math.Max(1, _commandPanel.Bounds.Height);
                    var cmdTop = Math.Max(0, cmdLines - cmdHeight);
                    _commandPanel.CursorPosition = new Point(0, cmdTop);
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteCommand(command);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ExecuteCommand error: {ex}");
                    }
                });

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

                var cmd = parts[0].ToLowerInvariant();
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

                    case "settings":
                        {
                    if (args.Length == 0)
                    {
                        await ShowSettingsInteractive();
                        break;
                    }

                            var sub = args[0].ToLowerInvariant();
                            switch (sub)
                            {
                                case "list":
                                    {
                                        if (args.Length == 2 && ulong.TryParse(args[1], out var gid))
                                        {
                                            var cfg = await _botController.GetServerConfigAsync(gid);
                                            if (cfg == null)
                                            {
                                                AddCommandOutput($"Настройки для {gid} не найдены.");
                                            }
                                            else
                                            {
                                                AddCommandOutput($"Настройки для {gid}:");
                                                AddCommandOutput($"moderation_channel: {cfg.ModerateChannelID}");
                                                AddCommandOutput($"welcome_channel: {cfg.WelcomeChannelID}");
                                                AddCommandOutput($"roll_channel: {cfg.RollChannelID}");
                                                AddCommandOutput($"stats_channel: {cfg.StatsChannelID}");
                                                AddCommandOutput($"record_channel: {cfg.RecordChannelID}");
                                                AddCommandOutput($"welcome_message: {cfg.WelcomeMessage}");
                                                AddCommandOutput($"line_message: {cfg.LineMessage}");
                                                AddCommandOutput($"default_role: {cfg.DefaultRoleID}");
                                                AddCommandOutput($"swear_filter: {cfg.SwearFilterEnabled}");
                                                AddCommandOutput($"swear_words: {(cfg.SwearWords != null ? string.Join(',', cfg.SwearWords) : "")}");
                                            }
                                        }
                                        else
                                        {
                                            var all = await _botController.GetAllServerConfigsAsync();
                                            AddCommandOutput($"Всего конфигов: {all.Count}");
                                            foreach (var kv in all)
                                            {
                                                AddCommandOutput($"- {kv.Key} (moderation={kv.Value.ModerateChannelID}, roll={kv.Value.RollChannelID})");
                                            }
                                        }
                                        break;
                                    }
                                case "get":
                                    {
                                        if (args.Length < 3 || !ulong.TryParse(args[1], out var gid))
                                        {
                                            AddCommandOutput("Использование: settings get <guildId> <key>");
                                        }
                                        else
                                        {
                                            var key = args[2].ToLowerInvariant();
                                            var cfg = await _botController.GetServerConfigAsync(gid);
                                            if (cfg == null) { AddCommandOutput($"Настройки для {gid} не найдены."); break; }
                                            string res = key switch
                                            {
                                                "moderation_channel" => cfg.ModerateChannelID.ToString(),
                                                "welcome_channel" => cfg.WelcomeChannelID.ToString(),
                                                "roll_channel" => cfg.RollChannelID.ToString(),
                                                "stats_channel" => cfg.StatsChannelID.ToString(),
                                                "record_channel" => cfg.RecordChannelID.ToString(),
                                                "welcome_message" => cfg.WelcomeMessage ?? "",
                                                "line_message" => cfg.LineMessage ?? "",
                                                "default_role" => cfg.DefaultRoleID.ToString(),
                                                "swear_filter" => cfg.SwearFilterEnabled.ToString(),
                                                "swear_words" => (cfg.SwearWords != null ? string.Join(',', cfg.SwearWords) : ""),
                                                _ => "Неизвестный ключ"
                                            };
                                            AddCommandOutput(res);
                                        }
                                        break;
                                    }
                                case "set":
                                    {
                                        if (args.Length < 4 || !ulong.TryParse(args[1], out var gid))
                                        {
                                            AddCommandOutput("Использование: settings set <guildId> <key> <value|channelId|toggle>");
                                            break;
                                        }
                                        var key = args[2].ToLowerInvariant();
                                        var val = string.Join(' ', args.Skip(3));
                                        ulong? channelId = null;
                                        bool? toggle = null;
                                        if (ulong.TryParse(val, out var cid)) channelId = cid;
                                        else if (bool.TryParse(val, out var b)) toggle = b;

                                        await _botController.SetServerConfigValueAsync(gid, key, val, channelId, toggle);
                                        AddCommandOutput($"OK: set {key} for {gid}");
                                        break;
                                    }
                                case "reset":
                                    {
                                        if (args.Length < 2 || !ulong.TryParse(args[1], out var gid))
                                        {
                                            AddCommandOutput("Использование: settings reset <guildId>");
                                            break;
                                        }
                                        await _botController.ResetServerConfigAsync(gid);
                                        AddCommandOutput($"Настройки для {gid} сброшены.");
                                        break;
                                    }
                                default:
                                    AddCommandOutput($"Неизвестная подкоманда settings: {sub}");
                                    break;
                            }
                        }
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

                        // Прокручиваем вниз к новым сообщениям учитывать высоту панели
                        var lines = _logPanel.Text.ToString().Split('\n').Length;
                        var height = Math.Max(1, _logPanel.Bounds.Height);
                        var newTop = Math.Max(0, lines - height);
                        _logPanel.CursorPosition = new Point(0, newTop);

                        _logPanel.SetNeedsDisplay();
                        Application.Refresh();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка добавления лога: {ex.Message}");
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
                        var height = Math.Max(1, _commandPanel.Bounds.Height);
                        var newTop = Math.Max(0, lines - height);
                        _commandPanel.CursorPosition = new Point(0, newTop);

                        _commandPanel.SetNeedsDisplay();
                        Application.Refresh();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка добавления вывода команды: {ex.Message}");
                    }
                });
            }
            else
            {
                Console.WriteLine(text);
            }
        }

        public void ShowSystemReady(string botName, int serverCount, double initTime)
        {
            // Подготовка сообщения вне UI-потока
            string initTimeStr;
            if (initTime < 1)
                initTimeStr = $"{(initTime * 1000):F0} мс";
            else if (initTime < 60)
                initTimeStr = $"{initTime:F1} сек";
            else
            {
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
                              $"║  Инициализация: {initTimeStr,-29} ║\n" +
                              $"╚══════════════════════════════════════════════════════╝\n";

            // Если UI не готов или уже уничтожен — сохраним сообщение в pending и выйдем
            if (Application.MainLoop == null || _isDisposed || _logPanel == null || _commandPanel == null)
            {
                // Добавим с меткой времени
                _pendingLogs.Add($"[{DateTime.Now:HH:mm:ss}] {readyMessage}");
                // Также добавим краткое уведомление для командной панели (если она есть later)
                try
                {
                    if (_commandPanel != null)
                    {
                        var shortMsg = "Консоль готова к приёму команд. Введите 'help'\n";
                        _pendingLogs.Add($"[{DateTime.Now:HH:mm:ss}] {shortMsg}");
                    }
                }
                catch { }

                return;
            }

            Application.MainLoop.Invoke(() =>
            {
                try
                {
                    string currentLogText = _logPanel?.Text.ToString() ?? "";
                    _logPanel.Text = currentLogText + readyMessage;

                    var logLines = _logPanel.Text.ToString().Split('\n').Length;
                    _logPanel.CursorPosition = new Point(0, Math.Max(0, logLines - 1));

                    string currentCommandText = _commandPanel?.Text.ToString() ?? "";
                    _commandPanel.Text = currentCommandText + "Консоль готова к приёму команд. Введите 'help'\n";

                    var cmdLines = _commandPanel.Text.ToString().Split('\n').Length;
                    _commandPanel.CursorPosition = new Point(0, Math.Max(0, cmdLines - 1));

                    _logPanel?.SetNeedsDisplay();
                    _commandPanel?.SetNeedsDisplay();
                    Application.Refresh();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка ShowSystemReady: {ex.Message}");
                }
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

                if (Application.MainLoop != null)
                {
                    try
                    {
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
                    catch
                    {
                        // Иногда MainLoop уже завершается — пробуем прямой Shutdown
                        try { Application.Shutdown(); } catch { }
                    }
                }
                else
                {
                    // Если MainLoop уже null — попытаемся корректно вызвать Shutdown() на всякий случай
                    try { Application.Shutdown(); } catch { }
                }
            }
            catch { }

            // Даем время на завершение
            // Сбрасываем состояние и даём время на завершение
            _isInitialized = false;
            Thread.Sleep(500);
        }
    }
}