using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace RPBot.SlashModules;

/// <summary>
/// Модуль slash-команд VtM (Vampire: the Masquerade V20).
/// Объявляет команду <c>/vampire</c> с поддействиями bind/unbind и
/// делегирует обработку <see cref="VampireCommands"/>.
/// </summary>
/// <remarks>
/// <para>В будущем сюда же подключатся <c>/vampire show</c>, чарник и т.п.</para>
/// </remarks>
public sealed class VampireSlashModule : ISlashCommandModule
{
    private readonly VampireCommands _commands;

    public VampireSlashModule(VampireCommands commands)
    {
        _commands = commands ?? throw new System.ArgumentNullException(nameof(commands));
    }

    public string Name => "vampire";

    public IReadOnlyCollection<string> CommandNames { get; } = new[]
    {
        "vampire",
    };

    public IReadOnlyList<SlashCommandBuilder> Register()
    {
        return new List<SlashCommandBuilder>
        {
            new SlashCommandBuilder()
                .WithName("vampire")
                .WithDescription("Действия над персонажами VtM (Vampire: the Masquerade V20).")
                .WithDefaultMemberPermissions(GuildPermission.ManageRoles)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("action")
                    .WithDescription("Действие")
                    .WithType(ApplicationCommandOptionType.String)
                    .AddChoice("bind",   "bind")
                    .AddChoice("unbind", "unbind")
                    .WithRequired(true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("name")
                    .WithDescription("Имя персонажа")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("user")
                    .WithDescription("Игрок (если не указан — привязывается к ST). Только для bind.")
                    .WithType(ApplicationCommandOptionType.User)
                    .WithRequired(false)),
        };
    }

    public async Task<bool> DispatchAsync(SocketSlashCommand command)
    {
        // /vampire — единая команда с поддействиями. Внутри разбираем по `action`.
        var action = command.Data.Options
            .FirstOrDefault(o => o.Name == "action")?.Value as string;

        switch (action)
        {
            case "bind":
                await _commands.HandleBindAsync(command);
                return true;
            case "unbind":
                await _commands.HandleUnbindAsync(command);
                return true;
            default:
                await command.RespondAsync("Неизвестное действие. Укажите `bind` или `unbind`.", ephemeral: true);
                return true;
        }
    }
}
