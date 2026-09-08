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
    ///   • Ряд 2: «🔁 Повторить» (customId <c>vam_repeat:{userId}</c>) — повторный бросок по V20:
    ///     пул N-1 кубов, берётся новый результат.
    ///
    /// При нажатии на «🎲 Переброс за волю» handler перерисовывает сообщение на picker
    /// (<see cref="BuildRerollAmountPicker"/>) с SelectMenu выбора числа кубиков 1/2/3
    /// и кнопкой «← Назад». По выбору N handler применяет <see cref="WillpowerReroll"/>
    /// со списанием пункта воли и удаляет запись из <see cref="VampireRollRegistry"/>.
    ///
    /// «🔁 Повторить» — повторный бросок по правилу повторных попыток (V20, стр. 286/267):
    /// пул уменьшается на 1 кубик, берётся результат повторного броска. Стоимости воли нет.
    ///
    /// Приватной видимости в Discord нет — handler проверяет <c>component.User.Id == originalUserId</c>
    /// и при чужом нажатии отвечает ephemeral «это не твой бросок».
    /// </remarks>
    public static class VampireRollComponents
    {
        /// <summary>Префикс кнопки «Переброс за волю» (открывает picker).</summary>
        public const string RerollPrefix = "vam_reroll";

        /// <summary>Префикс SelectMenu для выбора числа кубиков в picker'е.</summary>
        public const string RerollMenuAction = "vam_reroll_menu";

        /// <summary>Префикс кнопки «← Назад» из picker'а.</summary>
        public const string RerollBackAction = "vam_reroll_back";

        /// <summary>Префикс кнопки «🔁 Повторить» (повторный бросок по V20: пул −1, берётся новый результат).</summary>
        public const string RepeatAction = "vam_repeat";

        /// <summary>Префикс кнопки «✅ Готово».</summary>
        public const string DoneAction = "vam_done";

        /// <summary>Префикс кнопки «⭐ Специализация» (индикатор: «в листе есть спец.»).</summary>
        public const string SpecAction = "vam_spec";

        /// <summary>
        /// Собрать основной набор кнопок: «Переброс за волю | Готово», «Повторить»,
        /// и (если в листе / ad-hoc указана специализация) «⭐ Специализация: &lt;имя&gt;».
        /// </summary>
        /// <param name="originalUserId">Discord-ID автора броска. Используется в customId для авторизации.</param>
        /// <param name="specialization">
        /// Название специализации из листа (или ad-hoc), если она указана для этого броска.
        /// Если null/пустая — кнопка «⭐ Специализация» не показывается.
        /// </param>
        public static MessageComponent BuildRollButtons(ulong originalUserId, string? specialization = null)
        {
            var cb = new ComponentBuilder()
                .WithButton("🎲 Переброс за волю", $"{RerollPrefix}:{originalUserId}", ButtonStyle.Primary)
                .WithButton("✅ Готово", $"{DoneAction}:{originalUserId}", ButtonStyle.Secondary);

            if (!string.IsNullOrWhiteSpace(specialization))
            {
                // Показываем индикатор: «в этом броске применена специализация».
                // Кнопка disabled — носит чисто информативный характер.
                cb.WithButton(
                    $"⭐ Спец.: {Truncate(specialization, 28)}",
                    $"{SpecAction}:{originalUserId}",
                    ButtonStyle.Secondary,
                    disabled: true);
            }

            cb.WithButton("🔁 Повторить", $"{RepeatAction}:{originalUserId}", ButtonStyle.Secondary);
            return cb.Build();
        }

        /// <summary>Усечение строки до заданной длины (с многоточием).</summary>
        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        /// <summary>
        /// Picker числа кубиков для переброса: SelectMenu (1/2/3 кубика) в 1 ряду
        /// + кнопка «← Назад» во 2 ряду. Три варианта собраны в одну SelectMenu.
        /// </summary>
        /// <remarks>
        /// Вызывается при нажатии «🎲 Переброс за волю». Handler по выбору N применяет
        /// <see cref="WillpowerReroll"/> со списанием 1 пункта воли.
        /// </remarks>
        /// <param name="originalUserId">Discord-ID автора броска.</param>
        public static MessageComponent BuildRerollAmountPicker(ulong originalUserId)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId($"{RerollMenuAction}:{originalUserId}")
                .WithPlaceholder("Сколько кубиков перебросить за 1 волю?")
                .WithMinValues(1)
                .WithMaxValues(1)
                .AddOption("1 кубик", "1", "Перебросить 1 худший regular-кубик (−1 воля).")
                .AddOption("2 кубика", "2", "Перебросить 2 худших regular-кубика (−1 воля).")
                .AddOption("3 кубика", "3", "Перебросить 3 худших regular-кубика (−1 воля).");

            return new ComponentBuilder()
                .WithSelectMenu(menu)
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
        /// Разобрать customId SelectMenu выбора числа кубиков из picker'а.
        /// </summary>
        /// <returns>true, если это SelectMenu picker'а и parsed корректен.</returns>
        public static bool TryParseRerollMenu(string customId, out ulong originalUserId)
        {
            originalUserId = 0;
            if (string.IsNullOrEmpty(customId)) return false;
            if (!customId.StartsWith(RerollMenuAction + ":")) return false;
            var parts = customId.Split(':');
            if (parts.Length != 2) return false;
            return ulong.TryParse(parts[1], out originalUserId);
        }

        /// <summary>
        /// Разобрать выбранное значение в SelectMenu (1..3 кубика).
        /// </summary>
        public static bool TryParseRerollMenuValue(string? value, out int count)
        {
            count = 0;
            if (string.IsNullOrEmpty(value)) return false;
            if (!int.TryParse(value, out count)) return false;
            if (count < 1 || count > 3) return false;
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