using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace RPBot
{
	public sealed class EventAnnouncementStore
	{
		private readonly string _path;
		private readonly object _lock = new();
		private EventAnnouncementState _state = new();

		public EventAnnouncementStore(string path)
		{
			_path = BotConfig.ResolvePath(path);
			Load();
		}

		public EventAnnouncementEntry? TryGet(ulong guildId, ulong eventId)
		{
			lock (_lock)
			{
				if (!_state.Guilds.TryGetValue(guildId, out var g))
					return null;
				return g.Events.TryGetValue(eventId, out var e) ? e : null;
			}
		}

		public IReadOnlyList<EventAnnouncementEntry> GetEntriesSnapshot()
		{
			lock (_lock)
			{
				var list = new List<EventAnnouncementEntry>();
				foreach (var guildPair in _state.Guilds)
				{
					foreach (var eventPair in guildPair.Value.Events)
					{
						var e = eventPair.Value;
						list.Add(new EventAnnouncementEntry
						{
							GuildId = e.GuildId,
							EventId = e.EventId,
							AnnounceChannelId = e.AnnounceChannelId,
							AnnounceMessageId = e.AnnounceMessageId,
							DmMessageIdsByUserId = new Dictionary<ulong, ulong>(e.DmMessageIdsByUserId),
							TelegramChatId = e.TelegramChatId,
							TelegramMessageThreadId = e.TelegramMessageThreadId,
							TelegramMessageId = e.TelegramMessageId,
							TelegramHasPhoto = e.TelegramHasPhoto,
							LastUpdatedAtUtc = e.LastUpdatedAtUtc
						});
					}
				}

				return list;
			}
		}

		public void Upsert(EventAnnouncementEntry entry)
		{
			lock (_lock)
			{
				if (!_state.Guilds.TryGetValue(entry.GuildId, out var g))
				{
					g = new GuildEventAnnouncements();
					_state.Guilds[entry.GuildId] = g;
				}
				g.Events[entry.EventId] = entry;
				entry.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
				SaveLocked();
			}
		}

		public void Remove(ulong guildId, ulong eventId)
		{
			lock (_lock)
			{
				if (_state.Guilds.TryGetValue(guildId, out var g))
				{
					g.Events.Remove(eventId);
					SaveLocked();
				}
			}
		}

		private void Load()
		{
			try
			{
				lock (_lock)
				{
					if (!File.Exists(_path))
                      return;

					var json = File.ReadAllText(_path);
					_state = JsonSerializer.Deserialize<EventAnnouncementState>(json) ?? new EventAnnouncementState();
				}
			}
			catch
			{
				lock (_lock)
				{
					_state = new EventAnnouncementState();
				}
           }
		}

		public bool EnsureFileExists()
		{
			lock (_lock)
			{
				if (File.Exists(_path)) return false;
				_state = new EventAnnouncementState();
				SaveLocked();
				return true;
			}
		}

		private void SaveLocked()
		{
			try
			{
				var dir = Path.GetDirectoryName(_path) ?? AppContext.BaseDirectory;
				Directory.CreateDirectory(dir);

				var opts = new JsonSerializerOptions
				{
					WriteIndented = true,
					Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
				};
				var json = JsonSerializer.Serialize(_state, opts);
				File.WriteAllText(_path, json);
			}
			catch
			{
				// ignore
			}
		}
	}
}
