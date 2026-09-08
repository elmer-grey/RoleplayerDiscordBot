using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot.SlashModules;

/// <summary>
/// Обработчик автокомплита для <c>/vampire_roll</c>.
/// <para>Discord жёстко ограничивает <c>SlashCommandOptionBuilder.Choices</c> до 25 элементов,
/// а всего в VtM V20 — 9 характеристик + 30 навыков + 28+ дисциплин. Поэтому все
/// текстовые опции зарегистрированы с <c>WithAutocomplete(true)</c> и подсказки
/// отдаются динамически через этот обработчик.</para>
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
    public static IReadOnlyList<AutocompleteResult> Suggest(string focusedOption, string userInput)
    {
        var candidates = BuildCandidates(focusedOption);
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
    private static IReadOnlyList<string>? BuildCandidates(string focusedOption)
    {
        return focusedOption switch
        {
            "характеристика" => VampireParameterCatalog.Characteristics.ToList(),
            "навык" => (IReadOnlyList<string>)VampireParameterCatalog
                .Talents
                .Concat(VampireParameterCatalog.Skills)
                .Concat(VampireParameterCatalog.Knowledges)
                .ToArray(),
            "дисциплина" => VampireParameterCatalog.AllDisciplines.ToList(),
            _ => null,
        };
    }

    /// <summary>
    /// Полный обработчик события <see cref="DiscordSocketClient.AutocompleteExecuted"/>.
    /// Привязывается из <c>Program.cs</c>.
    /// </summary>
    public static async Task HandleAsync(SocketAutocompleteInteraction interaction)
    {
        try
        {
            if (interaction?.Data?.CommandName != "vampire_roll")
                return; // нас не звали

            var focused = interaction.Data.Options.FirstOrDefault(o => o.Focused);
            if (focused == null) return;

            var results = Suggest(focused.Name, focused.Value?.ToString() ?? "");
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
