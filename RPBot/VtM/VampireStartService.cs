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
                lines.Add("❌ Инициализация остановлена на шаге 1.");
                lines.Add("");
                lines.Add("**Шаг 1 — задать канал для VtM-бросков:**");
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
            lines.Add($"✅ Шаг 1 — VtMRollChannel задан: <#{VtMRollChannelIdSnapshot}>.");
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

        // ─── Шаг 0. Апгрейд JSON-справочников по schema_version. ─────
        // Делаем ДО всех остальных шагов, чтобы данные в памяти (на
        // следующих .Load()) были гарантированно актуальны. Не требует
        // VtMRollChannel и Discord API — можно прогнать даже с битым
        // конфигом.
        result.Upgrades.Add(("derangements", VampireDerangementCatalog.UpgradeIfStale()));
        result.Upgrades.Add(("clan_flaws", VampireClanFlawCatalog.UpgradeIfStale()));
        result.Upgrades.Add(("weapons", VampireWeaponsCatalog.UpgradeIfStale()));

        // ─── Шаг 1. Проверка VtMRollChannelID. ──────────────────────────
        if (cfg.VtMRollChannelID == 0UL)
        {
            result.StoppedAtVtMRollCheck = true;
            return result;
        }
        VtMRollChannelIdSnapshot = cfg.VtMRollChannelID;

        // ─── Шаг 2. Копирование шаблона словаря оружия. ──────────────────
        // Шаг 0 уже мог пересеять weapons.json — здесь делаем только
        // первый запуск (когда файла ещё не было вовсе). После Шага 0
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
        var testChar = BuildTestCharacter(command.User.Id);
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
        var rollEb = BuildTestRollEmbed(command.User);
        var ok = await VampireRollChannelPublisher.PublishAsync(command, rollEb);
        result.TestRollPosted = ok;
        if (!ok)
            result.ChannelName = "VtMRollChannel";

        return result;
    }

    private static VampireCharacter BuildTestCharacter(ulong ownerId) => new()
    {
        CharacterId = Guid.NewGuid(),
        PlayerId = ownerId,
        CharacterName = "Тестовый вампир",
        Clan = "Ventrue",
        Generation = 9,
        Nature = "Архитектор",
        Demeanor = "Архитектор",
        Concept = "Демонстрация листа",
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
