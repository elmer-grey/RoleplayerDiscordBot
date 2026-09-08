using System;
using Discord;

namespace RPBot.VtM
{
    /// <summary>
    /// Действия для кнопок карточки ярости.
    /// </summary>
    /// <remarks>
    /// CustomId формат: <c>vam_frenzy:&lt;action&gt;:&lt;characterId&gt;:&lt;kind&gt;</c>, где
    /// <c>kind</c> = <c>fr</c> (Frenzy) или <c>rs</c> (Rötschreck). Хранится в id, чтобы
    /// handler не перебирал персонажей.
    /// </remarks>
    public enum VampireFrenzyAction
    {
        /// <summary>Бросить сдерживание ярости (Frenzy).</summary>
        RollFrenzy,
        /// <summary>Бросить сдерживание Ротшрека (Rötschreck).</summary>
        RollRotschreck,
        /// <summary>Удалить один атавизм (по индексу в списке).</summary>
        ClearAtavism,
    }

    /// <summary>Константы и фабрика кнопок для карточки ярости.</summary>
    public static class VampireFrenzyComponents
    {
        public const string Prefix = "vam_frenzy";

        private const string BtnRollFrenzy     = "🩸 Сдержать ярость";
        private const string BtnRollRotschreck = "🌑 Сдержать Ротшрек";

        /// <summary>Собрать ActionRow с двумя основными кнопками броска.</summary>
        public static MessageComponent Build(VampireCharacter character)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));

            return new ComponentBuilder()
                .WithButton(BtnRollFrenzy,
                    BuildId(VampireFrenzyAction.RollFrenzy, character.CharacterId, "fr"),
                    ButtonStyle.Danger)
                .WithButton(BtnRollRotschreck,
                    BuildId(VampireFrenzyAction.RollRotschreck, character.CharacterId, "rs"),
                    ButtonStyle.Primary)
                .WithButton("🗑 Очистить атавизм",
                    BuildId(VampireFrenzyAction.ClearAtavism, character.CharacterId, "fr"),
                    ButtonStyle.Secondary)
                .Build();
        }

        /// <summary>Собрать customId для кнопки.</summary>
        public static string BuildId(VampireFrenzyAction action, Guid characterId, string kind)
            => $"{Prefix}:{ActionToString(action)}:{characterId:N}:{kind}";

        /// <summary>Распарсить customId нашей кнопки.</summary>
        public static bool TryParse(string customId, out VampireFrenzyAction action, out Guid characterId, out string kind)
        {
            action = default;
            characterId = Guid.Empty;
            kind = string.Empty;
            if (string.IsNullOrEmpty(customId)) return false;
            if (!customId.StartsWith(Prefix + ":")) return false;

            var parts = customId.Split(':');
            if (parts.Length != 4) return false;
            if (!TryParseAction(parts[1], out action)) return false;
            if (!Guid.TryParseExact(parts[2], "N", out characterId)) return false;
            kind = parts[3];
            return characterId != Guid.Empty;
        }

        /// <summary>Это customId нашей кнопки (без полного парсинга).</summary>
        public static bool IsOurButton(string customId)
            => !string.IsNullOrEmpty(customId) && customId.StartsWith(Prefix + ":");

        private static string ActionToString(VampireFrenzyAction a) => a switch
        {
            VampireFrenzyAction.RollFrenzy     => "rollfrenzy",
            VampireFrenzyAction.RollRotschreck => "rollrotschreck",
            VampireFrenzyAction.ClearAtavism   => "clearatav",
            _ => throw new ArgumentOutOfRangeException(nameof(a)),
        };

        private static bool TryParseAction(string s, out VampireFrenzyAction action)
        {
            switch (s)
            {
                case "rollfrenzy":     action = VampireFrenzyAction.RollFrenzy;     return true;
                case "rollrotschreck": action = VampireFrenzyAction.RollRotschreck; return true;
                case "clearatav":      action = VampireFrenzyAction.ClearAtavism;   return true;
                default:               action = default;                            return false;
            }
        }
    }
}
