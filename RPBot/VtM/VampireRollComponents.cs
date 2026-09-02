using Discord;
using System;

namespace RPBot.VtM
{
    /// <summary>
    /// Строит Discord-компоненты для сообщения с результатом броска VtM.
    /// </summary>
    /// <remarks>
    /// Под основным сообщением с броском — 4 кнопки:
    ///   • «🎲 Переброс 1 кубика» — customId <c>vam_reroll:1:{userId}</c>;
    ///   • «🎲 Переброс 2 кубиков» — customId <c>vam_reroll:2:{userId}</c>;
    ///   • «🎲 Переброс 3 кубиков» — customId <c>vam_reroll:3:{userId}</c>;
    ///   • «✅ Готово» — customId <c>vam_done:{userId}</c>, просто снимает кнопки.
    ///
    /// Приватной видимости в Discord нет — handler проверяет <c>component.User.Id == originalUserId</c>
    /// и при чужом нажатии отвечает ephemeral «это не твой бросок».
    /// </remarks>
    public static class VampireRollComponents
    {
        public const string RerollPrefix = "vam_reroll";
        public const string DoneAction = "vam_done";

        /// <summary>
        /// Собрать набор кнопок (3 переброса + «Готово») для сообщения с броском.
        /// </summary>
        /// <param name="originalUserId">Discord-ID автора броска. Используется в customId для авторизации.</param>
        public static MessageComponent BuildRollButtons(ulong originalUserId)
        {
            return new ComponentBuilder()
                .WithButton("🎲 Переброс 1 кубика", $"{RerollPrefix}:1:{originalUserId}", ButtonStyle.Primary)
                .WithButton("🎲 Переброс 2 кубиков", $"{RerollPrefix}:2:{originalUserId}", ButtonStyle.Primary)
                .WithButton("🎲 Переброс 3 кубиков", $"{RerollPrefix}:3:{originalUserId}", ButtonStyle.Primary)
                .WithButton("✅ Готово", $"{DoneAction}:{originalUserId}", ButtonStyle.Secondary)
                .Build();
        }

        /// <summary>
        /// Пустой набор компонентов — для сообщения после переброса/«Готово», чтобы кнопки исчезли.
        /// </summary>
        public static MessageComponent BuildEmpty() => new ComponentBuilder().Build();

        /// <summary>
        /// Разобрать customId кнопки переброса.
        /// </summary>
        /// <returns>true, если это кнопка переброса и parsed корректен.</returns>
        public static bool TryParseReroll(string customId, out ulong originalUserId, out int count)
        {
            originalUserId = 0;
            count = 0;
            if (string.IsNullOrEmpty(customId)) return false;
            if (!customId.StartsWith(RerollPrefix + ":")) return false;
            var parts = customId.Split(':');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[1], out count)) return false;
            if (count < 1 || count > 3) return false;
            if (!ulong.TryParse(parts[2], out originalUserId)) return false;
            return true;
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