using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RPBot.Util;

namespace RPBot.VtM
{
    /// <summary>
    /// Хранилище VtM-персонажей в JSON-файле на гильдию.
    /// </summary>
    /// <remarks>
    /// <para>Путь к файлу: <c>&lt;DataRoot&gt;/Data/vtm/characters_{guildId}.json</c>.</para>
    /// <para>Ключ внутри файла — <see cref="VampireCharacter.PlayerName"/> (Discord username).</para>
    /// <para>Запись идёт через <see cref="SafeJsonIO.WriteAtomicAsync"/> — при падении
    /// процесса прежний файл остаётся валидным.</para>
    /// <para>Внутри гильдии запись защищена <see cref="SemaphoreSlim"/>, чтобы
    /// одновременные <c>/vampire_set</c> с разных каналов не потеряли обновления.</para>
    /// </remarks>
    public sealed class VampireStorage
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                };

        // PlayerName → VampireCharacter
        private Dictionary<string, VampireCharacter> _characters = new(StringComparer.Ordinal);

        public VampireStorage(ulong guildId)
        {
            var dataDir = BotConfig.GetDataDirectory();
            var vtmDir = Path.Combine(dataDir, "vtm");
            Directory.CreateDirectory(vtmDir);
            _filePath = Path.Combine(vtmDir, $"characters_{guildId}.json");
        }

        /// <summary>Путь к JSON-файлу. Для диагностики и тестов.</summary>
        public string FilePath => _filePath;

        /// <summary>Загрузить всех персонажей гильдии с диска.</summary>
        public async Task LoadAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!File.Exists(_filePath))
                {
                    _characters = new(StringComparer.Ordinal);
                    return;
                }
                try
                {
                    var text = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, VampireCharacter>>(
                        text, _json);
                    _characters = loaded != null
                        ? new Dictionary<string, VampireCharacter>(loaded, StringComparer.Ordinal)
                        : new Dictionary<string, VampireCharacter>(StringComparer.Ordinal);
                }
                catch
                {
                    // Битый JSON — не падаем, начинаем с пустого словаря.
                    // Старый файл можно восстановить из .bak, если он есть.
                    _characters = new(StringComparer.Ordinal);
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
                var json = JsonSerializer.Serialize(_characters, _json);
                await SafeJsonIO.WriteAtomicAsync(_filePath, json, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Найти персонажа по Discord-имени игрока (case-sensitive).</summary>
        public VampireCharacter? GetCharacter(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return null;
            _characters.TryGetValue(playerName, out var c);
            return c;
        }

        /// <summary>Создать или перезаписать персонажа. Возвращает true, если создан новый.</summary>
        public async Task<bool> UpsertAsync(VampireCharacter character, CancellationToken ct = default)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));
            if (string.IsNullOrEmpty(character.PlayerName))
                throw new ArgumentException("PlayerName обязателен", nameof(character));

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bool isNew = !_characters.ContainsKey(character.PlayerName);
                _characters[character.PlayerName] = character;
                return isNew;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Удалить персонажа. Возвращает true, если что-то удалено.</summary>
        public async Task<bool> RemoveAsync(string playerName, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(playerName)) return false;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return _characters.Remove(playerName);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Все персонажи гильдии (для /vampire_list).</summary>
        public IReadOnlyList<VampireCharacter> ListAll()
        {
            return new List<VampireCharacter>(_characters.Values);
        }

        /// <summary>
        /// Установить или обновить одно поле <c>Attributes</c> персонажа и сразу сохранить.
        /// Возвращает true, если персонаж существует и обновление прошло.
        /// </summary>
        public async Task<bool> SetAttributeAsync(
            string playerName,
            string attributeName,
            int value,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(attributeName))
                throw new ArgumentException("attributeName обязателен", nameof(attributeName));
            if (value < 0 || value > 5)
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..5");

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_characters.TryGetValue(playerName, out var ch))
                    return false;

                ch.Attributes[attributeName] = value;
                var json = JsonSerializer.Serialize(_characters, _json);
                await SafeJsonIO.WriteAtomicAsync(_filePath, json, ct).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Установить Hunger (1..5).</summary>
        public async Task<bool> SetHungerAsync(
            string playerName,
            int hunger,
            CancellationToken ct = default)
        {
            if (hunger < 1 || hunger > 5)
                throw new ArgumentOutOfRangeException(nameof(hunger), hunger, "1..5");

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_characters.TryGetValue(playerName, out var ch))
                    return false;
                ch.Hunger = hunger;
                var json = JsonSerializer.Serialize(_characters, _json);
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