using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Threading;

namespace RPBot.Music
{
    /// <summary>Режим повтора.</summary>
    public enum LoopMode { None, Track, Queue }

    /// <summary>Запись в истории воспроизведения.</summary>
    public sealed class TrackHistoryEntry
    {
        public string  Title      { get; init; } = "";
        public string? Author     { get; init; }
        public string  Url        { get; init; } = "";
        public string? ArtworkUrl { get; init; }
        public TimeSpan Duration  { get; init; }
    }

    /// <summary>
    /// Хранит UI-состояние музыкального плеера для одной гильдии.
    /// </summary>
    public class MusicPlayerState
    {
        // ─── Сообщения ────────────────────────────────────────────────────
        public ITextChannel? NowPlayingChannel  { get; set; }
        public ulong?        NowPlayingMessageId { get; set; }
        public ulong?        QueueMessageId      { get; set; }

        // ─── Режим повтора ────────────────────────────────────────────────
        public LoopMode LoopMode { get; set; } = LoopMode.None;

        // ─── Текущий трек ─────────────────────────────────────────────────
        public DateTime  TrackStartedAtUtc    { get; set; } = DateTime.UtcNow;
        public TimeSpan? CurrentTrackDuration { get; set; }
        public string?   CurrentTrackTitle    { get; set; }
        public string?   CurrentTrackArtworkUrl { get; set; }
        public string?   CurrentTrackUrl      { get; set; }
        public string?   CurrentTrackAuthor   { get; set; }
        public bool      IsPaused             { get; set; }

        // ─── История треков (#1) ──────────────────────────────────────────
        /// <summary>Последние 20 треков (0 = самый новый).</summary>
        public LinkedList<TrackHistoryEntry> TrackHistory { get; } = new();
        public const int MaxHistory = 20;

        // ─── Auto-pause / Auto-stop (#4) ──────────────────────────────────
        /// <summary>Когда канал опустел (null = не пуст).</summary>
        public DateTime? ChannelEmptySince    { get; set; }
        /// <summary>Таймер авто-паузы (5 мин).</summary>
        public Timer?    AutoPauseTimer       { get; set; }
        /// <summary>Таймер авто-стопа (10 мин).</summary>
        public Timer?    AutoStopTimer        { get; set; }
        /// <summary>ID сообщения "продолжить воспроизведение?" (null = нет).</summary>
        public ulong?    AutoPausePromptId    { get; set; }
        /// <summary>Последний залогированный порог пустого канала (в целых минутах), чтобы не дублировать лог.</summary>
        public int       LastLoggedEmptyMinute { get; set; } = -1;

        // ─── Статистика (#13) ─────────────────────────────────────────────
        /// <summary>Треков сыграно за текущую сессию бота.</summary>
        public int TracksPlayedSession { get; set; }

        // ─── Сохранение очереди (#11) ─────────────────────────────────────
        /// <summary>URL-ы очереди для персистентности (заполняются при сохранении).</summary>
        public List<string> PersistedQueueUrls { get; set; } = new();

        // ─── Пагинация списка очереди ─────────────────────────────────────
        /// <summary>Текущая страница списка очереди (0-based).</summary>
        public int QueuePage { get; set; } = 0;
    }
}
