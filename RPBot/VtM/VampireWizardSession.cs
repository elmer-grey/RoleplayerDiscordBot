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
/// Под-шаги Шага 4 «Преимущества» (3 экрана).
/// </summary>
public enum VampireWizardAdvantagesSubStep
{
    /// <summary>4.1 — Дисциплины.</summary>
    Disciplines = 1,
    /// <summary>4.2 — Факты биографии.</summary>
    Backgrounds = 2,
    /// <summary>4.3 — Добродетели.</summary>
    Virtues = 3,
}

/// <summary>
/// Шаги каскада распределения свободных пунктов на Шаге 5 «Последние штрихи».
/// </summary>
/// <remarks>
/// Шаг 5 имеет 6 категорий (target) и до 30+ полей (Attribute/Ability) — это
/// превышает лимит Discord на 25 опций в одном SelectMenu. Решение — каскад:
/// <list type="number">
///   <item><see cref="None"/> или <see cref="Target"/> — выбор категории (Attribute/Ability/Discipline/Background/Virtue/Humanity/Willpower).</item>
///   <item><see cref="Subgroup"/> — выбор подгруппы (для Attribute: Physical/Social/Mental; для Ability: Talents/Skills/Knowledges; для остальных категорий шаг пропускается).</item>
///   <item><see cref="Field"/> — выбор конкретного поля и знака (+/−).</item>
/// </list>
/// </remarks>
public enum VampireFreebieCascadeStep
{
    /// <summary>Не в каскаде: показываем Step 1 (выбор категории). Начальное состояние.</summary>
    None = 0,
    /// <summary>Показываем Step 1 (выбор категории). Синоним <see cref="None"/> для семантики.</summary>
    Target = 1,
    /// <summary>Показываем Step 2 (выбор подгруппы).</summary>
    Subgroup = 2,
    /// <summary>Показываем Step 3 (выбор поля).</summary>
    Field = 3,
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

    /// <summary>
    /// Под-шаг внутри Шага 4 «Преимущества». Дисциплины / Факты / Добродетели.
    /// Игнорируется на других шагах.
    /// </summary>
    public VampireWizardAdvantagesSubStep AdvantagesSubStep { get; set; } = VampireWizardAdvantagesSubStep.Disciplines;

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

    /// <summary>
    /// Шаг 4.1: если задано — бот ждёт новое имя для дисциплины Каитифа.
    /// Хранит старое имя.
    /// </summary>
    public string? PendingDisciplineRename { get; set; }

    /// <summary>
    /// Шаг 4.2: «add» — ждём имя нового факта; иначе — старое имя факта для переименования.
    /// </summary>
    public string? PendingBackgroundOp { get; set; }

    /// <summary>Ключ сессии (GuildId, PlayerId).</summary>
    public (ulong Guild, ulong Player) Key => (GuildId, PlayerId);

    /// <summary>
    /// Шаг 2: индекс «страницы» (группы) атрибутов: 0=Физ, 1=Соц, 2=Мент.
    /// По умолчанию 0 (Физ). Сбрасывается только при сбросе прогресса шага.
    /// </summary>
    public int AttrPageIndex { get; set; } = 0;

    /// <summary>
    /// Шаг 2: имя атрибута, который сейчас «выбран» (через SelectMenu) для
    /// редактирования кнопками −/+. null — ничего не выбрано.
    /// </summary>
    public string? AttrSelected { get; set; }

    /// <summary>
    /// Шаг 3: индекс активной группы способностей: 0=Таланты, 1=Навыки, 2=Знания.
    /// По умолчанию 0 (Таланты). Сбрасывается только при сбросе прогресса шага.
    /// </summary>
    public int AbilityGroupIndex { get; set; } = 0;

    /// <summary>
    /// Шаг 3: имя способности в активной группе, которая сейчас «выбрана»
    /// (через SelectMenu) для редактирования кнопками −/+. null — ничего не выбрано.
    /// </summary>
    public string? AbilitySelected { get; set; }

    /// <summary>Шаг 5: текущий шаг каскада распределения свободных пунктов.</summary>
    public VampireFreebieCascadeStep FreebieCascadeStep { get; set; } = VampireFreebieCascadeStep.None;

    /// <summary>Шаг 5: выбранный target (категория свободного пункта). null = не выбран.</summary>
    public VampireFinishingResolver.FreebieTarget? FinishingTarget { get; set; }

    /// <summary>Шаг 5: выбранная подгруппа (например, "Physical" для Attribute, "Talents" для Ability). null = не выбрана.</summary>
    public string? FinishingSubgroup { get; set; }
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
