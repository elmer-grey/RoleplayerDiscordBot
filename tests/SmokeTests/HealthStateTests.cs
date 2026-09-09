using System;
using RPBot.VtM;
using Xunit;

namespace SmokeTests;

/// <summary>
/// Тесты для <see cref="HealthState"/> по 21 эталонному примеру пользователя (2026-09-02).
/// Размер шкалы = 7 (стойкость 4 + 3). Ячейки: 0..6 сверху вниз.
/// S = пусто, / = нелетальный, X = летальный, A = агравированный.
/// </summary>
public class HealthStateTests
{
    private static HealthState H() => new(7);

    // ─── Нелетальный урон ────────────────────────────────────────────────

    [Fact]
    public void N1_SSSSSSS_Plus1_BecomesSlashSSSSSS()
    {
        // SSSSSSS + / → /SSSSSS
        var h = H();
        h.ApplyNonLethal(1);
        Assert.Equal("/SSSSSS", h.Render());
    }

    [Fact]
    public void N2_AllSlashes_Plus1_BecomesX()
    {
        // /////// + / → X//////
        var h = H();
        h.ApplyNonLethal(7);
        Assert.Equal("///////", h.Render());
        h.ApplyNonLethal(1);
        Assert.Equal("X//////", h.Render());
    }

    [Fact]
    public void N3_XXSlash3SS_Plus1_FillsFirstEmpty()
    {
        // XX///SS + / → XX////S
        // Строим через прямой конструктор (естественный flow ведёт к ////SS).
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.NonLethal, CellState.NonLethal,
            CellState.NonLethal, CellState.Empty, CellState.Empty
        };
        var h = new HealthState(7, cells);
        Assert.Equal("XX///SS", h.Render());
        h.ApplyNonLethal(1);
        Assert.Equal("XX////S", h.Render());
    }

    [Fact]
    public void N4_XSlash6_Plus1_StaysAtFull_NoEmptyLeft()
    {
        // X////// + / → X////// (нет S, первая / → X только если / есть; тут X уже на яч.0, яч.1..6 = /.
        // Правило: пустых нет → первая / → X. Значит должно стать XX/////, а не X//////.
        // Пользователь сказал X/////S + / → X////// — здесь X/////S: первая S на яч.6, она → /.
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.NonLethal, CellState.NonLethal, CellState.NonLethal,
            CellState.NonLethal, CellState.NonLethal, CellState.Empty
        };
        var h = new HealthState(7, cells);
        Assert.Equal("X/////S", h.Render());
        h.ApplyNonLethal(1);
        // Первая S (яч.6) → / → X//////.
        Assert.Equal("X//////", h.Render());
    }

    [Fact]
    public void N5_Slash5SS_Plus1_FillsFirstEmpty()
    {
        // /////SS + / → //////S
        var h = H();
        h.ApplyNonLethal(5);
        Assert.Equal("/////SS", h.Render());
        h.ApplyNonLethal(1);
        Assert.Equal("//////S", h.Render());
    }

    [Fact]
    public void N6_XXSlash3SS_Plus2_FillsAllS()
    {
        // XX///SS + // → XX/////
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.NonLethal, CellState.NonLethal,
            CellState.NonLethal, CellState.Empty, CellState.Empty
        };
        var h = new HealthState(7, cells);
        Assert.Equal("XX///SS", h.Render());
        h.ApplyNonLethal(2);
        // 1-я /: яч.5 → / → XX////S.
        // 2-я /: яч.6 → / → XX/////.
        Assert.Equal("XX/////", h.Render());
    }

    [Fact]
    public void N7_XXSlash3SS_Plus3_BecomesXXXSlash4()
    {
        // XX///SS + /// → XXX////
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.NonLethal, CellState.NonLethal,
            CellState.NonLethal, CellState.Empty, CellState.Empty
        };
        var h = new HealthState(7, cells);
        Assert.Equal("XX///SS", h.Render());
        h.ApplyNonLethal(3);
        // 1-я: яч.5 → / → XX////S. 2-я: яч.6 → / → XX/////. 3-я: пустых нет → первая / (яч.2) → X → XXX////.
        Assert.Equal("XXX////", h.Render());
    }

    [Fact]
    public void N8_XXSlash5_Plus1_BecomesXXXSlash4()
    {
        // XX///// + / → XXX////
        var h = H();
        // XX///// получаем: 3 нелетальных + 2 летальных + 1 нелетальный (он же закрывает S и даёт X)
        // Реально проще: SSSSSSS + 3 / → ///SSSS, + 2 X → ////SS (не XX/////).
        // XX///// получается только через прямой конструктор:
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.NonLethal, CellState.NonLethal,
            CellState.NonLethal, CellState.NonLethal, CellState.NonLethal
        };
        var h2 = new HealthState(7, cells);
        Assert.Equal("XX/////", h2.Render());
        h2.ApplyNonLethal(1);
        // Пустых нет → первая / (яч.2) → X → XXX////.
        Assert.Equal("XXX////", h2.Render());
    }

    // ─── Летальный урон ──────────────────────────────────────────────────

    [Fact]
    public void L1_SSSSSSS_Plus1_Becomes2Slashes()
    {
        // SSSSSSS + X → //SSSSS
        var h = H();
        h.ApplyLethal(1);
        Assert.Equal("//SSSSS", h.Render());
    }

    [Fact]
    public void L2_Slash2S5_Plus1_Becomes4Slashes()
    {
        // //SSSSS + X → ////SSS
        var h = H();
        h.ApplyLethal(1);
        Assert.Equal("//SSSSS", h.Render());
        h.ApplyLethal(1);
        // 1-я подоперация: яч.2 → /. 2-я: яч.3 → /.
        Assert.Equal("////SSS", h.Render());
    }

    [Fact]
    public void L3_Slash5SS_Plus1_BecomesAllSlashes()
    {
        // /////SS + X → ///////
        var h = H();
        h.ApplyNonLethal(5);
        Assert.Equal("/////SS", h.Render());
        h.ApplyLethal(1);
        // 1-я: яч.5 → /. 2-я: яч.6 → /.
        Assert.Equal("///////", h.Render());
    }

    [Fact]
    public void L4_Slash3SS4_Plus2_BecomesAllSlashes()
    {
        // ///SSSS + XX → ///////
        var h = H();
        h.ApplyNonLethal(3);
        Assert.Equal("///SSSS", h.Render());
        h.ApplyLethal(2);
        // 1-й X: яч.3 → /, яч.4 → / → ////SS.
        // 2-й X: яч.5 → /, яч.6 → / → ///////.
        Assert.Equal("///////", h.Render());
    }

    [Fact]
    public void L5_XXSlash4S_Plus1_BecomesXXXSlash4()
    {
        // XX////S + X → XXX////
        // Строим XX////S через прямой конструктор.
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.NonLethal, CellState.NonLethal,
            CellState.NonLethal, CellState.NonLethal, CellState.Empty
        };
        var h = new HealthState(7, cells);
        Assert.Equal("XX////S", h.Render());
        h.ApplyLethal(1);
        // 1-я подоперация: яч.6 → / → XX/////.
        // 2-я подоперация: нет S → первая / (яч.2) → X → XXX////.
        Assert.Equal("XXX////", h.Render());
    }

    [Fact]
    public void L6_XXXXSSS_Plus1_BecomesXXXXXSS()
    {
        // XXXXSSS + X → XXXXXSS
        // Правило пользователя: летальный урон, когда шкала уже частично X,
        // "расщепляется" — 1-я / в первую S, 2-я превращает эту / обратно в X (если слева X есть).
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.Lethal, CellState.Lethal,
            CellState.Empty, CellState.Empty, CellState.Empty
        };
        var h = new HealthState(7, cells);
        Assert.Equal("XXXXSSS", h.Render());
        h.ApplyLethal(1);
        Assert.Equal("XXXXXSS", h.Render());
    }

    [Fact]
    public void L7_XXXXXXS_Plus1_BecomesAllX_Torpor()
    {
        // XXXXXXS + X → XXXXXXX (торпор)
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.Lethal, CellState.Lethal,
            CellState.Lethal, CellState.Lethal, CellState.Empty
        };
        var h = new HealthState(7, cells);
        Assert.Equal("XXXXXXS", h.Render());
        h.ApplyLethal(1);
        // 1-я: яч.6 → / → XXXXXX/.
        // 2-я: нет S → первая / (яч.6) → X → XXXXXXX.
        Assert.Equal("XXXXXXX", h.Render());
        Assert.True(h.IsTorpor);
    }

    [Fact]
    public void L8_XXXXXXX_Plus1_Dies()
    {
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.Lethal, CellState.Lethal,
            CellState.Lethal, CellState.Lethal, CellState.Lethal
        };
        var h = new HealthState(7, cells);
        Assert.True(h.IsTorpor);
        h.ApplyLethal(1);
        // Нет ни S, ни / — IsDead.
        Assert.True(h.IsDead);
    }

    [Fact]
    public void L9_XXSlash5_Plus1_BecomesXXXXSlash3()
    {
        // XX///// + X → XXXX///
        // Строим через прямой конструктор.
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.NonLethal, CellState.NonLethal,
            CellState.NonLethal, CellState.NonLethal, CellState.NonLethal
        };
        var h = new HealthState(7, cells);
        Assert.Equal("XX/////", h.Render());
        h.ApplyLethal(1);
        // 1-я подоперация: нет S → первая / (яч.2) → X → XXX////.
        // 2-я подоперация: нет S → первая / (яч.3) → X → XXXX///.
        Assert.Equal("XXXX///", h.Render());
    }

    // ─── Агравированный ──────────────────────────────────────────────────

    [Fact]
    public void A1_AXSlash4S_Plus1_BecomesAAXSlash4S_DropLastEmpty()
    {
        // AX////S (1 A + 1 X + 4 / + 1 S) + A:
        //   snapshot=[A,X,/,/,/,/,S], targetIdx=1.
        //   Сдвиг яч.2..6 по снимку: яч.2←X, яч.3←/, яч.4←/, яч.5←/, яч.6←/ (snapshot[5]=/).
        //   snapshot[6]=S «выпадает» за шкалу — S теряется.
        //   яч.1 = A.
        //   Итого: AAX//// (1 A + 1 X + 4 / + 0 S).
        var cells = new CellState[]
        {
            CellState.Aggravated, CellState.Lethal, CellState.NonLethal, CellState.NonLethal,
            CellState.NonLethal, CellState.NonLethal, CellState.Empty
        };
        var h = new HealthState(7, cells);
        Assert.Equal("AX////S", h.Render());
        h.ApplyAggravated(1);
        Assert.Equal("AAX////", h.Render());
    }

    [Fact]
    public void A2_AXXXXXX_Plus1_BecomesAAXXXXX()
    {
        var cells = new CellState[]
        {
            CellState.Aggravated, CellState.Lethal, CellState.Lethal, CellState.Lethal,
            CellState.Lethal, CellState.Lethal, CellState.Lethal
        };
        var h = new HealthState(7, cells);
        Assert.Equal("AXXXXXX", h.Render());
        h.ApplyAggravated(1);
        Assert.Equal("AAXXXXX", h.Render());
    }

    [Fact]
    public void A3_AllA_IsDestroyed()
    {
        var h = H();
        h.ApplyAggravated(7);
        Assert.Equal("AAAAAAA", h.Render());
        Assert.True(h.IsDestroyed);
        h.ApplyAggravated(1);
        Assert.True(h.IsDestroyed);
    }

    [Fact]
    public void A4_FillsFromTop_ReplacingAnything()
    {
        var h = H();
        h.ApplyNonLethal(2);
        h.ApplyLethal(1);
        // //SSSSS + X = ////SSS.
        Assert.Equal("////SSS", h.Render());
        h.ApplyAggravated(1);
        // targetIdx=0. Сдвиг: яч.1=/→яч.2, яч.2=/→яч.3, яч.3=/→яч.4, яч.4=S → break.
        // яч.0 = A.
        // Итого: A////SS.
        Assert.Equal("A////SS", h.Render());
    }

    // ─── Каскад агравированного по правилам пользователя (2026-09-09) ───

    [Fact]
    public void Cascade_EmptyToS_PutsAAtFirstEmpty()
    {
        // SSSSSSS + A → A в яч.0, остальное без изменений (всё S, сдвиг останавливается сразу).
        var h = H();
        h.ApplyAggravated(1);
        Assert.Equal("ASSSSSS", h.Render());
    }

    [Fact]
    public void Cascade_OnlyX_ShiftsAllDownByOne_DropsLastEmpty()
    {
        // Пример 3 пользователя 2026-09-09: X в яч.0..2, / в яч.3..4, S в яч.5..6.
        // + A → A в яч.0, X в яч.1..3, / в яч.4..5, S в яч.6 (последний элемент
        // сдвига, snapshot[6]=S, выпадает, и яч.6 получает snapshot[5]=S).
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.Lethal,
            CellState.NonLethal, CellState.NonLethal,
            CellState.Empty, CellState.Empty
        };
        var h = new HealthState(7, cells);
        Assert.Equal("XXX//SS", h.Render());
        h.ApplyAggravated(1);
        Assert.Equal("AXXX//S", h.Render());
        Assert.Equal(-2, h.TablePenalty); // penaltyIndex = 3 = Серьёзно ранен
    }

    [Fact]
    public void Cascade_PreservesEmptyCell_NoDropping()
    {
        // Дубль примера 3 пользователя.
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.Lethal,
            CellState.NonLethal, CellState.NonLethal,
            CellState.Empty, CellState.Empty
        };
        var h = new HealthState(7, cells);
        h.ApplyAggravated(1);
        Assert.Equal("AXXX//S", h.Render());
    }

    [Fact]
    public void Cascade_WithSlashInBuffer_ShiftsEverything()
    {
        // Пример 5 пользователя 2026-09-09: / в яч.0..4, S в яч.5..6 (5 / + 2 S = 7 ячеек).
        // (Это «если бы в примере 3 вместо X был бы /»: X в 0..2 → / в 0..4, / в 3..4 → S в 5..6.)
        // + A → A в яч.0, / в яч.1..5, S в яч.6.
        var cells = new CellState[]
        {
            CellState.NonLethal, CellState.NonLethal, CellState.NonLethal,
            CellState.NonLethal, CellState.NonLethal,
            CellState.Empty, CellState.Empty
        };
        var h = new HealthState(7, cells);
        Assert.Equal("/////SS", h.Render());
        h.ApplyAggravated(1);
        Assert.Equal("A/////S", h.Render());
    }

    [Fact]
    public void Cascade_FullScale_ShiftsAllByOne()
    {
        // XXXXXXX + A → AXXXXXX (targetIdx=0, сдвиг проходит до конца).
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.Lethal, CellState.Lethal,
            CellState.Lethal, CellState.Lethal, CellState.Lethal
        };
        var h = new HealthState(7, cells);
        h.ApplyAggravated(1);
        Assert.Equal("AXXXXXX", h.Render());
    }

    [Fact]
    public void Cascade_StopsAtFirstEmpty_DropsLastSnapshot()
    {
        // AAXXXXS + A: targetIdx=2 (X). Снимок: [A,A,X,X,X,X,S].
        //   Сдвиг яч.3..6 по снимку: яч.3←X, яч.4←X, яч.5←X, яч.6←X (snapshot[5]=X).
        //   snapshot[6]=S «выпадает» за шкалу.
        //   яч.2 = A.
        //   Итого: AAAXXXX (3 A + 4 X + 0 S).
        var cells = new CellState[]
        {
            CellState.Aggravated, CellState.Aggravated, CellState.Lethal, CellState.Lethal,
            CellState.Lethal, CellState.Lethal, CellState.Empty
        };
        var h = new HealthState(7, cells);
        Assert.Equal("AAXXXXS", h.Render());
        h.ApplyAggravated(1);
        Assert.Equal("AAAXXXX", h.Render());
    }

    // ─── Лечение ────────────────────────────────────────────────────────

    [Fact]
    public void H1_X6_2_BecomesX4SS()
    {
        // X////// - 2 = X////SS
        var h = H();
        h.ApplyNonLethal(8);
        Assert.Equal("X//////", h.Render());
        h.Heal(2);
        // Снизу вверх: яч.6 → S, яч.5 → S.
        Assert.Equal("X////SS", h.Render());
    }

    [Fact]
    public void H2_XXSlash5_3_BecomesXXSlash2SSS()
    {
        // XX///// - 3 = XX//SSS
        // Строим через прямой конструктор.
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.NonLethal, CellState.NonLethal,
            CellState.NonLethal, CellState.NonLethal, CellState.NonLethal
        };
        var h = new HealthState(7, cells);
        Assert.Equal("XX/////", h.Render());
        h.Heal(3);
        // Снизу вверх: яч.6, 5, 4 → S.
        Assert.Equal("XX//SSS", h.Render());
    }

    [Fact]
    public void H3_AllLethal_3_Becomes4Lethal3S()
    {
        // XXXXXXX - 3 = XXXXSSS (через конструктор)
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.Lethal, CellState.Lethal,
            CellState.Lethal, CellState.Lethal, CellState.Lethal
        };
        var h = new HealthState(7, cells);
        h.Heal(3);
        Assert.Equal("XXXXSSS", h.Render());
    }

    // ─── Штраф и инвариант ──────────────────────────────────────────────

    [Fact]
    public void Penalty_AllEmpty_IsZero()
    {
        Assert.Equal(0, H().Penalty);
        Assert.Equal(0, H().TablePenalty);
    }

    [Fact]
    public void Penalty_OnlySlashes_IsZero()
    {
        var h = H();
        h.ApplyNonLethal(3);
        Assert.Equal(0, h.Penalty);
        Assert.Equal(0, h.TablePenalty);
    }

    [Fact]
    public void Penalty_PositionOfLastXOrA()
    {
        var h = H();
        h.ApplyAggravated(2); // AA..... → lastXOrA=1, hasA=true → penaltyIndex=2 → -1 (Ранен)
        Assert.Equal(1, h.Penalty);
        Assert.Equal(-1, h.TablePenalty);
        h.ApplyAggravated(1); // AAA.... → lastXOrA=2, hasA=true → penaltyIndex=3 → -2 (Серьёзно)
        Assert.Equal(2, h.Penalty);
        Assert.Equal(-2, h.TablePenalty);
    }

    // ─── V20 таблица штрафов здоровья (стр. 92) ─────────────────────────

    [Theory]
    [InlineData(0, 0)]    // Помят
    [InlineData(1, -1)]   // Легко ранен
    [InlineData(2, -1)]   // Ранен
    [InlineData(3, -2)]   // Серьёзно
    [InlineData(4, -2)]   // Тяжело
    [InlineData(5, -5)]   // Совсем плох
    [InlineData(6, 0)]    // Небоеспособен
    public void TablePenalty_FollowsV20Page92(int lastFilledCell, int expectedPenalty)
    {
        var cells = new CellState[7];
        for (int i = 0; i <= lastFilledCell; i++)
            cells[i] = CellState.Lethal;
        Assert.Equal(expectedPenalty, HealthState.ComputeTablePenalty(cells));
    }

    [Fact]
    public void TablePenalty_AggravatedShiftsPenaltyDownByOne()
    {
        // Пример пользователя 2026-09-09: A в яч.0, X в яч.1,2.
        // lastXOrA=2, hasA=true → penaltyIndex=3 → -2 (Серьёзно).
        var cells = new CellState[]
        {
            CellState.Aggravated, CellState.Lethal, CellState.Lethal,
            CellState.Empty, CellState.Empty, CellState.Empty, CellState.Empty
        };
        Assert.Equal(-2, HealthState.ComputeTablePenalty(cells));
    }

    [Fact]
    public void TablePenalty_AggravatedOnly_StillShiftsDown()
    {
        // Только A, без X. lastXOrA=1, hasA=true → penaltyIndex=2 → -1 (Ранен).
        // Правило «A сдвигает штраф вниз на 1» применяется, но не дальше 6 ячейки.
        var cells = new CellState[]
        {
            CellState.Aggravated, CellState.Aggravated, CellState.Empty,
            CellState.Empty, CellState.Empty, CellState.Empty, CellState.Empty
        };
        Assert.Equal(-1, HealthState.ComputeTablePenalty(cells));
    }

    [Fact]
    public void TablePenalty_EmptyArray_IsNull()
    {
        Assert.Null(HealthState.ComputeTablePenalty(Array.Empty<CellState>()));
        Assert.Null(HealthState.ComputeTablePenalty(null!));
    }

    // ─── Небоеспособен (V20 стр. 274) ───────────────────────────────────

    [Fact]
    public void IsIncapacitated_OnlyWhenLastCellFilled()
    {
        var h = H();
        Assert.False(h.IsIncapacitated);
        for (int i = 0; i < 6; i++) h.ApplyLethal(1);
        Assert.False(h.IsIncapacitated); // 6 из 7 заполнено — ещё нет
        h.ApplyLethal(1); // 7-я ячейка
        Assert.True(h.IsIncapacitated);
    }

    [Fact]
    public void IsIncapacitated_AggravatedAlsoTriggers()
    {
        var h = H();
        for (int i = 0; i < 6; i++) h.ApplyAggravated(1);
        Assert.False(h.IsIncapacitated);
        h.ApplyAggravated(1);
        Assert.True(h.IsIncapacitated);
    }

    [Fact]
    public void Incapacitated_HasZeroTablePenalty_ButCannotRoll()
    {
        // По таблице V20 «нет штрафа на 7 строках» — числовой штраф = 0.
        // Но логика бросков должна проверять IsIncapacitated отдельно.
        var h = new HealthState(7, new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.Lethal,
            CellState.Lethal, CellState.Lethal, CellState.Lethal,
            CellState.Lethal,
        });
        Assert.Equal(0, h.TablePenalty);
        Assert.True(h.IsIncapacitated);
    }

    [Fact]
    public void Penalty_Cell0_IsZero_Buffer()
    {
        var h = H();
        h.ApplyLethal(1); // //SSSSS
        Assert.Equal(0, h.Penalty);
        h.ApplyNonLethal(5); // //////S
        h.ApplyNonLethal(1); // X//////
        Assert.Equal(0, h.Penalty); // буфер.
        h.ApplyNonLethal(1); // XX/////
        Assert.Equal(1, h.Penalty);
        // Проверим XXXXXXS (6 X): штраф = 5 (позиция яч.5).
        var h2 = new HealthState(7, new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.Lethal, CellState.Lethal,
            CellState.Lethal, CellState.Lethal, CellState.Empty
        });
        Assert.Equal(5, h2.Penalty);
        // XXXXXXX: штраф = 6.
        var h3 = new HealthState(7, new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.Lethal, CellState.Lethal,
            CellState.Lethal, CellState.Lethal, CellState.Lethal
        });
        Assert.Equal(6, h3.Penalty);
    }

    [Fact]
    public void Invariant_NormalizesBrokenState()
    {
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.NonLethal, CellState.Lethal, CellState.Empty,
            CellState.Empty, CellState.Empty, CellState.Empty
        };
        var h = new HealthState(7, cells);
        Assert.Equal("XX/SSSS", h.Render());
    }

    [Fact]
    public void Reset_ClearsAllCells()
    {
        var h = H();
        h.ApplyNonLethal(5);
        h.ApplyLethal(2);
        h.ApplyAggravated(1);
        h.Reset();
        Assert.Equal("SSSSSSS", h.Render());
        Assert.Equal(0, h.Penalty);
        Assert.False(h.IsDead);
    }

    [Fact]
    public void Size_MustBeAtLeast1()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HealthState(0));
    }

    [Fact]
    public void NonLethal_OnDestroyed_IsNoOp()
    {
        var h = H();
        h.ApplyAggravated(7);
        Assert.True(h.IsDestroyed);
        h.ApplyNonLethal(3);
        Assert.Equal("AAAAAAA", h.Render());
    }

    [Fact]
    public void Lethal_OnDead_StaysDead()
    {
        var cells = new CellState[]
        {
            CellState.Lethal, CellState.Lethal, CellState.Lethal, CellState.Lethal,
            CellState.Lethal, CellState.Lethal, CellState.Lethal
        };
        var h = new HealthState(7, cells);
        h.ApplyLethal(1);
        Assert.True(h.IsDead);
        h.ApplyLethal(5);
        Assert.True(h.IsDead);
    }

    // ─── Разные размеры шкалы (Стойкость 1..5 → 4..8 ячеек) ───────────
    // В текущей версии шкала фиксирована = 7 ячеек (Стойкость 4 + 3). Старые
    // тесты для size!=7 оставлены для обратной совместимости, но ожидания
    // штрафа пересмотрены под текущую таблицу.

    [Theory]
    [InlineData(7, "X/////S", 0, 0)]        // 1 X, штраф 0 (буфер).
    [InlineData(7, "XX////S", 1, -1)]       // 2 X, штраф -1 (Легко ранен).
    [InlineData(7, "XXX///S", 2, -1)]       // 3 X, штраф -1 (Ранен).
    [InlineData(7, "AXX///S", 2, -2)]       // A в 0, X в 1..2; lastXOrA=2, +1=3 → -2 (Серьёзно).
    [InlineData(7, "XXXXX/S", 4, -2)]       // 5 X, штраф -2 (Тяжело).
    [InlineData(7, "XXXXXXS", 5, -5)]       // 6 X, штраф -5 (Совсем плох).
    [InlineData(7, "XXXXXXX", 6, 0)]        // 7 X, штраф 0 (Небоеспособен, торпор).
    public void Penalty_ForVariousSizes(int size, string expectedRender, int expectedLastXOrA, int expectedTablePenalty)
    {
        var parsed = ParseRender(expectedRender, size);
        var h = new HealthState(size, parsed);
        Assert.Equal(expectedRender, h.Render());
        Assert.Equal(expectedLastXOrA, h.Penalty);
        Assert.Equal(expectedTablePenalty, h.TablePenalty);
    }

    [Theory]
    [InlineData(4)] // Стойкость 1
    [InlineData(5)] // Стойкость 2
    [InlineData(6)] // Стойкость 3
    [InlineData(8)] // Стойкость 5
    public void EmptyScale_FillsWithNonLethal(int size)
    {
        var h = new HealthState(size);
        h.ApplyNonLethal(size);
        var expected = new string('/', size);
        Assert.Equal(expected, h.Render());
        Assert.Equal(0, h.Penalty);
        // Один следующий нелетальный превращает первую / в X.
        h.ApplyNonLethal(1);
        var expected2 = "X" + new string('/', size - 1);
        Assert.Equal(expected2, h.Render());
        Assert.Equal(0, h.Penalty);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public void EmptyScale_Lethal_Becomes2Slashes(int size)
    {
        var h = new HealthState(size);
        h.ApplyLethal(1);
        Assert.Equal("//" + new string('S', size - 2), h.Render());
        Assert.Equal(0, h.Penalty);
    }

    [Theory]
    [InlineData(4, "XSSS", "XXSS")]           // XSSS + X → XXSS
    [InlineData(4, "XXSS", "XXXS")]           // XXSS + X → XXXS
    [InlineData(5, "XXSSS", "XXXSS")]         // XXSSS + X → XXXSS
    [InlineData(5, "XXXXS", "XXXXX")]         // XXXXS + X → XXXXX (торпор)
    [InlineData(6, "XXXSSS", "XXXXSS")]       // XXXSSS + X → XXXXSS
    [InlineData(6, "XXXXSS", "XXXXXS")]       // XXXXSS + X → XXXXXS
    [InlineData(8, "XXXXXXSS", "XXXXXXXS")]   // XXXXXXSS + X → XXXXXXXS
    [InlineData(8, "XXXXXXXS", "XXXXXXXX")]   // XXXXXXXS + X → XXXXXXXX (торпор)
    public void Lethal_OnPartiallyX_NoSlashes_DirectX(int size, string start, string expected)
    {
        // Правило «X уже есть, слэшей нет, S есть» — первая S → X сразу.
        var h = new HealthState(size, ParseRender(start, size));
        Assert.Equal(start, h.Render());
        h.ApplyLethal(1);
        Assert.Equal(expected, h.Render());
    }

    [Theory]
    [InlineData(4, "XXXX")]   // торпор
    [InlineData(5, "XXXXX")]  // торпор
    [InlineData(6, "XXXXXX")] // торпор
    [InlineData(8, "XXXXXXXX")] // торпор
    public void Torpor_LethalOnFullX_GoesDead(int size, string start)
    {
        var cells = ParseRender(start, size);
        var h = new HealthState(size, cells);
        Assert.True(h.IsTorpor);
        Assert.False(h.IsDead);
        h.ApplyLethal(1);
        Assert.True(h.IsDead);
    }

    private static CellState[] ParseRender(string render, int size)
    {
        if (render.Length != size) throw new ArgumentException($"render len {render.Length} != size {size}");
        var arr = new CellState[size];
        for (int i = 0; i < size; i++)
        {
            arr[i] = render[i] switch
            {
                'S' => CellState.Empty,
                '/' => CellState.NonLethal,
                'X' => CellState.Lethal,
                'A' => CellState.Aggravated,
                _ => throw new ArgumentException($"bad char '{render[i]}'")
            };
        }
        return arr;
    }
}
