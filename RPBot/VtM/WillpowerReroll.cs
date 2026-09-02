using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM
{
    /// <summary>
    /// Логика переброса кубиков за пункт воли.
    /// </summary>
    /// <remarks>
    /// Правила (vtm-dice-rules.md, 2026-09-02):
    ///   • Перебрасываются только regular-кубики. Голодные не трогаем.
    ///   • Берём <paramref name="count"/> худших regular-кубиков (наименьшие значения).
    ///   • Если regular-кубиков меньше, чем просят перебросить — перебрасываем все доступные.
    ///   • Стоимость — 1 пункт воли, независимо от числа перебрасываемых кубов.
    ///   • Если пунктов воли нет — переброс запрещён.
    ///   • Результат: новые значения в тех же позициях массива, остальные кубы неизменны.
    ///
    /// Класс не зависит от Discord — чистая логика для тестов.
    /// </remarks>
    public static class WillpowerReroll
    {
        /// <summary>
        /// Применяет переброс к набору кубов. Возвращает новый массив regular-кубиков
        /// с заменёнными значениями в позициях переброса.
        /// </summary>
        /// <param name="regularDice">Текущие regular-кубики (не пустой массив).</param>
        /// <param name="count">Сколько кубиков хотим перебросить (1..3).</param>
        /// <param name="rng">Источник случайных чисел (1..10 включительно).</param>
        /// <returns>Новый массив той же длины с переброшенными значениями.</returns>
        public static int[] RerollRegular(IReadOnlyList<int> regularDice, int count, IRandom rng)
        {
            if (regularDice == null) throw new System.ArgumentNullException(nameof(regularDice));
            if (regularDice.Count == 0) throw new System.ArgumentException("Regular пул пуст.", nameof(regularDice));
            if (rng == null) throw new System.ArgumentNullException(nameof(rng));
            if (count < 1 || count > 3)
                throw new System.ArgumentOutOfRangeException(nameof(count), "Переброс от 1 до 3 кубиков.");

            // Индексы сортируем по значению ascending — худшие первые.
            var ordered = regularDice
                .Select((v, i) => (value: v, index: i))
                .OrderBy(x => x.value)
                .ThenBy(x => x.index)
                .Take(count)
                .Select(x => x.index)
                .ToArray();

            var result = regularDice.ToArray();
            foreach (var idx in ordered)
            {
                result[idx] = rng.Next(1, 11);
            }
            return result;
        }

        /// <summary>
        /// Можно ли сейчас сделать переброс.
        /// </summary>
        /// <param name="willpowerPoints">Текущий запас пунктов воли.</param>
        public static bool CanReroll(int willpowerPoints)
        {
            return willpowerPoints >= 1;
        }

        /// <summary>
        /// Сколько regular-кубиков реально будет переброшено (с учётом доступного пула).
        /// </summary>
        public static int ActualRerollCount(int regularCount, int requestedCount)
        {
            if (regularCount < 0) regularCount = 0;
            if (requestedCount < 1) return 0;
            return System.Math.Min(requestedCount, regularCount);
        }
    }
}