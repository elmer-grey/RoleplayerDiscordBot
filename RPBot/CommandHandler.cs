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

        private static BotUI? _ui;

                // 👇 ДЛЯ РАСЧЕТА ВРЕМЕНИ
                private DateTime _registrationStartTime;

        public CommandHandler(DiscordSocketClient client, List<ulong> guildIDs)
        {
            _guildIDs = guildIDs;
            _client = client;
            _commandService = new CommandService();
        }

        // Метод для установки UI (вызывать из Program.cs после создания UI)
        public static void SetUI(BotUI? ui)
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

            BotLogger.Info(LogCategory.Cmd, "┌──────────── СПИСОК ЗАРЕГИСТРИРОВАННЫХ КОМАНД ────────────┐");

            foreach (var guildId in _guildIDs)
            {
                var guild = _client.GetGuild(guildId);

                if (guild == null)
                {
                    BotLogger.Info(LogCategory.Cmd, $"│ Гильдия {guildId} недоступна, команды пропущены");
                    continue;
                }

                var commands = await _client.GetGuild(guildId).GetApplicationCommandsAsync();
                totalCommands += commands.Count;

                processedGuilds++;
                BotLogger.Info(LogCategory.Cmd, $"│ {guild.Name} ({guildId}) — {commands.Count} команд");

                if (commands.Count > 0)
                {
                    foreach (var cmd in commands)
                    {
                        BotLogger.Info(LogCategory.Cmd, $"│   • /{cmd.Name}");
                    }
                }
                else
                {
                    BotLogger.Info(LogCategory.Cmd, "│   • Нет команд");
                }
            }

            BotLogger.Info(LogCategory.Cmd, "└──────────────────────────────────────────────────────────┘");
        }

        private async Task RegisterCommandsAsync()
        {
            _registrationStartTime = DateTime.UtcNow;

            // 👇 СОБИРАЕМ ВСЕ КОМАНДЫ ДЛЯ ПОДСЧЕТА
            var allCommands = GetAllCommands();
            int totalCommands = allCommands.Count * _guildIDs.Count;
            int completedCommands = 0;

            BotLogger.Info(LogCategory.Cmd, $"\u0420\u0435\u0433\u0438\u0441\u0442\u0440\u0430\u0446\u0438\u044f \u043a\u043e\u043c\u0430\u043d\u0434 \u2014 \u042d\u0422\u0410\u041f 1/4 ({totalCommands} \u043e\u043f\u0435\u0440\u0430\u0446\u0438\u0439)");

            foreach (var guildId in _guildIDs)
            {
                var guild = _client.GetGuild(guildId);

                if (guild == null)
                {
                    BotLogger.Info(LogCategory.Cmd, $"│  Гильдия {guildId} не найдена, пропускаем...            │");
                    continue;
                }

                BotLogger.Info(LogCategory.Cmd, $"│  Регистрация на сервере: {guild.Name} ({guildId})       │");

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

                        BotLogger.Info(LogCategory.Cmd, $"│  [{percent,3}%] Команда: {command.Name,-20} | Выполнено: {completedCommands}/{totalCommands} | Время: {elapsed:mm\\:ss}");

                        await Task.Delay(200);
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Info(LogCategory.Cmd, $"│   Ошибка регистрации {command.Name}: {ex.Message}     │");
                    }
                }

                BotLogger.Info(LogCategory.Cmd, $"│   Завершено: {guild.Name} ({guildCommandIndex} команд)             │");
            }

            var totalTime = DateTime.UtcNow - _registrationStartTime;
            BotLogger.Info(LogCategory.Cmd, $"└─────────────────────────────────── ({totalTime:mm\\:ss} сек) ────────────────────┘\n");
        }

        private List<SlashCommandBuilder> GetAllCommands()
        {
    return new List<SlashCommandBuilder>
    {
    new SlashCommandBuilder()
    .WithName("help")
    .WithDescription("Показывает список всех доступных команд бота"),

    new SlashCommandBuilder()
    .WithName("help_r")
    .WithDescription("Справка по системе бросков кубиков"),

    new SlashCommandBuilder()
    .WithName("help_gs")
    .WithDescription("Справка по управлению игровыми сессиями и подсчёту времени"),

    new SlashCommandBuilder()
    .WithName("help_predict")
    .WithDescription("Справка по системе прогнозов и ставок на костяшки"),

new SlashCommandBuilder()
.WithName("help_music")
.WithDescription("Выводит подробную справку по музыкальным командам (/music и /music-playlist)."),

new SlashCommandBuilder()
.WithName("clr")
.WithDescription("Удаляет выбранное количество сообщений.")
.AddOption("input", ApplicationCommandOptionType.String, "Формат: Х, где Х - количество сообщений, которые нужно удалить", isRequired: true),

    new SlashCommandBuilder()
    .WithName("serverinfo")
    .WithDescription("Показывает детальную информацию о текущем сервере"),

    new SlashCommandBuilder()
    .WithName("bug_report")
    .WithDescription("Отправить отчёт об ошибке или предложение по улучшению бота")
    .AddOption("input", ApplicationCommandOptionType.String, "Описание проблемы или предложение", isRequired: true),

    new SlashCommandBuilder()
    .WithName("settings")
    .WithDescription("Управление настройками бота на сервере (только для администраторов)")
    .AddOption(new SlashCommandOptionBuilder()
        .WithName("action")
        .WithDescription("Выбор действия с настройками")
        .WithType(ApplicationCommandOptionType.String)
        .AddChoice("Получить значение настройки", "get")
        .AddChoice("Установить новое значение", "set")
        .AddChoice("Показать все настройки", "list")
        .AddChoice("Сбросить настройки сервера", "reset")
        .AddChoice("Перезагрузить из файла", "reload")
        .AddChoice("Показать справку", "help")
        .WithRequired(true))
    .AddOption(new SlashCommandOptionBuilder()
        .WithName("key")
        .WithDescription("Ключ настройки для изменения")
        .WithType(ApplicationCommandOptionType.String)
        .AddChoice("Канал модерации", "moderation_channel")
        .AddChoice("Канал приветствий", "welcome_channel")
        .AddChoice("Канал бросков кубиков", "roll_channel")
        .AddChoice("Канал статистики", "stats_channel")
        .AddChoice("Канал записи времени", "record_channel")
        .AddChoice("Сообщение приветствия", "welcome_message")
        .AddChoice("Сообщение-линия", "line_message")
        .AddChoice("Общий ролевой канал", "general_rg_channel")
        .AddChoice("Роль по умолчанию", "default_role")
                .AddChoice("Роль мастера", "master_role")
        .AddChoice("Роль суперпользователя", "super_user_role")
        .AddChoice("Фильтр мата (вкл/выкл)", "swear_filter")
        .AddChoice("Список матерных слов", "swear_words")
        .AddChoice("Прогнозы (вкл/выкл)", "predictions")
                        .AddChoice("Картинки для бросков (вкл/выкл)", "roll_pictures")
        .AddChoice("Голосовой канал события", "event_voice_channel")
        .WithRequired(false))
    .AddOption("value", ApplicationCommandOptionType.String, "Новое значение настройки")
    .AddOption("channel", ApplicationCommandOptionType.Channel, "Выбор канала из списка")
    .AddOption("toggle", ApplicationCommandOptionType.Boolean, "Переключатель включения/выключения") ,

    new SlashCommandBuilder()
    .WithName("roll_pictures")
    .WithDescription("Включает или выключает картинки для бросков кубиков")
    .AddOption("enabled", ApplicationCommandOptionType.Boolean, "Включить картинки для бросков", isRequired: false),

    new SlashCommandBuilder()
    .WithName("prediction")
    .WithDescription("Прогнозы и ставки на костяшки")
    .AddOption(new SlashCommandOptionBuilder()
        .WithName("action")
        .WithDescription("Выбор действия с прогнозами")
        .WithType(ApplicationCommandOptionType.String)
        .AddChoice("Создать прогноз", "create")
        .AddChoice("Сделать ставку", "bet")
        .AddChoice("Завершить прогноз", "resolve")
        .AddChoice("Баланс и текущий прогноз", "info")
        .AddChoice("Отменить прогноз", "cancel")
        .AddChoice("История прогнозов", "history")
        .AddChoice("Профиль игрока", "profile")
        .AddChoice("Все достижения", "achievements")
        .AddChoice("Изменить баланс (только админ)", "adjust_points")
        .WithRequired(true))
    .AddOption(new SlashCommandOptionBuilder()
        .WithName("title")
        .WithDescription("Название прогноза")
        .WithType(ApplicationCommandOptionType.String)
        .WithRequired(false))
    .AddOption(new SlashCommandOptionBuilder()
        .WithName("outcome1")
        .WithDescription("Название первого исхода")
        .WithType(ApplicationCommandOptionType.String)
        .WithRequired(false))
    .AddOption(new SlashCommandOptionBuilder()
        .WithName("outcome2")
        .WithDescription("Название второго исхода")
        .WithType(ApplicationCommandOptionType.String)
        .WithRequired(false))
    .AddOption(new SlashCommandOptionBuilder()
        .WithName("duration_minutes")
        .WithDescription("Продолжительность приёма ставок в минутах")
        .WithType(ApplicationCommandOptionType.Integer)
        .WithRequired(false))
    .AddOption(new SlashCommandOptionBuilder()
        .WithName("outcome")
        .WithDescription("Номер исхода от 1 до 5")
        .WithType(ApplicationCommandOptionType.Integer)
        .WithRequired(false))
    .AddOption(new SlashCommandOptionBuilder()
        .WithName("amount")
        .WithDescription("Количество костяшек")
        .WithType(ApplicationCommandOptionType.Integer)
        .WithRequired(false))
    .AddOption(new SlashCommandOptionBuilder()
        .WithName("user")
        .WithDescription("Выбор пользователя")
        .WithType(ApplicationCommandOptionType.User)
        .WithRequired(false))
    .AddOption(new SlashCommandOptionBuilder()
        .WithName("page")
        .WithDescription("Номер страницы истории")
        .WithType(ApplicationCommandOptionType.Integer)
        .WithRequired(false)) ,

    new SlashCommandBuilder()
    .WithName("roll")
    .WithDescription("Выполняет бросок кубика по указанной формуле")
    .AddOption("input", ApplicationCommandOptionType.String, "Формула броска (например: 2d6+3, 1d20). Подробности: /help_r", isRequired: true),

    new SlashCommandBuilder()
    .WithName("roll20")
    .WithDescription("Выполняет бросок двадцатигранного кубика d20"),

    new SlashCommandBuilder()
    .WithName("queue")
    .WithDescription("Создаёт очередь участников для сцены")
    .AddOption("input", ApplicationCommandOptionType.String, "Количество участников сцены", isRequired: true),

    new SlashCommandBuilder()
    .WithName("bwonk")
    .WithDescription("Бонькнуть пользователя или посмотреть статистику бонков")
    .AddOption(new SlashCommandOptionBuilder()
        .WithName("action")
        .WithDescription("Выбор действия")
        .WithType(ApplicationCommandOptionType.String)
        .AddChoice("Бонькнуть", "bonk")
        .AddChoice("Статистика", "stats")
        .WithRequired(false))
    .AddOption("target", ApplicationCommandOptionType.User, "Пользователь для бонка или просмотра статистики", isRequired: false),

    new SlashCommandBuilder()
    .WithName("q")
    .WithDescription("Добавляет вас в очередь с броском инициативы")
    .AddOption("input", ApplicationCommandOptionType.String, "Формула броска (например: d20)", isRequired: true),

    new SlashCommandBuilder()
    .WithName("stop_q")
    .WithDescription("Останавливает текущую активную очередь"),

    new SlashCommandBuilder()
    .WithName("start")
    .WithDescription("Начать новую игровую сессию")
    .AddOption("game_name", ApplicationCommandOptionType.String, "Название игры или сцены", isRequired: true)
    .AddOption("master", ApplicationCommandOptionType.User, "Мастер игры", isRequired: false)
    .AddOption("comment", ApplicationCommandOptionType.String, "Дополнительные комментарии к сессии", isRequired: false),

    new SlashCommandBuilder()
    .WithName("pause")
    .WithDescription("Приостановить текущую игровую сессию"),

    new SlashCommandBuilder()
    .WithName("resume")
    .WithDescription("Возобновить приостановленную игровую сессию"),

    new SlashCommandBuilder()
    .WithName("stop")
    .WithDescription("Завершить текущую игровую сессию"),

    new SlashCommandBuilder()
    .WithName("edit_session")
    .WithDescription("Изменить параметры текущей игровой сессии (только для мастеров)")
    .AddOption("new_game_name", ApplicationCommandOptionType.String, "Новое название игры", isRequired: false)
    .AddOption("new_master", ApplicationCommandOptionType.User, "Новый мастер игры", isRequired: false)
    .AddOption("new_comment", ApplicationCommandOptionType.String, "Новый комментарий", isRequired: false),

    new SlashCommandBuilder()
    .WithName("close_chat")
    .WithDescription("Архивирует текстовый канал или блокирует ветку форума")
    .AddOption("reason", ApplicationCommandOptionType.String, "Причина закрытия", isRequired: false),

    new SlashCommandBuilder()
    .WithName("open_chat")
    .WithDescription("Возвращает канал из архива и перемещает в указанную категорию")
    .AddOption("category", ApplicationCommandOptionType.String, "Название категории для перемещения", isRequired: true),


new SlashCommandBuilder()
.WithName("event_notify")
.WithDescription("Управление личными уведомлениями о новых событиях")
.AddOption(new SlashCommandOptionBuilder()
.WithName("action")
.WithDescription("Выбор действия с уведомлениями")
.WithType(ApplicationCommandOptionType.String)
.AddChoice("Подписаться на уведомления", "subscribe")
.AddChoice("Отписаться от уведомлений", "unsubscribe")
.AddChoice("Проверить статус подписки", "status")
.WithRequired(true)),

                // === МУЗЫКА ===
                new SlashCommandBuilder()
                    .WithName("music")
                    .WithDescription("Управление музыкой в голосовом канале.")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("action")
                        .WithDescription("Действие")
                        .WithType(ApplicationCommandOptionType.String)
                        .AddChoice("играть",     "play")
                        .AddChoice("стоп",       "stop")
                        .AddChoice("пауза",      "pause")
                        .AddChoice("продолжить", "resume")
                        .AddChoice("пропустить", "skip")
                        .AddChoice("очередь",    "queue")
                        .AddChoice("повтор",     "loop")
                        .AddChoice("перемешать", "shuffle")
                        .AddChoice("перемотка",  "seek")
                        .AddChoice("удалить",    "remove")
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("запрос")
                        .WithDescription("Ссылка на трек, название для поиска или название сохранённого плейлиста — для «играть»")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("время")
                        .WithDescription("Позиция для перемотки: «1:30» или «90» сек — для «перемотка»")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("номер")
                        .WithDescription("Номер трека в очереди для удаления — для «удалить»")
                        .WithType(ApplicationCommandOptionType.Integer)
                        .WithRequired(false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("mode")
                        .WithDescription("Режим повтора (для «повтор»): трек / очередь / выкл")
                        .WithType(ApplicationCommandOptionType.String)
                        .AddChoice("трек",    "track")
                        .AddChoice("очередь", "queue")
                        .AddChoice("выкл",    "none")
                        .WithRequired(false)),

                // === ПЛЕЙЛИСТЫ ===
                new SlashCommandBuilder()
                    .WithName("music-playlist")
                    .WithDescription("Управление сохранёнными плейлистами.")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("действие")
                        .WithDescription("Что сделать с плейлистом")
                        .WithType(ApplicationCommandOptionType.String)
                        .AddChoice("сохранить",    "playlist_save")
                        .AddChoice("загрузить",    "playlist_load")
                        .AddChoice("список",       "playlist_list")
                        .AddChoice("переименовать","playlist_rename")
                        .AddChoice("доступ",       "playlist_access")
                        .AddChoice("удалить",      "playlist_delete")
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("название")
                        .WithDescription("Название плейлиста")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("новое_название")
                        .WithDescription("Новое название плейлиста — для действия «переименовать»")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("доступ")
                        .WithDescription("Уровень доступа — для действия «доступ»")
                        .WithType(ApplicationCommandOptionType.String)
                        .AddChoice("личный",   "private")
                        .AddChoice("публичный","public")
                        .WithRequired(false)),
            };
        }
    }
}

