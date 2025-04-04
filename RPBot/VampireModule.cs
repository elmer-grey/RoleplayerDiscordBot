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
        ["Дисциплины"] = new[] { "Протеанство", "Целестия", "Доминирование", "Онейрика",
                               "Викторианство", "Потенция", "Обскурация", "Анимализм",
                               "Обычай", "Кровная магия", "Демонония", "Химеризм" },
        ["Факты биографии"] = new[] { "Клан", "Поколение", "Причина принятия", "Первая смерть",
                                    "Дорога", "Убежище", "Союзники", "Враги",
                                    "Секрет", "Цель", "Слабость", "Достоинство" },
        ["Добродетели"] = new[] { "Совесть", "Самоконтроль", "Смелость" }
    };

    public class VampireCharacter
    {
        public string PlayerName { get; set; }
        public string CharacterName { get; set; }
        public string Clan { get; set; }
        public int Generation { get; set; } = 12;
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
        [Summary("character", "Имя персонажа")] string characterName)
    {
        await DeferAsync(ephemeral: true);

        if (!IsAllowedUser(Context.User))
        {
            await FollowupAsync("⛔ Доступ запрещен", ephemeral: true);
            return;
        }

        if (_characters.ContainsKey(playerName))
        {
            await FollowupAsync("❌ Персонаж для этого игрока уже существует", ephemeral: true);
            return;
        }

        _creationStates[Context.User.Id] = new CharacterCreationState
        {
            PlayerName = playerName,
            CharacterName = characterName
        };

        await ShowCategoryMenu();
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
    #endregion

    #region Интерактивное меню (исправленные методы)
    private async Task ShowCategoryMenu()
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId("vampire_category_select")
            .WithPlaceholder("Выберите категорию")
            .WithMaxValues(1);

        foreach (var category in AttributeCategories.Keys)
        {
            menu.AddOption(category, category);
        }

        var builder = new ComponentBuilder()
            .WithSelectMenu(menu)
            .WithButton("Готово", "vampire_finish", ButtonStyle.Success, row: 1)
            .WithButton("Отмена", "vampire_cancel", ButtonStyle.Danger, row: 1);

        // Используем ModifyOriginalResponseAsync для изменения исходного сообщения
        await ModifyOriginalResponseAsync(x =>
        {
            x.Content = "Выберите категорию параметров:";
            x.Components = builder.Build();
        });
    }

    [ComponentInteraction("vampire_category_select")]
    public async Task HandleCategorySelection(string[] selected)
    {
        var state = _creationStates[Context.User.Id];
        state.CurrentCategory = selected[0];
        state.CurrentPage = 0; // Сбрасываем страницу при выборе новой категории

        await RespondAsync("Обработка выбора...", ephemeral: true);
        await ShowParametersMenu();
    }

    private async Task ShowParametersMenu()
    {
        var state = _creationStates[Context.User.Id];
        var categoryParams = AttributeCategories[state.CurrentCategory];
        var pageSize = 5;

        var menu = new SelectMenuBuilder()
            .WithCustomId("vampire_param_select")
            .WithPlaceholder($"Выберите параметр ({state.CurrentCategory})")
            .WithMaxValues(1);

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
            builder.WithButton("< Назад", "vampire_page_prev", ButtonStyle.Secondary, row: 1);
        }

        if ((state.CurrentPage + 1) * pageSize < categoryParams.Length)
        {
            builder.WithButton("Вперед >", "vampire_page_next", ButtonStyle.Secondary, row: 1);
        }

        builder.WithButton("Назад к категориям", "vampire_back_categories", ButtonStyle.Primary, row: 2);

        // Всегда изменяем исходное сообщение
        await ModifyOriginalResponseAsync(x =>
        {
            x.Content = $"Выберите параметр ({state.CurrentCategory}, стр. {state.CurrentPage + 1})";
            x.Components = builder.Build();
        });
    }

    [ComponentInteraction("vampire_param_select")]
    public async Task HandleParamSelection(string[] selected)
    {
        var param = selected[0];
        var state = _creationStates[Context.User.Id];

        var builder = new ComponentBuilder();
        for (int i = 0; i <= 5; i++)
        {
            builder.WithButton(i.ToString(), $"vampire_set_{param}_{i}", ButtonStyle.Primary, row: i / 3);
        }

        builder.WithButton("Назад", "vampire_back_params", ButtonStyle.Secondary, row: 2);

        await ModifyOriginalResponseAsync(x =>
        {
            x.Content = $"Установите значение для {param} (0-5)";
            x.Components = builder.Build();
        });
    }

    [ComponentInteraction("vampire_set_*_*")]
    public async Task HandleSetValue(string param, string value)
    {
        var state = _creationStates[Context.User.Id];
        state.Parameters[param] = int.Parse(value);

        await RespondAsync($"Установлено значение {value} для {param}", ephemeral: true);
        await ShowParametersMenu();
    }

    [ComponentInteraction("vampire_finish")]
    public async Task HandleFinish()
    {
        var state = _creationStates[Context.User.Id];

        var character = new VampireCharacter
        {
            PlayerName = state.PlayerName,
            CharacterName = state.CharacterName,
            Attributes = state.Parameters
        };

        _characters[state.PlayerName] = character;
        SaveCharacters();
        _creationStates.TryRemove(Context.User.Id, out _);

        await ModifyOriginalResponseAsync(x =>
        {
            x.Content = $"✅ Персонаж {state.CharacterName} создан!";
            x.Components = new ComponentBuilder().Build(); // Убираем все компоненты
            x.Embed = BuildCharacterEmbed(character);
        });
    }

    [ComponentInteraction("vampire_cancel")]
    public async Task HandleCancel()
    {
        await DeferAsync(ephemeral: true);
        _creationStates.TryRemove(Context.User.Id, out _);
        await ModifyOriginalResponseAsync(x =>
        {
            x.Content = "❌ Создание персонажа отменено";
            x.Components = new ComponentBuilder().Build();
        });

        await FollowupAsync("Все введённые данные сброшены.", ephemeral: true);
    }

    [ComponentInteraction("vampire_back_categories")]
    public async Task HandleBackToCategories()
    {
        await RespondAsync("Возврат к категориям...", ephemeral: true);
        await ShowCategoryMenu();
    }

    [ComponentInteraction("vampire_back_params")]
    public async Task HandleBackToParams()
    {
        await RespondAsync("Возврат к параметрам...", ephemeral: true);
        await ShowParametersMenu();
    }

    [ComponentInteraction("vampire_page_prev")]
    public async Task HandleParamPagePrev()
    {
        var state = _creationStates[Context.User.Id];
        state.CurrentPage--;
        await ShowParametersMenu();
    }

    [ComponentInteraction("vampire_page_next")]
    public async Task HandleParamPageNext()
    {
        var state = _creationStates[Context.User.Id];
        state.CurrentPage++;
        await ShowParametersMenu();
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

    private Embed BuildCharacterEmbed(VampireCharacter character)
    {
        var embed = new EmbedBuilder()
            .WithTitle(character.CharacterName)
            .WithDescription($"Игрок: {character.PlayerName}\nКлан: {character.Clan ?? "Не указан"}\nПоколение: {character.Generation}")
            .WithColor(Color.DarkRed);

        foreach (var category in AttributeCategories)
        {
            var sb = new StringBuilder();
            foreach (var attr in category.Value)
            {
                int value = character.Attributes.GetValueOrDefault(attr, 0);
                int dots = (category.Key == "Физические" || category.Key == "Социальные" || category.Key == "Ментальные")
                    ? Math.Min(value + 1, 5)
                    : Math.Min(value, 5);

                sb.AppendLine($"{attr}: {new string('•', dots)}{new string('○', 5 - dots)}");
            }
            embed.AddField($"--- {category.Key} ---", sb.ToString(), true);
        }

        return embed.Build();
    }
    #endregion
}