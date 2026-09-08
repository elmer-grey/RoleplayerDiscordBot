using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot;

/// <summary>
/// Обработка кнопок блока «Мораль»: проверка совести, добавление/удаление
/// расстройства через SelectMenu.
/// </summary>
internal sealed class VampireMoralityCommands
{
    public async Task HandleMoralityButtonAsync(SocketMessageComponent component)
    {
        if (!VampireMoralityComponents.TryParse(component.Data.CustomId, out var action, out var charId))
        {
            await component.RespondAsync("⚠️ Не удалось разобрать кнопку морали.", ephemeral: true);
            return;
        }
        if (!component.GuildId.HasValue)
        {
            await component.RespondAsync("Кнопки морали работают только на сервере.", ephemeral: true);
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
            await component.RespondAsync("⚠️ Только владелец чарника может изменять мораль.", ephemeral: true);
            return;
        }

        switch (action)
        {
            case MoralityAction.RemoveLastDerangement:
                if (character.Derangements == null || character.Derangements.Count == 0)
                {
                    await component.RespondAsync("ℹ️ Список расстройств пуст.", ephemeral: true);
                    return;
                }
                var removed = character.Derangements[^1];
                character.Derangements.RemoveAt(character.Derangements.Count - 1);
                await storage.UpsertAsync(character);
                await component.RespondAsync($"✅ Удалено расстройство «{removed}».",
                    components: VampireMoralityComponents.Build(charId), ephemeral: true);
                return;

            case MoralityAction.StartAddDerangement:
                await component.RespondAsync(
                    "Выберите расстройство для добавления:",
                    components: VampireMoralityComponents.Build(charId, withDerangementMenu: true),
                    ephemeral: true);
                return;

            case MoralityAction.ConscienceCheck:
                await HandleConscienceCheckAsync(component, storage, character);
                return;
        }
    }

    /// <summary>
    /// Обработка нажатия «Проверка совести» (Roadmap #37, V20 стр. 333).
    /// Бросает пул по текущей Человечности/Пути, интерпретирует результат и применяет потери.
    /// </summary>
    private static async Task HandleConscienceCheckAsync(
        SocketMessageComponent component,
        VampireStorage storage,
        VampireCharacter character)
    {
        var currentHumanity = VampireFinishingResolver.ComputeHumanity(character);
        var poolSize = VampireConscienceResolver.ConscienceDicePool(character, currentHumanity);

        if (poolSize < 1)
        {
            await component.RespondAsync(
                "ℹ️ Пул проверки совести равен 0 (Человечность не задана). Бросок не требуется.",
                ephemeral: true);
            return;
        }

        var rng = new SystemRandomAdapter();
        var roll = VampireDicePool.RollV20(poolSize, rng);

        var conscienceResult = VampireConscienceResolver.Roll(roll.Dice);
        var apply = VampireConscienceResolver.Apply(conscienceResult.Outcome);

        var applied = VampireMoralityResolver.ApplyConscience(character, apply);
        await storage.UpsertAsync(character);

        var embed = VampireEmbeds.BuildConscienceCheckEmbed(character, poolSize, roll, conscienceResult, apply, applied);

        if (await VampireRollChannelPublisher.PublishAsync(component, embed))
            return;

        await component.RespondAsync(embed: embed, ephemeral: true);
    }

    public async Task HandleMoralitySelectAsync(SocketMessageComponent component)
    {
        var charId = VampireMoralityComponents.TryParseSelectedMenu(component.Data.CustomId);
        if (charId == null)
        {
            await component.RespondAsync("⚠️ Не удалось разобрать выбор расстройства.", ephemeral: true);
            return;
        }
        if (!component.GuildId.HasValue)
        {
            await component.RespondAsync("Мораль работает только на сервере.", ephemeral: true);
            return;
        }
        var storage = await VampireStorageCache.GetAsync(component.GuildId.Value);
        var character = storage.GetByCharacterId(charId.Value);
        if (character == null)
        {
            await component.RespondAsync("❌ Чарник не найден.", ephemeral: true);
            return;
        }
        if (character.PlayerId != component.User.Id)
        {
            await component.RespondAsync("⚠️ Только владелец чарника может менять мораль.", ephemeral: true);
            return;
        }
        var values = component.Data.Values;
        if (values == null || values.Count == 0)
        {
            await component.RespondAsync("ℹ️ Ничего не выбрано.", ephemeral: true);
            return;
        }
        var picked = values.First();
        if (!VampireDerangementCatalog.IsKnown(picked))
        {
            await component.RespondAsync("⚠️ Неизвестное расстройство.", ephemeral: true);
            return;
        }
        character.Derangements ??= new System.Collections.Generic.List<string>();
        if (character.Derangements.Contains(picked))
        {
            await component.RespondAsync($"ℹ️ «{picked}» уже в списке расстройств.", ephemeral: true);
            return;
        }
        character.Derangements.Add(picked);
        await storage.UpsertAsync(character);
        await component.RespondAsync($"✅ Добавлено расстройство «{picked}».",
            components: VampireMoralityComponents.Build(charId.Value), ephemeral: true);
    }
}
