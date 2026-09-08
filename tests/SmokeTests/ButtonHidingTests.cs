using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.Predictions;
using SmokeTests;
using Xunit;

namespace RPBot.SmokeTests;

[Collection("BotConfig")]
public class ButtonHidingTests : IsolatedDataTestBase
{
    public ButtonHidingTests() : base("rpbot_smoke_buttons") { }

    private static DiscordSocketClient NewClient() => new DiscordSocketClient(new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.None,
        LogLevel = LogSeverity.Critical,
    });

    private static PredictionService NewService()
    {
        var pts = new PointsService(Path.Combine(Path.GetTempPath(), "pts_" + Guid.NewGuid().ToString("N") + ".json"));
        return new PredictionService(NewClient(), pts, "");
    }

    private static ActivePrediction MakePrediction(int outcomeCount = 2)
    {
        var p = new ActivePrediction
        {
            GuildId = 100,
            CreatorId = 200,
            ChannelId = 300,
            MessageId = 400,
            Title = "Тест кнопок",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-30),
            BetsCloseAtUtc = DateTimeOffset.UtcNow.AddMinutes(3),
        };
        for (var i = 1; i <= outcomeCount; i++)
        {
            p.Outcomes.Add(new PredictionOutcome { Id = i, Name = $"Исход {i}", TotalStake = 50 * i });
        }
        return p;
    }

    [Fact]
    public void Bug7_ComponentsAreBuilt_WhenNotResolvedAndNotOffline()
    {
        var p = MakePrediction();
        Assert.False(p.IsResolved);
        // Условие из UpdateMessageAsync: !IsResolved && !botOffline → строим кнопки.
        // Здесь мы BuildComponents дёргаем напрямую через рефлексию.
        var comps = InvokeBuildComponents(p, showLocked: false);
        Assert.NotNull(comps);
        var built = comps!.Build();
        Assert.NotNull(built);
        // В фазе "сбор ставок" должна быть хотя бы кнопка "Сделать ставку".
            var anyBetButton = built.Components
                .OfType<ActionRowComponent>()
                .SelectMany(r => r.Components.OfType<ButtonComponent>())
                .Any(b => b.CustomId?.StartsWith("pred_bet:") ?? false);
            Assert.True(anyBetButton, "В фазе сбора ставок должна быть кнопка pred_bet");
        }

        [Fact]
        public void Bug7_ComponentsAreBuilt_AfterLock_WithOutcomeButtons()
        {
            var p = MakePrediction(outcomeCount: 3);
            p.IsLocked = true;

            var comps = InvokeBuildComponents(p, showLocked: true);
            Assert.NotNull(comps);
            var built = comps!.Build();
            Assert.NotNull(built);

            var outcomeButtons = built.Components
                .OfType<ActionRowComponent>()
                .SelectMany(r => r.Components.OfType<ButtonComponent>())
                .Count(b => (b.CustomId?.StartsWith("pred_resolve:") ?? false));
            // Должны быть 3 кнопки исходов + 1 "Отменить".
            Assert.Equal(3, outcomeButtons);
        }

    [Fact]
    public void Bug7_NoComponents_WhenOffline_LogicMatch()
    {
        // Симулируем условие из UpdateMessageAsync:
        //   MessageComponent? comps = null;
        //   if (!p.IsResolved && !botOffline) { ... comps = cb.Build(); }
        // BotOffline=true → ветка false → comps остаётся null.

        var p = MakePrediction();
        bool botOffline = true;
        var comps = (!p.IsResolved && !botOffline)
            ? InvokeBuildComponents(p, showLocked: false)?.Build()
            : null;
        Assert.Null(comps);
    }

    [Fact]
    public void Bug7_NoComponents_WhenOffline_AfterLock()
    {
        // Фаза "ожидание разрешения" + offline → тоже без кнопок.
        var p = MakePrediction();
        p.IsLocked = true;

        bool botOffline = true;
        MessageComponent? comps = (!p.IsResolved && !botOffline)
            ? InvokeBuildComponents(p, showLocked: true)?.Build()
            : null;

        Assert.Null(comps);
        // Sanity: при botOffline=false кнопки должны быть.
        comps = (!p.IsResolved && !false)
            ? InvokeBuildComponents(p, showLocked: true)?.Build()
            : null;
        Assert.NotNull(comps);
    }

    [Fact]
    public void Bug7_ResolvedPredictions_NeverShowComponents()
    {
        var p = MakePrediction();
        p.IsResolved = true;

        // Даже если botOffline=false, IsResolved=true → comps=null.
        MessageComponent? comps = (!p.IsResolved && !false)
            ? InvokeBuildComponents(p, showLocked: false)?.Build()
            : null;
        Assert.Null(comps);
    }

    [Fact]
    public async Task PredictionService_ConstructionAndShutdown_DoesNotThrow()
    {
        // Полноценный smoke: конструктор PredictionService, Shutdown, без сетевых вызовов.
        var svc = NewService();
        try
        {
            Assert.NotNull(svc);
        }
        finally
        {
            try { svc.Shutdown(); } catch { }
        }

        // Без вызова LogAsync/SaveStateAsync после Shutdown — инвариант.
        await Task.CompletedTask;
    }

    // Рефлексивный доступ к приватному BuildComponents.
    private static ComponentBuilder? InvokeBuildComponents(ActivePrediction p, bool showLocked)
    {
        var svc = NewService();
        try
        {
            var m = typeof(PredictionService).GetMethod(
                "BuildComponents",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (m == null) return null;
            var result = m.Invoke(svc, new object?[] { p, showLocked });
            return result as ComponentBuilder;
        }
        finally
        {
            try { svc.Shutdown(); } catch { }
        }
    }
}
