using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace RPBot.Music
{
    /// <summary>Сохранённое состояние очереди одной гильдии.</summary>
    public sealed class PersistedQueue
    {
        public ulong   GuildId      { get; set; }
        public string? CurrentUrl   { get; set; }
        public List<string> QueueUrls { get; set; } = new();
        public DateTime SavedAt     { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Сохраняет и восстанавливает очереди гильдий между перезапусками.</summary>
    public sealed class MusicQueueStore
    {
        private readonly string _filePath;
        private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

        public MusicQueueStore(string baseDirectory)
        {
            _filePath = Path.Combine(baseDirectory, BotConfig.DataFolderName, "music_queues.json");
        }

        public async Task SaveAsync(ulong guildId, string? currentUrl, IEnumerable<string> queueUrls)
        {
            var all = await LoadRawAsync();
            all[guildId.ToString()] = new PersistedQueue
            {
                GuildId    = guildId,
                CurrentUrl = currentUrl,
                QueueUrls  = new List<string>(queueUrls),
                SavedAt    = DateTime.UtcNow,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, _json));
        }

        public async Task<PersistedQueue?> LoadAsync(ulong guildId)
        {
            var all = await LoadRawAsync();
            return all.TryGetValue(guildId.ToString(), out var q) ? q : null;
        }

        public async Task ClearAsync(ulong guildId)
        {
            var all = await LoadRawAsync();
            if (all.Remove(guildId.ToString()))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, _json));
            }
        }

        /// <summary>Возвращает все сохранённые очереди (для восстановления после перезапуска).</summary>
        public async Task<IReadOnlyList<PersistedQueue>> LoadAllAsync()
        {
            var raw = await LoadRawAsync();
            return new List<PersistedQueue>(raw.Values);
        }

        private async Task<Dictionary<string, PersistedQueue>> LoadRawAsync()
        {
            if (!File.Exists(_filePath)) return new();
            try
            {
                var text = await File.ReadAllTextAsync(_filePath);
                return JsonSerializer.Deserialize<Dictionary<string, PersistedQueue>>(text, _json) ?? new();
            }
            catch { return new(); }
        }
    }
}
