using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RPBot;

namespace RPBot;

/// <summary>
/// Следит за «логическим днём» (см. <see cref="LogDayResolver"/>) и переключает
/// <see cref="BotLogger"/> на новую суточную папку в момент <c>CutoffHour</c>:00.
/// </summary>
/// <remarks>
/// Создаётся один раз при запуске бота, работает как обычный фоновый таймер.
/// Тик раз в минуту (минимально-достаточно, учитывая что cutoff-hour — это час,
/// а не минута). При смене дня пересоздаёт файлы логов через <see cref="BotLogger.RolloverToDay"/>.
///
/// Не падает, если папка дня уже существует — просто дописывает в неё.
/// </remarks>
public sealed class LogDayRolloverService : IDisposable
{
    private readonly string _logRoot;
    private readonly int _cutoffHour;
    private readonly TimeSpan _tickInterval;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _currentDayKey;
    private bool _disposed;

    /// <summary>
    /// Колбэк, вызываемый при смене дня. Параметр — новый идентификатор дня (yyyyMMdd).
    /// Позволяет внешним подсистемам (predictions.log и т.п.) подстроить свои пути.
    /// </summary>
    public Action<string>? OnDayRollover { get; set; }

    public LogDayRolloverService(string logRoot, int cutoffHour = LogDayResolver.DefaultCutoffHour)
    {
        _logRoot = logRoot;
        _cutoffHour = cutoffHour;
        _tickInterval = TimeSpan.FromMinutes(1);
        _currentDayKey = LogDayResolver.ResolveDay(DateTime.Now, cutoffHour);
    }

    public string CurrentDayKey => _currentDayKey;

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.Now;
                    var dayKey = LogDayResolver.ResolveDay(now, _cutoffHour);
                    if (!string.Equals(dayKey, _currentDayKey, StringComparison.Ordinal))
                    {
                        await PerformRolloverAsync(dayKey).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Логгер уже мог быть погашен (Shutdown) — не падаем.
                }
                try { await Task.Delay(_tickInterval, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
        catch (OperationCanceledException) { /* штатное завершение */ }
        catch { /* не фатально — логгер просто не сможет переключиться */ }
    }

    private async Task PerformRolloverAsync(string newDayKey)
    {
        // Сначала вызываем колбэк (внешние подсистемы подстроятся).
        try { OnDayRollover?.Invoke(newDayKey); } catch { /* идемпотентно */ }

        // Затем переключаем BotLogger на новую папку.
        try
        {
            BotLogger.RolloverToDay(_logRoot, newDayKey);
        }
        catch
        {
            // Даже если BotLogger не смог — продолжаем работу.
        }

        _currentDayKey = newDayKey;
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }
}
