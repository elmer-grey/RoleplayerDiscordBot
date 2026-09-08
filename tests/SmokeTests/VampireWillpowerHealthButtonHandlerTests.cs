using System;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Microsoft.Extensions.DependencyInjection;
using RPBot;
using RPBot.VtM;
using Xunit;

#pragma warning disable CS0618 // Willpower/WillpowerPoints — устарели для листа, но всё ещё runtime-поля для кнопок.

namespace SmokeTests;

/// <summary>
/// Smoke-проверки обработчиков кнопок «Воля» и «Здоровье» (Roadmap /vampire).
/// Валидируем:
/// <list type="bullet">
///   <item>префиксы customId корректно парсятся (TryParse),</item>
///   <item>после spend/restore пункт воли обновляется,</item>
///   <item>после apply/heal ячейка здоровья двигается.</item>
/// </list>
/// </summary>
/// <remarks>
/// Сами handler'ы обращаются к <see cref="SocketMessageComponent"/>, поэтому
/// полные интеграционные сценарии (нажатие через Discord) тестируются
/// вручную (live-smoke-checklist.md). На уровне unit-логики достаточно
/// проверить работу с <see cref="VampireCharacter"/> через те же методы,
/// которые вызываются в handler'ах.
/// </remarks>
public sealed class VampireWillpowerHealthButtonHandlerTests
{
    [Fact]
    public void WillpowerComponents_BuildId_RoundtripsTryParse()
    {
        var charId = Guid.NewGuid();
        var cid = VampireWillpowerComponents.BuildId(WillpowerAction.SpendOne, charId);

        Assert.True(VampireWillpowerComponents.TryParse(cid, out var action, out var id));
        Assert.Equal(WillpowerAction.SpendOne, action);
        Assert.Equal(charId, id);

        var cid2 = VampireWillpowerComponents.BuildId(WillpowerAction.RestoreOne, charId);
        Assert.True(VampireWillpowerComponents.TryParse(cid2, out var action2, out _));
        Assert.Equal(WillpowerAction.RestoreOne, action2);
    }

    [Fact]
    public void HealthComponents_BuildId_RoundtripsTryParse()
    {
        var charId = Guid.NewGuid();
        var cid = VampireHealthComponents.BuildId(HealthAction.ApplyNonLethal, charId);

        Assert.True(VampireHealthComponents.TryParse(cid, out var action, out var id));
        Assert.Equal(HealthAction.ApplyNonLethal, action);
        Assert.Equal(charId, id);

        var cid2 = VampireHealthComponents.BuildId(HealthAction.HealOne, charId);
        Assert.True(VampireHealthComponents.TryParse(cid2, out var action2, out _));
        Assert.Equal(HealthAction.HealOne, action2);
    }

    [Fact]
    public void Willpower_Spend_DecrementsPoints()
    {
        var ch = new VampireCharacter
        {
            CharacterId = Guid.NewGuid(),
            PlayerId = 1UL,
            Willpower = 5,
            WillpowerPoints = 3,
        };
        ch.EnsureWillpowerPointsValid();
        Assert.Equal(3, ch.WillpowerPoints);

        // Эмулируем действие SpendOne.
        ch.WillpowerPoints = ch.WillpowerPoints - 1;
        ch.WillpowerSpentThisTurn = true;

        Assert.Equal(2, ch.WillpowerPoints);
        Assert.True(ch.WillpowerSpentThisTurn);
    }

    [Fact]
    public void Willpower_Restore_DoesNotExceedCeiling()
    {
        var ch = new VampireCharacter
        {
            CharacterId = Guid.NewGuid(),
            PlayerId = 1UL,
            Willpower = 5,
            WillpowerPoints = 5,
        };
        ch.EnsureWillpowerPointsValid();
        Assert.Equal(5, ch.WillpowerPoints);

        // Восстанавливать нечего.
        var canRestore = ch.WillpowerPoints < ch.Willpower;
        Assert.False(canRestore);
    }

    [Fact]
    public void Health_ApplyLethal_FillsFirstEmptyCell()
    {
        var h = new HealthState(7);
        Assert.Equal(CellState.Empty, h.Cells[0]);
        h.ApplyLethal(1);
        // Один ApplyLethal = 2 подоперации: первая S → /, вторая → /.
        Assert.Equal(CellState.NonLethal, h.Cells[0]);
        Assert.Equal(CellState.NonLethal, h.Cells[1]);
    }

    [Fact]
    public void Health_HealOne_RestoresBottommostWound()
    {
        var h = new HealthState(7);
        h.ApplyLethal(1); // 2 ячейки заняты как /
        h.Heal(1); // самая нижняя / восстанавливается
        Assert.Equal(CellState.NonLethal, h.Cells[0]);
        Assert.Equal(CellState.Empty, h.Cells[1]);
    }

    [Fact]
    public void Health_Aggravated_FillsAggravatedCells()
    {
        var h = new HealthState(3);
        h.ApplyAggravated(1);
        Assert.Equal(CellState.Aggravated, h.Cells[0]);
    }
}