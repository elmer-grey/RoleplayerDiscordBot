using System;
using System.Collections.Generic;

namespace RPBot
{
	public sealed class EventAnnouncementState
	{
		public Dictionary<ulong, GuildEventAnnouncements> Guilds { get; set; } = new();
	}

	public sealed class GuildEventAnnouncements
	{
		public Dictionary<ulong, EventAnnouncementEntry> Events { get; set; } = new();
	}

	public sealed class EventAnnouncementEntry
	{
		public ulong GuildId { get; set; }
		public ulong EventId { get; set; }

		public ulong AnnounceChannelId { get; set; }
		public ulong AnnounceMessageId { get; set; }

		public Dictionary<ulong, ulong> DmMessageIdsByUserId { get; set; } = new();

		public long TelegramChatId { get; set; }
		public int TelegramMessageThreadId { get; set; }
		public int TelegramMessageId { get; set; }
		public bool TelegramHasPhoto { get; set; }

		public DateTimeOffset LastUpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
	}
}
