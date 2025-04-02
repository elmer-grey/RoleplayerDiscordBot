using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace RPBot
{
    public class CommandHandler
    {
        private readonly DiscordSocketClient _client;
        private readonly InteractionService _interactionService;
        private readonly IServiceProvider _services;
        private readonly List<ulong> GuildIDs;

        public CommandHandler(DiscordSocketClient client, IServiceProvider services)
        {
            _client = client;
            _services = services;
            _interactionService = new InteractionService(client.Rest);
            GuildIDs = new List<ulong> { 295189463376855040, 1288192593137635359 };
        }

        public async Task InitializeAsync()
        {
            await _interactionService.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
            _client.InteractionCreated += HandleInteraction;
            await RegisterCommandsAsync();
        }
        private async Task HandleInteraction(SocketInteraction interaction)
        {
            try
            {
                var context = new SocketInteractionContext(_client, interaction);
                await _interactionService.ExecuteCommandAsync(context, _services);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка выполнения команды: {ex}");
                if (interaction.Type == InteractionType.ApplicationCommand)
                {
                    await interaction.GetOriginalResponseAsync().ContinueWith(msg => msg.Result?.DeleteAsync());
                }
            }
        }

        public async Task ListAllCommandsAsync()
        {
            foreach (var guildId in GuildIDs)
            {
                var guild = _client.GetGuild(guildId);

                if (guild == null)
                {
                    Console.WriteLine($"Гильдия с ID {guildId} не найдена.");
                    continue;
                }

                var commands = await guild.GetApplicationCommandsAsync();
                Console.WriteLine($"\nКоманды на сервере {guild.Name}:");

                foreach (var cmd in commands)
                {
                    Console.WriteLine($"\n/{cmd.Name}: {cmd.Description}");

                    foreach (var option in cmd.Options)
                    {
                        Console.WriteLine($"  • {option.Name}: {option.Description}");
                        if (option.Options != null)
                        {
                            foreach (var subOption in option.Options)
                            {
                                Console.WriteLine($"    ◦ {subOption.Name}: {subOption.Description}");
                            }
                        }
                    }
                }
            }
        }

        private async Task RegisterCommandsAsync()
        {
            foreach (var guildId in GuildIDs)
            {
                var guild = _client.GetGuild(guildId);

                if (guild == null)
                {
                    Console.WriteLine($"Гильдия с ID {guildId} не найдена. Пропускаем регистрацию команд.");
                    continue;
                }

                // Основные команды
                var commands = new List<SlashCommandBuilder>
                {
                    new SlashCommandBuilder()
                        .WithName("help")
                        .WithDescription("Выводит список доступных команд."),

                    new SlashCommandBuilder()
                        .WithName("help_r")
                        .WithDescription("Выводит список команд для бросков кубов."),

                    new SlashCommandBuilder()
                        .WithName("help_gs")
                        .WithDescription("Выводит список команд для подсчёта времени игры."),

                    new SlashCommandBuilder()
                        .WithName("clr")
                        .WithDescription("Удаляет выбранное количество сообщений.")
                        .AddOption("input", ApplicationCommandOptionType.String, "Формат: Х, где Х - количество сообщений", isRequired: true),

                    new SlashCommandBuilder()
                        .WithName("serverinfo")
                        .WithDescription("Показывает информацию о сервере."),

                    new SlashCommandBuilder()
                        .WithName("bug_report")
                        .WithDescription("Сообщение об ошибке или предложение по улучшению.")
                        .AddOption("input", ApplicationCommandOptionType.String, "Описание проблемы или идеи", isRequired: true),

                    new SlashCommandBuilder()
                        .WithName("roll")
                        .WithDescription("Бросок кубика (формат: XdY)")
                        .AddOption("input", ApplicationCommandOptionType.String, "Пример: 2d20", isRequired: true),

                    new SlashCommandBuilder()
                        .WithName("roll20")
                        .WithDescription("Бросок d20"),

                    new SlashCommandBuilder()
                        .WithName("queue")
                        .WithDescription("Создание очереди")
                        .AddOption("input", ApplicationCommandOptionType.String, "Количество участников", isRequired: true),

                    new SlashCommandBuilder()
                        .WithName("q")
                        .WithDescription("Добавление в очередь")
                        .AddOption("input", ApplicationCommandOptionType.String, "Формат: dY", isRequired: true),

                    new SlashCommandBuilder()
                        .WithName("stop_q")
                        .WithDescription("Остановка очереди"),

                    new SlashCommandBuilder()
                        .WithName("start")
                        .WithDescription("Запуск игры")
                        .AddOption("game_name", ApplicationCommandOptionType.String, "Название игры", true)
                        .AddOption("master", ApplicationCommandOptionType.User, "Мастер игры", false)
                        .AddOption("comment", ApplicationCommandOptionType.String, "Комментарий", false),

                    new SlashCommandBuilder()
                        .WithName("pause")
                        .WithDescription("Приостановка игры"),

                    new SlashCommandBuilder()
                        .WithName("resume")
                        .WithDescription("Продолжение игры"),

                    new SlashCommandBuilder()
                        .WithName("stop")
                        .WithDescription("Остановка игры"),

                    new SlashCommandBuilder()
                        .WithName("close_chat")
                        .WithDescription("Закрытие чата/ветки")
                        .AddOption("reason", ApplicationCommandOptionType.String, "Причина закрытия", false),

                    new SlashCommandBuilder()
                        .WithName("open_chat")
                        .WithDescription("Открытие чата")
                        .AddOption("category", ApplicationCommandOptionType.String, "Категория", true)
                };

                // Вампирские команды
                var vampireCommand = new SlashCommandBuilder()
                    .WithName("vampire")
                    .WithDescription("Управление персонажами Vampire")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("create")
                        .WithDescription("Создать персонажа")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("player", ApplicationCommandOptionType.String, "Discord имя", true)
                        .AddOption("character", ApplicationCommandOptionType.String, "Имя персонажа", true)
                        .AddOption("parameters", ApplicationCommandOptionType.String, "Формат: Сила=3 Ловкость=2", false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("update")
                        .WithDescription("Изменить параметры")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("player", ApplicationCommandOptionType.String, "Discord имя", true)
                        .AddOption("changes", ApplicationCommandOptionType.String, "Формат: Сила+1 Ловкость=2", true))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("view")
                        .WithDescription("Просмотр персонажа")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("player", ApplicationCommandOptionType.String, "Discord имя", true)
                        .AddOption("public", ApplicationCommandOptionType.Boolean, "Видно всем", false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("delete")
                        .WithDescription("Удаление персонажа")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("player", ApplicationCommandOptionType.String, "Discord имя", true));

                commands.Add(vampireCommand);

                // Регистрация всех команд
                foreach (var command in commands)
                {
                    try
                    {
                        await guild.CreateApplicationCommandAsync(command.Build());
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка при регистрации команды {command.Name}: {ex.Message}");
                    }
                }
            }
        }
    }
}