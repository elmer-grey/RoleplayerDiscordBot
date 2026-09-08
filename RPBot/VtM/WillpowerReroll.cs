using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM
{
    /// <summary>
    /// Логика переброса кубиков за пункт воли.
    /// </summary>
    /// <remarks>
    /// <para>Правила (vtm-dice-rules.md, 2026-09-02):</para>
    /// <list type="bullet">
    /// <item>Перебрасываются только regular-кубики. Голодные не трогаем.</item>
    /// <item>Берём <paramref name="count"/> худших regular-кубиков (наименьшие значения).</item>
    /// <item>Если regular-кубиков меньше, чем просят перебросить — перебрасываем все доступные.</item>
    /// <item>Стоимость — 1 пункт воли, независимо от числа перебрасываемых кубов.</item>
    /// <item>Если пунктов воли нет — переброс запрещён.</item>
    /// <item>Результат: новые значения в тех же позициях массива, остальные кубы неизменны.</item>
    /// </list>
    /// <para>Для V20 (без разделения regular/hunger) используется общий метод
    /// <see cref="RerollDice"/> — перебрасываются <paramref name="count"/> худших
    /// кубов из всего пула.</para>
    ///
    /// Класс не зависит от Discord — чистая логика для тестов.
    /// </remarks>
    public static class WillpowerReroll
    {
        /// <summary>
        /// Применяет переброс к набору кубов. Возвращает новый массив той же длины
        /// с заменёнными значениями в позициях <paramref name="count"/> худших кубов.
        /// </summary>
        /// <param name="dice">Текущие кубики (не пустой массив).</param>
        /// <param name="count">Сколько кубиков хотим перебросить (1..3).</param>
        /// <param name="rng">Источник случайных чисел (1..10 включительно).</param>
        /// <returns>Новый массив той же длины с переброшенными значениями.</returns>
        public static int[] RerollDice(IReadOnlyList<int> dice, int count, IRandom rng)
        {
            if (dice == null) throw new System.ArgumentNullException(nameof(dice));
            if (dice.Count == 0) throw new System.ArgumentException("Пул пуст.", nameof(dice));
            if (rng == null) throw new System.ArgumentNullException(nameof(rng));
            if (count < 1 || count > 3)
                throw new System.ArgumentOutOfRangeException(nameof(count), "Переброс от 1 до 3 кубиков.");

            // Индексы сортируем по значению ascending — худшие первые.
            var ordered = dice
                .Select((v, i) => (value: v, index: i))
                .OrderBy(x => x.value)
                .ThenBy(x => x.index)
                .Take(count)
                .Select(x => x.index)
                .ToArray();

            var result = dice.ToArray();
            foreach (var idx in ordered)
            {
                result[idx] = rng.Next(1, 11);
            }
            return result;
        }

        /// <summary>
        /// Старая сигнатура (V5): перебрасывает худшие regular-кубики.
        /// Оставлено для совместимости с VampireRollButtonHandler,
        /// но в новых сценариях (V20) используйте <see cref="RerollDice"/>.
        /// </summary>
        public static int[] RerollRegular(IReadOnlyList<int> regularDice, int count, IRandom rng)
            => RerollDice(regularDice, count, rng);

        /// <summary>
        /// Можно ли сейчас сделать переброс.
        /// </summary>
        /// <param name="willpowerPoints">Текущий запас пунктов воли.</param>
        public static bool CanReroll(int willpowerPoints)
        {
            return willpowerPoints >= 1;
        }

        /// <summary>
        /// Сколько кубиков реально будет переброшено (с учётом доступного пула).
        /// </summary>
        public static int ActualRerollCount(int poolSize, int requestedCount)
        {
            if (poolSize < 0) poolSize = 0;
            if (requestedCount < 1) return 0;
            return System.Math.Min(requestedCount, poolSize);
        }
    }
}