using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Threading;

namespace RPBot.Music
{
    /// <summary>Режим повтора.</summary>
    public enum LoopMode { None, Track, Queue }

    /// <summary>Единая запись мастер-очереди (история + текущий + будущее).</summary>
    public sealed class MasterTrackEntry
    {
        public int       Number     { get; set; }   // 1-based; переприсваивается при удалении
        public string    Title      { get; init; } = "";
        public string?   Author     { get; init; }
        public string    Url        { get; init; } = "";
        public string?   ArtworkUrl { get; init; }
        public TimeSpan  Duration   { get; init; }
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

        // ─── Мастер-очередь ───────────────────────────────────────────────
        /// <summary>Единый список всех треков: история + текущий + будущее.</summary>
        public List<MasterTrackEntry> MasterQueue { get; } = new();
        /// <summary>Индекс текущего трека в MasterQueue (-1 = не задан).</summary>
        public int MasterCurrentIndex { get; set; } = -1;
        /// <summary>Семафор для потокобезопасного доступа к MasterQueue.</summary>
        public SemaphoreSlim MasterQueueLock { get; } = new(1, 1);

        /// <summary>Переприсваивает номера (Number) всем записям после удаления.</summary>
        public void RenumberMasterQueue()
        {
            for (int i = 0; i < MasterQueue.Count; i++)
                MasterQueue[i].Number = i + 1;
        }

        // ─── Auto-pause / Auto-stop (#4) ──────────────────────────────────
        public DateTime? ChannelEmptySince    { get; set; }
        public Timer?    AutoPauseTimer       { get; set; }
        public Timer?    AutoStopTimer        { get; set; }
        public ulong?    AutoPausePromptId    { get; set; }
        public ulong?    AutoPauseNoticeId    { get; set; }
        public ulong?    AutoStopNoticeId     { get; set; }
        public int       LastLoggedEmptyMinute { get; set; } = -1;

        // ─── Статистика (#13) ─────────────────────────────────────────────
        public int TracksPlayedSession { get; set; }

        // ─── Сохранение очереди (#11) ─────────────────────────────────────
        public List<string> PersistedQueueUrls { get; set; } = new();

        // ─── Пагинация списка очереди ─────────────────────────────────────
        /// <summary>Текущая страница (0 = окрестность текущего трека; может быть отрицательной).</summary>
        public int QueuePage { get; set; } = 0;
    }
}
