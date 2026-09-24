using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Все тесты, которые трогают статический BotLogger, должны быть в одной коллекции,
/// иначе xUnit запускает их параллельно и они ломают общее состояние
/// (_paths, _locks, _unifiedLogPath).
/// </summary>
[CollectionDefinition(nameof(BotLoggerCollection))]
public class BotLoggerCollection
{
}

/// <summary>
/// Тесты, которые трогают глобальный кеш BotConfig._dataRootOverride
/// (через GetDataRootDirectory или прямую рефлексию), нельзя запускать
/// параллельно — иначе один тест сбрасывает кеш и подменяет переменные
/// окружения, пока другой читает результат.
/// </summary>
[CollectionDefinition(nameof(BotConfigCollection))]
public class BotConfigCollection
{
}
