using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot.SlashModules;

/// <summary>
/// Обработчик автокомплита для VtM slash-команд.
/// <para>Discord жёстко ограничивает <c>SlashCommandOptionBuilder.Choices</c> до 25 элементов,
/// поэтому все текстовые опции зарегистрированы с <c>WithAutocomplete(true)</c> и подсказки
/// отдаются динамически через этот обработчик.</para>
/// <para>Обслуживает команды:
/// <list type="bullet">
///   <item><c>/vampire_roll</c> — поля «характеристика», «навык», «дисциплина».</item>
///   <item><c>/vampire_damage</c> — поля «манёвр», «оружие».</item>
///   <item><c>/vampire_maneuver</c> — поле <c>name</c>.</item>
///   <item><c>/vampire_weapon</c> — поле <c>name</c>.</item>
///   <item><c>/vampire_armor</c> — поле <c>name</c>.</item>
/// </list>
/// </para>
/// <para>Подключение: <c>Program.cs</c> подписывается на
/// <c>DiscordSocketClient.AutocompleteExecuted</c> и делегирует событие сюда.</para>
/// </summary>
public static class VampireRollAutocompleteHandler
{
    /// <summary>Жёсткий лимит Discord на подсказки в одном автокомплите.</summary>
    private const int DiscordAutocompleteLimit = 25;

    /// <summary>
    /// Вернуть до 25 подсказок для текущего поля.
    /// </summary>
    /// <remarks>
    /// Для большинства полей возвращаются обычные <see cref="AutocompleteResult"/>
    /// с одинаковыми name/value. Для справкичных команд (<c>/vampire_maneuver</c>,
    /// <c>/vampire_weapon</c>, <c>/vampire_armor</c>) возвращаются пары
    /// (label = русское название, value = slug), потому что обработчики команд
    /// ищут записи по <c>Key</c> (см. <see cref="VampireManeuverCatalog.FindMelee"/>
    /// и т. п.), а пользователь видит в подсказках человекочитаемое имя.
    /// </remarks>
    public static IReadOnlyList<AutocompleteResult> Suggest(string commandName, string focusedOption, string userInput)
    {
        var candidates = BuildCandidates(commandName, focusedOption);
        if (candidates == null) return Array.Empty<AutocompleteResult>();

        if (string.IsNullOrWhiteSpace(userInput))
        {
            return candidates.Take(DiscordAutocompleteLimit)
                .Select(BuildResult)
                .ToList();
        }

        var lowered = userInput.Trim();
        return candidates
            .Where(c => Matches(c, lowered))
            .Take(DiscordAutocompleteLimit)
            .Select(BuildResult)
            .ToList();
    }

    private static AutocompleteResult BuildResult(ChoiceCandidate c) =>
        new AutocompleteResult(c.Label, c.Value);

    private static bool Matches(ChoiceCandidate c, string lowered)
    {
        // Совпадение по value (slug) — для тех, кто знает точное название ключа.
        if (c.Value.StartsWith(lowered, StringComparison.OrdinalIgnoreCase)) return true;
        // И по label (русскому названию) — для обычного набора из автокомплита.
        if (c.Label.StartsWith(lowered, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Пара «label → value» для подсказки. У большинства полей они совпадают,
    /// у справкичных команд различаются (русское имя → slug).
    /// </summary>
    private sealed record ChoiceCandidate(string Label, string Value);

    /// <summary>
    /// Возвращает список пар (label, value) для указанного поля. Если поле
    /// неизвестно — <c>null</c>.
    /// </summary>
    private static IReadOnlyList<ChoiceCandidate>? BuildCandidates(string commandName, string focusedOption)
    {
        return (commandName, focusedOption) switch
        {
            ("vampire_roll", "характеристика") => Wrap(VampireParameterCatalog.Characteristics),
            ("vampire_roll", "навык") => Wrap(
                VampireParameterCatalog.Talents
                    .Concat(VampireParameterCatalog.Skills)
                    .Concat(VampireParameterCatalog.Knowledges)
                    .ToArray()),
            ("vampire_roll", "дисциплина") => Wrap(VampireParameterCatalog.AllDisciplines),
            ("vampire_damage", "манёвр") => BuildManeuverCandidates(),
            ("vampire_damage", "оружие") => BuildWeaponCandidates(),
            ("vampire_maneuver", "name") => BuildManeuverCandidates(),
            ("vampire_weapon", "name") => BuildWeaponCandidates(),
            ("vampire_armor", "name") => BuildArmorCandidates(),
            _ => null,
        };
    }

    /// <summary>
    /// Оборачивает плоский список имён в <see cref="ChoiceCandidate"/>,
    /// где label == value (используется для всех полей, кроме справкичных команд).
    /// </summary>
    private static IReadOnlyList<ChoiceCandidate> Wrap(IEnumerable<string> names) =>
        names.Select(n => new ChoiceCandidate(n, n)).ToList();

    /// <summary>
    /// Все манёвры из каталога (ближний + дистанционный бой), отсортированные по имени.
    /// label = русское название, value = slug.
    /// </summary>
    private static IReadOnlyList<ChoiceCandidate> BuildManeuverCandidates()
    {
        var candidates = new List<ChoiceCandidate>();
        foreach (var m in VampireManeuverCatalog.Melee)
            candidates.Add(new ChoiceCandidate(m.Name, m.Key));
        foreach (var m in VampireManeuverCatalog.Ranged)
            candidates.Add(new ChoiceCandidate(m.Name, m.Key));
        candidates.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return candidates;
    }

    /// <summary>
    /// Всё оружие из каталога, отсортированное по имени. label = русское название,
    /// value = slug.
    /// </summary>
    private static IReadOnlyList<ChoiceCandidate> BuildWeaponCandidates()
    {
        var candidates = new List<ChoiceCandidate>();
        foreach (var w in VampireManeuverCatalog.MeleeWeapons)
            candidates.Add(new ChoiceCandidate(w.Name, w.Key));
        foreach (var w in VampireManeuverCatalog.RangedWeapons)
            candidates.Add(new ChoiceCandidate(w.Name, w.Key));
        candidates.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return candidates;
    }

    /// <summary>
    /// Все классы брони из каталога, отсортированные по имени. label = русское
    /// название, value = slug.
    /// </summary>
    private static IReadOnlyList<ChoiceCandidate> BuildArmorCandidates()
    {
        var candidates = new List<ChoiceCandidate>();
        foreach (var a in VampireManeuverCatalog.Armor)
            candidates.Add(new ChoiceCandidate(a.Name, a.Key));
        candidates.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return candidates;
    }

    /// <summary>
    /// Полный обработчик события <see cref="DiscordSocketClient.AutocompleteExecuted"/>.
    /// Привязывается из <c>Program.cs</c>.
    /// </summary>
    public static async Task HandleAsync(SocketAutocompleteInteraction interaction)
    {
        try
        {
            if (interaction?.Data?.CommandName is not (
                "vampire_roll" or "vampire_damage" or
                "vampire_maneuver" or "vampire_weapon" or "vampire_armor"))
                return; // нас не звали

            var focused = interaction.Data.Options.FirstOrDefault(o => o.Focused);
            if (focused == null) return;

            var results = Suggest(interaction.Data.CommandName, focused.Name, focused.Value?.ToString() ?? "");
            await interaction.RespondAsync(results);
        }
        catch (Exception ex)
        {
            // Автокомплит не должен ронять бота — просто молча игнорируем.
            BotLogger.Error(
                LogCategory.Cmd,
                $"[Autocomplete] {ex.GetType().Name}: {ex.Message}");
        }
    }
}
