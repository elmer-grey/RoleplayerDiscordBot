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
    /// Вид сообщения с листом персонажа. Используется для синхронизации:
    /// при изменениях персонажа обновляются все сообщения каждого вида.
    /// </summary>
    public enum SheetMessageKind
    {
        /// <summary>Лист в DM игроку с кнопками Описание/Воля/Здоровье.</summary>
        DmSheetWithButtons,
        /// <summary>Лист в общем канале, без кнопок (для просмотра).</summary>
        PublicSheet,
    }

    /// <summary>
    /// Запись об одном видимом сообщении листа.
    /// </summary>
    public sealed class DisplayMessageRef
    {
        public ulong ChannelId { get; set; }
        public ulong MessageId { get; set; }
        public SheetMessageKind Kind { get; set; }
    }

    /// <summary>
    /// Список видимых сообщений с листом одного персонажа в рамках гильдии.
    /// </summary>
    public sealed class CharacterDisplayRecord
    {
        public string CharacterId { get; set; } = "";
        public List<DisplayMessageRef> Messages { get; set; } = new();
    }

    /// <summary>
    /// Хранилище индекса видимых сообщений с листами персонажей по гильдии.
    /// </summary>
    /// <remarks>
    /// <para>Путь к файлу: <c>&lt;DataRoot&gt;/Data/vtm/display_index_{guildId}.json</c>.</para>
    /// <para>Используется для глобальной синхронизации: при изменении персонажа
    /// бот пересобирает embed и обновляет все зарегистрированные сообщения.
    /// Если сообщение удалено (NotFound) — запись убирается из индекса.</para>
    /// <para>Запись атомарна через <see cref="SafeJsonIO.WriteAtomicAsync"/>.</para>
    /// </remarks>
    public sealed class VampireDisplayIndex
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

        // CharacterId → CharacterDisplayRecord
        private readonly Dictionary<string, CharacterDisplayRecord> _data = new(StringComparer.Ordinal);

        public VampireDisplayIndex(ulong guildId)
        {
            var dataDir = BotConfig.GetDataDirectory();
            var vtmDir = Path.Combine(dataDir, "vtm");
            Directory.CreateDirectory(vtmDir);
            _filePath = Path.Combine(vtmDir, $"display_index_{guildId}.json");
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
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, CharacterDisplayRecord>>(text, _json);
                    _data.Clear();
                    if (loaded != null)
                        foreach (var kv in loaded)
                            _data[kv.Key] = kv.Value;
                }
                catch
                {
                    // Битый JSON — пустой словарь, прежний файл можно поднять из .bak.
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
        /// Зарегистрировать новое сообщение листа для персонажа.
        /// </summary>
        public async Task RegisterAsync(
            string characterId,
            ulong channelId,
            ulong messageId,
            SheetMessageKind kind,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("characterId обязателен", nameof(characterId));

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_data.TryGetValue(characterId, out var rec))
                {
                    rec = new CharacterDisplayRecord { CharacterId = characterId };
                    _data[characterId] = rec;
                }

                // Если такая запись уже есть (тот же канал/сообщение) — обновим Kind.
                rec.Messages.RemoveAll(m => m.ChannelId == channelId && m.MessageId == messageId);
                rec.Messages.Add(new DisplayMessageRef
                {
                    ChannelId = channelId,
                    MessageId = messageId,
                    Kind = kind,
                });
                var json = JsonSerializer.Serialize(_data, _json);
                await SafeJsonIO.WriteAtomicAsync(_filePath, json, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Удалить регистрацию сообщения (например, пользователь удалил embed).
        /// Возвращает true, если что-то удалено.
        /// </summary>
        public async Task<bool> UnregisterAsync(
            ulong channelId,
            ulong messageId,
            CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bool removed = false;
                foreach (var rec in _data.Values)
                {
                    var before = rec.Messages.Count;
                    rec.Messages.RemoveAll(m => m.ChannelId == channelId && m.MessageId == messageId);
                    if (rec.Messages.Count != before) removed = true;
                }
                if (removed)
                {
                    // Уберём пустые записи.
                    var empty = _data.Where(kv => kv.Value.Messages.Count == 0)
                                     .Select(kv => kv.Key).ToList();
                    foreach (var key in empty) _data.Remove(key);

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
        /// Получить все регистрации сообщений для персонажа (копия).
        /// </summary>
        public IReadOnlyList<DisplayMessageRef> GetMessages(string characterId)
        {
            if (string.IsNullOrEmpty(characterId)) return Array.Empty<DisplayMessageRef>();
            if (_data.TryGetValue(characterId, out var rec))
                return new List<DisplayMessageRef>(rec.Messages);
            return Array.Empty<DisplayMessageRef>();
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
