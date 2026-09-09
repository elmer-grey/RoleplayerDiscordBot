using System.Collections.Generic;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace RPBot.SlashModules;

/// <summary>
/// Набор из 6 top-level slash-команд для работы с персонажами VtM:
/// <c>/vampire_create</c>, <c>/vampire_bind</c>, <c>/vampire_unbind</c>,
/// <c>/vampire_send</c>, <c>/vampire_show</c>, <c>/vampire_diablerie</c>.
/// Делегируют обработку в <see cref="VampireCommands"/>.
///
/// <para>Все эти команды — top-level; раньше часть из них жила под
/// единой <c>/vampire action:*</c> (этот модуль был удалён).</para>
/// </summary>
/// <remarks>
/// <para>Права доступа:</para>
/// <list type="bullet">
///   <item><c>/vampire_create</c> — доступно всем (визард идёт в ЛС инициатора).</item>
///   <item><c>/vampire_bind</c> — только ST.</item>
///   <item><c>/vampire_unbind</c> — только ST.</item>
///   <item><c>/vampire_send</c> — только ST.</item>
///   <item><c>/vampire_show</c> — доступно всем (публичный embed).</item>
///   <item><c>/vampire_diablerie</c> — справочный расчёт (доступно всем).</item>
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
        "vampire_unbind",
        "vampire_send",
        "vampire_show",
        "vampire_diablerie",
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

            // /vampire_unbind <name> — снять привязку (ST only).
            new SlashCommandBuilder()
                .WithName("vampire_unbind")
                .WithDescription("Отвязать персонажа VtM от игрока (только для рассказчика).")
                .WithDefaultMemberPermissions(StPermission)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("name")
                    .WithDescription("Имя персонажа (или часть имени для поиска).")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true)),

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

            // /vampire_diablerie @attacker @victim gen:N hum:M [success:true|false]
            // Справочный расчёт итогов диаблери (V20, стр. 310-311).
            new SlashCommandBuilder()
                .WithName("vampire_diablerie")
                .WithDescription("Справочный расчёт итогов диаблери (V20, стр. 310-311).")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("attacker")
                    .WithDescription("Кто совершает диаблери.")
                    .WithType(ApplicationCommandOptionType.User)
                    .WithRequired(true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("victim")
                    .WithDescription("Жертва диаблери.")
                    .WithType(ApplicationCommandOptionType.User)
                    .WithRequired(true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("gen")
                    .WithDescription("Поколение атакующего (3..15).")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(false))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("hum")
                    .WithDescription("Человечность или PathRating атакующего (1..10).")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(false))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("success")
                    .WithDescription("Диаблери удалось? По умолчанию — true.")
                    .WithType(ApplicationCommandOptionType.String)
                    .AddChoice("true", "true")
                    .AddChoice("false", "false")
                    .WithRequired(false)),
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
            case "vampire_unbind":
                await _commands.HandleUnbindAsync(command);
                return true;
            case "vampire_send":
                await _commands.HandleSendAsync(command);
                return true;
            case "vampire_show":
                await _commands.HandleShowPublicAsync(command);
                return true;
            case "vampire_diablerie":
                await _commands.HandleDiablerieAsync(command);
                return true;
            default:
                return false;
        }
    }
}
