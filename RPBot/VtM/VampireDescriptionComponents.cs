using System;
using Discord;

namespace RPBot.VtM
{
    /// <summary>
    /// Кнопки блока «Описание».
    /// </summary>
    /// <remarks>
    /// <para>CustomId: <c>vtm_desc:&lt;action&gt;:&lt;characterId&gt;</c>.</para>
    /// <para>Сейчас — одна кнопка «Изменить» (Bio) + «Скрыть».
    /// В дальнейшем сюда добавится «Перепривязать аватар».</para>
    /// </remarks>
    public static class VampireDescriptionComponents
    {
        public const string Prefix = "vtm_desc";

        /// <summary>Действие под блоком «Описание».</summary>
        public enum DescriptionAction
        {
            /// <summary>Открыть модальное окно редактирования Bio.</summary>
            Edit,
            /// <summary>Скрыть блок.</summary>
            Close,
        }

        public static MessageComponent Build(VampireCharacter c)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            if (c.CharacterId == Guid.Empty)
                throw new ArgumentException("CharacterId обязателен", nameof(c));

            return new ComponentBuilder()
                .WithButton("Изменить", BuildId(DescriptionAction.Edit,  c.CharacterId), ButtonStyle.Primary)
                .WithButton("Скрыть",   BuildId(DescriptionAction.Close, c.CharacterId), ButtonStyle.Secondary)
                .Build();
        }

        public static string BuildId(DescriptionAction action, Guid characterId)
            => $"{Prefix}:{ActionToString(action)}:{characterId:N}";

        public static bool TryParse(string customId, out DescriptionAction action, out Guid characterId)
        {
            action = default;
            characterId = Guid.Empty;
            if (string.IsNullOrEmpty(customId)) return false;
            if (!customId.StartsWith(Prefix + ":")) return false;
            var parts = customId.Split(':');
            if (parts.Length != 3) return false;
            if (!TryParseAction(parts[1], out action)) return false;
            if (!Guid.TryParseExact(parts[2], "N", out characterId)) return false;
            return characterId != Guid.Empty;
        }

        public static bool IsOurButton(string customId)
            => !string.IsNullOrEmpty(customId) && customId.StartsWith(Prefix + ":");

        private static string ActionToString(DescriptionAction a) => a switch
        {
            DescriptionAction.Edit  => "edit",
            DescriptionAction.Close => "close",
            _ => throw new ArgumentOutOfRangeException(nameof(a), a, null),
        };

        private static bool TryParseAction(string s, out DescriptionAction action)
        {
            switch (s)
            {
                case "edit":  action = DescriptionAction.Edit;  return true;
                case "close": action = DescriptionAction.Close; return true;
                default:      action = default;                 return false;
            }
        }
    }
}
