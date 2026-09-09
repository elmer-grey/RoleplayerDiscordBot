using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.SlashModules;
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
/// <para>Доступ к <see cref="VampireRollRegistry"/> и активному чарнику —
/// через <see cref="RollContext"/> (DI-точка).</para>
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

    private static async Task<bool> ReplyNotYoursAsync(SocketMessageComponent component, string text)
    {
        await component.RespondAsync(text, ephemeral: true);
        return true;
    }

    private static bool Authorizes(SocketMessageComponent component, ulong originalUserId)
    {
        return component.User.Id == originalUserId;
    }

    private async Task<bool> HandleOpenPickerAsync(SocketMessageComponent component, ulong originalUserId)
    {
        if (!Authorizes(component, originalUserId))
            return await ReplyNotYoursAsync(component, "Это не твой бросок.");

        await component.Message.ModifyAsync(msg =>
        {
            msg.Components = VampireRollComponents.BuildRerollAmountPicker(originalUserId);
        });

        // 15-секундный автооткат: если пользователь не выберет 1/2/3,
        // picker сам откатится к основному набору кнопок.
        var messageId = component.Message.Id;
        var message = component.Message;
        VampireRollMenuAutoRollback.Schedule(
            messageId,
            VampireRollMenuAutoRollback.MenuState.RerollPicker(originalUserId),
            async _ =>
            {
                try
                {
                    await message.ModifyAsync(msg =>
                    {
                        msg.Components = VampireRollComponents.BuildRollButtons(originalUserId);
                    });
                }
                catch
                {
                    // Сообщение удалено / бот потерял доступ — тихо игнорируем.
                }
            });

        await component.DeferAsync(ephemeral: true);
        return true;
    }

    /// <summary>
    /// Пользователь выбрал 1/2/3 кубика — перебрасываем ВСЕ доступные (с лимитом),
    /// списываем 1 пункт воли с активного чарника, перерисовываем embed с новым результатом.
    /// </summary>
    private async Task<bool> HandleMenuAsync(SocketMessageComponent component, ulong originalUserId, string customId)
    {
        if (!Authorizes(component, originalUserId))
            return await ReplyNotYoursAsync(component, "Это не твой бросок.");

        var value = component.Data.Values?.FirstOrDefault();
        if (!VampireRollComponents.TryParseRerollMenuValue(value, out var requestedCount))
        {
            await component.RespondAsync("Не удалось разобрать выбор количества кубиков.", ephemeral: true);
            return true;
        }

        var registry = RollContext.Registry;
        if (registry == null)
        {
            await component.RespondAsync("Реестр бросков не инициализирован.", ephemeral: true);
            return true;
        }
        if (!registry.TryGet(originalUserId, out var snapshot) || snapshot == null)
        {
            await component.RespondAsync(
                "Снапшот броска истёк или отсутствует. Кидай заново через /vampire_roll.",
                ephemeral: true);
            return true;
        }
        if (snapshot.MessageId != component.Message.Id)
        {
            await component.RespondAsync(
                "Это не тот бросок (ID сообщения не совпадает). Кидай заново через /vampire_roll.",
                ephemeral: true);
            return true;
        }

        // Пользователь успел выбрать число кубиков — отменяем 15-секундный
        // автооткат picker'а, чтобы он не «дёргал» сообщение после переброса.
        VampireRollMenuAutoRollback.Cancel(component.Message.Id);

        var activeLookup = RollContext.ActiveCharacterLookup;
        if (activeLookup == null || component.GuildId == null)
        {
            await component.RespondAsync(
                "Переброс за волю требует активного чарника на сервере. " +
                "Сначала используй /vampire bind в канале.",
                ephemeral: true);
            return true;
        }

        var active = await activeLookup(component.GuildId.Value, originalUserId);
        if (active == null)
        {
            await component.RespondAsync(
                "Не нашёл активного персонажа. Используй /vampire bind на сервере.",
                ephemeral: true);
            return true;
        }

#pragma warning disable CS0618 // WillpowerPoints устарел для листа, но это runtime-хранилище для переброса.
        if (active.Character.WillpowerPoints < 1)
#pragma warning restore CS0618
        {
            await component.RespondAsync(
                "Не хватает пунктов воли (нужен ≥ 1). " +
                $"Сейчас {active.Character.WillpowerPoints}.",
                ephemeral: true);
            return true;
        }

        // V20+V5: перебрасываются худшие regular-кубы (V5-разбиение),
        // hunger не трогаем; специализация (V20) применяется к regular.
        var actualCount = WillpowerReroll.ActualRerollCount(snapshot.RegularCount, requestedCount);

        var rng = new SystemRandomAdapter();
        var newRegular = WillpowerReroll.RerollDice(snapshot.RegularDice, actualCount, rng);

        // Списываем 1 пункт воли.
#pragma warning disable CS0618
        active.Character.WillpowerPoints -= 1;
#pragma warning restore CS0618
        await active.Storage.UpsertAsync(active.Character);

        // Удаляем запись (1 переброс = одно списание).
        registry.Forget(originalUserId);

        // V20+V5: пересчёт через CountSuccessesHybrid (regular новый, hunger прежний).
        int newSuccesses = VampireDicePool.CountSuccessesHybrid(
            newRegular, snapshot.HungerDice, snapshot.Specialization);
        var labelSuffix = actualCount < requestedCount
            ? $"(запрошено {requestedCount}, доступно {snapshot.RegularCount})"
            : "";
        var embed = BuildRerollEmbed(newRegular, snapshot.HungerDice, snapshot.Specialization,
            actualCount, newSuccesses, active.Character.WillpowerPoints, labelSuffix);

        // Снимаем picker, рисуем кнопки «🔁 Повторить + Готово»:
        // повторная попытка (V20 стр. 286) по-прежнему доступна,
        // а переброс за волю повторно в этом броске уже невозможен (запись удалена).
        // Без кнопки «Специализация»: после переброса новые действия недоступны,
        // и индикатор специализации уже отображается в самом embed.
        //
        // Сразу под обновлённым embed'ом публикуем PNG-кубы нового результата —
        // иначе пользователь не увидит, что изменилось после переброса.
        var attachments = VampireDiceImageProvider.Build(newRegular, snapshot.HungerDice).Files;
        await VampireRollChannelPublisher.ReplaceEmbedAndSendAttachmentsAsync(
            component.Message, embed,
            components: VampireRollComponents.BuildRepeatOnlyButtons(originalUserId),
            extraAttachments: attachments);
        await component.RespondAsync(
            $"Переброс за волю: −1 WP (осталось {active.Character.WillpowerPoints}).",
            ephemeral: true);
        return true;
    }

    /// <summary>Сборка embed'а после переброса (V20+V5: regular новый, hunger прежний).</summary>
    private static Embed BuildRerollEmbed(
        int[] newRegular,
        int[] hungerDice,
        string? specialization,
        int rerolledCount,
        int successes,
        int willpowerPoints,
        string labelSuffix)
    {
        var eb = new EmbedBuilder
        {
            Title = "🎲 Переброс за волю (−1 WP)",
            Color = new Color(0x5B3A8C),
        };
        var regularStr = newRegular.Length == 0 ? "—" : string.Join(", ", newRegular);
        var hungerStr = hungerDice == null || hungerDice.Length == 0 ? "—" : string.Join(", ", hungerDice);
        eb.AddField(
            "Кубы (regular — обновлены)",
            $"regular ({newRegular.Length}): {regularStr}\nhunger ({hungerDice?.Length ?? 0}): {hungerStr}",
            inline: false);
        eb.AddField("Переброшено", $"{rerolledCount} худших regular {labelSuffix}", inline: true);
        eb.AddField("Воли осталось", willpowerPoints.ToString(), inline: true);
        int regTens = newRegular.Count(d => d == 10);
        int regSixesToNines = newRegular.Count(d => d >= 6 && d < 10);
        var breakdown = !string.IsNullOrEmpty(specialization)
            ? $"regular: 6–9 × {regSixesToNines} + 10 × {regTens}×2 (спец.) = {regSixesToNines + regTens * 2}"
            : $"regular: 6–9 × {regSixesToNines} + 10 × {regTens} = {regSixesToNines + regTens}";
        eb.AddField("Подсчёт успехов", breakdown, inline: false);
        eb.AddField("Итог", $"**{successes}** успехов", inline: true);
        return eb.Build();
    }

    private async Task<bool> HandleBackAsync(SocketMessageComponent component, ulong originalUserId)
    {
        if (!Authorizes(component, originalUserId))
            return await ReplyNotYoursAsync(component, "Это не твой бросок.");

        // Пользователь сам нажал «Назад» — отменяем автооткат.
        VampireRollMenuAutoRollback.Cancel(component.Message.Id);

        // Возвращаемся с picker'а обратно к исходному embed.
        // Кнопки не рисуем: до повтора/переброса пользователь должен снова
        // видеть результат, а повторные действия применятся по исходным кнопкам
        // (которые были показаны при первоначальном броске).
        // На случай если embed был утерян — кнопки «Переброс / Готово / Повторить»
        // можно отдать заново, без индикатора «Специализация» (его перебьёт embed).
        await component.Message.ModifyAsync(msg =>
        {
            msg.Components = VampireRollComponents.BuildRollButtons(originalUserId);
        });
        await component.DeferAsync(ephemeral: true);
        return true;
    }

    /// <summary>
    /// Повторный бросок по правилу V20 стр. 286: пул не меняется,
    /// сложность возрастает на 1 пункт, берётся новый результат.
    /// </summary>
    private async Task<bool> HandleRepeatAsync(SocketMessageComponent component, ulong originalUserId)
    {
        if (!Authorizes(component, originalUserId))
            return await ReplyNotYoursAsync(component, "Это не твой бросок.");

        var registry = RollContext.Registry;
        if (registry == null)
        {
            await component.RespondAsync("Реестр бросков не инициализирован.", ephemeral: true);
            return true;
        }
        if (!registry.TryGet(originalUserId, out var snapshot) || snapshot == null)
        {
            await component.RespondAsync(
                "Снапшот броска истёк или отсутствует. Кидай заново через /vampire_roll.",
                ephemeral: true);
            return true;
        }
        if (snapshot.MessageId != component.Message.Id)
        {
            await component.RespondAsync(
                "Это не тот бросок (ID сообщения не совпадает). Кидай заново через /vampire_roll.",
                ephemeral: true);
            return true;
        }

        if (snapshot.PoolSize < VampireRepeatReroll.MinRepeatPool)
        {
            await component.RespondAsync(
                "Пул < 1. Повторный бросок невозможен.",
                ephemeral: true);
            return true;
        }

        // V20 стр. 286: повторная попытка = тот же пул, новая сложность = старая + 1.
        var repeat = VampireRepeatReroll.RollRepeat(
            originalPoolSize: snapshot.PoolSize,
            originalDifficulty: snapshot.Difficulty,
            regularCount: snapshot.RegularCount,
            hungerCount: snapshot.HungerCount,
            rng: new SystemRandomAdapter());

        // Удаляем запись: 1 повтор = один бросок.
        registry.Forget(originalUserId);

        // V20+V5: пересчёт через CountSuccessesHybrid (regular + hunger раздельно).
        int successes = VampireDicePool.CountSuccessesHybrid(
            repeat.NewRegularDice, repeat.NewHungerDice, snapshot.Specialization);
        var regStr = repeat.NewRegularDice.Length == 0 ? "—" : string.Join(", ", repeat.NewRegularDice);
        var hunStr = repeat.NewHungerDice.Length == 0 ? "—" : string.Join(", ", repeat.NewHungerDice);
        var eb = new EmbedBuilder
        {
            Title = "🔁 Повторная попытка (сложность +1)",
            Color = new Color(0x5B3A8C),
        };
        eb.AddField("Сложность", $"{snapshot.Difficulty} → {repeat.NewDifficulty}", inline: true);
        eb.AddField("Пул", $"{snapshot.PoolSize} (не изменился)", inline: true);
        eb.AddField(
            "Кубы (regular)",
            $"{repeat.NewRegularDice.Length}: {regStr}",
            inline: false);
        eb.AddField(
            "Кубы (hunger)",
            $"{repeat.NewHungerDice.Length}: {hunStr}",
            inline: false);
        eb.AddField(
            "Итог",
            $"**{successes}** успехов" +
            (!string.IsNullOrEmpty(snapshot.Specialization)
                ? $" (с учётом специализации «{snapshot.Specialization}», 10 = 2)"
                : ""),
            inline: false);
        eb.Footer = new EmbedFooterBuilder
        {
            Text = "Повторная попытка не меняет пул, повышает сложность на 1.",
        };

        // Оставляем «🎲 Переброс за волю + Готово»: переброс на новом пуле снова доступен,
        // но ещё одна повторная попытка подряд уже не предлагается (рассказчик решает,
        // продолжать ли рост сложности дальше через ещё один «Повторить»).
        //
        // Сразу под обновлённым embed'ом публикуем PNG-кубы нового результата —
        // иначе пользователь не увидит, что изменилось после повтора.
        var attachments = VampireDiceImageProvider.Build(repeat.NewRegularDice, repeat.NewHungerDice).Files;
        await VampireRollChannelPublisher.ReplaceEmbedAndSendAttachmentsAsync(
            component.Message, eb.Build(),
            components: VampireRollComponents.BuildRerollOnlyButtons(originalUserId),
            extraAttachments: attachments);
        await component.RespondAsync(
            $"Повторная попытка: сложность {snapshot.Difficulty} → {repeat.NewDifficulty}.",
            ephemeral: true);
        return true;
    }

    private async Task<bool> HandleDoneAsync(SocketMessageComponent component, ulong originalUserId)
    {
        if (!Authorizes(component, originalUserId))
            return await ReplyNotYoursAsync(component, "Это не твой бросок.");

        await component.Message.ModifyAsync(msg =>
        {
            msg.Components = VampireRollComponents.BuildEmpty();
        });
        await component.DeferAsync(ephemeral: true);
        return true;
    }
}
