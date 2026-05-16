using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Protocol.Payloads.Events;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot.Music
{
    /// <summary>
    /// Расширение QueuedLavalinkPlayer — уведомляет о старте и завершении трека.
    /// </summary>
    public sealed class NotifyingPlayer : QueuedLavalinkPlayer
    {
        public static Func<ulong, string, string?, TimeSpan, string?, string?, Task>? OnTrackStartedGlobal;
        public static Func<ulong, Task>? OnTrackEndedGlobal;

        public NotifyingPlayer(IPlayerProperties<NotifyingPlayer, QueuedLavalinkPlayerOptions> properties)
            : base(properties) { }

        protected override async ValueTask NotifyTrackStartedAsync(ITrackQueueItem queueItem, CancellationToken cancellationToken = default)
        {
            await base.NotifyTrackStartedAsync(queueItem, cancellationToken);
            var track = queueItem.Track;
            if (track is not null && OnTrackStartedGlobal is not null)
            {
                try
                {
                    await OnTrackStartedGlobal(
                        GuildId,
                        track.Title,
                        track.Author,
                        track.Duration,
                        track.ArtworkUri?.ToString(),
                        track.Uri?.ToString());
                }
                catch { }
            }
        }

        protected override async ValueTask NotifyTrackEndedAsync(ITrackQueueItem queueItem, TrackEndReason endReason, CancellationToken cancellationToken = default)
        {
            await base.NotifyTrackEndedAsync(queueItem, endReason, cancellationToken);
            if (OnTrackEndedGlobal is not null && endReason == TrackEndReason.Finished)
            {
                try { await OnTrackEndedGlobal(GuildId); } catch { }
            }
        }
    }
}
