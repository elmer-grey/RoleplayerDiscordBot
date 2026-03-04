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
        private readonly List<ulong> _guildIDs;

        private static BotUI _ui;

        // 👇 ДОБАВЛЯЕМ СОБЫТИЕ ДЛЯ ОТСЛЕЖИВАНИЯ ПРОГРЕССА
        public event Func<int, int, string, Task> OnCommandProgress;

        // 👇 ДЛЯ РАСЧЕТА ВРЕМЕНИ
        private DateTime _registrationStartTime;

        public CommandHandler(DiscordSocketClient client, List<ulong> guildIDs)
        {
            _guildIDs = guildIDs;
            _client = client;
            _commandService = new CommandService();
        }

        // Метод для установки UI (вызывать из Program.cs после создания UI)
        public static void SetUI(BotUI ui)
        {
            _ui = ui;
        }

        public async Task InitializeAsync()
        {
            await RegisterCommandsAsync();
        }

        public async Task ListSlashCommandsAsync()
        {
            int totalCommands = 0;
            int processedGuilds = 0;

            foreach (var guildId in _guildIDs)
            {
                var guild = _client.GetGuild(guildId);

                if (guild == null)
                {
                    _ui?.AddLog($"Не удалось получить гильдию с ID: {guildId}. Пропускаем вывод команд для этой гильдии.");
                    continue;
                }

                var commands = await _client.GetGuild(guildId).GetApplicationCommandsAsync();
                totalCommands += commands.Count;

                foreach (var command in commands)
                {
                    Console.WriteLine($"Гильдия: {guildId}, Команда: {command.Name}, ID: {command.Id}");
                }

                processedGuilds++;
                _ui?.AddLog($"Просмотр команд: гильдия {processedGuilds}/{_guildIDs.Count}");
            }
        }

        private async Task RegisterCommandsAsync()
        {
            _registrationStartTime = DateTime.UtcNow;

            // 👇 СОБИРАЕМ ВСЕ КОМАНДЫ ДЛЯ ПОДСЧЕТА
            var allCommands = GetAllCommands();
            int totalCommands = allCommands.Count * _guildIDs.Count;
            int completedCommands = 0;

            Console.WriteLine($"\n┌──────────── ЭТАП 1/4: РЕГИСТРАЦИЯ КОМАНД ({totalCommands} операций) ────────────┐");

            foreach (var guildId in _guildIDs)
            {
                var guild = _client.GetGuild(guildId);

                if (guild == null)
                {
                    _ui?.AddLog($"│  Гильдия {guildId} не найдена, пропускаем...            │");
                    continue;
                }

                _ui?.AddLog($"│  Регистрация на сервере: {guild.Name} ({guildId})       │");

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

                        _ui?.AddLog($"│  [{percent,3}%] Команда: {command.Name,-20} | Выполнено: {completedCommands}/{totalCommands} | Время: {elapsed:mm\\:ss}");

                        await Task.Delay(200);
                    }
                    catch (Exception ex)
                    {
                        _ui?.AddLog($"│   Ошибка регистрации {command.Name}: {ex.Message}     │");
                    }
                }

                _ui?.AddLog($"│   Завершено: {guild.Name} ({guildCommandIndex} команд)             │");
            }

            var totalTime = DateTime.UtcNow - _registrationStartTime;
            _ui?.AddLog($"└─────────────────────────────────── ({totalTime:mm\\:ss} сек) ────────────────────┘\n");
        }

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
                    .WithName("settings")
                    .WithDescription("Управление настройками бота на этом сервере (только для администраторов).")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("action")
                        .WithDescription("Действие: get, set, list, reset")
                        .WithType(ApplicationCommandOptionType.String)
                        .AddChoice("get", "get")
                        .AddChoice("set", "set")
                        .AddChoice("list", "list")
                        .AddChoice("reset", "reset")
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("key")
                        .WithDescription("Ключ настройки")
                        .WithType(ApplicationCommandOptionType.String)
                        .AddChoice("moderation_channel", "moderation_channel")
                        .AddChoice("welcome_channel", "welcome_channel")
                        .AddChoice("roll_channel", "roll_channel")
                        .AddChoice("stats_channel", "stats_channel")
                        .AddChoice("record_channel", "record_channel")
                        .AddChoice("welcome_message", "welcome_message")
                        .AddChoice("line_message", "line_message")
                        .AddChoice("general_rg_channel", "general_rg_channel")
                        .AddChoice("default_role", "default_role")
                        .AddChoice("swear_filter", "swear_filter")
                        .AddChoice("swear_words", "swear_words")
                        .AddChoice("predictions", "predictions")
                        .WithRequired(false))
                    .AddOption("value", ApplicationCommandOptionType.String, "Значение для установки (ID канала, текст, true/false для переключателей)")
                    .AddOption("channel", ApplicationCommandOptionType.Channel, "Канал (альтернативный способ указать канал)")
                    .AddOption("toggle", ApplicationCommandOptionType.Boolean, "Переключатель (true/false)") ,

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