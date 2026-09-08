using System;
using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM;

/// <summary>
/// Чистая логика проверки сопротивления ярости (Frenzy) и Ротшреку (Rötschreck)
/// по правилам VtM V20 (стр. 322-325). Без побочных эффектов.
/// </summary>
/// <remarks>
/// <para><b>Ярость</b> — попытка предотвратить приступ бешенства от гнева/голода.
/// Проверка <b>самоконтроля</b> против сложности. Успешный бросок с ≥5 успехами
/// полностью подавляет Зверя; иначе каждый успех сдерживает приступ на один ход,
/// после чего можно повторить проверку (успехи суммируются). Провал → приступ
/// ярости на усмотрение рассказчика.</para>
///
/// <para><b>Ротшрек</b> (стр. 324) — паническая ярость от страха (огонь, солнце).
/// Проверка <b>смелости</b>, та же механика подавления/сдерживания.</para>
///
/// <para>Добродетель «Инстинкты» вместо «Самоконтроль» означает, что персонаж
/// вообще не способен пройти проверку и всегда впадает в ярость (стр. 337).</para>
///
/// <para>Клан Бруха: сложность +2 (кэп 10), трата воли для предотвращения невозможна
/// (но трата для «взять себя в руки на ход» — возможна, если приступ уже начался).</para>
/// </remarks>
public static class VampireFrenzyResolver
{
    /// <summary>Минимальный порог успеха для кубов d10 (V20: 6+).</summary>
    public const int SuccessThreshold = 6;

    /// <summary>Кэп сложности проверок сопротивления ярости/Ротшреку.</summary>
    public const int MaxDifficulty = 10;

    /// <summary>Сколько успехов нужно набрать (суммарно за все попытки),
    /// чтобы полностью подавить Зверя.</summary>
    public const int FullSuppressThreshold = 5;

    /// <summary>Тип проверки.</summary>
    public enum FrenzyKind
    {
        /// <summary>Ярость (гнев/голод). Проверка самоконтроля (или «Инстинктов» — автопровал).</summary>
        Frenzy,
        /// <summary>Ротшрек (паническая ярость). Проверка смелости.</summary>
        Rötschreck,
    }

    /// <summary>Исход одной попытки проверки.</summary>
    public enum FrenzyRollOutcome
    {
        /// <summary>Персонаж полностью подавил Зверя (накопленных успехов ≥ 5).</summary>
        FullySuppressed,
        /// <summary>Частичное сдерживание: бросок успешный, но общая сумма успехов &lt; 5.
        /// Приступ откладывается на количество успехов ходов.</summary>
        PartiallyContained,
        /// <summary>Неудача — Зверь вырвался на свободу.</summary>
        Unleashed,
    }

    /// <summary>Результат одной попытки проверки.</summary>
    /// <param name="Outcome">Итог проверки.</param>
    /// <param name="PoolSize">Размер пула (сколько кубиков было брошено).</param>
    /// <param name="Successes">Успехи в этом броске (≥ 0).</param>
    /// <param name="Difficulty">Сложность, против которой проходила проверка.</param>
    /// <param name="AccumulatedSuccesses">Сколько успехов накоплено с учётом прошлых попыток.</param>
    /// <param name="RoundsContained">Сколько ходов Зверь сдерживается
    /// (если <see cref="Outcome"/> = PartiallyContained).</param>
    /// <param name="ActiveAtavism">Атавизм Гангрела, добавленный при входе в ярость
    /// (если персонаж — Гангрел и проверка провалена). Иначе null.</param>
    public sealed record FrenzyRollResult(
        FrenzyRollOutcome Outcome,
        int PoolSize,
        int Successes,
        int Difficulty,
        int AccumulatedSuccesses,
        int RoundsContained,
        string? ActiveAtavism)
    {
        /// <summary>Краткое текстовое описание исхода для embed.</summary>
        public string Describe() => Outcome switch
        {
            FrenzyRollOutcome.FullySuppressed =>
                $"🟢 Зверь усмирён полностью (накоплено {AccumulatedSuccesses}/{FullSuppressThreshold} успехов).",
            FrenzyRollOutcome.PartiallyContained =>
                $"🟡 Зверь сдерживается {RoundsContained} ход(а) (+{Successes} усп., всего {AccumulatedSuccesses}/{FullSuppressThreshold}).",
            FrenzyRollOutcome.Unleashed =>
                $"🔴 Зверь вырвался на своботу — {Successes} успехов против сложности {Difficulty}.",
            _ => "",
        };
    }

    /// <summary>Рассчитать сложность проверки сопротивления ярости (V20 стр. 323).</summary>
    /// <param name="kind">Frenzy или Rötschreck.</param>
    /// <param name="stimulusComplexity">
    /// Сложность стимула. Для Frenzy: 3..8 (таблица стр. 323). Для Rötschreck: 3..9 (таблица стр. 324).
    /// Если в результате приступа персонаж может совершить ужасный поступок — передать
    /// <c>Math.Clamp(9 − conscience, ...)</c>.
    /// </param>
    /// <param name="conscience">Совесть (1..5). Используется только при «ужасных» стимулах.</param>
    /// <param name="clan">Клан персонажа (влияет на модификаторы).</param>
    public static int ComputeDifficulty(FrenzyKind kind, int stimulusComplexity, int conscience, string clan)
    {
        var baseDiff = Math.Clamp(stimulusComplexity, 3, MaxDifficulty);
        if (IsBrujah(clan))
        {
            // Бруха: +2 к сложности, но не выше 10 (стр. 300).
            baseDiff = Math.Min(MaxDifficulty, baseDiff + 2);
        }
        return baseDiff;
    }

    /// <summary>Альтернативный способ задать сложность через «ужасный» поступок:
    /// <c>[9 − совесть]</c> (V20 стр. 323).</summary>
    public static int HorrorActDifficulty(int conscience)
    {
        var raw = 9 - Math.Clamp(conscience, 1, 5);
        return Math.Clamp(raw, 3, MaxDifficulty);
    }

    /// <summary>Размер пула проверки = значение добродетели (V20: самоконтроль 1..5
    /// или смелость 1..5).</summary>
    public static int ComputePoolSize(VampireCharacter character, FrenzyKind kind)
    {
        if (character == null) return 0;
        string virtueName = kind switch
        {
            FrenzyKind.Frenzy => VampireParameterCatalog.VirtueSelfControl,
            FrenzyKind.Rötschreck => VampireParameterCatalog.VirtueCourage,
            _ => "",
        };
        if (string.IsNullOrEmpty(virtueName)) return 0;
        int value = 0;
        if (character.Virtues != null && character.Virtues.TryGetValue(virtueName, out var v))
            value = v;
        return Math.Clamp(value, 0, 5);
    }

    /// <summary>Является ли персонаж «инстинктом» (не самоконтролем)?
    /// Если в Virtues задано «Инстинкты» (а «Самоконтроль» не задан или равен 0),
    /// то проверка самоконтроля невозможна — персонаж всегда впадает в ярость.</summary>
    public static bool IsInstinctDriven(VampireCharacter character)
    {
        if (character?.Virtues == null) return false;
        int selfCtrl = character.Virtues.TryGetValue(VampireParameterCatalog.VirtueSelfControl, out var sc) ? sc : 0;
        int instincts = character.Virtues.TryGetValue("Инстинкты", out var i) ? i : 0;
        return selfCtrl <= 0 && instincts > 0;
    }

    /// <summary>
    /// Совершить попытку проверки сопротивления ярости.
    /// </summary>
    /// <param name="character">Персонаж (берётся пул, добродетели, клан).</param>
    /// <param name="kind">Frenzy или Rötschreck.</param>
    /// <param name="difficulty">Сложность проверки (3..10).</param>
    /// <param name="accumulatedSuccesses">
    /// Сколько успехов уже накоплено с прошлых попыток (если это повторная попытка в том же ходу).
    /// </param>
    /// <param name="rng">Источник случайных чисел (1..10 включительно).</param>
    /// <param name="rollAtavism">Нужно ли выбирать атавизм (Гангрел).</param>
    /// <param name="atavismPicker">
    /// Опциональная функция выбора атавизма из списка. По умолчанию — детерминированный
    /// выбор по случайному кубику d10 (1..10).</param>
    public static FrenzyRollResult Roll(
        VampireCharacter character,
        FrenzyKind kind,
        int difficulty,
        int accumulatedSuccesses,
        IRandom rng,
        bool rollAtavism = false,
        Func<int, string>? atavismPicker = null)
    {
        if (character == null) throw new ArgumentNullException(nameof(character));
        if (rng == null) throw new ArgumentNullException(nameof(rng));
        difficulty = Math.Clamp(difficulty, 3, MaxDifficulty);
        accumulatedSuccesses = Math.Max(0, accumulatedSuccesses);

        // 1. Инстинкты → автопровал при попытке самоконтроля.
        if (kind == FrenzyKind.Frenzy && IsInstinctDriven(character))
        {
            return new FrenzyRollResult(
                Outcome: FrenzyRollOutcome.Unleashed,
                PoolSize: 0,
                Successes: 0,
                Difficulty: difficulty,
                AccumulatedSuccesses: accumulatedSuccesses,
                RoundsContained: 0,
                ActiveAtavism: PickAtavism(character, rng, rollAtavism, atavismPicker, kind));
        }

        // 2. Считаем пул и кидаем.
        var pool = ComputePoolSize(character, kind);
        var dice = new int[pool];
        for (var i = 0; i < pool; i++) dice[i] = rng.Next(1, 11);
        var successes = dice.Count(d => d >= SuccessThreshold);

        // 3. Определяем исход.
        var total = accumulatedSuccesses + successes;
        FrenzyRollOutcome outcome;
        int rounds = 0;
        string? atavism = null;

        if (successes <= 0)
        {
            outcome = FrenzyRollOutcome.Unleashed;
            atavism = PickAtavism(character, rng, rollAtavism, atavismPicker, kind);
        }
        else if (total >= FullSuppressThreshold)
        {
            outcome = FrenzyRollOutcome.FullySuppressed;
        }
        else
        {
            outcome = FrenzyRollOutcome.PartiallyContained;
            rounds = successes;
        }

        return new FrenzyRollResult(outcome, pool, successes, difficulty, total, rounds, atavism);
    }

    /// <summary>Детерминированный список атавизмов Гангрела (V20 стр. 95, 268).</summary>
    public static readonly IReadOnlyList<string> DefaultAtavisms = new[]
    {
        "Пробивающаяся шерсть",
        "Послеобеденная спячка",
        "Стремление избегать больших скоплений народа",
        "Удлинённые клыки",
        "Острые когти",
        "Звериный оскал",
        "Светящиеся в темноте глаза",
        "Звериный рык",
        "Хвост",
        "Сгорбленная спина",
    };

    private static string? PickAtavism(
        VampireCharacter character,
        IRandom rng,
        bool rollAtavism,
        Func<int, string>? picker,
        FrenzyKind kind)
    {
        if (!rollAtavism) return null;
        if (!string.Equals(character.Clan, "Гангрел", StringComparison.OrdinalIgnoreCase)) return null;

        var idx = rng.Next(1, 11); // 1..10
        var pick = picker ?? (i => DefaultAtavisms[(i - 1) % DefaultAtavisms.Count]);
        var atavism = pick(idx);

        if (character.ActiveAtavisms == null)
            character.ActiveAtavisms = new List<string>();
        if (!character.ActiveAtavisms.Contains(atavism))
            character.ActiveAtavisms.Add(atavism);

        // Дублируем в новый список с метаданными.
        if (character.ActiveAtavismEntries == null)
            character.ActiveAtavismEntries = new List<AtavismEntry>();
        if (!character.ActiveAtavismEntries.Any(e => e.Name == atavism))
        {
            // Звериный признак Гангрела (для Frenzy) или Берсерк (для Rötschreck).
            var entryKind = kind == FrenzyKind.Rötschreck
                ? AtavismKind.BerserkPanic
                : AtavismKind.GangrelBeastFeature;
            character.ActiveAtavismEntries.Add(new AtavismEntry(atavism, entryKind, AcquiredAt: null));
        }

        return atavism;
    }

    private static bool IsBrujah(string? clan)
        => !string.IsNullOrEmpty(clan)
           && clan.Equals("Бруха", StringComparison.OrdinalIgnoreCase);
}
