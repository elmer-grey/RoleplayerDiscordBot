using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot.Web
{
    public sealed class WebDashboardService : IDisposable
    {
        private readonly string _prefix;
        private readonly Func<object> _healthProvider;
        private readonly Func<IReadOnlyDictionary<ulong, ServerConfig>> _serverConfigsProvider;
        private readonly Func<object> _sessionsProvider;
        private readonly HttpListener _listener = new();
        private readonly object _logsLock = new();
        private readonly LinkedList<BotLogRecord> _logs = new();
        private readonly int _maxLogs;
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        public WebDashboardService(
            string host,
            int port,
            Func<object> healthProvider,
            Func<IReadOnlyDictionary<ulong, ServerConfig>> serverConfigsProvider,
            Func<object> sessionsProvider,
            int maxLogs = 1000)
        {
            _prefix = $"http://{host}:{port}/";
            _healthProvider = healthProvider;
            _serverConfigsProvider = serverConfigsProvider;
            _sessionsProvider = sessionsProvider;
            _maxLogs = Math.Max(100, maxLogs);
        }

        public void Start()
        {
            if (_cts != null)
                return;

            var cts = new CancellationTokenSource();
            _cts = cts;
            _listener.Prefixes.Add(_prefix);

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                _cts = null;
                _listener.Prefixes.Remove(_prefix);
                BotLogger.Error(LogCategory.System,
                    $"[WebDashboard] Не удалось запустить HTTP-сервер на {_prefix}: {ex.Message}");
                BotLogger.Warn(LogCategory.System,
                    $"[WebDashboard] На Windows может потребоваться регистрация URL: " +
                    $"netsh http add urlacl url={_prefix} user=Everyone");
                return;
            }
            catch (Exception ex)
            {
                _cts = null;
                _listener.Prefixes.Remove(_prefix);
                BotLogger.Error(LogCategory.System, $"[WebDashboard] Ошибка запуска: {ex.Message}");
                return;
            }

            BotLogger.RegisterObserver(OnLog);
            _loopTask = Task.Run(() => AcceptLoopAsync(cts.Token));
            BotLogger.Info(LogCategory.System, $"[WebDashboard] Запущен на {_prefix}");
        }

        public async Task StopAsync()
        {
            var cts = _cts;
            if (cts == null)
                return;

            _cts = null;
            try { cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { BotLogger.UnregisterObserver(OnLog); } catch { }

            if (_loopTask != null)
            {
                try { await _loopTask.ConfigureAwait(false); } catch { }
                _loopTask = null;
            }

            try { cts.Dispose(); } catch { }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequestAsync(context, token), token);
                }
                catch (HttpListenerException)
                {
                    if (token.IsCancellationRequested)
                        return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.System, $"[WebDashboard] Ошибка цикла HTTP: {ex.Message}");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken token)
        {
            var path = context.Request.Url?.AbsolutePath?.TrimEnd('/') ?? string.Empty;
            if (string.IsNullOrEmpty(path)) path = "/";

            try
            {
                switch (path)
                {
                    case "/":
                        await WriteHtmlAsync(context.Response, BuildHtml(), token).ConfigureAwait(false);
                        break;
                    case "/api/health":
                        await WriteJsonAsync(context.Response, SafeInvoke(_healthProvider), token).ConfigureAwait(false);
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
                            var payload = servers
                                .OrderBy(x => x.Key)
                                .Select(x => new
                                {
                                    GuildId = x.Key,
                                    x.Value.ModerateChannelID,
                                    x.Value.GeneralRGChannelID,
                                    x.Value.RecordChannelID,
                                    x.Value.MasterRoleId,
                                    x.Value.SuperUserRoleId,
                                    x.Value.TelegramEnabled,
                                    x.Value.PredictionsEnabled,
                                })
                                .ToList();
                            await WriteJsonAsync(context.Response, payload, token).ConfigureAwait(false);
                        }
                        break;
                    case "/api/sessions":
                        await WriteJsonAsync(context.Response, SafeInvoke(_sessionsProvider), token).ConfigureAwait(false);
                        break;
                    case "/api/logs":
                        List<BotLogRecord> snapshot;
                        lock (_logsLock)
                        {
                            snapshot = _logs.Reverse().Take(200).ToList();
                        }
                        var logs = snapshot.Select(x => new
                        {
                            x.Timestamp,
                            x.Level,
                            x.Category,
                            x.Message,
                        });
                        await WriteJsonAsync(context.Response, logs, token).ConfigureAwait(false);
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

        private static Task WriteJsonAsync(HttpListenerResponse response, object payload, CancellationToken token)
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            return WriteTextAsync(response, json, "application/json; charset=utf-8", token);
        }

        private static Task WriteHtmlAsync(HttpListenerResponse response, string html, CancellationToken token)
            => WriteTextAsync(response, html, "text/html; charset=utf-8", token);

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
        }

        private static string BuildHtml()
        {
            return """
<!doctype html>
<html lang=\"ru\">
<head>
<meta charset=\"utf-8\" />
<title>RPBot Dashboard</title>
<style>
body{font-family:Segoe UI,Arial,sans-serif;background:#111;color:#eee;margin:20px}
.card{background:#1b1b1b;border:1px solid #333;border-radius:10px;padding:12px;margin-bottom:12px}
pre{white-space:pre-wrap;word-break:break-word;background:#0f0f0f;padding:10px;border-radius:8px}
h1,h2{margin:0 0 10px}
small{color:#aaa}
</style>
</head>
<body>
<h1>RPBot Web Dashboard</h1>
<div class=\"card\"><h2>Health</h2><pre id=\"health\">loading...</pre></div>
<div class=\"card\"><h2>Servers</h2><pre id=\"servers\">loading...</pre></div>
<div class=\"card\"><h2>Sessions</h2><pre id=\"sessions\">loading...</pre></div>
<div class=\"card\"><h2>Logs (последние 200)</h2><pre id=\"logs\">loading...</pre><small>Автообновление раз в 5 секунд</small></div>
<script>
async function load(id,url){
 const r=await fetch(url); const j=await r.json();
 document.getElementById(id).textContent=JSON.stringify(j,null,2);
}
async function tick(){
 await load('health','/api/health');
 await load('servers','/api/servers');
 await load('sessions','/api/sessions');
 await load('logs','/api/logs');
}
tick(); setInterval(tick,5000);
</script>
</body>
</html>
""";
        }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); }
            catch { }
        }
    }
}
