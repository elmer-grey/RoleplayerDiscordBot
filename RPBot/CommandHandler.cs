using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordBot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace RPBot
{
    public class CommandHandler
    {
        private readonly DiscordSocketClient _client;
        private readonly InteractionService _interactionService;
        private readonly IServiceProvider _services;
        private readonly List<ulong> _guildIDs;

        public CommandHandler(DiscordSocketClient client,
                            InteractionService interactionService,
                            IServiceProvider services)
        {
            _client = client;
            _interactionService = interactionService;
            _services = services;
            _guildIDs = new List<ulong> { 295189463376855040, 1288192593137635359 };
        }

        public async Task InitializeAsync()
        {
            await _interactionService.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
            await RegisterCommandsAsync();
        }

        public async Task HandleInteraction(SocketInteraction interaction)
        {
            try
            {
                // Обязательное подтверждение для компонентов
                if (interaction is IComponentInteraction component)
                {
                    await component.DeferAsync(ephemeral: true);
                }

                var context = new SocketInteractionContext(_client, interaction);
                await _interactionService.ExecuteCommandAsync(context, _services);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка: {ex}");
                if (interaction is IComponentInteraction ci)
                {
                    await ci.RespondAsync("⚠ Ошибка обработки", ephemeral: true);
                }
            }
        }

        private async Task RegisterCommandsAsync()
        {
            foreach (var guildId in _guildIDs)
            {
                var guild = _client.GetGuild(guildId);
                if (guild == null) continue;

                try
                {
                    await _interactionService.RegisterCommandsToGuildAsync(guildId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка регистрации команд для гильдии {guildId}: {ex.Message}");
                }
            }
        }

        private async Task LogRegisteredCommands()
        {
            Console.WriteLine("\n=== ЗАРЕГИСТРИРОВАННЫЕ КОМАНДЫ ===");

            foreach (var module in _interactionService.Modules)
            {
                // Получаем атрибут группы из модуля взаимодействий
                var interactionGroupAttr = module.SlashGroupName;
                var interactionGroupDesc = module.Description;

                Console.WriteLine($"\nМодуль: {module.Name}");

                if (!string.IsNullOrEmpty(interactionGroupAttr))
                {
                    Console.WriteLine($"Группа: /{interactionGroupAttr} - {interactionGroupDesc}");
                }

                foreach (var command in module.SlashCommands)
                {
                    Console.WriteLine($"\n  /{(interactionGroupAttr != null ? $"{interactionGroupAttr} " : "")}{command.Name}");
                    Console.WriteLine($"  • Описание: {command.Description}");

                    if (command.Parameters.Count > 0)
                    {
                        Console.WriteLine("  • Параметры:");
                        foreach (var param in command.Parameters)
                        {
                            Console.WriteLine($"    - {param.Name}: {param.Description}");
                            Console.WriteLine($"      Тип: {param.ParameterType.Name}");
                            Console.WriteLine($"      Обязательный: {param.IsRequired}");
                        }
                    }
                }
            }
        }
    }
}