using System;
using System.IO;
using RPBot;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Реестр xUnit-коллекции "BotConfig" и общий фикстур — гарантирует, что все тесты,
/// меняющие <c>RPBOT_DATA_DIR</c>, выполняются последовательно (не параллельно),
/// а закешированный в <see cref="BotConfig"/> путь сбрасывается при старте.
/// </summary>
[CollectionDefinition("BotConfig", DisableParallelization = true)]
public class BotConfigCollectionDefinition
{
}

/// <summary>
/// Фикстура коллекции: сбрасывает кеш <see cref="BotConfig"/> один раз до запуска
/// тестов коллекции. Дополнительно IsolatedDataTestBase делает reset в каждом ctor.
/// </summary>
public class BotConfigResetFixture : IDisposable
{
    public BotConfigResetFixture() { BotConfig.ResetForTests(); }
    public void Dispose() { BotConfig.ResetForTests(); }
}

/// <summary>
/// Базовый класс для тестов, которые меняют переменную окружения <c>RPBOT_DATA_DIR</c>.
/// Изолирует данные от других параллельных тестов: поднимает уникальную временную
/// папку, сбрасывает закешированный в <see cref="BotConfig"/> путь, и очищает
/// после прогона.
/// </summary>
/// <remarks>
/// <para>Без сброса <see cref="BotConfig.GetDataDirectory"/> продолжает возвращать
/// путь, закешированный при первом обращении — поэтому параллельные тесты с разными
/// <c>RPBOT_DATA_DIR</c> начинали писать в одну и ту же папку. Это и был flaky.</para>
/// <para>Использование: <c>[Collection("BotConfig")] public class XTests : IsolatedDataTestBase { ctor : base("prefix") {} }</c>.</para>
/// </remarks>
public abstract class IsolatedDataTestBase : IDisposable
{
    private readonly string _tmp;
    private readonly string? _previousDataDir;

    protected IsolatedDataTestBase(string subDir)
    {
        _previousDataDir = Environment.GetEnvironmentVariable(BotConfig.DataRootEnvVar);
        _tmp = Path.Combine(Path.GetTempPath(), $"{subDir}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
        Environment.SetEnvironmentVariable(BotConfig.DataRootEnvVar, _tmp);
        BotConfig.ResetForTests();
    }

    public virtual void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* ignore */ }
        Environment.SetEnvironmentVariable(BotConfig.DataRootEnvVar, _previousDataDir);
        BotConfig.ResetForTests();
    }

    protected string TempDir => _tmp;
}
