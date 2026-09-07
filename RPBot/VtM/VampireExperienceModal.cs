using System;
using Discord;

namespace RPBot.VtM
{
    /// <summary>
    /// Действие внутри модалки опыта VtM.
    /// </summary>
    public enum ExperienceModalAction
    {
        /// <summary>Начислить опыт.</summary>
        Grant,
        /// <summary>Потратить опыт.</summary>
        Spend,
    }

    /// <summary>
    /// Кнопка «Опыт» + модалки начисления/траты опыта для листа VtM V20 (Roadmap #34).
    /// </summary>
    /// <remarks>
    /// <para>По V20 стр. 141 опыт начисляется рассказчиком за сюжетные арки и тратится
    /// по фиксированной таблице (новая способность/дисциплина — 3, повышение
    /// характеристики — 4, добродетели — 2, человечность — 2, и т.д.).</para>
    ///
    /// <para>В этой версии (UI-каркас) кнопка открывает модалку с единственным полем
    /// «Количество». Применение дельты к персонажу — обязанность обработчика:
    /// <list type="bullet">
    /// <item>Grant: <c>ExperienceCurrent += amount</c>, плюс <c>ExperienceTotal += amount</c>.</item>
    /// <item>Spend: <c>ExperienceCurrent -= amount</c> (без ухода в минус).</item>
    /// </list></para>
    /// </remarks>
    public static class VampireExperienceModal
    {
        public const string Prefix = "vtm_xp_modal";

        /// <summary>Имя текстового поля с количеством опыта в модалке.</summary>
        public const string AmountFieldId = "amount";

        /// <summary>CustomId модалки начисления/траты опыта.</summary>
        public static string BuildCustomId(ExperienceModalAction action, Guid characterId)
        {
            if (characterId == Guid.Empty)
                throw new ArgumentException("CharacterId обязателен", nameof(characterId));
            return $"{Prefix}:{ActionToString(action)}:{characterId:N}";
        }

        /// <summary>Построить модалку для указанного действия.</summary>
        public static Modal Build(ExperienceModalAction action, Guid characterId)
        {
            var title = action switch
            {
                ExperienceModalAction.Grant => "Начислить опыт",
                ExperienceModalAction.Spend => "Потратить опыт",
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
            return new ModalBuilder()
                .WithTitle(title)
                .WithCustomId(BuildCustomId(action, characterId))
                .AddTextInput(
                    label: "Количество опыта",
                    customId: AmountFieldId,
                    style: TextInputStyle.Short,
                    placeholder: "Целое число > 0",
                    minLength: 1,
                    maxLength: 6,
                    required: true)
                .Build();
        }

        /// <summary>Это наш customId модалки опыта.</summary>
        public static bool IsOurs(string customId)
            => !string.IsNullOrEmpty(customId) && customId.StartsWith(Prefix + ":");

        /// <summary>Распарсить customId модалки опыта.</summary>
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

        /// <summary>
        /// Распарсить и провалидировать введённое количество.
        /// Возвращает false и amount = 0 при любой невалидной строке.
        /// </summary>
        /// <remarks>
        /// Допускает только положительные целые числа. Лимит — <c>int.MaxValue</c>.
        /// </remarks>
        public static bool TryParseAmount(string? raw, out int amount)
        {
            amount = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var trimmed = raw.Trim();
            if (!int.TryParse(trimmed, out var parsed)) return false;
            if (parsed <= 0) return false;
            amount = parsed;
            return true;
        }

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
