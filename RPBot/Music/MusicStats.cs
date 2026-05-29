using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace RPBot.Music
{
    /// <summary>Глобальная статистика воспроизведения (хранится в JSON).</summary>
    public sealed class MusicStats
    {
        public long TotalTracksAllTime { get; set; }
        public long TotalTracksThisSession { get; set; }

        // ─── Персистентность ──────────────────────────────────────────────

        private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };
        private string? _filePath;

        public static async Task<MusicStats> LoadAsync(string baseDirectory)
        {
            var path = Path.Combine(baseDirectory, BotConfig.DataFolderName, "music_stats.json");
            MusicStats stats;
            if (File.Exists(path))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(path);
                    stats = JsonSerializer.Deserialize<MusicStats>(text, _json) ?? new();
                }
                catch { stats = new(); }
            }
            else { stats = new(); }
            stats._filePath = path;
            stats.TotalTracksThisSession = 0; // сессия всегда начинается с 0
            return stats;
        }

        public async Task IncrementAsync()
        {
            TotalTracksAllTime++;
            TotalTracksThisSession++;
            if (_filePath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(this, _json));
            }
        }
    }
}
