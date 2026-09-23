using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.Predictions;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// pred-parallelization — тесты под новый API PredictionService,
/// который позволяет держать несколько активных прогнозов параллельно
/// на одной гильдии (по одному на каждый голосовой канал).
/// </summary>
public class PredictionParallelizationTests : IDisposable
{
    private readonly string _tmpDir;

    public PredictionParallelizationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "rpbot_smoke_predpar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        Environment.SetEnvironmentVariable("RPBOT_DATA_DIR", _tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    private static DiscordSocketClient NewClient() => new DiscordSocketClient(new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.None,
        LogLevel = LogSeverity.Critical
    });

    private static PredictionService NewService(string dir)
    {
        var pts = new PointsService(Path.Combine(dir, "points.json"));
        return new PredictionService(NewClient(), pts, "");
    }

    private static ActivePrediction MakePrediction(ulong guildId, ulong channelId, ulong creatorId, ulong? eventId = null) => new()
    {
        GuildId = guildId,
        CreatorId = creatorId,
        ChannelId = channelId,
        EventId = eventId,
        Title = $"pred g={guildId} ch={channelId}",
        Outcomes = new List<PredictionOutcome>
        {
            new() { Id = 1, Name = "A", TotalStake = 0 },
            new() { Id = 2, Name = "B", TotalStake = 0 },
        },
        CreatedAtUtc = DateTimeOffset.UtcNow,
        BetsCloseAtUtc = DateTimeOffset.UtcNow.AddMinutes(3),
        IsLocked = false,
        IsResolved = false,
        Bets = new Dictionary<ulong, PredictionBet>(),
    };

    /// <summary>
    /// Подкладывает прогноз напрямую через _active через рефлексию —
    /// чтобы не поднимать Discord-канал через ConnectAsync и
    /// SendMessageAsync (это требует реальной сессии).
    /// </summary>
    private static void InjectPrediction(PredictionService svc, ActivePrediction pred)
    {
        var activeField = typeof(PredictionService).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var activeDict = (ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, ActivePrediction>>)activeField.GetValue(svc)!;
        ConcurrentDictionary<ulong, ActivePrediction> inner;
        if (!activeDict.TryGetValue(pred.GuildId, out var byChannel))
        {
            inner = new ConcurrentDictionary<ulong, ActivePrediction>();
            activeDict[pred.GuildId] = inner;
        }
        else
        {
            inner = byChannel;
        }
        inner[pred.ChannelId] = pred;
    }

    private static void ClearAll(PredictionService svc)
    {
        var activeField = typeof(PredictionService).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var activeDict = (ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, ActivePrediction>>)activeField.GetValue(svc)!;
        activeDict.Clear();
    }

    // ───────────────────────────────────────────────────────────────────
    // 1. GetActive(guild, channel) и GetAllActive(guild)
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void GetActive_ByGuildAndChannel_ReturnsCorrectPrediction()
    {
        var svc = NewService(_tmpDir);
        try
        {
            var a = MakePrediction(guildId: 1, channelId: 100, creatorId: 10);
            var b = MakePrediction(guildId: 1, channelId: 200, creatorId: 20);
            var c = MakePrediction(guildId: 2, channelId: 300, creatorId: 30);
            InjectPrediction(svc, a);
            InjectPrediction(svc, b);
            InjectPrediction(svc, c);

            Assert.Same(a, svc.GetActive(1, 100));
            Assert.Same(b, svc.GetActive(1, 200));
            Assert.Same(c, svc.GetActive(2, 300));

            // Канал, по которому ничего нет, — null.
            Assert.Null(svc.GetActive(1, 999));

            // Другая гильдия — null.
            Assert.Null(svc.GetActive(99, 100));
        }
        finally { svc.Shutdown(); }
    }

    [Fact]
    public void GetAllActive_ByGuild_ReturnsAllChannelPredictions()
    {
        var svc = NewService(_tmpDir);
        try
        {
            var a = MakePrediction(1, 100, 10);
            var b = MakePrediction(1, 200, 20);
            var c = MakePrediction(2, 300, 30);
            InjectPrediction(svc, a);
            InjectPrediction(svc, b);
            InjectPrediction(svc, c);

            var guild1 = svc.GetAllActive(1);
            Assert.Equal(2, guild1.Count);
            Assert.Contains(a, guild1);
            Assert.Contains(b, guild1);

            var guild2 = svc.GetAllActive(2);
            Assert.Single(guild2);
            Assert.Contains(c, guild2);

            // Гильдия, которой нет — пустой массив.
            Assert.Empty(svc.GetAllActive(99));
        }
        finally { svc.Shutdown(); }
    }

    // ───────────────────────────────────────────────────────────────────
    // 2. PlaceBet(ActivePrediction, …) пишет в конкретный прогноз,
    //    не пересекаясь с соседним каналом.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlaceBet_OnPredictionA_DoesNotMutatePredictionB()
    {
        var pts = new PointsService(Path.Combine(_tmpDir, "points.json"));
        var svc = new PredictionService(NewClient(), pts, "");
        try
        {
            var a = MakePrediction(1, 100, 10);
            var b = MakePrediction(1, 200, 20);
            InjectPrediction(svc, a);
            InjectPrediction(svc, b);

            // Юзер id=99, баланс 1000.
            pts.Add(1, 99, 1000);
            Assert.Equal(1000, pts.GetBalance(1, 99));

            // Ставим в прогноз A: outcome=1, amount=300.
            var (ok, error) = await svc.PlaceBetAsync(a, userId: 99, outcomeId: 1, amount: 300L);
            Assert.True(ok, $"unexpected error: {error}");
            Assert.Single(a.Bets);
            Assert.Equal(300, a.Bets[99].Amount);
            Assert.Equal(1, a.Bets[99].OutcomeId);
            Assert.Empty(b.Bets);
            Assert.Equal(700, pts.GetBalance(1, 99));

            // Повторная ставка в A на тот же исход увеличит existing.Amount
            // (один юзер — одна запись в Bets на один исход).
            var (ok2, error2) = await svc.PlaceBetAsync(a, 99, 1, 100L);
            Assert.True(ok2, $"unexpected error2: {error2}");
            Assert.Single(a.Bets); // всё ещё одна запись — апдейт по existing
            Assert.Equal(400, a.Bets[99].Amount);
            Assert.Empty(b.Bets);
            Assert.Equal(600, pts.GetBalance(1, 99));

            // Попытка поставить на ДРУГОЙ исход должна быть отвергнута.
            var (ok3, err3) = await svc.PlaceBetAsync(a, 99, 2, 50L);
            Assert.False(ok3);
            Assert.Contains("другой исход", err3);
            Assert.Single(a.Bets);
            Assert.Empty(b.Bets);
            Assert.Equal(600, pts.GetBalance(1, 99));
        }
        finally { svc.Shutdown(); }
    }

    // ───────────────────────────────────────────────────────────────────
    // 3. CancelPredictionForEventAsync(eventId) отменяет ТОЛЬКО прогноз
    //    с этим eventId, не трогая прогнозы в других каналах.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelPredictionForEventAsync_CancelsOnlyMatchedEvent()
    {
        var pts = new PointsService(Path.Combine(_tmpDir, "points.json"));
        var svc = new PredictionService(NewClient(), pts, "");
        try
        {
            // Два прогноза в разных каналах, привязанные к РАЗНЫМ eventId.
            var predA = MakePrediction(1, 100, 10, eventId: 555);
            var predB = MakePrediction(1, 200, 20, eventId: 777);
            // Бе ставок, чтобы cancel-логика была чистой.
            InjectPrediction(svc, predA);
            InjectPrediction(svc, predB);

            // Завершилось событие eventId=555 — должен отмениться только predA.
            // CancelAsync НЕ выставляет IsResolved (это делает только ResolveAsync),
            // но гарантированно удаляет прогноз из _active через RemovePrediction.
            var (ok, error) = await svc.CancelPredictionForEventAsync(1, 555, "тестовое завершение");
            Assert.True(ok, error);

            // predA удалён из _active, predB остался.
            Assert.Null(svc.GetActive(1, 100));
            Assert.Same(predB, svc.GetActive(1, 200));
            Assert.Same(predB, svc.GetAllActive(1).Single());

            // История пополнена только для predA (был отменён).
            var historyField = typeof(PredictionService).GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic);
            if (historyField != null)
            {
                var history = historyField.GetValue(svc) as System.Collections.IEnumerable;
                if (history is not null)
                {
                    var titles = new List<string>();
                    foreach (var item in history)
                    {
                        var tProp = item.GetType().GetProperty("Title");
                        if (tProp != null) titles.Add((string)tProp.GetValue(item)!);
                    }
                    Assert.Contains(titles, x => x == predA.Title);
                }
            }
        }
        finally { svc.Shutdown(); }
    }

    [Fact]
    public async Task CancelPredictionForEventAsync_NoMatch_ReturnsTrueAndDoesNothing()
    {
        var svc = NewService(_tmpDir);
        try
        {
            var predA = MakePrediction(1, 100, 10, eventId: 555);
            InjectPrediction(svc, predA);

            // Запрашиваем eventId, которого ни у одного прогноза нет.
            var (ok, error) = await svc.CancelPredictionForEventAsync(1, 999, "несуществующий event");
            Assert.True(ok, error);
            Assert.False(predA.IsResolved);
            Assert.Same(predA, svc.GetActive(1, 100));
        }
        finally { svc.Shutdown(); }
    }

    // ───────────────────────────────────────────────────────────────────
    // 4. ResolveAsync(ActivePrediction, …) — резолвит конкретный канал,
    //    не трогая другой.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_ResolvesOnlyMatchedPrediction()
    {
        var pts = new PointsService(Path.Combine(_tmpDir, "points.json"));
        var svc = new PredictionService(NewClient(), pts, "");
        try
        {
            var predA = MakePrediction(1, 100, 10, eventId: 555);
            var predB = MakePrediction(1, 200, 20, eventId: 777);
            InjectPrediction(svc, predA);
            InjectPrediction(svc, predB);

            // Ставки в A.
            pts.Add(1, 100, 1000);
            await svc.PlaceBetAsync(predA, 100, 1, 200L);

            // isAdminOverride=true, чтобы можно было резолвить до окончания приёма ставок.
            var (ok, error) = await svc.ResolveAsync(predA, resolverId: 10, isAdminOverride: true, winningOutcomeId: 1);
            Assert.True(ok, error);
            Assert.True(predA.IsResolved);
            Assert.True(predA.IsLocked);
            Assert.False(predB.IsResolved);

            // predA удалён из _active, predB остался.
            Assert.Null(svc.GetActive(1, 100));
            Assert.Same(predB, svc.GetActive(1, 200));
        }
        finally { svc.Shutdown(); }
    }

    // ───────────────────────────────────────────────────────────────────
    // 5. ChannelKey композитный.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ChannelKey_CompositeFormat_UsesGuildAndChannel()
    {
        var key = InvokeStatic<string>("ChannelKey", new object[] { 42UL, 100UL });
        Assert.Equal("42:100", key);
    }

    [Fact]
    public void ChannelKey_DifferentGuildsSameChannelId_ProduceDifferentKeys()
    {
        var a = InvokeStatic<string>("ChannelKey", new object[] { 1UL, 100UL });
        var b = InvokeStatic<string>("ChannelKey", new object[] { 2UL, 100UL });
        Assert.NotEqual(a, b);
    }

    private static T InvokeStatic<T>(string methodName, object[] args)
    {
        var mi = typeof(PredictionService).GetMethod(methodName,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Не нашёл {methodName}");
        return (T)mi.Invoke(null, args)!;
    }

    private static object MakePersistentPrediction(ulong guildId, ulong channelId, string title)
    {
        var t = typeof(PredictionService).GetNestedType("PersistentPrediction",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PersistentPrediction не найден");
        var instance = Activator.CreateInstance(t)!;
        t.GetProperty("GuildId")!.SetValue(instance, guildId);
        t.GetProperty("ChannelId")!.SetValue(instance, channelId);
        t.GetProperty("Title")!.SetValue(instance, title);
        t.GetProperty("CreatedAtUtc")!.SetValue(instance, DateTimeOffset.UtcNow);
        t.GetProperty("BetsCloseAtUtc")!.SetValue(instance, DateTimeOffset.UtcNow.AddHours(1));
        var betsType = typeof(Dictionary<ulong, RPBot.PredictionBet>);
        var betsDict = Activator.CreateInstance(betsType)!;
        t.GetProperty("Bets")!.SetValue(instance, betsDict);
        return instance;
    }

    // ───────────────────────────────────────────────────────────────────
    // 6. MigrateLegacyFormat — старый формат «один прогноз на гильдию»
    //    мигрирует в новый вложенный.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void MigrateLegacyFormat_OldShape_BecomesNestedByChannel()
    {
        // PersistentPrediction — internal nested в PredictionService, поэтому
        // строим коллекцию через рефлексию.
        var ppType = typeof(PredictionService).GetNestedType("PersistentPrediction",
            BindingFlags.NonPublic)!;

        var legacyDictType = typeof(Dictionary<,>).MakeGenericType(typeof(ulong), ppType);
        var legacy = Activator.CreateInstance(legacyDictType);

        var addMethod = legacyDictType.GetMethod("Add")!;
        addMethod.Invoke(legacy, new object?[] { 1UL, MakePersistentPrediction(1, 100, "legacy A") });
        addMethod.Invoke(legacy, new object?[] { 2UL, MakePersistentPrediction(2, 200, "legacy B") });

        var json = JsonSerializer.Serialize(legacy);

        var migrate = typeof(PredictionService).GetMethod("MigrateLegacyFormat",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = migrate.Invoke(null, new object[] { json })!;

        // Тип результата — Dictionary<ulong, Dictionary<ulong, PersistentPrediction>>.
        var nestedType = result.GetType();
        Assert.Equal(typeof(Dictionary<ulong, Dictionary<ulong, object>>).Name, nestedType.Name);

        // Достаём по индексу [1] и [2] и проверяем, что внутри есть ключи 100 и 200.
        var indexer = nestedType.GetProperty("Item")!;
        var inner1 = indexer.GetValue(result, new object?[] { 1UL })!;
        var inner2 = indexer.GetValue(result, new object?[] { 2UL })!;

        var innerIndexer1 = inner1.GetType().GetProperty("Item")!;
        var innerIndexer2 = inner2.GetType().GetProperty("Item")!;

        var ppA = innerIndexer1.GetValue(inner1, new object?[] { 100UL })!;
        var ppB = innerIndexer2.GetValue(inner2, new object?[] { 200UL })!;

        Assert.NotNull(ppA);
        Assert.NotNull(ppB);
        Assert.Equal("legacy A", ppType.GetProperty("Title")!.GetValue(ppA));
        Assert.Equal("legacy B", ppType.GetProperty("Title")!.GetValue(ppB));
    }

    [Fact]
    public void MigrateLegacyFormat_MalformedJson_ReturnsNull()
    {
        var migrate = typeof(PredictionService).GetMethod("MigrateLegacyFormat",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = migrate.Invoke(null, new object[] { "{not-json" });
        Assert.Null(result);
    }
}
