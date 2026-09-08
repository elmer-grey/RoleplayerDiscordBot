using System;
using System.ComponentModel;
using Discord;

namespace RPBot.VtM
{
    /// <summary>
    /// Действие под блоком «Здоровье».
    /// </summary>
    public enum HealthAction
    {
        /// <summary>Нанести нелетальный урон (<c>/</c>).</summary>
        ApplyNonLethal,
        /// <summary>Нанести летальный урон (<c>X</c>).</summary>
        ApplyLethal,
        /// <summary>Нанести агравированный урон (<c>A</c>).</summary>
        ApplyAggravated,
        /// <summary>Лечение на 1 (снимает правый нелетальный <c>/</c> или пустую ячейку).</summary>
        HealOne,
    }

    /// <summary>
    /// Кнопки для блока «Здоровье» и парсер customId.
    /// </summary>
    /// <remarks>
    /// <para>CustomId: <c>vtm_health:&lt;action&gt;:&lt;characterId&gt;</c>.</para>
    /// <para>В Discord есть лимит 5 кнопок на ActionRow — все 5 кнопок укладываются.</para>
    /// </remarks>
    public static class VampireHealthComponents
    {
        public const string Prefix = "vtm_health";

        public static MessageComponent Build(Guid characterId)
        {
            if (characterId == Guid.Empty)
                throw new ArgumentException("CharacterId обязателен", nameof(characterId));

            return new ComponentBuilder()
                .WithButton("Нелетальный урон", BuildId(HealthAction.ApplyNonLethal, characterId), ButtonStyle.Primary)
                .WithButton("Летальный урон",   BuildId(HealthAction.ApplyLethal,     characterId), ButtonStyle.Danger)
                .WithButton("Агравированный",   BuildId(HealthAction.ApplyAggravated, characterId), ButtonStyle.Danger)
                .WithButton("Лечение 1",        BuildId(HealthAction.HealOne,        characterId), ButtonStyle.Success)
                .Build();
        }

        /// <summary>Собрать customId.</summary>
        public static string BuildId(HealthAction action, Guid characterId)
            => $"{Prefix}:{ActionToString(action)}:{characterId:N}";

        /// <summary>Распарсить customId.</summary>
        public static bool TryParse(string customId, out HealthAction action, out Guid characterId)
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

        /// <summary>Это customId нашей кнопки здоровья.</summary>
        public static bool IsOurButton(string customId)
            => !string.IsNullOrEmpty(customId) && customId.StartsWith(Prefix + ":");

        private static string ActionToString(HealthAction action) => action switch
        {
            HealthAction.ApplyNonLethal => "nonlethal",
            HealthAction.ApplyLethal    => "lethal",
            HealthAction.ApplyAggravated=> "aggravated",
            HealthAction.HealOne        => "heal",
            _ => throw new InvalidEnumArgumentException(nameof(action), (int)action, typeof(HealthAction)),
        };

        private static bool TryParseAction(string s, out HealthAction action)
        {
            switch (s)
            {
                case "nonlethal": action = HealthAction.ApplyNonLethal; return true;
                case "lethal":    action = HealthAction.ApplyLethal;    return true;
                case "aggravated":action = HealthAction.ApplyAggravated;return true;
                case "heal":      action = HealthAction.HealOne;        return true;
                default:          action = default;                    return false;
            }
        }
    }
}
