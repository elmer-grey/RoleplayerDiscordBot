using Discord;
using Discord.Commands;
using Discord.WebSocket;
using RPBot;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace RPBot
{
    public class ModerationCommands : ModuleBase<SocketCommandContext>
    {
        private readonly DiscordSocketClient _client;

        private static bool CanUseSuperUserActions(SocketGuildUser? user)
        {
            if (user == null)
                return false;

            if (user.GuildPermissions.Administrator)
                return true;

            var config = Program.ServerConfigResolver?.Invoke(user.Guild.Id);
            return config?.SuperUserRoleId.HasValue == true && user.Roles.Any(r => r.Id == config.SuperUserRoleId.Value);
        }

        private static Task LogStartup(string message)
        {
            BotLogger.Info(LogCategory.Cmd, message);
            return Task.CompletedTask;
        }

        public ModerationCommands(DiscordSocketClient client)
        {
            _client = client;
        }

        [Command("close_chat")]
        public async Task CloseChat(SocketSlashCommand command)
        {
            // Отложим ответ, чтобы Discord не считал команду "зависшей"
            await command.DeferAsync();

            await LogStartup($"Команда '/close_chat' вызвана пользователем {command.User.Username} ({command.User.Id}).");

            var user = command.User as SocketGuildUser;

            if (!CanUseSuperUserActions(user))
            {
                await LogStartup($"Отказ в доступе: пользователь {command.User.Username} не имеет прав на выполнение команды.");
                await command.FollowupAsync("У вас нет прав на выполнение этой команды. Администратор оповещён.", ephemeral: true);
                return;
            }

            var channel = command.Channel as SocketGuildChannel;
            var guild = channel?.Guild;

            if (guild == null)
            {
                await command.FollowupAsync("Эта команда может быть выполнена только на сервере.", ephemeral: true);
                return;
            }

            var reason = command.Data.Options.FirstOrDefault(opt => opt.Name == "reason")?.Value?.ToString() ?? "Причина не указана.";

            if (channel is SocketThreadChannel threadChannel)
            {
                // Если это ветка, выводим информацию
                await LogStartup($"Обработка ветки {threadChannel.Name} ({threadChannel.Id}).");

                // Отправляем сообщение в ветке перед её закрытием
                await threadChannel.SendMessageAsync("Тема закрыта. Сбор на игры перешёл в отдельный чат.");

                // Закрываем ветку, если это поддерживается
                try
                {
                    // Здесь можно использовать метод ArchiveAsync, если он доступен
                    await threadChannel.ModifyAsync(prop =>
                    {
                        prop.Locked = true;
                        prop.Archived = true; // Убедитесь, что это свойство поддерживается
                    });

                    BotLogger.Info(LogCategory.Cmd, $"Ветка {threadChannel.Name} закрыта. Причина: {reason}");
                    await command.FollowupAsync($"Ветка {threadChannel.Mention} была закрыта. Причина: {reason}");
                }
                catch (NotSupportedException ex)
                {
                    BotLogger.Error(LogCategory.Cmd, $"Ошибка при закрытии ветки: {ex.Message}");
                    await command.FollowupAsync("Не удалось закрыть ветку. Пожалуйста, проверьте права доступа или тип канала.");
                }
            }
            else if (channel is SocketTextChannel textChannel)
            {
                // Если это текстовый канал, перемещаем его в архив и закрываем доступ
                SocketCategoryChannel archiveCategory = guild.CategoryChannels.FirstOrDefault(cat => cat.Name == "Архив");

                if (archiveCategory == null)
                {
                    BotLogger.Info(LogCategory.Cmd, "Категория 'Архив' не найдена. Создание новой категории.");
                    var restCategory = await guild.CreateCategoryChannelAsync("Архив");

                    if (restCategory == null)
                    {
                        BotLogger.Error(LogCategory.Cmd, "Ошибка: не удалось создать категорию 'Архив'.");
                        await command.FollowupAsync("Не удалось создать категорию 'Архив'.", ephemeral: true);
                        return;
                    }

                    await textChannel.ModifyAsync(prop => { prop.CategoryId = restCategory.Id; });
                    BotLogger.Info(LogCategory.Cmd, $"Канал {textChannel.Name} перемещён в 'Архив'.");
                }
                else
                {
                    await textChannel.ModifyAsync(prop => { prop.CategoryId = archiveCategory.Id; });
                    BotLogger.Info(LogCategory.Cmd, $"Канал {textChannel.Name} перемещён в 'Архив'.");
                }

                // Запрещаем отправку сообщений через overwrite роли @everyone (один API-вызов вместо цикла по пользователям)
                var everyoneRole = guild.EveryoneRole;
                var existing = textChannel.GetPermissionOverwrite(everyoneRole);
                var updatedOverwrite = (existing ?? OverwritePermissions.InheritAll)
                    .Modify(sendMessages: PermValue.Deny, sendMessagesInThreads: PermValue.Deny);
                await textChannel.AddPermissionOverwriteAsync(everyoneRole, updatedOverwrite);
                await LogStartup($"Запрещена отправка сообщений для @everyone в канале {textChannel.Name}.");

                await LogStartup($"Чат {textChannel.Name} перемещён в архив и закрыт. Причина: {reason}");
                await command.FollowupAsync($"Чат {textChannel.Mention} был перемещён в архив и закрыт. Причина: {reason}");
            }
            else
            {
                await LogStartup("Ошибка: команда выполнена в неподдерживаемом типе канала.");
                await command.FollowupAsync("Эта команда может быть выполнена только в текстовом канале или ветке на форуме.", ephemeral: true);
            }
        }

        [Command("open_chat")]
        public async Task OpenChat(SocketSlashCommand command)
        {
            // Отложим ответ, чтобы Discord не считал команду "зависшей"
            await command.DeferAsync();

            await LogStartup($"Команда '/open_chat' вызвана пользователем {command.User.Username} ({command.User.Id}).");

            var user = command.User as SocketGuildUser;

            if (!CanUseSuperUserActions(user))
            {
                await LogStartup($"Отказ в доступе: пользователь {command.User.Username} не имеет прав на выполнение команды.");
                await command.FollowupAsync("У вас нет прав на выполнение этой команды. Администратор оповещён.", ephemeral: true);
                return;
            }

            var channel = command.Channel as SocketGuildChannel;
            var guild = channel?.Guild;

            if (guild == null)
            {
                await command.FollowupAsync("Эта команда может быть выполнена только на сервере.", ephemeral: true);
                return;
            }

            if (channel is not SocketTextChannel textChannel)
            {
                await LogStartup("Ошибка: команда выполнена в неподдерживаемом типе канала.");
                await command.FollowupAsync("Эта команда может быть выполнена только в текстовом канале.", ephemeral: true);
                return;
            }

            // Получаем название категории из аргумента команды
            var categoryName = command.Data.Options.FirstOrDefault(opt => opt.Name == "category")?.Value?.ToString();

            if (string.IsNullOrEmpty(categoryName))
            {
                await LogStartup("Ошибка: не указана категория для перемещения.");
                await command.FollowupAsync("Не указана категория для перемещения.", ephemeral: true);
                return;
            }

            // Ищем категорию по имени
            var targetCategory = guild.CategoryChannels.FirstOrDefault(cat => cat.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));

            if (targetCategory == null)
            {
                await LogStartup($"Ошибка: категория '{categoryName}' не найдена.");
                await command.FollowupAsync($"Категория с именем '{categoryName}' не найдена.", ephemeral: true);
                return;
            }

            // Снимаем запрет отправки сообщений для @everyone через один overwrite
            var everyoneRole = guild.EveryoneRole;
            var existingOverwrite = textChannel.GetPermissionOverwrite(everyoneRole);
            if (existingOverwrite.HasValue)
            {
                var updatedOverwrite = existingOverwrite.Value.Modify(sendMessages: PermValue.Inherit, sendMessagesInThreads: PermValue.Inherit);
                await textChannel.AddPermissionOverwriteAsync(everyoneRole, updatedOverwrite);
                await LogStartup($"Снят запрет отправки сообщений для @everyone в канале {textChannel.Name}.");
            }

            // Перемещаем канал в указанную категорию
            await LogStartup($"Перемещение канала {textChannel.Name} в категорию '{targetCategory.Name}'.");
            await textChannel.ModifyAsync(prop =>
            {
                prop.CategoryId = targetCategory.Id;
            });

            await LogStartup($"Чат {textChannel.Name} открыт и перемещён в категорию '{targetCategory.Name}'.");
            await command.FollowupAsync($"Чат {textChannel.Mention} был открыт и перемещён в категорию '{targetCategory.Name}'.");
        }

        [Command("clr")]
        public async Task ClearMessages(SocketSlashCommand command, int count)
        {
            var user = command.User as SocketGuildUser;

            if (!CanUseSuperUserActions(user))
            {
                await command.RespondAsync("У вас нет необходимой роли для выполнения этой команды.", ephemeral: true);
                await LogStartup($"Ошибка: У пользователя {user?.DisplayName ?? command.User.Username} недостаточно прав для выполнения команды");
                return;
            }

            if (count < 1 || count > 100)
            {
                await command.RespondAsync("Пожалуйста, укажите число от 1 до 100.", ephemeral: true);
                await LogStartup($"Ошибка: Пользователь {user?.DisplayName ?? command.User.Username} ввёл некорректное число сообщений - {count}");
                return;
            }

            var messagesToDeleteList = await command.Channel.GetMessagesAsync(count).FlattenAsync();

            if (command.Channel is ITextChannel textChannel)
            {
                await textChannel.DeleteMessagesAsync(messagesToDeleteList);

                await command.RespondAsync(GetMessageCountString(count), ephemeral: true);
                    await LogStartup(GetMessageCountString(count));
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    await command.DeleteOriginalResponseAsync();
                });
            }
            else
            {
                await command.RespondAsync("Эта команда может быть выполнена только в текстовом канале.", ephemeral: true);
            }
        }

        private string GetMessageCountString(int count)
        {
            if (count % 10 == 1 && count % 100 != 11)
                return $"{count} сообщение удалено.";
            else if ((count % 10 >= 2 && count % 10 <= 4) && (count % 100 < 10 || count % 100 >= 20))
                return $"{count} сообщения удалено.";
            else
                return $"{count} сообщений удалено.";
        }
    }
}