using Discord;
using Discord.Commands;
using Discord.WebSocket;
using RPBot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RPBot
{
    public class InfoCommands : ModuleBase<SocketCommandContext>
    {
        [Command("help")]
        public async Task Help(SocketSlashCommand command)
        {
            var helpMessage = new EmbedBuilder()
                .WithTitle("ℹ️ Список доступных команд")
                .WithColor(Color.DarkBlue)
                .WithDescription("Основные команды бота для управления сервером и игровых механик")
                .AddField("📚 Основные команды",
                    "> `/help` - Показывает это сообщение\n" +
                    "> `/help_r` - Помощь по системе бросков кубиков\n" +
                    "> `/help_gs` - Помощь по управлению игровыми сессиями\n" +
                    "> `/help_predict` - Помощь по прогнозам и костяшкам\n" +
                    "> `/serverinfo` - Показывает информацию о сервере\n" +
                    "> `/bug_report [сообщение]` - Отправка отчета об ошибке или предложения")
                .AddField("🛠 Модерация и настройки",
                    "> `/open_chat [категория]` - Открывает доступ писать и перемещает в указанную категорию *(только для мастеров)*\n" +
                    "> `/close_chat [причина]` - Архивирует чат и закрывает доступ писать *(только для мастеров)*\n" +
                    "> `/clr X` - Удаляет X сообщений (1-100) *(для модераторов)*\n" +
                    "> `/settings ...` - Настройки сервера *(администратор/суперпользователь)*")
                .AddField("🎮 Игровые команды",
                    "> `/roll ...`, `/roll20` - Броски кубиков\n" +
                    "> `/start` - Запуск игровой сессии\n" +
                    "> `/prediction ...` - Прогнозы и ставки (см. `/help_predict`)")
                .WithFooter("При проблемах используйте /bug_report")
                .Build();

            await command.RespondAsync(embed: helpMessage);
        }

        [Command("serverinfo")]
        public async Task ServerInfo(SocketSlashCommand command)
        {
            await command.DeferAsync();
            var server = (command.Channel as SocketGuildChannel)?.Guild;

            if (server == null)
            {
                await command.FollowupAsync("Не удалось получить информацию о сервере.");
                return;
            }

            try
            {
                // Получаем актуальные данные о пользователях
                await server.DownloadUsersAsync();

                int totalMembers = server.MemberCount;
                // Если нет GuildPresences, статус может быть Offline для всех — учитываем голосовые каналы как онлайн
                int onlineMembers = server.Users.Count(u => u.Status != UserStatus.Offline || u.VoiceChannel != null);
                int botCount = server.Users.Count(u => u.IsBot);
                int humanCount = totalMembers - botCount;

                // Получаем информацию о каналах
                int textChannels = server.TextChannels.Count;
                int voiceChannels = server.VoiceChannels.Count;
                int categories = server.CategoryChannels.Count;

                var embed = new EmbedBuilder()
                    .WithTitle($"ℹ️ Информация о сервере {server.Name}")
                    .WithThumbnailUrl(server.IconUrl)
                    .AddField("📅 Создан", server.CreatedAt.ToString("dd.MM.yyyy"), true)
                    .AddField("👑 Владелец", server.Owner?.Mention ?? "Неизвестно", true)
                    .AddField("📊 Участники",
                        $"👥 Всего: {totalMembers}\n" +
                        $"🟢 Онлайн: {onlineMembers}\n" +
                        $"🤖 Ботов: {botCount}\n" +
                        $"👤 Людей: {humanCount}", true)
                    .AddField("🌐 Каналы",
                        $"📝 Текстовые: {textChannels}\n" +
                        $"🎤 Голосовые: {voiceChannels}\n" +
                        $"📂 Категории: {categories}", true)
                    .AddField("📌 Важные даты",
                        "• 20.11.2020 - Основание КнР\n" +
                        "• 01.02.2025 - Первый запуск бота на сервере")
                    .WithColor(Color.Blue)
                    //.WithFooter("Статистика участников временно недоступна")
                    .Build();

                await command.FollowupAsync(embed: embed);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка в serverinfo: {ex.Message}");
                await command.FollowupAsync("Произошла ошибка при обработке команды.");
            }
        }

        [Command("help_r")]
        public async Task Help_R(SocketSlashCommand command)
        {
            var helpMessage = new EmbedBuilder()
                .WithTitle("🎲 Помощь по системе бросков")
                .WithColor(Color.DarkPurple)
                .WithDescription("Система позволяет совершать броски кубиков с различными параметрами и модификаторами.")
                .AddField("🔹 Основные команды бросков",
                    "> `/roll XdY` - Будет совершён бросок кубика, или кубиков _(если_ `X`_ > 1)_, с номиналом `Y`.\n" +
                    "> `/roll XdY+Z` - Будет совершён бросок кубика, или кубиков _(если_ `X`_ > 1)_, " +
                    "с номиналом `Y`. Также будет добавлен модификатор в +/-`Z`.\n" +
                    "> `/roll Xd[min,max]` - Будет совершён бросок кубика, или кубиков _(если_ `X`_ > 1)_, " +
                    "с границами от`min` до `max`.\n" +
                    "> `/roll Xd[min,max]+Z` - Будет совершён бросок кубика, или кубиков _(если_ `X`_ > 1)_, с границами от`min` до `max`. " +
                    "Также будет добавлен модификатор в +/-`Z`.\n" +
                    "> `/roll20` - Быстрый бросок d20 (аналогично 1d20)")
                .AddField("🚪 Команды очереди действий",
                    "> `/queue Z` - Запуск очереди с `Z` персонажами в сцене *(только для мастеров)*\n" +
                    "> `/q dY` - Совершается бросок кубика выбранного номинала `Y`. Добавляет вас в очередь на выполнение действия. " +
                    "Может быть использована несколько раз одним человеком. **(только при активной очереди)**\n" +
                    "> `/stop_q` - Остановка текущей очереди, если она активна *(только для мастеров)*")
                .AddField("📊 Особенности системы",
                    "• Автоматическая запись бросков d20 в активных сессиях\n" +
                    "• Визуализация результатов d20 (изображения кубиков)\n" +
                    "• Градиентная цветовая индикация результатов\n" +
                    "• Ограничение на 10 бросков за раз")
                .AddField("⚠ Ограничения",
                    "• Броски в канале статистики учитываются только в активных сессиях\n" +
                    "• Броски не записываются, если сессия на паузе\n" +
                    "• Только мастера могут управлять очередями")
                .WithFooter("*При обнаружении некорректной работы бота сообщите об этом в `/bug_report`*")
                .Build();

            await command.RespondAsync(embed: helpMessage);
        }

        [Command("help_predict")]
        public async Task Help_Predict(SocketSlashCommand command)
        {
            var helpMessage = new EmbedBuilder()
                .WithTitle("📈 Прогнозы и ставки — помощь")
                .WithColor(Color.DarkTeal)
                .WithDescription("Прогнозы позволяют ставить **костяшки** на один из 2-5 исходов. Создание — через модал, ставки — через кнопки.")
                .AddField("✅ Где работает",
                    "Создание, ставки, завершение и отмена доступны **только в чате голосового канала**.\n" +
                    "Для создания прогноза нужно: **быть в голосовом канале** и чтобы на нём было **активное событие**.")
                .AddField("🧩 Основные команды",
                    "> `/prediction action:create` — создать прогноз с 2-5 исходами (мастер НРИ/админ)\n" +
                    "> `/prediction action:info` — ваш баланс и текущий прогноз\n" +
                    "> `/prediction action:bet outcome:<№> amount:<N>` — сделать ставку\n" +
                    "> `/prediction action:resolve outcome:<№>` — завершить (создатель/админ)\n" +
                    "> `/prediction action:cancel` — отменить с возвратом ставок (создатель/админ)")
                .AddField("📊 Статистика и достижения",
                    "> `/prediction action:history [page:<№>]` — история завершённых прогнозов\n" +
                    "> `/prediction action:profile [user:<@>]` — профиль игрока с полной статистикой\n" +
                    "> `/prediction action:achievements` — все 29 достижений + кто получил")
                .AddField("🖱️ Ставки через кнопки",
                    "1) `Сделать ставку` → показывается баланс + кнопка `Продолжить`\n" +
                    "2) `Продолжить` → модал с выбором исхода и суммы\n" +
                    "3) Если ставка уже была → модал **увеличения** без выбора исхода")
                .AddField("🎯 Множественные исходы",
                    "• При создании можно указать от 2 до 5 исходов\n" +
                    "• Коэффициенты рассчитываются автоматически: `общий банк / ставки на исход`\n" +
                    "• Прогресс-бары показывают распределение ставок\n" +
                    "• Пример: 70% → исход 1 (коэфф. 1.40x), 30% → исход 2 (коэфф. 3.33x)")
                .AddField("🏅 Система достижений",
                    "Автоматически присваиваются 29 достижений:\n" +
                    "• 🌟 Новичковые (5): прогресс участия\n" +
                    "• 💰 Финансовые (6): крупные выигрыши\n" +
                    "• 🎯 Точность (5): винрейт и серии\n" +
                    "• 🚀 Риск (4): высокие коэффициенты\n" +
                    "• 📈 Стратегия (4): создание и прибыль\n" +
                    "• ⭐ Специальные (5): уникальные условия")
                .AddField("⏳ Таймер",
                    "Приём ставок показывает **оставшееся время** (обновление ~10 сек).\n" +
                    "После закрытия приёма кнопки меняются на **выбор победителя**.")
                .AddField("ℹ️ Важно",
                    "• История: сохраняется до 100 прогнозов на сервер\n" +
                    "• Профиль: отслеживает все ставки, выигрыши, прибыль\n" +
                    "• Достижения: получаются автоматически после каждого прогноза\n" +
                    "• Если событие завершилось — прогноз закрывается с возвратом ставок")
                .WithFooter("Используйте /bug_report при проблемах | Помощь: /help_predict")
                .Build();

            await command.RespondAsync(embed: helpMessage);
        }

        [Command("help_gs")]
        public async Task Help_GS(SocketSlashCommand command)
        {
            var helpMessage = new EmbedBuilder()
                .WithTitle("📚 Помощь по управлению игровыми сессиями")
                .WithColor(Color.Blue)
                .WithDescription("Система позволяет отслеживать время игровых сессий, делать паузы и собирать статистику бросков.")
                .AddField("🔹 Основные действия",
                    "> `/start [название игры]` - Начинает новую сессию\n" +
                    "> `/help_gs` - Показывает это сообщение\n")
                .AddField("🕹 Управление сессией (через кнопки)",
                    "> `⏸ Пауза` - Приостановить отсчёт времени\n" +
                    "> `▶ Продолжить` - Возобновить сессию после паузы\n" +
                    "> `✏ Изменить` - Редактировать параметры сессии\n" +
                    "> `🎲 Броски` - Вкл/выкл сбор статистики бросков\n" +
                    "> `⏹ Завершить` - Остановить сессию и показать статистику")
                .AddField("📊 Статистика бросков",
                    "После завершения сессии вы можете:\n" +
                    "• Просмотреть общую статистику по значениям\n" +
                    "• Получить детальную статистику по игрокам\n" +
                    "• Отказаться от просмотра статистики")
                .AddField("ℹ Особенности",
                    "• Сессии автоматически создаются при старте ивентов\n" +
                    "• Напоминания о паузе каждые 10 минут\n" +
                    "• Время пауз не учитывается в общей статистике\n" +
                    "• Только мастера могут управлять сессиями")
                .WithFooter("*При обнаружении некорректной работы бота сообщите об этом в `/bug_report`*")
                .Build();

            await command.RespondAsync(embed: helpMessage);
        }

        [Command("bug_report")]
        public async Task Bug_Report(SocketSlashCommand command, string input)
        {
            var user = command.User as SocketGuildUser;

            if (user == null)
            {
                await command.RespondAsync("Произошла ошибка. Пожалуйста, попробуйте снова.");
                return;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                await command.RespondAsync("Пожалуйста, укажите сообщение для отчета об ошибке.");
                return;
            }

            string directoryPath = BotConfig.ResolvePath(BotConfig.Current?.BugReportDirectory ?? Path.Combine("Logs"));
            Directory.CreateDirectory(directoryPath);

            string filePath = Path.Combine(directoryPath, $"{user.Username}.txt");

            using (StreamWriter writer = new StreamWriter(filePath, true))
            {
                string reportTime = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
                await writer.WriteLineAsync($"[{reportTime}] {input}");
            }

            IncrementBugReportCounter();

            await command.RespondAsync("Ваш отчет об ошибке был успешно отправлен!", ephemeral: true);
        }

        private void IncrementBugReportCounter()
        {
            var logDir = BotConfig.ResolvePath(BotConfig.Current?.BugReportDirectory ?? Path.Combine("Logs"));
            Directory.CreateDirectory(logDir);
            string counterFilePath = Path.Combine(logDir, "bug_report_counter.txt");

            if (!File.Exists(counterFilePath))
            {
                File.WriteAllText(counterFilePath, "1");
            }
            else
            {
                if (int.TryParse(File.ReadAllText(counterFilePath), out var currentCount))
                {
                    currentCount++;
                    File.WriteAllText(counterFilePath, currentCount.ToString());
                }
                else
                {
                    File.WriteAllText(counterFilePath, "1");
                }
            }
        }
    }


}