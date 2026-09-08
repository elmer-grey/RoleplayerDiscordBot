using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot;

/// <summary>
/// Обработка кнопок и модалки опыта в листе персонажа.
/// Владелец чарника нажимает «+ опыт» или «− опыт», модалка просит
/// число, бот применяет дельту.
/// </summary>
internal sealed class VampireExperienceCommands
{
    public async Task HandleExperienceButtonAsync(SocketMessageComponent component)
    {
        if (!VampireExperienceComponents.TryParse(component.Data.CustomId, out var action, out var charId))
        {
            await component.RespondAsync("⚠️ Не удалось разобрать кнопку опыта.", ephemeral: true);
            return;
        }
        if (!component.GuildId.HasValue)
        {
            await component.RespondAsync("Кнопки опыта работают только на сервере.", ephemeral: true);
            return;
        }
        var storage = await VampireStorageCache.GetAsync(component.GuildId.Value);
        var character = storage.GetByCharacterId(charId);
        if (character == null)
        {
            await component.RespondAsync("❌ Чарник не найден.", ephemeral: true);
            return;
        }
        if (character.PlayerId != component.User.Id)
        {
            await component.RespondAsync("⚠️ Только владелец чарника может изменять опыт.", ephemeral: true);
            return;
        }
        await component.RespondWithModalAsync(VampireExperienceModal.Build(action, charId));
    }

    public async Task HandleExperienceModalAsync(SocketModal modal)
    {
        if (!VampireExperienceModal.TryParse(modal.Data.CustomId, out var action, out var charId))
        {
            await modal.RespondAsync("⚠️ Не удалось разобрать модалку опыта.", ephemeral: true);
            return;
        }

        string raw = string.Empty;
        foreach (var comp in modal.Data.Components)
        {
            if (string.Equals(comp.CustomId, VampireExperienceModal.AmountFieldId, StringComparison.OrdinalIgnoreCase))
                raw = comp.Value ?? string.Empty;
        }
        if (!VampireExperienceModal.TryParseAmount(raw, out var amount))
        {
            await modal.RespondAsync("⚠️ Количество опыта должно быть положительным целым.", ephemeral: true);
            return;
        }

        var guildId = modal.GuildId ?? (modal.User as SocketGuildUser)?.Guild.Id;
        if (!guildId.HasValue)
        {
            await modal.RespondAsync("Модалка доступна только на сервере.", ephemeral: true);
            return;
        }
        var storage = await VampireStorageCache.GetAsync(guildId.Value);
        var character = storage.GetByCharacterId(charId);
        if (character == null)
        {
            await modal.RespondAsync("❌ Чарник не найден.", ephemeral: true);
            return;
        }
        if (character.PlayerId != modal.User.Id)
        {
            await modal.RespondAsync("⚠️ Только владелец чарника может изменять опыт.", ephemeral: true);
            return;
        }

        string msg;
        if (action == ExperienceModalAction.Grant)
        {
            character.ExperienceCurrent += amount;
            character.ExperienceTotal   += amount;
            msg = $"✅ Начислено **{amount}** опыта. Текущий: **{character.ExperienceCurrent}**.";
        }
        else
        {
            if (character.ExperienceCurrent < amount)
            {
                await modal.RespondAsync(
                    $"⚠️ Недостаточно опыта: доступно {character.ExperienceCurrent}, нужно {amount}.",
                    ephemeral: true);
                return;
            }
            character.ExperienceCurrent -= amount;
            msg = $"✅ Потрачено **{amount}** опыта. Остаток: **{character.ExperienceCurrent}**.";
        }

        await storage.UpsertAsync(character);
        await modal.RespondAsync(msg, ephemeral: true);
    }
}
