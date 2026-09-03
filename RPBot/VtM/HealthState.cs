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
/// <para>Размер шкалы: <c>Stamina + 3</c> ячейки, пронумерованные 0..N-1 сверху вниз.</para>
/// <para>Ячейка 0 — буфер «Помят» (штрафа не даёт).</para>
/// <para>Инвариант: <c>[A…A][X…X][/…/][S…S]</c> — никаких других расположений быть не может.</para>
/// <para>Правила зафиксированы по 21 примеру пользователя (2026-09-02).</para>
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

    /// <summary>Штраф по позиции последнего X или A (ячейка 0 = 0).</summary>
    [JsonPropertyName("penalty")]
    public int Penalty { get; private set; }

    /// <summary>
    /// Штраф по таблице V20 (стр. 92):
    /// Помят → -0, Легко ранен → -1, Ранен → -1, Серьёзно → -2, Тяжело → -2,
    /// Совсем плох → -5, Небоеспособен → без штрафа (рассказчик решает).
    /// null — шкала пуста.
    /// </summary>
    /// <remarks>
    /// Это «логический» штраф по строкам таблицы, а не арифметический по индексу ячейки.
    /// Может отличаться от <see cref="Penalty"/> в редких случаях
    /// (например, оголённый X поверх S без /).
    /// <para>
    /// ⚠️ Для «Небоеспособен» штраф численно = 0, но по правилам V20 (стр. 274)
    /// персонаж не может совершать действия — проверяйте <see cref="IsIncapacitated"/>
    /// в логике бросков, не только <see cref="TablePenalty"/>.
    /// </para>
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
        TablePenalty = ComputeTablePenalty(_cells);
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
        TablePenalty = ComputeTablePenalty(_cells);
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

    /// <summary>Применить <paramref name="amount"/> единиц агравированного урона.</summary>
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

    private void StepAggravated()
    {
        // Первая не-A ячейка сверху становится A.
        for (int i = 0; i < _cells.Length; i++)
        {
            if (_cells[i] != CellState.Aggravated)
            {
                _cells[i] = CellState.Aggravated;
                RecomputePenalty();
                return;
            }
        }
        // Всё уже A — игнор.
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

    private void StepAsNonLethal()
    {
        // Подшаг для расщепления летального: одна операция /.
        int idxS = IndexOfFirst(CellState.Empty);
        if (idxS >= 0)
        {
            _cells[idxS] = CellState.NonLethal;
            return;
        }
        int idxSlash = IndexOfFirst(CellState.NonLethal);
        if (idxSlash >= 0)
        {
            _cells[idxSlash] = CellState.Lethal;
            return;
        }
        // Негде — игнор.
    }

    private int IndexOfFirst(CellState state)
    {
        for (int i = 0; i < _cells.Length; i++)
            if (_cells[i] == state) return i;
        return -1;
    }

    private bool HasAny(CellState state)
    {
        for (int i = 0; i < _cells.Length; i++)
            if (_cells[i] == state) return true;
        return false;
    }

    private void RecomputePenalty()
    {
            // Позиция последнего X или A сверху (ячейка 0 = 0 = штраф 0).
        int last = -1;
        for (int i = 0; i < _cells.Length; i++)
        {
                if (_cells[i] == CellState.Lethal || _cells[i] == CellState.Aggravated)
                last = i;
        }
        Penalty = Math.Max(0, last);
        TablePenalty = ComputeTablePenalty(_cells);
        IsIncapacitated = last == _cells.Length - 1 && last >= 0;
    }

    /// <summary>
    /// Штраф по таблице V20 (стр. 92) на основе количества заполненных ячеек.
    /// Правило: индекс последней заполненной ячейки N → штраф по таблице.
    /// Пустая шкала → null.
    /// </summary>
    public static int? ComputeTablePenalty(CellState[] cells)
    {
        if (cells is null || cells.Length == 0) return null;
        int last = -1;
        for (int i = 0; i < cells.Length; i++)
        {
                if (cells[i] == CellState.Lethal || cells[i] == CellState.Aggravated)
                last = i;
        }
            if (last < 0) return 0; // без летального/агравированного — штраф 0 по таблице
        // V20 стр. 92:
        // 0  Помят       -0
        // 1  Легко ранен -1
        // 2  Ранен       -1
        // 3  Серьёзно    -2
        // 4  Тяжело      -2
        // 5  Совсем плох -5
        // 6  Небоеспос.  -0
        return last switch
        {
            0 => 0,
            1 => -1,
            2 => -1,
            3 => -2,
            4 => -2,
            5 => -5,
            _ => 0, // 6+ — небоеспособен, без штрафа на броски
        };
    }

    /// <summary>
    /// Привести состояние к инварианту [A…A][X…X][/…/][S…S].
    /// Используется при десериализации из старого/повреждённого файла.
    /// </summary>
    private void NormalizeInvariant()
    {
        // 1. Все A остаются на месте (они уже сверху).
        // 2. Все X должны быть после A.
        // 3. Все / после X.
        // Если порядок нарушен — переразложить.
        int aCount = _cells.Count(c => c == CellState.Aggravated);
        int xCount = _cells.Count(c => c == CellState.Lethal);
        int slashCount = _cells.Count(c => c == CellState.NonLethal);
        int sCount = _cells.Count(c => c == CellState.Empty);
        // Если суммы сходятся, порядок уже инвариантен (валидация).
        // Если нет — пересобираем.
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
