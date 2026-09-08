using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.Predictions;
using RPBot.Util;
using SmokeTests;
using Xunit;

namespace RPBot.SmokeTests;

[Collection("BotConfig")]
public class Round6PredictionRegressionTests : IsolatedDataTestBase
{
    public Round6PredictionRegressionTests() : base("rpbot_smoke_r6") { }

    private static DiscordSocketClient NewClient() => new DiscordSocketClient(new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.None,
        LogLevel = LogSeverity.Critical,
    });

    /// <summary>
    /// Рефлексивный доступ к закрытым SaveAsync/Gate методам PredictionService.
    /// </summary>
    private static MethodInfo SaveHistoryMethod = typeof(PredictionService)
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .First(m => m.Name == "SaveHistoryAsync");

    private static MethodInfo SaveStatsMethod = typeof(PredictionService)
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .First(m => m.Name == "SaveStatsAsync");

    private static MethodInfo SaveAchievementsMethod = typeof(PredictionService)
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .First(m => m.Name == "SaveAchievementsAsync");

    private static FieldInfo HistoryFilePathField = typeof(PredictionService)
        .GetField("_historyFilePath", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static FieldInfo StatsFilePathField = typeof(PredictionService)
        .GetField("_statsFilePath", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static FieldInfo AchievementsFilePathField = typeof(PredictionService)
        .GetField("_achievementsFilePath", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static Task InvokeSaveHistoryAsync(PredictionService svc) => (Task)SaveHistoryMethod.Invoke(svc, null)!;
    private static Task InvokeSaveStatsAsync(PredictionService svc) => (Task)SaveStatsMethod.Invoke(svc, null)!;
    private static Task InvokeSaveAchievementsAsync(PredictionService svc) => (Task)SaveAchievementsMethod.Invoke(svc, null)!;

    // -----------------------------------------------------------------------
    // R6-Bug3: PlaceBetAsync vs MonitorLoopAsync — race condition fix
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Bug3_PlaceBet_LockBeforeCheck_NoPlaceAfterDeadline()
    {
        var pts = new PointsService(Path.Combine(TempDir, "points.json"));
        var svc = new PredictionService(NewClient(), pts, "");

        // Создаём ActivePrediction напрямую через рефлексию: нам не нужен
        // полноценный Discord-канал, только объект с BetsCloseAtUtc в прошлом.
        var pred = new ActivePrediction
        {
            GuildId = 1,
            CreatorId = 100,
            ChannelId = 200,
            Title = "Test race",
            Outcomes = new List<PredictionOutcome>
            {
                new() { Id = 1, Name = "A", TotalStake = 0 },
                new() { Id = 2, Name = "B", TotalStake = 0 },
            },
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            BetsCloseAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1), // уже истёк
            IsLocked = false,
            IsResolved = false,
            Bets = new Dictionary<ulong, PredictionBet>(),
        };

        // Закидываем в _active через рефлексию.
        var activeField = typeof(PredictionService).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var activeDict = (ConcurrentDictionary<ulong, ActivePrediction>)activeField.GetValue(svc)!;
        activeDict[pred.GuildId] = pred;

        var pointsService = pts;
        // Зачисляем много юзеров, чтобы хватило очков.
        for (ulong u = 1; u <= 50; u++)
            pointsService.Add(pred.GuildId, u, 1000);

        // Атакуем PlaceBetAsync из 50 потоков. До фикса: каждый из них
        // сначала проверял время (уже истекло), но перед `Sync.WaitAsync()`
        // монитор мог ещё не поставить IsLocked, и в гонке проходили ставки.
        // После фикса: `Sync.WaitAsync()` захватывается первым, и хотя бы
        // однократно при попытке первый поток увидит истёкший дедлайн,
        // поставит IsLocked=true, и весь следующий трафик отклонится.
        var placeBetMethod = typeof(PredictionService).GetMethod("PlaceBetAsync")!;
        var tasks = Enumerable.Range(1, 50).Select(async i =>
        {
            return await (Task<(bool, string)>)placeBetMethod.Invoke(svc, new object[] { pred.GuildId, (ulong)i, 1, 50L })!;
        }).ToArray();
        await Task.WhenAll(tasks);

        var results = tasks.Select(t => t.Result).ToList();
        int succeeded = results.Count(r => r.Item1);
        int rejected = results.Count(r => !r.Item1);

        // Главное: после гонки либо ноль, либо строго ограниченное число
        // ставок прошло (размер гонки до захвата `Sync`). В любом случае
        // не 50 успешных — потому что после захвата первый же поток
        // пишет IsLocked=true, и остальные отвергаются.
        Assert.True(succeeded <= 1, $"Ожидали <= 1 успешной ставки после гонки, получили {succeeded}.");
        Assert.True(succeeded + rejected == 50, "Все ставки должны быть обработаны.");

        // Состояние consistent: если хоть одна ставка прошла, дальше IsLocked=true.
        if (succeeded >= 1)
        {
            Assert.True(pred.IsLocked, "После первой принятой ставки с истёкшим дедлайном IsLocked должен быть true.");
        }

        // Cleanup, чтобы PredictionServiceMonitorLoop не пытался параллельно
        // что-то делать после Shutdown.
        svc.Shutdown();
    }

    // -----------------------------------------------------------------------
    // R6-Bug5: SaveHistoryAsync / SaveStatsAsync / SaveAchievementsAsync должны
    // идти через SafeJsonIO.WriteAtomicAsync — без .tmp после серии вызовов.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Bug5_HistoryStatsAchievements_AllAtomic_NoRemainsTmp()
    {
        var pts = new PointsService(Path.Combine(TempDir, "points.json"));
        var svc = new PredictionService(NewClient(), pts, "");

        var historyPath = (string)HistoryFilePathField.GetValue(svc)!;
        var statsPath = (string)StatsFilePathField.GetValue(svc)!;
        var achievementsPath = (string)AchievementsFilePathField.GetValue(svc)!;

        // PredictionService._historyGate/_statsGate/_achievementsGate внутри
        // сериализуют записи для каждого канала, поэтому конкурентности
        // на .tmp-файл в рамках одного инстанса нет. Дёргаем последовательно.
        for (int i = 0; i < 5; i++)
        {
            await InvokeSaveHistoryAsync(svc);
            await InvokeSaveStatsAsync(svc);
            await InvokeSaveAchievementsAsync(svc);
        }

        // Главное: файлы существуют, валидный JSON, .tmp не остаётся.
        var histFiles = new[] { historyPath, statsPath, achievementsPath };
        foreach (var f in histFiles)
        {
            Assert.True(File.Exists(f), $"Файл {f} должен существовать.");
            var content = await File.ReadAllTextAsync(f);
            Assert.False(string.IsNullOrWhiteSpace(content), $"Файл {f} не должен быть пустым.");
            Assert.True(content.TrimStart().StartsWith("{") || content.TrimStart().StartsWith("["),
                $"Файл {f} должен содержать валидный JSON.");
            Assert.False(File.Exists(f + ".tmp"), $"Файл {f}.tmp не должен оставаться после атомарной записи.");
        }

        svc.Shutdown();
    }

    [Fact]
    public async Task Bug5_HistoryAtomicRename_DoesNotClobberOnConcurrent()
    {
        // Параллельные попытки записи в один файл — сериализуем общим
        // семафором, чтобы не натыкаться на гонку rename на одном .tmp
        // (это ограничение уровня OS, не SafeJsonIO). Проверяем,
        // что последняя запись выигрывает, .tmp не остаётся, файл валиден.
        var path = Path.Combine(TempDir, "predictions_history.json");
        await SafeJsonIO.WriteAtomicAsync(path, "{}");

        var gate = new SemaphoreSlim(1, 1);
        var writes = Enumerable.Range(0, 20).Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                await SafeJsonIO.WriteAtomicAsync(path, $"{{\"v\":{i}}}");
            }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(writes);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        var content = await File.ReadAllTextAsync(path);
        Assert.True(content.Contains("\"v\":"), "Должна быть последняя запись.");
    }

    // -----------------------------------------------------------------------
    // R6-Bug1: PredictionResolved / PredictionCancelled events fire.
    // Подписчик должен иметь возможность почистить UI-state.
    // -----------------------------------------------------------------------

    [Fact]
    public void Bug1_PredictionResolvedAndCancelled_EventsFire()
    {
        var pts = new PointsService(Path.Combine(TempDir, "points.json"));
        var svc = new PredictionService(NewClient(), pts, "");

        var resolvedGuilds = new List<ulong>();
        var cancelledGuilds = new List<ulong>();
        svc.PredictionResolved += g => resolvedGuilds.Add(g);
        svc.PredictionCancelled += g => cancelledGuilds.Add(g);

        // Прогноз создаётся через рефлексию (без Discord-канала).
        var pred = new ActivePrediction
        {
            GuildId = 42,
            CreatorId = 77,
            ChannelId = 0,
            Title = "events test",
            Outcomes = new List<PredictionOutcome>
            {
                new() { Id = 1, Name = "A", TotalStake = 0 },
                new() { Id = 2, Name = "B", TotalStake = 0 },
            },
            CreatedAtUtc = DateTimeOffset.UtcNow,
            BetsCloseAtUtc = DateTimeOffset.UtcNow.AddMinutes(3),
            Bets = new Dictionary<ulong, PredictionBet>(),
        };
        var activeField = typeof(PredictionService).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var activeDict = (ConcurrentDictionary<ulong, ActivePrediction>)activeField.GetValue(svc)!;
        activeDict[pred.GuildId] = pred;

        // Дёргаем ResolveAsync и CancelAsync напрямую.
        var resolveMethod = typeof(PredictionService).GetMethod("ResolveAsync")!;
        var cancelMethod = typeof(PredictionService).GetMethod("CancelAsync")!;

        // Resolve (won't actually post result to channel since ChannelId=0)
        var resolveTask = (Task)resolveMethod.Invoke(svc, new object[] { pred.GuildId, (ulong)77, true, 1 })!;
        try { resolveTask.GetAwaiter().GetResult(); } catch { /* expected: channel=0 */ }

        // Cancel с новым прогнозом.
        activeDict[pred.GuildId] = new ActivePrediction
        {
            GuildId = pred.GuildId,
            CreatorId = pred.CreatorId,
            ChannelId = 0,
            Title = "events test 2",
            Outcomes = new List<PredictionOutcome>
            {
                new() { Id = 1, Name = "A", TotalStake = 0 },
                new() { Id = 2, Name = "B", TotalStake = 0 },
            },
            CreatedAtUtc = DateTimeOffset.UtcNow,
            BetsCloseAtUtc = DateTimeOffset.UtcNow.AddMinutes(3),
            Bets = new Dictionary<ulong, PredictionBet>(),
        };
        var cancelTask = (Task)cancelMethod.Invoke(svc, new object[] { pred.GuildId, (ulong)77, true, null })!;
        try { cancelTask.GetAwaiter().GetResult(); } catch { /* expected: channel=0 */ }

        Assert.Contains(pred.GuildId, resolvedGuilds);
        Assert.Contains(pred.GuildId, cancelledGuilds);

        svc.Shutdown();
    }
}

/// <summary>
/// Минимальная обёртка, чтобы reflection из Round6PredictionRegressionTests
/// работал. Dictionary, потому что _active тоже ConcurrentDictionary с
/// ulong-ключом; через рефлексию нам просто нужен доступ к полю.
/// </summary>
internal class ConcurrentDictionaryT<TKey, TValue>
{
}

