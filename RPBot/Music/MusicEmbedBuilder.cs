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
            // Флаги навигации: если плейлист загружен, используем индекс; иначе альт отключен
            bool hasPrev = state.PlaylistTrackList is not null
                ? state.PlaylistCurrentIndex > 0
                : false;
            bool hasNext = state.PlaylistTrackList is not null
                ? state.PlaylistCurrentIndex < state.PlaylistTrackList.Count - 1
                : queueCount > 0;
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

        // ─── Embed очереди ────────────────────────────────────────────────

        /// <summary>
        /// Строит embed со списком очереди.
        /// items[0] = текущий трек, items[1..] = очередь.
        /// </summary>
        public static (Embed embed, MessageComponent components) BuildQueueEmbed(
            string? currentTitle,
            TimeSpan? currentDuration,
            IReadOnlyList<(string Title, TimeSpan? Duration)> queue,
            LoopMode loop,
            int page = 0)
        {
            const int PageSize = 15;
            int totalPages = queue.Count == 0 ? 1 : (int)Math.Ceiling(queue.Count / (double)PageSize);
            page = Math.Clamp(page, 0, totalPages - 1);

            var sb = new System.Text.StringBuilder();

            if (currentTitle is not null)
            {
                sb.AppendLine($"▶️ **{currentTitle}** — `{FormatTime(currentDuration ?? TimeSpan.Zero)}`");
                if (queue.Count > 0) sb.AppendLine();
            }

            int start = page * PageSize;
            int end   = Math.Min(start + PageSize, queue.Count);
            for (int i = start; i < end; i++)
            {
                var (title, dur) = queue[i];
                sb.AppendLine($"`{i + 1}.` {title} — `{FormatTime(dur ?? TimeSpan.Zero)}`");
            }

            if (sb.Length == 0) sb.AppendLine("*Очередь пуста*");

            var loopStr = loop switch
            {
                LoopMode.Track => "🔂 Повтор трека",
                LoopMode.Queue => "🔁 Повтор очереди",
                _              => ""
            };

            string footer = totalPages > 1
                ? $"Страница {page + 1}/{totalPages} • {queue.Count} треков" + (loopStr.Length > 0 ? $" • {loopStr}" : "")
                : loopStr;

            var embed = new EmbedBuilder()
                .WithColor(new Color(0x5865F2))
                .WithTitle("📋 Очередь воспроизведения")
                .WithDescription(sb.ToString())
                .WithFooter(footer)
                .Build();

            // Кнопки пагинации — показываем только если больше одной страницы
            var cb = new ComponentBuilder();
            if (totalPages > 1)
            {
                cb.WithButton("◀", "music_queue_prev", ButtonStyle.Secondary, disabled: page == 0);
                cb.WithButton("▶", "music_queue_next", ButtonStyle.Secondary, disabled: page >= totalPages - 1);
            }

            return (embed, cb.Build());
        }

        // ─── Helpers ──────────────────────────────────────────────────────

        public static string FormatTime(TimeSpan t)
        {
            if (t.TotalHours >= 1)
                return t.ToString(@"h\:mm\:ss");
            return t.ToString(@"m\:ss");
        }
    }
}
