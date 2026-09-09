using System;

namespace RPBot.VtM;

/// <summary>
/// Резолвер для команды <c>/vampire_show</c>. Чистая логика без Discord-зависимостей:
/// на входе данные slash-опций и текущее состояние хранилища — на выходе намерение показать
/// конкретного персонажа (или диагностическая ошибка).
/// </summary>
/// <remarks>
/// <para>Правила резолва (по обсуждению):</para>
/// <list type="bullet">
///   <item><b>Имя обязательно</b> всегда. Без имени команда не имеет смысла ни в DM, ни в public.</item>
///   <item><b>DM</b> (default): если указан <c>@user</c> — ищем привязку именно к этому user.Id
///         (ошибка, если к нему ничего не привязано). Иначе ищем по имени. ST может смотреть
///         любое имя без @user-а (он явно знает, кого просит).</item>
///   <item><b>Public</b>: разрешён либо <c>@user</c>, либо имя. <c>@user</c> берётся как «искать
///         его привязанного персонажа» — это безопасно, потому что в листе уже отображается ник.</item>
///   <item>Не резолвим «по умолчанию» (т.е. команда без аргументов) — нет смысла показывать
///         случайному вызывающему свой лист, не убедившись, что он существует.</item>
/// </list>
/// </remarks>
public static class VampireShowResolver
{
    public enum Mode
    {
        /// <summary>Лист улетает в DM получателю (с кнопками).</summary>
        Dm,
        /// <summary>Лист публикуется в текущем канале (без кнопок).</summary>
        Public,
    }

    public enum Failure
    {
        None,
        NameRequired,
        CharacterNotFound,
        UserHasNoCharacter,
        UserMismatch, // @user указывает на плеера, не совпадающего с тем, кто привязан к этому персонажу.
    }

    /// <summary>
    /// Решение резолвера. Содержит либо персонажа, либо код ошибки (один из).
    /// </summary>
    public readonly record struct Decision(
        VampireCharacter? Character,
        Failure FailureCode,
        string Message);

    /// <summary>
    /// Решить, какого персонажа нужно показать.
    /// </summary>
    /// <param name="storage">Хранилище персонажей гильдии.</param>
    /// <param name="mode">Режим показа (DM/public).</param>
    /// <param name="name">Опциональное имя персонажа.</param>
    /// <param name="userId">Опциональный Discord userId, к которому привязан плеер.</param>
    /// <param name="mention">Опциональное имя упомянутого пользователя (для сообщения об ошибке
    /// и для публичного режима — чтобы корректно показать владельца в embed).</param>
    public static Decision Resolve(
        VampireStorage storage,
        Mode mode,
        string? name,
        ulong? userId,
        string? mention = null)
    {
        if (storage is null) throw new ArgumentNullException(nameof(storage));

        var hasName = !string.IsNullOrWhiteSpace(name);
        var hasUser = userId.HasValue;

        // Правило 1: имя обязательно ВСЕГДА. Если ничего не указано — команда бессмысленна.
        if (!hasName && !hasUser)
        {
            return new Decision(null, Failure.NameRequired,
                "Укажите имя персонажа (обязательно) или @user.");
        }

        // Только @user без имени → пытаемся найти привязку к этому user.
        if (!hasName && hasUser)
        {
            var byUser = storage.GetByPlayerId(userId!.Value);
            if (byUser == null)
            {
                var who = string.IsNullOrWhiteSpace(mention) ? "этот пользователь" : mention;
                        var hint = string.IsNullOrWhiteSpace(mention) ? "user" : mention;
                        return new Decision(null, Failure.UserHasNoCharacter,
                            $"У {who} нет привязанного персонажа. Сначала `/vampire_bind <имя> {hint}`.");
                    }
                    return new Decision(byUser, Failure.None, "");
                }

        // Есть имя → ищем.
        VampireCharacter? byName;
        try
        {
            byName = storage.FindByCharacterName(name!);
        }
        catch (InvalidOperationException ex)
        {
            // Несколько совпадений — превращаем в понятную ошибку.
            return new Decision(null, Failure.CharacterNotFound, ex.Message);
        }

        if (byName == null)
        {
            return new Decision(null, Failure.CharacterNotFound,
                $"Персонаж «{name}» не найден.");
        }

        // Доп. согласованность: если указан @user, его привязка должна совпадать с тем,
        // что мы нашли по имени. Это защищает от опечаток.
        if (hasUser)
        {
            if (byName.PlayerId == 0)
            {
                // Персонаж не привязан — @user в этом режиме бесполезен.
                return new Decision(null, Failure.UserMismatch,
                    $"«{name}» ещё не привязан к игроку. Сначала `/vampire_bind {name}`.");
            }
            if (byName.PlayerId != userId!.Value)
            {
                return new Decision(null, Failure.UserMismatch,
                    $"«{name}» привязан к другому игроку. Сначала `/vampire_unbind {name}`.");
            }
        }

        return new Decision(byName, Failure.None, "");
    }
}
