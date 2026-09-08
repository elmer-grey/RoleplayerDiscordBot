using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot.SlashModules;

/// <summary>
/// <c>/vampire_start</c> — пошаговая инициализация VtM-модуля на сервере:
/// <list type="number">
///   <item>Проверка <see cref="ServerConfig.VtMRollChannelID"/>.</item>
///   <item>Копирование шаблона weapons.json.</item>
///   <item>Отправка тестового персонажа в ЛС.</item>
///   <item>Тестовый бросок в VtMRollChannel.</item>
/// </list>
/// Доступно только администраторам гильдии.
/// </summary>
public sealed class VampireStartSlashModule : ISlashCommandModule
{
    private static readonly GuildPermission AdminPermission =
        GuildPermission.Administrator;

    public string Name => "vampire-start";

    public IReadOnlyCollection<string> CommandNames { get; } = new[]
    {
        "vampire_start",
    };

    public IReadOnlyList<SlashCommandBuilder> Register()
    {
        return new List<SlashCommandBuilder>
        {
            new SlashCommandBuilder()
                .WithName("vampire_start")
                .WithDescription("Инициализировать VtM-модуль: канал, словари, тест-персонаж, тест-бросок.")
                .WithDefaultMemberPermissions(AdminPermission),
        };
    }

    public async Task<bool> DispatchAsync(SocketSlashCommand command)
    {
        if (!command.GuildId.HasValue)
        {
            await command.RespondAsync("VtM-инициализация работает только на сервере.", ephemeral: true);
            return true;
        }

        var cfg = Program.ServerConfigResolver?.Invoke(command.GuildId.Value);
        if (cfg == null)
        {
            await command.RespondAsync("Конфигурация сервера не загружена.", ephemeral: true);
            return true;
        }

        await command.DeferAsync(ephemeral: true);
        var result = await VampireStartService.RunAsync(command, cfg, CancellationToken.None);
        await command.FollowupAsync(result.ToHumanLines(), ephemeral: true);
        return true;
    }
}
