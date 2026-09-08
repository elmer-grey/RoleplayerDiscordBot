using System.Collections.Generic;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace RPBot.SlashModules;

/// <summary>
/// Набор из 4 top-level slash-команд для работы с персонажами VtM:
/// <c>/vampire_create</c>, <c>/vampire_bind</c>, <c>/vampire_send</c>, <c>/vampire_show</c>.
/// Делегируют обработку в <see cref="VampireCommands"/>.
///
/// <para>Эти команды — это разнесённые по top-level (без перегруза одной
/// <c>/vampire action:*</c>) версии под-команд, которые раньше жили под
/// единой <c>/vampire</c>. Оставлены оба набора для удобства:
/// top-level проще в автокомплите Discord и не превращаются в «выпадающее меню».</para>
/// </summary>
/// <remarks>
/// <para>Права доступа:</para>
/// <list type="bullet">
///   <item><c>/vampire_create</c> — доступно всем (визард идёт в ЛС инициатора).</item>
///   <item><c>/vampire_bind</c> — только ST (проверяется внутри <c>HandleBindAsync</c>).</item>
///   <item><c>/vampire_send</c> — только ST (проверяется внутри <c>HandleSendAsync</c>).</item>
///   <item><c>/vampire_show</c> — доступно всем (публичный embed).</item>
/// </list>
/// </remarks>
public sealed class VampireTopLevelSlashModule : ISlashCommandModule
{
    private static readonly GuildPermission? StPermission =
        GuildPermission.ManageRoles | GuildPermission.Administrator;

    private readonly VampireCommands _commands;

    public VampireTopLevelSlashModule(VampireCommands commands)
    {
        _commands = commands ?? throw new System.ArgumentNullException(nameof(commands));
    }

    public string Name => "vampire-top-level";

    public IReadOnlyCollection<string> CommandNames { get; } = new[]
    {
        "vampire_create",
        "vampire_bind",
        "vampire_send",
        "vampire_show",
    };

    public IReadOnlyList<SlashCommandBuilder> Register()
    {
        return new List<SlashCommandBuilder>
        {
            // /vampire_create — открыть визард создания персонажа в ЛС.
            new SlashCommandBuilder()
                .WithName("vampire_create")
                .WithDescription("Открыть визард создания персонажа VtM в личных сообщениях."),

            // /vampire_bind <name> [user] — привязать персонажа к игроку (ST only).
            new SlashCommandBuilder()
                .WithName("vampire_bind")
                .WithDescription("Привязать персонажа VtM к игроку (только для рассказчика).")
                .WithDefaultMemberPermissions(StPermission)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("name")
                    .WithDescription("Имя персонажа (или часть имени для поиска).")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("user")
                    .WithDescription("Игрок, к которому привязывается персонаж. По умолчанию — вы.")
                    .WithType(ApplicationCommandOptionType.User)
                    .WithRequired(false)),

            // /vampire_send <name> [user] — отправить лист с кнопками в ЛС игроку (ST only).
            new SlashCommandBuilder()
                .WithName("vampire_send")
                .WithDescription("Отправить лист персонажа с кнопками в личку игроку (только для рассказчика).")
                .WithDefaultMemberPermissions(StPermission)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("name")
                    .WithDescription("Имя персонажа (или часть имени для поиска).")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("user")
                    .WithDescription("Кому отправить. По умолчанию — владельцу персонажа.")
                    .WithType(ApplicationCommandOptionType.User)
                    .WithRequired(false)),

            // /vampire_show <name> — публичный embed без кнопок в текущем канале.
            new SlashCommandBuilder()
                .WithName("vampire_show")
                .WithDescription("Показать лист персонажа в канале (без кнопок).")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("name")
                    .WithDescription("Имя персонажа (или часть имени для поиска).")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true)),
        };
    }

    public async Task<bool> DispatchAsync(SocketSlashCommand command)
    {
        switch (command.Data.Name)
        {
            case "vampire_create":
                await _commands.HandleCreateAsync(command);
                return true;
            case "vampire_bind":
                await _commands.HandleBindAsync(command);
                return true;
            case "vampire_send":
                await _commands.HandleSendAsync(command);
                return true;
            case "vampire_show":
                await _commands.HandleShowPublicAsync(command);
                return true;
            default:
                return false;
        }
    }
}
