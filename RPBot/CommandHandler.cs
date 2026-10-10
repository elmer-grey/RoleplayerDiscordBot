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
            int doneCommands = 0;

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

                // ⚡ BulkOverwriteApplicationCommandsAsync — один REST-запрос на всю гильдию вместо N.
                // Раньше было ~1.2 сек на команду (REST + Task.Delay(200) для rate-limit).
                // С ~22 командами × 2 гильдии = ~50 сек; теперь ≤ 1 REST-вызов = 1-3 сек.
                // Метод определён на IGuild, поэтому кастим SocketGuild → IGuild.
                //
                // ⚠️ ВАЖНО: BulkOverwriteApplicationCommandsAsync ЗАМЕНЯЕТ ВСЕ команды гильдии
                // на переданный массив (PUT-семантика). Если передать неполный список — лишние
                // команды удалятся. Поэтому ВСЕГДА шлём полный список allCommands, а не только
                // [new]. Сравнение с existing — только чтобы понять, нужен ли запрос вообще.
                var existing = (System.Collections.Generic.IReadOnlyCollection<Discord.IApplicationCommand>?)null;
                try
                {
                    existing = await ((IGuild)guild).GetApplicationCommandsAsync();
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(LogCategory.Cmd, $"Не удалось получить список зарегистрированных команд для {guild.Name}: {ex.GetType().Name}: {ex.Message}");
                }

                bool needsOverwrite = true;
                if (existing != null)
                {
                    // Сравниваем только по Name+Description: опции и choices при изменении
                    // будут перезаписаны — BulkOverwrite идемпотентен.
                    // localCmd — SlashCommandBuilder, его .Name/.Description — обычные string.
                    needsOverwrite = false;
                    foreach (var localCmd in allCommands)
                    {
                        var remote = existing.FirstOrDefault(c =>
                            string.Equals(c.Name, localCmd.Name, StringComparison.Ordinal) &&
                            string.Equals(c.Description, localCmd.Description, StringComparison.Ordinal));
                        if (remote == null)
                        {
                            // Нашли команду, которой нет или описание отличается — нужна перезапись.
                            needsOverwrite = true;
                            break;
                        }
                    }
                }

                if (!needsOverwrite)
                {
                    var elapsed = DateTime.UtcNow - _registrationStartTime;
                    BotLogger.Info(LogCategory.Cmd, $"│  Все {allCommands.Count} команд уже актуальны на {guild.Name} — пропускаем. Время: {elapsed:mm\\:ss}");
                    doneCommands += allCommands.Count;
                }
                else
                {
                    try
                    {
                        var payload = allCommands.Select(c => c.Build()).ToArray();
                        await ((IGuild)guild).BulkOverwriteApplicationCommandsAsync(payload);
                        doneCommands += allCommands.Count;

                        var elapsed = DateTime.UtcNow - _registrationStartTime;
                        int pct = totalCommands == 0 ? 100 : (int)Math.Round(doneCommands * 100.0 / totalCommands);
                        BotLogger.Info(LogCategory.Cmd, $"│  [{pct,3}%] Сервер: {guild.Name,-22} | Выполнено: {doneCommands}/{totalCommands} | BulkOverwrite: {allCommands.Count} команд | Время: {elapsed:mm\\:ss}");
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Error(LogCategory.Cmd, $"BulkOverwriteApplicationCommandsAsync failed for guild={guildId}: {ex.GetType().Name}: {ex.Message}");
                        BotLogger.Info(LogCategory.Cmd, $"│   Ошибка регистрации на {guild.Name}: {ex.Message}     │");
                    }
                }

                BotLogger.Info(LogCategory.Cmd, $"│   Завершено: {guild.Name} ({allCommands.Count} команд)             │");
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
    .WithName("voice")
    .WithDescription("Создать временный голосовой канал с лимитом на число человек (только для мастеров)"),

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

    // start/pause/resume/stop/edit_session/open_chat — раньше регистрировались как отдельные slash-команды,
    // но с 2026-10-10 заменены кнопками в сообщении-управления. Регистрация отключена, чтобы:
    // 1) не путать пользователей (команды в палитре Discord, но при вызове «Команда не распознана» — default в switch);
    // 2) ускорить регистрацию (было ~67 сек, 56 REST-вызовов по 1 на команду × 2 гильдии).
    // Логика StartGameSession/CloseChatCommand/EditSession остаётся — она вызывается кнопками напрямую.

    new SlashCommandBuilder()
    .WithName("close_chat")
    .WithDescription("Архивирует текстовый канал или блокирует ветку форума")
    .AddOption("reason", ApplicationCommandOptionType.String, "Причина закрытия", isRequired: false),


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

