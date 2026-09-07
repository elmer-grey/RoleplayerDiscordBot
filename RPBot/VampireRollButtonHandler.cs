using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot;

/// <summary>
/// Обработчик кнопок под сообщением с результатом броска VtM:
///   • <c>vam_reroll</c> — открыть picker выбора числа кубиков для переброса за волю.
///   • <c>vam_reroll_menu</c> — SelectMenu с 3 опциями (1/2/3 кубика).
///   • <c>vam_reroll_back</c> — вернуть исходный набор кнопок.
///   • <c>vam_repeat</c> — повторный бросок (V20: пул N−1, берётся новый результат).
///   • <c>vam_done</c> — снять кнопки (подтверждение, что выбор сделан).
/// </summary>
/// <remarks>
/// <para>Все кнопки авторизованы по <c>component.User.Id == originalUserId</c> —
/// инициировать переброс/повтор может только автор броска.</para>
/// </remarks>
public sealed class VampireRollButtonHandler
{
    /// <summary>
    /// Главный диспетчер: префиксы customId из <see cref="VampireRollComponents"/>.
    /// </summary>
    public async Task<bool> HandleAsync(SocketMessageComponent component)
    {
        if (component?.Data?.CustomId is not { } cid) return false;

        if (VampireRollComponents.IsRerollOpenButton(cid, out var rerollOwner))
            return await HandleOpenPickerAsync(component, rerollOwner);

        if (VampireRollComponents.TryParseRerollMenu(cid, out var menuOwner))
            return await HandleMenuAsync(component, menuOwner, cid);

        if (VampireRollComponents.IsRerollBackButton(cid, out var backOwner))
            return await HandleBackAsync(component, backOwner);

        if (VampireRollComponents.IsRepeatButton(cid, out var repeatOwner))
            return await HandleRepeatAsync(component, repeatOwner);

        if (VampireRollComponents.IsDoneButton(cid, out var doneOwner))
            return await HandleDoneAsync(component, doneOwner);

        return false;
    }

    private static async Task<bool> ReplyNotYoursAsync(SocketMessageComponent component)
    {
        await component.RespondAsync("Это не твой бросок.", ephemeral: true);
        return true;
    }

    private static bool Authorizes(SocketMessageComponent component, ulong originalUserId)
    {
        return component.User.Id == originalUserId;
    }

    private async Task<bool> HandleOpenPickerAsync(SocketMessageComponent component, ulong originalUserId)
    {
        if (!Authorizes(component, originalUserId)) return await ReplyNotYoursAsync(component);

        await component.Message.ModifyAsync(msg =>
        {
            msg.Components = VampireRollComponents.BuildRerollAmountPicker(originalUserId);
        });
        await component.DeferAsync(ephemeral: true);
        return true;
    }

    private async Task<bool> HandleMenuAsync(SocketMessageComponent component, ulong originalUserId, string customId)
    {
        if (!Authorizes(component, originalUserId)) return await ReplyNotYoursAsync(component);

        // Значение из SelectMenu приходит в component.Data.Values[0].
        var value = component.Data.Values?.FirstOrDefault();
        if (!VampireRollComponents.TryParseRerollMenuValue(value, out var count))
        {
            await component.RespondAsync("Не удалось разобрать выбор количества кубиков.", ephemeral: true);
            return true;
        }

        // На данном этапе у нас ещё нет инфраструктуры для боевой модификации персонажа
        // (лист + Воля + WillpowerPoints) и нет источника снапшота броска — это закрывается
        // отдельной задачей (подключение /rollV к VampireCharacter + VampireRollRegistry).
        // Здесь лишь сообщаем автору выбор и оставляем кнопки исходного вида.
        await component.RespondAsync(
            $"Принято: перебросить {count} худших regular-кубика (−1 воля). " +
            "Применится после интеграции с листом персонажа.",
            ephemeral: true);
        return true;
    }

    private async Task<bool> HandleBackAsync(SocketMessageComponent component, ulong originalUserId)
    {
        if (!Authorizes(component, originalUserId)) return await ReplyNotYoursAsync(component);

        await component.Message.ModifyAsync(msg =>
        {
            msg.Components = VampireRollComponents.BuildRollButtons(originalUserId);
        });
        await component.DeferAsync(ephemeral: true);
        return true;
    }

    private async Task<bool> HandleRepeatAsync(SocketMessageComponent component, ulong originalUserId)
    {
        if (!Authorizes(component, originalUserId)) return await ReplyNotYoursAsync(component);

        // Демонстрация чистой логики: кидаём 5 → 4 куба (по правилу V20).
        var rng = new SystemRandomAdapter();
        VampireRepeatReroll.Repeat repeat;
        try
        {
            repeat = VampireRepeatReroll.RollRepeat(5, rng);
        }
        catch (ArgumentException)
        {
            await component.RespondAsync("Повторный бросок недоступен (исходный пул < 1).",
                ephemeral: true);
            return true;
        }

        var diceStr = repeat.RepeatDice.Length == 0
            ? "—"
            : string.Join(", ", repeat.RepeatDice);
        var label = repeat.IsBotch
            ? $"🎲 Повторный пул: {repeat.RepeatPoolSize} | кубы: {diceStr} | **БОТЧ**"
            : $"🎲 Повторный пул: {repeat.RepeatPoolSize} | кубы: {diceStr} | Успехов: **{repeat.Successes}**";

        await component.RespondAsync(label +
            "\n_Результат повторного броска — окончательный (не лучший из двух)._",
            ephemeral: true);
        return true;
    }

    private async Task<bool> HandleDoneAsync(SocketMessageComponent component, ulong originalUserId)
    {
        if (!Authorizes(component, originalUserId)) return await ReplyNotYoursAsync(component);

        await component.Message.ModifyAsync(msg =>
        {
            msg.Components = VampireRollComponents.BuildEmpty();
        });
        await component.DeferAsync(ephemeral: true);
        return true;
    }
}

