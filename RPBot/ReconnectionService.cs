using Discord;
using Discord.WebSocket;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot
{
    public sealed class ManualReconnectException : Exception
    {
        public ManualReconnectException(string message = "Ручной реконнект по команде") : base(message) { }
    }

    public sealed class BackgroundDisconnectException : Exception
    {
        public BackgroundDisconnectException(string message = "Фоновая проверка: клиент всё ещё отключен") : base(message) { }
    }

    public class ReconnectionService : IDisposable
    {
        private readonly DiscordSocketClient _client;
        private readonly ConnectionStateInfo _connectionInfo;
        private readonly SemaphoreSlim _reconnectLock = new SemaphoreSlim(1, 1);
        private readonly object _stateLock = new();
        private CancellationTokenSource _reconnectCts = new();

        private const int InitialWaitMs = 5000;
        private const int ConnectTimeoutMs = 60000;
        private const int MaxReconnectAttempts = 8;
        private static readonly int[] BackoffSeconds = { 10, 20, 40, 60, 60, 60, 60, 60 };

        private bool _isReconnecting;
        private bool _isShuttingDown;
        private DateTime _ignoreDisconnectEventsUntil = DateTime.MinValue;

        public Action<string>? LogSink { get; set; }

        public event Func<string, Task>? OnReconnectStarted;
        public event Func<bool, Task>? OnReconnectCompleted;
        public event Func<Exception, Task>? OnDisconnectDetected;
        public event Func<Task>? OnFullRestartRequested;

        public ConnectionStateInfo ConnectionInfo => _connectionInfo;
        public bool IsReconnectInProgress { get { lock (_stateLock) { return _isReconnecting; } } }
        public bool ShouldPauseBackgroundDisconnectChecks
        {
            get { lock (_stateLock) { return _isReconnecting || _connectionInfo.IsExpectedDisconnect || _ignoreDisconnectEventsUntil > DateTime.UtcNow; } }
        }

        public ReconnectionService(DiscordSocketClient client)
        {
            _client = client;
            _connectionInfo = new ConnectionStateInfo();
        }

        public Task RequestManualReconnectAsync(string message = "Ручной реконнект по команде")
            => HandleDisconnect(new ManualReconnectException(message));

        public async Task HandleDisconnect(Exception exception)
        {
            if (_isShuttingDown) return;
            var isManual = exception is ManualReconnectException;
            if (!isManual && exception is GatewayReconnectException)
            {
                BotLogger.Debug(LogCategory.Discord, "Плановый реконнект Discord Gateway — не вмешиваемся.");
                return;
            }
            if (!isManual && IsExpectedDisconnectInProgress()) return;

            var reason = DisconnectReasonTranslator.GetFriendlyReason(exception);
            _connectionInfo.AddDisconnectReason(reason, exception?.Message);
            if (!isManual && OnDisconnectDetected != null) await OnDisconnectDetected.Invoke(exception);

            CancellationTokenSource cts;
            lock (_stateLock)
            {
                // Отменяем предыдущий CTS (если реконнект уже шёл — он завершится
                // с OperationCanceledException и освободит _reconnectLock).
                try { _reconnectCts.Cancel(); } catch { }
                try { _reconnectCts.Dispose(); } catch { }
                _reconnectCts = new CancellationTokenSource();
                cts = _reconnectCts;
            }
            _ = Task.Run(() => ReconnectLoopAsync(cts, isManual));
        }

        private async Task ReconnectLoopAsync(CancellationTokenSource cts, bool forceReconnect)
        {
            var ct = cts.Token;

            // Ждём блокировку без таймаута: если предыдущая петля ещё владеет ею,
            // дождёмся пока она не освободит (после OperationCanceledException в finally).
            // Передаём ct — если ещё один HandleDisconnect снова отменит CTS пока мы
            // ждём, WaitAsync выбросит OperationCanceledException и мы не начнём
            // устаревший реконнект.
            try { await _reconnectLock.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }

            // После получения блокировки ct мог уже быть отменён (ещё один Disconnect
            // пришёл пока мы ждали). Берём актуальный CTS.
            lock (_stateLock)
            {
                if (ct.IsCancellationRequested)
                {
                    // Текущий CTS устарел — переключаемся на последний действующий.
                    cts = _reconnectCts;
                    ct = cts.Token;
                }
            }
            if (ct.IsCancellationRequested)
            {
                try { _reconnectLock.Release(); } catch { }
                return;
            }
            try
            {
                lock (_stateLock) { _isReconnecting = true; }
                await Log($"Ждём {InitialWaitMs / 1000} сек — даём Discord.NET шанс восстановиться самому...");
                try { await Task.Delay(InitialWaitMs, ct); } catch (OperationCanceledException) { return; }

                if (!forceReconnect && _client.ConnectionState == ConnectionState.Connected)
                {
                    await Log("Клиент восстановился сам — реконнект не нужен.");
                    // Убираем этот дисконнект из истории предсказаний — кратковременный WebSocket-сбой не должен ухудшать прогноз
                    _connectionInfo.RemoveLastSelfRecoveredDisconnect();
                    if (OnReconnectCompleted != null) await OnReconnectCompleted.Invoke(true);
                    _connectionInfo.ResetAttempts();
                    return;
                }

                var attempt = 0;
                var success = false;
                while (!ct.IsCancellationRequested && !_isShuttingDown)
                {
                    attempt++;
                    _connectionInfo.ReconnectAttempts = attempt;
                    if (attempt > MaxReconnectAttempts)
                    {
                        await Log($"Превышен лимит попыток ({attempt - 1}). Запрашиваем полный рестарт.");
                        try { if (OnFullRestartRequested != null) await OnFullRestartRequested.Invoke(); } catch (Exception ex) { await Log($"Ошибка при запросе рестарта: {ex.Message}"); }
                        return;
                    }

                    if (OnReconnectStarted != null) await OnReconnectStarted.Invoke($"Попытка реконнекта #{attempt}");
                    await Log($"=== Попытка реконнекта {attempt}/{MaxReconnectAttempts} ===");

                    if (_client.ConnectionState != ConnectionState.Disconnected)
                    {
                        await Log("Останавливаем клиент...");
                        MarkExpectedDisconnect(TimeSpan.FromSeconds(20));
                        try { await _client.StopAsync(); } catch (Exception ex) { await Log($"StopAsync: {ex.Message}"); }
                        for (int i = 0; i < 50; i++)
                        {
                            if (_client.ConnectionState == ConnectionState.Disconnected) break;
                            try { await Task.Delay(100, ct); } catch (OperationCanceledException) { return; }
                        }
                    }

                    await Log("Запускаем клиент...");
                    try { await _client.StartAsync(); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("already running"))
                    {
                        await Log("Клиент уже запущен — ждём соединения...");
                    }
                    catch (Exception ex)
                    {
                        await Log($"StartAsync: {ex.Message}");
                        _connectionInfo.IncrementFailedAttempts();
                        forceReconnect = false;
                        await DoBackoffAsync(attempt, ct);
                        continue;
                    }

                    if (await WaitForConnectedAsync(ct)) { success = true; break; }
                    await Log($"Попытка {attempt} не дала результата.");
                    _connectionInfo.IncrementFailedAttempts();
                    forceReconnect = false;
                    await DoBackoffAsync(attempt, ct);
                }

                if (OnReconnectCompleted != null) await OnReconnectCompleted.Invoke(success);
                if (success)
                {
                    await Log("РЕКОННЕКТ УСПЕШЕН");
                    _connectionInfo.LastConnectionTime = DateTime.UtcNow;
                    _connectionInfo.LastConnectReason = "Автоматический реконнект";
                    _connectionInfo.ResetAttempts();
                }
                else if (!ct.IsCancellationRequested && !_isShuttingDown)
                {
                    await Log("РЕКОННЕКТ НЕ УДАЛСЯ");
                }
            }
            catch (OperationCanceledException) { await Log("Реконнект отменён."); }
            catch (Exception ex) { await Log($"Ошибка реконнекта: {ex.Message}"); }
            finally
            {
                ClearExpectedDisconnect();
                lock (_stateLock) { _isReconnecting = false; }
                try { _reconnectLock.Release(); } catch { }
            }
        }

        private async Task<bool> WaitForConnectedAsync(CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(ConnectTimeoutMs);
            var regularDeadline = DateTime.UtcNow.AddSeconds(30);
            var loggedExtended = false;
            while (DateTime.UtcNow < deadline)
            {
                if (_client.ConnectionState == ConnectionState.Connected) { ClearExpectedDisconnect(); return true; }
                if (!loggedExtended && DateTime.UtcNow >= regularDeadline)
                {
                    if (_client.ConnectionState == ConnectionState.Connecting) { await Log("Подключение устанавливается дольше обычного, ждём..."); loggedExtended = true; }
                    else return false;
                }
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return false; }
            }
            return false;
        }

        private async Task DoBackoffAsync(int attempt, CancellationToken ct)
        {
            var idx = Math.Min(attempt - 1, BackoffSeconds.Length - 1);
            var secs = BackoffSeconds[idx];
            await Log($"Пауза {secs} сек перед следующей попыткой...");
            try { await Task.Delay(secs * 1000, ct); } catch (OperationCanceledException) { throw; }
        }

        public void UpdateHeartbeat() { _connectionInfo.LastHeartbeatTime = DateTime.UtcNow; _connectionInfo.HeartbeatMisses = 0; }
        public void MissHeartbeat() { _connectionInfo.HeartbeatMisses++; }
        public void Shutdown() { _isShuttingDown = true; try { _reconnectCts.Cancel(); } catch { } ClearExpectedDisconnect(); }

        private bool IsExpectedDisconnectInProgress() { lock (_stateLock) { return _connectionInfo.IsExpectedDisconnect && _ignoreDisconnectEventsUntil > DateTime.UtcNow; } }
        private void MarkExpectedDisconnect(TimeSpan duration) { lock (_stateLock) { _connectionInfo.IsExpectedDisconnect = true; _ignoreDisconnectEventsUntil = DateTime.UtcNow.Add(duration); } }
        private void ClearExpectedDisconnect() { lock (_stateLock) { _connectionInfo.IsExpectedDisconnect = false; _ignoreDisconnectEventsUntil = DateTime.MinValue; } }
        private Task Log(string message) { BotLogger.Info(LogCategory.Discord, $"[RECONNECT] {message}"); return Task.CompletedTask; }

        public void Dispose()
        {
            _isShuttingDown = true;
            try { _reconnectCts?.Cancel(); } catch { }
            try { _reconnectCts?.Dispose(); } catch { }
            try { _reconnectLock?.Dispose(); } catch { }
            OnReconnectStarted = null; OnReconnectCompleted = null; OnDisconnectDetected = null; OnFullRestartRequested = null;
        }
    }
}
