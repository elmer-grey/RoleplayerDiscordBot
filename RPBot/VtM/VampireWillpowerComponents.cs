using System;
using System.ComponentModel;
using Discord;

namespace RPBot.VtM
{
    /// <summary>
    /// Действие под блоком «Воля».
    /// </summary>
    public enum WillpowerAction
    {
        /// <summary>Потратить 1 пункт (сопротивление). Можно 1 раз за ход.</summary>
        SpendOne,
        /// <summary>Восстановить 1 пункт (только в конце истории, по решению рассказчика).</summary>
        RestoreOne,
        /// <summary>Закрыть блок.</summary>
        Close,
    }

    /// <summary>
    /// Кнопки для блока «Воля» и парсер customId.
    /// </summary>
    /// <remarks>
    /// <para>CustomId: <c>vtm_will:&lt;action&gt;:&lt;characterId&gt;</c>.</para>
    /// </remarks>
    public static class VampireWillpowerComponents
    {
        public const string Prefix = "vtm_will";

        public static MessageComponent Build(Guid characterId)
        {
            if (characterId == Guid.Empty)
                throw new ArgumentException("CharacterId обязателен", nameof(characterId));

            return new ComponentBuilder()
                .WithButton("Сопротивление (−1)", BuildId(WillpowerAction.SpendOne,   characterId), ButtonStyle.Primary)
                .WithButton("Восстановление (+1)", BuildId(WillpowerAction.RestoreOne, characterId), ButtonStyle.Success)
                .WithButton("Скрыть",              BuildId(WillpowerAction.Close,      characterId), ButtonStyle.Secondary)
                .Build();
        }

        public static string BuildId(WillpowerAction action, Guid characterId)
            => $"{Prefix}:{ActionToString(action)}:{characterId:N}";

        public static bool TryParse(string customId, out WillpowerAction action, out Guid characterId)
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

        private static string ActionToString(WillpowerAction action) => action switch
        {
            WillpowerAction.SpendOne   => "spend",
            WillpowerAction.RestoreOne => "restore",
            WillpowerAction.Close      => "close",
            _ => throw new InvalidEnumArgumentException(nameof(action), (int)action, typeof(WillpowerAction)),
        };

        private static bool TryParseAction(string s, out WillpowerAction action)
        {
            switch (s)
            {
                case "spend":   action = WillpowerAction.SpendOne;   return true;
                case "restore": action = WillpowerAction.RestoreOne; return true;
                case "close":   action = WillpowerAction.Close;      return true;
                default:        action = default;                    return false;
            }
        }
    }
}
