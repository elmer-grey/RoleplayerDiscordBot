using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace RPBot
{
    /// <summary>
    /// Отвечает за ВСЁ, что связано с переподключением бота
    /// </summary>
    public class ReconnectionService : IDisposable
    {
        private readonly DiscordSocketClient _client;
        private readonly ConnectionStateInfo _connectionInfo;
        private readonly SemaphoreSlim _reconnectLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _reconnectCts = new();

        private bool _isReconnecting = false;
        private bool _isShuttingDown = false;

        // Порог по числу последовательных попыток реконнекта, после которого требуется полный рестарт
        private const int MaxReconnectAttemptsBeforeFullRestart = 8;

        // События для оповещения других частей бота
        public event Func<string, Task> OnReconnectStarted;
        public event Func<bool, Task> OnReconnectCompleted;
        public event Func<Exception, Task> OnDisconnectDetected;
        // Событие запроса полной перезагрузки клиента (после многократных неудачных попыток)
        public event Func<Task> OnFullRestartRequested;

        public ConnectionStateInfo ConnectionInfo => _connectionInfo;

        public ReconnectionService(DiscordSocketClient client)
        {
            _client = client;
            _connectionInfo = new ConnectionStateInfo();
        }

        /// <summary>
        /// Вызывается при отключении бота
        /// </summary>
        public async Task HandleDisconnect(Exception exception)
        {
            if (_isShuttingDown) return;

            // Сохраняем причину
            var reason = DisconnectReasonTranslator.GetFriendlyReason(exception);
            _connectionInfo.AddDisconnectReason(reason, exception?.Message);

            // Оповещаем подписчиков
            if (OnDisconnectDetected != null)
                await OnDisconnectDetected.Invoke(exception);

            // Плановый реконнект Discord - не вмешиваемся
            if (exception is GatewayReconnectException)
            {
                await Log("Плановый реконнект Discord - пропускаем");
                return;
            }

            // Отменяем старый реконнект
            try { _reconnectCts.Cancel(); } catch { }
            _reconnectCts = new CancellationTokenSource();

            // Ждем 2 секунды перед началом
            await Task.Delay(2000);

            // Запускаем реконнект
            _ = Task.Run(() => ReconnectAsync(_reconnectCts.Token));
        }

        /// <summary>
        /// Основная логика реконнекта
        /// </summary>
        private async Task ReconnectAsync(CancellationToken cancellationToken)
        {
            if (!await _reconnectLock.WaitAsync(0, cancellationToken))
            {
                await Log(" Реконнект уже выполняется");
                return;
            }

            try
            {
                _isReconnecting = true;
                _connectionInfo.ReconnectAttempts++;

                // Если превысили порог последовательных попыток — жалуемся и просим полный рестарт
                if (_connectionInfo.ReconnectAttempts >= MaxReconnectAttemptsBeforeFullRestart)
                {
                    await Log($"Превышен лимит попыток реконнекта ({_connectionInfo.ReconnectAttempts}). Запрос полного перезапуска клиента.");
                    try
                    {
                        if (OnFullRestartRequested != null)
                            await OnFullRestartRequested.Invoke();
                    }
                    catch (Exception ex)
                    {
                        await Log($"Ошибка при запросе полного перезапуска: {ex.Message}");
                    }
                    return;
                }

                if (_client.ConnectionState == ConnectionState.Connected)
                {
                    await Log(" Клиент уже подключен, реконнект не требуется");
                    _connectionInfo.ResetAttempts();
                    if (OnReconnectCompleted != null) await OnReconnectCompleted.Invoke(true);
                    return;
                }

                await Log($"=== НАЧАЛО РЕКОННЕКТА #{_connectionInfo.ReconnectAttempts} ===");
                if (OnReconnectStarted != null) await OnReconnectStarted.Invoke($"Попытка #{_connectionInfo.ReconnectAttempts}");

                // ВАЖНО: Сначала убеждаемся, что клиент полностью остановлен
                if (_client.ConnectionState != ConnectionState.Disconnected)
                {
                    await Log(" Останавливаем клиент перед перезапуском...");
                    try { await _client.StopAsync(); } catch (Exception ex) { await Log($"Error stopping client before restart: {ex.Message}"); }
                    await Task.Delay(2000, cancellationToken);
                }

                await ExponentialDelay(cancellationToken);

                // Пытаемся подключиться
                bool success = await TryConnectWithRetries(cancellationToken);

                if (OnReconnectCompleted != null) await OnReconnectCompleted.Invoke(success);

                if (success)
                {
                    await Log(" РЕКОННЕКТ УСПЕШЕН");
                    _connectionInfo.ResetAttempts();
                }
                else
                {
                    await Log(" РЕКОННЕКТ НЕ УДАЛСЯ");
                    _connectionInfo.IncrementFailedAttempts();

                    // Если превысили порог в процессе попыток — также инициируем полный рестарт
                    if (_connectionInfo.ReconnectAttempts >= MaxReconnectAttemptsBeforeFullRestart)
                    {
                        await Log($"Превышен лимит попыток реконнекта после неудачи ({_connectionInfo.ReconnectAttempts}). Запрос полного перезапуска клиента.");
                        try
                        {
                            if (OnFullRestartRequested != null)
                                await OnFullRestartRequested.Invoke();
                        }
                        catch (Exception ex)
                        {
                            await Log($"Ошибка при запросе полного перезапуска: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await Log(" Реконнект отменен");
            }
            catch (Exception ex)
            {
                await Log($" Ошибка реконнекта: {ex.Message}");
            }
            finally
            {
                _isReconnecting = false;
                try { _reconnectLock.Release(); } catch { }
            }
        }

        /// <summary>
        /// Попытки подключения с увеличивающейся задержкой
        /// </summary>
        private async Task<bool> TryConnectWithRetries(CancellationToken cancellationToken)
        {
            int maxAttempts = 5;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (_isShuttingDown) break;

                // Снова проверяем состояние перед попыткой
                if (_client.ConnectionState == ConnectionState.Connected)
                {
                    await Log(" Клиент подключился сам");
                    return true;
                }

                try
                {
                    await Log($" Попытка подключения {attempt}/{maxAttempts}...");

                    // Убеждаемся, что клиент остановлен
                    if (_client.ConnectionState != ConnectionState.Disconnected)
                    {
                        await Log(" Останавливаем клиент перед попыткой...");
                        await _client.StopAsync();
                        await Task.Delay(1000, cancellationToken);
                    }

                    // Пробуем подключиться
                    await _client.StartAsync();

                    // Ждем подключения с таймаутом
                    var startTime = DateTime.UtcNow;
                    while ((DateTime.UtcNow - startTime).TotalSeconds < 30)
                    {
                        if (_client.ConnectionState == ConnectionState.Connected)
                        {
                            await Log($" Подключено на попытке {attempt}");
                            return true;
                        }

                        if (_client.ConnectionState == ConnectionState.Disconnected)
                        {
                            // Если отключились - выходим из цикла ожидания
                            break;
                        }

                        await Task.Delay(500, cancellationToken);
                    }

                    // Если не подключились, останавливаем и пробуем снова
                    await Log($" Таймаут попытки {attempt}");
                    await _client.StopAsync();

                    // Экспоненциальная задержка между попытками
                    if (attempt < maxAttempts)
                    {
                        var delay = Math.Min(30000, 2000 * (int)Math.Pow(2, attempt - 1));
                        await Log($" Ожидание {delay / 1000} сек перед следующей попыткой...");
                        await Task.Delay(delay, cancellationToken);
                    }
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("already running"))
                {
                    await Log($" Клиент уже запущен на попытке {attempt}, останавливаем...");
                    try { await _client.StopAsync(); } catch { }
                    await Task.Delay(2000, cancellationToken);
                }
                catch (Exception ex)
                {
                    await Log($" Ошибка попытки {attempt}: {ex.Message}");

                    if (attempt < maxAttempts)
                    {
                        var delay = 5000 * attempt;
                        await Task.Delay(delay, cancellationToken);
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Экспоненциальная задержка между попытками
        /// </summary>
        private async Task ExponentialDelay(CancellationToken cancellationToken)
        {
            var delaySeconds = Math.Min(60, 5 * (int)Math.Pow(2, Math.Min(_connectionInfo.ReconnectAttempts - 1, 6)));

            if (delaySeconds > 0)
            {
                await Log($"Ожидание {delaySeconds} сек перед попыткой...");
                await Task.Delay(delaySeconds * 1000, cancellationToken);
            }
        }

        /// <summary>
        /// Полная перезагрузка клиента (создание с нуля)
        /// </summary>
        public async Task FullRestartAsync()
        {
            await Log("ПОЛНАЯ ПЕРЕЗАГРУЗКА КЛИЕНТА");

            try
            {
                // Уведомляем о перезагрузке
                _isShuttingDown = true;

                // Полная остановка
                try { await _client.StopAsync(); } catch (Exception ex) { await Log($"Error stopping client during FullRestart: {ex.Message}"); }
                await Task.Delay(3000);

                // Сигнал для Program.cs на пересоздание клиента
                _connectionInfo.AddDisconnectReason("Полная перезагрузка", "Требуется пересоздание клиента");

                // Program.cs должен поймать это и пересоздать клиент
                throw new InvalidOperationException("FULL_RESTART_REQUIRED");
            }
            catch (Exception ex)
            {
                await Log($"Ошибка полной перезагрузки: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Останавливает клиент корректно
        /// </summary>
        private async Task StopClientGracefully()
        {
            try
            {
                if (_client.ConnectionState != ConnectionState.Disconnected)
                {
                    await Log("⏹️ Остановка клиента...");
                    await _client.StopAsync();
                    await Task.Delay(1000);
                }
            }
            catch (Exception ex)
            {
                await Log($"⚠️ Ошибка остановки: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновляет heartbeat для мониторинга
        /// </summary>
        public void UpdateHeartbeat()
        {
            _connectionInfo.LastHeartbeatTime = DateTime.UtcNow;
            _connectionInfo.HeartbeatMisses = 0;
        }

        /// <summary>
        /// Отмечает пропущенный heartbeat
        /// </summary>
        public void MissHeartbeat()
        {
            _connectionInfo.HeartbeatMisses++;
        }

        /// <summary>
        /// Выключает сервис (при остановке бота)
        /// </summary>
        public void Shutdown()
        {
            _isShuttingDown = true;
            try { _reconnectCts.Cancel(); } catch (Exception ex) { Console.WriteLine($"Error cancelling reconnect token: {ex.Message}"); }
        }

        private Task Log(string message)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [RECONNECT] {message}");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _isShuttingDown = true;
            try { _reconnectCts?.Cancel(); } catch (Exception ex) { Console.WriteLine($"Error cancelling reconnect token during dispose: {ex.Message}"); }
            try { _reconnectCts?.Dispose(); } catch (Exception ex) { Console.WriteLine($"Error disposing reconnect token: {ex.Message}"); }
            try { _reconnectLock?.Dispose(); } catch (Exception ex) { Console.WriteLine($"Error disposing reconnect lock: {ex.Message}"); }

            // Clear event subscribers to avoid keeping references
            OnReconnectStarted = null;
            OnReconnectCompleted = null;
            OnDisconnectDetected = null;
            OnFullRestartRequested = null;
        }
    }
}
