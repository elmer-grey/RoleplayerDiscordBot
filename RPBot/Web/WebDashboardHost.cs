using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Discord.WebSocket;

namespace RPBot.Web
{
    /// <summary>
    /// Linux-only реализация WebDashboard на Kestrel (вместо HttpListener).
    /// HttpListener в .NET 8 на Linux имеет хронические баги:
    ///   - Start() падает с ErrorCode=400 на двух loopback-префиксах ([::1]+127.0.0.1);
    ///   - ломает обработку любых запросов с заголовком Authorization (даже после
    ///     AuthenticationSchemes.Anonymous), возвращая 404 до switch-case;
    ///   - в Windows нужен на Windows отдельный urlacl через netsh (нетривиально для юзера).
    ///
    /// Kestrel этих проблем не имеет: нормально работает на 127.0.0.1, корректно
    /// обрабатывает Authorization, не требует urlacl и регистраций.
    ///
    /// На Windows продолжает работать старый WebDashboardService (HttpListener).
    /// Здесь только Linux-путь.
    /// </summary>
    public sealed class WebDashboardHost : IWebDashboard
    {
        private readonly string _host;
        private readonly int _port;
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
        private readonly int _maxLogs;

        // Хранилище логов (общее с прежней реализацией, чтобы клиентский UI не заметил подмены).
        private readonly object _logsLock = new();
        private readonly LinkedList<BotLogRecord> _logs = new();
        private long _logSeq;
        private long NextSeq() => Interlocked.Increment(ref _logSeq);

        // Подписчик логов от BotLogger.
        private Guid _tailObserverId;
        private bool _tailRegistered;
        private readonly object _tailInitLock = new();

        // Сигнал «новые записи» для всех открытых /api/logs/stream подписчиков.
        // SemaphoreSlim с currentCount=0 ждёт WaitAsync(), Release() будит — то же, что ManualResetEventSlim,
        // но async-friendly.
        private readonly SemaphoreSlim _streamSignal = new(0, int.MaxValue);

        // Rate-limit: 240 запросов/мин с одного IP.
        private readonly object _rateLimitLock = new();
        private readonly Dictionary<string, RateLimitBucket> _rateLimitBuckets = new(StringComparer.Ordinal);
        private readonly int _rateLimitPerMinute;

        private WebApplication? _app;
        private CancellationTokenSource? _cts;

        public WebDashboardHost(
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
            _host = host;
            _port = port;
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
            _rateLimitPerMinute = 240;
        }

        public void Start()
        {
            if (_cts != null)
            {
                BotLogger.Warn(LogCategory.System, "[WebDashboardHost] Start(): уже запущен — повторный вызов игнорируется");
                return;
            }

            // Kestrel на Linux биндим на заданный хост из конфига.
            //
            // "0.0.0.0" — слушать на всех интерфейсах (loopback, eth0, tailscale0).
            // Наружу порт фильтрует iptables, поэтому безопасность не страдает.
            // Это даёт доступ через Tailscale (100.x.y.z) без nginx-прокси.
            //
            // Если хост пустой — дефолт 127.0.0.1 (loopback, для nginx-режима).
            var bindHost = string.IsNullOrEmpty(_host) ? "127.0.0.1" : _host;
            var bindUrl = $"http://{bindHost}:{_port}";

            BotLogger.Info(LogCategory.System, $"[WebDashboardHost] Start(): bind={bindUrl}");

            var builder = WebApplication.CreateBuilder();
            // Перенаправляем логи Kestrel в BotLogger, чтобы вся диагностика шла в тот же поток.
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new BotLoggerProvider());

                        // Привязываем Kestrel к нужному URL. По умолчанию .NET 8 биндит localhost:5000,
                        // что на VPS конфликтует с другими сервисами и не соответствует заданному порту.
                        builder.WebHost.UseUrls(bindUrl);

                        var app = builder.Build();

            // Middleware: rate-limit по IP.
            app.Use(async (ctx, next) =>
            {
                var clientIp = ResolveClientIp(ctx);
                if (!CheckRateLimit(clientIp))
                {
                    ctx.Response.StatusCode = 429;
                    ctx.Response.Headers["Retry-After"] = "60";
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    await ctx.Response.WriteAsync(
                        "{\"error\":\"rate limit exceeded (60 req/min per IP)\"}",
                        ctx.RequestAborted).ConfigureAwait(false);
                    return;
                }
                await next();
            });

            // Маршруты — в порядке возрастания специфичности (статика > api > root).
            app.MapGet("/", HandleRoot);
            app.MapGet("/api/health", HandleHealth);
            app.MapGet("/api/systems", HandleSystems);
            app.MapGet("/api/servers", HandleServers);
            app.MapGet("/api/sessions", HandleSessions);
            app.MapGet("/api/events", HandleEvents);
            app.MapGet("/api/stats", HandleStats);
            app.MapGet("/api/activity", HandleActivity);
            app.MapGet("/api/logs", HandleLogs);
            app.MapGet("/api/logs/stream", HandleLogsStream);

            _app = app;

            // Подписка на логи.
            lock (_tailInitLock)
            {
                if (_tailRegistered)
                {
                    try { BotLogger.UnregisterObserver(_tailObserverId); } catch { }
                    _tailRegistered = false;
                }
                lock (_logsLock) { _logs.Clear(); }
                _tailObserverId = BotLogger.RegisterObserver(OnLog);
                _tailRegistered = true;
                BotLogger.Info(LogCategory.System, $"[WebDashboardHost] Start observer={_tailObserverId}");
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await app.RunAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    BotLogger.Error(LogCategory.System,
                        $"[WebDashboardHost] RunAsync упал: {ex.GetType().Name}: {ex.Message}");
                }
            }, cts.Token);

            BotLogger.Info(LogCategory.System, $"[WebDashboardHost] Запущен на {bindUrl}");
        }

        public async Task StopAsync()
        {
            var cts = Interlocked.Exchange(ref _cts, null);
            if (cts == null)
            {
                BotLogger.Info(LogCategory.System, "[WebDashboardHost] StopAsync: уже остановлен");
                return;
            }
            try { cts.Cancel(); } catch { }

            lock (_tailInitLock)
            {
                if (_tailRegistered)
                {
                    try { BotLogger.UnregisterObserver(_tailObserverId); } catch { }
                    _tailRegistered = false;
                }
            }

            if (_app != null)
            {
                try { await _app.StopAsync().ConfigureAwait(false); } catch { }
                try { await _app.DisposeAsync().ConfigureAwait(false); } catch { }
                _app = null;
            }

            try { cts.Dispose(); } catch { }
            BotLogger.Info(LogCategory.System, "[WebDashboardHost] StopAsync завершён");
        }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
            try { _streamSignal.Dispose(); } catch { }
        }

        // ───── Handlers ─────────────────────────────────────────────────────────

        private async Task HandleRoot(HttpContext ctx)
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers["Pragma"] = "no-cache";
            await ctx.Response.WriteAsync(LoadDashboardHtml(), ctx.RequestAborted).ConfigureAwait(false);
        }

        private async Task HandleHealth(HttpContext ctx)
        {
            await WriteJson(ctx, BuildHealthPayload()).ConfigureAwait(false);
        }

        private async Task HandleSystems(HttpContext ctx)
        {
            await WriteJson(ctx, BuildSystemsPayload()).ConfigureAwait(false);
        }

        private async Task HandleServers(HttpContext ctx)
        {
            var servers = SafeInvoke(_serverConfigsProvider) as IReadOnlyDictionary<ulong, ServerConfig>;
            if (servers == null)
            {
                ctx.Response.StatusCode = 503;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync("{\"error\":\"server configs not ready\"}", ctx.RequestAborted).ConfigureAwait(false);
                return;
            }

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
            await WriteJson(ctx, payload).ConfigureAwait(false);
        }

        private async Task HandleSessions(HttpContext ctx)
        {
            await WriteJson(ctx, SafeInvoke(_sessionsProvider)).ConfigureAwait(false);
        }

        private async Task HandleEvents(HttpContext ctx)
        {
            var announcements = SafeInvoke(_eventsProvider);
            if (announcements is string errMsg)
            {
                ctx.Response.StatusCode = 503;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(
                    "{\"error\":" + JsonSerializer.Serialize(errMsg) + "}",
                    ctx.RequestAborted).ConfigureAwait(false);
                return;
            }
            await WriteJson(ctx, announcements).ConfigureAwait(false);
        }

        private async Task HandleStats(HttpContext ctx)
        {
            int rolls = SafeInvokeInt(_rollsTodayProvider);
            int sess = SafeInvokeInt(_activeSessionsProvider);
            int chat = SafeInvokeInt(_chatMessagesTodayProvider);
            int voice = SafeInvokeInt(_usersInVoiceProvider);
            int totalLogs;
            lock (_logsLock) totalLogs = _logs.Count;
            await WriteJson(ctx, new
            {
                RollsToday = rolls,
                ChatMessagesToday = chat,
                ActiveSessions = sess,
                UsersInVoice = voice,
                LogRecords = totalLogs,
            }).ConfigureAwait(false);
        }

        private async Task HandleActivity(HttpContext ctx)
        {
            var activity = SafeInvoke(_activityProvider) as IReadOnlyList<ActivityBucket>
                           ?? Array.Empty<ActivityBucket>();
            await WriteJson(ctx, activity.Select(b => new
            {
                Minute = b.Minute.ToString("HH:mm"),
                b.Count,
            })).ConfigureAwait(false);
        }

        private async Task HandleLogs(HttpContext ctx)
        {
            DateTimeOffset? since = null;
            var sinceRaw = ctx.Request.Query["since"].ToString();
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
                    snapshot = _logs.Where(x => x.Timestamp > since.Value).Take(500).ToList();
                }
                else
                {
                    snapshot = _logs.Take(200).ToList();
                }
            }
            await WriteJson(ctx, snapshot.Select(MapRecord)).ConfigureAwait(false);
        }

        private async Task HandleLogsStream(HttpContext ctx)
        {
            // Длинное соединение: сначала bootstrap (последние 200), потом chunked NDJSON.
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            // chunked — Kestrel сам разруливает Content-Length: -1 через Transfer-Encoding.

                    // Снимок состояния под локом — bound _logsCount:
                    //   _logs: LinkedList<BotLogRecord>, свежие записи впереди (AddFirst).
                    //   snapshot[0] — самый свежий, snapshot[^1] — самый старый.
                    //   lastSentSeq = максимальный Seq в snapshot.
                    List<BotLogRecord> snapshot;
                    long lastSentSeq;
                    lock (_logsLock)
                    {
                        snapshot = _logs.Take(200).ToList();
                        lastSentSeq = snapshot.Count > 0 ? snapshot[0].Seq : 0;
                    }
                    var bootstrap = new { items = snapshot.Select(MapRecord).ToList() };
                    var bootstrapBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bootstrap) + "\n");
                    await ctx.Response.Body.WriteAsync(bootstrapBytes, ctx.RequestAborted).ConfigureAwait(false);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);

                    try
                    {
                        while (!ctx.RequestAborted.IsCancellationRequested)
                        {
                            try
                            {
                                await _streamSignal.WaitAsync(ctx.RequestAborted).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { break; }

                            // _streamSignal будит нас на КАЖДЫЙ эвент, но в реальности
                            // между Wait и Lock может прийти ещё пачка. Семплим всё,
                            // что свежее lastSentSeq, одним блоком, чтобы не залипнуть
                            // в lock+write-loop на бёрсте.
                            List<string>? payloads = null;
                            lock (_logsLock)
                            {
                                var fresh = _logs.Where(x => x.Seq > lastSentSeq).ToList();
                                if (fresh.Count > 0)
                                {
                                    payloads = fresh.Select(x => JsonSerializer.Serialize(MapRecord(x))).ToList();
                                    lastSentSeq = fresh[0].Seq;
                                }
                            }
                            if (payloads == null) continue;
                            foreach (var line in payloads)
                            {
                                var chunk = Encoding.UTF8.GetBytes(line + "\n");
                                try
                                {
                                    await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) { return; }
                            }
                            try
                            {
                                await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { return; }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        BotLogger.Warn(LogCategory.System,
                            $"[WebDashboardHost] Stream loop error: {ex.GetType().Name}: {ex.Message}");
                    }
                }

        // ───── Логи ─────────────────────────────────────────────────────────────

        private void OnLog(BotLogRecord record)
        {
            var stamped = record with { Seq = Interlocked.Increment(ref _logSeq) };
            lock (_logsLock)
            {
                _logs.AddFirst(stamped);
                while (_logs.Count > _maxLogs)
                    _logs.RemoveLast();
            }
            // Будим ВСЕ ждущие стримы. SemaphoreSlim.Release без maxValue-наращивания —
            // каждый WaitAsync заберёт одно. Для нескольких подписчиков делаем Release без аргумента
            // (по умолчанию = +1), но если накопилось больше — расширяем до текущего числа подписчиков.
            // Простое Release() — оптимально для нашего сценария: 1-3 подписчика, события редкие.
            try { _streamSignal.Release(); } catch (SemaphoreFullException) { /* игнорируем переполнение */ }
        }

        private object MapRecord(BotLogRecord x) => new
        {
            seq = x.Seq,
            timestamp = x.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
            level = x.Level.ToString(),
            category = x.Category.ToString(),
            message = x.Message,
            isUser = x.IsUser,
            levelClass = Program.LevelCssClass(x.Level),
            categoryClass = Program.CategoryCssClass(x.Category),
        };

        // ───── Health / Systems payload ──────────────────────────────────────────

        private object BuildHealthPayload()
        {
            object raw;
            try { raw = _healthProvider(); }
            catch (Exception ex) { raw = new { Error = ex.Message }; }

            string version;
            try { version = _versionProvider(); }
            catch (Exception ex) { version = $"error: {ex.Message}"; }

            TimeSpan uptime;
            try { uptime = _uptimeProvider(); }
            catch (Exception ex) { uptime = TimeSpan.Zero; }

            return new
            {
                Status = "ok",
                Version = version,
                UptimeSeconds = (long)uptime.TotalSeconds,
                Provider = raw,
                UtcNow = DateTimeOffset.UtcNow,
            };
        }

        private object BuildSystemsPayload()
        {
            return SafeInvoke(_systemsProvider);
        }

        // ───── Утилиты ─────────────────────────────────────────────────────────

        private static object SafeInvoke(Func<object> provider)
        {
            try { return provider(); }
            catch (Exception ex)
            {
                BotLogger.Warn(LogCategory.System, $"[WebDashboardHost] Провайдер вернул ошибку: {ex.Message}");
                return new { error = ex.Message };
            }
        }

        private static int SafeInvokeInt(Func<int> provider)
        {
            try { return provider(); }
            catch (Exception ex)
            {
                BotLogger.Warn(LogCategory.System, $"[WebDashboardHost] Провайдер счётчика вернул ошибку: {ex.Message}");
                return 0;
            }
        }

        private static async Task WriteJson(HttpContext ctx, object payload)
        {
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            await ctx.Response.WriteAsync(json, ctx.RequestAborted).ConfigureAwait(false);
        }

        private static string LoadDashboardHtml()
        {
            // dashboard.html копируется в выходную папку при сборке (см. RPBot.csproj, Content Include).
            // AppContext.BaseDirectory на Linux = /opt/rpbot/.../RPBot/, на Windows — bin/Debug/net8.0/.
            var path = Path.Combine(AppContext.BaseDirectory, "Web", "dashboard.html");
            return File.ReadAllText(path);
        }

        private static string ResolveClientIp(HttpContext ctx)
        {
            // Kestrel сам прокидывает X-Forwarded-For при наличии ForwardedHeadersMiddleware,
            // но мы не подключаем его — для loopback-сервиса X-Real-IP/X-Forwarded-For может
            // прийти от nginx, если тот их добавляет (наш конфиг их добавляет). Берём из заголовка,
            // иначе RemoteIpAddress.
            var forwarded = ctx.Request.Headers["X-Real-IP"].ToString();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                // X-Real-IP может быть IPv4 или IPv6, берём первый из списка, если их несколько.
                var first = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                     .FirstOrDefault();
                if (!string.IsNullOrEmpty(first)) return first;
            }
            return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }

        private bool CheckRateLimit(string clientIp)
        {
            var now = DateTimeOffset.UtcNow;
            lock (_rateLimitLock)
            {
                if (!_rateLimitBuckets.TryGetValue(clientIp, out var bucket))
                {
                    bucket = new RateLimitBucket { WindowStart = now, Count = 0 };
                    _rateLimitBuckets[clientIp] = bucket;
                }
                if (now - bucket.WindowStart >= TimeSpan.FromMinutes(1))
                {
                    bucket.WindowStart = now;
                    bucket.Count = 0;
                }
                if (bucket.Count >= _rateLimitPerMinute) return false;
                bucket.Count++;
                return true;
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

        private sealed class RateLimitBucket
        {
            public DateTimeOffset WindowStart;
            public int Count;
        }

        // ───── ASP.NET Core → BotLogger мост ────────────────────────────────────

        /// <summary>
        /// Провайдер логов Microsoft.Extensions.Logging → BotLogger.
        /// Kestrel и ASP.NET Core генерируют свои сообщения (binding info, request logs),
        /// мы их пробрасываем в общий лог-канал для удобства отладки.
                ///
                /// Чтобы не засорять вывод низкоуровневой Kestrel-диагностикой
                /// (Request starting/finished/Executing endpoint — это спам каждые ~5 секунд
                /// из поллера дашборда), категории Microsoft.AspNetCore.* и Microsoft.Extensions.Hosting
                /// отсекаются ниже Warning. Только реальные проблемы (Warning/Error) проходят.
                /// </summary>
                private sealed class BotLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
                {
                            public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new BotLoggerAdapter(categoryName);
                    public void Dispose() { }
                }

                        private sealed class BotLoggerAdapter : Microsoft.Extensions.Logging.ILogger
                {
                    private readonly string _category;
                    public BotLoggerAdapter(string category) { _category = category; }

                            public IDisposable BeginScope<TState>(TState state) => null!;

                            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
                            {
                                // Отсекаем шум от инфраструктуры ASP.NET Core: спам поллера
                                // (Request starting/finished, Executing/Executed endpoint,
                                // Hosting lifetime messages) идёт с категориями
                                // Microsoft.AspNetCore.* и Microsoft.Extensions.Hosting*.
                                // Их Debug/Information не нужны — оставляем только Warning+.
                                if (logLevel < Microsoft.Extensions.Logging.LogLevel.Warning &&
                                    (_category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                                     || _category.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal)))
                                {
                                    return false;
                                }
                                return true;
                            }

                            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state,
                                            Exception exception, Func<TState, Exception, string> formatter)
                    {
                                var msg = formatter(state, exception!);
                        if (string.IsNullOrEmpty(msg)) return;

                        // Только Warning/Error пробрасываем в BotLogger (Info-сообщения Kestrel'а слишком шумные).
                        if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
                        {
                            BotLogger.Warn(LogCategory.System, $"[Kestrel/{_category}] {msg}");
                        }
                        else if (logLevel == Microsoft.Extensions.Logging.LogLevel.Information)
                        {
                            BotLogger.Debug(LogCategory.System, $"[Kestrel/{_category}] {msg}");
                        }
                    }
                }
            }
        }