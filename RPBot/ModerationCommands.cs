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
            Program.CommandLogSink?.Invoke(message);
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

                    Console.WriteLine($"[{DateTime.UtcNow}] Ветка {threadChannel.Name} закрыта. Причина: {reason}");
                    await command.FollowupAsync($"Ветка {threadChannel.Mention} была закрыта. Причина: {reason}");
                }
                catch (NotSupportedException ex)
                {
                    Console.WriteLine($"Ошибка при закрытии ветки: {ex.Message}");
                    await command.FollowupAsync("Не удалось закрыть ветку. Пожалуйста, проверьте права доступа или тип канала.");
                }
            }
            else if (channel is SocketTextChannel textChannel)
            {
                // Если это текстовый канал, перемещаем его в архив и закрываем доступ
                SocketCategoryChannel archiveCategory = guild.CategoryChannels.FirstOrDefault(cat => cat.Name == "Архив");

                /*if (archiveCategory != null && textChannel.CategoryId == archiveCategory.Id)
                {
                    Console.WriteLine($"[{DateTime.UtcNow}] Чат {textChannel.Name} уже находится в архиве.");
                    await command.FollowupAsync($"Чат {textChannel.Mention} уже находится в архиве.", ephemeral: true);
                    return;
                }*/

                if (archiveCategory == null)
                {
                    Console.WriteLine($"[{DateTime.UtcNow}] Категория 'Архив' не найдена. Создание новой категории.");
                    var restCategory = await guild.CreateCategoryChannelAsync("Архив");

                    if (restCategory == null)
                    {
                        Console.WriteLine($"[{DateTime.UtcNow}] Ошибка: не удалось создать категорию 'Архив'.");
                        await command.FollowupAsync("Не удалось создать категорию 'Архив'.", ephemeral: true);
                        return;
                    }

                    // Используем restCategory напрямую
                    await textChannel.ModifyAsync(prop =>
                    {
                        prop.CategoryId = restCategory.Id;
                    });

                    Console.WriteLine($"[{DateTime.UtcNow}] Канал {textChannel.Name} перемещён в категорию 'Архив'.");
                }
                else
                {
                    // Используем существующую категорию
                    await textChannel.ModifyAsync(prop =>
                    {
                        prop.CategoryId = archiveCategory.Id;
                    });

                    Console.WriteLine($"[{DateTime.UtcNow}] Канал {textChannel.Name} перемещён в категорию 'Архив'.");
                }

                // Закрываем доступ на отправку сообщений для всех пользователей
                // Получаем всех пользователей на сервере
                var users = await guild.GetUsersAsync().Flatten().ToListAsync();

                // Закрываем доступ на отправку сообщений для пользователей, у которых есть доступ к каналу
                foreach (var guildUser in users)
                {
                    // Проверяем, есть ли у пользователя доступ к каналу
                    var permissions = guildUser.GetPermissions(textChannel);

                    if (permissions.ViewChannel) // Если пользователь имеет доступ к каналу
                    {
                        // Получаем текущие переопределения прав для пользователя
                        var overwrite = textChannel.GetPermissionOverwrite(guildUser);

                        if (overwrite != null)
                        {
                            // Получаем текущие разрешения и запреты
                            var allow = overwrite.Value.AllowValue; // Разрешения
                            var deny = overwrite.Value.DenyValue;  // Запреты

                            // Убираем разрешение на отправку сообщений
                            allow &= ~(ulong)Discord.ChannelPermission.SendMessages;

                            // Добавляем запрет на отправку сообщений
                            deny |= (ulong)Discord.ChannelPermission.SendMessages;

                            // Создаём новые переопределения
                            var newOverwrite = new OverwritePermissions(allow, deny);

                            // Обновляем переопределение прав
                            await textChannel.AddPermissionOverwriteAsync(guildUser, newOverwrite);
                            await LogStartup($"Запрещена отправка сообщений для пользователя {guildUser.Username}.");
                        }
                        else
                        {
                            // Если переопределения нет, создаём новое с запретом на отправку сообщений
                            await textChannel.AddPermissionOverwriteAsync(guildUser, new OverwritePermissions(sendMessages: PermValue.Deny));
                            await LogStartup($"Запрещена отправка сообщений для пользователя {guildUser.Username}.");
                        }
                    }
                    await Task.Delay(100);
                }

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

            // Получаем всех пользователей на сервере
            var users = await guild.GetUsersAsync().Flatten().ToListAsync();

            // Возвращаем доступ на запись только тем, кто уже есть в чате
            foreach (var guildUser in users)
            {
                // Проверяем, есть ли у пользователя доступ к каналу
                var permissions = guildUser.GetPermissions(textChannel);

                if (permissions.ViewChannel) // Если пользователь имеет доступ к каналу
                {
                    // Получаем текущие переопределения прав для пользователя
                    var overwrite = textChannel.GetPermissionOverwrite(guildUser);

                    if (overwrite != null)
                    {
                        // Получаем текущие разрешения и запреты
                        var allow = overwrite.Value.AllowValue; // Разрешения
                        var deny = overwrite.Value.DenyValue;  // Запреты

                        // Убираем запрет на отправку сообщений
                        deny &= ~(ulong)Discord.ChannelPermission.SendMessages;

                        // Добавляем разрешение на отправку сообщений
                        allow |= (ulong)Discord.ChannelPermission.SendMessages;

                        // Создаём новые переопределения
                        var newOverwrite = new OverwritePermissions(allow, deny);

                        // Обновляем переопределение прав
                        await textChannel.AddPermissionOverwriteAsync(guildUser, newOverwrite);
                        await LogStartup($"Возвращена возможность отправки сообщений для пользователя {guildUser.Username}.");
                    }
                    else
                    {
                        // Если переопределения нет, создаём новое с разрешением на отправку сообщений
                        await textChannel.AddPermissionOverwriteAsync(guildUser, new OverwritePermissions(sendMessages: PermValue.Allow));
                        await LogStartup($"Возвращена возможность отправки сообщений для пользователя {guildUser.Username}.");
                    }
                }
                await Task.Delay(100);
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