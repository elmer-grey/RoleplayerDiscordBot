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
        /// <summary>Открыть/скрыть блок «Клан» (клановые дисциплины + изъян).</summary>
        Clan,
        /// <summary>Открыть/скрыть блок «Мораль» (Путь, расстройства, проверка совести).</summary>
        Morality,
        /// <summary>Открыть/скрыть блок «Опыт» (начисление/трата).</summary>
        Experience,
        /// <summary>Открыть блок «Ярость» (проверка сопротивления ярости V20 стр. 322-325, #42).</summary>
        Frenzy,
        /// <summary>Переключить «активность» персонажа для бросков (только если у игрока несколько чарников).</summary>
        ToggleActive,
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
        private const string ButtonLabelClan        = "Клан";
        private const string ButtonLabelMorality    = "Мораль";
        private const string ButtonLabelExperience  = "Опыт";
        private const string ButtonLabelFrenzy      = "🔥 Ярость";
        private const string ButtonLabelActive      = "✅ Активный";
        private const string ButtonLabelInactive    = "❌ Неактивный";

            /// <summary>
            /// Собрать кнопки под листом в одном ActionRow.
            /// </summary>
            /// <param name="character">Чарник, для которого строится ряд кнопок.</param>
            /// <param name="showActiveToggle">
            /// true — добавить кнопку активности (если у игрока несколько чарников).
            /// false — не показывать (один чарник).
            /// </param>
            /// <param name="isActive">
            /// true если этот чарник сейчас активный. Влияет на текст кнопки активности.
            /// </param>
            /// <param name="showExperienceButton">
            /// true — добавить второй ActionRow с кнопкой «Опыт» (Roadmap #34).
            /// false — не показывать.
            /// </param>
            /// <param name="showFrenzyButton">
            /// true — добавить кнопку «🔥 Ярость» во второй ряд (#42 «Зверь в ярости»).
            /// </param>
            public static MessageComponent Build(
                VampireCharacter character,
                bool showActiveToggle = false,
                bool isActive = true,
                bool showExperienceButton = false,
                bool showFrenzyButton = false)
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
                        ButtonStyle.Primary)
                    .WithButton(ButtonLabelClan, BuildCustomId(VampireSheetAction.Clan, character.CharacterId),
                        ButtonStyle.Secondary)
                    .WithButton(ButtonLabelMorality, BuildCustomId(VampireSheetAction.Morality, character.CharacterId),
                        ButtonStyle.Secondary);

                if (showActiveToggle)
                {
                    var label = isActive ? ButtonLabelActive : ButtonLabelInactive;
                    var style = isActive ? ButtonStyle.Success : ButtonStyle.Secondary;
                    cb.WithButton(label, BuildCustomId(VampireSheetAction.ToggleActive, character.CharacterId), style);
                }

                if (showExperienceButton || showFrenzyButton)
                {
                    // Второй ряд: «Опыт» и/или «Ярость». Discord разрешает до 5 кнопок в ряду,
                    // пока оставляем в отдельном ActionRow для чистоты.
                    if (showExperienceButton)
                    {
                        cb.WithButton(ButtonLabelExperience,
                            BuildCustomId(VampireSheetAction.Experience, character.CharacterId),
                            ButtonStyle.Success, row: 1);
                    }
                    if (showFrenzyButton)
                    {
                        cb.WithButton(ButtonLabelFrenzy,
                            BuildCustomId(VampireSheetAction.Frenzy, character.CharacterId),
                            ButtonStyle.Danger, row: 1);
                    }
                }

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
            VampireSheetAction.Description  => "desc",
            VampireSheetAction.Willpower    => "wp",
            VampireSheetAction.Health       => "hp",
            VampireSheetAction.Clan         => "clan",
            VampireSheetAction.Morality     => "moral",
            VampireSheetAction.Experience   => "xp",
            VampireSheetAction.Frenzy       => "frenzy",
            VampireSheetAction.ToggleActive => "active",
            _ => throw new InvalidEnumArgumentException(nameof(action), (int)action, typeof(VampireSheetAction)),
        };

        private static bool TryParseAction(string s, out VampireSheetAction action)
        {
            switch (s)
            {
                case "desc":   action = VampireSheetAction.Description;  return true;
                case "wp":     action = VampireSheetAction.Willpower;    return true;
                case "hp":     action = VampireSheetAction.Health;       return true;
                case "clan":   action = VampireSheetAction.Clan;         return true;
                case "moral":  action = VampireSheetAction.Morality;     return true;
                case "xp":     action = VampireSheetAction.Experience;   return true;
                case "frenzy": action = VampireSheetAction.Frenzy;       return true;
                case "active": action = VampireSheetAction.ToggleActive; return true;
                default:       action = default;                          return false;
            }
        }
    }
}
