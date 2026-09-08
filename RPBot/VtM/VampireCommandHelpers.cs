using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot;

/// <summary>
/// Общие приватные хелперы для обработчиков VtM-команд.
/// Вынесены из <see cref="VampireCommands"/>, чтобы дочерние хендлеры
/// (Bind/Unbind/Diablerie/Morality/Experience) могли ими пользоваться
/// без копирования.
/// </summary>
internal static class VampireCommandHelpers
{
    /// <summary>
    /// Проверить, что вызывающий — рассказчик (Administrator или ManageRoles).
    /// </summary>
    public static async Task<bool> IsStorytellerAsync(SocketSlashCommand command)
    {
        if (command.User is not SocketGuildUser g) return false;
        if (g.GuildPermissions.Administrator) return true;
        return await Task.FromResult(g.GuildPermissions.ManageRoles);
    }

    /// <summary>
    /// Извлечь аргумент (часть после третьего «:») из CustomId формата
    /// <c>vtm_wiz:{action}:{guid}:{arg}</c>. Используется для кнопок
    /// с аргументом, обрабатываемых в общем case (например, DisciplineRename).
    /// </summary>
    public static string? ExtractCustomIdArg(string? customId)
    {
        if (string.IsNullOrEmpty(customId)) return null;
        var parts = customId.Split(new[] { ':' }, 4);
        return parts.Length >= 4 ? parts[3] : null;
    }

    public static string? GetString(SocketSlashCommand command, string key)
    {
        var opt = command.Data.Options.FirstOrDefault(o => o.Name == key);
        return opt?.Value as string;
    }

    public static (ulong id, bool found)? GetUser(SocketSlashCommand command, string key)
    {
        var opt = command.Data.Options.FirstOrDefault(o => o.Name == key);
        if (opt?.Value is not IUser user) return null;
        return (user.Id, true);
    }

    /// <summary>
    /// Построить embed с результатом <see cref="VampireStorage.BindResult"/>.
    /// </summary>
    public static Embed BindResultToEmbed(VampireStorage.BindResult result, ulong requestedUserId)
    {
        var eb = new EmbedBuilder();
        switch (result.Kind)
        {
            case VampireStorage.BindResultKind.Ok:
                eb.Title = "✅ Привязка выполнена";
                eb.Description = $"«{result.Character?.CharacterName}» привязан к <@{requestedUserId}>.";
                eb.Color = Color.Green;
                break;
            case VampireStorage.BindResultKind.AlreadyBoundToSame:
                eb.Title = "ℹ️ Уже привязан";
                eb.Description = $"«{result.Character?.CharacterName}» уже привязан к этому игроку.";
                eb.Color = Color.LightGrey;
                break;
            case VampireStorage.BindResultKind.AlreadyBoundToOther:
                eb.Title = "⚠️ Другой игрок";
                eb.Description = $"«{result.Character?.CharacterName}» уже привязан к <@{result.CurrentPlayerId}>.\nСначала выполните `/vampire unbind {result.Character?.CharacterName}`.";
                eb.Color = Color.Orange;
                break;
            case VampireStorage.BindResultKind.NotFound:
                eb.Title = "❌ Не найдено";
                eb.Description = result.Message;
                eb.Color = Color.Red;
                break;
            case VampireStorage.BindResultKind.InvalidUserId:
                eb.Title = "❌ Некорректный пользователь";
                eb.Description = result.Message;
                eb.Color = Color.Red;
                break;
        }
        return eb.Build();
    }

    /// <summary>
    /// Прочитать поколение жертвы из листа, если она привязана. Иначе null.
    /// </summary>
    public static async Task<int?> TryReadVictimGenerationAsync(ulong guildId, ulong victimUserId)
    {
        try
        {
            var storage = await VampireStorageCache.GetAsync(guildId);
            var character = storage.GetByPlayerId(victimUserId);
            return character?.Generation;
        }
        catch
        {
            return null;
        }
    }
}
