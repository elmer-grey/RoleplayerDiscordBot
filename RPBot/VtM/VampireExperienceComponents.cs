using System;
using Discord;

namespace RPBot.VtM
{
    /// <summary>
    /// Кнопки выбора «Начислить / Потратить» опыт, открываются из кнопки «Опыт» в листе (Roadmap #34).
    /// </summary>
    /// <remarks>
    /// Кнопки открывают модалку <see cref="VampireExperienceModal"/>, в которой
    /// игрок вводит количество. Применение дельты — в обработчике модалки.
    /// </remarks>
    public static class VampireExperienceComponents
    {
        public const string Prefix = "vtm_xp";

        /// <summary>CustomId кнопки «Начислить опыт».</summary>
        public static string BuildGrantId(Guid characterId)
            => BuildId(ExperienceModalAction.Grant, characterId);

        /// <summary>CustomId кнопки «Потратить опыт».</summary>
        public static string BuildSpendId(Guid characterId)
            => BuildId(ExperienceModalAction.Spend, characterId);

        /// <summary>ActionRow с двумя кнопками — Grant и Spend.</summary>
        public static MessageComponent Build(Guid characterId)
        {
            if (characterId == Guid.Empty)
                throw new ArgumentException("CharacterId обязателен", nameof(characterId));
            return new ComponentBuilder()
                .WithButton("Начислить", BuildGrantId(characterId), ButtonStyle.Success)
                .WithButton("Потратить", BuildSpendId(characterId), ButtonStyle.Danger)
                .Build();
        }

        /// <summary>Это наш customId (кнопка опыта).</summary>
        public static bool IsOurs(string customId)
            => !string.IsNullOrEmpty(customId) && customId.StartsWith(Prefix + ":");

        /// <summary>Распарсить customId кнопки опыта.</summary>
        public static bool TryParse(string customId, out ExperienceModalAction action, out Guid characterId)
        {
            action = default;
            characterId = Guid.Empty;
            if (!IsOurs(customId)) return false;
            var parts = customId.Split(':');
            if (parts.Length != 3) return false;
            if (!TryParseAction(parts[1], out action)) return false;
            if (!Guid.TryParseExact(parts[2], "N", out characterId)) return false;
            return characterId != Guid.Empty;
        }

        private static string BuildId(ExperienceModalAction action, Guid characterId)
            => $"{Prefix}:{ActionToString(action)}:{characterId:N}";

        private static string ActionToString(ExperienceModalAction action) => action switch
        {
            ExperienceModalAction.Grant => "grant",
            ExperienceModalAction.Spend => "spend",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        private static bool TryParseAction(string s, out ExperienceModalAction action)
        {
            switch (s)
            {
                case "grant": action = ExperienceModalAction.Grant; return true;
                case "spend": action = ExperienceModalAction.Spend; return true;
                default:      action = default;                    return false;
            }
        }
    }
}
