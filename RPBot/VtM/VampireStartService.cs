using System;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace RPBot.VtM;

/// <summary>
/// Пошаговая инициализация VtM-модуля на сервере. Вызывается из
/// <c>/vampire_start</c>:
/// <list type="number">
///   <item><b>Шаг 0.</b> Апгрейд JSON-справочников из embedded-ресурсов
///         (по полю <c>schema_version</c>): расстройства, клановые изъяны,
///         оружие. Если на диске файл старее embedded — пересеивается
///         целиком. Выполняется всегда, до всех остальных проверок —
///         гарантирует, что данные в памяти после первой загрузки будут
///         актуальны.</item>
///   <item>Проверка <see cref="ServerConfig.VtMRollChannelID"/>.
///         Если 0 — стоп, инструкция, как задать.</item>
///   <item>Копирование шаблона <c>weapons.json</c> из embedded-ресурса.</item>
///   <item>Отправка тестового персонажа в DM тому, кто вызвал команду.</item>
///   <item>Тестовый бросок в VtMRollChannel.</item>
/// </list>
/// </summary>
public static class VampireStartService
{
    public sealed class StartResult
    {
        /// <summary>Список событий по каталогам для шага 0 (диагностика).</summary>
        public System.Collections.Generic.List<(string Catalog, CatalogUpdater.UpgradeResult Result)> Upgrades { get; } = new();
        public bool StoppedAtVtMRollCheck { get; set; }
        public bool WeaponsSeeded { get; set; }
        public string? WeaponsPath { get; set; }
        public bool DmSent { get; set; }
        public bool TestRollPosted { get; set; }
        public string? ChannelName { get; set; }
        public string? Error { get; set; }

        public string ToHumanLines()
        {
            var lines = new System.Collections.Generic.List<string>();

            // Шаг 0 — отчёт об апгрейдах.
            if (Upgrades.Count > 0)
            {
                lines.Add("**Шаг 0 — апгрейд JSON-справочников:**");
                foreach (var (catalog, r) in Upgrades)
                {
                    var emoji = r switch
                    {
                        CatalogUpdater.UpgradeResult.Upgraded => "🔄",
                        CatalogUpdater.UpgradeResult.Seeded => "📥",
                        CatalogUpdater.UpgradeResult.UpToDate => "✅",
                        CatalogUpdater.UpgradeResult.DowngradeRefused => "🛡️",
                        CatalogUpdater.UpgradeResult.ResourceMissing => "⚠️",
                        _ => "•",
                    };
                    var word = r switch
                    {
                        CatalogUpdater.UpgradeResult.Upgraded => "обновлён",
                        CatalogUpdater.UpgradeResult.Seeded => "скопирован из шаблона",
                        CatalogUpdater.UpgradeResult.UpToDate => "актуален",
                        CatalogUpdater.UpgradeResult.DowngradeRefused => "на диске новее, оставлено",
                        CatalogUpdater.UpgradeResult.ResourceMissing => "embedded-ресурс не найден",
                        _ => r.ToString(),
                    };
                    lines.Add($"{emoji} `{catalog}` — {word}.");
                }
                lines.Add("");
            }

            if (StoppedAtVtMRollCheck)
            {
                lines.Add("❌ Инициализация остановлена на шаге 0.");
                lines.Add("");
                lines.Add("**Шаг 0 — задать канал для VtM-бросков:**");
                lines.Add("1. Создайте канал, куда бот будет публиковать броски.");
                lines.Add("2. В Discord ПКМ по каналу → «Копировать ID канала».");
                linesAddHelp(lines);
                lines.Add("После задания канала снова выполните `/vampire_start`.");
                return string.Join("\n", lines);
            }
            if (Error != null)
            {
                lines.Add($"❌ Ошибка: {Error}");
                return string.Join("\n", lines);
            }
            lines.Add($"✅ Шаг 0 — VtMRollChannel задан: <#{VtMRollChannelIdSnapshot}>.");
            if (WeaponsSeeded)
                lines.Add($"✅ Шаг 2 — словарь оружия скопирован: `{WeaponsPath}`.");
            else
                lines.Add($"ℹ️ Шаг 2 — словарь оружия уже был, путь: `{WeaponsPath}`.");
            lines.Add(DmSent
                ? "✅ Шаг 3 — тестовый персонаж отправлен в ЛС."
                : "⚠️ Шаг 3 — не удалось отправить ЛС (проверьте, что ЛС открыты).");
            if (TestRollPosted)
                lines.Add($"✅ Шаг 4 — тестовый бросок опубликован в <#{VtMRollChannelIdSnapshot}>.");
            else if (ChannelName != null)
                lines.Add($"⚠️ Шаг 4 — канал <#{VtMRollChannelIdSnapshot}> недоступен.");
            return string.Join("\n", lines);
        }

        private static void linesAddHelp(System.Collections.Generic.List<string> lines)
        {
            lines.Add("3. Выполните `/set_server_property key:vtm_roll_channel value:<ID>` "
                + "(или в BotUI: `set vtm_roll_channel <ID>`).");
            lines.Add("");
            lines.Add("> Подсказка: ID канала — это длинное число, например `123456789012345678`.");
        }
    }

    /// <summary>
    /// Технический снимок ID канала для отчёта. Заполняется внутри
    /// <see cref="RunAsync"/> до того, как сообщение уходит пользователю.
    /// </summary>
    public static ulong? VtMRollChannelIdSnapshot { get; private set; }

    /// <summary>
    /// Выполнить пошаговую инициализацию.
    /// </summary>
    /// <param name="command">Slash-команда, вызвавшая инициализацию.</param>
    /// <param name="cfg">Текущий <see cref="ServerConfig"/> для гильдии.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<StartResult> RunAsync(
        SocketSlashCommand command, ServerConfig cfg, System.Threading.CancellationToken ct)
    {
        var result = new StartResult();

        // ─── Шаг 0. Проверка VtMRollChannelID. ──────────────────────────
        // Без канала бот не сможет публиковать тестовый бросок на шаге 4 —
        // останавливаемся с подсказкой. Каталоги данных апгрейдим ПОСЛЕ
        // этой проверки (нужен хоть какой-то валидный cfg).
        if (cfg.VtMRollChannelID == 0UL)
        {
            result.StoppedAtVtMRollCheck = true;
            return result;
        }
        VtMRollChannelIdSnapshot = cfg.VtMRollChannelID;

        // ─── Шаг 1. Апгрейд JSON-справочников по schema_version. ─────
        // Делаем после Шага 0, но до Шага 2 (копирование оружия) —
        // чтобы данные в памяти (на следующих .Load()) были гарантированно
        // актуальны. Не требует Discord API — только диск.
        result.Upgrades.Add(("derangements", VampireDerangementCatalog.UpgradeIfStale()));
        result.Upgrades.Add(("clan_flaws", VampireClanFlawCatalog.UpgradeIfStale()));
        result.Upgrades.Add(("weapons", VampireWeaponsCatalog.UpgradeIfStale()));

        // ─── Шаг 2. Копирование шаблона словаря оружия. ──────────────────
        // Шаг 1 уже мог пересеять weapons.json — здесь делаем только
        // первый запуск (когда файла ещё не было вовсе). После Шага 1
        // файл всегда существует → WeaponsSeeded = false, что и нужно
        // для повторных запусков.
        var (seedOk, weaponsPath) = VampireWeaponsCatalog.EnsureSeeded();
        result.WeaponsSeeded = seedOk && !System.IO.File.Exists(weaponsPath);
        result.WeaponsPath = weaponsPath;
        if (!seedOk)
        {
            result.Error = "Не удалось скопировать шаблон weapons.json из ресурсов сборки.";
            return result;
        }

        // ─── Шаг 3. Тестовый персонаж в DM. ──────────────────────────────
        var dm = await command.User.CreateDMChannelAsync();
        var testChar = BuildTestCharacter(command.User);
        var eb = VampireSheetEmbed.Build(testChar);
        try
        {
            await dm.SendMessageAsync(
                "👋 Привет! Это **тестовый персонаж** — посмотри, как будет выглядеть лист.",
                embed: eb);
            result.DmSent = true;
        }
        catch (Discord.Net.HttpException)
        {
            result.DmSent = false;
        }

        // ─── Шаг 4. Тестовый бросок в VtMRollChannel. ───────────────────
        // Используем PublishForStartAsync вместо PublishAsync(command, ...),
        // потому что PublishAsync делает command.RespondAsync внутри,
        // а slash уже задеферен в VampireStartSlashModule — повторный
        // ack кидает InvalidOperationException
        // ("Cannot respond twice to the same interaction").
        var rollEb = BuildTestRollEmbed(command.User);
        var rollResult = await VampireRollChannelPublisher.PublishForStartAsync(
            command.GuildId!.Value, rollEb);
        result.TestRollPosted = rollResult.IsPublished;
        result.ChannelName = rollResult.IsPublished
            ? "VtMRollChannel"
            : rollResult.Status switch
            {
                VampireRollChannelPublisher.StartPublishStatus.ChannelNotConfigured =>
                    "VtMRollChannel (не задан)",
                VampireRollChannelPublisher.StartPublishStatus.ChannelUnavailable =>
                    $"VtMRollChannel (канал #{rollResult.ChannelId} недоступен)",
                VampireRollChannelPublisher.StartPublishStatus.ConfigMissing =>
                    "VtMRollChannel (конфиг не загружен)",
                VampireRollChannelPublisher.StartPublishStatus.SendFailed =>
                    $"VtMRollChannel (SendMessageAsync упал: {rollResult.ErrorMessage})",
                _ => "VtMRollChannel",
            };

        return result;
    }

    /// <summary>
    /// Собрать полностью заполненного тестового персонажа по правилам V20.
    /// Используется только для <c>/vampire_start</c> — игрок видит, как
    /// выглядит «настоящий» лист (атрибуты 7/5/3, способности 13/9/5,
    /// дисциплина, добродетели, факты, воля, человечность, здоровье).
    /// </summary>
    /// <remarks>
    /// <para>Стартовый набор (пример):</para>
    /// <list type="bullet">
    ///   <item>Клан Ventrue, поколение 9, природа/маска «Архитектор».</item>
    ///   <item>Атрибуты 7/5/3: Физ=7 (Сила 4, Ловкость 2, Стойкость 1),
    ///         Соц=5 (Харизма 2, Манипулирование 2, Внешность 1),
    ///         Мент=3 (Восприятие 1, Интеллект 1, Сообразительность 1).</item>
    ///   <item>Способности 13/9/5: Таланты=13 (по 4 в Атлетике, Бдительности,
    ///         Лидерстве; 1 в Запугивании), Навыки=9 (по 3 в Скрытности и
    ///         Этикете; по 1 в Вождении, Фехтовании, Ремесле),
    ///         Знания=5 (по 2 в Оккультизме и Расследовании; 1 в Политике).</item>
    ///   <item>Дисциплина: Доминирование 1 (клановый).</item>
    ///   <item>Добродетели: Совесть 2, Самоконтроль 2, Смелость 2.</item>
    ///   <item>Факты (5/5): Стая 2, Ресурсы 1, Союзники 1, Влияние 1.</item>
    ///   <item>Специализация: Ремесло → кузнечное дело.</item>
    ///   <item>Воля = Смелость = 2. Человечность = Совесть + Самоконтроль = 4.</item>
    ///   <item>Здоровье: 7 пустых ячеек (V20 шкала фиксирована).</item>
    ///   <item>Клановый изъян Ventrue: утончённый вкус.</item>
    /// </list>
    /// </remarks>
    private static VampireCharacter BuildTestCharacter(IUser owner) => new()
    {
        CharacterId = Guid.NewGuid(),
        PlayerId = owner.Id,
        PlayerName = owner.Username ?? "",
        CharacterName = "Тестовый вампир",
        Clan = "Вентру",
        Generation = 9,
        Nature = "Архитектор",
        Demeanor = "Архитектор",
        Concept = "Демонстрация листа VtM V20",
        Archetype = "Архитектор",
        Sire = "Сэр Арчибальд Монтегю",
        Weakness = "Утончённый вкус — Вентру не способен питаться кровью людей низкого социального положения.",

        // Атрибуты 7/5/3 (Физические/Социальные/Ментальные).
        AttributesStruct = new VampireAttributes
        {
            Strength = 4,
            Dexterity = 2,
            Stamina = 1,
            Charisma = 2,
            Manipulation = 2,
            Appearance = 1,
            Perception = 1,
            Intelligence = 1,
            Wits = 1,
        },
        AttributesPriority = "PhysicalFirst",

        // Способности 13/9/5 (Таланты/Навыки/Знания).
        AbilitiesStruct = new VampireAbilities
        {
            // Таланты (13): Атлетика 4, Бдительность 4, Лидерство 4, Запугивание 1.
            Атлетика = 4,
            Бдительность = 4,
            Драка = 0,
            Запугивание = 1,
            Красноречие = 0,
            Лидерство = 4,
            УличноеЧутьё = 0,
            Хитрость = 0,
            ШестоеЧувство = 0,
            Эмпатия = 0,
            // Навыки (9): Скрытность 3, Этикет 3, Вождение 1, Фехтование 1, Ремесло 1.
            Вождение = 1,
            Воровство = 0,
            Выживание = 0,
            Исполнение = 0,
            ОбращениеСЖивотными = 0,
            Ремесло = 1,
            Скрытность = 3,
            Стрельба = 0,
            Фехтование = 1,
            Этикет = 3,
            // Знания (5): Оккультизм 2, Расследование 2, Политика 1.
            ГуманитарныеНауки = 0,
            ЕстественныеНауки = 0,
            Информатика = 0,
            Медицина = 0,
            Оккультизм = 2,
            Политика = 1,
            Расследование = 2,
            Финансы = 0,
            Электроника = 0,
            Юриспруденция = 0,
        },
        AbilitiesPriority = "TalentsFirst",

        // Клановая дисциплина — Доминирование 1.
        Disciplines = new System.Collections.Generic.Dictionary<string, int>
        {
            ["Доминирование"] = 1,
        },

        // Добродетели (база 1/1/1 + 1 за приоритет поколения = 2/2/2).
        Virtues = new System.Collections.Generic.Dictionary<string, int>
        {
            [VampireParameterCatalog.VirtueConscience] = 2,
            [VampireParameterCatalog.VirtueSelfControl] = 2,
            [VampireParameterCatalog.VirtueCourage] = 2,
        },

        // Факты биографии (5 пунктов): Стая 2 + Ресурсы 1 + Союзники 1 + Влияние 1.
        Backgrounds = new System.Collections.Generic.Dictionary<string, int>
        {
            ["Стая"] = 2,
            ["Ресурсы"] = 1,
            ["Союзники"] = 1,
            ["Влияние"] = 1,
        },

        // Одна специализация, чтобы в листе был виден формат «способность: сфера».
        Specializations = new System.Collections.Generic.Dictionary<string, string>
        {
            ["Ремесло"] = "кузнечное дело",
        },

        // Hunger (V20): стартует с 1; на Шаге 5 не редактируется.
        Hunger = 1,

        // Здоровье: V20 шкала фиксирована на 7 ячеек. Инициализируем явно,
        // чтобы первый же бросок (/vampire_roll и т. п.) не падал с
        // "здоровье не инициализировано".
        Health = new HealthState(VampireFinishingResolver.HealthTrackSize),
    };

    private static Embed BuildTestRollEmbed(IUser user)
    {
        var eb = new EmbedBuilder
        {
            Title = "🎲 Тестовый бросок",
            Color = new Color(0x808080),
            Description =
                $"Это автоматический тестовый бросок, отправленный {user.Mention} " +
                $"через `/vampire_start`. Если ты это видишь — VtM-модуль успешно инициализирован.",
        };
        eb.AddField("Кто", user.Username, inline: true);
        eb.AddField("Когда", DateTimeOffset.UtcNow.ToString("u"), inline: true);
        eb.Footer = new EmbedFooterBuilder
        {
            Text = "VtM V20 /vampire_start — проверка канала VtMRollChannelID.",
        };
        return eb.Build();
    }
}
