using Discord;

namespace RPBot.Music
{
    /// <summary>
    /// Строит Discord Embed и компоненты кнопок для Now-Playing сообщения.
    /// </summary>
    public static class MusicEmbedBuilder
    {
        // Кнопочные custom_id — используются при обработке ButtonExecuted
        public const string BtnPrev      = "music_prev";
        public const string BtnPausePlay = "music_pauseplay";
        public const string BtnSkip      = "music_skip";
        public const string BtnStop      = "music_stop";
        public const string BtnLoop      = "music_loop";
        public const string BtnShuffle   = "music_shuffle";
        public const string BtnVolDown   = "music_vol_down";
        public const string BtnVolUp     = "music_vol_up";
        public const string BtnQueue     = "music_queue";

        private const int ProgressBarLength = 20;
        private const string ProgressFilled  = "▓";
        private const string ProgressEmpty   = "░";

        // Цвета embed
        private static readonly Color ColorPlaying = new Color(0x1DB954); // зелёный (Spotify-style)
        private static readonly Color ColorPaused  = new Color(0xFFA500); // оранжевый
        private static readonly Color ColorStopped = new Color(0xFF4444); // красный

        /// <summary>Строит embed + кнопки для текущего состояния плеера.</summary>
        public static (Embed embed, MessageComponent components) Build(
            MusicPlayerState state,
            bool isPaused,
            int volume = 100,
            int queueCount = 0,
            int sessionCount = 0,
            long allTimeCount = 0)
        {
            var embed = BuildEmbed(state, isPaused, queueCount, volume, sessionCount, allTimeCount);
            int cur  = state.MasterCurrentIndex;
            int total = state.MasterQueue.Count;
            bool hasPrev = cur > 0;
            bool hasNext = cur >= 0 && cur < total - 1;
            var components = BuildComponents(state.LoopMode, isPaused, volume, queueCount, hasPrev, hasNext);
            return (embed, components);
        }

        // ─── Embed ────────────────────────────────────────────────────────

        private static Embed BuildEmbed(MusicPlayerState state, bool isPaused, int queueCount, int volume, int sessionCount = 0, long allTimeCount = 0)
        {
            var elapsed = DateTime.UtcNow - state.TrackStartedAtUtc;
            var duration = state.CurrentTrackDuration ?? TimeSpan.Zero;

            // Не даём прогрессу уйти за 100%
            if (elapsed > duration && duration > TimeSpan.Zero)
                elapsed = duration;

            var progressBar = BuildProgressBar(elapsed, duration);
            var elapsedStr  = FormatTime(elapsed);
            var durationStr = duration > TimeSpan.Zero ? FormatTime(duration) : "∞";

            var color = isPaused ? ColorPaused : ColorPlaying;
            var statusIcon = isPaused ? "⏸" : "▶️";

            var builder = new EmbedBuilder()
                .WithColor(color)
                .WithTitle($"{statusIcon} {state.CurrentTrackTitle ?? "Неизвестный трек"}")
                .WithDescription($"{progressBar}\n`{elapsedStr}` / `{durationStr}`")
                .WithFooter(footer =>
                {
                    footer.Text = BuildFooter(state.LoopMode, queueCount, volume, sessionCount, allTimeCount);
                });

            if (!string.IsNullOrWhiteSpace(state.CurrentTrackAuthor))
                builder.WithAuthor(state.CurrentTrackAuthor, iconUrl: null, url: null);

            if (!string.IsNullOrWhiteSpace(state.CurrentTrackArtworkUrl))
                builder.WithThumbnailUrl(state.CurrentTrackArtworkUrl);

            if (!string.IsNullOrWhiteSpace(state.CurrentTrackUrl))
                builder.WithUrl(state.CurrentTrackUrl);

            return builder.Build();
        }

        private static string BuildProgressBar(TimeSpan elapsed, TimeSpan duration)
        {
            if (duration == TimeSpan.Zero) return new string('░', ProgressBarLength);

            var ratio = elapsed.TotalSeconds / duration.TotalSeconds;
            var filled = (int)Math.Round(ratio * ProgressBarLength);
            filled = Math.Clamp(filled, 0, ProgressBarLength);

            return new string('▓', filled) + new string('░', ProgressBarLength - filled);
        }

        private static string BuildFooter(LoopMode loop, int queueCount, int volume, int sessionCount = 0, long allTimeCount = 0)
        {
            var loopStr = loop switch
            {
                LoopMode.Track => "🔂 Повтор трека",
                LoopMode.Queue => "🔁 Повтор очереди",
                _              => "▶ Без повтора",
            };
            var queueStr = queueCount > 0 ? $" • В очереди: {queueCount}" : "";
            var statsStr = sessionCount > 0 ? $" • Сыграно: {sessionCount} (всего: {allTimeCount})" : "";
            return $"{loopStr}{queueStr} • 🔊 {volume}%{statsStr}";
        }

        // ─── Кнопки ───────────────────────────────────────────────────────

        private static MessageComponent BuildComponents(
            LoopMode loop, bool isPaused, int volume, int queueCount,
            bool hasPrev = false, bool hasNext = true)
        {
            var pauseLabel = isPaused ? "▶️" : "⏸";
            var pauseStyle = isPaused ? ButtonStyle.Success : ButtonStyle.Secondary;

            var loopLabel = loop switch
            {
                LoopMode.Track => "🔂",
                LoopMode.Queue => "🔁",
                _              => "➡️",
            };
            var loopStyle = loop != LoopMode.None ? ButtonStyle.Primary : ButtonStyle.Secondary;

            return new ComponentBuilder()
                // Ряд 1: управление воспроизведением
                .WithButton("⏮", BtnPrev,      ButtonStyle.Secondary, row: 0, disabled: !hasPrev)
                .WithButton(pauseLabel, BtnPausePlay, pauseStyle,      row: 0)
                .WithButton("⏭", BtnSkip,      ButtonStyle.Secondary, row: 0, disabled: !hasNext)
                .WithButton("⏹", BtnStop,      ButtonStyle.Danger,    row: 0)
                // Ряд 2: дополнительно
                .WithButton(loopLabel, BtnLoop,    loopStyle,           row: 1)
                .WithButton("🔀",     BtnShuffle, ButtonStyle.Secondary, row: 1)
                .WithButton("🔉", BtnVolDown, ButtonStyle.Secondary, row: 1)
                .WithButton("🔊",     BtnVolUp,  ButtonStyle.Secondary, row: 1)
                .WithButton("📋",     BtnQueue,  ButtonStyle.Secondary, row: 1)
                .Build();
        }

        // ─── Embed очереди (MasterQueue) ─────────────────────────────────

        private const int HistoryOnPage0 = 3;   // треков истории на стр.0
        private const int PageSize       = 20;  // всего записей на странице
        private const int FutureOnPage0  = PageSize - HistoryOnPage0 - 1; // 16
        private const int MaxTitleLength = 60;

        private static string Truncate(string s)
            => s.Length > MaxTitleLength ? s[..MaxTitleLength] + "…" : s;

        /// <summary>Возвращает (minPage, maxPage) для текущего состояния MasterQueue.</summary>
        public static (int min, int max) GetMasterQueuePageRange(IReadOnlyList<MasterTrackEntry> master, int currentIndex)
        {
            if (master.Count == 0) return (0, 0);
            int historyCount = currentIndex < 0 ? 0 : currentIndex;           // треков до текущего
            int futureCount  = currentIndex < 0 ? master.Count
                             : master.Count - currentIndex - 1;               // треков после текущего

            // Страница 0 вмещает min(3, historyCount) + 1 + min(16, futureCount)
            int histExtra = Math.Max(0, historyCount - HistoryOnPage0);       // история сверх стр.0
            int futExtra  = Math.Max(0, futureCount  - FutureOnPage0);        // будущее сверх стр.0

            int minPage = histExtra == 0 ? 0 : -(int)Math.Ceiling(histExtra / (double)PageSize);
            int maxPage = futExtra  == 0 ? 0 :  (int)Math.Ceiling(futExtra  / (double)PageSize);
            return (minPage, maxPage);
        }

        /// <summary>Строит embed очереди из MasterQueue с учётом страницы (может быть отрицательной).</summary>
        public static (Embed embed, MessageComponent components) BuildQueueEmbedFromMaster(
            IReadOnlyList<MasterTrackEntry> master,
            int currentIndex,
            LoopMode loop,
            int page = 0)
        {
            var sb = new System.Text.StringBuilder();

            if (master.Count == 0 || currentIndex < 0)
            {
                sb.AppendLine("*Очередь пуста*");
            }
            else
            {
                // Вычисляем срез для данной страницы
                // Страница 0: [currentIndex-3 .. currentIndex .. currentIndex+16]
                // Страница -1: 20 треков истории до стр.0
                // Страница +1: 20 треков будущего после стр.0

                int page0HistStart = Math.Max(0, currentIndex - HistoryOnPage0);

                int sliceStart, sliceEnd;
                if (page == 0)
                {
                    sliceStart = page0HistStart;
                    sliceEnd   = Math.Min(master.Count, page0HistStart + PageSize);
                }
                else if (page < 0)
                {
                    // уходим глубже в историю
                    int offset = (-page - 1) * PageSize;
                    sliceEnd   = page0HistStart - offset;
                    sliceStart = Math.Max(0, sliceEnd - PageSize);
                }
                else
                {
                    // уходим дальше в будущее
                    int page0End = Math.Min(master.Count, page0HistStart + PageSize);
                    int offset   = (page - 1) * PageSize;
                    sliceStart   = page0End + offset;
                    sliceEnd     = Math.Min(master.Count, sliceStart + PageSize);
                }

                sliceStart = Math.Clamp(sliceStart, 0, master.Count);
                sliceEnd   = Math.Clamp(sliceEnd,   0, master.Count);

                for (int i = sliceStart; i < sliceEnd; i++)
                {
                    var e = master[i];
                    var title = Truncate(e.Title);
                    var dur   = FormatTime(e.Duration);
                    if (i == currentIndex)
                        sb.AppendLine($"▶️ **`#{e.Number}`** **{title}** — `{dur}`");
                    else if (i < currentIndex)
                        sb.AppendLine($"— `#{e.Number}` {title} — `{dur}`");
                    else
                        sb.AppendLine($"`#{e.Number}.` {title} — `{dur}`");
                }

                if (sb.Length == 0) sb.AppendLine("*Нет треков на этой странице*");
            }

            var (minPage, maxPage) = GetMasterQueuePageRange(master, currentIndex);
            var loopStr = loop switch
            {
                LoopMode.Track => "🔂 Повтор трека",
                LoopMode.Queue => "🔁 Повтор очереди",
                _              => ""
            };
            string footer = $"Стр. {page} ({minPage}…{maxPage}) • {master.Count} треков"
                + (loopStr.Length > 0 ? $" • {loopStr}" : "");

            var embed = new EmbedBuilder()
                .WithColor(new Color(0x5865F2))
                .WithTitle("📋 Очередь воспроизведения")
                .WithDescription(sb.ToString())
                .WithFooter(footer)
                .Build();

            var cb = new ComponentBuilder();
            cb.WithButton("◀", "music_queue_prev", ButtonStyle.Secondary, disabled: page <= minPage, row: 0);
            cb.WithButton("🔢", "music_queue_goto", ButtonStyle.Secondary, row: 0);
            cb.WithButton("▶", "music_queue_next", ButtonStyle.Secondary, disabled: page >= maxPage, row: 0);

            return (embed, cb.Build());
        }

        // ─── Устаревший BuildQueueEmbed (оставлен для совместимости) ──────
        /// <summary>Устаревший метод. Используй BuildQueueEmbedFromMaster.</summary>
        public static (Embed embed, MessageComponent components) BuildQueueEmbed(
            string? currentTitle,
            TimeSpan? currentDuration,
            IReadOnlyList<(string Title, TimeSpan? Duration)> queue,
            LoopMode loop,
            int page = 0,
            IReadOnlyList<(string Title, TimeSpan? Duration)>? history = null)
            => BuildQueueEmbedFromMaster(System.Array.Empty<MasterTrackEntry>(), -1, loop, 0);

        // ─── Helpers ──────────────────────────────────────────────────────

        public static string FormatTime(TimeSpan t)
        {
            if (t.TotalHours >= 1)
                return t.ToString(@"h\:mm\:ss");
            return t.ToString(@"m\:ss");
        }
    }
}
