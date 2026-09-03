using System;
using System.ComponentModel;
using Discord;

namespace RPBot.VtM
{
    /// <summary>
    /// Действие, которое зашито в кнопку под листом персонажа.
    /// </summary>
    public enum VampireSheetAction
    {
        /// <summary>Открыть/скрыть блок «Описание» (Bio).</summary>
        Description,
        /// <summary>Открыть/скрыть блок «Воля».</summary>
        Willpower,
        /// <summary>Открыть/скрыть блок «Здоровье».</summary>
        Health,
    }

    /// <summary>
    /// Константы customId и фабрика кнопок под листом персонажа.
    /// </summary>
    /// <remarks>
    /// <para>Все три кнопки лежат в одном <see cref="ActionRowBuilder"/>
    /// (Discord не разрешает разные ряды в одной строке, но три кнопки
    /// с коротким текстом помещаются рядом).</para>
    /// <para>CustomId формат: <c>vtm_btn:&lt;action&gt;:&lt;characterId&gt;</c>.</para>
    /// <para>Авторизация: нажатие проверяется на владельца персонажа —
    /// handler обязан убедиться, что <c>component.User.Id == character.PlayerId</c>.</para>
    /// </remarks>
    public static class VampireSheetComponents
    {
        public const string Prefix = "vtm_btn";

        private const string ButtonLabelDescription = "Описание";
        private const string ButtonLabelWillpower   = "Воля";
        private const string ButtonLabelHealth      = "Здоровье";

        /// <summary>
        /// Собрать 3 кнопки под листом в одном ActionRow.
        /// </summary>
        public static MessageComponent Build(VampireCharacter character)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));
            if (character.CharacterId == Guid.Empty)
                throw new ArgumentException("CharacterId обязателен", nameof(character));

            var cb = new ComponentBuilder()
                .WithButton(ButtonLabelDescription, BuildCustomId(VampireSheetAction.Description, character.CharacterId),
                    ButtonStyle.Primary)
                .WithButton(ButtonLabelWillpower, BuildCustomId(VampireSheetAction.Willpower, character.CharacterId),
                    ButtonStyle.Primary)
                .WithButton(ButtonLabelHealth, BuildCustomId(VampireSheetAction.Health, character.CharacterId),
                    ButtonStyle.Primary);
            return cb.Build();
        }

        /// <summary>Собрать customId для одной кнопки.</summary>
        public static string BuildCustomId(VampireSheetAction action, Guid characterId)
            => $"{Prefix}:{ActionToString(action)}:{characterId:N}";

        /// <summary>
        /// Распарсить customId кнопки. Возвращает false для любых чужих id.
        /// </summary>
        public static bool TryParse(string customId, out VampireSheetAction action, out Guid characterId)
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

        /// <summary>Это customId нашей кнопки (без полного парсинга).</summary>
        public static bool IsOurButton(string customId)
            => !string.IsNullOrEmpty(customId) && customId.StartsWith(Prefix + ":");

        private static string ActionToString(VampireSheetAction action) => action switch
        {
            VampireSheetAction.Description => "desc",
            VampireSheetAction.Willpower   => "wp",
            VampireSheetAction.Health      => "hp",
            _ => throw new InvalidEnumArgumentException(nameof(action), (int)action, typeof(VampireSheetAction)),
        };

        private static bool TryParseAction(string s, out VampireSheetAction action)
        {
            switch (s)
            {
                case "desc": action = VampireSheetAction.Description; return true;
                case "wp":   action = VampireSheetAction.Willpower;   return true;
                case "hp":   action = VampireSheetAction.Health;      return true;
                default:     action = default;                        return false;
            }
        }
    }
}
