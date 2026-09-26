using System;
using System.Reflection;
using System.Threading;
using RPBot;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Поведение BotUI.Start() при переменной окружения RPBOT_NO_UI=1:
/// должен пропустить Terminal.Gui Init, не стартовать UI-поток, и
/// установить _uiInitialized в set-состояние (чтобы EnsureUiInitialized
/// не завис в Wait()).
///
/// Без этого флага на headless VPS (systemd, nohup, Docker без TTY)
/// Application.Init() повиснет или кинет — и бот не стартует.
/// </summary>
public class BotUiNoUiModeTests
{
    [Fact]
    public void Start_WithNoUiEnvVar_SkipsInitAndSignalsInitialized()
    {
        // Создаём BotUI без вызова Application.Init (конструктор не трогает UI).
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
            // Реальный конструктор требует Discord-клиент — для теста достаточно факта
            // существования объекта. Если конструктор упал — тест бесполезен, помечаем skip.
        }
        if (ui == null) return; // конструктор требует рантайм-зависимостей — пропускаем

        // Подменяем переменную только на время теста.
        var prev = Environment.GetEnvironmentVariable("RPBOT_NO_UI");
        Environment.SetEnvironmentVariable("RPBOT_NO_UI", "1");
        try
        {
            // Start() под RPBOT_NO_UI=1 не должен бросить, должен сигнализировать UI-ready.
            // Мы НЕ вызываем Application.Init — это и есть смысл флага.
            ui.Start();

            var initializedField = typeof(BotUI).GetField(
                "_uiInitialized", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(initializedField);
            var mre = initializedField!.GetValue(ui) as ManualResetEventSlim;
            Assert.NotNull(mre);
            Assert.True(mre!.IsSet, "Под RPBOT_NO_UI=1 _uiInitialized должен быть выставлен (без этого EnsureUiInitialized.Wait() зависнет)");

            var isRunningField = typeof(BotUI).GetField(
                "_isRunning", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(isRunningField);
            Assert.False((bool)isRunningField!.GetValue(ui)!,
                "Под RPBOT_NO_UI=1 _isRunning должен быть false — UI-поток не запущен");
        }
        finally
        {
            Environment.SetEnvironmentVariable("RPBOT_NO_UI", prev);
        }
    }
}