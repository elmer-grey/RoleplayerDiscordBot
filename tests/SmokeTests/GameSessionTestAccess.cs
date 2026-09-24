using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RPBot;

namespace RPBot.SmokeTests;

/// <summary>
/// Хелперы для доступа к internal/public API GameSession/RollDiceCommands
/// в юнит-тестах через рефлексию, чтобы не тащить Discord-контекст.
/// </summary>
internal static class GameSessionTestAccess
{
    public static GameSession MakeSession(
            ulong sessionId,
            ulong guildId,
            string name,
            bool trackRolls = true,
            bool paused = false,
            bool stopped = false,
            int seedRolls = 0)
        {
            var s = new GameSession
            {
                SessionId = sessionId,
                GuildId = guildId,
                GameName = name,
                MasterName = "Master",
                MasterId = 1,
                ChannelId = 1,
                StartTime = DateTime.UtcNow.AddMinutes(-30),
                TrackRolls = trackRolls,
            };
            if (stopped)
            {
                // EndTime — public initable через приватный? Выставим через рефлексию:
                typeof(GameSession).GetProperty("EndTime", BindingFlags.Public | BindingFlags.Instance)!
                    .SetValue(s, (DateTime?)DateTime.UtcNow.AddMinutes(-1));
            }

            if (seedRolls > 0)
            {
                for (var i = 0; i < seedRolls; i++)
                {
                    s.Rolls.Add(new RollStatistic
                    {
                        PlayerName = "tester",
                        RollValue = 1 + i,
                        DiceType = "d6",
                    });
                }
            }

            if (paused)
            {
                // Заполняем PausePeriods и IsPaused через рефлексию —
                // это даёт согласованное состояние (как вызвал бы мастер на UI).
                var ppProp = typeof(GameSession).GetProperty(
                    "PausePeriods",
                    BindingFlags.Public | BindingFlags.Instance)!;
                var list = (List<(DateTime Start, DateTime? End)>)(ppProp.GetValue(s)
                    ?? new List<(DateTime, DateTime?)>());
                var newList = list.ToList();
                newList.Add((DateTime.UtcNow.AddMinutes(-10), null));
                ppProp.SetValue(s, newList);

                var pausedProp = typeof(GameSession).GetProperty(
                    "IsPaused",
                    BindingFlags.Public | BindingFlags.Instance)!;
                pausedProp.SetValue(s, true);
            }

            return s;
        }

    public static void RegisterSession(GameSession session)
    {
        var gm = typeof(GameSessionCommands);
        var field = gm.GetField(
            "_sessions",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (field == null) throw new InvalidOperationException("_sessions field not found");
        var sessions = (System.Collections.Concurrent.ConcurrentDictionary<ulong, System.Collections.Concurrent.ConcurrentDictionary<ulong, GameSession>>)field.GetValue(null)!;
        var inner = sessions.GetOrAdd(session.GuildId, _ => new System.Collections.Concurrent.ConcurrentDictionary<ulong, GameSession>());
        inner[session.SessionId] = session;
    }

    public static void UnregisterGuild(ulong guildId)
    {
        var gm = typeof(GameSessionCommands);
        var field = gm.GetField(
            "_sessions",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var raw = field!.GetValue(null) as System.Collections.Concurrent.ConcurrentDictionary<ulong, System.Collections.Concurrent.ConcurrentDictionary<ulong, GameSession>>;
                        if (raw is null)
            {
                return;
            }
                        raw!.TryRemove(guildId, out _);
        }
}
