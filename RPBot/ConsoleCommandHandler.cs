using Discord;
using Discord.WebSocket;
using DiscordBot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RPBot
{
    public class ConsoleCommandHandler
    {
        private readonly DiscordSocketClient _client;
        private readonly IBotController _botController;


        public ConsoleCommandHandler(DiscordSocketClient client, IBotController botController)
        {
            _client = client;
            _botController = botController;
        }

        public async Task StartListening()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var input = Console.ReadLine();
                    if (!string.IsNullOrWhiteSpace(input))
                    {
                        await ProcessCommand(input.Trim());
                    }
                    await Task.Delay(100);
                }
            });
        }

        private async Task ProcessCommand(string command)
        {
            try
            {
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return;

                var cmd = parts[0].ToLower();
                var args = parts.Skip(1).ToArray();

                switch (cmd)
                {
                    case "help":
                        ShowHelp();
                        break;
                    case "status":
                        await ShowStatus();
                        break;
                    case "servers":
                        await ListServers();
                        break;
                    case "channels":
                        await ListChannels(args);
                        break;
                    case "send":
                        await SendMessage(args);
                        break;
                    case "restart":
                        await RestartBot();
                        break;
                    case "stop":
                        await StopBot();
                        break;
                    case "sessions":
                        await ListSessions();
                        break;
                    case "broadcast":
                        await BroadcastMessage(args);
                        break;
                    default:
                        Console.WriteLine($"Неизвестная команда: {cmd}. Введите 'help' для списка команд.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка выполнения команды: {ex.Message}");
            }
        }

        private void ShowHelp()
        {
            Console.WriteLine("=== Доступные команды ===");
            Console.WriteLine("help - Показать эту справку");
            Console.WriteLine("status - Статус бота");
            Console.WriteLine("servers - Список серверов");
            Console.WriteLine("channels [server_id] - Список каналов на сервере");
            Console.WriteLine("send [channel_id] [message] - Отправить сообщение в канал");
            Console.WriteLine("broadcast [server_id] [message] - Отправить сообщение во все каналы сервера");
            Console.WriteLine("sessions - Список активных игровых сессий");
            Console.WriteLine("restart - Перезапустить бота");
            Console.WriteLine("stop - Остановить бота");
            Console.WriteLine("=========================");
        }

        private async Task ShowStatus()
        {
            Console.WriteLine($"=== Статус бота ===");
            Console.WriteLine($"Состояние: {_client.ConnectionState}");
            Console.WriteLine($"Логин: {_client.CurrentUser?.Username}");
            Console.WriteLine($"ID: {_client.CurrentUser?.Id}");
            Console.WriteLine($"Серверов: {_client.Guilds.Count}");
            Console.WriteLine($"Пинг: {_client.Latency}ms");
            Console.WriteLine($"Uptime: {DateTime.Now - Process.GetCurrentProcess().StartTime:hh\\:mm\\:ss}");
            Console.WriteLine($"===================");
        }

        private async Task ListServers()
        {
            Console.WriteLine($"=== Серверы ({_client.Guilds.Count}) ===");
            foreach (var guild in _client.Guilds)
            {
                Console.WriteLine($"{guild.Id} - {guild.Name} (Участников: {guild.MemberCount})");
            }
            Console.WriteLine($"=======================");
        }

        private async Task ListChannels(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Использование: channels [server_id]");
                return;
            }

            if (!ulong.TryParse(args[0], out var serverId))
            {
                Console.WriteLine("Неверный ID сервера");
                return;
            }

            var guild = _client.GetGuild(serverId);
            if (guild == null)
            {
                Console.WriteLine("Сервер не найден");
                return;
            }

            Console.WriteLine($"=== Каналы сервера {guild.Name} ===");

            Console.WriteLine("\nТекстовые каналы:");
            foreach (var channel in guild.TextChannels.OrderBy(c => c.Position))
            {
                Console.WriteLine($"  #{channel.Name} ({channel.Id})");
            }

            Console.WriteLine("\nГолосовые каналы:");
            foreach (var channel in guild.VoiceChannels.OrderBy(c => c.Position))
            {
                Console.WriteLine($"  {channel.Name} ({channel.Id})");
            }
            Console.WriteLine($"================================");
        }

        private async Task SendMessage(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Использование: send [channel_id] [message]");
                return;
            }

            if (!ulong.TryParse(args[0], out var channelId))
            {
                Console.WriteLine("Неверный ID канала");
                return;
            }

            var channel = _client.GetChannel(channelId) as IMessageChannel;
            if (channel == null)
            {
                Console.WriteLine("Канал не найден");
                return;
            }

            var message = string.Join(" ", args.Skip(1));
            await channel.SendMessageAsync($"[Админ] {message}");
            Console.WriteLine($"Сообщение отправлено в #{channel.Name}");
        }

        private async Task BroadcastMessage(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Использование: broadcast [server_id] [message]");
                return;
            }

            if (!ulong.TryParse(args[0], out var serverId))
            {
                Console.WriteLine("Неверный ID сервера");
                return;
            }

            var guild = _client.GetGuild(serverId);
            if (guild == null)
            {
                Console.WriteLine("Сервер не найден");
                return;
            }

            var message = string.Join(" ", args.Skip(1));
            var sentCount = 0;

            foreach (var channel in guild.TextChannels.Where(c => c is ITextChannel))
            {
                try
                {
                    var textChannel = channel as ITextChannel;
                    if (textChannel != null)
                    {
                        await textChannel.SendMessageAsync($"[Объявление] {message}");
                        sentCount++;
                        await Task.Delay(200); // Чтобы не превысить лимиты Discord
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка отправки в #{channel.Name}: {ex.Message}");
                }
            }

            Console.WriteLine($"Сообщение отправлено в {sentCount} каналов сервера {guild.Name}");
        }

        private async Task ListSessions()
        {
            Console.WriteLine($"=== Активные игровые сессии ===");

            foreach (var guildSessions in GameSessionCommands._sessions)
            {
                var guild = _client.GetGuild(guildSessions.Key);
                Console.WriteLine($"\nСервер: {guild?.Name ?? "Unknown"} ({guildSessions.Key})");

                foreach (var session in guildSessions.Value.Values.Where(s => !s.IsStopped))
                {
                    var status = session.IsPaused ? "На паузе" : "Активна";
                    var rolls = session.TrackRolls ? $" (бросков: {session.Rolls.Count})" : "";
                    Console.WriteLine($"  {session.GameName} - {status}{rolls}");
                    Console.WriteLine($"    ID: {session.SessionId}, Мастер: {session.MasterName}");
                    Console.WriteLine($"    Начало: {session.StartTime:dd.MM.yyyy HH:mm}");
                }
            }

            if (!GameSessionCommands._sessions.Any())
            {
                Console.WriteLine("Активных сессий нет");
            }
            Console.WriteLine($"===============================");
        }

        private async Task RestartBot()
        {
            Console.WriteLine("Перезапуск бота...");
            await _botController.RestartAsync();
        }

        private async Task StopBot()
        {
            Console.WriteLine("Остановка бота...");
            await _botController.StopAsync();
        }
    }
}
