using System;
using System.Threading;
using System.Threading.Tasks;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты на <see cref="VampireRollMenuAutoRollback"/>: автооткат picker'а
/// через 15 с (согласование 2026-09-09: «если открыт выбор количества кубиков
/// и проходит 15 секунд, то меню откатывается обратно, где выбор морали или
/// полный переброс»).
/// </summary>
public class VampireRollMenuAutoRollbackTests
{
    [Fact]
    public void Schedule_AddsEntry_And_CancelRemovesIt()
    {
        VampireRollMenuAutoRollback.Clear();
        try
        {
            var called = 0;
            VampireRollMenuAutoRollback.Schedule(
                100UL,
                VampireRollMenuAutoRollback.MenuState.RerollPicker(42),
                _ => { called++; return Task.CompletedTask; },
                timeout: TimeSpan.FromMilliseconds(50));

            Assert.Equal(1, VampireRollMenuAutoRollback.ActiveCount);

            // Отменяем — откат не должен сработать.
            VampireRollMenuAutoRollback.Cancel(100UL);
            Assert.Equal(0, VampireRollMenuAutoRollback.ActiveCount);

            // Ждём больше, чем timeout, чтобы убедиться, что callback не вызвался.
            Thread.Sleep(150);
            Assert.Equal(0, called);
        }
        finally
        {
            VampireRollMenuAutoRollback.Clear();
        }
    }

    [Fact]
    public async Task Schedule_FiresRollback_AfterTimeout()
    {
        VampireRollMenuAutoRollback.Clear();
        try
        {
            var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            VampireRollMenuAutoRollback.Schedule(
                200UL,
                VampireRollMenuAutoRollback.MenuState.RerollPicker(42),
                _ => { fired.TrySetResult(true); return Task.CompletedTask; },
                timeout: TimeSpan.FromMilliseconds(50));

            // Ждём срабатывания.
            var completed = await Task.WhenAny(fired.Task, Task.Delay(2000));
            Assert.Same(fired.Task, completed);
            Assert.True(await fired.Task);
            Assert.Equal(0, VampireRollMenuAutoRollback.ActiveCount);
        }
        finally
        {
            VampireRollMenuAutoRollback.Clear();
        }
    }

    [Fact]
    public async Task Schedule_RaceWithCancel_CallbackDoesNotFire()
    {
        VampireRollMenuAutoRollback.Clear();
        try
        {
            var fired = false;
            VampireRollMenuAutoRollback.Schedule(
                300UL,
                VampireRollMenuAutoRollback.MenuState.RerollPicker(42),
                _ => { fired = true; return Task.CompletedTask; },
                timeout: TimeSpan.FromMilliseconds(30));

            // Сразу отменяем — имитация «пользователь успел нажать».
            VampireRollMenuAutoRollback.Cancel(300UL);

            await Task.Delay(200);
            Assert.False(fired);
        }
        finally
        {
            VampireRollMenuAutoRollback.Clear();
        }
    }

    [Fact]
    public void Schedule_TwiceOnSameMessage_CancelsPrevious()
    {
        VampireRollMenuAutoRollback.Clear();
        try
        {
            var calls = 0;
            VampireRollMenuAutoRollback.Schedule(
                400UL,
                VampireRollMenuAutoRollback.MenuState.RerollPicker(42),
                _ => { calls++; return Task.CompletedTask; },
                timeout: TimeSpan.FromMilliseconds(30));

            // Повторный Schedule на тот же messageId.
            VampireRollMenuAutoRollback.Schedule(
                400UL,
                VampireRollMenuAutoRollback.MenuState.RerollPicker(42),
                _ => { calls++; return Task.CompletedTask; },
                timeout: TimeSpan.FromMilliseconds(50));

            // Активен должен быть только один таймер.
            Assert.Equal(1, VampireRollMenuAutoRollback.ActiveCount);
        }
        finally
        {
            VampireRollMenuAutoRollback.Clear();
        }
    }

    [Fact]
    public void Cancel_OnUnknownMessage_DoesNotThrow()
    {
        VampireRollMenuAutoRollback.Clear();
        // Не падать.
        VampireRollMenuAutoRollback.Cancel(999_999UL);
        Assert.Equal(0, VampireRollMenuAutoRollback.ActiveCount);
    }

    [Fact]
    public void Schedule_ThrowsOnNullCallback()
    {
        Assert.Throws<ArgumentNullException>(() =>
            VampireRollMenuAutoRollback.Schedule(
                1UL,
                VampireRollMenuAutoRollback.MenuState.RerollPicker(42),
                null!));
    }

    [Fact]
    public async Task Schedule_RollbackThrows_DoesNotPropagate()
    {
        VampireRollMenuAutoRollback.Clear();
        try
        {
            // Бросающее исключение внутри callback (например, сообщение удалено)
            // не должно валить фоновую задачу.
            VampireRollMenuAutoRollback.Schedule(
                500UL,
                VampireRollMenuAutoRollback.MenuState.RerollPicker(42),
                _ => throw new InvalidOperationException("Message gone"),
                timeout: TimeSpan.FromMilliseconds(30));

            await Task.Delay(150);
            Assert.Equal(0, VampireRollMenuAutoRollback.ActiveCount);
        }
        finally
        {
            VampireRollMenuAutoRollback.Clear();
        }
    }
}
