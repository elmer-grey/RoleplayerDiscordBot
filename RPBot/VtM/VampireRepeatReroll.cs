using System;
using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM;

/// <summary>
/// Чистая логика «повторной попытки» по правилам VtM V20.
/// </summary>
/// <remarks>
/// <para>Правило повторных попыток (V20, стр. 286, глава «Повторные попытки»):
/// если персонажу не удалось действие, он может попробовать снова.
/// <b>Сложность следующей проверки возрастает на 1 пункт</b> (рассказчик может
/// оставить сложность без изменений), а <b>размер пула не меняется</b>.
/// Если и вторая попытка не удалась — сложность растёт ещё на 1, и так далее.</para>
///
/// <para>Применяется и в V5-гибриде: пул остаётся исходным (regular + hunger),
/// меняется только сложность. Повторный бросок бесплатен (без траты воли).</para>
///
/// <para>Это отличается от <see cref="WillpowerReroll"/>: переброс за 1 пункт
/// воли перебрасывает часть кубиков (худшие regular, 1-3 штуки), а повторная
/// попытка — это новый бросок всего пула с повышенной сложностью.</para>
///
/// <para>Пример (V20, стр. 286): проверка смекалки + уличного чутья, сложность 6.
/// Попытка 1: сложность 6. Попытка 2 (если неуспех): сложность 7. Попытка 3: 8. И т.д.</para>
/// </remarks>
public static class VampireRepeatReroll
{
    /// <summary>Минимальный размер пула, при котором повторная попытка осмысленна (≥ 1).</summary>
    public const int MinRepeatPool = 1;

    /// <summary>
    /// Результат повторной попытки по правилу V20 (стр. 286).
    /// </summary>
    /// <param name="OriginalPoolSize">Размер исходного пула (не меняется при повторе).</param>
    /// <param name="OriginalDifficulty">Сложность исходной проверки.</param>
    /// <param name="NewDifficulty">Сложность повторной попытки = <c>OriginalDifficulty + 1</c>.</param>
    /// <param name="NewRegularDice">Значения кубов повторного броска (regular).</param>
    /// <param name="NewHungerDice">Значения кубов повторного броска (hunger, V5).</param>
    public sealed record Repeat(
        int OriginalPoolSize,
        int OriginalDifficulty,
        int NewDifficulty,
        int[] NewRegularDice,
        int[] NewHungerDice)
    {
        /// <summary>Сколько успехов в повторном броске (regular 6-10 + hunger 6-9, без двойки за крит).</summary>
        public int Successes => VampireDicePool.CountSuccessesHybrid(
            NewRegularDice ?? Array.Empty<int>(),
            NewHungerDice ?? Array.Empty<int>(),
            null);

        /// <summary>Является ли бросок ботчем (0 успехов + хотя бы одна 1).</summary>
        public bool IsBotch =>
            Successes == 0
            && ((NewRegularDice?.Any(d => d == 1) ?? false)
                || (NewHungerDice?.Any(d => d == 1) ?? false));
    }

    /// <summary>
    /// Можно ли сделать повторный бросок (исходный пул должен быть ≥ 1).
    /// </summary>
    public static bool CanRepeat(int originalPoolSize)
    {
        return originalPoolSize >= MinRepeatPool;
    }

    /// <summary>
    /// Вычислить новую сложность для повторной попытки (по V20 стр. 286: +1).
    /// Рассказчик может отказаться от повышения — в нашей механике это решение
    /// остаётся за рассказчиком, бот всегда предлагает +1 по умолчанию.
    /// </summary>
    public static int ComputeRepeatDifficulty(int originalDifficulty)
    {
        if (originalDifficulty < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(originalDifficulty),
                originalDifficulty,
                "Сложность не может быть ниже 2 (минимум по правилам V20).");
        }
        return originalDifficulty + 1;
    }

    /// <summary>
    /// Совершить повторный бросок по правилу V20: кидает <b>тот же</b> пул,
    /// но со сложностью <c>originalDifficulty + 1</c>.
    /// </summary>
    /// <param name="originalPoolSize">Размер исходного пула (≥ 1).</param>
    /// <param name="originalDifficulty">Сложность исходной проверки (≥ 2).</param>
    /// <param name="regularCount">Сколько regular-кубиков в исходном пуле.</param>
    /// <param name="hungerCount">Сколько hunger-кубиков в исходном пуле (V5; для V20 = 0).</param>
    /// <param name="rng">Источник случайных чисел 1..10 включительно.</param>
    /// <exception cref="ArgumentException">Если <paramref name="originalPoolSize"/> &lt; 1.</exception>
    public static Repeat RollRepeat(
        int originalPoolSize,
        int originalDifficulty,
        int regularCount,
        int hungerCount,
        IRandom rng)
    {
        if (rng == null) throw new ArgumentNullException(nameof(rng));
        if (originalPoolSize < MinRepeatPool)
        {
            throw new ArgumentException(
                $"Повторный бросок требует пула ≥ {MinRepeatPool}, получено {originalPoolSize}.",
                nameof(originalPoolSize));
        }
        if (regularCount < 0 || hungerCount < 0)
        {
            throw new ArgumentException(
                "regularCount и hungerCount не могут быть отрицательными.",
                nameof(regularCount));
        }
        if (regularCount + hungerCount != originalPoolSize)
        {
            throw new ArgumentException(
                $"regularCount ({regularCount}) + hungerCount ({hungerCount}) ≠ originalPoolSize ({originalPoolSize}).",
                nameof(originalPoolSize));
        }

        var newDifficulty = ComputeRepeatDifficulty(originalDifficulty);
        var regular = new int[regularCount];
        var hunger = new int[hungerCount];
        for (var i = 0; i < regularCount; i++) regular[i] = rng.Next(1, 11);
        for (var i = 0; i < hungerCount; i++) hunger[i] = rng.Next(1, 11);

        return new Repeat(
            OriginalPoolSize: originalPoolSize,
            OriginalDifficulty: originalDifficulty,
            NewDifficulty: newDifficulty,
            NewRegularDice: regular,
            NewHungerDice: hunger);
    }
}
