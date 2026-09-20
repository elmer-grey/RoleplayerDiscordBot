using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Discord.WebSocket;

namespace RPBot.Web
{
    public sealed record ActivityBucket(DateTimeOffset Minute, int Count);

    /// <summary>
    /// Помощник для регистрации URL-префикса HttpListener в Windows через
    /// <c>netsh http add urlacl</c>. Без этого вызов <c>HttpListener.Start()</c>
    /// на не-локальных URL (или на любом URL, если у пользователя нет
    /// администраторских прав) падает с <c>HttpListenerException (503)</c>.
    /// </summary>
    /// <remarks>
    /// <para>На Windows для запуска <c>netsh</c> обычно нужны права
    /// администратора. Поэтому мы запускаем команду через <c>cmd /c start</c>:
    /// сама команда открывает короткий чёрный терминал, выполняется и
    /// закрывается, а основной процесс бота продолжает жить.</para>
    ///
    /// <para>Если регистрация уже есть — ничего не делаем (это узнаём из
    /// <c>netsh http show urlacl</c>).</para>
    ///
    /// <para>На не-Windows платформах это no-op.</para>
    /// </remarks>
    internal static class UrlAclBootstrap
        {
            // Кэш по полному prefix (а не по порту): для одного порта может быть
            // несколько разных IP (192.168.x.x, 10.x.x.x, 100.x.x.x, [::1] и т.д.),
            // у каждого свой urlacl. Кэшируем ТОЛЬКО конкретный prefix, для которого
            // уже подтвердили регистрацию. Иначе мигающие netsh при рестарте.
            private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _prefixVerified = new(System.StringComparer.Ordinal);

        /// <summary>
        /// Если URL ещё не зарегистрирован — пробует выполнить
        /// <c>netsh http add urlacl url=... user=Everyone</c> в отдельном
        /// короткоживущем окне cmd.
        /// </summary>
        /// <returns>
        /// true, если после выхода URL уже зарегистрирован (был или только что добавлен).
        /// false, если регистрация не удалась (нет прав / нет netsh / прочая ошибка).
        /// </returns>
        public static bool TryEnsureRegistered(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                BotLogger.Warn(LogCategory.System, "[UrlAclBootstrap] prefix пуст — пропуск");
                return false;
            }
            if (!OperatingSystem.IsWindows())
            {
                BotLogger.Info(LogCategory.System, "[UrlAclBootstrap] не Windows — пропуск netsh");
                return false;
            }

            BotLogger.Info(LogCategory.System, $"[UrlAclBootstrap] проверка регистрации {prefix}");
                        if (_prefixVerified.TryGetValue(prefix, out var verified) && verified)
                        {
                            BotLogger.Info(LogCategory.System, $"[UrlAclBootstrap] {prefix} уже подтверждён ранее в этой сессии — пропускаю netsh show");
                            return true;
                        }
                        bool alreadyRegistered = IsRegistered(prefix);
                        if (alreadyRegistered) _prefixVerified[prefix] = true;
            BotLogger.Info(LogCategory.System, $"[UrlAclBootstrap] IsRegistered({prefix}) = {alreadyRegistered}");
            if (alreadyRegistered)
            {
                BotLogger.Info(LogCategory.System, $"[UrlAclBootstrap] {prefix} уже зарегистрирован");
                return true;
            }

            // На доменных аккаунтах `Everyone` часто не резолвится → SDDL не создаётся
            // (Error 183/1332). Используем конкретного текущего пользователя. Если и это
                        // не сработает — fallback на SDDL "D:(A;;GA;;;WD)" (Allow Generic All для
            // World Domain / Everyone) — это работает на любой Windows-машине без
                        // необходимости резолвить доменный аккаунт, и — главное — урлпреджные правила
                        // с GA покрывают HttpListener-префиксы с конкретным IP. Только GX недостаточно
                        // для HttpListener.Start() на concrete-IP префиксах.
                        string aclUser = Environment.GetEnvironmentVariable("USERNAME") ?? "Everyone";
                        const string sddlEveryone = "D:(A;;GA;;;WD)";
                        bool ok = TryNetshAdd(psi => $"netsh http add urlacl url={prefix} user={aclUser}", prefix, aclUser);
                        if (!ok)
                        {
                            BotLogger.Info(LogCategory.System,
                                $"[UrlAclBootstrap] user={aclUser} не сработал, fallback на SDDL '{sddlEveryone}'");
                            ok = TryNetshAdd(psi => $"netsh http add urlacl url={prefix} sddl=\"{sddlEveryone}\"", prefix, "SDDL(WD)");
                        }

                        return ok;
                    }

        private static bool TryNetshAdd(Func<string, string> commandForArg, string prefix, string label)
        {
            try
            {
                BotLogger.Info(LogCategory.System,
                    $"[UrlAclBootstrap] {prefix} НЕ зарегистрирован, пробую ({label}): {commandForArg(prefix)}");
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start \"\" cmd /c \"{commandForArg(prefix)}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    Verb = "runas",
                };
                using var p = Process.Start(psi);
                if (p == null)
                {
                    BotLogger.Warn(LogCategory.System, "[UrlAclBootstrap] Process.Start вернул null (UAC отклонён или нет шелла)");
                    return false;
                }
                BotLogger.Info(LogCategory.System, "[UrlAclBootstrap] ожидаю завершения UAC/netsh (до 5с)...");
                if (!p.WaitForExit(5000))
                {
                    BotLogger.Warn(LogCategory.System, "[UrlAclBootstrap] таймаут 5с на netsh/UAC — продолжаю без повышения");
                    return false;
                }
                BotLogger.Info(LogCategory.System, $"[UrlAclBootstrap] UAC-процесс завершился, ExitCode={p.ExitCode}");
            }
            catch (Exception ex)
            {
                BotLogger.Warn(LogCategory.System,
                    $"[UrlAclBootstrap] исключение при запуске netsh: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            bool finalRegistered = IsRegistered(prefix);
                        BotLogger.Info(LogCategory.System,
                            $"[UrlAclBootstrap] пост-проверка IsRegistered({prefix}) = {finalRegistered}");
                        if (finalRegistered) _prefixVerified[prefix] = true;
                        return finalRegistered;
                    }

        private static int ExtractPort(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return -1;
            int schemeEnd = prefix.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) return -1;
            var body = prefix[(schemeEnd + 3)..];
            int colonIdx = body.LastIndexOf(':');
            if (colonIdx < 0) return -1;
            return int.TryParse(body[(colonIdx + 1)..], out var p) ? p : -1;
        }

                // Собираем все адреса, на которые HttpListener реально может забиндиться
                // (конкретные IP локальных интерфейсов). Wildcard-префиксы ('+', '*', '0.0.0.0')
                // .NET 8 HttpListener НЕ принимает — Start() падает с ErrorCode=50.
                public static HashSet<string> EnumerateBindableAddresses()
                {
                    var result = new HashSet<string>(StringComparer.Ordinal);
                    try
                    {
                        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                            // пропускаем loopback — отдельно добавим 127.0.0.1 и [::1]
                            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                            var props = ni.GetIPProperties();
                            foreach (var ua in props.UnicastAddresses)
                            {
                                if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
                                    ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) continue;
                                // link-local IPv6 ([fe80::..]) HttpListener принимает только при наличии scope id
                                if (ua.Address.IsIPv6LinkLocal) continue;
                                var ip = ua.Address.ToString();
                                // отбрасываем IPv4 link-local (169.254.x.x) — Bluetooth / WinRM / Wi-Fi-Direct,
                                // не маршрутизируется, мусор в urlacl.
                                if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                                result.Add(ip);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Warn(LogCategory.System,
                            $"[UrlAclBootstrap] EnumerateBindableAddresses: {ex.GetType().Name}: {ex.Message}");
                    }
                    // loopback
                    result.Add("127.0.0.1");
                    result.Add("::1");
                    return result;
                }

        private static bool IsRegistered(string prefix)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "http show urlacl",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null)
                {
                    BotLogger.Warn(LogCategory.System, "[UrlAclBootstrap] IsRegistered: Process.Start(netsh) вернул null");
                    return false;
                }
                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(2000);
                // netsh выводит URL в формате:    URL : http://127.0.0.1:5057/
                // Нам важно знать: покрыта ли записью urlacl пара host:port нашего prefix.
                                //
                                // Поведение Windows urlacl vs HttpListener:
                                //   • Строгий матч host:port       — покрывает (если SDDL даёт GA для бота).
                                //   • Weak wildcard '*' в urlacl   — НЕ покрывает concrete-IP префикс HttpListener.
                                //                                    Раньше я считал, что покрывает — НЕВЕРНО,
                                //                                    отсюда ложные found=True без реальной записи.
                                //   • Strong wildcard '+' в urlacl — тоже НЕ покрывает concrete-IP (НЕ tested by us).
                                //                                    HttpListener хочет либо exact-match host, либо
                                //                                    собственный +/'*' на своей стороне (но .NET 8
                                //                                    Start() для +/'*' отдаёт ErrorCode=50).
                                //
                                // Значит: считаем prefix покрытым ТОЛЬКО если в urlacl есть строгий матч
                                // host:port для нашего prefix. Wildcard-записи игнорируем.
                                var needle = prefix.TrimEnd('/');
                                string needleHost = "", needlePort = "";
                                int nSchemeEnd = needle.IndexOf("://", StringComparison.Ordinal);
                                if (nSchemeEnd >= 0)
                {
                                    var nBody = needle[(nSchemeEnd + 3)..];
                                    int nColon = nBody.LastIndexOf(':');
                                    if (nColon >= 0)
                                    {
                                        needleHost = nBody[..nColon];
                                        needlePort = nBody[(nColon + 1)..];
                                    }
                                }
                                bool found = false;
                                foreach (var rawLine in stdout.Split('\n'))
                                {
                                    var line = rawLine.Trim();
                                    var urlIdx = line.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
                                    if (urlIdx < 0) continue;
                                    var rest = line[urlIdx..];
                                    int spaceIdx = rest.IndexOfAny(new[] { ' ', '\t' }, "http://".Length);
                                    var registered = (spaceIdx >= 0 ? rest[..spaceIdx] : rest).TrimEnd('/');
                                    if (!registered.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) continue;
                                    var regBody = registered["http://".Length..];
                                    int colonIdx = regBody.LastIndexOf(':');
                                    if (colonIdx < 0) continue;
                                    var regHost = regBody[..colonIdx];
                                    var regPort = regBody[(colonIdx + 1)..];
                                    if (!regPort.Equals(needlePort, StringComparison.OrdinalIgnoreCase)) continue;
                                    // ТОЛЬКО strict match. Wildcard-host в urlacl не покрывает конкретный IP
                                    // префикс HttpListener (доказано на netsh-выводе с ложным found=True).
                                    if (regHost.Equals(needleHost, StringComparison.OrdinalIgnoreCase))
                                    {
                                        found = true;
                                        break;
                                    }
                                }
                BotLogger.Info(LogCategory.System,
                    $"[UrlAclBootstrap] IsRegistered: needle='{needle}' (host={needleHost}, port={needlePort}) found={found} exit={p.ExitCode} stderr='{stderr.Trim()}'");
                return found;
            }
            catch (Exception ex)
            {
                BotLogger.Warn(LogCategory.System,
                    $"[UrlAclBootstrap] IsRegistered: исключение {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    public sealed class WebDashboardService : IDisposable
    {
        private readonly string _prefix;
        private readonly List<string> _prefixes = new();
        private readonly Func<object> _healthProvider;
        private readonly Func<IReadOnlyDictionary<ulong, ServerConfig>> _serverConfigsProvider;
        private readonly Func<object> _sessionsProvider;
        private readonly Func<object> _eventsProvider;
        private readonly Func<DiscordSocketClient?> _clientProvider;
        private readonly Func<int> _rollsTodayProvider;
        private readonly Func<int> _activeSessionsProvider;
        private readonly Func<int> _chatMessagesTodayProvider;
        private readonly Func<int> _usersInVoiceProvider;
        private readonly Func<IReadOnlyList<ActivityBucket>> _activityProvider;
        private readonly Func<string> _versionProvider;
        private readonly Func<TimeSpan> _uptimeProvider;
                private readonly Func<object> _systemsProvider;
                private readonly HttpListener _listener = new();
        private readonly object _logsLock = new();
        private readonly LinkedList<BotLogRecord> _logs = new();
        private readonly int _maxLogs;
        private readonly object _rateLimitLock = new();
        private readonly Dictionary<string, RateLimitBucket> _rateLimitBuckets = new(StringComparer.Ordinal);
        private readonly int _rateLimitPerMinute;
        private CancellationTokenSource? _cts;
                private Task? _loopTask;
                private static string? _dashboardHtmlCache;

        public WebDashboardService(
            string host,
            int port,
            Func<object> healthProvider,
            Func<IReadOnlyDictionary<ulong, ServerConfig>> serverConfigsProvider,
            Func<object> sessionsProvider,
                        Func<object> eventsProvider,
                        Func<DiscordSocketClient?> clientProvider,
                        Func<int> rollsTodayProvider,
                        Func<int> activeSessionsProvider,
                        Func<int> chatMessagesTodayProvider,
                        Func<int> usersInVoiceProvider,
                        Func<IReadOnlyList<ActivityBucket>> activityProvider,
                        Func<string> versionProvider,
                        Func<TimeSpan> uptimeProvider,
                        Func<object> systemsProvider,
                        int maxLogs = 1000)
                                {
                                    // HttpListener на .NET 8 принимает префиксы в формате:
                                    //   http://+:PORT/        — strong wildcard (все IPv4), ТРЕБУЕТ admin
                                    //                      или urlacl с правом Register (GA) для текущего юзера.
                                    //   http://[::]:PORT/     — IPv6 any
                                    //   http://localhost:PORT/
                                    //   http://<конкретный-IP>:PORT/
                                    // Префикс http://0.0.0.0:PORT/ Start() отвергает (ErrorCode=50).
                                    //
                                    // Для dual-stack на .NET 8 Windows:
                                    //   http://+:PORT/   — покрывает все IPv4 (включая Tailscale 100.x.x.x),
                                    //                      если urlacl содержит GA (Generic All) для текущего
                                    //                      пользователя или Everyone.
                                    //   http://[::]:PORT/ — IPv6 any, проходит с urlacl GX.
                                    //
                                    // Urlacl-запись http://*:PORT/ (weak wildcard) покрывает оба префикса.
                                    if (string.IsNullOrEmpty(host) || host == "0.0.0.0" || host == "*")
                                                                        {
                                                                            // dual-stack: IPv4 + IPv6 всех интерфейсов.
                                                                            // .NET 8 HttpListener НЕ принимает "http://+:PORT/" и "http://0.0.0.0:PORT/"
                                                                            // (Start() → ErrorCode=50 "Такой запрос не поддерживается").
                                                                            // Принимает только конкретные хосты: "http://<ip>:PORT/".
                                                                            //
                                                                            // Urlacl "http://*:PORT/ sddl=D:(A;;GA;;;WD)" покрывает ЛЮБОЙ из них.
                                                                            // HttpListener биндит каждый префикс отдельным сокетом (как Listener.exe).
                                                                            _prefixes.Clear();
                                                                            var addresses = UrlAclBootstrap.EnumerateBindableAddresses();
                                                                            foreach (var ip in addresses)
                                                                            {
                                                                                if (ip.Contains(':'))
                                                                                    _prefixes.Add($"http://[{ip}]:{port}/");
                                                                                else
                                                                                    _prefixes.Add($"http://{ip}:{port}/");
                                                                            }
                                                                            if (_prefixes.Count == 0)
                                                                            {
                                                                                // fallback на loopback
                                                                                _prefixes.Add($"http://127.0.0.1:{port}/");
                                                                                _prefixes.Add($"http://[::1]:{port}/");
                                                                            }
                                                                            _prefix = string.Join(", ", _prefixes);
                                                                        }
                                    else
                                    {
                                        _prefix = $"http://{host}:{port}/";
                                        _prefixes.Clear();
                                        _prefixes.Add(_prefix);
                                    }
                                    _healthProvider = healthProvider;
                                    _serverConfigsProvider = serverConfigsProvider;
                                    _sessionsProvider = sessionsProvider;
                                    _eventsProvider = eventsProvider ?? (() => Array.Empty<object>());
                                    _clientProvider = clientProvider ?? (() => null);
                                    _rollsTodayProvider = rollsTodayProvider;
            _activeSessionsProvider = activeSessionsProvider;
            _chatMessagesTodayProvider = chatMessagesTodayProvider;
            _usersInVoiceProvider = usersInVoiceProvider;
            _activityProvider = activityProvider;
            _versionProvider = versionProvider;
            _uptimeProvider = uptimeProvider;
                        _systemsProvider = systemsProvider ?? (() => Array.Empty<object>());
                        _maxLogs = Math.Max(100, maxLogs);
            // Лимит нужен, чтобы случайный скрипт/краулер не положил дашборд.
            // Один цикл автообновления (5 с) тащит 6 эндпоинтов → ≈ 72 req/min.
            // 240 req/min даёт 4-кратный запас на параллельные вкладки и редкие бурсты.
            // Раньше было 60 req/min — перекрывалось даже одиночным открытием дашборда.
            _rateLimitPerMinute = 240;
            BotLogger.Info(LogCategory.System, $"[WebDashboard] конструктор завершён: prefix={_prefix}, rateLimit={_rateLimitPerMinute}/min");
        }

        // Состояние «хвостового» чтения. Подписываемся не на observer (он может
                // пропустить запись при исключении/гонке), а на хвост run.log — там
                // гарантированно всё (стартап + рантайм), порядок честный.
                private Guid _tailObserverId;
                private bool _tailRegistered;
                private readonly object _tailInitLock = new();

                public void Start()
                {
                    BotLogger.Info(LogCategory.System, $"[WebDashboard] Start() вызван, prefix={_prefix}, OS=Windows={OperatingSystem.IsWindows()}");
                    if (_cts != null)
                    {
                        BotLogger.Warn(LogCategory.System, "[WebDashboard] Start(): _cts уже установлен — повторный вызов игнорируется");
                        return;
                    }

                    // Превентивная регистрация URL в Windows urlacl — иначе HttpListener.Start()
                    // падает с HttpListenerException (503) на не-локальных префиксах и у обычных
                    // пользователей. Пробуем один раз, без всплытия UAC, если уже зарегистрировано.
                    if (OperatingSystem.IsWindows())
                    {
                        // Для dual-stack (IPv4+IPv6) регистрируем все префиксы —
                        // urlacl-запись http://*:PORT/ покрывает любой из них.
                        foreach (var p in _prefixes)
                        {
                            BotLogger.Info(LogCategory.System, "[WebDashboard] вызываю UrlAclBootstrap.TryEnsureRegistered");
                            bool aclOk = UrlAclBootstrap.TryEnsureRegistered(p);
                            BotLogger.Info(LogCategory.System, $"[WebDashboard] UrlAclBootstrap.TryEnsureRegistered → {aclOk}");
                            if (!aclOk)
                            {
                                BotLogger.Warn(LogCategory.System,
                                    $"[WebDashboard] Не удалось зарегистрировать {p} в urlacl автоматически. " +
                                    $"Запустите от администратора: netsh http add urlacl url={p} user=Everyone");
                            }
                        }
                    }
                    else
                    {
                        BotLogger.Info(LogCategory.System, "[WebDashboard] не Windows — пропускаю UrlAclBootstrap");
                    }

                    var cts = new CancellationTokenSource();
                    _cts = cts;
                    try
                    {
                        foreach (var p in _prefixes)
                        {
                            BotLogger.Info(LogCategory.System, $"[WebDashboard] добавляю префикс {p} в HttpListener");
                            _listener.Prefixes.Add(p);
                        }
            }
                    catch (ObjectDisposedException ex)
            {
                        // HttpListener в редких случаях оказывается disposed до первого Start
                        // (например, после неудачного предыдущего запуска). Это не наша ошибка —
                        // просто выходим, и пусть следующий restart цикл создаст свежий инстанс.
                        _cts = null;
                        BotLogger.Error(LogCategory.System,
                            $"[WebDashboard] HttpListener уже disposed до старта на {_prefix}: {ex.Message}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _cts = null;
                        BotLogger.Error(LogCategory.System,
                            $"[WebDashboard] исключение при добавлении префикса {_prefix}: {ex.GetType().Name}: {ex.Message}");
                        return;
                    }

                    try
                    {
                        BotLogger.Info(LogCategory.System, $"[WebDashboard] HttpListener.Start() для {string.Join(", ", _prefixes)}");
                        _listener.Start();
                        BotLogger.Info(LogCategory.System, $"[WebDashboard] HttpListener.Start() успешно для {string.Join(", ", _prefixes)}");
                    }
                    catch (HttpListenerException ex)
                    {
                        _cts = null;
                        SafeRemovePrefix();
                        BotLogger.Error(LogCategory.System,
                            $"[WebDashboard] Не удалось запустить HTTP-сервер на {_prefix}: {ex.Message} (ErrorCode={ex.ErrorCode})");
                        BotLogger.Warn(LogCategory.System,
                            $"[WebDashboard] На Windows может потребоваться регистрация URL: " +
                            $"netsh http add urlacl url={_prefix} user=Everyone");
                        return;
                    }
                    catch (ObjectDisposedException ex)
                    {
                        // HttpListener на Windows после неудачного Start() иногда переходит
                        // в disposed-состояние — тогда геттер Prefixes тоже бросает.
                        // Ловим здесь, чтобы бот не валился с непонятным стектрейсом.
                        _cts = null;
                        BotLogger.Error(LogCategory.System,
                            $"[WebDashboard] HttpListener disposed во время Start на {_prefix}: {ex.Message}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _cts = null;
                        SafeRemovePrefix();
                        BotLogger.Error(LogCategory.System, $"[WebDashboard] Ошибка запуска: {ex.GetType().Name}: {ex.Message}");
                        BotLogger.Error(LogCategory.System, $"[WebDashboard] Stack: {ex.StackTrace}");
                        return;
                    }

            // Подписка напрямую на observer BotLogger. Это надёжнее, чем хвост
                        // из run.log: гарантированно получаем все записи, прошедшие через Write,
                        // без парсера и без гонки со смещением файла.
            // На рестарте мы уже были подписаны — отписываем старый observer
            // и подписываемся заново, чтобы _logs был очищен для новой сессии
            // (а не накапливал записи прошлого запуска).
            lock (_tailInitLock)
            {
                if (_tailRegistered)
                {
                    try { BotLogger.UnregisterObserver(_tailObserverId); } catch { }
                    _tailRegistered = false;
                }
                lock (_logsLock)
                                {
                                    _logs.Clear();
                                }
                                _tailObserverId = BotLogger.RegisterObserver(OnLog);
                                _tailRegistered = true;
                                BotLogger.Info(LogCategory.System, $"[WebDashboard] Start observer={_tailObserverId}");
            }
            _loopTask = Task.Run(() => AcceptLoopAsync(cts.Token));
            BotLogger.Info(LogCategory.System, $"[WebDashboard] Запущен на {string.Join(", ", _prefixes)} (loopTask={_loopTask?.Id})");
        }

        public async Task StopAsync()
        {
            BotLogger.Info(LogCategory.System, $"[WebDashboard] StopAsync вызван");
                    // Идемпотентность — повторный вызов из GracefulShutdownAsync + DisposeAsync
                    // не должен ронять _listener.Close() на disposed объекте.
                    var cts = Interlocked.Exchange(ref _cts, null);
                    if (cts == null)
                    {
                        BotLogger.Info(LogCategory.System, "[WebDashboard] StopAsync: уже остановлен");
                        return;
                    }

                    try { cts.Cancel(); BotLogger.Info(LogCategory.System, "[WebDashboard] StopAsync: cts.Cancel()"); } catch (Exception ex) { BotLogger.Warn(LogCategory.System, $"[WebDashboard] StopAsync: cts.Cancel ex: {ex.Message}"); }
                    try { _listener.Stop(); BotLogger.Info(LogCategory.System, "[WebDashboard] StopAsync: _listener.Stop()"); } catch (Exception ex) { BotLogger.Warn(LogCategory.System, $"[WebDashboard] StopAsync: _listener.Stop ex: {ex.Message}"); }
                    try { _listener.Close(); BotLogger.Info(LogCategory.System, "[WebDashboard] StopAsync: _listener.Close()"); } catch (Exception ex) { BotLogger.Warn(LogCategory.System, $"[WebDashboard] StopAsync: _listener.Close ex: {ex.Message}"); }
                    lock (_tailInitLock)
                    {
                        if (_tailRegistered)
                        {
                            try { BotLogger.UnregisterObserver(_tailObserverId); } catch { }
                            _tailRegistered = false;
                        }
                    }

                    if (_loopTask != null)
                    {
                        try { await _loopTask.ConfigureAwait(false); } catch { }
                        _loopTask = null;
                    }

                    try { cts.Dispose(); } catch { }
                }

        /// <summary>
        /// Безопасно убрать префикс URL из HttpListener. Геттер <c>Prefixes</c>
        /// бросает <see cref="ObjectDisposedException"/>, если listener уже
        /// disposed — это нормальное состояние после неудачного Start/Stop,
        /// игнорируем.
        /// </summary>
        private void SafeRemovePrefix()
        {
            foreach (var p in _prefixes)
            {
                try
                {
                    _listener.Prefixes.Remove(p);
                }
                catch (ObjectDisposedException)
                {
                    // Listener уже закрыт — префикс точно не активен.
                }
            }
        }

        public int RateLimitPerMinute => _rateLimitPerMinute;

        public IReadOnlyDictionary<string, (DateTimeOffset WindowStart, int Count)> GetRateLimitSnapshot()
        {
            lock (_rateLimitLock)
            {
                return _rateLimitBuckets.ToDictionary(kv => kv.Key, kv => (kv.Value.WindowStart, kv.Value.Count));
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            BotLogger.Info(LogCategory.System, $"[WebDashboard] AcceptLoopAsync стартовал, ожидаю подключения на {string.Join(", ", _prefixes)}");
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequestAsync(context, token), token);
                }
                catch (HttpListenerException ex)
                {
                    if (token.IsCancellationRequested)
                    {
                        BotLogger.Info(LogCategory.System, "[WebDashboard] AcceptLoopAsync: токен отменён, выхожу");
                        return;
                    }
                    BotLogger.Warn(LogCategory.System,
                        $"[WebDashboard] AcceptLoop: HttpListenerException {ex.ErrorCode}: {ex.Message}");
                }
                catch (ObjectDisposedException)
                {
                    BotLogger.Warn(LogCategory.System, "[WebDashboard] AcceptLoopAsync: listener disposed, выхожу");
                    return;
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.System,
                        $"[WebDashboard] Ошибка цикла HTTP: {ex.GetType().Name}: {ex.Message}");
                }
            }
            BotLogger.Info(LogCategory.System, "[WebDashboard] AcceptLoopAsync: выход из while (token cancelled)");
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken token)
        {
            var path = context.Request.Url?.AbsolutePath?.TrimEnd('/') ?? string.Empty;
            if (string.IsNullOrEmpty(path)) path = "/";

            var clientIp = ResolveClientIp(context);
            if (!CheckRateLimit(clientIp))
            {
                context.Response.StatusCode = 429;
                context.Response.Headers["Retry-After"] = "60";
                try
                {
                    await WriteTextAsync(context.Response,
                        "{\"error\":\"rate limit exceeded (60 req/min per IP)\"}",
                        "application/json; charset=utf-8", token).ConfigureAwait(false);
                }
                catch { }
                return;
            }

            try
            {
                switch (path)
                {
                    case "/":
                        await WriteHtmlAsync(context.Response, LoadDashboardHtml(), token).ConfigureAwait(false);
                        break;
                    case "/api/health":
                        await WriteJsonAsync(context.Response, BuildHealthPayload(), token).ConfigureAwait(false);
                        break;
                                        case "/api/systems":
                                            await WriteJsonAsync(context.Response, BuildSystemsPayload(), token).ConfigureAwait(false);
                                            break;
                    case "/api/servers":
                        var servers = SafeInvoke(_serverConfigsProvider) as IReadOnlyDictionary<ulong, ServerConfig>;
                        if (servers == null)
                        {
                            context.Response.StatusCode = 503;
                            await WriteTextAsync(context.Response, "{\"error\":\"server configs not ready\"}",
                                "application/json; charset=utf-8", token).ConfigureAwait(false);
                        }
                        else
                        {
                                                var client = _clientProvider?.Invoke();
                                                var payload = servers
                                                    .OrderBy(x => x.Key)
                                                    .Select(x =>
                                                    {
                                                        SocketGuild? g = null;
                                                                                                            SocketRole? masterRole = null;
                                                                                                            SocketRole? superRole = null;
                                                                                                            try
                                                                                                            {
                                                                                                                g = client?.GetGuild(x.Key);
                                                                                                                if (g != null && x.Value.MasterRoleId.HasValue && x.Value.MasterRoleId.Value != 0)
                                                                                                                {
                                                                                                                    var mr = g.GetRole(x.Value.MasterRoleId.Value);
                                                                                                                    masterRole = mr as SocketRole;
                                                                                                                }
                                                                                                                if (g != null && x.Value.SuperUserRoleId.HasValue && x.Value.SuperUserRoleId.Value != 0)
                                                                                                                {
                                                                                                                    var sr = g.GetRole(x.Value.SuperUserRoleId.Value);
                                                                                                                    superRole = sr as SocketRole;
                                                                                                                }
                                                                                                            }
                                                                                                            catch { }
                                                                                                        return new
                                                                                                        {
                                                                                                            GuildId = x.Key,
                                                                                                            GuildName = g?.Name,
                                                                                                            x.Value.ModerateChannelID,
                                                                                                            x.Value.GeneralRGChannelID,
                                                                                                            x.Value.RecordChannelID,
                                                                                                            x.Value.WelcomeChannelID,
                                                                                                            x.Value.RollChannelID,
                                                                                                            x.Value.StatsChannelID,
                                                                                                            x.Value.EventVoiceChannelID,
                                                                                                            MasterRoleId = x.Value.MasterRoleId,
                                                                                                            MasterRoleName = masterRole?.Name,
                                                                                                            x.Value.SuperUserRoleId,
                                                                                                            SuperUserRoleName = superRole?.Name,
                                                                                                            x.Value.TelegramEnabled,
                                                                                                            x.Value.PredictionsEnabled,
                                                                                                            x.Value.RollPicturesEnabled,
                                                                                                            x.Value.SwearFilterEnabled,
                                                                                                            x.Value.MasterGuideEnabled,
                                                                                                        };
                                                                                                    })
                                                                                                    .ToList();
                                                await WriteJsonAsync(context.Response, payload, token).ConfigureAwait(false);
                                            }
                                            break;
                                        case "/api/sessions":
                                            await WriteJsonAsync(context.Response, SafeInvoke(_sessionsProvider), token).ConfigureAwait(false);
                                            break;
                                        case "/api/events":
                                            var announcements = SafeInvoke(_eventsProvider);
                                            if (announcements is string errMsg)
                                            {
                                                context.Response.StatusCode = 503;
                                                await WriteTextAsync(context.Response,
                                                    "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(errMsg) + "}",
                                                    "application/json; charset=utf-8", token).ConfigureAwait(false);
                                            }
                                            else
                                            {
                                                await WriteJsonAsync(context.Response, announcements, token).ConfigureAwait(false);
                                            }
                                            break;
                    case "/api/logs/stream":
                        // Длинное соединение: сервер пушит JSON-записи по мере поступления.
                        // Сначала отдаём последние N для бутстрапа UI, потом — каждую новую запись
                        // отдельной строкой (chunked). Клиент мерджит их с буфером и рендерит.
                        await HandleLogsStreamAsync(context, token).ConfigureAwait(false);
                        break;
                    case "/api/logs":
                                            // Backward-compat: список последних 200 записей одним массивом.
                                            // Стрим-эндпоинт /api/logs/stream — основной источник данных.
                                            // Принимает опциональный ?since=ISO для дельты (устаревший, оставлен
                                            // для скриптов/тестов).
                                            DateTimeOffset? since = null;
                                            var sinceRaw = context.Request.QueryString["since"];
                                            if (!string.IsNullOrEmpty(sinceRaw)
                                                && DateTimeOffset.TryParse(
                                                    sinceRaw,
                                                    System.Globalization.CultureInfo.InvariantCulture,
                                                    System.Globalization.DateTimeStyles.AssumeUniversal,
                                                    out var parsedSince))
                                            {
                                                since = parsedSince;
                                            }
                                            List<BotLogRecord> snapshot;
                                            lock (_logsLock)
                                            {
                                                if (since.HasValue)
                                                {
                                                    // Храним FIFO: новые в начале, старые в конце. Нам нужны
                                                    // записи, у которых Timestamp > since. _logs — LinkedList,
                                                    // отдаём свежие первыми.
                                                    snapshot = _logs
                                                        .Where(x => x.Timestamp > since.Value)
                                                        .Take(500)
                                                        .ToList();
                                                }
                                                else
                                                {
                                                    snapshot = _logs.Take(200).ToList();
                                                }
                                            }
                                            var logs = snapshot.Select(MapRecord);
                                            await WriteJsonAsync(context.Response, logs, token).ConfigureAwait(false);
                                            break;
                    case "/api/stats":
                        int rolls = SafeInvokeInt(_rollsTodayProvider);
                        int sess  = SafeInvokeInt(_activeSessionsProvider);
                        int chat  = SafeInvokeInt(_chatMessagesTodayProvider);
                        int voice = SafeInvokeInt(_usersInVoiceProvider);
                        int totalLogs;
                        lock (_logsLock) totalLogs = _logs.Count;
                        await WriteJsonAsync(context.Response, new
                        {
                            RollsToday = rolls,
                            ChatMessagesToday = chat,
                            ActiveSessions = sess,
                            UsersInVoice = voice,
                            LogRecords = totalLogs,
                        }, token).ConfigureAwait(false);
                        break;
                    case "/api/activity":
                        var activity = SafeInvoke(_activityProvider) as IReadOnlyList<ActivityBucket>
                                        ?? Array.Empty<ActivityBucket>();
                        await WriteJsonAsync(context.Response, activity.Select(b => new
                        {
                            Minute = b.Minute.ToString("HH:mm"),
                            b.Count,
                        }), token).ConfigureAwait(false);
                        break;
                    default:
                        context.Response.StatusCode = 404;
                        await WriteTextAsync(context.Response, "Not found", "text/plain; charset=utf-8", token).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                try { context.Response.StatusCode = 500; } catch { }
                try
                {
                    await WriteTextAsync(context.Response, $"Internal error: {ex.Message}", "text/plain; charset=utf-8", token).ConfigureAwait(false);
                }
                catch { }
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        private static object SafeInvoke(Func<object> provider)
        {
            try { return provider(); }
            catch (Exception ex)
            {
                BotLogger.Warn(LogCategory.System, $"[WebDashboard] Провайдер вернул ошибку: {ex.Message}");
                return new { error = ex.Message };
            }
        }

        private static int SafeInvokeInt(Func<int> provider)
        {
            try { return provider(); }
            catch (Exception ex)
            {
                BotLogger.Warn(LogCategory.System, $"[WebDashboard] Провайдер счётчика вернул ошибку: {ex.Message}");
                return 0;
            }
        }

        private static Task WriteJsonAsync(HttpListenerResponse response, object payload, CancellationToken token)
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            return WriteTextAsync(response, json, "application/json; charset=utf-8", token);
        }

        private static Task WriteHtmlAsync(HttpListenerResponse response, string html, CancellationToken token)
        {
            // Мета-теги в HTML мы тоже ставим, но без явных заголовков браузер
            // может игнорировать их. Дашборд показывает live-данные, кешировать
            // HTML-код незачем — при F5 без Ctrl+Shift:R будет показываться
            // устаревший JSON-рендер.
            try
            {
                response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                response.Headers["Pragma"] = "no-cache";
            }
            catch { /* headers могут быть недоступны до отправки ответа */ }
            return WriteTextAsync(response, html, "text/html; charset=utf-8", token);
        }

        private static async Task WriteTextAsync(HttpListenerResponse response, string text, string contentType, CancellationToken token)
        {
            response.ContentType = contentType;
            var data = Encoding.UTF8.GetBytes(text);
            response.ContentLength64 = data.LongLength;
            try
            {
                await response.OutputStream.WriteAsync(data, 0, data.Length, token).ConfigureAwait(false);
            }
            catch (HttpListenerException) { /* клиент отвалился */ }
            catch (ObjectDisposedException) { /* уже закрыто */ }
        }

        private void OnLog(BotLogRecord record)
                {
                    lock (_logsLock)
                    {
                        _logs.AddFirst(record);
                        while (_logs.Count > _maxLogs)
                            _logs.RemoveLast();
                    }
                    _streamSignal.Set();
                }

                // Сигнал «появились новые записи» для всех открытых /api/logs/stream соединений.
                // Слабая блокировка (тонкая семафор-нотификация), потому что OnLog вызывается
                // из BotLogger-обсервера на каждый Write — он не должен стоять в очереди
                // ожидания дольше микросекунд.
                private readonly ManualResetEventSlim _streamSignal = new(false);

                // Подписчики стрима: на каждого — свой снепшот логов и ссылка на writer.
                private readonly object _streamsLock = new();
                private readonly Dictionary<Guid, StreamSubscription> _streams = new();

                private sealed class StreamSubscription
                {
                    public DateTimeOffset LastSent;   // последний Timestamp, который мы уже отдали
                    public HttpListenerResponse? Response;
                    public CancellationTokenSource? Cts;
                }

                private async Task HandleLogsStreamAsync(HttpListenerContext context, CancellationToken token)
                {
                    var sid = Guid.NewGuid();
                    var subCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    StreamSubscription sub;

                    // Отдаём бутстрап: последние 200 записей единым JSON-массивом,
                    // потом — chunked-поток отдельных объектов.
                    try
                    {
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json; charset=utf-8";
                        context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                        context.Response.Headers["X-Accel-Buffering"] = "no"; // nginx hint, на винде игнор
                        // chunked-режим HttpListener: важно не выставлять ContentLength64 вообще,
                        // а сразу включить SendChunked. Иначе runtime бросает
                        // ArgumentOutOfRangeException на value '-1'.
                        try { context.Response.SendChunked = true; } catch { }

                        List<BotLogRecord> snapshot;
                        DateTimeOffset lastTs;
                        lock (_logsLock)
                        {
                            snapshot = _logs.Take(200).ToList();
                            lastTs = snapshot.Count > 0 ? snapshot[0].Timestamp : DateTimeOffset.MinValue;
                        }

                        var bootstrap = new
                        {
                            items = snapshot.Select(MapRecord).ToList(),
                        };
                        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bootstrap) + "\n");
                        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, subCts.Token)
                            .ConfigureAwait(false);
                        // Без flush HttpListener держит bootstrap в буфере, пока не
                        // накопится достаточно данных. У нас bootstrap — ровно один
                        // массив, и без flush клиент может долго ждать первого чанка.
                        try { await context.Response.OutputStream.FlushAsync(subCts.Token).ConfigureAwait(false); }
                        catch (HttpListenerException) { return; }
                        catch (ObjectDisposedException) { return; }

                        sub = new StreamSubscription
                        {
                            LastSent = lastTs,
                            Response = context.Response,
                            Cts = subCts,
                        };
                        lock (_streamsLock) _streams[sid] = sub;
                    }
                    catch (HttpListenerException) { return; }
                    catch (ObjectDisposedException) { return; }
                    catch (Exception ex)
                    {
                        BotLogger.Warn(LogCategory.System,
                            $"[WebDashboard] Stream bootstrap error: {ex.GetType().Name}: {ex.Message}");
                        return;
                    }

                    try
                    {
                        // Цикл: ждём сигнал «есть новые записи» — сливаем их, шлём как NDJSON.
                        while (!subCts.IsCancellationRequested)
                        {
                            _streamSignal.Wait(subCts.Token);
                            _streamSignal.Reset();

                            // Под нашу подписку собрать только записи новее её LastSent,
                            // потом обновить LastSent, записать в сокет.
                            List<string>? payloads = null;
                            lock (_streamsLock)
                            {
                                if (!_streams.TryGetValue(sid, out var current) || current != sub) break;
                            }
                            lock (_logsLock)
                            {
                                // Берём всё с Timestamp > LastSent (свежие записи идут первыми).
                                var fresh = _logs
                                    .Where(x => x.Timestamp > sub.LastSent)
                                    .ToList();
                                if (fresh.Count > 0)
                                                                {
                                                                    payloads = fresh.Select(x => JsonSerializer.Serialize(MapRecord(x))).ToList();
                                                                    sub.LastSent = fresh[0].Timestamp;
                                                                }
                            }
                            if (payloads == null) continue;
                            foreach (var line in payloads)
                            {
                                var chunk = Encoding.UTF8.GetBytes(line + "\n");
                                try
                                {
                                    await context.Response.OutputStream
                                        .WriteAsync(chunk, 0, chunk.Length, subCts.Token)
                                        .ConfigureAwait(false);
                                }
                                catch (HttpListenerException) { return; }
                                catch (ObjectDisposedException) { return; }
                            }
                            try
                            {
                                await context.Response.OutputStream.FlushAsync(subCts.Token)
                                    .ConfigureAwait(false);
                            }
                            catch (HttpListenerException) { return; }
                            catch (ObjectDisposedException) { return; }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                    catch (Exception ex)
                    {
                        BotLogger.Warn(LogCategory.System,
                            $"[WebDashboard] Stream loop error: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        lock (_streamsLock) _streams.Remove(sid);
                        try { subCts.Dispose(); } catch { }
                    }
                }

                                private static object MapRecord(BotLogRecord x) => new
                                {
                                    timestamp = x.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                                    level = x.Level.ToString(),
                                    category = x.Category.ToString(),
                                    message = x.Message,
                                    isUser = x.IsUser,
                                    levelClass = Program.LevelCssClass(x.Level),
                                    categoryClass = Program.CategoryCssClass(x.Category),
                                };

                                private object BuildHealthPayload()
        {
            object raw;
            try
            {
                raw = _healthProvider();
            }
            catch (Exception ex)
            {
                raw = new { Error = ex.Message };
            }

            // Машиночитаемый JSON со всеми доступными полями: состояние подключения,
            // число серверов, время старта/версия/аптайм, текущие счётчики из /api/stats,
            // размер буферов дашборда. Health-провайдер уже отдаёт Connected/Guilds/StartupType/
            // UtcNow/Version/Uptime — добавляем Timestamp и снапшот статистики.
            int rolls, activeSessions, chatMessages, usersInVoice, totalLogs;
            try { rolls = SafeInvokeInt(_rollsTodayProvider); }
            catch { rolls = 0; }
            try { activeSessions = SafeInvokeInt(_activeSessionsProvider); }
            catch { activeSessions = 0; }
            try { chatMessages = SafeInvokeInt(_chatMessagesTodayProvider); }
            catch { chatMessages = 0; }
            try { usersInVoice = SafeInvokeInt(_usersInVoiceProvider); }
            catch { usersInVoice = 0; }
            lock (_logsLock) totalLogs = _logs.Count;

            return new
                        {
                            Provider = raw,
                            Timestamp = DateTimeOffset.UtcNow,
                            PingMs = SafeInvokeInt(() => (int)(_clientProvider?.Invoke()?.Latency ?? 0)),
                            Stats = new
                            {
                                RollsToday = rolls,
                                ActiveSessions = activeSessions,
                                ChatMessagesToday = chatMessages,
                                UsersInVoice = usersInVoice,
                                LogRecords = totalLogs,
                            },
                        };
                    }

                    private object BuildSystemsPayload()
                    {
                        object raw;
                        try { raw = _systemsProvider(); }
                        catch (Exception ex) { raw = new { Error = ex.Message }; }
                        return raw;
                    }

        private bool CheckRateLimit(string clientIp)
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddMinutes(-1);

            lock (_rateLimitLock)
            {
                // Чистим протухшие корзины и удаляем IP с нулевым счётчиком.
                if (_rateLimitBuckets.Count > 64)
                {
                    var expired = _rateLimitBuckets.Where(kv => kv.Value.WindowStart < cutoff && kv.Value.Count == 0).Select(kv => kv.Key).ToList();
                    foreach (var key in expired) _rateLimitBuckets.Remove(key);
                }

                if (!_rateLimitBuckets.TryGetValue(clientIp, out var bucket) || bucket.WindowStart < cutoff)
                {
                    bucket = new RateLimitBucket { WindowStart = now, Count = 0 };
                    _rateLimitBuckets[clientIp] = bucket;
                }
                bucket.Count++;
                return bucket.Count <= _rateLimitPerMinute;
            }
        }

        private static string ResolveClientIp(HttpListenerContext context)
        {
            // Для localhost HttpListener.RemoteEndPoint выглядит как [::1]:port — нормализуем до "::1" / "127.0.0.1".
            try
            {
                var remote = context.Request.RemoteEndPoint;
                if (remote == null) return "unknown";
                var addr = remote.Address?.ToString();
                return string.IsNullOrWhiteSpace(addr) ? "unknown" : addr;
            }
            catch
            {
                return "unknown";
            }
        }

        private sealed class RateLimitBucket
        {
            public DateTimeOffset WindowStart;
            public int Count;
        }

        private static string LoadDashboardHtml()
        {
                    // Кешируем содержимое dashboard.html после первой удачной загрузки —
                    // на пиковом трафике это снимает с диска несколько сотен чтений в минуту
                    // (каждая вкладка дашборда раз в ~5с дёргает '/').
                    if (_dashboardHtmlCache != null)
                        return _dashboardHtmlCache;

                    string fallback = "<!doctype html><html><body><h1>RPBot</h1><p>dashboard.html не найден</p></body></html>";

                    try
                    {
                        // Ищем файл в Web/dashboard.html рядом с .exe, потом в исходниках
                        var candidates = new[]
                        {
                            Path.Combine(AppContext.BaseDirectory, "Web", "dashboard.html"),
                            Path.Combine(Directory.GetCurrentDirectory(), "Web", "dashboard.html"),
                            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Web", "dashboard.html"),
                        };
                        foreach (var path in candidates)
                        {
                            if (File.Exists(path))
                            {
                                var html = File.ReadAllText(path, Encoding.UTF8);
                                _dashboardHtmlCache = html;
                                return html;
                            }
                        }
                        BotLogger.Warn(LogCategory.System, "[WebDashboard] dashboard.html не найден, отдаём заглушку");
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Warn(LogCategory.System, $"[WebDashboard] Ошибка загрузки dashboard.html: {ex.Message}");
                    }

                    _dashboardHtmlCache = fallback;
                    return fallback;
                }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); }
            catch { }
        }
    }
}
