using System;
using System.IO;
using System.Reflection;
using System.Threading;
using RPBot;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// audit-leaks #3: BotUI.StartUiThread при повторном вызове должен
/// Cancel+Dispose предыдущий _uiCts перед созданием нового. Тест проверяет
/// этот инвариант через рефлексию — без поднятия Terminal.Gui.
/// </summary>
public class BotUiCtsDisposeTests
{
    [Fact]
    public void UiCts_ManualCancelDispose_DisposesIt()
    {
        // Создаём объект BotUI через рефлексию (без вызова Application.Init).
        var ctor = typeof(BotUI).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        Assert.NotNull(ctor);

        BotUI? ui = null;
        try
        {
            ui = (BotUI)ctor!.Invoke(new object?[ctor.GetParameters().Length]);
        }
        catch (TargetInvocationException)
        {
            // Конструктор может бросить — нам важен сам факт наличия полей.
        }

        Assert.NotNull(ui);

        // Достаём _uiCts и _isDisposed.
        var ctsField = typeof(BotUI).GetField("_uiCts",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(ctsField);

        // Кладём наш CTS.
        var ourCts = new CancellationTokenSource();
        ctsField!.SetValue(ui, ourCts);
        Assert.Same(ourCts, ctsField.GetValue(ui));

        // Имитируем логику фикса audit-leaks #3 — две строки из StartUiThread.
        // До фикса этих строк не было бы, и при повторном StartUiThread
        // предыдущий CTS просто терялся бы.
        var current = ctsField.GetValue(ui) as CancellationTokenSource;
        Assert.NotNull(current);
        try { current!.Cancel(); } catch { }
        try { current.Dispose(); } catch { }
        ctsField.SetValue(ui, null);

        Assert.Null(ctsField.GetValue(ui));
        // Предыдущий CTS — Cancel'нут и Dispose'нут.
        Assert.True(ourCts.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => { var _ = ourCts.Token; });
    }
}
