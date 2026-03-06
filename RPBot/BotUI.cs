using Discord.WebSocket;
using Terminal.Gui;
using static Terminal.Gui.View;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Collections.Generic;

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

        // Lock to protect Terminal.Gui Init/Shutdown from concurrent calls
        private readonly object _uiLock = new object();

        // Permanent UI thread fields
        private Thread _uiThread;
        private CancellationTokenSource _uiCts;
        private ManualResetEventSlim _uiInitialized = new ManualResetEventSlim(false);
        private bool _statusTimeoutAdded = false;

        private bool _inputEnabled = false;
        private bool _isDisposed = false;

        private const int MaxLogLines = 5000;
        private const int MaxCommandLines = 2000;

        private readonly List<string> _logLines = new List<string>();
        private readonly List<string> _commandLines = new List<string>();

        private readonly List<string> _pendingLogLines = new List<string>();
        private readonly List<string> _pendingCommandLines = new List<string>();

        // Чтобы не показывать старые ошибки после успешного рестарта
        private long _errorLogLengthAtRestart = -1;

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

        private static IEnumerable<string> WrapLineForWidth(string line, int width)
        {
            if (string.IsNullOrEmpty(line))
            {
                yield return string.Empty;
                yield break;
            }

            var remaining = line;
            while (remaining.Length > 0)
            {
                if (remaining.Length <= width)
                {
                    yield return remaining;
                    yield break;
                }

                // Try to break at last space within width
                var segment = remaining.Substring(0, width);
                var lastSpace = segment.LastIndexOf(' ');
                if (lastSpace > Math.Max(0, width / 2))
                {
                    // break at space
                    var part = remaining.Substring(0, lastSpace).TrimEnd();
                    yield return part;
                    remaining = remaining.Substring(lastSpace + 1);
                }
                else
                {
                    // hard break
                    yield return segment;
                    remaining = remaining.Substring(width);
                }
            }
        }

        // Ensure Terminal.Gui is initialized in a threadsafe manner
        private void EnsureUiInitialized()
        {
            lock (_uiLock)
            {
                if (_isInitialized && _uiInitialized.IsSet) return;

                try
                {
                    // Запускаем постоянный UI-поток, если ещё не запущен
                    if (_uiThread == null || !_uiThread.IsAlive)
                    {
                        StartUiThread();
                    }

                    // Ждём сигнализации Init внутри UI-потока (best-effort)
                    if (!_uiInitialized.Wait(5000))
                    {
                        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] EnsureUiInitialized: UI thread did not initialize within timeout\n"); } catch { }
                    }

                    _isInitialized = _uiInitialized.IsSet;
                    try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] EnsureUiInitialized: Init completed\n"); } catch { }
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] EnsureUiInitialized error: {ex}\n"); } catch { }
                    _isInitialized = false;
                    throw;
                }
            }
        }

        // Запуск постоянного UI-потока, который держит Application.Run(Application.Top)
        private void StartUiThread()
        {
            lock (_uiLock)
            {
                if (_uiThread != null && _uiThread.IsAlive) return;

                _uiCts = new CancellationTokenSource();
                _uiInitialized = new ManualResetEventSlim(false);

                _uiThread = new Thread(() =>
                {
                    try
                    {
                        Application.Init();
                        _uiInitialized.Set();
                        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UI thread: Init completed\n"); } catch { }

                        // Запускаем главный цикл; управление subviews будет выполняться через MainLoop.Invoke
                        Application.Run(Application.Top);
                    }
                    catch (Exception ex)
                    {
                        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UI thread exception: {ex}\n"); } catch { }
                    }
                    finally
                    {
                        try { Application.Shutdown(); } catch { }
                    }
                })
                {
                    IsBackground = true,
                    Name = "BotUI Thread"
                };

                _uiThread.Start();
            }
        }

        // Safe shutdown of UI elements (do not stop the permanent UI thread here)
        private void SafeShutdown()
        {
            lock (_uiLock)
            {
                try
                {
                    if (Application.MainLoop != null)
                    {
                        try
                        {
                            Application.MainLoop.Invoke(() =>
                            {
                                try
                                {
                                    // Удалим главное окно из Top и отписываемся от событий
                                    try
                                    {
                                        var top = Application.Top;
                                        if (_mainWindow != null && top != null)
                                        {
                                            try { top.Remove(_mainWindow); } catch { }
                                        }
                                    }
                                    catch { }

                                    try { if (_inputField != null) _inputField.KeyPress -= OnInputKeyPress; } catch { }
                                    try { Application.RootKeyEvent -= OnRootKeyEvent; } catch { }
                                    try { Application.RootMouseEvent -= OnRootMouseEvent; } catch { }

                                    _mainWindow = null;
                                    _logPanel = null;
                                    _commandPanel = null;
                                    _inputField = null;
                                    _statusBar = null;
                                    // Allow status timer to be re-registered on next CreateMainWindow
                                    _statusTimeoutAdded = false;
                                }
                                catch (Exception ex)
                                {
                                    try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] SafeShutdown inner error: {ex}\n"); } catch { }
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] SafeShutdown Invoke error: {ex}\n"); } catch { }
                        }
                    }
                    else
                    {
                        // Если MainLoop отсутствует — всё равно очищаем локальные ссылки (best-effort)
                        try { if (_inputField != null) _inputField.KeyPress -= OnInputKeyPress; } catch { }
                        try { Application.RootKeyEvent -= OnRootKeyEvent; } catch { }
                        try { Application.RootMouseEvent -= OnRootMouseEvent; } catch { }

                        _mainWindow = null;
                        _logPanel = null;
                        _commandPanel = null;
                        _inputField = null;
                        _statusBar = null;
                        // Allow status timer to be re-registered on next CreateMainWindow
                        _statusTimeoutAdded = false;
                    }

                    _isInitialized = false;
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] SafeShutdown outer error: {ex}\n"); } catch { }
                }
            }
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
                    AddLog($"Warning: unable to resize console: {ex.Message}");
                }

                // Централизованная инициализация Terminal.Gui
                EnsureUiInitialized();

                CreateMainWindow();

                FlushPendingOutput();
                while (_isRunning)
                {
                    try
                    {
                        if (_mainWindow == null)
                        {
                            try { CreateMainWindow(); } catch { }
                        }

                        try
                        {
                            bool needReinit = false;
                            try
                            {
                                if (Application.MainLoop == null) needReinit = true;
                                else
                                {
                                    try
                                    {
                                        var top = Application.Top;
                                        var subs = top?.Subviews;
                                        if (subs == null) needReinit = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        needReinit = true;
                                        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] PreRun inner check exception: {ex}\n"); } catch { }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                needReinit = true;
                                try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] PreRun outer check exception: {ex}\n"); } catch { }
                            }

                            if (needReinit)
                            {
                                try
                                {
                                    SafeShutdown();
                                }
                                catch { }

                                Thread.Sleep(100);
                                try
                                {
                                    EnsureUiInitialized();
                                    CreateMainWindow();
                                    try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] PreRun: reinitialized Terminal.Gui and recreated main window\n"); } catch { }
                                }
                                catch (Exception ex)
                                {
                                    try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] PreRun reinit failed: {ex}\n"); } catch { }
                                    throw;
                                }
                            }
                        }
                        catch { }

                        bool guiReady = false;
                        for (int drvAttempt = 0; drvAttempt < 10; drvAttempt++)
                        {
                            try
                            {
                                if (Application.Driver != null && Application.Top != null)
                                {
                                    guiReady = true;
                                    break;
                                }
                            }
                            catch { }

                            try { EnsureUiInitialized(); } catch { }
                            Thread.Sleep(100);
                        }

                        if (!guiReady)
                        {
                            try
                            {
                                var logPath = Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt");
                                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Warning: Application.Driver/Top not ready before Application.Run, retrying loop\n");
                            }
                            catch { }

                            continue;
                        }

                        try
                        {
                            Application.MainLoop.Invoke(() =>
                            {
                                try
                                {
                                    if (_mainWindow != null && _mainWindow.SuperView == null)
                                    {
                                        Application.Top?.Add(_mainWindow);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    try
                                    {
                                        var logPath = Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt");
                                        File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] AddMainWindow error: {ex}\n");
                                    }
                                    catch { }
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            var logPath = Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt");
                            try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] AddMainWindow invoke failed: {ex}\n"); } catch { }
                        }

                        while (_isRunning && _uiThread != null && _uiThread.IsAlive)
                        {
                            Thread.Sleep(500);
                        }

                        break;
                    }
                    catch (Exception ex)
                    {
                        AddLog($"Application.Run error: {ex.Message}");

                        try
                        {
                            var logPath = Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt");
                            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Application.Run error:\n{ex}\n\n";
                            File.AppendAllText(logPath, text);
                        }
                        catch { }

                        try
                        {
                            try { Application.RequestStop(); } catch { }
                            try { Application.Shutdown(); } catch { }
                        }
                        catch { }

                        Thread.Sleep(500);

                        try
                        {
                            Application.Init();
                            CreateMainWindow();
                        }
                        catch (Exception initEx)
                        {
                            AddLog($"Ошибка повторной инициализации UI: {initEx.Message}");
                        }

                        Thread.Sleep(1000);
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"Критическая ошибка UI: {ex.Message}");
                Environment.Exit(1);
            }
        }

        private void CreateMainWindow()
        {
            if (_isDisposed) return;

            _mainWindow = new Window($"Discord Bot Control Panel v0.6.0 - {DateTime.Now:HH:mm:ss}")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
            };

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

            var logFrame = new FrameView("LOGS PANEL")
            {
                X = 0,
                Y = 1,
                Width = Dim.Percent(70),
                Height = Dim.Fill() - 3,
                CanFocus = false
            };

            _logPanel = new TextView()
            {
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ReadOnly = true,
                WordWrap = true,
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.White, Color.Black)
                },
                CanFocus = false,
                TabStop = false
            };

            logFrame.Add(_logPanel);

            var commandFrame = new FrameView("COMMANDS PANEL")
            {
                X = Pos.Percent(70),
                Y = 1,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 3,
                CanFocus = false
            };

            _commandPanel = new TextView()
            {
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ReadOnly = true,
                WordWrap = true,
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.White, Color.Black)
                },
                CanFocus = false,
                TabStop = false
            };

            commandFrame.Add(_commandPanel);

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
                Enabled = false,
                CanFocus = true,
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

            _mainWindow.Add(logFrame, commandFrame, inputFrame);

            try
            {
                try { _mainWindow.TabStop = false; } catch { }
                try { _mainWindow.FocusFirst(); } catch { try { _mainWindow.SetFocus(); } catch { } }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CreateMainWindow focus error: {ex}\n"); } catch { }
            }

            _inputField.KeyPress += OnInputKeyPress;
            Application.RootKeyEvent += OnRootKeyEvent;

            if (!_statusTimeoutAdded && Application.MainLoop != null)
            {
                try
                {
                    Application.MainLoop.AddTimeout(TimeSpan.FromSeconds(1), UpdateStatusBar);
                    _statusTimeoutAdded = true;
                }
                catch { }
            }

            Application.RootMouseEvent += OnRootMouseEvent;
        }

        private bool OnRootKeyEvent(KeyEvent keyEvent)
        {
            if (!_inputEnabled)
            {
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
                var cols = Application.Driver?.Cols ?? Console.WindowWidth;
                var rows = Application.Driver?.Rows ?? Console.WindowHeight;
                var splitX = (int)(cols * 0.7);
                var isWheelUp = mouseEvent.Flags.HasFlag(MouseFlags.WheeledUp);
                var isWheelDown = mouseEvent.Flags.HasFlag(MouseFlags.WheeledDown);

                if (!isWheelUp && !isWheelDown)
                    return;

                if (mouseEvent.Y >= rows - 3)
                    return;

                var delta = isWheelUp ? -3 : 3;

                if (mouseEvent.X < splitX)
                {
                    ScrollListBy(_logPanel, _logLines, delta);
                    mouseEvent.Handled = true;
                }
                else
                {
                    ScrollListBy(_commandPanel, _commandLines, delta);
                    mouseEvent.Handled = true;
                }
            }
            catch (Exception ex)
            {
                TryAppendErrorToFile($"OnRootMouseEvent error: {ex}");
            }
        }

        private void FlushPendingOutput()
        {
            if (Application.MainLoop == null || _isDisposed)
                return;

            Application.MainLoop.Invoke(() =>
            {
                try
                {
                    if (_pendingLogLines.Count > 0)
                    {
                        AppendLinesUnsafe(_logLines, _logPanel, _pendingLogLines, MaxLogLines);
                        _pendingLogLines.Clear();
                    }

                    if (_pendingCommandLines.Count > 0)
                    {
                        AppendLinesUnsafe(_commandLines, _commandPanel, _pendingCommandLines, MaxCommandLines);
                        _pendingCommandLines.Clear();
                    }
                }
                catch (Exception ex)
                {
                    TryAppendErrorToFile($"FlushPendingOutput error: {ex}");
                }
            });
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
                            _inputEnabled = true;
                            _inputField.Enabled = true;
                            try { _mainWindow?.FocusFirst(); } catch { try { _mainWindow?.SetFocus(); } catch { } }
                            _inputField.SetFocus();

                            // Удаляем прежние артефакты с текстом разблокировки, если они попали в буферы
                            try
                            {
                                _logLines.RemoveAll(l => l != null && l.Contains("Ввод команд разблокирован"));
                                _pendingLogLines.RemoveAll(l => l != null && l.Contains("Ввод команд разблокирован"));
                                _commandLines.RemoveAll(l => l != null && l.Contains("Ввод команд разблокирован"));
                                _pendingCommandLines.RemoveAll(l => l != null && l.Contains("Ввод команд разблокирован"));
                            }
                            catch { }

                            Application.Refresh();
                        }
                    }
                    catch (Exception ex)
                    {
                        TryAppendErrorToFile($"Ошибка разблокировки ввода: {ex}");
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

                            try { _mainWindow?.FocusFirst(); } catch { try { _mainWindow?.SetFocus(); } catch { } }

                            Application.Refresh();
                        }
                    }
                    catch (Exception ex)
                    {
                        TryAppendErrorToFile($"Ошибка блокировки ввода: {ex}");
                    }
                });
            }
        }

        private bool UpdateStatusBar(MainLoop loop)
        {
            if (_isDisposed) return false;

            if (_statusBar == null) return false;

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
                try { _statusBar.Text = $" Ошибка обновления статуса: {ex.Message}"; } catch { }
            }

            return true;
        }

        private void OnInputKeyPress(KeyEventEventArgs args)
        {
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

                AddCommandOutput($"> {command}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteCommand(command);
                    }
                    catch (Exception ex)
                    {
                        TryAppendErrorToFile($"ExecuteCommand error: {ex}");
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
                        try
                        {
                            var ok = await _statusNotifier.SendAllSystemsActive("Ручная проверка систем");
                            if (ok)
                                AddCommandOutput("Статус успешно отправлен во все каналы.");
                            else
                                AddCommandOutput("Статус отправлен с ошибками. Подробности в логах.");
                        }
                        catch (Exception ex)
                        {
                            AddCommandOutput("Ошибка при отправке статуса. Смотрите логи.");
                            TryAppendErrorToFile($"ExecuteCommand announce error: {ex}");
                        }
                        break;

                    case "servers":
                        await ListServers();
                        break;

                    case "settings":
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
                                            AddCommandOutput($"swear_words: {(cfg.SwearWords != null ? string.Join(',', cfg.SwearWords) : "")} ");
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
            var formatted = FormatLogLines(message);
            if (formatted.Count == 0)
                return;

            if (_logPanel != null && Application.MainLoop != null && !_isDisposed)
            {
                Application.MainLoop.Invoke(() =>
                {
                    try
                    {
                        AppendLinesUnsafe(_logLines, _logPanel, formatted, MaxLogLines);
                    }
                    catch (Exception ex)
                    {
                        TryAppendErrorToFile($"Ошибка добавления лога: {ex}");
                    }
                });
            }
            else
            {
                _pendingLogLines.AddRange(formatted);
            }
        }

        private void AddCommandOutput(string text)
        {
            var lines = SplitToLines(text)
                .Select(NormalizePanelLine)
                .ToList();

            if (lines.Count == 0)
                return;

            if (_commandPanel != null && Application.MainLoop != null && !_isDisposed)
            {
                Application.MainLoop.Invoke(() =>
                {
                    try
                    {
                        AppendLinesUnsafe(_commandLines, _commandPanel, lines, MaxCommandLines);
                    }
                    catch (Exception ex)
                    {
                        TryAppendErrorToFile($"Ошибка добавления вывода команды: {ex}");
                    }
                });
            }
            else
            {
                _pendingCommandLines.AddRange(lines);
            }
        }

        public void ShowSystemReady(string botName, int serverCount, double initTime)
        {
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

            // Показываем только краткое уведомление о готовности консоли
            var readyShort = "Консоль готова к приёму команд. Введите 'help'";

            if (Application.MainLoop == null || _isDisposed || _logPanel == null)
            {
                _pendingLogLines.AddRange(FormatLogLines(readyShort));
                return;
            }

            Application.MainLoop.Invoke(() =>
            {
                try
                {
                    AppendLinesUnsafe(_logLines, _logPanel, FormatLogLines(readyShort), MaxLogLines);
                }
                catch (Exception ex)
                {
                    TryAppendErrorToFile($"Ошибка ShowSystemReady: {ex}");
                }
            });
        }

        public Task<bool?> AskYesNoQuestion(string question, string hint, int timeoutSeconds)
        {
            var tcs = new TaskCompletionSource<bool?>();

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

                    var yesButton = new Button("Y - Да") { X = Pos.Percent(20), Y = 4 };
                    yesButton.Clicked += () => { Application.RequestStop(dialog); tcs.TrySetResult(true); };

                    var noButton = new Button("N - Нет") { X = Pos.Percent(65), Y = 4 };
                    noButton.Clicked += () => { Application.RequestStop(dialog); tcs.TrySetResult(false); };

                    dialog.Add(questionLabel, hintLabel, yesButton, noButton);

                    noButton.SetFocus();

                    dialog.Closed += (_) =>
                    {
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
                        _errorLogLengthAtRestart = GetErrorLogLength();

                        _logLines.Clear();
                        _commandLines.Clear();
                        _logPanel?.SetNeedsDisplay();
                        _commandPanel?.SetNeedsDisplay();

                        AppendLinesUnsafe(_logLines, _logPanel, FormatLogLines("Бот перезапускается..."), MaxLogLines);
                    }
                    catch (Exception ex)
                    {
                        TryAppendErrorToFile($"Ошибка очистки UI: {ex}");
                    }
                });
            }
        }

        public void NotifyRestartCompleted(string initiator)
        {
            if (Application.MainLoop == null || _isDisposed) return;

            Application.MainLoop.Invoke(() =>
            {
                try
                {
                    // По завершении перезапуска не дублируем сообщение о завершении;
                    // показываем только новые ошибки, если они появились.
                    var newErrors = ReadErrorLogDelta();
                    if (!string.IsNullOrWhiteSpace(newErrors))
                    {
                        AppendLinesUnsafe(_logLines, _logPanel, FormatLogLines("Ошибки за время перезапуска:"), MaxLogLines);
                        AppendLinesUnsafe(_logLines, _logPanel, FormatLogLines(newErrors), MaxLogLines);
                    }
                }
                catch (Exception ex)
                {
                    TryAppendErrorToFile($"NotifyRestartCompleted error: {ex}");
                }
            });
        }

        private static IEnumerable<string> SplitToLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                yield break;

            using var sr = new StringReader(text);
            string? line;
            while ((line = sr.ReadLine()) != null)
                yield return line;
        }

        private static bool IsBoxGlyph(char c) => c is '│' or '┌' or '└' or '├' or '─' or '╔' or '╚' or '╠' or '║' or '═';

        private static string NormalizePanelLine(string line)
        {
            if (line == null)
                return string.Empty;

            line = line.Replace("\r", "").TrimEnd();
            if (line.Length == 0)
                return string.Empty;

            var trimmedStart = line.TrimStart();
            if (trimmedStart.Length == 0)
                return string.Empty;

            var first = trimmedStart[0];
            if (IsBoxGlyph(first) || first == '>')
                return trimmedStart;

            return trimmedStart;
        }

        private List<string> FormatLogLines(string message)
        {
            var now = DateTime.Now.ToString("HH:mm:ss");
            var result = new List<string>();
            foreach (var rawLine in SplitToLines(message))
            {
                var line = NormalizePanelLine(rawLine);
                if (string.IsNullOrEmpty(line))
                {
                    result.Add(string.Empty);
                    continue;
                }

                result.Add($"[{now}] {line}");
            }

            // Если сообщение без переводов строки, но пустое после нормализации
            if (result.Count == 0 && !string.IsNullOrWhiteSpace(message))
                result.Add($"[{now}] {NormalizePanelLine(message)}");

            return result;
        }

        private void AppendLinesUnsafe(List<string> buffer, TextView? panel, IEnumerable<string> lines, int maxLines)
        {
            if (lines == null)
                return;

            // If panel supports word wrap, split logical lines into display rows
            if (panel != null && panel.WordWrap)
            {
                var availWidth = Math.Max(1, panel.Bounds.Width);
                foreach (var line in lines)
                {
                    foreach (var part in WrapLineForWidth(line ?? string.Empty, availWidth))
                        buffer.Add(part);
                }
            }
            else
            {
                foreach (var line in lines)
                {
                    buffer.Add(line);
                }
            }

            if (buffer.Count > maxLines)
            {
                buffer.RemoveRange(0, buffer.Count - maxLines);
            }

            if (panel != null)
            {
                try
                {
                    // Обновим текст панели (без автоскролла через SelectedItem)
                    panel.Text = string.Join("\n", buffer);
                    var height = Math.Max(1, panel.Bounds.Height);
                    var top = Math.Max(0, buffer.Count - height);
                    try { panel.TopRow = top; } catch { }
                    panel.SetNeedsDisplay();
                }
                catch (Exception ex)
                {
                    TryAppendErrorToFile($"AppendLinesUnsafe panel update error: {ex}");
                }
            }
        }

        private void ScrollListBy(TextView? panel, List<string> buffer, int delta)
        {
            if (panel == null)
                return;
            try
            {
                var height = Math.Max(1, panel.Bounds.Height);
                var maxTop = Math.Max(0, buffer.Count - height);
                var currTop = 0;
                try { currTop = panel.TopRow; } catch { currTop = 0; }
                var nextTop = currTop + delta;
                if (nextTop < 0) nextTop = 0;
                if (nextTop > maxTop) nextTop = maxTop;

                try { panel.TopRow = nextTop; } catch { }
                panel.SetNeedsDisplay();
            }
            catch (Exception ex)
            {
                TryAppendErrorToFile($"ScrollListBy error: {ex}");
            }
        }

        private void ScrollListToBottom(TextView panel, List<string> buffer)
        {
            var height = Math.Max(1, panel.Bounds.Height);
            var maxTop = Math.Max(0, buffer.Count - height);
            var currTop = 0;
            try { currTop = panel.TopRow; } catch { currTop = 0; }
            var top = Math.Max(0, Math.Min(currTop, maxTop));
            try { panel.TopRow = top; } catch { }

            // Ensure text ends visible
            try
            {
                if (buffer.Count > 0)
                {
                    var lastRow = buffer.Count - 1;
                    try { panel.TopRow = Math.Max(0, lastRow - height + 1); } catch { }
                }
                else
                {
                    try { panel.TopRow = 0; } catch { }
                }
            }
            catch { }
        }

        private long GetErrorLogLength()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt");
                if (!File.Exists(path))
                    return 0;
                return new FileInfo(path).Length;
            }
            catch
            {
                return -1;
            }
        }

        private string ReadErrorLogDelta()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt");
                if (!File.Exists(path))
                    return string.Empty;

                var start = _errorLogLengthAtRestart;
                if (start < 0)
                    return string.Empty;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length <= start)
                    return string.Empty;

                fs.Position = start;
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void TryAppendErrorToFile(string message)
        {
            try
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "ErrorLog.txt");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
            catch { }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _isRunning = false;

            try
            {
                try { if (_inputField != null) _inputField.KeyPress -= OnInputKeyPress; } catch { }
                try { Application.RootKeyEvent -= OnRootKeyEvent; } catch { }
                try { Application.RootMouseEvent -= OnRootMouseEvent; } catch { }

                SafeShutdown();
            }
            catch { }

            try
            {
                if (_uiInitialized != null && _uiInitialized.IsSet && Application.MainLoop != null)
                {
                    try { Application.MainLoop.Invoke(() => Application.RequestStop()); } catch { }
                }
                if (_uiThread != null && _uiThread.IsAlive)
                {
                    _uiThread.Join(2000);
                }
            }
            catch { }

            _isInitialized = false;
        }
    }
}
