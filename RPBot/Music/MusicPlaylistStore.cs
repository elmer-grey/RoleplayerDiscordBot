using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace RPBot.Music
{
    /// <summary>
    /// Модель именованного плейлиста.
    /// </summary>
    public sealed class MusicPlaylist
    {
        public string Name { get; set; } = "";
        public ulong OwnerId { get; set; }
        public List<string> Urls { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool IsPublic { get; set; }
    }

    /// <summary>
    /// Хранит пользовательские плейлисты в JSON-файле.
    /// Ключ: (guildId, ownerUserId, playlistName).
    /// </summary>
    public sealed class MusicPlaylistStore
    {
        private readonly string _filePath;

        // Внутренняя структура: guildId → userId → playlistName → playlist
        private Dictionary<string, Dictionary<string, Dictionary<string, MusicPlaylist>>> _data = new();

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public MusicPlaylistStore(string baseDirectory)
        {
            _filePath = Path.Combine(baseDirectory, "Settings", "music_playlists.json");
        }

        // ─── CRUD ─────────────────────────────────────────────────────────

        public async Task LoadAsync()
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                var json = await File.ReadAllTextAsync(_filePath);
                _data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, MusicPlaylist>>>>(json, _json)
                        ?? new();
            }
            catch { _data = new(); }
        }

        public async Task SaveAsync()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(_data, _json);
            await File.WriteAllTextAsync(_filePath, json);
        }

        public MusicPlaylist? Get(ulong guildId, ulong userId, string name)
        {
            var key = name.ToLowerInvariant();
            if (_data.TryGetValue(guildId.ToString(), out var users)
                && users.TryGetValue(userId.ToString(), out var playlists)
                && playlists.TryGetValue(key, out var pl))
                return pl;
            return null;
        }

        public IReadOnlyList<MusicPlaylist> GetAll(ulong guildId, ulong userId)
        {
            if (_data.TryGetValue(guildId.ToString(), out var users)
                && users.TryGetValue(userId.ToString(), out var playlists))
                return new List<MusicPlaylist>(playlists.Values);
            return Array.Empty<MusicPlaylist>();
        }

        /// <summary>Возвращает все публичные плейлисты гильдии (кроме принадлежащих userId).</summary>
        public IReadOnlyList<MusicPlaylist> GetAllPublic(ulong guildId, ulong excludeUserId)
        {
            var result = new List<MusicPlaylist>();
            if (!_data.TryGetValue(guildId.ToString(), out var users)) return result;
            foreach (var (uid, playlists) in users)
            {
                if (uid == excludeUserId.ToString()) continue;
                foreach (var pl in playlists.Values)
                    if (pl.IsPublic) result.Add(pl);
            }
            return result;
        }

        /// <summary>Устанавливает публичность плейлиста.</summary>
        public async Task SetPublicAsync(ulong guildId, ulong userId, string name, bool isPublic)
        {
            var pl = Get(guildId, userId, name);
            if (pl is null) return;
            pl.IsPublic = isPublic;
            await SaveAsync();
        }

        public async Task SavePlaylistAsync(ulong guildId, ulong userId, MusicPlaylist playlist)
        {
            var gk = guildId.ToString();
            var uk = userId.ToString();
            var nk = playlist.Name.ToLowerInvariant();

            if (!_data.TryGetValue(gk, out var users))
                _data[gk] = users = new();
            if (!users.TryGetValue(uk, out var playlists))
                users[uk] = playlists = new();

            playlists[nk] = playlist;
            await SaveAsync();
        }

        public async Task<bool> DeletePlaylistAsync(ulong guildId, ulong userId, string name)
        {
            var key = name.ToLowerInvariant();
            if (_data.TryGetValue(guildId.ToString(), out var users)
                && users.TryGetValue(userId.ToString(), out var playlists)
                && playlists.Remove(key))
            {
                await SaveAsync();
                return true;
            }
            return false;
        }
    }
}
