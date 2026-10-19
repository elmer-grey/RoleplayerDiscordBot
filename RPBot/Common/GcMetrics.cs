using System;
using System.Diagnostics;

namespace RPBot.Common
{
    /// <summary>
    /// Снимки GC-счётчиков для SLOW-логов (DeferAsync/ComponentPreDefer).
    /// <para>
    /// <see cref="GC.GetTotalPauseDuration"/> возвращает кумулятивное время пауз GC
    /// с момента старта процесса, поэтому печать «сырого» значения в каждой
    /// SLOW-записи создаёт видимость бесконечно растущей паузы (в логах 18.10
    /// наблюдали gcPause=25666→27099ms и ошибочно трактовали как одну большую
    /// паузу). Теперь печатаем дельту за измеряемую операцию: gcPauseDelta=0ms
    /// значит «GC не участвовал», а не «пауза 27000 ms».
    /// </para>
    /// </summary>
    internal static class GcMetrics
    {
        public readonly struct Snapshot
        {
            public Snapshot(TimeSpan pauseTotal, int gen0, int gen1, int gen2)
            {
                PauseTotal = pauseTotal;
                Gen0 = gen0;
                Gen1 = gen1;
                Gen2 = gen2;
            }

            public TimeSpan PauseTotal { get; }
            public int Gen0 { get; }
            public int Gen1 { get; }
            public int Gen2 { get; }
        }

        public static Snapshot Take()
            => new(GC.GetTotalPauseDuration(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

        public static string DeltaSince(Snapshot before)
        {
            var after = Take();
            double pauseDeltaMs = (after.PauseTotal - before.PauseTotal).TotalMilliseconds;
            long heapMB = (long)(GC.GetTotalMemory(false) / 1024d / 1024d);
            return $"gcPauseDelta={pauseDeltaMs:F0}ms dGen0={after.Gen0 - before.Gen0} " +
                   $"dGen1={after.Gen1 - before.Gen1} dGen2={after.Gen2 - before.Gen2} heap={heapMB:F1}MB";
        }
    }
}