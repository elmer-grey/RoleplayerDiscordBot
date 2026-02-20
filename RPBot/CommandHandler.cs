using Discord;
using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RPBot
{
    public class CommandHandler
    {
        private readonly DiscordSocketClient _client;
        private readonly CommandService _commandService;
        private readonly List<ulong> GuildIDs;

        // 👇 ДОБАВЛЯЕМ СОБЫТИЕ ДЛЯ ОТСЛЕЖИВАНИЯ ПРОГРЕССА
        public event Func<int, int, string, Task> OnCommandProgress;

        // 👇 ДЛЯ РАСЧЕТА ВРЕМЕНИ
        private DateTime _registrationStartTime;

        public CommandHandler(DiscordSocketClient client)
        {
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
            int totalCommands = 0;
            int processedGuilds = 0;

            foreach (var guildId in GuildIDs)
            {
                var guild = _client.GetGuild(guildId);

                if (guild == null)
                {
                    Console.WriteLine($"Не удалось получить гильдию с ID: {guildId}. Пропускаем вывод команд для этой гильдии.");
                    continue;
                }

                var commands = await _client.GetGuild(guildId).GetApplicationCommandsAsync();
                totalCommands += commands.Count;

                foreach (var command in commands)
                {
                    Console.WriteLine($"Гильдия: {guildId}, Команда: {command.Name}, ID: {command.Id}");
                }

                processedGuilds++;

                // 👇 ОТПРАВЛЯЕМ ПРОГРЕСС ПРОСМОТРА КОМАНД
                OnCommandProgress?.Invoke(
                    processedGuilds,
                    GuildIDs.Count,
                    $"📋 Просмотр команд: гильдия {processedGuilds}/{GuildIDs.Count}"
                );
            }
        }

        private async Task RegisterCommandsAsync()
        {
            _registrationStartTime = DateTime.UtcNow;

            // 👇 СОБИРАЕМ ВСЕ КОМАНДЫ ДЛЯ ПОДСЧЕТА
            var allCommands = GetAllCommands();
            int totalCommands = allCommands.Count * GuildIDs.Count; // команд * серверов
            int completedCommands = 0;

            Console.WriteLine($"\n┌──────────── ЭТАП 1/4: РЕГИСТРАЦИЯ КОМАНД ({totalCommands} операций) ────────────┐");

            foreach (var guildId in GuildIDs)
            {
                var guild = _client.GetGuild(guildId);

                if (guild == null)
                {
                    Console.WriteLine($"│  ❌ Гильдия {guildId} не найдена, пропускаем...            │");
                    continue;
                }

                Console.WriteLine($"│  🌐 Регистрация на сервере: {guild.Name} ({guildId})       │");

                int guildCommandIndex = 0;
                foreach (var command in allCommands)
                {
                    guildCommandIndex++;
                    completedCommands++;

                    try
                    {
                        await guild.CreateApplicationCommandAsync(command.Build());

                        // 👇 РАССЧИТЫВАЕМ ПРОГРЕСС И ВРЕМЯ
                        var percent = (int)((double)completedCommands / totalCommands * 100);
                        var elapsed = DateTime.UtcNow - _registrationStartTime;
                        var estimatedTotal = TimeSpan.FromTicks((long)(elapsed.Ticks * (totalCommands / (double)completedCommands)));
                        var remaining = estimatedTotal - elapsed;

                        var progressMessage = $"│  [{percent,3}%] Команда: {command.Name,-15} | Выполнено: {completedCommands}/{totalCommands} | Время: {elapsed:mm\\:ss}";

                        // 👇 ВЫЗЫВАЕМ СОБЫТИЕ
                        OnCommandProgress?.Invoke(completedCommands, totalCommands, progressMessage);

                        // 👇 ПОКАЗЫВАЕМ В КОНСОЛИ КРАСИВЫЙ ПРОГРЕСС
                        if (guildCommandIndex % 3 == 0 || guildCommandIndex == allCommands.Count)
                        {
                            Console.WriteLine($"│  {progressMessage,-58} │");
                        }

                        // МАЛЕНЬКАЯ ЗАДЕРЖКА, ЧТОБЫ НЕ ЗАБИТЬ RATE LIMIT
                        await Task.Delay(200);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"│   Ошибка регистрации {command.Name}: {ex.Message}     │");
                    }
                }

                Console.WriteLine($"│   Завершено: {guild.Name} ({guildCommandIndex} команд)             │");
            }

            var totalTime = DateTime.UtcNow - _registrationStartTime;
            Console.WriteLine($"└─────────────────────────────────── ({totalTime:mm\\:ss} сек) ────────────────────┘\n");

            // 👇 ФИНАЛЬНОЕ СООБЩЕНИЕ
            OnCommandProgress?.Invoke(
                totalCommands,
                totalCommands,
                $"✅ РЕГИСТРАЦИЯ ЗАВЕРШЕНА! ({totalTime:mm\\:ss} сек)"
            );
        }

        // 👇 МЕТОД ДЛЯ ПОЛУЧЕНИЯ ВСЕХ КОМАНД
        private List<SlashCommandBuilder> GetAllCommands()
        {
            return new List<SlashCommandBuilder>
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
                    .WithName("start")
                    .WithDescription("Запустить игру.")
                    .AddOption("game_name", ApplicationCommandOptionType.String, "Название игры", isRequired: true)
                    .AddOption("master", ApplicationCommandOptionType.User, "Имя мастера, проводящего игру", isRequired: false)
                    .AddOption("comment", ApplicationCommandOptionType.String, "Дополнительные комментарии", isRequired: false),

                new SlashCommandBuilder()
                    .WithName("pause")
                    .WithDescription("Приостановить игру."),

                new SlashCommandBuilder()
                    .WithName("resume")
                    .WithDescription("Продолжить игру."),

                new SlashCommandBuilder()
                    .WithName("stop")
                    .WithDescription("Остановить игру."),

                new SlashCommandBuilder()
                    .WithName("edit_session")
                    .WithDescription("Изменить параметры текущей игры (только для мастеров)")
                    .AddOption("new_game_name", ApplicationCommandOptionType.String, "Новое название игры", isRequired: false)
                    .AddOption("new_master", ApplicationCommandOptionType.User, "Новый мастер", isRequired: false)
                    .AddOption("new_comment", ApplicationCommandOptionType.String, "Новый комментарий", isRequired: false),

                new SlashCommandBuilder()
                    .WithName("close_chat")
                    .WithDescription("Закрывает чат/ветку на форуме: чат — в архив, ветку — блокирует.")
                    .AddOption("reason", ApplicationCommandOptionType.String, "Причина закрытия", isRequired: false),

                new SlashCommandBuilder()
                    .WithName("open_chat")
                    .WithDescription("Возвращает закрытый чат в открытый статус и перемещает в указанную категорию.")
                    .AddOption("category", ApplicationCommandOptionType.String, "Название категории, в которую нужно переместить чат", isRequired: true),
            };
        }
    }
}