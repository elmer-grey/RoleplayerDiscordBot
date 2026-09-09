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
    public static IReadOnlyList<AutocompleteResult> Suggest(string commandName, string focusedOption, string userInput)
    {
        var candidates = BuildCandidates(commandName, focusedOption);
        if (candidates == null) return Array.Empty<AutocompleteResult>();

        if (string.IsNullOrWhiteSpace(userInput))
        {
            return candidates.Take(DiscordAutocompleteLimit)
                .Select(name => new AutocompleteResult(name, name))
                .ToList();
        }

        var lowered = userInput.Trim();
        return candidates
            .Where(name => name.StartsWith(lowered, StringComparison.OrdinalIgnoreCase))
            .Take(DiscordAutocompleteLimit)
            .Select(name => new AutocompleteResult(name, name))
            .ToList();
    }

    /// <summary>
    /// Возвращает список имён для указанного поля. Если поле неизвестно — <c>null</c>.
    /// </summary>
    private static IReadOnlyList<string>? BuildCandidates(string commandName, string focusedOption)
    {
        return (commandName, focusedOption) switch
        {
            ("vampire_roll", "характеристика") => VampireParameterCatalog.Characteristics.ToList(),
            ("vampire_roll", "навык") => (IReadOnlyList<string>)VampireParameterCatalog
                .Talents
                .Concat(VampireParameterCatalog.Skills)
                .Concat(VampireParameterCatalog.Knowledges)
                .ToArray(),
            ("vampire_roll", "дисциплина") => VampireParameterCatalog.AllDisciplines.ToList(),
            ("vampire_damage", "манёвр") => BuildManeuverCandidates(),
            ("vampire_damage", "оружие") => BuildWeaponCandidates(),
            _ => null,
        };
    }

    /// <summary>
    /// Все манёвры из каталога (ближний + дистанционный бой), отсортированные по имени.
    /// </summary>
    private static IReadOnlyList<string> BuildManeuverCandidates()
    {
        var names = new List<string>();
        foreach (var m in VampireManeuverCatalog.Melee) names.Add(m.Name);
        foreach (var m in VampireManeuverCatalog.Ranged) names.Add(m.Name);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Всё оружие из каталога, отсортированное по имени.
    /// </summary>
    private static IReadOnlyList<string> BuildWeaponCandidates()
    {
        var names = new List<string>();
        foreach (var w in VampireManeuverCatalog.MeleeWeapons) names.Add(w.Name);
        foreach (var w in VampireManeuverCatalog.RangedWeapons) names.Add(w.Name);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Полный обработчик события <see cref="DiscordSocketClient.AutocompleteExecuted"/>.
    /// Привязывается из <c>Program.cs</c>.
    /// </summary>
    public static async Task HandleAsync(SocketAutocompleteInteraction interaction)
    {
        try
        {
            if (interaction?.Data?.CommandName is not ("vampire_roll" or "vampire_damage"))
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
