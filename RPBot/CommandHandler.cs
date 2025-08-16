using Discord;
using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Threading.Tasks;
namespace RPBot
{
    public class CommandHandler
    {
        private readonly DiscordSocketClient _client;
        private readonly CommandService _commandService;
        private readonly List<ulong> GuildIDs; // Список идентификаторов гильдий

        public CommandHandler(DiscordSocketClient client, CommandService commands, IServiceProvider services)
        {
            // Идентификаторы гильдий, на которых будет работать бот
            GuildIDs = new List<ulong>
            {
                295189463376855040, // Канал "КнР"
                1288192593137635359  // Канал "Тест"
            };

            _client = client;
            _commandService = new CommandService();
        }

        public async Task InitializeAsync()
        {
            await RegisterCommandsAsync();
        }

        public async Task ListSlashCommandsAsync()
        {
            foreach (var guildId in GuildIDs)
            {
                var guild = _client.GetGuild(guildId);

                if (guild == null)
                {
                    Console.WriteLine($"Не удалось получить гильдию с ID: {guildId}. Пропускаем вывод команд для этой гильдии.");
                    continue;
                }

                var commands = await _client.GetGuild(guildId).GetApplicationCommandsAsync();

                foreach (var command in commands)
                {
                    Console.WriteLine($"Гильдия: {guildId}, Команда: {command.Name}, ID: {command.Id}");
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
                    Console.WriteLine($"Не удалось получить гильдию с ID: {guildId}. Пропускаем регистрацию команд для этой гильдии.");
                    continue;
                }

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
                .WithDescription("Выводит список команд, которые используются для подсчёта времени игры."),
                    new SlashCommandBuilder()
                .WithName("clr")
                .WithDescription("Удаляет выбранное количество сообщений.")
                .AddOption("input", ApplicationCommandOptionType.String, "Формат: Х, где Х - количество сообщений, которые нужно удалить", isRequired: true),
                    new SlashCommandBuilder()
                .WithName("serverinfo")
                .WithDescription("Показывает информацию о текущем сервере."),
                    new SlashCommandBuilder()
                .WithName("bug_report")
                .WithDescription("Отправка сообщения об ошибке, которая связана с ботом, или любое предложение по его улучшению.")
                .AddOption("input", ApplicationCommandOptionType.String, "Введите описание ошибки бота или ваше предложение, которое можно реализовать", isRequired: true),
                    new SlashCommandBuilder()
                .WithName("roll")
                .WithDescription("Выполняет бросок кубика с заданными условиями. Подробнее в команде /help_r.")
                .AddOption("input", ApplicationCommandOptionType.String, "Формат: XdY, где X - количество бросков, Y - верхняя граница. Подробнее в команде `/help_r`", isRequired: true),
                    new SlashCommandBuilder()
                .WithName("roll20")
                .WithDescription("Выполняет бросок кубика d20."),
                    new SlashCommandBuilder()
                .WithName("queue")
                .WithDescription("Запускает создание очереди.")
                .AddOption("input", ApplicationCommandOptionType.String, "Формат: X, где X - количество участников сцены", isRequired: true),
                    new SlashCommandBuilder()
                .WithName("q")
                .WithDescription("Добавляет вас в очередь на выполнение действия.")
                .AddOption("input", ApplicationCommandOptionType.String, "Формат: dY, где Y - верхняя граница", isRequired: true),
                    new SlashCommandBuilder()
                .WithName("stop_q")
                .WithDescription("Останавливает текущую очередь, если она активна."),
                                        new SlashCommandBuilder()
                .WithName("ping")
                .WithDescription("Test."),
                    new SlashCommandBuilder()
                .WithName("start")
                .WithDescription("Запустить игру.")
                .AddOption("game_name", ApplicationCommandOptionType.String, "Название игры", isRequired: true)
                .AddOption("master", ApplicationCommandOptionType.User, "Имя мастера, проводящего игру", isRequired: false)
                .AddOption("comment", ApplicationCommandOptionType.String, "Дополнительные комментарии", isRequired: false),
                //    new SlashCommandBuilder()
                //.WithName("pause")
                //.WithDescription("Приостановить игру."),
                //    new SlashCommandBuilder()
                //.WithName("resume")
                //.WithDescription("Продолжить игру."),
                //    new SlashCommandBuilder()
                //.WithName("stop")
                //.WithDescription("Остановить игру."),
                //    new SlashCommandBuilder()
                //.WithName("edit_session")
                //.WithDescription("Изменить параметры текущей игры (только для мастеров)")
                //.AddOption("new_game_name", ApplicationCommandOptionType.String, "Новое название игры", isRequired: false)
                //.AddOption("new_master", ApplicationCommandOptionType.User, "Новый мастер", isRequired: false)
                //.AddOption("new_comment", ApplicationCommandOptionType.String, "Новый комментарий", isRequired: false),
                    new SlashCommandBuilder()
                .WithName("close_chat")
                .WithDescription("Закрывает чат/ветку на форуме: чат — в архив, ветку — блокирует.")
                .AddOption("reason", ApplicationCommandOptionType.String, "Причина закрытия", isRequired: false),
                    new SlashCommandBuilder()
                .WithName("open_chat")
                .WithDescription("Возвращает закрытый чат в открытый статус и перемещает в указанную категорию.")
                .AddOption("category", ApplicationCommandOptionType.String, "Название категории, в которую нужно переместить чат", isRequired: true),
                };

                foreach (var command in commands)
                {
                    await guild.CreateApplicationCommandAsync(command.Build());
                }
            }
        }
    }
}
