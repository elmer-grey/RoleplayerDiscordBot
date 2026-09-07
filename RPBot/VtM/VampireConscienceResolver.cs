using System;

namespace RPBot.VtM
{
    /// <summary>
    /// Чистая логика проверки совести (Roadmap #37, V20 стр. 333).
    /// </summary>
    /// <remarks>
    /// <para>Проверка совести — рефлекторное действие, кидается при моральной дилемме.
    /// Рассказчик назначает её, когда персонаж собирается совершить поступок, противоречащий
    /// его Человечности (или Пути). Пул формируется из текущей Человечности (или
    /// <see cref="VampireCharacter.PathRating"/>, если задана), без модификаторов.
    /// Пункт Воли не тратится для автоуспеха — никакое эго не спасёт от чувства вины.</para>
    ///
    /// <para>Исход (V20 стр. 333):
    /// <list type="bullet">
    /// <item>≥ 1 успех → показатель Человечности не меняется;</item>
    /// <item>0 успехов (неудача) → −1 к Человечности;</item>
    /// <item>провал (botch, ни один кубик не выпал ≥ 6) → −1 к Человечности И −1 к совести
    /// И подходящее психическое расстройство.</item>
    /// </list></para>
    ///
    /// <para>Этот класс — чистая логика без побочных эффектов; применять потери должен вызывающий код.</para>
    /// </remarks>
    public static class VampireConscienceResolver
    {
        /// <summary>Базовая сложность проверки совести (V20 стр. 333).</summary>
        public const int DefaultDifficulty = 8;

        /// <summary>Исход проверки совести.</summary>
        public enum ConscienceRollOutcome
        {
            /// <summary>Хотя бы один успех — Человечность не снижается.</summary>
            Success,

            /// <summary>Ноль успехов, но не провал — −1 к Человечности.</summary>
            Failure,

            /// <summary>Ни один кубик не выпал на 6+ и не было успехов — −1 к Человечности,
            /// −1 к Совести, плюс подходящее расстройство.</summary>
            Botch,
        }

        /// <summary>
        /// Сформировать пул проверки совести — текущая Человечность или PathRating.
        /// Кэп = 10 (V20 стр. 270: «параметры с максимумом 10 не усиливаются другими»).
        /// </summary>
        /// <param name="character">Персонаж.</param>
        /// <param name="currentHumanity">Текущая Человечность (берётся из <see cref="VampireFinishingResolver.ComputeHumanity"/>,
        /// если передан <c>computeHumanity</c>, иначе значение из аргумента).</param>
        public static int ConscienceDicePool(VampireCharacter character, int currentHumanity)
        {
            if (character == null) return 0;
            if (!string.IsNullOrEmpty(character.Path) && character.PathRating > 0)
            {
                return ClampPool(character.PathRating);
            }
            return ClampPool(currentHumanity);
        }

        private static int ClampPool(int value)
            => value < 0 ? 0 : (value > 10 ? 10 : value);

        /// <summary>
        /// Полный результат проверки совести с подсчётом успехов и определением исхода.
        /// </summary>
                /// <remarks>
                /// <para>По V20 стр. 333:
                /// <list type="bullet">
                /// <item>≥ 1 успех — Success;</item>
                /// <item>0 успехов, но хотя бы один кубик ≥ 6 (тот же диапазон, что и проверки характеристик) — Failure;</item>
                /// <item>0 успехов и ни один кубик &lt; 6 — Botch (провал с последствиями).</item>
                /// </list>
                /// Сложность используется для подсчёта успехов: всё, что ≥ difficulty (по умолчанию 8),
                /// считается успехом. Для строгости V20 «провал» определяется по шкале 6 — оставляю
                /// константу <see cref="BotchThreshold"/> настраиваемой, но по умолчанию 6 (что
                /// соответствует «ни один кубик не выпал даже на успех обычной проверки»).</para>
                /// </remarks>
                /// <param name="diceRolls">Массив граней кубиков из пула (1..10).</param>
                /// <param name="difficulty">Сложность проверки (по умолчанию 8).</param>
                /// <param name="botchThreshold">Порог для проверки провала: если все кубики
                /// строго меньше этого значения, считается ботч (по умолчанию 6).</param>
                public static ConscienceRollResult Roll(int[] diceRolls,
                    int difficulty = DefaultDifficulty,
                    int botchThreshold = BotchThresholdDefault)
                {
                    if (diceRolls == null) diceRolls = Array.Empty<int>();
                    if (botchThreshold < 2 || botchThreshold > 10)
                        throw new ArgumentOutOfRangeException(nameof(botchThreshold));

                    int successes = 0;
                    int maxDie = 0;
                    foreach (var d in diceRolls)
                    {
                        if (d < 1 || d > 10)
                            throw new ArgumentOutOfRangeException(nameof(diceRolls),
                                $"Грань кубика вне диапазона 1..10: {d}");
                        if (d >= difficulty) successes++;
                        if (d > maxDie) maxDie = d;
                    }
                    var outcome = successes switch
                    {
                        >= 1 => ConscienceRollOutcome.Success,
                        0 when maxDie >= botchThreshold => ConscienceRollOutcome.Failure,
                        _ => ConscienceRollOutcome.Botch,
                    };
                    return new ConscienceRollResult(successes, outcome, difficulty);
                }

                /// <summary>Базовый порог для отличия обычной неудачи от боча: 6.</summary>
                public const int BotchThresholdDefault = 6;

        /// <summary>Применить последствия исхода к персонажу.</summary>
        /// <remarks>
        /// Не уменьшает показатели ниже 0. Не модифицирует <see cref="VampireCharacter"/> —
        /// только возвращает рекомендуемые изменения. Это позволяет вызывающему коду
        /// самостоятельно решить, нужны ли оговорки (например, при Humanity=0 персонаж
        /// становится NPC).
        /// </remarks>
        public static ConscienceApplyResult Apply(ConscienceRollOutcome outcome)
        {
            return outcome switch
            {
                ConscienceRollOutcome.Success => new ConscienceApplyResult(
                    HumanityDelta: 0,
                    ConscienceDelta: 0,
                    AddDerangement: false),
                ConscienceRollOutcome.Failure => new ConscienceApplyResult(
                    HumanityDelta: -1,
                    ConscienceDelta: 0,
                    AddDerangement: false),
                ConscienceRollOutcome.Botch => new ConscienceApplyResult(
                    HumanityDelta: -1,
                    ConscienceDelta: -1,
                    AddDerangement: true),
                _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
            };
        }

        /// <summary>Текстовое описание исхода для embed (без окраски, для логов).</summary>
        public static string Describe(ConscienceRollOutcome outcome) => outcome switch
        {
            ConscienceRollOutcome.Success => "Успех — показатель Человечности не меняется.",
            ConscienceRollOutcome.Failure => "Неудача — −1 к Человечности.",
            ConscienceRollOutcome.Botch => "Провал — −1 к Человечности, −1 к Совести, новое расстройство.",
            _ => "",
        };
    }

    /// <summary>Результат броска кубов: сколько успехов и общий исход.</summary>
    /// <param name="Successes">Число успехов (≥ 0).</param>
    /// <param name="Outcome">Исход проверки.</param>
    /// <param name="Difficulty">Использованная сложность.</param>
    public sealed record ConscienceRollResult(
        int Successes,
        VampireConscienceResolver.ConscienceRollOutcome Outcome,
        int Difficulty);

    /// <summary>Рекомендуемые изменения показателей после проверки совести.</summary>
    /// <param name="HumanityDelta">Изменение Человечности (отрицательное — потеря).</param>
    /// <param name="ConscienceDelta">Изменение добродетели «Совесть» (отрицательное — потеря).</param>
    /// <param name="AddDerangement">true — нужно подобрать подходящее расстройство.</param>
    public sealed record ConscienceApplyResult(
        int HumanityDelta,
        int ConscienceDelta,
        bool AddDerangement);
}
