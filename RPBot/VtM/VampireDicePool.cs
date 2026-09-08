using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RPBot.VtM
{
    /// <summary>
    /// Доменная модель, итоговая и согласованная с пользователем 2026-09-01:
    ///
    ///   1) Regular пул:
    ///      • 6–9 → +1, 10 → +1, 1 → −1, 2–5 → 0.
    ///      • Каждая X "съедает" один обычный успех 6–9 (не 10).
    ///      • Лишние X (больше, чем 6–9) дают −1; лишние 6–9 (больше, чем X) дают +1.
    ///      • Десятки неуязвимы для X.
    ///
    ///   2) Hunger пул:
    ///      • голодный 6–9 или 10 — всегда +1;
    ///      • голодный X/! трактуется как «голод» (Messy/Bestial) ТОЛЬКО если:
    ///          а) в regular нет ни одного «+» ПОСЛЕ поедания X→6–9, И
    ///          б) в regular есть хотя бы один крит (X или 10) в исходном наборе.
    ///      • Иначе голодный X = −1, голодный ! = +1 (как обычный куб).
    ///      • На бросок — не более одного Messy/Bestial (флаг + значение).
    ///
    ///   3) Доп. кубик: если и в hunger, и в regular есть и X, и !, кидается ещё один d10:
    ///      X→Bestial, !→Messy, 6–9→+1, 2–5→0.
    ///
    ///   4) Итог = RegularΣ + HungerΣ (+ значение доп. кубика, если был).
    ///      > 0 — успех, = 0 — нейтральный провал, < 0 — ботч.
    ///
    /// Класс намеренно не зависит от Discord.Net — только System.Random через IRandom,
    /// чтобы тесты могли подсунуть детерминированный источник.
    /// </summary>
    public static class VampireDicePool
    {
        public const int SuccessThreshold = 6; // на кубе ≥6 — успех (V20)
        public const int MaxSingleAttribute = 5; // потолок одного параметра в v20
        public const int MaxHunger = 5; // V5 Hunger 0..5
        public const int MaxTotalDice = 30; // предохранитель — больше бросать не даём

        /// <summary>
        /// Бросок пула по правилам VtM v20 (без голодных кубов).
        /// </summary>
        public static V20RollResult RollV20(int poolSize, IRandom rng)
        {
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            if (poolSize < 1)
                throw new ArgumentOutOfRangeException(nameof(poolSize), "Пул должен быть не меньше 1.");

            var poolSizeClamped = Math.Min(poolSize, MaxTotalDice);
            var dice = RollDice(poolSizeClamped, rng);

            return EvaluateV20(dice);
        }

        /// <summary>
        /// Бросок пула по правилам VtM v5 — обычные кубы + голодные.
        /// regular = max(poolSize - hunger, 0), hungerDice = min(hunger, poolSize, MaxHunger).
        /// </summary>
        public static V5RollResult RollV5(int poolSize, int hunger, IRandom rng)
        {
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            if (poolSize < 1)
                throw new ArgumentOutOfRangeException(nameof(poolSize), "Пул должен быть не меньше 1.");
            if (hunger < 0 || hunger > MaxHunger)
                throw new ArgumentOutOfRangeException(nameof(hunger), $"Голод должен быть в диапазоне 0..{MaxHunger}.");
            if (poolSize < hunger)
                throw new ArgumentException(
                    $"Голод ({hunger}) не может превышать размер пула ({poolSize}).",
                    nameof(hunger));

            var poolSizeClamped = Math.Min(poolSize, MaxTotalDice);
            var regularCount = poolSizeClamped - hunger;
            var hungerCount = hunger;

            var allDice = RollDice(poolSizeClamped, rng);
            var regularDice = allDice.Take(regularCount).ToArray();
            var hungerDice = allDice.Skip(regularCount).Take(hungerCount).ToArray();

            // Доп. кубик: кидается, если и в hunger, и в regular есть и X, и !
            int? bonusDie = null;
            bool regularHasBothCrits = regularDice.Any(d => d == 1) && regularDice.Any(d => d == 10);
            bool hungerHasBothCrits = hungerDice.Any(d => d == 1) && hungerDice.Any(d => d == 10);
            if (regularHasBothCrits && hungerHasBothCrits)
            {
                bonusDie = rng.Next(1, 11);
            }

            return EvaluateV5(regularDice, hungerDice, bonusDie);
        }

        // ---------- helpers ----------

        private static int[] RollDice(int count, IRandom rng)
        {
            var result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = rng.Next(1, 11); // [1..10] — обе границы включительно
            return result;
        }

        /// <summary>
        /// Чистая оценка уже брошенных кубов по правилам V20. Полезно для тестов.
        /// </summary>
        public static V20RollResult EvaluateV20(IReadOnlyList<int> dice)
        {
            if (dice == null) throw new ArgumentNullException(nameof(dice));
            if (dice.Count == 0) throw new ArgumentException("Пул не может быть пустым.", nameof(dice));

            int successes = dice.Count(d => d >= SuccessThreshold);
            bool isBotch = successes == 0 && dice.Any(d => d == 1);
            return new V20RollResult(dice.ToArray(), successes, isBotch);
        }

        /// <summary>
        /// V20: специализация удваивает десятки (каждая «10» = 2 успеха вместо 1).
        /// Без специализации результат идентичен стандартному V20-подсчёту.
        /// </summary>
        /// <param name="dice">Уже брошенные кубы.</param>
        /// <param name="hasSpecialization">
        /// Если <c>true</c> — каждая 10 даёт 2 успеха (V20). Если <c>false</c> — обычный подсчёт.
        /// </param>
        /// <returns>Число успехов с учётом специализации.</returns>
        public static int CountSuccessesWithSpecialization(
            IReadOnlyList<int> dice, bool hasSpecialization)
        {
            if (dice == null) throw new ArgumentNullException(nameof(dice));
            int tens = dice.Count(d => d == 10);
            int sixesToNines = dice.Count(d => d >= 6 && d < 10);
            if (hasSpecialization)
            {
                // каждая 10 = 2 успеха
                return sixesToNines + (tens * 2);
            }
            // стандартный V20 (одна 10 = один успех)
            return sixesToNines + tens;
        }

        /// <summary>
        /// Гибридный подсчёт V20 + V5:
        ///   • Regular: V5-правило «1 съедает один успех» (минус 1 за каждую 1-цу),
        ///     плюс обычный V20-подсчёт (6–9 = 1, 10 = 1 или 2 при специализации).
        ///   • Hunger: голодные 1-цы НЕ съедают успехи — они только триггерят
        ///     Messy/Bestial Critical, который подсчитывается отдельно.
        ///   • Специализация действует ТОЛЬКО на regular.
        /// </summary>
        /// <param name="regularDice">Regular-кубы (V5-разбиение).</param>
        /// <param name="hungerDice">Hunger-кубы (V5-разбиение).</param>
        /// <param name="specialization">Название специализации (пусто/пробелы = нет).</param>
        public static int CountSuccessesHybrid(
            IReadOnlyList<int> regularDice,
            IReadOnlyList<int> hungerDice,
            string? specialization)
        {
            if (regularDice == null) throw new ArgumentNullException(nameof(regularDice));
            if (hungerDice == null) throw new ArgumentNullException(nameof(hungerDice));
            bool hasSpec = !string.IsNullOrWhiteSpace(specialization);

            int regTens = regularDice.Count(d => d == 10);
            int regSixesToNines = regularDice.Count(d => d >= 6 && d < 10);
            int regOnes = regularDice.Count(d => d == 1);
            // V20: каждая 10 = 1 успех, либо 2 при специализации.
            int regHits = regSixesToNines + (hasSpec ? regTens * 2 : regTens);
            // V5: каждая 1-ца съедает один успех.
            int regSuccesses = regHits - regOnes;

            int hungTens = hungerDice.Count(d => d == 10);
            int hungSixesToNines = hungerDice.Count(d => d >= 6 && d < 10);
            int hungSuccesses = hungSixesToNines + hungTens;

            return regSuccesses + hungSuccesses;
        }

        /// <summary>
        /// Обёртка совместимости: пересчитать успехи для одного пула кубов с учётом
        /// специализации. Используется в коде, который работает с единым пулом V20.
        /// </summary>
        public static int CountSuccessesFor(
            IReadOnlyList<int> dice, string? specialization)
        {
            return CountSuccessesWithSpecialization(
                dice, hasSpecialization: !string.IsNullOrWhiteSpace(specialization));
        }

        /// <summary>
        /// Чистая оценка уже брошенных кубов по правилам V5.
        /// </summary>
        /// <remarks>
        /// Правила (финальная формулировка, согласована 2026-09-01):
        ///   • regular: каждая X "съедает" один обычный успех 6–9 (не 10).
        ///     regularSum = regularTens + max(0, regularSuccesses_6_9 − X_count) − max(0, X_count − regularSuccesses_6_9)
        ///   • голодный 6–9 или 10 — всегда +1.
        ///   • голодный X/! = Messy/Bestial, только если regularPlusAfterEat == 0 и в regular есть крит.
        ///     Сам Messy/Bestial кубик считается как +1 / −1, плюс флаг.
        ///   • на бросок — не более одного Messy/Bestial.
        ///   • доп. куб при взаимном X↔! в обоих пулах.
        /// </remarks>
        public static V5RollResult EvaluateV5(
            IReadOnlyList<int> regularDice,
            IReadOnlyList<int> hungerDice,
            int? bonusDie = null)
        {
            if (regularDice == null) throw new ArgumentNullException(nameof(regularDice));
            if (hungerDice == null) throw new ArgumentNullException(nameof(hungerDice));

            // 1) Regular: X ест обычные 6–9 (не 10).
            int regularSixes = regularDice.Count(d => d >= SuccessThreshold && d < 10);
            int regularTens = regularDice.Count(d => d == 10);
            int regularOnes = regularDice.Count(d => d == 1);

            int plusAfterEat = Math.Max(0, regularSixes - regularOnes) + regularTens;
            int onesAfterEat = Math.Max(0, regularOnes - regularSixes);
            int regularSum = plusAfterEat - onesAfterEat;

            // «+» после поедания = plusAfterEat. Поддержка = есть X или 10 (исходный набор).
            bool regularHasCrit = regularOnes > 0 || regularTens > 0;
            // Нет «+» после поедания X — обычные успехи 6–9 съедены, десятки тоже дают +1,
            // но +1+1-1-1=0 — значит голодные X/! взаимно компенсируются.
            // В этом случае доп. куб имеет власть над результатом, флаги Messy/Bestial не взводятся.
            bool regularNoSuccesses = plusAfterEat == 0;
            bool hungerHasBothCrits = hungerDice.Any(d => d == 1) && hungerDice.Any(d => d == 10);
            bool bonusDieActive = bonusDie.HasValue && hungerHasBothCrits && regularNoSuccesses;

            // 2) Hunger — каждый куб индивидуально по правилам «голод».
            int hungerSum = 0;
            bool isMessy = false;
            bool isBestial = false;

            if (bonusDieActive)
            {
                // Доп. куб имеет власть — флаги Messy/Bestial не взводятся.
                // X → −1, ! → +1, 6–9 → +1.
                foreach (var d in hungerDice)
                {
                    if (d == 1) hungerSum -= 1;
                    else if (d >= SuccessThreshold) hungerSum += 1;
                }
            }
            else
            {
                // Некритические успехи 6–9 и единицы — отдельный проход.
                foreach (var d in hungerDice)
                {
                    if (d >= SuccessThreshold && d < 10)
                    {
                        hungerSum += 1; // 6–9 всегда +1
                    }
                    else if (d == 1)
                    {
                        if (!isBestial && plusAfterEat == 0 && regularHasCrit)
                        {
                            isBestial = true;
                            hungerSum -= 1;
                        }
                        else
                        {
                            hungerSum -= 1;
                        }
                    }
                    // 10 обработаем ниже; 2–5 — ноль
                }

                // Десятки — отдельный проход.
                foreach (var d in hungerDice)
                {
                    if (d == 10)
                    {
                        if (!isMessy && plusAfterEat == 0 && regularHasCrit)
                        {
                            isMessy = true;
                            hungerSum += 1;
                        }
                        else
                        {
                            hungerSum += 1;
                        }
                    }
                }
            }

            // 3) Доп. кубик.
            int? bonusValue = null;
            if (bonusDie.HasValue && bonusDieActive)
            {
                var b = bonusDie.Value;
                bonusValue = b;
                if (b == 10)
                {
                                if (!isMessy) isMessy = true;
                }
                else if (b == 1)
                {
                                if (!isBestial) isBestial = true;
                }
                else if (b >= SuccessThreshold)
                {
                    hungerSum += 1;
                }
                // 2–5 — ничего
            }

            int totalSuccesses = regularSum + hungerSum;
            bool isBotch = totalSuccesses < 0
                && (regularOnes > 0 || hungerDice.Any(d => d == 1));

            return new V5RollResult(
                regularDice.ToArray(),
                hungerDice.ToArray(),
                regularTens + regularSixes, // RegularSuccesses
                regularOnes,
                hungerSum,
                totalSuccesses,
                isMessy,
                isBestial,
                isBotch,
                bonusValue);
        }

        /// <summary>
        /// Текстовое представление результата для встраивания в эмбед.
        /// </summary>
        public static string FormatDice(IReadOnlyList<int> dice, string separator = ", ")
        {
            if (dice == null || dice.Count == 0) return "—";
            var sb = new StringBuilder();
            for (int i = 0; i < dice.Count; i++)
            {
                if (i > 0) sb.Append(separator);
                sb.Append(dice[i]);
            }
            return sb.ToString();
        }
    }

    /// <summary>Результат броска V20.</summary>
    public sealed record V20RollResult(
        int[] Dice,
        int Successes,
        bool IsBotch)
    {
        /// <summary>Сколько было критических (10-к).</summary>
        public int Criticals => Dice.Count(d => d == 10);

        /// <summary>Сколько было провальных (1-к).</summary>
        public int Ones => Dice.Count(d => d == 1);

        public string Describe() => IsBotch
            ? $"🎲 Пул: {Dice.Length} | **БОТЧ** | 0 успехов"
            : $"🎲 Пул: {Dice.Length} | Успехов: **{Successes}**";
    }

    /// <summary>Результат броска V5 — с разделением обычных и голодных кубов.</summary>
    public sealed record V5RollResult(
        int[] RegularDice,
        int[] HungerDice,
        int RegularSuccesses,
        int RegularOnes,
        int HungerSum,
        int TotalSuccesses,
        bool IsMessyCritical,
        bool IsBestialFailure,
        bool IsBotch,
        int? BonusDie = null)
    {
        public int HungerTens => HungerDice.Count(d => d == 10);
        public int HungerOnes => HungerDice.Count(d => d == 1);

        /// <summary>
        /// Короткая подпись под броском: "Успех", "Голодный успех", "Голодный провал", "Ботч", "Провал".
        /// </summary>
        public string OutcomeLabel()
        {
            if (IsMessyCritical) return "🩸 Голодный успех";
            if (IsBestialFailure) return "🩸 Голодный провал";
            if (IsBotch) return "Ботч";
            if (TotalSuccesses >= 1) return "Успех";
            return "Провал";
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"🎲 Обычные: {RegularDice.Length} | Голодные: {HungerDice.Length} | Всего: **{TotalSuccesses}**");
            if (BonusDie.HasValue)
                sb.Append($" | Доп. куб: {BonusDie}");
            sb.Append('\n');
            sb.Append($"Итог: **{OutcomeLabel()}**");
            if (IsMessyCritical) sb.Append($" (10 на голоде, regular без +, но с критом)");
            else if (IsBestialFailure) sb.Append($" (1 на голоде, regular без +, но с критом)");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Интерфейс-обёртка над System.Random, чтобы тесты могли подсунуть детерминированный seed.
    /// </summary>
    public interface IRandom
    {
        /// <summary>Возвращает целое в диапазоне [min..max] (обе границы включительно).</summary>
        int Next(int min, int max);
    }

    /// <summary>Thread-safe обёртка над <see cref="Random.Shared"/>.</summary>
    public sealed class SystemRandomAdapter : IRandom
    {
        public int Next(int min, int max) => Random.Shared.Next(min, max + 1);
    }

    /// <summary>Детерминированная обёртка для тестов.</summary>
    public sealed class SeededRandom : IRandom
    {
        private readonly Random _inner;
        public SeededRandom(int seed) => _inner = new Random(seed);
        public int Next(int min, int max) => _inner.Next(min, max + 1);
    }
}
