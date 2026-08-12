using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using RPBot.Util;

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
				if (!g.Events.TryGetValue(eventId, out var e))
					return null;
				// Снимок снимка: гарантируем non-null словарь, иначе NRE в resync.
				if (e.DmMessageIdsByUserId == null)
					e.DmMessageIdsByUserId = new Dictionary<ulong, ulong>();
				return e;
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
						// На случай если в JSON словарь был null — гарантируем non-null
						// в снимке, иначе resync/обновление упадёт с NRE.
						var dmMap = e.DmMessageIdsByUserId is null
							? new Dictionary<ulong, ulong>()
							: new Dictionary<ulong, ulong>(e.DmMessageIdsByUserId);
						list.Add(new EventAnnouncementEntry
						{
							GuildId = e.GuildId,
							EventId = e.EventId,
							AnnounceChannelId = e.AnnounceChannelId,
							AnnounceMessageId = e.AnnounceMessageId,
							DmMessageIdsByUserId = dmMap,
							TelegramChatId = e.TelegramChatId,
							TelegramMessageThreadId = e.TelegramMessageThreadId,
							TelegramMessageId = e.TelegramMessageId,
							TelegramHasPhoto = e.TelegramHasPhoto,
							LastUpdatedAtUtc = e.LastUpdatedAtUtc,
							LastName = e.LastName,
							LastDescription = e.LastDescription,
							LastStartTimeUtc = e.LastStartTimeUtc,
							LastEndTimeUtc = e.LastEndTimeUtc,
							LastChannelId = e.LastChannelId,
							LastLocation = e.LastLocation,
							LastCoverImageUrl = e.LastCoverImageUrl,
							LastUpdatedMark = e.LastUpdatedMark,
							LastUpdatedAt = e.LastUpdatedAt
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

		/// <summary>
		/// Обновляет существующую запись, не трогая DmMessageIdsByUserId.
		/// Используется для фиксации LastUpdatedMark/LastUpdatedAt в resync-сценарии.
		/// </summary>
		public void UpdateEntry(EventAnnouncementEntry updated)
		{
			lock (_lock)
			{
				if (!_state.Guilds.TryGetValue(updated.GuildId, out var g))
					return;
				if (!g.Events.TryGetValue(updated.EventId, out var _))
					return;
				g.Events[updated.EventId] = updated;
				updated.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
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

		/// <summary>
		/// Возвращает флаг «одноразовая миграция старых анонсов выполнена».
		/// См. <see cref="RPBot.EventOps.EventOpsRemigrationService"/>.
		/// </summary>
		public bool IsRemigratedOnce()
		{
			lock (_lock) { return _state.RemigratedOnce; }
		}

		/// <summary>
		/// Выставляет флаг «одноразовая миграция выполнена» и сохраняет JSON.
		/// </summary>
		public void MarkRemigratedOnce()
		{
			lock (_lock)
			{
				_state.RemigratedOnce = true;
				SaveLocked();
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
						// Advisory inter-process lock: защищает от двух одновременно
						// работающих инстансов RPBot.exe на одном RPBOT_DATA_DIR.
						// При неудаче — fallback на прямую запись (best-effort).
						FileStream? lockHandle = SafeJsonIO.AcquireLock(_path, retries: 5, retryDelayMs: 50);
						try
						{
							SafeJsonIO.WriteAtomic(_path, json);
						}
						finally
						{
							lockHandle?.Dispose();
						}
					}
					catch
					{
						// ignore
					}
				}
	}
}
