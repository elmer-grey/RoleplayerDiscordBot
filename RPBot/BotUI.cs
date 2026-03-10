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
        private string _tabCompletionPrefix = string.Empty;
        private string _tabCompletionSeed = string.Empty;
        private string _tabCompletionSuffix = string.Empty;
        private List<string> _tabCompletionMatches = new List<string>();
        private int _tabCompletionIndex = -1;

        // Lock to protect Terminal.Gui Init/Shutdown from concurrent calls
        private readonly object _uiLock = new object();

        // Permanent UI thread fields
        private Thread _uiThread;
        private CancellationTokenSource _uiCts;
        private ManualResetEventSlim _uiInitialized = new ManualResetEventSlim(false);
        private bool _statusTimeoutAdded = false;
        private bool _resizeTimeoutAdded = false;
        private bool _dialogInputActive = false;

        private bool _inputEnabled = false;
        private bool _isDisposed = false;

        private const int MaxLogLines = 5000;
        private const int MaxCommandLines = 2000;

        private readonly List<string> _logLines = new List<string>();
        private readonly List<string> _commandLines = new List<string>();
        // Keep original logical lines so we can re-wrap on resize
        private readonly List<string> _logLogicalLines = new List<string>();
        private readonly List<string> _commandLogicalLines = new List<string>();

        private int _lastLogWidth = -1;
        private int _lastCommandWidth = -1;
        private int _lastLogTopRow = 0;
        private int _lastCommandTopRow = 0;

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

        private void RewrapLogicalToDisplay(List<string> logical, List<string> display, int availWidth, int maxLines)
        {
            // Build full list of display rows with mapping to logical index
            var rows = new List<(int logicalIndex, string text)>();
            for (int i = 0; i < logical.Count; i++)
            {
                var l = logical[i] ?? string.Empty;
                foreach (var part in WrapLineForWidth(l, availWidth))
                {
                    rows.Add((i, part));
                }
            }
            if (rows.Count <= maxLines)
            {
                // Use all rows
                display.Clear();
                foreach (var r in rows) display.Add(r.text);
                return;
            }

            // Keep only the last maxLines display rows
            var start = rows.Count - maxLines;
            display.Clear();
            for (int i = start; i < rows.Count; i++) display.Add(rows[i].text);
            // NOTE: do not remove logical lines here — keep full logical buffer so that
            // when the window is expanded the text can reflow back to fewer wrapped lines.
        }

        private bool ResizeWatcher(MainLoop main)
        {
            if (RewrapIfNeeded())
            {
                try { Application.Refresh(); } catch { }
            }
            return true;
        }

        private bool RewrapIfNeeded()
        {
            try
            {
                var changed = false;
                if (_logPanel != null)
                {
                    var w = GetUsablePanelWidth(_logPanel);
                    if (w != _lastLogWidth)
                    {
                        var oldTop = _lastLogTopRow;
                        var oldDisplay = _logLines.ToList();
                        var oldVisible = _logPanel.Visible;

                        _lastLogWidth = w;
                        try { _logPanel.Visible = false; } catch { }
                        RewrapLogicalToDisplay(_logLogicalLines, _logLines, w, MaxLogLines);
                        _logPanel.Text = string.Join("\n", _logLines);
                        try
                        {
                            _lastLogTopRow = GetPreservedTopRow(_logPanel, oldDisplay, oldTop, _logLines);
                            _logPanel.TopRow = _lastLogTopRow;
                        }
                        catch { }
                        try { _logPanel.Visible = oldVisible; } catch { }
                        _logPanel.SetNeedsDisplay();
                        changed = true;
                    }
                }

                if (_commandPanel != null)
                {
                    var w = GetUsablePanelWidth(_commandPanel);
                    if (w != _lastCommandWidth)
                    {
                        var oldTop = _lastCommandTopRow;
                        var oldDisplay = _commandLines.ToList();
                        var oldVisible = _commandPanel.Visible;

                        _lastCommandWidth = w;
                        try { _commandPanel.Visible = false; } catch { }
                        RewrapLogicalToDisplay(_commandLogicalLines, _commandLines, w, MaxCommandLines);
                        _commandPanel.Text = string.Join("\n", _commandLines);
                        try
                        {
                            _lastCommandTopRow = GetPreservedTopRow(_commandPanel, oldDisplay, oldTop, _commandLines);
                            _commandPanel.TopRow = _lastCommandTopRow;
                        }
                        catch { }
                        try { _commandPanel.Visible = oldVisible; } catch { }
                        _commandPanel.SetNeedsDisplay();
                        changed = true;
                    }
                }

                return changed;
            }
            catch (Exception ex)
            {
                TryAppendErrorToFile($"RewrapIfNeeded error: {ex}");
                return false;
            }
        }
        private static int GetUsablePanelWidth(TextView panel)
        {
            // Небольшой запас, чтобы не упираться в правую границу TextView
            return Math.Max(1, panel.Bounds.Width - 1);
        }

        private static int GetBottomTopRow(TextView panel, IReadOnlyCollection<string> displayLines)
        {
            var height = Math.Max(1, panel.Bounds.Height);

            if (displayLines.Count <= height)
                return 0;

            return Math.Max(0, displayLines.Count - height);
        }

        private static int GetPreservedTopRow(TextView panel, IReadOnlyCollection<string> oldDisplayLines, int oldTopRow, IReadOnlyCollection<string> newDisplayLines)
        {
            var height = Math.Max(1, panel.Bounds.Height);
            var oldMaxTop = Math.Max(0, oldDisplayLines.Count - height);
            var newMaxTop = Math.Max(0, newDisplayLines.Count - height);

            if (oldTopRow >= oldMaxTop)
                return newMaxTop;

            var offsetFromBottom = oldMaxTop - Math.Max(0, oldTopRow);
            var newTop = newMaxTop - offsetFromBottom;
            if (newTop < 0) newTop = 0;
            if (newTop > newMaxTop) newTop = newMaxTop;
            return newTop;
        }

        private static IEnumerable<string> WrapLineForWidth(string line, int width)
        {
            if (string.IsNullOrEmpty(line))
            {
                yield return string.Empty;
                yield break;
            }

            width = Math.Max(1, width);

            // Для рамок/псевдографики лучше использовать жёсткий перенос
            if (line.Length > 0 && IsBoxGlyph(line[0]))
            {
                var boxRemaining = line;
                while (boxRemaining.Length > width)
                {
                    yield return boxRemaining.Substring(0, width);
                    boxRemaining = boxRemaining.Substring(width);
                }

                if (boxRemaining.Length > 0)
                    yield return boxRemaining;

                yield break;
            }

            var words = line.Split(' ');
            var current = string.Empty;

            foreach (var word in words)
            {
                if (string.IsNullOrEmpty(current))
                {
                    if (word.Length <= width)
                    {
                        current = word;
                        continue;
                    }

                    // Слишком длинное слово — режем по ширине
                    var longWord = word;
                    while (longWord.Length > width)
                    {
                        yield return longWord.Substring(0, width);
                        longWord = longWord.Substring(width);
                    }

                    current = longWord;
                    continue;
                }

                var candidate = current + " " + word;
                if (candidate.Length <= width)
                {
                    current = candidate;
                    continue;
                }

                yield return current;

                if (word.Length <= width)
                {
                    current = word;
                    continue;
                }

                var nextLongWord = word;
                while (nextLongWord.Length > width)
                {
                    yield return nextLongWord.Substring(0, width);
                    nextLongWord = nextLongWord.Substring(width);
                }

                current = nextLongWord;
            }

            if (!string.IsNullOrEmpty(current))
                yield return current;
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
							// Техническая диагностика UI: пишем в отдельный UiErrorLog_yyyyMMdd.txt
							TryAppendErrorToFile("EnsureUiInitialized: UI thread did not initialize within timeout");
						}

						_isInitialized = _uiInitialized.IsSet;
						TryAppendErrorToFile("EnsureUiInitialized: Init completed");
                }
                catch (Exception ex)
                {
						TryAppendErrorToFile($"EnsureUiInitialized error: {ex}");
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
						TryAppendErrorToFile("UI thread: Init completed");

                        // Запускаем главный цикл; управление subviews будет выполняться через MainLoop.Invoke
                        Application.Run(Application.Top);
                    }
					catch (Exception ex)
					{
						TryAppendErrorToFile($"UI thread exception: {ex}");
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
                                    _resizeTimeoutAdded = false;
                                }
									catch (Exception ex)
									{
										TryAppendErrorToFile($"SafeShutdown inner error: {ex}");
									}
                            });
                        }
						catch (Exception ex)
						{
							TryAppendErrorToFile($"SafeShutdown Invoke error: {ex}");
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
                        _resizeTimeoutAdded = false;
                    }

                    _isInitialized = false;
                }
					catch (Exception ex)
					{
						TryAppendErrorToFile($"SafeShutdown outer error: {ex}");
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
						AddCommandOutput($"super_user_role: {(cfg.SuperUserRoleId.HasValue ? cfg.SuperUserRoleId.Value.ToString() : "null")}");
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
					"welcome_message","line_message","default_role","super_user_role","swear_filter","swear_words"
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
						"super_user_role" => cfg.SuperUserRoleId.HasValue ? cfg.SuperUserRoleId.Value.ToString() : "",
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
				else if (key == "default_role" || key == "super_user_role")
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
						TryAppendErrorToFile("Warning: Application.Driver/Top not ready before Application.Run, retrying loop");

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
										TryAppendErrorToFile($"AddMainWindow error: {ex}");
									}
                            });
                        }
						catch (Exception ex)
						{
							TryAppendErrorToFile($"AddMainWindow invoke failed: {ex}");
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

						TryAppendErrorToFile($"Application.Run error: {ex}");

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
                WordWrap = false,
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
                WordWrap = false,
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

            if (!_resizeTimeoutAdded && Application.MainLoop != null)
            {
                try
                {
                    Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(16), ResizeWatcher);
                    _resizeTimeoutAdded = true;
                }
                catch { }
            }

            // Subscribe to window resize to rewrap immediately when user resizes
            try
            {
                _mainWindow.Resized += (_) =>
                {
                    try
                    {
                        RewrapIfNeeded();
                        Application.Refresh();
                    }
                    catch { }
                };
            }
            catch { }

            // Start a short-lived retry timer to attempt initial rewrap until sizes stabilize
            try
            {
                int attempts = 0;
                Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(200), (MainLoop ml) =>
                {
                    attempts++;
                    var ok = RewrapIfNeeded();
                    // stop if rewrap succeeded or after ~5 seconds (25 attempts)
                    if (ok || attempts > 25) return false;
                    return true;
                });
            }
            catch { }

            Application.RootMouseEvent += OnRootMouseEvent;

            // Ensure initial wrapping/layout is applied after window creation
            try
            {
                if (Application.MainLoop != null)
                {
                    Application.MainLoop.Invoke(() =>
                    {
                        try
                        {
                            RewrapIfNeeded();
                            Application.Refresh();
                        }
                        catch { }
                    });
                }
            }
            catch { }
        }

        private bool OnRootKeyEvent(KeyEvent keyEvent)
        {
            if (_dialogInputActive)
                return false;

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

        private static IReadOnlyList<string> GetUiRootCommandSuggestions()
        {
            return new[]
            {
                "announce",
                "exit",
                "help",
                "predict",
                "reconnect",
                "restart",
                "servers",
                "settings",
                "status",
                "stop",
                "systems"
            };
        }

        private static IReadOnlyList<string> GetUiSettingsSubcommandSuggestions()
        {
            return new[] { "get", "list", "reset", "set" };
        }

        private static IReadOnlyList<string> GetUiSettingsKeySuggestions()
        {
            return new[]
            {
                "default_role",
				"super_user_role",
                "general_rg_channel",
                "line_message",
                "moderation_channel",
                "record_channel",
                "roll_channel",
                "stats_channel",
                "swear_filter",
                "swear_words",
                "welcome_channel",
                "welcome_message"
            };
        }

        private static List<string> GetUiCommandSuggestions(string prefixContext)
        {
            var contextParts = prefixContext
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (contextParts.Length == 0)
                return GetUiRootCommandSuggestions().ToList();

            if (!string.Equals(contextParts[0], "settings", StringComparison.OrdinalIgnoreCase))
                return new List<string>();

            if (contextParts.Length == 1)
                return GetUiSettingsSubcommandSuggestions().ToList();

            var settingsSubcommand = contextParts[1].ToLowerInvariant();
            if ((settingsSubcommand == "get" || settingsSubcommand == "set") && contextParts.Length >= 3)
                return GetUiSettingsKeySuggestions().ToList();

            return new List<string>();
        }

        private void ResetTabCompletion()
        {
            _tabCompletionPrefix = string.Empty;
            _tabCompletionSeed = string.Empty;
            _tabCompletionSuffix = string.Empty;
            _tabCompletionMatches.Clear();
            _tabCompletionIndex = -1;
        }

        private bool TryCycleCommandCompletion()
        {
            var input = _inputField?.Text.ToString() ?? string.Empty;
            var endsWithSpace = input.Length > 0 && char.IsWhiteSpace(input[^1]);

            string prefix;
            string commandPart;
            if (endsWithSpace)
            {
                prefix = input;
                commandPart = string.Empty;
            }
            else
            {
                var spaceIndex = input.LastIndexOf(' ');
                prefix = spaceIndex >= 0 ? input[..(spaceIndex + 1)] : string.Empty;
                commandPart = spaceIndex >= 0 ? input[(spaceIndex + 1)..] : input;
            }

            if (string.IsNullOrWhiteSpace(commandPart) && string.IsNullOrWhiteSpace(prefix))
                return false;

            var canContinueCycle = _tabCompletionIndex >= 0
                && _tabCompletionPrefix == prefix
                && (string.Equals(_tabCompletionPrefix + _tabCompletionSeed, input, StringComparison.OrdinalIgnoreCase)
                    || _tabCompletionMatches.Any(x => string.Equals(_tabCompletionPrefix + x, input, StringComparison.OrdinalIgnoreCase)));

            if (!canContinueCycle)
            {
                var suggestionSource = prefix.Length == 0
                    ? GetUiRootCommandSuggestions().ToList()
                    : GetUiCommandSuggestions(prefix);

                _tabCompletionSeed = commandPart;
                _tabCompletionPrefix = prefix;
                _tabCompletionSuffix = string.Empty;
                _tabCompletionMatches = suggestionSource
                    .Where(x => x.StartsWith(commandPart, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (_tabCompletionMatches.Count == 0)
                {
                    ResetTabCompletion();
                    return false;
                }

                _tabCompletionIndex = 0;
            }
            else
            {
                _tabCompletionIndex++;
                if (_tabCompletionIndex > _tabCompletionMatches.Count)
                    _tabCompletionIndex = 0;
            }

            var replacement = _tabCompletionIndex == _tabCompletionMatches.Count
                ? _tabCompletionSeed
                : _tabCompletionMatches[_tabCompletionIndex];

            _inputField.Text = _tabCompletionPrefix + replacement;
            return true;
        }

        private void OnInputKeyPress(KeyEventEventArgs args)
        {
            if (!_inputEnabled)
            {
                args.Handled = true;
                return;
            }

            if (args.KeyEvent.Key == Key.Tab)
            {
                args.Handled = TryCycleCommandCompletion();
                return;
            }

            ResetTabCompletion();

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
                    finally
                    {
                        AddCommandSpacer();
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

                    case "help":
                        ShowHelp();
                        break;

                    case "predict":
                        var prediction = await _connectionPredictor.AnalyzeAndPredict();
                        if (prediction != null)
                        {
                            AddCommandOutput($"[ПРОГНОЗ] {prediction.Reason} в {prediction.PredictedTime:HH:mm:ss} (уверенность: {prediction.Confidence}%)");
                        }
                        else
                        {
                            AddCommandOutput("[ПРОГНОЗ] Прогнозов нет, соединение стабильно");
                        }
                        break;

                    case "reconnect":
                        AddCommandOutput("Принудительный реконнект...");
                        try
                        {
                            if (_client.ConnectionState == Discord.ConnectionState.Connected)
                                await _statusNotifier.SendReconnectNotification("Ручной реконнект из UI");
                        }
                        catch (Exception ex)
                        {
                            TryAppendErrorToFile($"ExecuteCommand reconnect notification error: {ex}");
                        }
                        await _reconnectionService.RequestManualReconnectAsync("Ручной реконнект из UI");
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

                    default:
                        AddCommandOutput($"Неизвестная команда: {cmd}. Введите 'help' для списка команд.");
                        break;

                }
            }
            catch (Exception ex)
            {
                AddCommandOutput($"Ошибка: {ex.Message}");
            }
        }

        private void AddCommandSpacer()
        {
            if (_commandPanel != null && Application.MainLoop != null && !_isDisposed)
            {
                Application.MainLoop.Invoke(() =>
                {
                    try
                    {
                        AppendLinesUnsafe(_commandLines, _commandPanel, new[] { string.Empty }, MaxCommandLines);
                    }
                    catch (Exception ex)
                    {
                        TryAppendErrorToFile($"Ошибка добавления отступа вывода команды: {ex}");
                    }
                });
            }
            else
            {
                _pendingCommandLines.Add(string.Empty);
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
                    _dialogInputActive = true;

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

                    void ConfirmYes()
                    {
                        if (tcs.Task.IsCompleted)
                            return;

                        Application.RequestStop(dialog);
                        tcs.TrySetResult(true);
                    }

                    void ConfirmNo()
                    {
                        if (tcs.Task.IsCompleted)
                            return;

                        Application.RequestStop(dialog);
                        tcs.TrySetResult(false);
                    }

                    var yesButton = new Button("Y - Да") { X = Pos.Percent(20), Y = 4 };
                    yesButton.Clicked += ConfirmYes;

                    var noButton = new Button("N - Нет") { X = Pos.Percent(65), Y = 4 };
                    noButton.Clicked += ConfirmNo;

                    dialog.Add(questionLabel, hintLabel, yesButton, noButton);

                    void HandleDialogKey(KeyEventEventArgs args)
                    {
                        try
                        {
                            var key = args.KeyEvent.Key;
                            var keyChar = char.ToLowerInvariant((char)(uint)key);

                            if (keyChar == 'y' || keyChar == 'н')
                            {
                                ConfirmYes();
                                args.Handled = true;
                                return;
                            }

                            if (keyChar == 'n' || keyChar == 'т')
                            {
                                ConfirmNo();
                                args.Handled = true;
                                return;
                            }

                            switch (key)
                            {
                                case Key.Esc:
                                    ConfirmNo();
                                    args.Handled = true;
                                    break;
                                case Key.CursorLeft:
                                case Key.CursorUp:
                                    yesButton.SetFocus();
                                    args.Handled = true;
                                    break;
                                case Key.CursorRight:
                                case Key.CursorDown:
                                    noButton.SetFocus();
                                    args.Handled = true;
                                    break;
                                case Key.Enter:
                                    if (yesButton.HasFocus)
                                        ConfirmYes();
                                    else
                                        ConfirmNo();
                                    args.Handled = true;
                                    break;
                            }
                        }
                        catch { }
                    }

                    dialog.KeyPress += HandleDialogKey;
                    yesButton.KeyPress += HandleDialogKey;
                    noButton.KeyPress += HandleDialogKey;

                    noButton.SetFocus();

                    dialog.Closed += (_) =>
                    {
                        _dialogInputActive = false;
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
                                    ConfirmNo();
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
                    _dialogInputActive = false;
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
            AddCommandOutput("reconnect - Принудительный реконнект");
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
                        _logLogicalLines.Clear();
                        _commandLogicalLines.Clear();
                        _lastLogTopRow = 0;
                        _lastCommandTopRow = 0;
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

            if (panel != null)
            {
                try
                {
                    if (ReferenceEquals(panel, _logPanel) || ReferenceEquals(panel, _commandPanel))
                    {
                        List<string> logical = ReferenceEquals(panel, _logPanel) ? _logLogicalLines : _commandLogicalLines;

                        foreach (var line in lines)
                        {
                            logical.Add(line ?? string.Empty);
                        }

                        if (logical.Count > maxLines)
                        {
                            logical.RemoveRange(0, logical.Count - maxLines);
                        }

                        RewrapLogicalToDisplay(logical, buffer, GetUsablePanelWidth(panel), maxLines);
                        panel.Text = string.Join("\n", buffer);

                        var height = Math.Max(1, panel.Bounds.Height);
                        var top = buffer.Count <= height ? 0 : GetBottomTopRow(panel, buffer);
                        try
                        {
                            if (ReferenceEquals(panel, _logPanel))
                                _lastLogTopRow = top;
                            else if (ReferenceEquals(panel, _commandPanel))
                                _lastCommandTopRow = top;

                            panel.TopRow = top;
                        }
                        catch { }
                    }
                    else
                    {
                        foreach (var line in lines)
                        {
                            buffer.Add(line ?? string.Empty);
                        }

                        if (buffer.Count > maxLines)
                        {
                            buffer.RemoveRange(0, buffer.Count - maxLines);
                        }

                        panel.Text = string.Join("\n", buffer);
                    }

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
                var currTop = 0;
                try { currTop = panel.TopRow; } catch { currTop = 0; }
                var nextTop = currTop + delta;
                if (nextTop < 0) nextTop = 0;

                if (ReferenceEquals(panel, _logPanel))
                {
                    var maxTop = GetBottomTopRow(panel, _logLines);
                    if (nextTop > maxTop) nextTop = maxTop;
                    _lastLogTopRow = nextTop;
                }
                else if (ReferenceEquals(panel, _commandPanel))
                {
                    var maxTop = GetBottomTopRow(panel, _commandLines);
                    if (nextTop > maxTop) nextTop = maxTop;
                    _lastCommandTopRow = nextTop;
                }

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

		// Путь к основному ErrorLog (внутренние ошибки бота), который отображается в UI.
		// Используем тот же шаблон, что и Program.LogError: ErrorLog_yyyyMMdd.txt в LogDirectory.
		private string GetErrorLogPath()
		{
			var logDir = BotConfig.Current?.LogDirectory;
			var resolvedLogDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDir) ? "Logs" : logDir);
			Directory.CreateDirectory(resolvedLogDir);
			var dateSuffix = DateTime.Now.ToString("yyyyMMdd");
			return Path.Combine(resolvedLogDir, $"ErrorLog_{dateSuffix}.txt");
		}

		private long GetErrorLogLength()
        {
            try
            {
				var path = GetErrorLogPath();
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
				var path = GetErrorLogPath();
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
				var logPath = GetUiErrorLogPath();
				File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
            catch { }
        }

		private static string GetUiErrorLogPath()
		{
			var logDir = BotConfig.Current?.LogDirectory;
			var resolvedLogDir = BotConfig.ResolvePath(string.IsNullOrWhiteSpace(logDir) ? "Logs" : logDir);
			Directory.CreateDirectory(resolvedLogDir);
			// Отдельный лог ошибок UI по дням: UiErrorLog_yyyyMMdd.txt
			var dateSuffix = DateTime.Now.ToString("yyyyMMdd");
			return Path.Combine(resolvedLogDir, $"UiErrorLog_{dateSuffix}.txt");
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
