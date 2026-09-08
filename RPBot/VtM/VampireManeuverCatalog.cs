using System.Collections.Generic;

namespace RPBot.VtM;

/// <summary>
/// Справочник манёвров, оружия и брони по правилам VtM V20.
/// <para>Источник: <c>v20_p285-320.txt</c>, стр. 309-310.</para>
/// </summary>
/// <remarks>
/// <para>Все типы представлены в виде статических полей и коллекций, чтобы
/// облегчить встраивание в slash-команды (<c>/vampire_maneuver</c>,
/// <c>/vampire_weapon</c>, <c>/vampire_armor</c>) и автоматизированные тесты.</para>
/// <para>При обновлении правил — поднять <see cref="SchemaVersion"/> и
/// продублировать правки в <c>vtm-dice-rules.md</c>.</para>
/// </remarks>
public static class VampireManeuverCatalog
{
    /// <summary>Версия схемы справочника. Поднимается при изменении структуры.</summary>
    public const int SchemaVersion = 2;

    /// <summary>Список всех манёвров ближнего боя (V20, стр. 309).</summary>
    public static readonly IReadOnlyList<Maneuver> Melee = new Maneuver[]
    {
        new("удар_рукой",        "Удар рукой",     MeleeKind.Attack,    "Ловкость + Драка",        0, "Сила",           DamageType.Lethal),
        new("удар_ногой",        "Удар ногой",     MeleeKind.Attack,    "Сила + Драка",            1, "Сила + 1",       DamageType.Bashing),
        new("удар_когтями",      "Удар когтями",   MeleeKind.Attack,    "Ловкость + Драка",        0, "Сила + 1",       DamageType.Aggravated),
        new("укус",              "Укус",           MeleeKind.Attack,    "Ловкость + Драка",        1, "Сила + 1",       DamageType.Aggravated,
            "Требует предшествующего клинча/захвата/броска. Можно заменить на Поцелуй (без урона)."),
        new("поцелуй",           "Поцелуй",        MeleeKind.Attack,    "Ловкость + Драка",        1, "—",              DamageType.None,
            "Замена Укуса без нанесения повреждений. Используется для кормления."),
        new("клинч",             "Клинч",          MeleeKind.Attack,    "Сила + Драка",            0, "Сила",           DamageType.Bashing,
            "Можно поддерживать несколько ходов."),
        new("бросок",            "Бросок",         MeleeKind.Attack,    "Сила + Драка",            1, "Сила + 1",       DamageType.Bashing,
            "При неудаче жертвы (Лов + Атлетика 7) — сбивание; при успехе защиты — +1 к сл. на след. ход."),
        new("подсечка",          "Подсечка",       MeleeKind.Attack,    "Ловкость + (Драка или Фехтование)", 1, "Сила", DamageType.Bashing,
            "Защита Лов + Атлетика 8. При успехе — сбивание."),
        new("разоружение",       "Разоружение",    MeleeKind.Attack,    "Ловкость + Фехтование",   1, "Особый",         DamageType.None,
            "Успех = количество успехов превышает Силу противника."),
        new("удар_оружием",      "Удар оружием",   MeleeKind.Attack,    "Ловкость + Фехтование",   0, "Оружие",         DamageType.Bashing,
            "База и тип повреждения — из таблицы оружия."),
        new("блок",              "Блок",           MeleeKind.Defense,   "Ловкость + Драка",        0, "—",              DamageType.None,
            "Защита от лёгких повреждений. Уменьшает успехи атаки противника."),
        new("парирование",       "Парирование",    MeleeKind.Defense,   "Ловкость + Фехтование",   0, "—",              DamageType.None,
            "Защита от любой атаки. Контратака: урон оружия +1d10 за каждый доп. успех сверх первого."),
        new("уклонение",         "Уклонение",      MeleeKind.Defense,   "Ловкость + Атлетика",     0, "—",              DamageType.None,
            "Защита от любой атаки. Уменьшает успехи атаки противника."),
        new("захват",            "Захват",         MeleeKind.Attack,    "Сила + Драка",            0, "—",              DamageType.None,
            "Можно поддерживать несколько ходов. Позволяет провести Укус."),
    };

    /// <summary>Список манёвров дистанционного боя (V20, стр. 309).</summary>
    public static readonly IReadOnlyList<Maneuver> Ranged = new Maneuver[]
    {
        new("беглый_огонь",      "Беглый огонь",   RangedKind.Attack,   "Ловкость + Стрельба",     0, "Оружие",         DamageType.Lethal,
            "Пул делится на количество выстрелов, до предела скорострельности."),
        new("короткая_очередь",  "Короткая очередь", RangedKind.Attack, "Ловкость + Стрельба",     2, "Оружие",         DamageType.Lethal,
            "+2d10 к пулу, +1 к сложности, расход 3 патронов."),
        new("длинная_очередь",   "Длинная очередь",  RangedKind.Attack, "Ловкость + Стрельба",     10, "Оружие",        DamageType.Lethal,
            "+10d10 к пулу, +2 к сложности, расход ≥50% магазина."),
        new("обстрел",           "Обстрел",        RangedKind.Attack,   "Ловкость + Стрельба",     10, "Особый",         DamageType.Lethal,
            "+10d10 к пулу, +2 к сложности. Пул делится между целями, минимум 1d10 каждой."),
        new("наведение",         "Наведение",      RangedKind.Support,  "Ловкость + Стрельба",     0, "—",              DamageType.None,
            "Целый ход. +1d10 на следующий выстрел (макс Восприятие); с оптикой — первый выстрел +3d10."),
        new("перезарядка",       "Перезарядка",    RangedKind.Support,  "—",                       0, "—",              DamageType.None,
            "Действие на целый ход."),
        new("стрельба_по_македонски", "Стрельба по-македонски", RangedKind.Attack, "Ловкость + Стрельба", 0, "Оружие", DamageType.Lethal,
            "+1 к сложности для неосновной руки."),
    };

    /// <summary>Общие боевые модификаторы (V20, стр. 303-304).</summary>
    public static readonly IReadOnlyList<GeneralModifier> General = new GeneralModifier[]
    {
        new("атака_с_фланга",    "Атака с фланга",    "+1d10 к пулу атаки."),
        new("атака_с_тыла",      "Атака с тыла",      "+2d10 к пулу атаки."),
        new("засада",            "Засада",            "Встречная Лов + Скрытность vs Восприятие + Бдительность → свободная атака +1d10 за каждый успех."),
        new("численное_превосходство", "Численное превосходство",
            "+1 к пулу атаки за каждого противника после первого (макс +4)."),
        new("отмена_действия",   "Отмена действия",   "Проверка воли (сл. 6) или трата пункта воли."),
        new("прицеливание",      "Прицеливание",      "+1 к сл. для средней цели, +2 для маленькой, +3 для крошечной."),
        new("обездвижен_противник", "Обездвиженный противник",
            "+2d10 к пулу атаки. Полностью обездвиженный — атака автоматически успешна."),
        new("ослепленный_противник", "Ослеплённый противник",
            "+2d10 к пулу атаки. Сам ослеплён — +2 к сл. всех своих проверок."),
    };

    /// <summary>Таблица брони (V20, стр. 310).</summary>
    public static readonly IReadOnlyList<ArmorClass> Armor = new ArmorClass[]
    {
        new("класс_1", "Класс I — защитная одежда",     1,  0),
        new("класс_2", "Класс II — бронированная одежда", 2, -1),
        new("класс_3", "Класс III — лёгкий бронежилет",  3, -1),
        new("класс_4", "Класс IV — армейский бронежилет", 4, -2),
        new("класс_5", "Класс V — тяжёлая полицейская броня", 5, -3),
    };

    /// <summary>Таблица холодного оружия (V20, стр. 310).</summary>
    public static readonly IReadOnlyList<Weapon> MeleeWeapons = new Weapon[]
    {
        Weapon.CreateMelee("дубинка",  "Дубинка",   "Сила + 1", WeaponDamageKind.StrengthPlus, 1, DamageType.Bashing, "ВК",
            "Дробящее. Наносит лёгкие повреждения, если удар не направлен в голову."),
        Weapon.CreateMelee("бита",     "Бита",      "Сила + 2", WeaponDamageKind.StrengthPlus, 2, DamageType.Bashing, "ПП",
            "Дробящее. Наносит лёгкие повреждения, если удар не направлен в голову."),
        Weapon.CreateMelee("нож",      "Нож",       "Сила + 1", WeaponDamageKind.StrengthPlus, 1, DamageType.Lethal, "ПК"),
        Weapon.CreateMelee("меч",      "Меч",       "Сила + 2", WeaponDamageKind.StrengthPlus, 2, DamageType.Lethal, "ПП"),
        Weapon.CreateMelee("топор",    "Топор",     "Сила + 3", WeaponDamageKind.StrengthPlus, 3, DamageType.Lethal, "Н"),
        Weapon.CreateMelee("кол",      "Кол",       "Сила + 1", WeaponDamageKind.StrengthPlus, 1, DamageType.Lethal, "ПП",
            "Если вонзить в сердце, может парализовать вампира. Сл. 9, нужно нанести 3 повреждения."),
    };

    /// <summary>Таблица дистанционного оружия (V20, стр. 311).</summary>
    public static readonly IReadOnlyList<Weapon> RangedWeapons = new Weapon[]
    {
        Weapon.CreateRanged("легкий_револьвер",        "Лёгкий револьвер (Смит-Вессон М36, .38)",          4, 12, 2, 3,  6,  "ВК", DamageType.Lethal,
            "Смертным — тяжёлые; вампирам — лёгкие, кроме выстрела в голову."),
        Weapon.CreateRanged("тяжелый_револьвер",       "Тяжёлый револьвер (Ругер Редхок, .44)",             6, 35, 7, 2,  6,  "ПК", DamageType.Lethal),
        Weapon.CreateRanged("легкий_саморез_пистолет", "Лёгкий самозарядный пистолет (HK USP, 9мм)",        4, 20, 4, 4,  15, "ВК", DamageType.Lethal),
        Weapon.CreateRanged("тяжелый_саморез_пистолет","Тяжёлый самозарядный пистолет (Springfield XD, .45)",5, 25, 5, 3,  13, "ПК", DamageType.Lethal),
        Weapon.CreateRanged("винтовка",                "Винтовка (Beretta Tikka T3, .30-06)",                8, 200, 40,1,  3,  "Н", DamageType.Lethal),
        Weapon.CreateRanged("легкий_пп",               "Лёгкий пистолет-пулемёт (Glock 18, 9мм)",           4, 20, 4, 3,  17, "ПК", DamageType.Lethal,
            "Поддерживает очереди и обстрел."),
        Weapon.CreateRanged("тяжелый_пп",              "Тяжёлый пистолет-пулемёт (HK MP5, 9мм)",            4, 50, 10,3,  30, "ПП", DamageType.Lethal,
            "Поддерживает очереди и обстрел."),
        Weapon.CreateRanged("автомат",                 "Автомат (FN SCAR, 5.56 НАТО)",                      7, 150, 30,3,  30, "Н",  DamageType.Lethal,
            "Поддерживает очереди и обстрел."),
        Weapon.CreateRanged("дробовик",                "Дробовик (Remington 870, 12-й калибр)",             8, 20,  4, 1,  5,  "ПП", DamageType.Lethal),
        Weapon.CreateRanged("саморез_дробовик",        "Самозарядный дробовик (Benelli M4, 12-й калибр)",   8, 20,  4, 3,  6,  "ПП", DamageType.Lethal),
        Weapon.CreateRanged("арбалет",                 "Арбалет",                                            5, 20,  4, 1,  1,  "ПП", DamageType.Lethal,
            "Перезаряжается 5 ходов. Стрелой можно пронзить сердце вампира. В голову/сердце — лёгкие, иначе тоже лёгкие; смертным — всегда тяжёлые."),
    };

    // ─── Поисковые хелперы ─────────────────────────────────────────

    public static Maneuver? FindMelee(string key) =>
        Maneuver.Find(Melee, key);

    public static Maneuver? FindRanged(string key) =>
        Maneuver.Find(Ranged, key);

    public static Weapon? FindWeapon(string key)
    {
        foreach (var w in MeleeWeapons)
        {
            if (w.Key == key) return w;
        }
        foreach (var w in RangedWeapons)
        {
            if (w.Key == key) return w;
        }
        return null;
    }

    public static ArmorClass? FindArmor(string key) =>
        ArmorClass.Find(Armor, key);

    /// <summary>Все ключи манёвров и оружия для выпадающих списков slash-команд.</summary>
    public static IEnumerable<string> AllKeys()
    {
        foreach (var m in Melee) yield return m.Key;
        foreach (var m in Ranged) yield return m.Key;
        foreach (var w in MeleeWeapons) yield return w.Key;
        foreach (var w in RangedWeapons) yield return w.Key;
        foreach (var a in Armor) yield return a.Key;
    }
}

/// <summary>Тип манёвра ближнего боя.</summary>
public enum MeleeKind { Attack, Defense }

/// <summary>Тип манёвра дистанционного боя.</summary>
public enum RangedKind { Attack, Defense, Support }

/// <summary>Тип повреждения по V20.</summary>
public enum DamageType
{
    /// <summary>Без повреждения (защитный манёвр, поцелуй, разоружение).</summary>
    None,
    /// <summary>Лёгкие повреждения (/).</summary>
    Bashing,
    /// <summary>Тяжёлые повреждения (Х).</summary>
    Lethal,
    /// <summary>Губительные повреждения (Ж).</summary>
    Aggravated,
}

/// <summary>Как оружие задаёт базу урона.</summary>
public enum WeaponDamageKind
{
    /// <summary>Фиксированная база (например, револьвер — 4).</summary>
    Fixed,
    /// <summary>База = Сила + N (например, меч — Сила + 2).</summary>
    StrengthPlus,
}

/// <summary>Описание манёвра (ближний или дистанционный).</summary>
public sealed record Maneuver(
    string Key,
    string Name,
    /// <summary>Общий тип манёвра — атака/защита/поддержка.</summary>
    object Kind,
    string Stat,
    int Accuracy,
    string DamageFormula,
    DamageType DamageKind,
    string? Notes = null)
{
    /// <summary>Ищет манёвр по ключу в коллекции.</summary>
    public static Maneuver? Find(IReadOnlyList<Maneuver> list, string key)
    {
        foreach (var m in list)
        {
            if (m.Key == key) return m;
        }
        return null;
    }
}

/// <summary>Общий боевой модификатор (V20, стр. 303-304).</summary>
public sealed record GeneralModifier(string Key, string Name, string Effect);

/// <summary>Класс брони (V20, стр. 310).</summary>
public sealed record ArmorClass(string Key, string Name, int Protection, int ComfortModifier)
{
    /// <summary>Ищет класс брони по ключу.</summary>
    public static ArmorClass? Find(IReadOnlyList<ArmorClass> list, string key)
    {
        foreach (var a in list)
        {
            if (a.Key == key) return a;
        }
        return null;
    }
}

/// <summary>
/// Оружие (холодное или дистанционное).
/// <para>Использовать <see cref="CreateMelee"/> и <see cref="CreateRanged"/> для создания записей.</para>
/// </summary>
public sealed record Weapon
{
    public string Key { get; init; }
    public string Name { get; init; }
    public string DamageFormula { get; init; } = "";
    public WeaponDamageKind DamageFormulaKind { get; init; } = WeaponDamageKind.Fixed;
    public int DamageBase { get; init; }
    public DamageType DamageType { get; init; } = DamageType.Bashing;
    public string Concealment { get; init; } = "";
    public string? Notes { get; init; }
    public int Range { get; init; }
    public int MaxRange { get; init; }
    public int RateOfFire { get; init; }
    public int Magazine { get; init; }

    /// <summary>Создать запись холодного оружия.</summary>
    public static Weapon CreateMelee(
        string key, string name, string damageFormula, WeaponDamageKind kind,
        int damageBase, DamageType damageType, string concealment, string? notes = null)
        => new()
        {
            Key = key,
            Name = name,
            DamageFormula = damageFormula,
            DamageFormulaKind = kind,
            DamageBase = damageBase,
            DamageType = damageType,
            Concealment = concealment,
            Notes = notes,
        };

    /// <summary>Создать запись дистанционного оружия.</summary>
    public static Weapon CreateRanged(
        string key, string name, int damageBase, int range, int maxRange,
        int rateOfFire, int magazine, string concealment, DamageType damageType, string? notes = null)
        => new()
        {
            Key = key,
            Name = name,
            DamageFormula = damageBase.ToString(),
            DamageFormulaKind = WeaponDamageKind.Fixed,
            DamageBase = damageBase,
            DamageType = damageType,
            Concealment = concealment,
            Range = range,
            MaxRange = maxRange,
            RateOfFire = rateOfFire,
            Magazine = magazine,
            Notes = notes,
        };
}
