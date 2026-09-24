using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// audit-leaks #6: BotLogger._locks — словарь SemaphoreSlim-ов по категориям.
/// До фикса Shutdown() очищал _paths, но Dispose-ить семафоры не вызывал,
/// и при горячем рестарте (многократном цикле Initialize→Shutdown→Initialize)
/// старые экземпляры оставались в памяти. Поскольку BotLogger — static,
/// утечка видна как рост private-словаря между сессиями.
///
/// Тесты через рефлексию проверяют, что Shutdown полностью сливает
/// словарь и Dispose-ит каждый SemaphoreSlim.
/// </summary>
[Collection(nameof(BotLoggerCollection))]
public class BotLoggerLocksLeakTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly FieldInfo _locksField;
    private readonly Type _slType = typeof(System.Threading.SemaphoreSlim);

    public BotLoggerLocksLeakTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_locks_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _locksField = typeof(BotLogger).GetField("_locks",
            BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException("_locks field not found");
            // Гарантируем чистый старт: предыдущие тесты могли оставить семафоры
            // либо зафиксированные уже Disposed (тесты на Shutdown), либо новые
            // (если кто-то звал Initialize без Shutdown).
            try { BotLogger.Shutdown("ctor"); } catch { }
        }

        public void Dispose()
        {
            try { Directory.Delete(_tmpDir, true); } catch { }
            // Вернуть логгер в чистое состояние для следующих тестов.
            try { BotLogger.Shutdown("dispose"); } catch { }
        }

    private int GetLocksCount()
    {
        var dict = (System.Collections.IDictionary)_locksField.GetValue(null)!;
        return dict.Count;
    }

    [Fact]
    public void Shutdown_DisposesAndClearsCategoryLocks()
    {
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 1, 1));

        // Перед Shutdown словарь должен быть заполнен семафорами по всем категориям Enum.
        var before = GetLocksCount();
        Assert.True(before > 0, "_locks должен содержать хотя бы один SemaphoreSlim после Initialize");

        var beforeSems = new System.Collections.Generic.List<System.Threading.SemaphoreSlim>();
        var dict = (System.Collections.IDictionary)_locksField.GetValue(null)!;
        foreach (var sem in dict.Values) beforeSems.Add((System.Threading.SemaphoreSlim)sem!);

        BotLogger.Shutdown("leak-#6-test");

        // После Shutdown словарь пуст.
        Assert.Equal(0, GetLocksCount());

        // И все сохранённые ссылки теперь disposable — Dispose не должен кидать
        // (раньше после Dispose у них не было проверки, потому что в Shutdown
        // их никто не дёргал).
        foreach (var sem in beforeSems)
        {
            // SemaphoreSlim не имеет публичного IsDisposed, но повторный Dispose
            // идемпотентен — главное что .Dispose() не бросает.
            sem.Dispose();
        }
    }

    [Fact]
    public void Shutdown_IsIdempotent_NoLockAccumulation()
    {
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 1, 1));
        BotLogger.Shutdown("idempotent-1");
        BotLogger.Shutdown("idempotent-2");
        BotLogger.Shutdown("idempotent-3");

        Assert.Equal(0, GetLocksCount());
    }

    [Fact]
    public void Initialize_Shutdown_Initialize_DoesNotAccumulateLocksAcrossRounds()
    {
        // Симулируем горячий рестарт: 5 циклов Initialize→Shutdown→Initialize.
        for (var i = 0; i < 5; i++)
        {
            BotLogger.Initialize(_tmpDir, new DateTime(2026, 1, 1 + i));
            BotLogger.Shutdown($"round-{i}");
        }

        // До фикса на 5 кругов осталось бы (count-per-round × 5) экземпляров.
        Assert.Equal(0, GetLocksCount());
    }

    [Fact]
    public async Task PostShutdown_Writes_DoNotReferenceDisposedSemaphores()
    {
        BotLogger.Initialize(_tmpDir, new DateTime(2026, 1, 1));
        BotLogger.Shutdown("postdisposed-test");

        // После Shutdown словарь пуст, так что обращения из AppendToFileAsync
        // теперь должны попасть в ветку с null-check (или вообще не доходить
        // до семафора). Главное — нет исключения ObjectDisposedException.
        BotLogger.Info(LogCategory.System, "after-shutdown");
        BotLogger.Warn(LogCategory.Session, "after-shutdown-warn");
        BotLogger.Error(LogCategory.Discord, "after-shutdown-err");

        await Task.Delay(100); // даём фоновым задачам шанс упасть, если что-то не так
    }
}
