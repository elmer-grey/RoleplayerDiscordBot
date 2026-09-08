using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RPBot.Util;

namespace RPBot.VtM
{
    /// <summary>
    /// Вид блока под листом персонажа в DM.
    /// </summary>
    public enum DmBlockKind
    {
        Description,
        Willpower,
        Health,
        /// <summary>Блок «Мораль» (V20 стр. 333): проверка совести + расстройства.</summary>
        Morality,
        /// <summary>Блок «Ярость» (V20 стр. 322-325): Frenzy + Rötschreck + атавизмы.</summary>
        Frenzy,
        /// <summary>Блок «Клан» (дисциплины + изъян).</summary>
        Clan,
    }

    /// <summary>
    /// Ссылка на одно сообщение-блок в DM (видимое, с тогглом).
    /// </summary>
    public sealed class DmBlockRef
    {
        public ulong ChannelId { get; set; }
        public ulong MessageId { get; set; }
    }

    /// <summary>
    /// Видимые блоки под листом одного персонажа в DM конкретного игрока.
    /// </summary>
    public sealed class CharacterDmBlocks
    {
        public string CharacterId { get; set; } = "";

        // Kind → message ref
        public Dictionary<DmBlockKind, DmBlockRef> Blocks { get; set; } = new();
    }

    /// <summary>
    /// Хранилище тоггл-индекса видимых блоков под листом в DM.
    /// </summary>
    /// <remarks>
    /// <para>Путь к файлу: <c>&lt;DataRoot&gt;/Data/vtm/dm_blocks_{userId}.json</c>.</para>
    /// <para>Тоггл-логика как у музыкальной очереди: при нажатии кнопки под листом
    /// бот смотрит, есть ли регистрация для этого <see cref="DmBlockKind"/>:
    /// если есть — удаляет сообщение и обнуляет запись, иначе отправляет новое
    /// сообщение и регистрирует. Так блок можно открыть/скрыть повторным нажатием.</para>
    /// <para>При изменении значения (например, +1 к воле) блок обновляется по
    /// <c>messageId</c> — а само сообщение продолжает жить, как и открытая
    /// музыкальная очередь.</para>
    /// </remarks>
    public sealed class VampireDmBlockIndex
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
        };

        // CharacterId → CharacterDmBlocks
        private readonly Dictionary<string, CharacterDmBlocks> _data = new(StringComparer.Ordinal);

        public VampireDmBlockIndex(ulong userId)
        {
            if (userId == 0)
                throw new ArgumentException("userId обязателен", nameof(userId));
            var dataDir = BotConfig.GetDataDirectory();
            var vtmDir = Path.Combine(dataDir, "vtm");
            Directory.CreateDirectory(vtmDir);
            _filePath = Path.Combine(vtmDir, $"dm_blocks_{userId}.json");
        }

        /// <summary>Путь к JSON-файлу (для диагностики и тестов).</summary>
        public string FilePath => _filePath;

        /// <summary>Загрузить индекс с диска. Безопасен при отсутствии файла.</summary>
        public async Task LoadAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!File.Exists(_filePath)) return;
                try
                {
                    var text = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, CharacterDmBlocks>>(text, _json);
                    _data.Clear();
                    if (loaded != null)
                        foreach (var kv in loaded)
                            _data[kv.Key] = kv.Value;
                }
                catch
                {
                    _data.Clear();
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Сохранить текущий снэпшот на диск атомарно.</summary>
        public async Task SaveAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var json = JsonSerializer.Serialize(_data, _json);
                await SafeJsonIO.WriteAtomicAsync(_filePath, json, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Получить регистрацию блока, либо null.
        /// </summary>
        public DmBlockRef? Get(string characterId, DmBlockKind kind)
        {
            if (string.IsNullOrEmpty(characterId)) return null;
            if (_data.TryGetValue(characterId, out var blocks)
                && blocks.Blocks.TryGetValue(kind, out var refr))
                return refr;
            return null;
        }

        /// <summary>
        /// Получить все блоки персонажа (копия).
        /// </summary>
        public IReadOnlyDictionary<DmBlockKind, DmBlockRef> GetAll(string characterId)
        {
            if (string.IsNullOrEmpty(characterId)) return new Dictionary<DmBlockKind, DmBlockRef>();
            if (_data.TryGetValue(characterId, out var blocks))
                return new Dictionary<DmBlockKind, DmBlockRef>(blocks.Blocks);
            return new Dictionary<DmBlockKind, DmBlockRef>();
        }

        /// <summary>
        /// Зарегистрировать открытый блок.
        /// </summary>
        public async Task SetAsync(
            string characterId,
            DmBlockKind kind,
            ulong channelId,
            ulong messageId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("characterId обязателен", nameof(characterId));

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_data.TryGetValue(characterId, out var blocks))
                {
                    blocks = new CharacterDmBlocks { CharacterId = characterId };
                    _data[characterId] = blocks;
                }
                blocks.Blocks[kind] = new DmBlockRef
                {
                    ChannelId = channelId,
                    MessageId = messageId,
                };
                var json = JsonSerializer.Serialize(_data, _json);
                await SafeJsonIO.WriteAtomicAsync(_filePath, json, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Снять регистрацию блока (без удаления самого сообщения —
        /// удаление на стороне вызывающего). Возвращает true, если запись была.
        /// </summary>
        public async Task<bool> ClearAsync(
            string characterId,
            DmBlockKind kind,
            CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_data.TryGetValue(characterId, out var blocks)) return false;
                bool removed = blocks.Blocks.Remove(kind);
                if (removed)
                {
                    if (blocks.Blocks.Count == 0) _data.Remove(characterId);
                    var json = JsonSerializer.Serialize(_data, _json);
                    await SafeJsonIO.WriteAtomicAsync(_filePath, json, ct).ConfigureAwait(false);
                }
                return removed;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Удалить все регистрации персонажа (например, при удалении персонажа).
        /// Возвращает true, если персонаж был в индексе.
        /// </summary>
        public async Task<bool> ForgetAsync(string characterId, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_data.Remove(characterId)) return false;
                var json = JsonSerializer.Serialize(_data, _json);
                await SafeJsonIO.WriteAtomicAsync(_filePath, json, ct).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
