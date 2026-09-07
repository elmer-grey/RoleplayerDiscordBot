using System;
using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM;

/// <summary>
/// Чистая логика «повторного броска» по правилам VtM V20.
/// </summary>
/// <remarks>
/// <para>Правило повторных попыток (стр. 286 со ссылкой на стр. 267): персонаж
/// может предпринять повторную попытку того же действия. Размер пула —
/// на 1 кубик меньше исходного; берётся результат повторного броска
/// (а не лучший из двух, как в D&amp;D).</para>
///
/// <para>Аналогия: было 5 кубов → повторный пул = 4 куба. Если повторный пул
/// пуст (исходный пул = 1), повторный бросок невозможен.</para>
///
/// <para>Применяется и в V5 (regular + hunger раздельно): пул уменьшается
/// пропорционально — 1 кубик снимается с regular-пула (или hunger, если
/// regular уже пуст).</para>
/// </remarks>
public static class VampireRepeatReroll
{
    /// <summary>Минимальный размер повторного пула (если 0 — повтор невозможен).</summary>
    public const int MinRepeatPool = 1;

    /// <summary>
    /// Результат повторного броска по правилу повторных попыток V20.
    /// </summary>
    /// <param name="OriginalPoolSize">Размер исходного пула.</param>
    /// <param name="RepeatPoolSize">Размер повторного пула (= <c>OriginalPoolSize − 1</c>, но не меньше 0).</param>
    /// <param name="RepeatDice">Значения кубов повторного броска.</param>
    public sealed record Repeat(
        int OriginalPoolSize,
        int RepeatPoolSize,
        int[] RepeatDice)
    {
        /// <summary>Сколько успехов в повторном пуле (по порогу V20).</summary>
        public int Successes => RepeatDice.Count(d => d >= VampireDicePool.SuccessThreshold);

        /// <summary>Является ли бросок ботчем (0 успехов + хотя бы одна 1).</summary>
        public bool IsBotch => Successes == 0 && RepeatDice.Any(d => d == 1);
    }

    /// <summary>
    /// Вычислить размер повторного пула (на 1 меньше исходного, минимум 0).
    /// </summary>
    public static int ComputeRepeatPoolSize(int originalPoolSize)
    {
        if (originalPoolSize < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(originalPoolSize),
                originalPoolSize,
                "Размер исходного пула не может быть отрицательным.");
        }
        return Math.Max(0, originalPoolSize - 1);
    }

    /// <summary>
    /// Можно ли сделать повторный бросок (исходный пул должен быть ≥ 1).
    /// </summary>
    public static bool CanRepeat(int originalPoolSize)
    {
        return originalPoolSize >= MinRepeatPool;
    }

    /// <summary>
    /// Совершить повторный бросок: кидает <c>originalPoolSize − 1</c> кубов.
    /// </summary>
    /// <param name="originalPoolSize">Размер исходного пула (≥ 0).</param>
    /// <param name="rng">Источник случайных чисел 1..10 включительно.</param>
    /// <exception cref="ArgumentException">Если <paramref name="originalPoolSize"/> &lt; 1.</exception>
    public static Repeat RollRepeat(int originalPoolSize, IRandom rng)
    {
        if (rng == null) throw new ArgumentNullException(nameof(rng));
        if (originalPoolSize < MinRepeatPool)
        {
            throw new ArgumentException(
                $"Повторный бросок требует пула ≥ {MinRepeatPool}, получено {originalPoolSize}.",
                nameof(originalPoolSize));
        }

        var repeatPoolSize = ComputeRepeatPoolSize(originalPoolSize);
        var dice = new int[repeatPoolSize];
        for (var i = 0; i < repeatPoolSize; i++)
        {
            dice[i] = rng.Next(1, 11);
        }

        return new Repeat(originalPoolSize, repeatPoolSize, dice);
    }
}
