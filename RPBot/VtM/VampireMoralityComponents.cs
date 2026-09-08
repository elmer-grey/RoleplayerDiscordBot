using System;
using System.ComponentModel;
using Discord;

namespace RPBot.VtM
{
    /// <summary>
    /// Действие под блоком «Мораль».
    /// </summary>
    public enum MoralityAction
    {
        /// <summary>Начать выбор расстройства для добавления (показывает SelectMenu).</summary>
        StartAddDerangement,
        /// <summary>Удалить последнее расстройство из списка (для отмены ошибки/теста).</summary>
        RemoveLastDerangement,
        /// <summary>Бросить проверку совести (Roadmap #37, V20 стр. 333). Инициатор — игрок,
        /// но V20 предполагает, что назначает рассказчик; сейчас бросает сам владелец чарника
        /// (ST может запретить в более строгом режиме).</summary>
        ConscienceCheck,
    }

    /// <summary>
    /// Кнопки + SelectMenu для блока «Мораль» под листом персонажа.
    /// </summary>
    public static class VampireMoralityComponents
    {
        public const string Prefix = "vtm_moral";

        /// <summary>Собрать ActionRow с кнопками (Add/Remove/Close).</summary>
        public static MessageComponent Build(Guid characterId) => Build(characterId, withDerangementMenu: false);

        /// <summary>
        /// Собрать ActionRow с кнопками и опциональным SelectMenu расстройств.
        /// Discord ограничивает 5 элементов в ActionRow, поэтому если показываем меню — оставляем 3 кнопки.
        /// </summary>
        public static MessageComponent Build(Guid characterId, bool withDerangementMenu)
        {
            if (characterId == Guid.Empty)
                throw new ArgumentException("CharacterId обязателен", nameof(characterId));

            // Discord ограничивает 5 элементов в ActionRow.
            // Базовая раскладка: 3 кнопки (Conscience / Add / Remove). При показе
            // SelectMenu расстройств — 2 кнопки (без Add, потому что SelectMenu его и открывает).
            if (withDerangementMenu)
            {
                var cb = new ComponentBuilder()
                    .WithButton("Проверка совести",
                        BuildId(MoralityAction.ConscienceCheck, characterId),
                        ButtonStyle.Success)
                    .WithButton("Удалить последнее",
                        BuildId(MoralityAction.RemoveLastDerangement, characterId),
                        ButtonStyle.Secondary);

                var menu = new SelectMenuBuilder()
                    .WithCustomId(BuildSelectId(characterId))
                    .WithPlaceholder("Выберите расстройство…")
                    .WithMinValues(1)
                    .WithMaxValues(1);
                foreach (var d in VampireDerangementCatalog.All)
                {
                    menu.AddOption(new SelectMenuOptionBuilder()
                        .WithLabel(d.Name)
                        .WithValue(d.Name)
                        .WithDescription(Truncate(d.Effect, 100)));
                }
                cb.WithSelectMenu(menu);
                return cb.Build();
            }

            return new ComponentBuilder()
                .WithButton("Проверка совести",
                    BuildId(MoralityAction.ConscienceCheck, characterId),
                    ButtonStyle.Success)
                .WithButton("Добавить расстройство",
                    BuildId(MoralityAction.StartAddDerangement, characterId),
                    ButtonStyle.Primary)
                .WithButton("Удалить последнее",
                    BuildId(MoralityAction.RemoveLastDerangement, characterId),
                    ButtonStyle.Secondary)
                .Build();
        }

        /// <summary>CustomId кнопки «Мораль».</summary>
        public static string BuildId(MoralityAction action, Guid characterId)
            => $"{Prefix}:{ActionToString(action)}:{characterId:N}";

        /// <summary>CustomId SelectMenu выбора расстройства.</summary>
        public static string BuildSelectId(Guid characterId)
            => $"{Prefix}:sel:{characterId:N}";

        /// <summary>Это наш customId (кнопка или меню).</summary>
        public static bool IsOurs(string customId)
            => !string.IsNullOrEmpty(customId) && customId.StartsWith(Prefix + ":");

        /// <summary>Распарсить customId кнопки. Меню (sel) — false.</summary>
        public static bool TryParse(string customId, out MoralityAction action, out Guid characterId)
        {
            action = default;
            characterId = Guid.Empty;
            if (!IsOurs(customId)) return false;
            var parts = customId.Split(':');
            if (parts.Length != 3) return false;
            if (parts[1] == "sel") return false; // SelectMenu обрабатывается отдельно через TryParseSelectedMenu
            if (!TryParseAction(parts[1], out action)) return false;
            if (!Guid.TryParseExact(parts[2], "N", out characterId)) return false;
            return characterId != Guid.Empty;
        }

        /// <summary>Распарсить customId SelectMenu. Возвращает null если не наш.</summary>
        public static Guid? TryParseSelectedMenu(string customId)
        {
            if (!IsOurs(customId)) return null;
            var parts = customId.Split(':');
            if (parts.Length != 3 || parts[1] != "sel") return null;
            return Guid.TryParseExact(parts[2], "N", out var id) && id != Guid.Empty ? id : null;
        }

        private static string ActionToString(MoralityAction action) => action switch
        {
            MoralityAction.StartAddDerangement   => "add",
            MoralityAction.RemoveLastDerangement => "remove",
            MoralityAction.ConscienceCheck       => "conscience",
            _ => throw new InvalidEnumArgumentException(nameof(action), (int)action, typeof(MoralityAction)),
        };

        private static bool TryParseAction(string s, out MoralityAction action)
        {
            switch (s)
            {
                case "add":       action = MoralityAction.StartAddDerangement;   return true;
                case "remove":    action = MoralityAction.RemoveLastDerangement; return true;
                case "conscience":action = MoralityAction.ConscienceCheck;       return true;
                default:          action = default;                              return false;
            }
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s[..(max - 1)] + "…";
    }
}
