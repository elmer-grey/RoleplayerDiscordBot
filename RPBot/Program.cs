using System;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using RPBot;
using System.Text.RegularExpressions;
using System.IO;

namespace DiscordBot
{
    class Program
    {
        private DiscordSocketClient _client;
        private CommandService _commandService;
        private IServiceProvider _services;
        private CommandHandler _commandHandler;

        static void Main(string[] args) => new Program().RunBotAsync().GetAwaiter().GetResult();

        public Program()
        {
            var config = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers | GatewayIntents.GuildMessages |
                GatewayIntents.MessageContent | GatewayIntents.GuildScheduledEvents,

                ConnectionTimeout = 15000,
                MessageCacheSize = 100,
                LogLevel = LogSeverity.Info 

            };
            _client = new DiscordSocketClient(config);
            _commandService = new CommandService();

            //var guild = _client.GetGuild(1288192593137635359);
            //var guild = _client.GetGuild(295189463376855040); //Переключить в субботу! //Канал "КнР"
            var services = new ServiceCollection()
                .AddSingleton(_client)
                .AddSingleton(_commandService)
                .AddSingleton<QueueModule>()
                .AddSingleton<InfoCommands>()
                .AddSingleton<RollDiceCommands>()
                .AddSingleton<GameSessionCommands>()
                .BuildServiceProvider();

            _services = services;
        }

        public async Task RunBotAsync()
        {
            Console.WriteLine("Инициализация бота...");
            LogToFile($"Инициализация бота в {DateTime.Now}.");
            _client.Log += Log;
            _client.UserJoined += UserJoined;
            _client.MessageReceived += HandleCommandAsync;
            _client.Ready += OnReady;
            _client.SlashCommandExecuted += OnSlashCommandExecuted;
            _client.GuildScheduledEventStarted += (guildEvent) => GameSessionCommands.OnGuildScheduledEventStarted(guildEvent, _client);
            await _client.LoginAsync(TokenType.Bot, "MTMzMTYyODkxMDE1MjEyMjM4OA.GzWsZE.WJgvlfflP5wkFxFGqce6tK3mDYOygSvc0q2TBk");
            Console.WriteLine("Вход выполнен успешно.");
            await _client.StartAsync();
            Console.WriteLine("Бот запущен и подключен к Discord.");
            //await RegisterSlashCommandsAsync(_services);
            //await _commandService.AddModulesAsync(Assembly.GetEntryAssembly(), _services);

            _commandHandler = new CommandHandler(_client);
            await Task.Delay(-1);
        }
        /*private static async Task RegisterSlashCommandsAsync(IServiceProvider services)
        {
            var commandService = services.GetRequiredService<CommandService>();
            //await commandService.AddModulesAsync(Assembly.GetEntryAssembly(), services);
        }*/
        private async Task OnReady()
        {
            await _commandHandler.InitializeAsync();
            Console.WriteLine("Команды инициализированы.");
            LogToFile($"Команды инициализированы в {DateTime.Now}.");

            await _commandHandler.ListSlashCommandsAsync();
            Console.WriteLine($"Bot is connected as {_client.CurrentUser}");

            ulong channelId = 1288192593137635362; // ID канала - 373788351246893056 (КнР), 1288192593137635362 (тест)

            // Получаем канал
            var channel = _client.GetChannel(channelId) as ITextChannel;

            // Проверяем, что канал существует и это текстовый канал
            if (channel != null)
            {
                // Отправляем сообщение в канал
                await channel.SendMessageAsync("Все системы активны. Ожидаю сообщение от пользователя...");
                LogToFile($"Запуск всех систем в {DateTime.Now}.");
            }
            else
            {
                Console.WriteLine($"Канал с ID {channelId} не найден или это не текстовый канал.");
                LogToFile($"Канал с ID {channelId} не найден или это не текстовый канал.");

            }
        }

        private async Task HandleCommandAsync(SocketMessage arg)
        {
            if (arg is not SocketUserMessage message || message.Author.IsBot) return;

            var context = new SocketCommandContext(_client, message);
            var user = message.Author as SocketGuildUser;

            var (fludChannelId, specificChannelId, responseMessage, emoji, lineMessages, emoteKappa, emoteAga) = GetResponseData(message);
            
            if (message.Content.ToLower() == "👏")
            {
                if (user != null)
                {
                    await message.DeleteAsync();
                    await message.Channel.SendMessageAsync("КРАСИВО 🔥 ВЕЛИКОЛЕПНО 🔥 ЗАМЕЧАТЕЛЬНО 🔥 ПРЕКРАСНО 🔥 СУПЕР 🔥 УМОПОМРАЧИТЕЛЬНО 🔥 СНОГШИБАТЕЛЬНО 🔥 ПРЕВОСХОДНО 🔥 ШИКАРНО");
                }
                Console.WriteLine("Хлопание");
            }

            if (message.Content.ToLower().Contains("диктатор"))
            {
                await HandleDictatorCommand(user, message);
            }

            if (message.Content.ToLower().Contains("мастерский произвол"))
            {
                await HandleMasteryArbitrarinessCommand(user, message, emoteKappa, emoteAga);
            }

            if (message.Content.ToLower().Contains("бот, спокойной ночи") && user.Username == "perekrestok_mirov")
            {
                await message.Channel.SendMessageAsync("Отключение всех систем...");
                Console.WriteLine($"Бот отключен пользователем {message.Author.Username} в {DateTime.Now}.");
                LogToFile($"Бот отключен пользователем {message.Author.Username} в {DateTime.Now}.");
                await _client.StopAsync();
                Environment.Exit(0);
            }

            if (message.Content.ToLower().Contains("бот, перезагрузка") && user.Username == "perekrestok_mirov")
            {
                await message.Channel.SendMessageAsync("Бот будет перезагружен. Пожалуйста, подождите...");
                Console.WriteLine($"Инициализация перезагрузки пользователем {message.Author.Username} в {DateTime.Now}.");
                LogToFile($"Инициализация перезагрузки пользователем {message.Author.Username} в {DateTime.Now}.");
                System.Diagnostics.Process.Start(AppDomain.CurrentDomain.FriendlyName);
                await _client.StopAsync();
                Environment.Exit(0);
            }

            if (message.Content.ToLower().Contains("привет, ролевой бот") && message.Channel.Id == fludChannelId)
            {
                await HandleGreetingCommand(user, message, responseMessage, emoji);
            }

            if ((message.Content.ToLower() == "line" || message.Content.ToLower() == "ход") && message.Channel.Id == specificChannelId)
            {
                await HandleLineCommand(user, message, lineMessages);
            }
        }

        private (ulong fludChannelId, ulong specificChannelId, string responseMessage, string emoji, string lineMessages, string emoteKappa, string emoteAga) GetResponseData(SocketMessage message)
        {
            var channel = message.Channel as SocketGuildChannel;
            // ID КнР: 295189463376855040 Канал для Флуда: 373788351246893056 Канал для Линии (броски кубов): 710471746108784691
            // ID Тест: 1288192593137635359 Канал для Флуда: 1288192593137635362 Канал для Линии: 1333559817045807176
            
            if (channel != null)
            {
                switch (channel.Guild.Id)
                {
                    case 1288192593137635359: // Место для Тест
                        return (1288192593137635362, 1333559817045807176,
                                "Приветствую тебя, {user.Mention}! О всех проблемах, которые могут со мной возникнуть, передай, пожалуйста, администратору, или воспользуйся командой `/bug_report`. Хорошо? \nСоветую первой командой использовать `/help`",
                                "👋",
                                "<:begin:1333879098488918098><:middle1:1333879112510472254><:middle2:1333879114440118334>" +
                                "<:middle3:1333879116281151550><:end1:1333879106747633735>",
                                "<:kappa:1333879110602326046>", // emoteKappa
                                "<:agakakskagesh:1333878999977431174>"); // emoteAga
                    case 295189463376855040: // Место для КнР
                        return (373788351246893056, 710471746108784691,
                                "Приветствую тебя, {user.Mention}! О всех проблемах, которые могут со мной возникнуть, передай, пожалуйста, администратору, или воспользуйся командой `/bug_report`. Хорошо? \nСоветую первой командой использовать `/help`",
                                "👋",
                                "<:1begin:1151822634250686504><:2middle1:1151822618677219328><:3middle2:1151822625216155649>" +
                                "<:4middle3:1151822621198000169><:5end:1151822629381087292>",
                                "<:kappa:1100150992428871720>", // emoteKappa
                                "<:Agakakskagesh:1316461730569916557>"); // emoteAga
                    default:
                        return (0, 0, null, null, null, null, null); // Если гильдия не распознана
                }
            }
            return (0, 0, null, null, null, null, null); // Если канал не является гильдийским
        }

        private async Task HandleGreetingCommand(SocketGuildUser user, SocketMessage message, string responseMessage, string emoji)
        {

            await message.AddReactionAsync(new Emoji(emoji));

            if (user.Username == "perekrestok_mirov" || user.Username == "domen_")
            {
                int reportCount = GetBugReportCounter();
                await message.Channel.SendMessageAsync($"Приветствую тебя, Админ. Все системы в норме. Отчётов о неисправности: {reportCount}");
            }
            else
            {
                var formattedMessage = responseMessage.Replace("{user.Mention}", user.Mention);
                var responseMessageObj = await message.Channel.SendMessageAsync(formattedMessage);
                await responseMessageObj.AddReactionAsync(new Emoji("✅"));
            }
            Console.WriteLine("Приветствие с ботом");
        }

        private async Task HandleDictatorCommand(SocketGuildUser user, SocketMessage message)
        {
            await message.DeleteAsync();

            if (user != null)
            {
                var guildChannel = message.Channel as SocketGuildChannel;
                var guild = guildChannel?.Guild;

                if (guild != null)
                {
                    var muteRole = guild.Roles.FirstOrDefault(r => r.Name == "Mute");
                    if (muteRole != null)
                    {
                        await user.AddRoleAsync(muteRole);

                        var punishmentMessage = await message.Channel.SendMessageAsync($"Ахаха, {user.Mention} досанабился!");
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(3000);
                            await punishmentMessage.DeleteAsync();
                            await user.RemoveRoleAsync(muteRole);
                        });
                    }
                }
            }
        }

        private async Task HandleMasteryArbitrarinessCommand(SocketGuildUser user, SocketMessage message, string emoteKappa, string emoteAga)
        {
            if (user != null)
            {
                var emote = Emote.Parse(emoteAga);
                await message.AddReactionAsync(emote);
                var messageReference = new MessageReference(message.Id);

                var responseMessage = await message.Channel.SendMessageAsync($"Ууу, сука! Скажи, да?!",
                    messageReference: messageReference);

                emote = Emote.Parse(emoteKappa); 
                await responseMessage.AddReactionAsync(emote);
            }
            Console.WriteLine("Произволит");
        }

        private async Task HandleLineCommand(SocketGuildUser user, SocketMessage message, string lineMessages)
        {
            if (user != null)
            {
                await message.DeleteAsync();

                await message.Channel.SendMessageAsync(lineMessages);
            }
            Console.WriteLine("Линия отправлена");
        }

        private int GetBugReportCounter()
        {
            string counterFilePath = @"C:\Favorites\Desktop\НРИ\Discord_BR\bug_report_counter.txt";

            if (File.Exists(counterFilePath))
            {
                return int.Parse(File.ReadAllText(counterFilePath));
            }

            return 0;
        }

        private async Task OnSlashCommandExecuted(SocketSlashCommand command)
        {
            switch (command.Data.Name)
            {
                case "stop_q":
                    await StopQueue(command);
                    break;
                case "queue":
                    await QueueCommand(command);
                    break;
                case "q":
                    await Q_InCommand(command);
                    break;
                case "clr":
                    await ClearMessage(command);
                    break;
                case "roll":
                    await RollCommand(command);
                    break;
                case "serverinfo":
                    await ServerInfoCommand(command);
                    break;
                case "help":
                    await HelpCommand(command);
                    break;
                case "help_r":
                    await Help_RollCommand(command);
                    break;
                case "help_gs":
                    await Help_GameSessionCommand(command);
                    break;
                case "bug_report":
                    await Bug_ReportCommand(command);
                    break;
                case "start":
                    await StartGameSession(command);
                    break;
                case "pause":
                    await PauseGameSession(command);
                    break;
                case "resume":
                    await ResumeGameSession(command);
                    break;
                case "stop":
                    await StopGameSession(command);
                    break;
                default:
                    await command.RespondAsync("Команда не распознана.");
                    break;
            }
        }

        private async Task StopQueue(SocketSlashCommand command)
        {
            var queueModule = _services.GetService<QueueModule>();
            await queueModule.StopQueue(command);
            Console.WriteLine("Очередь остановлена.");
        }

        private async Task QueueCommand(SocketSlashCommand command)
        {
            var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
            if (int.TryParse(inputOption?.Value?.ToString(), out int participantsCount))
            {
                var queueModule = _services.GetService<QueueModule>();
                await queueModule.QueueCommand(command, participantsCount);
            }
            else
            {
                await command.RespondAsync("Ошибка: неверный формат ввода. Пожалуйста, введите целое число.");
                Console.WriteLine("Ошибка: неверный формат ввода. Пожалуйста, введите целое число.");
            }
        }

        private async Task Q_InCommand(SocketSlashCommand command)
        {
            var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
            var input = inputOption?.Value?.ToString();

            var queueModule = new QueueModule();
            await queueModule.QIn_RollDice(command, input);
        }

        private async Task ClearMessage(SocketSlashCommand command)
        {
            var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
            if (int.TryParse(inputOption?.Value?.ToString(), out int messagesToDelete))
            {
                var infoModule = _services.GetService<InfoCommands>();
                await infoModule.ClearMessages(command, messagesToDelete);
            }
            else
            {
                Console.WriteLine("Ошибка: неверный формат ввода. Пожалуйста, введите целое число.");
                await command.RespondAsync("Ошибка: неверный формат ввода. Пожалуйста, введите целое число.", ephemeral: true);
            }
        }

        private async Task RollCommand(SocketSlashCommand command)
        {
            var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
            var input = inputOption?.Value?.ToString();

            var diceModule = _services.GetService<RollDiceCommands>();
            await diceModule.RollDice(command, input);
        }

        private async Task ServerInfoCommand(SocketSlashCommand command)
        {
            var infoModule = _services.GetService<InfoCommands>();
            await infoModule.ServerInfo(command);
            Console.WriteLine("Выведена информация о сервере.");
        }

        private async Task HelpCommand(SocketSlashCommand command)
        {
            var infoModule = _services.GetService<InfoCommands>();
            await infoModule.Help(command);
            Console.WriteLine("Выведена подсказка о командах.");
        }

        private async Task Help_RollCommand(SocketSlashCommand command)
        {
            var infoModule = _services.GetService<InfoCommands>();
            await infoModule.Help_R(command);
            Console.WriteLine("Выведена подсказка о командах для бросков кубов.");
        }

        private async Task Help_GameSessionCommand(SocketSlashCommand command)
        {
            var infoModule = _services.GetService<InfoCommands>();
            await infoModule.Help_GS(command);
            Console.WriteLine("Выведена подсказка о командах для статистики.");
        }

        private async Task Bug_ReportCommand(SocketSlashCommand command)
        {
            var inputOption = command.Data.Options.FirstOrDefault(o => o.Name == "input");
            var input = inputOption?.Value?.ToString();

            var infoModule = _services.GetService<InfoCommands>();
            await infoModule.Bug_Report(command, input);
            Console.WriteLine("Использовано уведомление администратора о баге.");            
        }

        private async Task StartGameSession(SocketSlashCommand command)
        {
            var gameNameOption = command.Data.Options.FirstOrDefault(o => o.Name == "game_name");
            var gameName = gameNameOption?.Value?.ToString();

            var gameSessionModule = _services.GetService<GameSessionCommands>();
            await gameSessionModule.StartGameSession(command, gameName);
            Console.WriteLine("Игра начата.");
        }

        private async Task PauseGameSession(SocketSlashCommand command)
        {
            var gameSessionModule = _services.GetService<GameSessionCommands>();
            await gameSessionModule.PauseGameSession(command);
            Console.WriteLine("Игра на паузе.");
        }

        private async Task ResumeGameSession(SocketSlashCommand command)
        {
            var gameSessionModule = _services.GetService<GameSessionCommands>();
            await gameSessionModule.ResumeGameSession(command);
            Console.WriteLine("Игра снята с паузы.");
        }

        private async Task StopGameSession(SocketSlashCommand command)
        {
            var gameSessionModule = _services.GetService<GameSessionCommands>();
            await gameSessionModule.StopGameSession(command);
            Console.WriteLine("Игра остановлена.");
        }

        private Task Log(LogMessage arg)
        {
            string path = @"C:\Favorites\Desktop\НРИ\Discord_BR\LogFile.txt"; // Укажите путь к вашему файлу
            Console.WriteLine(arg);
            using (StreamWriter writer = new StreamWriter(path, true)) // true для добавления в конец файла
            {
                writer.WriteLine(arg);
            }
            return Task.CompletedTask;
        }

        private void LogToFile(string message)
        {
            string path = @"C:\Favorites\Desktop\НРИ\Discord_BR\LogFile.txt"; // Укажите путь к вашему файлу
            if (!File.Exists(path))
            {
                using (File.Create(path)) { } // Создаем файл, если он не существует
            }

            // Записываем сообщение в файл
            using (StreamWriter writer = new StreamWriter(path, true)) // true для добавления в конец файла
            {
                writer.WriteLine(DateTime.Now.ToString("\ndd-MM-yyyy"));
                writer.WriteLine(message);
            }
        }

        private async Task UserJoined(SocketGuildUser user) 
        {
            Console.WriteLine($"{user.Username} joined the server.");
            try
            {
                var guild = user.Guild;

                if (guild.Id == 295189463376855040) // ID КнР
                {
                    var welcomeChannel = _client.GetChannel(373788351246893056) as IMessageChannel;
                    if (welcomeChannel != null)
                    {
                        Console.WriteLine($"Sending message to channel: {welcomeChannel.Name}");
                        await welcomeChannel.SendMessageAsync($"Привет, {user.Mention}!");
                    }
                    else
                    {
                        Console.WriteLine("Welcome channel not found.");
                    }
                }
                else if (guild.Id == 1288192593137635359) // ID Тест
                {
                    var welcomeChannel = _client.GetChannel(1288192593137635362) as IMessageChannel;
                    if (welcomeChannel != null)
                    {
                        Console.WriteLine($"Sending message to channel: {welcomeChannel.Name}");
                        await welcomeChannel.SendMessageAsync($"Привет, {user.Mention}!");
                    }
                    else
                    {
                        Console.WriteLine("Welcome channel not found.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }

    public class RollDiceCommands : ModuleBase<SocketCommandContext>
    {
        private string ValidateRollInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "Ввод не может быть пустым.";

            if (!input.Contains('d', StringComparison.OrdinalIgnoreCase))
                return "Ввод должен содержать символ `d` (например, `2d6`).";

            var parts = input.Split(new[] { 'd', '+', '-' }, StringSplitOptions.RemoveEmptyEntries);

            // Проверяем количество частей
            if (parts.Length < 1 || parts.Length > 3)
                return "Некорректное количество параметров. Используйте формат `XdY`, `dY`, `XdY+Z`, `XdY-Z`, `Xd[min,max]+Z` или `d[min,max]-Z`.";

            // Проверяем, что все части являются числами
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out _) && !part.StartsWith('[') && !part.EndsWith(']'))
                    return $"Некорректное значение: `{part}`. Ожидается число или диапазон в формате `[min,max]`.";
            }

            // Проверяем диапазоны (если есть)
            if (input.Contains('[') || input.Contains(']'))
            {
                var rangePattern = @"\[\d+,\d+\]";
                if (!Regex.IsMatch(input, rangePattern))
                    return "Некорректный формат диапазона. Используйте `[min,max]`, где `min` и `max` — числа.";
            }

            // Если всё в порядке, возвращаем null
            return null;
        }

        [Command("roll")]
        public async Task RollDice(SocketSlashCommand command, string input)
        {
            Console.WriteLine($"\nБыло введено условие: {input}");
            var user = command.User as SocketGuildUser;
            if (user == null)
            {
                await command.RespondAsync("Не удалось получить информацию о пользователе.");
                return;
            }

            var _input = input.Trim();

            // Проверяем ввод
            var errorMessage = ValidateRollInput(_input);
            if (errorMessage != null)
            {
                await command.RespondAsync($"Ошибка: {errorMessage}");
                Console.WriteLine($"Предупреждение: Был введён неверный формат. Ошибка: {errorMessage}");
                return;
            }

            // Если ввод корректен, продолжаем обработку
            var match = Regex.Match(_input, @"^(?:(?:(\d*)d(\d+)|d(\d+))([+-]\d+)?|(?:(\d+)\[(\d+),(\d+)\])([+-]\d+)?|(?:(\d*)d\[(\d+),(\d+)\])([+-]\d+)?)$", RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                await command.RespondAsync("Неверный формат! Используйте `XdY`, `dY`, `XdY+Z`, `XdY-Z`, `Xd[min,max]+Z` или `d[min,max]-Z`, где `X`, `Y`, `Z` — строго больше 0.");
                Console.WriteLine("Предупреждение: Был введён неверный формат.");
                return;
            }

            int count = 1;
            int min = 1;
            int max = 1;
            int modifier = 0;

            if (match.Groups[1].Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)) // Обрабатываем формат XdY
            {
                count = int.Parse(match.Groups[1].Value);
                max = int.Parse(match.Groups[2].Value);
                if (match.Groups[4].Success)
                {
                    modifier = int.Parse(match.Groups[4].Value);
                }
            }
            else if (match.Groups[2].Success) // Обрабатываем формат dY
            {
                max = int.Parse(match.Groups[2].Value);
                count = 1;
                if (match.Groups[4].Success)
                {
                    modifier = int.Parse(match.Groups[4].Value);
                }
            }
            else if (match.Groups[4].Success) // Обрабатываем формат d[min,max]
            {
                max = int.Parse(match.Groups[4].Value);
                count = 1;
                if (match.Groups[5].Success)
                {
                    modifier = int.Parse(match.Groups[5].Value);
                }
            }
            else if (match.Groups[6].Success) // Обрабатываем формат Xd[min,max]
            {
                count = int.Parse(match.Groups[6].Value);
                min = int.Parse(match.Groups[7].Value);
                max = int.Parse(match.Groups[8].Value);
                if (min >= max)
                {
                    await command.RespondAsync("Минимальное значение должно быть меньше максимального.", ephemeral: true);
                    return;
                }
            }
            else if (match.Groups[9].Success) // Обрабатываем формат Xd[min,max]+Z или Xd[min,max]-Z
            {
                if (!string.IsNullOrWhiteSpace(match.Groups[9].Value))
                    count = int.Parse(match.Groups[9].Value);
                min = int.Parse(match.Groups[10].Value);
                max = int.Parse(match.Groups[11].Value);
                if (min >= max)
                {
                    await command.RespondAsync("Минимальное значение должно быть меньше максимального.", ephemeral: true);
                    return;
                }
                if (match.Groups[12].Success)
                {
                    modifier = int.Parse(match.Groups[12].Value);
                }
            }

            if (count <= 0 || max <= 0)
            {
                await command.RespondAsync("Количество бросков и верхняя граница должны быть больше нуля.", ephemeral: true);
                return;
            }
            if (count > 20)
            {
                await command.RespondAsync("Давайте сильно не наглеть? 20 бросков - это максимум.", ephemeral: true);
                return;
            }

            Random random = new Random();
            List<int> results;

            // Обработка случая d20 отдельно
            if (max == 20 && count == 1 && modifier == 0)
            {
                /*if (user.Username == "perekrestok_mirov")
                {
                    var result1 = random.Next(15, 21);
                    Console.WriteLine($"Полученное значение: {result1}");
                    var filePath1 = Path.Combine("Numbers", $"{result1}.png");
                    Color embedColor1 = GetGradientColor(result1, 1, max);

                    if (File.Exists(filePath1))
                    {
                        var embed = new EmbedBuilder()
                            .WithTitle($"Результат броска {user.DisplayName}: {result1}")
                            .WithImageUrl($"attachment://{Path.GetFileName(filePath1)}")
                            .WithColor(embedColor1)
                            .Build();
                        // Отправляем сообщение с файлом и embed без присвоения результата
                        await command.RespondWithFileAsync(filePath1, embed: embed, isTTS: false, allowedMentions: null);
                        return;
                    }
                }
                else
                {*/
                var result = random.Next(1, max + 1);
                Console.WriteLine($"Полученное значение: {result}");
                var filePath = Path.Combine("Numbers", $"{result}.png");
                Color embedColor = GetGradientColor(result, 1, max);

                if (File.Exists(filePath))
                {
                    var embed = new EmbedBuilder()
                        //.WithTitle($"Результат броска {user.DisplayName}: {result}")
                        .WithImageUrl($"attachment://{Path.GetFileName(filePath)}")
                        .WithColor(embedColor)
                        .Build();
                    await command.RespondWithFileAsync(filePath, embed: embed, isTTS: false, allowedMentions: null);
                }
                else
                {
                    Console.WriteLine($"Ошибка: Не найдено изображение для значения \"{result}\"!");
                    await command.RespondAsync("Изображение не найдено. Пожалуйста, сообщите об этом через команду `/bug_report`.", ephemeral: true);
                }
                return;
                //}
            }

            results = Enumerable.Range(0, count).Select(_ => {
                int result = random.Next(min, max + 1);
                Console.WriteLine($"Полученное значение: {result}");
                return result;
            }).ToList();

            string resultMessage = "__**Результат броска**__\n" +
                                   "```plaintext\n";

            if (count == 1)
            {
                var rolledValue = results[0];
                int finalValue = rolledValue + modifier;

                if (modifier != 0)
                {
                    resultMessage += $"Выпавшее значение: {rolledValue}\n" +
                                     $"Модификатор: {modifier}\n" +
                                     $"Полученное значение: {finalValue}\n";
                }
                else
                {
                    resultMessage += $"Полученное значение: {rolledValue}\n";
                }
            }
            else
            {
                for (int i = 0; i < results.Count; i++)
                {
                    var rolledValue = results[i];
                    int finalValue = rolledValue + modifier;

                    if (modifier != 0)
                    {
                        resultMessage += $"Бросок {i + 1}: Выпавшее значение: {rolledValue}\n" +
                                         $"Модификатор: {modifier}\n" +
                                         $"Полученное значение: {finalValue}\n";
                    }
                    else
                    {
                        resultMessage += $"Бросок {i + 1}: Полученное значение: {rolledValue}\n";
                    }
                }
            }

            resultMessage += "```";

            await command.RespondAsync(resultMessage);
            Console.WriteLine($"Совершён бросок. {resultMessage}");
        }

        private Color GetGradientColor(int value, int minValue, int maxValue)
        {
            float normalizedValue = (float)(value - minValue) / (maxValue - minValue);

            int r, g, b;

            if (normalizedValue < 0.5f) // от 1 до 10
            {
                // Красный к желтому
                r = 255;
                g = (int)(255 * (normalizedValue * 2)); // Увеличиваем зеленый
                b = 0;
            }
            else // от 10 до 20
            {
                // Желтый к зеленому
                r = (int)(255 * (1 - (normalizedValue - 0.5f) * 2)); // Уменьшаем красный
                g = 255;
                b = 0;
            }

            return new Color(r, g, b);
        }
    }

    public class InfoCommands : ModuleBase<SocketCommandContext>
    {
        [Command("help")]
        public async Task Help(SocketSlashCommand command)
        {
            var helpMessage = new StringBuilder();

            helpMessage.AppendLine("**Список доступных команд:**");
            helpMessage.AppendLine("> `/help` - Вы находитесь здесь.");
            helpMessage.AppendLine("> `/help_r` - Выводит список команд, где указаны все вариации для бросков кубов.");
            helpMessage.AppendLine("> `/help_gs` - Выводит список команд, которые используются для подсчёта времени игры.");
            helpMessage.AppendLine("> `/bug_report` - Позволяет отправить администратору сообщение об ошибке, " +
                "которая связана с ботом, или любое ваше предложение по его улучшению.");
            helpMessage.AppendLine("> `/serverinfo` - Показывает очень краткую информацию о сервере.");
            helpMessage.AppendLine("> `/roll XdY` - Выполняет заданное количество `Х` бросков кубика заданного номинала `Y`. " +
                "Если оставить аргумент `X` пустым, то будет совершён один бросок.");
            helpMessage.AppendLine("> `/queue Z` - Запускает очередь с необходимым `Z` количеством персонажей в сцене. __Доступ ограничен__");
            helpMessage.AppendLine("> `/q dY` - **Только при активной очереди!** Совершается бросок кубика выбранного номинала `Y`. " +
                "Добавляет вас в очередь на выполнение действия. Может быть использована несколько раз одним человеком.");
            helpMessage.AppendLine("> `/stop_q` - Останавливает текущую очередь, если она активна. __Доступ ограничен__");
            helpMessage.AppendLine("> `/clr X` - Удаляет выбранное количество сообщений `X`. __Доступ ограничен__");
            helpMessage.AppendLine("-# *Некоторые пасхалки скрыты в коде. Количество команд будет добавляться. Данный бот всё ещё в разработке.*");
            helpMessage.AppendLine("-# *При обнаружении некорректной работы бота сообщите об этом в `/bug_report`*");

            await command.RespondAsync(helpMessage.ToString());
        }

        [Command("serverinfo")]
        public async Task ServerInfo(SocketSlashCommand command)
        {
            var server = (command.Channel as SocketGuildChannel)?.Guild;

            if (server == null)
            {
                await command.RespondAsync("Не удалось получить информацию о сервере.");
                return;
            }
            // Получаем информацию о участниках
            int totalMembers = server.MemberCount;
            int onlineMembers = server.Users.Count(u => u.Status == UserStatus.Offline);
            int botCount = server.Users.Count(u => u.IsBot);
            int humanCount = totalMembers - botCount;
            var embed = new EmbedBuilder()
                .WithTitle("Информация о сервере")
                .AddField("Название", server.Name)
                .AddField("Создан", server.CreatedAt)
                .AddField("Участники",
                $"Всего участников: {totalMembers}\n" +
                $"Участников в сети: {onlineMembers}\n" +
                $"Ботов: {botCount}\n" +
                $"Людей: {humanCount}")
                .AddField("Важная дата", "*20.11.2020*")
                .AddField("Первое включение бота на сервере", "*01.02.2025*")
                .WithColor(Color.Blue)
                .Build();

            await command.RespondAsync(embed: embed);
        }

        [Command("help_r")]
        public async Task Help_R(SocketSlashCommand command)
        {
            var help_RMessage = new StringBuilder();

            help_RMessage.AppendLine("**Команды поддерживаются следующего вида:**");
            help_RMessage.AppendLine("> `/roll XdY` - Будет совершён бросок кубика, или кубиков _(если_ `X`_ > 1)_, с верхней границей с номиналом `Y`.");
            help_RMessage.AppendLine("> `/roll XdY+Z` - Будет совершён бросок кубика, или кубиков _(если_ `X`_ > 1)_, " +
                "с верхней границей с номиналом `Y`. Также будет добавлен модификатор в `Z` _(может принимать и отрицательные значения)_.");
            help_RMessage.AppendLine("> `/roll Xd[min,max]` - Будет совершён бросок кубика, или кубиков _(если_ `X`_ > 1)_, с границами `min <= Y <= max`.");
            help_RMessage.AppendLine("> `/roll Xd[min,max]+Z` - Будет совершён бросок кубика, или кубиков _(если_ `X`_ > 1)_, с границами `min <= Y <= max`." +
                " Также будет добавлен модификатор в `Z` _(может принимать и отрицательные значения)_.");
            help_RMessage.AppendLine("-# *При обнаружении некорректной работы бота сообщите об этом в `/bug_report`*");

            await command.RespondAsync(help_RMessage.ToString());
        }

        [Command("help_gs")]
        public async Task Help_GS(SocketSlashCommand command)
        {
            var help_GSMessage = new StringBuilder();

            help_GSMessage.AppendLine("**Последовательнсть команд:**");
            help_GSMessage.AppendLine("> 1) `/start *game_name*` - Это команда запускает секундомер и оповещает о начале игры. В `game_name` необходимо указать название " +
                "текущей игры.");
            help_GSMessage.AppendLine("> 2) `/pause` и `/resume` - Данные команды создают начало и конец перерыва, чтобы его время не учитывать в итоговой статистике.");
            help_GSMessage.AppendLine("> 3) `/stop` - Остановка секундомера и вывод статистики.");
            help_GSMessage.AppendLine("-# *При обнаружении некорректной работы бота сообщите об этом в `/bug_report`*");

            await command.RespondAsync(help_GSMessage.ToString());
        }

        [Command("clr")]
        public async Task ClearMessages(SocketSlashCommand command, int count)
        {
            var user = command.User as SocketGuildUser;

            var rolesToCheck = new List<string> { "Технический гуру", "Модератор", "Хранители" };
            var hasRole = user.Roles.Any(role => rolesToCheck.Contains(role.Name, StringComparer.OrdinalIgnoreCase));

            if (user == null || !hasRole)
            {
                await command.RespondAsync("У вас нет необходимой роли для выполнения этой команды.", ephemeral: true);
                Console.WriteLine($"Ошибка: У пользователя {user.DisplayName} недостаточно прав для выполнения команды");
                return;
            }

            if (count < 1 || count > 100)
            {
                await command.RespondAsync("Пожалуйста, укажите число от 1 до 100.", ephemeral: true);
                Console.WriteLine($"Ошибка: Пользователь {user.DisplayName} ввёл некорректное число сообщений - {count}");
                return;
            }

            var messagesToDeleteList = await command.Channel.GetMessagesAsync(count).FlattenAsync();

            if (command.Channel is ITextChannel textChannel)
            {
                await textChannel.DeleteMessagesAsync(messagesToDeleteList);

                await command.RespondAsync(GetMessageCountString(count), ephemeral: true);
                Console.WriteLine(GetMessageCountString(count));
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

            string directoryPath = @"C:\Favorites\Desktop\НРИ\Discord_BR";
            Directory.CreateDirectory(directoryPath);

            string filePath = Path.Combine(directoryPath, $"{user.Username}.txt");

            using (StreamWriter writer = new StreamWriter(filePath, true))
            {
                string reportTime = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
                await writer.WriteLineAsync($"[{reportTime}] {input}");
            }

            IncrementBugReportCounter();

            await command.RespondAsync("Ваш отчет об ошибке был успешно отправлен!",ephemeral:true);
        }

        private void IncrementBugReportCounter()
        {
            string counterFilePath = @"C:\Favorites\Desktop\НРИ\Discord_BR\bug_report_counter.txt";

            if (!File.Exists(counterFilePath))
            {
                File.WriteAllText(counterFilePath, "1");
            }
            else
            {
                int currentCount = int.Parse(File.ReadAllText(counterFilePath));
                currentCount++;
                File.WriteAllText(counterFilePath, currentCount.ToString());
            }
        }
    }

    public class QueueModule : ModuleBase<SocketCommandContext>
    {
        private static Dictionary<SocketGuildUser, List<int>> userRolls = new();
        private static List<IUserMessage> messagesToDelete = new();
        private static int maxRolls;
        private static int rollCount;
        private static Timer rollTimer;
        private static bool isQueueActive;
        private static IUserMessage queueStartMessage;
        private static ITextChannel channel;

        [Command("queue")]
        public async Task QueueCommand(SocketSlashCommand command, int count)
        {
            if (command.User is SocketGuildUser guildUser)
            {
                var roleNameToCheck = "Мастер НРИ";
                var hasRole = guildUser.Roles.Any(role => role.Name.Equals(roleNameToCheck, StringComparison.OrdinalIgnoreCase));

                if (!hasRole)
                {
                    await command.RespondAsync("У вас нет необходимой роли для выполнения этой команды.", ephemeral: true);
                    Console.WriteLine($"Ошибка: У пользователя {guildUser.DisplayName} недостаточно прав для выполнения команды");
                    var userRoles = guildUser.Roles.Select(r => r.Name).ToList();
                    Console.WriteLine($"Роли пользователя: {string.Join(", ", userRoles)}");
                    return;
                }
            }
            if (isQueueActive)
            {
                Console.WriteLine($"Предупреждение: Попытка создания новой очереди, когда одна уже активна.");
                await command.RespondAsync($"Очередь уже создана на {maxRolls} бросков. Если вы хотите её остановить принудительно, введите `/stop_q`.", ephemeral: true);
                return;
            }

            if (count <= 0)
            {
                await command.RespondAsync("Пожалуйста, укажите положительное число.");
                Console.WriteLine($"Ошибка: при создании очереди указано не положительное число ({count}) участников!");
                return;
            }

            maxRolls = count;
            rollCount = 0;
            userRolls.Clear();
            messagesToDelete.Clear();
            isQueueActive = true;

            await command.RespondAsync("Вы запустили создание очереди. Уведомьте об этом своих игроков. Бот остальную информацию уже сообщил." +
                "\nДанное сообщение можно скрыть или оно удалится автоматически.", ephemeral: true);
            _ = Task.Run(async () =>
            {
                await Task.Delay(7000);
                await command.DeleteOriginalResponseAsync();
            });

            channel = (ITextChannel)command.Channel;
            queueStartMessage = await channel.SendMessageAsync($"Очередь активирована. Ожидаем {count} бросков. " +
                $"Вводите значения в формате `/q dY`. Таймер на минуту ожидания запущен.");

            messagesToDelete.Add(queueStartMessage);

            rollTimer = new Timer(ResetQueue, null, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
            Console.WriteLine($"Успех: Очередь создана с заданным числом ({count}) участников! Таймер запущен.");
        }

        [Command("q")]
        public async Task QIn_RollDice(SocketSlashCommand command, string input)
        {
            if (!isQueueActive)
            {
                Console.WriteLine($"Предупреждение: Очередь не активна.");
                await command.RespondAsync("Пожалуйста, запустите очередь перед выполнением этой команды.", ephemeral:true);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    await command.DeleteOriginalResponseAsync();
                });
                return;
            }

            if (!input.StartsWith("d") || !int.TryParse(input[1..], out int y) || y <= 0)
            {
                Console.WriteLine($"Ошибка: Введены некорректные данные.");
                await command.RespondAsync("Пожалуйста, укажите корректные данные.", ephemeral: true);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    await command.DeleteOriginalResponseAsync();
                });
                return;
            }

            var user = command.User as SocketGuildUser;
            if (user == null || (userRolls.ContainsKey(user) && userRolls[user].Count >= maxRolls))
            {
                return;
            }

            rollTimer.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);

            int result = new Random().Next(1, y + 1);
            if (!userRolls.ContainsKey(user))
            {
                userRolls[user] = new List<int>();
            }

            userRolls[user].Add(result);
            rollCount++;

            await command.RespondAsync("Вывод результата:", ephemeral: false);
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                await command.DeleteOriginalResponseAsync();
            });

            var waitingMessage = await channel.SendMessageAsync($"{user.DisplayName}, ваш бросок d{y}: {result}. Ещё {maxRolls - rollCount} бросков. Ожидание следующего броска...");
            messagesToDelete.Add(waitingMessage);

            Console.WriteLine($"Успех: Совершён бросок пользователем {user.DisplayName}. Его результат - {result}. Таймер обновлён.");

            if (rollCount >= maxRolls)
            {
                await DisplayResults();
            }
        }

        private async Task DisplayResults()
        {
            rollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            isQueueActive = false;

            foreach (var msg in messagesToDelete)
            {
                try
                {
                    if (msg != null)
                    {
                        await msg.DeleteAsync();
                        await Task.Delay(100);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка (вывод результатов): При попытке удалить сообщения произошла ошибка. Причина: {ex.Message}");
                }
            }

            var embed = new EmbedBuilder()
                .WithTitle("Последовательность ходов")
                .WithColor(Color.Green);

            if (userRolls == null || userRolls.Count == 0)
            {
                Console.WriteLine("userRolls is null or empty");
                return;
            }

            var sortedResults = userRolls
                .SelectMany(userRoll => userRoll.Value.Select((roll, index) => new
                {
                    User = userRoll.Key,
                    Roll = roll,
                    Index = index + 1
                }))
                .OrderByDescending(x => x.Roll)
                .ToList();

            var resultString = new StringBuilder();
            int sequence = 1;

            foreach (var result in sortedResults)
            {
                var userNickname = result.User.DisplayName;
                if (sortedResults.Count(r => r.User == result.User) > 1)
                {
                    resultString.AppendLine($"{sequence} - {userNickname} - {result.Roll} (# {result.Index})");
                }
                else
                {
                    resultString.AppendLine($"{sequence} - {userNickname} - {result.Roll}");
                }
                sequence++;
            }

            Console.WriteLine($"Успех: Произведён вывод результатов. Сообщения удалены. Таймер остановлён.");
            embed.AddField("Результаты", resultString.ToString(), false);
            if (embed != null)
            {
                await channel.SendMessageAsync(embed: embed.Build());
            }
            else
            {
                Console.WriteLine("Ошибка: embed не был создан.");
            }

            maxRolls = 0;
            rollCount = 0;
            userRolls.Clear();
            messagesToDelete.Clear();        
        }

        private async void ResetQueue(object state)
        {
            isQueueActive = false;

            var timeoutMessage = await channel.SendMessageAsync(":exclamation: Время ожидания истекло, " +
                "введите команду для создания очереди по новой. :exclamation:");
            messagesToDelete.Add(timeoutMessage);

            _ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(async t =>
            {
                foreach (var msg in messagesToDelete)
                {
                    try
                    {
                        await msg.DeleteAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка (очистка очереди): При попытке удалить сообщения произошла ошибка. Причина: {ex.Message}");
                    }
                }
            });
            Console.WriteLine($"Предупреждение: Таймер истёк. Сообщения и очередь удалены.");
        }

        [Command("stop_q")]
        public async Task StopQueue(SocketSlashCommand command)
        {
            if (command.User is SocketGuildUser guildUser)
            {
                var roleNameToCheck = "Мастер НРИ";
                var hasRole = guildUser.Roles.Any(role => role.Name.Equals(roleNameToCheck, StringComparison.OrdinalIgnoreCase));

                if (!hasRole)
                {
                    await command.RespondAsync("У вас нет необходимой роли для выполнения этой команды.", ephemeral: true);
                    Console.WriteLine($"Ошибка: У пользователя {guildUser.DisplayName} недостаточно прав для выполнения команды");
                    var userRoles = guildUser.Roles.Select(r => r.Name).ToList();
                    Console.WriteLine($"Роли пользователя: {string.Join(", ", userRoles)}");
                    return;
                }
            }
            if (!isQueueActive)
            {
                await command.RespondAsync("Очередь не активна.", ephemeral: false);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(7000);
                    await command.DeleteOriginalResponseAsync();
                });
                return;
            }

            // Остановка таймера
            rollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            isQueueActive = false;

            // Удаляем все сообщения, связанные с очередью
            foreach (var msg in messagesToDelete)
            {
                try
                {
                    await msg.DeleteAsync();
                }
                catch (Exception ex) { Console.WriteLine($"Ошибка (остановка очереди): При попытке удалить сообщения произошла ошибка. Причина: {ex.Message}"); }
            }

            // Уведомление об отмене очереди
            await command.RespondAsync("Запись очереди принудительно отменена мастером.");
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                await command.DeleteOriginalResponseAsync();
            });
        }
    }

    public class GameSession
    {
        public string GameName { get; set; }
        public string MasterName { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public List<(DateTime Start, DateTime? End)> PausePeriods { get; set; } = new();
        public bool IsPaused { get; set; }
        public bool IsStopped { get; set; }
    }

    public class GameSessionCommands : ModuleBase<SocketCommandContext>
    {
        private readonly DiscordSocketClient _client;
        private static GameSession _currentSession;

        public GameSessionCommands(DiscordSocketClient client)
        {
            _client = client;
        }

        public static async Task OnGuildScheduledEventStarted(SocketGuildEvent guildEvent, DiscordSocketClient client)
        {
            var gameName = guildEvent.Name;

            var creator = guildEvent.Creator as SocketGuildUser;
            if (creator == null || !creator.Roles.Any(r => r.Name.Equals("Мастер НРИ", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"Мероприятие '{gameName}' запущено не мастером. Пропускаем.");
                return;
            }

            _currentSession = new GameSession
            {
                GameName = gameName,
                MasterName = creator.DisplayName,
                StartTime = DateTime.Now
            };

            ulong channelId = 1345036014519058464;
            var channel = client.GetChannel(channelId) as ITextChannel;
            if (channel != null)
            {
                await channel.SendMessageAsync($"Игра **{gameName}** начата мастером **{_currentSession.MasterName}**.\nВремя начала: {_currentSession.StartTime:HH:mm:ss}");
            }
            else
            {
                Console.WriteLine($"Канал с ID {channelId} не найден или это не текстовый канал.");
            }

            Console.WriteLine($"Оповещение: Игра '{gameName}' начата.");
        }

        [Command("start")]
        public async Task StartGameSession(SocketSlashCommand command, string gameName)
        {
            var user = command.User as SocketGuildUser;

            if (user == null || !user.Roles.Any(r => r.Name.Equals("Мастер НРИ", StringComparison.OrdinalIgnoreCase)))
            {
                await command.RespondAsync("Только мастера могут запускать игру.", ephemeral: true);
                return;
            }

            if (_currentSession != null && !_currentSession.IsStopped)
            {
                await command.RespondAsync("Игра уже запущена. Сначала остановите текущую сессию.", ephemeral: true);
                return;
            }

            _currentSession = new GameSession
            {
                GameName = gameName,
                MasterName = user.DisplayName,
                StartTime = DateTime.Now,
                IsStopped = false
            };
            Console.WriteLine("Оповещение: Создание новой записи времени.");
            await command.RespondAsync($"Игра **{gameName}** запущена мастером **{user.DisplayName}**.\nВремя начала: {_currentSession.StartTime:HH:mm:ss}");
        }

        [Command("pause")]
        public async Task PauseGameSession(SocketSlashCommand command)
        {
            var user = command.User as SocketGuildUser;

            if (user == null || !user.Roles.Any(r => r.Name.Equals("Мастер НРИ", StringComparison.OrdinalIgnoreCase)))
            {
                await command.RespondAsync("Только мастера могут приостанавливать игру.", ephemeral: true);
                return;
            }

            if (_currentSession == null || _currentSession.IsStopped)
            {
                await command.RespondAsync("Нет активной игры для приостановки.", ephemeral: true);
                return;
            }

            if (_currentSession.IsPaused)
            {
                await command.RespondAsync("Игра уже на паузе.", ephemeral: true);
                return;
            }

            _currentSession.PausePeriods.Add((DateTime.Now, null));
            _currentSession.IsPaused = true;

            Console.WriteLine("Оповещение: Игра приостановлена.");
            await command.RespondAsync("Игра приостановлена.");
        }

        [Command("resume")]
        public async Task ResumeGameSession(SocketSlashCommand command)
        {
            var user = command.User as SocketGuildUser;

            if (user == null || !user.Roles.Any(r => r.Name.Equals("Мастер НРИ", StringComparison.OrdinalIgnoreCase)))
            {
                await command.RespondAsync("Только мастера могут продолжать игру.", ephemeral: true);
                return;
            }

            if (_currentSession == null || _currentSession.IsStopped)
            {
                await command.RespondAsync("Нет активной игры для продолжения.", ephemeral: true);
                return;
            }

            if (!_currentSession.IsPaused)
            {
                await command.RespondAsync("Игра не на паузе.", ephemeral: true);
                return;
            }

            var lastPause = _currentSession.PausePeriods.Last();
            _currentSession.PausePeriods[^1] = (lastPause.Start, DateTime.Now);
            _currentSession.IsPaused = false;
            Console.WriteLine("Оповещение: Игра продолжена.");
            await command.RespondAsync("Игра продолжена.");
        }

        [Command("stop")]
        public async Task StopGameSession(SocketSlashCommand command)
        {
            var user = command.User as SocketGuildUser;

            if (user == null || !user.Roles.Any(r => r.Name.Equals("Мастер НРИ", StringComparison.OrdinalIgnoreCase)))
            {
                await command.RespondAsync("Только мастера могут останавливать игру.", ephemeral: true);
                return;
            }

            if (_currentSession == null || _currentSession.IsStopped)
            {
                await command.RespondAsync("Нет активной игры для остановки.", ephemeral: true);
                return;
            }

            _currentSession.EndTime = DateTime.Now;
            _currentSession.IsStopped = true;

            var totalDuration = _currentSession.EndTime - _currentSession.StartTime;
            var pauseDuration = _currentSession.PausePeriods
                .Where(p => p.End.HasValue)
                .Sum(p => (p.End.Value - p.Start).TotalSeconds);

            var activeDuration = totalDuration.Value.TotalSeconds - pauseDuration;

            var message = new StringBuilder();
            message.AppendLine($"# Игра **{_currentSession.GameName}** завершена.");
            message.AppendLine($"- **Мастер:** {_currentSession.MasterName}");
            message.AppendLine($"- **Начало:** {_currentSession.StartTime:HH:mm:ss}");
            message.AppendLine($"- **Конец:** {_currentSession.EndTime:HH:mm:ss}");
            message.AppendLine($"- **Общее время:** {FormatTimeSpan(totalDuration.Value)}");
            message.AppendLine($"- **Активное время:** {FormatTimeSpan(TimeSpan.FromSeconds(activeDuration))}");

            if (_currentSession.PausePeriods.Any())
            {
                message.AppendLine("## Перерывы:");
                foreach (var pause in _currentSession.PausePeriods)
                {
                    message.AppendLine($"- {pause.Start:HH:mm:ss} — {pause.End?.ToString("HH:mm:ss") ?? "не завершён"}");
                }
            }
            Console.WriteLine("Оповещение: Игра закончена.");

            await command.RespondAsync("Игра остановлена. Статистика отправлена в чат и в личные сообщения админу.", ephemeral: true);
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                await command.DeleteOriginalResponseAsync();
            });
            await command.Channel.SendMessageAsync(message.ToString());

            var targetUser = _client.GetUser(263758178091532288);
            if (targetUser == null)
            {
                await command.Channel.SendMessageAsync("Пользователь *Админ* не найден.");
                return;
            }

            try
            {
                var dmChannel = await targetUser.CreateDMChannelAsync();
                await dmChannel.SendMessageAsync(message.ToString());
            }
            catch (Exception ex)
            {
                await command.Channel.SendMessageAsync($"Ошибка: Не удалось отправить результаты пользователю {targetUser.Mention}. Убедитесь, что у него открыты личные сообщения для бота.");
                Console.WriteLine($"Ошибка отправки статистики: {ex.Message}");
            }

            _currentSession = null;
        }
             
        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            var hours = (int)timeSpan.TotalHours;
            var minutes = timeSpan.Minutes;
            var seconds = timeSpan.Seconds;

            var hoursText = hours > 0 ? $"{hours} {hours switch { 1 => "час", >= 2 and <= 4 => "часа", _ => "часов" }}" : "";
            var minutesText = minutes > 0 ? $"{minutes} {minutes switch { 1 => "минута", >= 2 and <= 4 => "минуты", _ => "минут" }}" : "";
            var secondsText = seconds > 0 ? $"{seconds} {seconds switch { 1 => "секунда", >= 2 and <= 4 => "секунды", _ => "секунд" }}" : "";

            return string.Join(" ", new[] { hoursText, minutesText, secondsText }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }
}

/*
 * await _client.LoginAsync(TokenType.Bot, "MTMzMTYyODkxMDE1MjEyMjM4OA.GzWsZE.WJgvlfflP5wkFxFGqce6tK3mDYOygSvc0q2TBk"); * 
 */