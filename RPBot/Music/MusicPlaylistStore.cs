using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using RPBot.Util;

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
            // baseDirectory обычно равен AppContext.BaseDirectory; используем BotConfig.GetDataDirectory(),
            // чтобы файл жил в production-каталоге данных (AppData/.../RPBot/Data/ на Windows),
            // а не рядом с .exe.
            _filePath = Path.Combine(BotConfig.GetDataDirectory(), "music_playlists.json");
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
                    // SafeJsonIO.WriteAtomicAsync пишет через .tmp + File.Move(overwrite:true),
                    // чтобы при падении процесса прежний файл остался валидным.
                    var json = JsonSerializer.Serialize(_data, _json);
                    await SafeJsonIO.WriteAtomicAsync(_filePath, json).ConfigureAwait(false);
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

        /// <summary>
        /// Переименовывает плейлист. Возвращает false, если плейлист не найден
        /// или имя уже занято у этого пользователя.
        /// </summary>
        public async Task<bool> RenamePlaylistAsync(ulong guildId, ulong userId, string oldName, string newName)
        {
            var gk     = guildId.ToString();
            var uk     = userId.ToString();
            var oldKey = oldName.ToLowerInvariant();
            var newKey = newName.ToLowerInvariant();

            if (!_data.TryGetValue(gk, out var users)) return false;
            if (!users.TryGetValue(uk, out var playlists)) return false;
            if (!playlists.TryGetValue(oldKey, out var pl)) return false;
            if (playlists.ContainsKey(newKey) && newKey != oldKey) return false;

            playlists.Remove(oldKey);
            pl.Name = newName;
            playlists[newKey] = pl;
            await SaveAsync();
            return true;
        }

        /// <summary>
        /// Ищет плейлист по имени в рамках гильдии.
        /// Приоритет: сначала у самого пользователя, затем публичные других.
        /// Возвращает плейлист даже если он личный чужой — вызывающий код проверяет OwnerId.
        /// </summary>
        public MusicPlaylist? FindByName(ulong guildId, ulong requestingUserId, string name)
        {
            var key = name.ToLowerInvariant();
            if (!_data.TryGetValue(guildId.ToString(), out var users)) return null;

            // 1. Сначала ищем у самого пользователя
            var uk = requestingUserId.ToString();
            if (users.TryGetValue(uk, out var own) && own.TryGetValue(key, out var ownPl))
                return ownPl;

            // 2. Затем ищем публичные плейлисты других пользователей
            foreach (var (uid, playlists) in users)
            {
                if (uid == uk) continue;
                if (playlists.TryGetValue(key, out var pl) && pl.IsPublic)
                    return pl;
            }

            // 3. Плейлист с таким именем есть, но личный чужой — вернём его чтобы дать правильную ошибку
            foreach (var (uid, playlists) in users)
            {
                if (uid == uk) continue;
                if (playlists.TryGetValue(key, out var pl))
                    return pl;
            }

            return null;
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
