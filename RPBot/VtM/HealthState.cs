using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace RPBot.VtM;

/// <summary>
/// Состояние одной ячейки шкалы здоровья.
/// </summary>
public enum CellState
{
    /// <summary>Пустая ячейка (S).</summary>
    Empty,

    /// <summary>Нелетальный урон — косая линия (/).</summary>
    NonLethal,

    /// <summary>Летальный урон — крест (X).</summary>
    Lethal,

    /// <summary>Агравированный урон — закрашенная ячейка (A).</summary>
    Aggravated
}

/// <summary>
/// Шкала здоровья VtM-персонажа с правилами заполнения.
/// </summary>
/// <remarks>
/// <para>Шкала фиксирована: 7 ячеек (V20 стр. 263). Направление:
/// <b>яч.0 = «Помят» (самый лёгкий) сверху</b>, яч.6 = «Небоеспособен» (самый тяжёлый) снизу.</para>
/// <para>Штрафы по строкам таблицы V20 (стр. 92):
/// 0 Помят → −0, 1 Легко ранен → −1, 2 Ранен → −1, 3 Серьёзно → −2,
/// 4 Тяжело → −2, 5 Совсем плох → −5, 6 Небоеспособен → небоеспособен (без штрафа).</para>
/// <para>Штраф для броска определяется так:
/// <list type="bullet">
///   <item>если в шкале есть хотя бы один A: penaltyIndex = maxIndex(X/A) + 1 (но не больше 6);</item>
///   <item>иначе если есть X: penaltyIndex = maxIndex(X);</item>
///   <item>иначе штраф = 0.</item>
/// </list>
/// Это правило покрывает случай «A смещает штраф вниз» (пример пользователя 2026-09-09).</para>
/// <para>Каскад при получении агравированного:
/// <list type="number">
///   <item>targetIdx = первая не-A ячейка сверху (наименьший индекс с Empty/NonLethal/Lethal);</item>
///   <item>идём от targetIdx+1 вниз: каждая занятая ячейка заменяется значением предыдущей,
///         сдвиг останавливается на первой пустой;</item>
///   <item>в targetIdx ставится A.</item>
/// </list></para>
/// <para>Инвариант после любых операций: <c>[A…A][X…X][/…/][S…S]</c>.</para>
/// </remarks>
public sealed class HealthState
{
    private CellState[] _cells;

    /// <summary>Размер шкалы (количество ячеек).</summary>
    [JsonPropertyName("size")]
    public int Size => _cells.Length;

    /// <summary>Текущие ячейки (только для чтения/диагностики).</summary>
    [JsonPropertyName("cells")]
    public IReadOnlyList<CellState> Cells => _cells;

    /// <summary>
    /// Позиция последней X или A ячейки (наибольший индекс). Прокси для штрафа,
    /// но для целей отображения лучше использовать <see cref="TablePenalty"/>.
    /// </summary>
    [JsonPropertyName("penalty")]
    public int Penalty { get; private set; }

    /// <summary>
    /// Штраф по таблице V20 (стр. 92) с учётом правила «A сдвигает штраф вниз».
    /// null — массив ячеек пустой или null.
    /// </summary>
    /// <remarks>
    /// <para>Логика:</para>
    /// <list type="bullet">
    ///   <item>если есть A: penaltyIndex = maxIndex(X/A) + 1;</item>
    ///   <item>иначе если есть X: penaltyIndex = maxIndex(X);</item>
    ///   <item>иначе 0 (без X/A штраф 0).</item>
    /// </list>
    /// <para>Пример пользователя (2026-09-09): A в яч.0, X в яч.1,2 → penaltyIndex = 3 → −2.</para>
    /// <para>⚠️ Для «Небоеспособен» штраф численно = 0, но по правилам V20 (стр. 274)
    /// персонаж не может совершать действия — проверяйте <see cref="IsIncapacitated"/>.</para>
    /// </remarks>
    [JsonPropertyName("tablePenalty")]
    public int? TablePenalty { get; private set; }

    /// <summary>
    /// Небоеспособен (последняя ячейка шкалы заполнена X или A).
    /// По V20 стр. 274 персонаж не может совершать действия, кроме попытки выйти
    /// из этого состояния. <see cref="TablePenalty"/> для этого случая = 0,
    /// но применять его к пулу **нельзя** — см. <see cref="TablePenalty"/>.
    /// </summary>
    [JsonPropertyName("isIncapacitated")]
    public bool IsIncapacitated { get; private set; }

    /// <summary>Все ячейки Lethal или Aggravated, и хотя бы одна Lethal → торпор.</summary>
    [JsonPropertyName("isTorpor")]
    public bool IsTorpor =>
        _cells.Length > 0
        && _cells.All(c => c == CellState.Lethal || c == CellState.Aggravated)
        && _cells.Any(c => c == CellState.Lethal);

    /// <summary>Все ячейки Aggravated → персонаж уничтожен.</summary>
    [JsonPropertyName("isDestroyed")]
    public bool IsDestroyed =>
        _cells.Length > 0 && _cells.All(c => c == CellState.Aggravated);

    /// <summary>Финальная смерть (летальный поверх тора).</summary>
    [JsonPropertyName("isDead")]
    public bool IsDead { get; private set; }

    /// <summary>Создать пустую шкалу на <paramref name="size"/> ячеек.</summary>
    public HealthState(int size)
    {
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(size), size, "минимум 1 ячейка");
        _cells = Enumerable.Repeat(CellState.Empty, size).ToArray();
        RecomputePenalty();
    }

    /// <summary>Конструктор для десериализации.</summary>
    [JsonConstructor]
    public HealthState(int size, CellState[]? cells)
    {
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(size), size, "минимум 1 ячейка");
        _cells = cells != null && cells.Length == size
            ? (CellState[])cells.Clone()
            : Enumerable.Repeat(CellState.Empty, size).ToArray();
        NormalizeInvariant();
        RecomputePenalty();
    }

    // ─── Публичные API ───────────────────────────────────────────────────

    /// <summary>Применить <paramref name="amount"/> единиц нелетального урона.</summary>
    public void ApplyNonLethal(int amount)
    {
        if (amount <= 0 || IsDestroyed || IsDead) return;
        for (int i = 0; i < amount; i++) StepNonLethal();
    }

    /// <summary>Применить <paramref name="amount"/> единиц летального урона.</summary>
    public void ApplyLethal(int amount)
    {
        if (amount <= 0 || IsDestroyed || IsDead) return;
        for (int i = 0; i < amount; i++) StepLethal();
    }

    /// <summary>
    /// Применить <paramref name="amount"/> единиц агравированного урона.
    /// Реализует каскад по правилам пользователя (2026-09-09).
    /// </summary>
    public void ApplyAggravated(int amount)
    {
        if (amount <= 0 || IsDestroyed || IsDead) return;
        for (int i = 0; i < amount; i++) StepAggravated();
    }

    /// <summary>Вылечить <paramref name="amount"/> ячеек снизу вверх (сначала /, потом X).</summary>
    public void Heal(int amount)
    {
        if (amount <= 0 || IsDestroyed || IsDead) return;
        for (int i = 0; i < amount; i++) StepHeal();
    }

    /// <summary>Сбросить шкалу (например, при пересоздании персонажа).</summary>
    public void Reset()
    {
        for (int i = 0; i < _cells.Length; i++) _cells[i] = CellState.Empty;
        IsDead = false;
        RecomputePenalty();
    }

    /// <summary>Краткое текстовое представление (S, /, X, A) для отладки.</summary>
    public string Render() => string.Concat(_cells.Select(c => c switch
    {
        CellState.Empty => "S",
        CellState.NonLethal => "/",
        CellState.Lethal => "X",
        CellState.Aggravated => "A",
        _ => "?"
    }));

    // ─── Внутренняя логика ──────────────────────────────────────────────

    private void StepNonLethal()
    {
        // 1. Если есть S — первая S сверху становится /.
        int idxS = IndexOfFirst(CellState.Empty);
        if (idxS >= 0)
        {
            _cells[idxS] = CellState.NonLethal;
            RecomputePenalty();
            return;
        }
        // 2. Иначе — первая / сверху становится X.
        int idxSlash = IndexOfFirst(CellState.NonLethal);
        if (idxSlash >= 0)
        {
            _cells[idxSlash] = CellState.Lethal;
            RecomputePenalty();
            return;
        }
        // Негде ставить — игнор (например, шкала вся X/A уже).
    }

    /// <summary>
    /// Летальный урон. Логика (две подоперации; первая из них может "расщепляться"):
    ///   • Если есть X, есть S, но нет / — шкала уже частично X сверху и S внизу
    ///     (были летальные ранения, между ними и S нет ни одной / — тело оголено,
    ///     вампир уязвим). Тогда летальный удар сразу превращает первую S в X
    ///     (одна подоперация). Пример: XXXXSSS + X → XXXXXSS.
    ///   • Если X нет вообще, летальный = 2 подоперации "S → /".
    ///     SSSSSSS + X → //SSSSS. //SSSSS + X → ////SSS.
    ///   • Иначе (есть X и есть либо /, либо только X+S но "/"-ы ещё остались):
    ///     две подоперации "S → / или / → X".
    ///     XX////S + X → XXX//// (1 /, 1 X).
    ///     XX///// + X → XXXX/// (2 X).
    ///     XXXXXXS + X → XXXXXXX торпор (1 /, 1 X).
    ///     XXXXXXX + X → Мёртв.
    /// </summary>
    private void StepLethal()
    {
        bool hasX = false, hasEmpty = false, hasSlash = false;
        for (int i = 0; i < _cells.Length; i++)
        {
            if (_cells[i] == CellState.Lethal) hasX = true;
            else if (_cells[i] == CellState.Empty) hasEmpty = true;
            else if (_cells[i] == CellState.NonLethal) hasSlash = true;
        }

        // Случай 1: шкала уже частично X сверху, есть S, но нет ни одной /.
        // Вампир был ранен летально, его лечили, и "/"-слой полностью исчерпан —
        // тело оголено. Летальный удар сразу заполняет X (одна подоперация).
        if (hasX && hasEmpty && !hasSlash)
        {
            int idxS = IndexOfFirst(CellState.Empty);
            _cells[idxS] = CellState.Lethal;
            RecomputePenalty();
            return;
        }

        // Случай 2: иначе — две подоперации "S → /" или "/ → X".
        for (int step = 0; step < 2; step++)
        {
            int idxS = IndexOfFirst(CellState.Empty);
            if (idxS >= 0)
            {
                _cells[idxS] = CellState.NonLethal;
                continue;
            }
            int idxSlash = IndexOfFirst(CellState.NonLethal);
            if (idxSlash >= 0)
            {
                _cells[idxSlash] = CellState.Lethal;
                continue;
            }
            // Нет ни S, ни / — торпор. Летальный поверх = смерть.
            IsDead = true;
            RecomputePenalty();
            return;
        }
        RecomputePenalty();
    }

    /// <summary>
    /// Агравированный урон с каскадом (правила пользователя 2026-09-09, пересмотр):
    ///   1. targetIdx = первая не-A ячейка сверху (Empty/NonLethal/Lethal).
    ///   2. Снимок состояния.
    ///   3. Сдвиг ячеек targetIdx+1..Length-1 вправо на 1 по снимку —
    ///      каждая ячейка получает значение предыдущей. Последний элемент снимка
    ///      (яч. Length-1) «выпадает» за шкалу и теряется.
    ///   4. В targetIdx ставится A.
    ///   5. Если вся шкала заполнена A — «Торпор»
    ///      (IsIncapacitated = true, Penalty = 0, TablePenalty = 0).
    ///   Примеры пользователя 2026-09-09:
    ///     «X,X,X,/,/,S,S + A → A,X,X,X,/,/,S» (Пример 3 → Пример 4).
    ///     «/,/,/,/,/,S,S + A → A,/,/,/,/,/,S» (Пример 5).
    ///     «/,/,/,/,S,S,S + A → A,/,/,/,/,S,S» (A4).
    /// </summary>
    private void StepAggravated()
    {
        if (_cells.All(c => c == CellState.Aggravated))
        {
            return;
        }

        // 1. targetIdx.
        int targetIdx = -1;
        for (int i = 0; i < _cells.Length; i++)
        {
            if (_cells[i] != CellState.Aggravated)
            {
                targetIdx = i;
                break;
            }
        }
        if (targetIdx < 0) return;

        // 2. Снимок и сдвиг.
        var snapshot = (CellState[])_cells.Clone();
        for (int i = targetIdx + 1; i < _cells.Length; i++)
        {
            _cells[i] = snapshot[i - 1];
        }

        // 3. A в targetIdx.
        _cells[targetIdx] = CellState.Aggravated;

        // 4. Торпор.
        if (_cells.All(c => c == CellState.Aggravated))
        {
            IsIncapacitated = true;
            Penalty = 0;
            TablePenalty = 0;
            return;
        }

        RecomputePenalty();
    }

    private void StepHeal()
    {
        // Снизу вверх: сначала все / → S, потом все X → S.
        for (int i = _cells.Length - 1; i >= 0; i--)
        {
            if (_cells[i] == CellState.NonLethal)
            {
                _cells[i] = CellState.Empty;
                RecomputePenalty();
                return;
            }
        }
        for (int i = _cells.Length - 1; i >= 0; i--)
        {
            if (_cells[i] == CellState.Lethal)
            {
                _cells[i] = CellState.Empty;
                RecomputePenalty();
                return;
            }
        }
        // A и S не лечатся.
    }

    private int IndexOfFirst(CellState state)
    {
        for (int i = 0; i < _cells.Length; i++)
            if (_cells[i] == state) return i;
        return -1;
    }

    private void RecomputePenalty()
    {
        // Позиция последнего X или A (наибольший индекс).
        int last = -1;
        bool hasA = false;
        bool hasX = false;
        for (int i = 0; i < _cells.Length; i++)
        {
            if (_cells[i] == CellState.Lethal)
            {
                last = i;
                hasX = true;
            }
            else if (_cells[i] == CellState.Aggravated)
            {
                last = i;
                hasA = true;
            }
        }
        Penalty = Math.Max(0, last);
        IsIncapacitated = last == _cells.Length - 1 && last >= 0;

        // Штраф по правилам пользователя (2026-09-09).
        if (hasA)
        {
            int penaltyIdx = Math.Min(last + 1, _cells.Length - 1);
            TablePenalty = PenaltyByIndex(penaltyIdx);
        }
        else if (hasX)
        {
            TablePenalty = PenaltyByIndex(last);
        }
        else
        {
            // Без X/A — штраф 0 по таблице (не null: нелетальные не дают штрафа,
            // но шкала не «пустая»).
            TablePenalty = 0;
        }
    }

    /// <summary>
    /// Штраф по таблице V20 (стр. 92) на основе индекса ячейки.
    /// </summary>
    public static int PenaltyByIndex(int idx) => idx switch
    {
        0 => 0,
        1 => -1,
        2 => -1,
        3 => -2,
        4 => -2,
        5 => -5,
        _ => 0, // 6+ — небоеспособен, без штрафа на броски
    };

    /// <summary>
    /// Статический расчёт штрафа по массиву ячеек (для тестов и обратной совместимости).
    /// </summary>
    public static int? ComputeTablePenalty(CellState[] cells)
    {
        if (cells is null || cells.Length == 0) return null;
        int last = -1;
        bool hasA = false;
        bool hasX = false;
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] == CellState.Lethal)
            {
                last = i;
                hasX = true;
            }
            else if (cells[i] == CellState.Aggravated)
            {
                last = i;
                hasA = true;
            }
        }
        if (hasA)
        {
            int penaltyIdx = Math.Min(last + 1, cells.Length - 1);
            return PenaltyByIndex(penaltyIdx);
        }
        if (hasX) return PenaltyByIndex(last);
        return 0; // без X/A — штраф 0
    }

    /// <summary>
    /// Привести состояние к инварианту [A…A][X…X][/…/][S…S].
    /// Используется при десериализации из старого/повреждённого файла.
    /// </summary>
    private void NormalizeInvariant()
    {
        int aCount = _cells.Count(c => c == CellState.Aggravated);
        int xCount = _cells.Count(c => c == CellState.Lethal);
        int slashCount = _cells.Count(c => c == CellState.NonLethal);
        int sCount = _cells.Count(c => c == CellState.Empty);
        int total = aCount + xCount + slashCount + sCount;
        if (total != _cells.Length)
        {
            // Что-то неизвестное — сбрасываем в S.
            _cells = Enumerable.Repeat(CellState.Empty, _cells.Length).ToArray();
            return;
        }
        var rebuilt = new CellState[_cells.Length];
        int pos = 0;
        for (int i = 0; i < aCount; i++) rebuilt[pos++] = CellState.Aggravated;
        for (int i = 0; i < xCount; i++) rebuilt[pos++] = CellState.Lethal;
        for (int i = 0; i < slashCount; i++) rebuilt[pos++] = CellState.NonLethal;
        for (int i = 0; i < sCount; i++) rebuilt[pos++] = CellState.Empty;
        _cells = rebuilt;
    }
}
