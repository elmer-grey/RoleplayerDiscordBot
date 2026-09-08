using System;
using System.Linq;
using System.Threading.Tasks;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot;

/// <summary>
/// Обработка кнопок главной панели листа (Описание/Воля/Здоровье/Клан/Мораль/Опыт/Ярость/ToggleActive).
/// </summary>
internal sealed class VampireSheetButtonHandler
{
    public async Task HandleSheetButtonAsync(SocketMessageComponent component)
    {
        if (!VampireSheetComponents.TryParse(component.Data.CustomId, out var action, out var charId))
        {
            await component.RespondAsync("⚠️ Не удалось разобрать кнопку.", ephemeral: true);
            return;
        }
        if (!component.GuildId.HasValue)
        {
            await component.RespondAsync("Кнопки листа работают только на сервере.", ephemeral: true);
            return;
        }
        var storage = await VampireStorageCache.GetAsync(component.GuildId.Value);
        var character = storage.GetByCharacterId(charId);
        if (character == null)
        {
            await component.RespondAsync("❌ Чарник не найден в хранилище.", ephemeral: true);
            return;
        }

        switch (action)
        {
            case VampireSheetAction.Description:
            {
                var embed = VampireDescriptionEmbed.Build(character);
                if (embed == null)
                    await component.RespondAsync(
                        $"ℹ️ У «{character.CharacterName}» пока нет описания (Bio) и аватара.",
                        ephemeral: true);
                else
                    await component.RespondAsync(embed: embed, ephemeral: true);
                return;
            }
            case VampireSheetAction.Willpower:
                await component.RespondAsync(embed: VampireWillpowerEmbed.Build(character), ephemeral: true);
                return;
            case VampireSheetAction.Health:
                await component.RespondAsync(embed: VampireHealthEmbed.Build(character), ephemeral: true);
                return;
            case VampireSheetAction.Clan:
            {
                var embed = VampireClanEmbed.Build(character);
                if (embed == null)
                    await component.RespondAsync(
                        $"ℹ️ У «{character.CharacterName}» не указан клан — нечего показать.",
                        ephemeral: true);
                else
                    await component.RespondAsync(embed: embed, ephemeral: true);
                return;
            }
            case VampireSheetAction.Morality:
                await component.RespondAsync(
                    embed: VampireMoralityEmbed.BuildEmbed(character),
                    components: VampireMoralityComponents.Build(charId),
                    ephemeral: true);
                return;
            case VampireSheetAction.Experience:
                await component.RespondAsync(
                    "**Опыт** — выберите действие:",
                    components: VampireExperienceComponents.Build(charId),
                    ephemeral: true);
                return;
            case VampireSheetAction.Frenzy:
                await component.RespondAsync(
                    embed: VampireFrenzyEmbed.Build(character),
                    components: VampireFrenzyComponents.Build(character),
                    ephemeral: true);
                return;
            case VampireSheetAction.ToggleActive:
                await ToggleActiveAsync(component, storage, character, charId);
                return;
            default:
                await component.RespondAsync("⚠️ Неизвестное действие кнопки.", ephemeral: true);
                return;
        }
    }

    private static async Task ToggleActiveAsync(
        SocketMessageComponent component,
        VampireStorage storage,
        VampireCharacter character,
        Guid charId)
    {
        var guildId = component.GuildId!.Value;
        var playerId = component.User.Id;
        var registry = VampireActiveRegistry.Instance;

        var ownedByPlayer = storage.ListAll()
            .Where(c => c.PlayerId == playerId)
            .ToList();

        if (ownedByPlayer.Count <= 1)
        {
            await component.RespondAsync(
                "ℹ️ У вас только один персонаж — кнопка активности не нужна.",
                ephemeral: true);
            return;
        }

        var current = registry.GetActiveCharacterId(guildId, playerId);
        if (current.HasValue && current.Value == charId)
        {
            await component.RespondAsync(
                $"✅ «{character.CharacterName}» уже активен — он и так будет использоваться для бросков.",
                ephemeral: true);
            return;
        }

        registry.SetActiveCharacterId(guildId, playerId, charId);
        await component.RespondAsync(
            $"✅ «{character.CharacterName}» теперь активный. " +
            "Дальнейшие броски пойдут по нему. Переключить обратно — кнопка в листе.",
            ephemeral: true);
    }
}
