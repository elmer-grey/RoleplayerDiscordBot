using System.Security.Cryptography;

namespace RPBot;

/// <summary>
/// Криптографически стойкие броски (CSPRNG вместо псевдослучайного Random.Shared):
/// распределение одинаково равномерное, но результат непредсказуем даже при знании
/// внутреннего состояния генератора и истории предыдущих бросков.
/// </summary>
public static class SecureDice
{
    /// <summary>Целое число в диапазоне [min, max] (обе границы включительно).</summary>
    public static int Next(int min, int max) => RandomNumberGenerator.GetInt32(min, max + 1);
}
