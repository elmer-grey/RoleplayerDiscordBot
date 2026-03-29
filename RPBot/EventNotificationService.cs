using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RPBot
{
	public sealed class EventNotificationService
	{
		private readonly string _statePath;
		private readonly object _lock = new();
		private readonly Dictionary<ulong, HashSet<ulong>> _subscriptionsByGuild = new();
		private readonly HashSet<ulong> _pausedUsers = new();

		public EventNotificationService(string statePath)
		{
			_statePath = statePath;
			Load();
		}

		public bool IsPaused(ulong userId)
		{
			lock (_lock)
			{
				return _pausedUsers.Contains(userId);
			}
		}

		public void Pause(ulong userId)
		{
			EventNotificationStateDto? snapshot = null;
			lock (_lock)
			{
				if (_pausedUsers.Add(userId))
					snapshot = BuildSnapshotLocked();
			}

			if (snapshot != null)
				SaveSnapshot(snapshot);
		}

		public void Unpause(ulong userId)
		{
			EventNotificationStateDto? snapshot = null;
			lock (_lock)
			{
				if (_pausedUsers.Remove(userId))
					snapshot = BuildSnapshotLocked();
			}

			if (snapshot != null)
				SaveSnapshot(snapshot);
		}

		public bool Subscribe(ulong guildId, ulong userId)
		{
			var changed = false;
			EventNotificationStateDto? snapshot = null;
			lock (_lock)
			{
				if (!_subscriptionsByGuild.TryGetValue(guildId, out var users))
				{
					users = new HashSet<ulong>();
					_subscriptionsByGuild[guildId] = users;
				}

				changed = users.Add(userId);
				var unpaused = _pausedUsers.Remove(userId);
				if (changed || unpaused)
					snapshot = BuildSnapshotLocked();
			}

			if (snapshot != null)
				SaveSnapshot(snapshot);

			return changed;
		}

		public bool Unsubscribe(ulong guildId, ulong userId)
		{
			var changed = false;
			EventNotificationStateDto? snapshot = null;
			lock (_lock)
			{
				if (!_subscriptionsByGuild.TryGetValue(guildId, out var users))
					return false;

				changed = users.Remove(userId);
				if (changed)
					snapshot = BuildSnapshotLocked();
			}

			if (snapshot != null)
				SaveSnapshot(snapshot);

			return changed;
		}

		public bool IsSubscribed(ulong guildId, ulong userId)
		{
			lock (_lock)
			{
				return _subscriptionsByGuild.TryGetValue(guildId, out var users) && users.Contains(userId);
			}
		}

		public IReadOnlyCollection<ulong> GetActiveSubscribers(ulong guildId)
		{
			lock (_lock)
			{
				if (!_subscriptionsByGuild.TryGetValue(guildId, out var users) || users.Count == 0)
					return Array.Empty<ulong>();

				if (_pausedUsers.Count == 0)
					return users.ToArray();

				return users.Where(u => !_pausedUsers.Contains(u)).ToArray();
			}
		}

		public int GetSubscriberCount(ulong guildId)
		{
			lock (_lock)
			{
				return _subscriptionsByGuild.TryGetValue(guildId, out var users) ? users.Count : 0;
			}
		}

		public int GetActiveSubscriberCount(ulong guildId)
		{
			lock (_lock)
			{
				if (!_subscriptionsByGuild.TryGetValue(guildId, out var users) || users.Count == 0)
					return 0;

				if (_pausedUsers.Count == 0)
					return users.Count;

				var count = 0;
				foreach (var u in users)
					if (!_pausedUsers.Contains(u)) count++;

				return count;
			}
		}

		private void Load()
		{
			lock (_lock)
			{
				try
				{
					if (!File.Exists(_statePath))
                      return;

					var json = File.ReadAllText(_statePath);
					var dto = JsonSerializer.Deserialize<EventNotificationStateDto>(json) ?? new EventNotificationStateDto();

					_subscriptionsByGuild.Clear();
					foreach (var kv in dto.SubscriptionsByGuild)
					{
						_subscriptionsByGuild[kv.Key] = new HashSet<ulong>(kv.Value ?? new List<ulong>());
					}

					_pausedUsers.Clear();
					foreach (var u in dto.PausedUsers ?? new List<ulong>())
						_pausedUsers.Add(u);
				}
				catch
				{
					_subscriptionsByGuild.Clear();
					_pausedUsers.Clear();
				}
			}
		}

		private EventNotificationStateDto BuildSnapshotLocked()
		{
			return new EventNotificationStateDto
			{
				SubscriptionsByGuild = _subscriptionsByGuild.ToDictionary(k => k.Key, v => v.Value.ToList()),
				PausedUsers = _pausedUsers.ToList()
			};
		}

		private void SaveSnapshot(EventNotificationStateDto dto)
		{
			try
			{
				var dir = Path.GetDirectoryName(_statePath) ?? AppContext.BaseDirectory;
				Directory.CreateDirectory(dir);

				var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
				File.WriteAllText(_statePath, json);
			}
			catch
			{
				// ignore
			}
		}

		public bool EnsureFileExists()
		{
			lock (_lock)
			{
				if (File.Exists(_statePath)) return false;
				SaveSnapshot(BuildSnapshotLocked());
				return true;
			}
		}

		private sealed class EventNotificationStateDto
		{
			public Dictionary<ulong, List<ulong>> SubscriptionsByGuild { get; set; } = new();
			public List<ulong> PausedUsers { get; set; } = new();
		}
	}
}
