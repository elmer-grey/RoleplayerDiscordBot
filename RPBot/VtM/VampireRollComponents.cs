using Discord;

namespace RPBot.VtM
{
    /// <summary>
    /// Строит Discord-компоненты для сообщения с результатом броска VtM.
    /// </summary>
    /// <remarks>
    /// Под основным сообщением с броском — 2 ряда кнопок:
    ///   • Ряд 1: «🎲 Переброс за волю» (customId <c>vam_reroll:{userId}</c>) и «✅ Готово»
    ///     (customId <c>vam_done:{userId}</c>, просто снимает кнопки).
    ///   • Ряд 2: «🔁 Повторить» (customId <c>vam_repeat:{userId}</c>) — повторный бросок того же
    ///     пула без траты пункта воли (V20: «повторный бросок»).
    ///
    /// При нажатии на «🎲 Переброс за волю» handler перерисовывает сообщение на picker
    /// (<see cref="BuildRerollAmountPicker"/>) с выбором числа кубиков 1/2/3 и кнопкой «← Назад».
    /// По выбору N handler применяет <see cref="WillpowerReroll"/> со списанием пункта воли и
    /// удаляет запись из <see cref="VampireRollRegistry"/>.
    ///
    /// «🔁 Повторить» — повторный бросок (V20, стр. 274): можно повторить любой бросок один раз,
    /// результат — лучший из двух. Стоимости воли нет.
    ///
    /// Приватной видимости в Discord нет — handler проверяет <c>component.User.Id == originalUserId</c>
    /// и при чужом нажатии отвечает ephemeral «это не твой бросок».
    /// </remarks>
    public static class VampireRollComponents
    {
        /// <summary>Префикс кнопки «Переброс за волю» (открывает picker).</summary>
        public const string RerollPrefix = "vam_reroll";

        /// <summary>Префикс кнопки выбора числа кубиков в picker'е (после открытия переброса).</summary>
        public const string RerollAmountPrefix = "vam_reroll_amt";

        /// <summary>Префикс кнопки «← Назад» из picker'а.</summary>
        public const string RerollBackAction = "vam_reroll_back";

        /// <summary>Префикс кнопки «🔁 Повторить» (повторный бросок без траты воли).</summary>
        public const string RepeatAction = "vam_repeat";

        /// <summary>Префикс кнопки «✅ Готово».</summary>
        public const string DoneAction = "vam_done";

        /// <summary>
        /// Собрать основной набор кнопок (2 ряда): «Переброс за волю | Готово», «Повторить».
        /// </summary>
        /// <param name="originalUserId">Discord-ID автора броска. Используется в customId для авторизации.</param>
        public static MessageComponent BuildRollButtons(ulong originalUserId)
        {
            return new ComponentBuilder()
                .WithButton("🎲 Переброс за волю", $"{RerollPrefix}:{originalUserId}", ButtonStyle.Primary)
                .WithButton("✅ Готово", $"{DoneAction}:{originalUserId}", ButtonStyle.Secondary)
                .WithButton("🔁 Повторить", $"{RepeatAction}:{originalUserId}", ButtonStyle.Secondary)
                .Build();
        }

        /// <summary>
        /// Picker числа кубиков для переброса (3 кнопки в 1 ряду + кнопка «← Назад» во 2 ряду).
        /// </summary>
        /// <remarks>
        /// Вызывается при нажатии «🎲 Переброс за волю». Handler по выбору N применяет
        /// <see cref="WillpowerReroll"/> со списанием 1 пункта воли.
        /// </remarks>
        /// <param name="originalUserId">Discord-ID автора броска.</param>
        public static MessageComponent BuildRerollAmountPicker(ulong originalUserId)
        {
            return new ComponentBuilder()
                .WithButton("1 кубик", $"{RerollAmountPrefix}:1:{originalUserId}", ButtonStyle.Primary)
                .WithButton("2 кубика", $"{RerollAmountPrefix}:2:{originalUserId}", ButtonStyle.Primary)
                .WithButton("3 кубика", $"{RerollAmountPrefix}:3:{originalUserId}", ButtonStyle.Primary)
                .WithButton("← Назад", $"{RerollBackAction}:{originalUserId}", ButtonStyle.Secondary)
                .Build();
        }

        /// <summary>
        /// Пустой набор компонентов — для сообщения после переброса/«Готово», чтобы кнопки исчезли.
        /// </summary>
        public static MessageComponent BuildEmpty() => new ComponentBuilder().Build();

        /// <summary>
        /// Это кнопка «Переброс за волю» (открыть picker)?
        /// </summary>
        public static bool IsRerollOpenButton(string customId, out ulong originalUserId)
        {
            originalUserId = 0;
            if (string.IsNullOrEmpty(customId)) return false;
            if (!customId.StartsWith(RerollPrefix + ":")) return false;
            var parts = customId.Split(':');
            if (parts.Length != 2) return false;
            return ulong.TryParse(parts[1], out originalUserId);
        }

        /// <summary>
        /// Разобрать customId кнопки выбора числа кубиков из picker'а.
        /// </summary>
        /// <returns>true, если это кнопка picker'а и parsed корректен.</returns>
        public static bool TryParseRerollAmount(string customId, out ulong originalUserId, out int count)
        {
            originalUserId = 0;
            count = 0;
            if (string.IsNullOrEmpty(customId)) return false;
            if (!customId.StartsWith(RerollAmountPrefix + ":")) return false;
            var parts = customId.Split(':');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[1], out count)) return false;
            if (count < 1 || count > 3) return false;
            if (!ulong.TryParse(parts[2], out originalUserId)) return false;
            return true;
        }

        /// <summary>
        /// Это кнопка «← Назад» из picker'а?
        /// </summary>
        public static bool IsRerollBackButton(string customId, out ulong originalUserId)
        {
            originalUserId = 0;
            if (string.IsNullOrEmpty(customId)) return false;
            if (!customId.StartsWith(RerollBackAction + ":")) return false;
            var parts = customId.Split(':');
            if (parts.Length != 2) return false;
            return ulong.TryParse(parts[1], out originalUserId);
        }

        /// <summary>
        /// Это кнопка «🔁 Повторить»?
        /// </summary>
        public static bool IsRepeatButton(string customId, out ulong originalUserId)
        {
            originalUserId = 0;
            if (string.IsNullOrEmpty(customId)) return false;
            if (!customId.StartsWith(RepeatAction + ":")) return false;
            var parts = customId.Split(':');
            if (parts.Length != 2) return false;
            return ulong.TryParse(parts[1], out originalUserId);
        }

        /// <summary>
        /// Это кнопка «Готово»?
        /// </summary>
        public static bool IsDoneButton(string customId, out ulong originalUserId)
        {
            originalUserId = 0;
            if (string.IsNullOrEmpty(customId)) return false;
            if (!customId.StartsWith(DoneAction + ":")) return false;
            var parts = customId.Split(':');
            if (parts.Length != 2) return false;
            return ulong.TryParse(parts[1], out originalUserId);
        }
    }
}