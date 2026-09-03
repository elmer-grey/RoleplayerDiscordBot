using System;
using System.Collections.Generic;
using System.Linq;

namespace RPBot.VtM;

/// <summary>
/// Шаги визарда создания персонажа VtM V20.
///
/// <para>Соответствует разделам книги «Vampire: the Masquerade V20», глава 3:</para>
/// <list type="number">
///   <item><see cref="Concept"/> — амплуа, клан, натура, маска (можно пропустить).</item>
///   <item><see cref="Attributes"/> — характеристики 7/5/3.</item>
///   <item><see cref="Abilities"/> — способности 13/9/5.</item>
///   <item><see cref="Advantages"/> — преимущества (дисциплины, факты био, добродетели).</item>
///   <item><see cref="FinishingTouches"/> — последние штрихи (человечность, воля, свободные пункты).</item>
/// </list>
/// </summary>
public enum VampireWizardStep
{
    /// <summary>Шаг 1: концепция (амплуа, клан, натура, маска, описание).</summary>
    Concept = 1,
    /// <summary>Шаг 2: характеристики 7/5/3.</summary>
    Attributes = 2,
    /// <summary>Шаг 3: способности 13/9/5.</summary>
    Abilities = 3,
    /// <summary>Шаг 4: преимущества (дисциплины, факты био, добродетели).</summary>
    Advantages = 4,
    /// <summary>Шаг 5: последние штрихи (человечность, воля, свободные пункты, достоинства/недостатки).</summary>
    FinishingTouches = 5,

    /// <summary>Все шаги пройдены, черновик сохранён.</summary>
    Completed = 99,
}

/// <summary>
/// Состояние визарда создания персонажа в памяти.
///
/// <para>Сессия живёт, пока игрок проходит визард; при выходе из бота
/// или завершении сессии — удаляется. Уникальный ключ — (GuildId, PlayerId);
/// на каждую гильдию у игрока может быть только один активный визард.</para>
/// </summary>
/// <remarks>
/// <para>Черновик <see cref="Draft"/> обновляется по мере прохождения шагов.
/// На последнем шаге вызывается <c>VampireStorage.CommitDraftAsync</c> для
/// финального сохранения персонажа в JSON-файл гильдии.</para>
/// </remarks>
public sealed class VampireWizardSession
{
    /// <summary>Discord guild ID, в которой запущен визард.</summary>
    public ulong GuildId { get; set; }

    /// <summary>Discord user ID игрока, проходящего визард.</summary>
    public ulong PlayerId { get; set; }

    /// <summary>Discord username игрока (для отображения и для бинда в лист).</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>Текущий шаг визарда.</summary>
    public VampireWizardStep Step { get; set; } = VampireWizardStep.Concept;

    /// <summary>Черновик персонажа — обновляется по мере прохождения шагов.</summary>
    public VampireCharacter Draft { get; set; } = new VampireCharacter();

    /// <summary>ID сообщения в DM, в котором визард общается с игроком. Меняется при каждом обновлении UI.</summary>
    public ulong? DmMessageId { get; set; }

    /// <summary>ID канала DM, в котором идёт визард.</summary>
    public ulong DmChannelId { get; set; }

    /// <summary>
    /// Поле, которое бот ждёт от игрока следующим текстовым сообщением.
    /// null — никакое поле не запрошено.
    /// </summary>
    public string? PendingField { get; set; }

    /// <summary>Ключ сессии (GuildId, PlayerId).</summary>
    public (ulong Guild, ulong Player) Key => (GuildId, PlayerId);
}

/// <summary>
/// Потокобезопасный реестр активных визардов по (GuildId, PlayerId).
/// Хранится в памяти процесса (на время жизни бота).
/// </summary>
public sealed class VampireWizardRegistry
{
    private static readonly VampireWizardRegistry _instance = new();
    public static VampireWizardRegistry Instance => _instance;

    private readonly Dictionary<(ulong, ulong), VampireWizardSession> _byKey = new();
    private readonly object _gate = new();

    private VampireWizardRegistry() { }

    /// <summary>Получить сессию или null, если её нет.</summary>
    public VampireWizardSession? Get(ulong guildId, ulong playerId)
    {
        lock (_gate)
        {
            return _byKey.TryGetValue((guildId, playerId), out var s) ? s : null;
        }
    }

    /// <summary>Найти любую сессию по userId (по всем гильдиям).</summary>
    public VampireWizardSession? GetByUser(ulong userId)
    {
        lock (_gate)
        {
            foreach (var kv in _byKey)
            {
                if (kv.Value.PlayerId == userId) return kv.Value;
            }
            return null;
        }
    }

    /// <summary>Зарегистрировать новую сессию. Перезаписывает, если уже была.</summary>
    public void Set(ulong guildId, ulong playerId, VampireWizardSession session)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        session.GuildId = guildId;
        session.PlayerId = playerId;
        lock (_gate)
        {
            _byKey[(guildId, playerId)] = session;
        }
    }

    /// <summary>Удалить сессию по (guildId, playerId). Возвращает true, если было что удалять.</summary>
    public bool Remove(ulong guildId, ulong playerId)
    {
        lock (_gate)
        {
            return _byKey.Remove((guildId, playerId));
        }
    }

    /// <summary>Удалить все сессии указанного пользователя (по всем гильдиям). Возвращает true, если что-то удалили.</summary>
    public bool RemoveByUser(ulong userId)
    {
        lock (_gate)
        {
            var keys = _byKey.Where(kv => kv.Value.PlayerId == userId)
                              .Select(kv => kv.Key)
                              .ToList();
            foreach (var k in keys) _byKey.Remove(k);
            return keys.Count > 0;
        }
    }

    /// <summary>Проверить наличие сессии.</summary>
    public bool Contains(ulong guildId, ulong playerId)
    {
        lock (_gate)
        {
            return _byKey.ContainsKey((guildId, playerId));
        }
    }
}
