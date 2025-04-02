using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

[Group("vampire", "Управление персонажем Vampire: The Masquerade")]
public class VampireModule : InteractionModuleBase<SocketInteractionContext>
{
    private const string StorageFile = "vampire_characters.json";
    private static Dictionary<string, VampireCharacter> _characters = LoadCharacters();
    private static readonly ConcurrentDictionary<ulong, CharacterCreationState> _creationStates = new();

    public static readonly Dictionary<string, string[]> AttributeCategories = new()
    {
        ["Физические"] = new[] { "Сила", "Ловкость", "Выносливость" },
        ["Социальные"] = new[] { "Обаяние", "Манипуляция", "Привлекательность" },
        ["Ментальные"] = new[] { "Восприятие", "Интеллект", "Смекалка" },
        ["Таланты"] = new[] { "Атлетика", "Бдительность", "Драка", "Красноречие", "Лидерство",
                              "Уличное чутьё", "Хитрость", "Шестое чувство", "Эмпатия" },
        ["Навыки"] = new[] { "Вождение", "Воровство", "Выживание", "Исполнение", "Обращение с животными",
                             "Ремесло", "Скрытность", "Стрельба", "Фехтование", "Этикет" },
        ["Знания"] = new[] { "Гуманитарные науки", "Естественные науки", "Законы", "Информатика", "Медицина",
                             "Оккультизм", "Политика", "Расследование", "Финансы", "Электроника" },
        ["Дисциплины"] = new[] { "Дисциплина 1", "Дисциплина 2", "Дисциплина 3", "Дисциплина 4", "Дисциплина 5",
                                 "Дисциплина 6", "Дисциплина 7", "Дисциплина 8", "Дисциплина 9", "Дисциплина 10",
                                 "Дисциплина 11", "Дисциплина 12", "Дисциплина 13", "Дисциплина 14", "Дисциплина 15",
                                 "Дисциплина 16", "Дисциплина 17" },
        ["Факты биографии"] = new[] { "Факт 1", "Факт 2", "Факт 3", "Факт 4", "Факт 5",
                                      "Факт 6", "Факт 7", "Факт 8", "Факт 9", "Факт 10",
                                      "Факт 11", "Факт 12" },
        ["Добродетели"] = new[] { "Совесть", "Самоконтроль", "Смелость" }
    };

    public class VampireCharacter
    {
        public string PlayerName { get; set; }
        public string CharacterName { get; set; }
        public Dictionary<string, int> Attributes { get; set; } = new();
    }

    private class CharacterCreationState
    {
        public string PlayerName { get; set; }
        public string CharacterName { get; set; }
        public Dictionary<string, int> Parameters { get; } = new();
        public string CurrentCategory { get; set; }
        public int CurrentPage { get; set; } = 0;
    }

    #region Основные команды
    [SlashCommand("create", "Создать персонажа")]
    public async Task CreateCharacter(
        [Summary("player", "Discord имя игрока")] string playerName,
        [Summary("character", "Имя персонажа")] string characterName,
        [Summary("parameters", "Формат: Сила=3 Ловкость=2"), Autocomplete(typeof(ParamAutocompleteHandler))]
        string parameters = null)
    {
        await DeferAsync(ephemeral: true);

        if (!IsAllowedUser(Context.User))
        {
            await FollowupAsync("⛔ Доступ запрещен", ephemeral: true);
            return;
        }

        if (!string.IsNullOrEmpty(parameters))
        {
            var parsedParams = ParseParameters(parameters);
            await CreateCharacterAsync(playerName, characterName, parsedParams);
        }
        else
        {
            _creationStates[Context.User.Id] = new CharacterCreationState
            {
                PlayerName = playerName,
                CharacterName = characterName
            };
            await ShowCategoryMenu(Context.User.Id);
        }
    }

    [SlashCommand("delete", "Удалить персонажа")]
    public async Task DeleteCharacter(
        [Summary("player", "Discord имя игрока")] string playerName)
    {
        await DeferAsync(ephemeral: true);

        if (!IsAllowedUser(Context.User))
        {
            await FollowupAsync("⛔ Доступ запрещен", ephemeral: true);
            return;
        }

        if (_characters.Remove(playerName))
        {
            SaveCharacters();
            await FollowupAsync($"✅ Персонаж {playerName} удален");
        }
        else
        {
            await FollowupAsync("❌ Персонаж не найден");
        }
    }

    [SlashCommand("view", "Просмотреть персонажа")]
    public async Task ViewCharacter(
        [Summary("player", "Discord имя игрока")] string playerName,
        [Summary("public", "Видно всем")] bool isPublic = false)
    {
        await DeferAsync(!isPublic);

        if (!_characters.TryGetValue(playerName, out var character))
        {
            await FollowupAsync("❌ Персонаж не найден", ephemeral: true);
            return;
        }

        await FollowupAsync(embed: BuildCharacterEmbed(character), ephemeral: !isPublic);
    }

    [SlashCommand("update", "Изменить параметры")]
    public async Task UpdateCharacter(
        [Summary("player", "Discord имя игрока")] string playerName,
        [Summary("changes", "Формат: Сила+1 Ловкость=2"), Autocomplete(typeof(ParamAutocompleteHandler))]
        string changes)
    {
        await DeferAsync(ephemeral: true);

        if (!IsAllowedUser(Context.User))
        {
            await FollowupAsync("⛔ Доступ запрещен", ephemeral: true);
            return;
        }

        if (!_characters.TryGetValue(playerName, out var character))
        {
            await FollowupAsync("❌ Персонаж не найден", ephemeral: true);
            return;
        }

        var updates = ParseChanges(changes);
        foreach (var update in updates)
        {
            character.Attributes[update.Key] = update.Value;
        }

        SaveCharacters();
        await FollowupAsync($"✅ Параметры обновлены", embed: BuildCharacterEmbed(character));
    }
    #endregion

    #region Интерактивное меню
    private async Task ShowCategoryMenu(ulong userId)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId("vampire:select_category")
            .WithPlaceholder("Выберите категорию");

        foreach (var category in AttributeCategories.Keys)
        {
            menu.AddOption(category, category);
        }

        var builder = new ComponentBuilder()
            .WithSelectMenu(menu)
            .WithButton("Готово", "vampire:finish", ButtonStyle.Success)
            .WithButton("Отмена", "vampire:cancel", ButtonStyle.Danger);

        await ModifyOriginalResponseAsync(x =>
        {
            x.Content = "Выберите категорию параметров:";
            x.Components = builder.Build();
        });
    }

    [ComponentInteraction("vampire:select_category")]
    public async Task HandleCategorySelection(string[] selected)
    {
        var category = selected.First();
        var state = _creationStates[Context.User.Id];
        state.CurrentCategory = category;
        state.CurrentPage = 0;
        await ShowParametersMenu(Context.User.Id);
    }

    private async Task ShowParametersMenu(ulong userId)
    {
        var state = _creationStates[userId];
        var categoryParams = AttributeCategories[state.CurrentCategory];
        var pageSize = 5;

        var menu = new SelectMenuBuilder()
            .WithCustomId("vampire:select_param")
            .WithPlaceholder($"Выберите параметр ({state.CurrentCategory})");

        var currentPageItems = categoryParams
            .Skip(state.CurrentPage * pageSize)
            .Take(pageSize);

        foreach (var param in currentPageItems)
        {
            menu.AddOption(param, param);
        }

        var builder = new ComponentBuilder()
            .WithSelectMenu(menu);

        if (state.CurrentPage > 0)
        {
            builder.WithButton("< Назад", "vampire:param_page_prev", ButtonStyle.Secondary);
        }

        if ((state.CurrentPage + 1) * pageSize < categoryParams.Length)
        {
            builder.WithButton("Вперед >", "vampire:param_page_next", ButtonStyle.Secondary, row: 1);
        }

        builder.WithButton("Назад к категориям", "vampire:back_to_categories", ButtonStyle.Primary, row: 1);

        await ModifyOriginalResponseAsync(x =>
        {
            x.Content = $"Выберите параметр ({state.CurrentCategory}, стр. {state.CurrentPage + 1}):";
            x.Components = builder.Build();
        });
    }

    [ComponentInteraction("vampire:select_param")]
    public async Task HandleParamSelection(string[] selected)
    {
        var param = selected.First();
        var state = _creationStates[Context.User.Id];

        var builder = new ComponentBuilder();
        for (int i = 0; i <= 5; i++)
        {
            builder.WithButton(i.ToString(), $"vampire:set_value:{param}:{i}", ButtonStyle.Primary);
        }

        await ModifyOriginalResponseAsync(x =>
        {
            x.Content = $"Установите значение для {param} (0-5)";
            x.Components = builder
                .WithButton("Назад", "vampire:back_to_params", ButtonStyle.Secondary)
                .Build();
        });
    }

    [ComponentInteraction("vampire:set_value:*:*")]
    public async Task HandleSetValue(string param, string value)
    {
        var state = _creationStates[Context.User.Id];
        state.Parameters[param] = int.Parse(value);
        await ShowParametersMenu(Context.User.Id);
    }

    [ComponentInteraction("vampire:finish")]
    public async Task HandleFinish()
    {
        var state = _creationStates[Context.User.Id];
        await CreateCharacterAsync(state.PlayerName, state.CharacterName, state.Parameters);
        _creationStates.TryRemove(Context.User.Id, out _);
    }

    [ComponentInteraction("vampire:cancel")]
    public async Task HandleCancel()
    {
        _creationStates.TryRemove(Context.User.Id, out _);
        await ModifyOriginalResponseAsync(x =>
        {
            x.Content = "Создание персонажа отменено";
            x.Components = new ComponentBuilder().Build();
        });
    }
    #endregion

    #region Вспомогательные методы
    private static Dictionary<string, VampireCharacter> LoadCharacters()
    {
        try
        {
            if (File.Exists(StorageFile))
            {
                var json = File.ReadAllText(StorageFile);
                return JsonSerializer.Deserialize<Dictionary<string, VampireCharacter>>(json) ?? new();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка загрузки персонажей: {ex.Message}");
        }
        return new();
    }

    private static void SaveCharacters()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_characters, options);
            File.WriteAllText(StorageFile, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка сохранения персонажей: {ex.Message}");
        }
    }

    private bool IsAllowedUser(SocketUser user)
    {
        string username = user.Username;
        return username == "domen_" || username == "perekrestok_mirov";
    }

    private async Task CreateCharacterAsync(string playerName, string characterName, Dictionary<string, int> parameters)
    {
        if (_characters.ContainsKey(playerName))
        {
            await FollowupAsync("❌ Персонаж для этого игрока уже существует", ephemeral: true);
            return;
        }

        var character = new VampireCharacter
        {
            PlayerName = playerName,
            CharacterName = characterName,
            Attributes = parameters
        };

        _characters[playerName] = character;
        SaveCharacters();
        await FollowupAsync($"✅ Персонаж {characterName} создан", embed: BuildCharacterEmbed(character));
    }

    private Embed BuildCharacterEmbed(VampireCharacter character)
    {
        var embed = new EmbedBuilder()
            .WithTitle(character.CharacterName)
            .WithDescription($"Игрок: {character.PlayerName}")
            .WithColor(Color.DarkRed);

        // Характеристики (3 колонки)
        var physSb = new StringBuilder();
        var socSb = new StringBuilder();
        var mentSb = new StringBuilder();

        foreach (var attr in AttributeCategories["Физические"])
        {
            int value = character.Attributes.GetValueOrDefault(attr, 0);
            int dots = Math.Min(value + 1, 5); // +1 базовая точка для характеристик
            physSb.AppendLine($"{attr}: {new string('•', dots)}{new string('○', 5 - dots)}");
        }

        foreach (var attr in AttributeCategories["Социальные"])
        {
            int value = character.Attributes.GetValueOrDefault(attr, 0);
            int dots = Math.Min(value + 1, 5); // +1 базовая точка для характеристик
            socSb.AppendLine($"{attr}: {new string('•', dots)}{new string('○', 5 - dots)}");
        }

        foreach (var attr in AttributeCategories["Ментальные"])
        {
            int value = character.Attributes.GetValueOrDefault(attr, 0);
            int dots = Math.Min(value + 1, 5); // +1 базовая точка для характеристик
            mentSb.AppendLine($"{attr}: {new string('•', dots)}{new string('○', 5 - dots)}");
        }

        embed.AddField("--- Физические ---", physSb.ToString(), true);
        embed.AddField("--- Социальные ---", socSb.ToString(), true);
        embed.AddField("--- Ментальные ---", mentSb.ToString(), true);

        // Атрибуты (3 колонки)
        var talentsSb = new StringBuilder();
        var skillsSb = new StringBuilder();
        var knowledgesSb = new StringBuilder();

        foreach (var attr in AttributeCategories["Таланты"])
        {
            int value = character.Attributes.GetValueOrDefault(attr, 0);
            int dots = Math.Min(value, 5);
            talentsSb.AppendLine($"{attr}: {new string('•', dots)}{new string('○', 5 - dots)}");
        }

        foreach (var attr in AttributeCategories["Навыки"])
        {
            int value = character.Attributes.GetValueOrDefault(attr, 0);
            int dots = Math.Min(value, 5);
            skillsSb.AppendLine($"{attr}: {new string('•', dots)}{new string('○', 5 - dots)}");
        }

        foreach (var attr in AttributeCategories["Знания"])
        {
            int value = character.Attributes.GetValueOrDefault(attr, 0);
            int dots = Math.Min(value, 5);
            knowledgesSb.AppendLine($"{attr}: {new string('•', dots)}{new string('○', 5 - dots)}");
        }

        embed.AddField("--- Таланты ---", talentsSb.ToString(), true);
        embed.AddField("--- Навыки ---", skillsSb.ToString(), true);
        embed.AddField("--- Знания ---", knowledgesSb.ToString(), true);

        // Преимущества (3 колонки)
        var disciplinesSb = new StringBuilder();
        var factsSb = new StringBuilder();
        var virtuesSb = new StringBuilder();

        foreach (var attr in AttributeCategories["Дисциплины"])
        {
            int value = character.Attributes.GetValueOrDefault(attr, 0);
            int dots = Math.Min(value, 5);
            disciplinesSb.AppendLine($"{attr}: {new string('•', dots)}{new string('○', 5 - dots)}");
        }

        foreach (var attr in AttributeCategories["Факты биографии"])
        {
            int value = character.Attributes.GetValueOrDefault(attr, 0);
            int dots = Math.Min(value, 5);
            factsSb.AppendLine($"{attr}: {new string('•', dots)}{new string('○', 5 - dots)}");
        }

        foreach (var attr in AttributeCategories["Добродетели"])
        {
            int value = character.Attributes.GetValueOrDefault(attr, 0);
            int dots = Math.Min(value, 5);
            virtuesSb.AppendLine($"{attr}: {new string('•', dots)}{new string('○', 5 - dots)}");
        }

        embed.AddField("--- Дисциплины ---", disciplinesSb.ToString(), true);
        embed.AddField("--- Факты биографии ---", factsSb.ToString(), true);
        embed.AddField("--- Добродетели ---", virtuesSb.ToString(), true);

        return embed.Build();
    }

    private Dictionary<string, int> ParseParameters(string input)
    {
        var result = new Dictionary<string, int>();
        if (string.IsNullOrWhiteSpace(input)) return result;

        var pairs = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=');
            if (parts.Length == 2)
            {
                var paramName = AttributeCategories
                    .SelectMany(c => c.Value)
                    .FirstOrDefault(p => p.Equals(parts[0], StringComparison.OrdinalIgnoreCase));

                if (paramName != null && int.TryParse(parts[1], out int value) && value >= 0 && value <= 5)
                {
                    result[paramName] = value;
                }
            }
        }
        return result;
    }

    private Dictionary<string, int> ParseChanges(string input)
    {
        var result = new Dictionary<string, int>();
        if (string.IsNullOrWhiteSpace(input)) return result;

        var operations = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var op in operations)
        {
            var parts = op.Split(new[] { '=', '+', '-' }, 2);
            if (parts.Length == 2)
            {
                var paramName = AttributeCategories
                    .SelectMany(c => c.Value)
                    .FirstOrDefault(p => p.Equals(parts[0], StringComparison.OrdinalIgnoreCase));

                if (paramName != null && int.TryParse(parts[1], out int value))
                {
                    int currentValue = _characters.TryGetValue(Context.User.Username, out var character) &&
                                      character.Attributes.TryGetValue(paramName, out var charValue)
                        ? charValue : 0;

                    if (op.Contains('='))
                    {
                        result[paramName] = Math.Clamp(value, 0, 5);
                    }
                    else if (op.Contains('+'))
                    {
                        result[paramName] = Math.Clamp(currentValue + value, 0, 5);
                    }
                    else if (op.Contains('-'))
                    {
                        result[paramName] = Math.Clamp(currentValue - value, 0, 5);
                    }
                }
            }
        }
        return result;
    }
    #endregion
}

public class ParamAutocompleteHandler : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        var input = autocompleteInteraction.Data.Current.Value.ToString();
        var results = new List<AutocompleteResult>();

        foreach (var category in VampireModule.AttributeCategories)
        {
            foreach (var param in category.Value)
            {
                if (param.Contains(input, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new AutocompleteResult($"{param} ({category.Key})", param));
                }
            }
        }

        return AutocompletionResult.FromSuccess(results.Take(25));
    }
}