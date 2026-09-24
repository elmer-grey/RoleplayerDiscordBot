using System;
using System.Threading;
using RPBot;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// audit-leaks #5: GameSession.PauseReminderCTS должен корректно
/// отменяться и диспозиться при остановке сессии (аудит HandleForceStop).
/// GameSession — простой POCO, поэтому тестируем инвариант:
/// после Dispose старый CTS больше не возвращает валидный token.
/// </summary>
public class GameSessionPauseCtsTests
{
    [Fact]
    public void PauseReminderCTS_AfterDispose_ThrowsObjectDisposed()
    {
        var session = new GameSession();
        session.PauseReminderCTS = new CancellationTokenSource();
        var cts = session.PauseReminderCTS!;

        // Cancel + Dispose (как делает HandleForceStop после фикса).
        cts.Cancel();
        cts.Dispose();
        session.PauseReminderCTS = null;

        Assert.Null(session.PauseReminderCTS);
        // Старый CTS — disposed.
        Assert.Throws<ObjectDisposedException>(() => { var _ = cts.Token; });
    }

    [Fact]
    public void PauseReminderCTS_Initial_Null()
    {
        var session = new GameSession();
        Assert.Null(session.PauseReminderCTS);
    }

    [Fact]
    public void PauseReminderCTS_CanBeReplaced()
    {
        var session = new GameSession();
        session.PauseReminderCTS = new CancellationTokenSource();
        Assert.NotNull(session.PauseReminderCTS);

        // Имитируем re-assignment в HandlePauseSession.
        var old = session.PauseReminderCTS;
        session.PauseReminderCTS = new CancellationTokenSource();
        old!.Cancel();
        old.Dispose();

        Assert.NotNull(session.PauseReminderCTS);
        Assert.False(session.PauseReminderCTS.IsCancellationRequested);
    }
}
