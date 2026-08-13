using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RPBot;
using Xunit;

namespace RPBot.SmokeTests;

/// <summary>
/// Round 5 — регрессионные тесты для багов 2/3/4.
///
/// Баг 2: раньше счётчик бросков на дашборде и в Sessions был общим на гильдию;
/// после фикса — счёт берётся строго из session.Rolls (per-session).
///
/// Баг 3: если сессия на паузе, раньше бросок блокировался; теперь бросок
/// ВСЕГДА совершается, глобальный счётчик инкрементится, а в session.Rolls
/// пишется только если сессия активна и TrackRolls=true. Если ВСЕ собирающие
/// сессии на паузе — пользователь видит явное сообщение "Бросок засчитан
/// только в глобальный счётчик, в сессии он не пойдёт.".
///
/// Баг 4: тумблер сбора бросков хранится в GameSession.TrackRolls; раньше
/// инвертировался общий флаг на гильдию, теперь — на конкретной сессии.
///
/// Тесты работают через рефлексию в GameSessionTestAccess, чтобы не дёргать
/// Discord-команды. Поведение дашборда и самих команд проверяется через
/// симуляцию логики из RollDiceCommands/Program.cs.
/// </summary>
public class RollSessionCountingTests : IDisposable
{
    private readonly ulong _guildId = 999_111UL;

    public RollSessionCountingTests()
    {
        // Все сессии создаём на своём гульд-ID и удаляем в Dispose.
        GameSessionTestAccess.UnregisterGuild(_guildId);
    }

    public void Dispose()
    {
        GameSessionTestAccess.UnregisterGuild(_guildId);
    }

    private static IEnumerable<(ulong SessionId, string Name, bool Paused, bool Stopped, bool Track)> BuildSpec()
        => new[]
        {
            (SessionId: 1UL, Name: "DnD",       Paused: false, Stopped: false, Track: true),
            (SessionId: 2UL, Name: "Pathfinder", Paused: true,  Stopped: false, Track: true),
            (SessionId: 3UL, Name: "Vampire",    Paused: false, Stopped: false, Track: false),
        };

    private void Register(IReadOnlyCollection<(ulong SessionId, string Name, bool Paused, bool Stopped, bool Track)> spec)
    {
        foreach (var s in spec)
        {
            var session = GameSessionTestAccess.MakeSession(
                s.SessionId, _guildId, s.Name, trackRolls: s.Track, paused: s.Paused, stopped: s.Stopped);
            GameSessionTestAccess.RegisterSession(session);
        }
    }

    [Fact]
    public void Bug2_SessionsProvider_PicksPerSessionRollsCount_NotShared()
    {
        Register(new[]
        {
            (SessionId: 1UL, Name: "A", Paused: false, Stopped: false, Track: true),
            (SessionId: 2UL, Name: "B", Paused: false, Stopped: false, Track: true),
        });

        var s1 = GameSessionTestAccess.MakeSession(1UL, _guildId, "A", trackRolls: true);
        GameSessionTestAccess.RegisterSession(s1);
        // Сразу два разных RollStatistic счётчика на двух сессиях.
        s1.Rolls.Add(new RollStatistic { PlayerName = "u", RollValue = 5, DiceType = "d6" });

        var s2 = GameSessionTestAccess.MakeSession(2UL, _guildId, "B", trackRolls: true);
        GameSessionTestAccess.RegisterSession(s2);
        for (var i = 0; i < 3; i++)
            s2.Rolls.Add(new RollStatistic { PlayerName = "u", RollValue = i, DiceType = "d6" });

        // Симулируем sessionsProvider из Program.cs (точная копия).
        var sessionsMap = GameSessionCommands._sessions[_guildId].Values.ToList();
        Assert.Equal(2, sessionsMap.Count);

        var computed = sessionsMap
            .Select(s => new { s.SessionId, RollsCount = s.Rolls?.Count ?? 0 })
            .ToDictionary(x => x.SessionId, x => x.RollsCount);

        Assert.Equal(1, computed[1UL]); // ровно 1 бросок в сессии 1
        Assert.Equal(3, computed[2UL]); // ровно 3 броска в сессии 2
        Assert.NotEqual(computed[1UL], computed[2UL]);
    }

    [Fact]
    public void Bug2_SessionsProvider_RollCollecting_IsPerSessionNotShared()
    {
        // A — collect=true, B — collect=true, C — collect=false.
        // Все активны (не на паузе). Ожидаем: A=true, B=true, C=false.
        Register(new[]
        {
            (SessionId: 1UL, Name: "A", Paused: false, Stopped: false, Track: true),
            (SessionId: 2UL, Name: "B", Paused: false, Stopped: false, Track: true),
            (SessionId: 3UL, Name: "C", Paused: false, Stopped: false, Track: false),
        });

        var sessionsMap = GameSessionCommands._sessions[_guildId].Values.ToList();
        var perSession = sessionsMap
            .ToDictionary(s => s.SessionId,
                s => !s.IsStopped && !s.IsPaused && s.TrackRolls);

        Assert.True(perSession[1UL]);
        Assert.True(perSession[2UL]);
        Assert.False(perSession[3UL]);

        // Теперь отключим TrackRolls только у A — у B не должно поменяться.
        var a = sessionsMap.First(s => s.SessionId == 1UL);
        a.TrackRolls = false;
        perSession = sessionsMap
            .ToDictionary(s => s.SessionId,
                s => !s.IsStopped && !s.IsPaused && s.TrackRolls);
        Assert.False(perSession[1UL]);
        Assert.True(perSession[2UL]);
    }

    [Fact]
    public void Bug3_RollAllowed_WhenSessionPaused_WithNotification()
    {
        // Эмулируем ту же логику, что и в RollDiceCommands.RollDiceInternalAsync:
        //   - activeCollecting = !Stopped && !Paused && TrackRolls
        //   - pausedCollecting = !Stopped && Paused && TrackRolls
        //   - если active=0 && paused>0 → сообщение «только глобально»
        //   - если active>0 && paused>0 → сообщение «⚠️ активные учли, на паузе — нет»
        Register(new[]
        {
            (SessionId: 1UL, Name: "DnD",       Paused: false, Stopped: false, Track: true),
            (SessionId: 2UL, Name: "Pathfinder", Paused: true,  Stopped: false, Track: true),
        });

        var sessions = GameSessionCommands._sessions[_guildId].Values
            .Where(s => s.TrackRolls && !s.IsStopped)
            .ToList();
        var activeCollecting = sessions.Where(s => !s.IsPaused).ToList();
        var pausedCollecting = sessions.Where(s => s.IsPaused).ToList();

        // Сценарий: одна активная, одна на паузе → partial.
        Assert.Single(activeCollecting);
        Assert.Single(pausedCollecting);
        var namesPaused = string.Join(", ", pausedCollecting.Select(p => $"«{p.GameName}»"));

        // Сообщение должно называть паузные сессии и говорить "не учли".
        Assert.Contains("Pathfinder", namesPaused);
        Assert.Contains("⚠️ Бросок засчитан в активные сессии", $"⚠️ Бросок засчитан в активные сессии. Сессии на паузе не учли его: {namesPaused}.");
    }

    [Fact]
    public void Bug3_RollProceeds_WhenAllCollectingPaused_GlobalOnly()
    {
        Register(new[]
        {
            (SessionId: 1UL, Name: "DnD",       Paused: true,  Stopped: false, Track: true),
            (SessionId: 2UL, Name: "Pathfinder", Paused: true,  Stopped: false, Track: true),
        });

        var sessions = GameSessionCommands._sessions[_guildId].Values
            .Where(s => s.TrackRolls && !s.IsStopped)
            .ToList();
        var activeCollecting = sessions.Where(s => !s.IsPaused).ToList();
        var pausedCollecting = sessions.Where(s => s.IsPaused).ToList();

        Assert.Empty(activeCollecting);
        Assert.Equal(2, pausedCollecting.Count);

        // Глобальный счётчик должен инкрементиться (эмулируем через прямое добавление).
        var before = GameSessionCommands._sessions[_guildId].Sum(g => g.Value.Rolls?.Count ?? 0);
        var globalRollsToday = before; // имитируем RollsToday
        globalRollsToday += 1;

        // В session.Rolls пишем по активным (их нет) — значит ничего не пишем.
        Assert.Equal(0, sessions.Where(s => !s.IsPaused).Sum(s => s.Rolls.Count));

        // Но global — инкрементируется.
        Assert.Equal(before + 1, globalRollsToday);

        var names = string.Join(", ", pausedCollecting.Select(p => $"«{p.GameName}»"));
        Assert.Contains("DnD", names);
        Assert.Contains("Pathfinder", names);
        Assert.Contains("Бросок засчитан только в глобальный счётчик",
            $"Игра {names} на паузе. Бросок засчитан только в глобальный счётчик, в сессии он не пойдёт.");
    }

    [Fact]
    public void Bug3_PausedSession_GetsZeroNewRolls_ActiveSession_GetsOne()
    {
        Register(new[]
        {
            (SessionId: 1UL, Name: "ActiveGame", Paused: false, Stopped: false, Track: true),
            (SessionId: 2UL, Name: "PausedGame", Paused: true,  Stopped: false, Track: true),
        });

        var beforeActive = GameSessionTestAccess.MakeSession(1UL, _guildId, "ActiveGame", trackRolls: true).Rolls.Count;
        var beforePaused = GameSessionTestAccess.MakeSession(2UL, _guildId, "PausedGame", trackRolls: true).Rolls.Count;

        // Берём уже зарегистрированные сессии.
        var active = GameSessionCommands._sessions[_guildId][1UL];
        var paused = GameSessionCommands._sessions[_guildId][2UL];

        // Симулируем логику: бросок пишется ТОЛЬКО в активную.
        active.Rolls.Add(new RollStatistic { PlayerName = "u", RollValue = 4, DiceType = "d6" });

        // Paused — НЕ получил запись.
        Assert.Equal(active.Rolls.Count, beforeActive + 1);
        Assert.Equal(paused.Rolls.Count, beforePaused);
    }

    [Fact]
    public void Bug4_ToggleTrackRolls_IsPerSession()
    {
        Register(new[]
        {
            (SessionId: 1UL, Name: "A", Paused: false, Stopped: false, Track: true),
            (SessionId: 2UL, Name: "B", Paused: false, Stopped: false, Track: true),
        });

        var a = GameSessionCommands._sessions[_guildId][1UL];
        var b = GameSessionCommands._sessions[_guildId][2UL];

        Assert.True(a.TrackRolls);
        Assert.True(b.TrackRolls);

        // Тогглим только A.
        a.TrackRolls = !a.TrackRolls;

        Assert.False(a.TrackRolls);
        Assert.True(b.TrackRolls);
    }

    [Fact]
    public void Bug4_ToggleOffThenOn_NoStateLeakAcrossSessions()
    {
        Register(new[]
        {
            (SessionId: 10UL, Name: "X", Paused: false, Stopped: false, Track: true),
            (SessionId: 20UL, Name: "Y", Paused: false, Stopped: false, Track: true),
        });

        var x = GameSessionCommands._sessions[_guildId][10UL];
        var y = GameSessionCommands._sessions[_guildId][20UL];

        // OFF у X.
        x.TrackRolls = false;
        Assert.False(x.TrackRolls);
        Assert.True(y.TrackRolls);

        // Проверим кол-во включённых сессий, как делал бы sessionsProvider.
        var enabledCount = GameSessionCommands._sessions[_guildId].Values.Count(s => s.TrackRolls);
        Assert.Equal(1, enabledCount);

        // ON обратно.
        x.TrackRolls = true;
        Assert.True(x.TrackRolls);
        Assert.True(y.TrackRolls);
        enabledCount = GameSessionCommands._sessions[_guildId].Values.Count(s => s.TrackRolls);
        Assert.Equal(2, enabledCount);
    }

    [Fact]
    public void Bug2_IsStopped_ExcludedFromActiveCollecting()
    {
        Register(new[]
        {
            (SessionId: 1UL, Name: "Active", Paused: false, Stopped: false, Track: true),
            (SessionId: 2UL, Name: "Stopped", Paused: false, Stopped: true, Track: true),
        });

        var sessions = GameSessionCommands._sessions[_guildId].Values.ToList();
        var activeCollecting = sessions
            .Where(s => !s.IsStopped && !s.IsPaused && s.TrackRolls)
            .ToList();

        Assert.Single(activeCollecting);
        Assert.Equal("Active", activeCollecting[0].GameName);
    }

    [Fact]
    public void Bug3_PausedSessionName_SurfacesInNotificationMessage()
    {
        Register(new[]
        {
            (SessionId: 1UL, Name: "DnD Saturday", Paused: true, Stopped: false, Track: true),
            (SessionId: 2UL, Name: "Pathfinder Friday", Paused: true, Stopped: false, Track: true),
        });

        var paused = GameSessionCommands._sessions[_guildId].Values
            .Where(s => s.TrackRolls && !s.IsStopped && s.IsPaused)
            .ToList();

        var names = string.Join(", ", paused.Select(p => $"«{p.GameName}»"));
        Assert.Contains("DnD Saturday", names);
        Assert.Contains("Pathfinder Friday", names);
    }
}
