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
                            Converters = { new VampireBackgroundsJsonConverter() },
                        };

        // PlayerName → VampireCharacter
        private Dictionary<string, VampireCharacter> _characters = new(StringComparer.Ordinal);
                // Вторичный индекс PlayerId → PlayerName (для поиска по Discord user ID).
                private Dictionary<ulong, string> _byPlayerId = new();
                // Вторичный индекс CharacterId → PlayerName (для поиска по UUID).
                private Dictionary<Guid, string> _byCharacterId = new();

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
                                    var migrated = RebuildCharacterIds();
                                    var freebieMigrated = VampireFreebieSplitMigration.Migrate(_characters);
                                    RebuildPlayerIdIndex();
                                    if (migrated > 0 || freebieMigrated > 0)
                                    {
                                        // Сохраняем, чтобы UUID'ы и/или freebie-разделение попали на диск,
                                        // а жили только в RAM.
                                        var json = JsonSerializer.Serialize(_characters, _json);
                                        await SafeJsonIO.WriteAtomicAsync(_filePath, json, ct).ConfigureAwait(false);
                                    }
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

                /// <summary>
                /// Найти персонажа по Discord user ID (ulong). Возвращает null, если такого ID нет.
                /// </summary>
                public VampireCharacter? GetByPlayerId(ulong playerId)
                {
                    if (playerId == 0) return null;
                    if (_byPlayerId.TryGetValue(playerId, out var name))
                    {
                        return GetCharacter(name);
                    }
                    return null;
                }

                /// <summary>
                                /// Найти персонажа по его стабильному UUID (CharacterId). Возвращает null,
                                /// если такого Id нет в индексе.
                                /// </summary>
                                /// <remarks>
                                /// Используется в сценариях, когда команда/кнопка сначала получает UUID
                                /// (например, из CustomId кнопки под листом).
                                /// </remarks>
                                public VampireCharacter? GetByCharacterId(Guid characterId)
                                {
                                    if (characterId == Guid.Empty) return null;
                                    if (_byCharacterId.TryGetValue(characterId, out var name))
                                    {
                                        return GetCharacter(name);
                                    }
                                    return null;
                                }

                                /// <summary>
                                /// Перестроить вторичный индекс PlayerId → PlayerName (вызывать после Load и при ручных правках).
                                /// </summary>
                                private void RebuildPlayerIdIndex()
                                {
                                    _byPlayerId.Clear();
                                    foreach (var c in _characters.Values)
                                    {
                                        if (c.PlayerId != 0)
                                        {
                                            _byPlayerId[c.PlayerId] = c.PlayerName;
                                        }
                                    }
                                }

                                /// <summary>
                                /// Проставить CharacterId персонажам без него (для миграции со старых
                                /// сохранений). Возвращает количество персонажей, получивших новый UUID.
                                /// </summary>
                                /// <remarks>
                                /// Вызывать из <see cref="LoadAsync"/> сразу после десериализации,
                                /// чтобы индекс <see cref="_byCharacterId"/> был согласован с диском.
                                /// </remarks>
                                public int RebuildCharacterIds()
                                {
                                    _byCharacterId.Clear();
                                    int migrated = 0;
                                    foreach (var c in _characters.Values)
                                    {
                                        if (c.CharacterId == Guid.Empty)
                                        {
                                            c.CharacterId = Guid.NewGuid();
                                            migrated++;
                                        }
                                        if (c.CharacterId != Guid.Empty)
                                        {
                                            _byCharacterId[c.CharacterId] = c.PlayerName;
                                        }
                                    }
                                    return migrated;
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
                                            // Если у нового/обновлённого персонажа есть PlayerId, индексируем.
                                            // Если у нового/обновлённого персонажа ещё нет CharacterId — генерируем.
                                            if (character.CharacterId == Guid.Empty)
                                            {
                                                character.CharacterId = Guid.NewGuid();
                                            }
                                            _characters[character.PlayerName] = character;
                                            if (character.PlayerId != 0)
                                            {
                                                _byPlayerId[character.PlayerId] = character.PlayerName;
                                            }
                                            _byCharacterId[character.CharacterId] = character.PlayerName;
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
                        if (_characters.TryGetValue(playerName, out var existing))
                        {
                            UnregisterIndices(existing);
                        }
                        return _characters.Remove(playerName);
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }

                /// <summary>
                /// Сохранить черновик из визарда как готового персонажа.
                /// </summary>
                /// <remarks>
                /// <para>В Этапе 1 черновик сохраняется по PlayerName (так же, как старые
                /// персонажи). В Этапе 3 будет переход на ключ CharacterName с миграцией.</para>
                /// </remarks>
                public Task<bool> UpsertDraftAsync(VampireCharacter draft, CancellationToken ct = default)
                {
                    if (draft == null) throw new ArgumentNullException(nameof(draft));
                    if (draft.CharacterId == Guid.Empty)
                        draft.CharacterId = Guid.NewGuid();
                    if (string.IsNullOrEmpty(draft.PlayerName))
                        throw new ArgumentException("PlayerName обязателен у draft", nameof(draft));
                    if (string.IsNullOrEmpty(draft.CharacterName))
                    {
                        // На Этапе 1 имя персонажа не запрашивается отдельно — берём как PlayerName,
                        // чтобы лист был виден в /vampire show. На Этапе 3 будет отдельное поле.
                        draft.CharacterName = draft.PlayerName;
                    }
                    return UpsertAsync(draft, ct);
                }

                /// <summary>Найти персонажа по имени (CharacterName). Case-insensitive.</summary>
                /// <returns>0 или 1 персонажа; при >1 — исключение <see cref="InvalidOperationException"/>.</returns>
                public VampireCharacter? FindByCharacterName(string characterName)
                {
                    if (string.IsNullOrWhiteSpace(characterName)) return null;
                    int matches = 0;
                    VampireCharacter? found = null;
                    foreach (var c in _characters.Values)
                    {
                        if (string.Equals(c.CharacterName, characterName, StringComparison.OrdinalIgnoreCase))
                        {
                            matches++;
                            found = c;
                        }
                    }
                    if (matches > 1)
                        throw new InvalidOperationException(
                            $"Несколько персонажей с именем '{characterName}' ({matches} шт.)");
                    return found;
                }

                /// <summary>Все персонажи с указанным CharacterName (для диагностики коллизий имён).</summary>
                public IReadOnlyList<VampireCharacter> FindAllByCharacterName(string characterName)
                {
                    if (string.IsNullOrWhiteSpace(characterName)) return Array.Empty<VampireCharacter>();
                    var list = new List<VampireCharacter>();
                    foreach (var c in _characters.Values)
                    {
                        if (string.Equals(c.CharacterName, characterName, StringComparison.OrdinalIgnoreCase))
                            list.Add(c);
                    }
                    return list;
                }

                /// <summary>
                /// Результат <see cref="BindAsync"/> и <see cref="UnbindAsync"/> —
                /// дискриминированный исход, потому что при отказе нужна причина
                /// для embed-ответа команды.
                /// </summary>
                public enum BindResultKind
                {
                    Ok,
                    NotFound,
                    AlreadyBoundToSame,
                    AlreadyBoundToOther,
                    InvalidUserId,
                }

                public readonly record struct BindResult(
                    BindResultKind Kind,
                    VampireCharacter? Character,
                    ulong CurrentPlayerId,
                    string Message);

                /// <summary>
                /// Привязать Discord-пользователя к персонажу (по CharacterName).
                /// </summary>
                /// <remarks>
                /// <para>Сначала ищет персонажа по <see cref="VampireCharacter.CharacterName"/>
                /// (case-insensitive). Если найдено несколько — возвращает
                /// <see cref="BindResultKind.NotFound"/> с пояснением (команда должна
                /// уточнить у ST).</para>
                /// <para>Правила:</para>
                /// <list type="bullet">
                /// <item>userId == 0 → <see cref="BindResultKind.InvalidUserId"/>.</item>
                /// <item>Уже привязан к этому же userId → <see cref="BindResultKind.AlreadyBoundToSame"/>
                /// (no-op, ошибка не отдаётся).</item>
                /// <item>Уже привязан к другому userId → <see cref="BindResultKind.AlreadyBoundToOther"/>;
                /// сначала нужно <see cref="UnbindAsync"/>.</item>
                /// </list>
                /// </remarks>
                public async Task<BindResult> BindAsync(string characterName, ulong userId, CancellationToken ct = default)
                {
                    if (userId == 0)
                        return new BindResult(BindResultKind.InvalidUserId, null, 0, "userId == 0");

                    VampireCharacter? ch;
                    try
                    {
                        ch = FindByCharacterName(characterName);
                    }
                    catch (InvalidOperationException ex)
                    {
                        return new BindResult(BindResultKind.NotFound, null, 0, ex.Message);
                    }
                    if (ch == null)
                        return new BindResult(BindResultKind.NotFound, null, 0, $"«{characterName}» не найден");

                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (ch.PlayerId == userId)
                        {
                            return new BindResult(
                                BindResultKind.AlreadyBoundToSame, ch, userId,
                                "уже привязан к этому игроку");
                        }
                        if (ch.PlayerId != 0 && ch.PlayerId != userId)
                        {
                            return new BindResult(
                                BindResultKind.AlreadyBoundToOther, ch, ch.PlayerId,
                                $"уже привязан к <@{ch.PlayerId}>; сначала /vampire unbind");
                        }

                        // Снимаем старый индекс, если был.
                        ch.PlayerId = userId;
                        _byPlayerId[userId] = ch.PlayerName;

                        var json = JsonSerializer.Serialize(_characters, _json);
                        await SafeJsonIO.WriteAtomicAsync(_filePath, json, ct).ConfigureAwait(false);

                        return new BindResult(BindResultKind.Ok, ch, userId, "привязано");
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }

                /// <summary>
                /// Отменить привязку Discord-пользователя от персонажа (по CharacterName).
                /// </summary>
                public async Task<BindResult> UnbindAsync(string characterName, CancellationToken ct = default)
                {
                    VampireCharacter? ch;
                    try
                    {
                        ch = FindByCharacterName(characterName);
                    }
                    catch (InvalidOperationException ex)
                    {
                        return new BindResult(BindResultKind.NotFound, null, 0, ex.Message);
                    }
                    if (ch == null)
                        return new BindResult(BindResultKind.NotFound, null, 0, $"«{characterName}» не найден");

                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (ch.PlayerId == 0)
                        {
                            return new BindResult(
                                BindResultKind.AlreadyBoundToSame, ch, 0,
                                "привязки и так нет");
                        }

                        ulong old = ch.PlayerId;
                        ch.PlayerId = 0;
                        // Удаляем индекс, только если он указывает на этого персонажа.
                        if (_byPlayerId.TryGetValue(old, out var name) && name == ch.PlayerName)
                        {
                            _byPlayerId.Remove(old);
                        }

                        var json = JsonSerializer.Serialize(_characters, _json);
                        await SafeJsonIO.WriteAtomicAsync(_filePath, json, ct).ConfigureAwait(false);

                        return new BindResult(BindResultKind.Ok, ch, 0, $"снято (был <@{old}>)");
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }

                private void UnregisterIndices(VampireCharacter c)
                {
                    if (c.PlayerId != 0 && _byPlayerId.TryGetValue(c.PlayerId, out var name) && name == c.PlayerName)
                    {
                        _byPlayerId.Remove(c.PlayerId);
                    }
                    if (c.CharacterId != Guid.Empty)
                    {
                        _byCharacterId.Remove(c.CharacterId);
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