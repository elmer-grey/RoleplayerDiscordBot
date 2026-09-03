using System;
using Discord;

namespace RPBot.VtM
{
    /// <summary>
    /// Embed шкалы здоровья, отображаемый в DM под листом по кнопке «Здоровье».
    /// </summary>
    /// <remarks>
    /// <para>Содержит: имя персонажа, шкалу (S / / / X / A), штраф по таблице V20 (стр. 92),
    /// флаг «Небоеспособен» и кнопки управления.</para>
    /// <para>Если <see cref="VampireCharacter.Health"/> = null — возвращает встроенный
    /// <c>null</c>. Хэндлер должен показать «Шкала здоровья не задана».</para>
    /// </remarks>
    public static class VampireHealthEmbed
    {
        /// <summary>Длина строки рендера шкалы.</summary>
        public const int MaxTrackLength = 64;

        /// <summary>
        /// Собрать embed шкалы здоровья вместе с кнопками блока.
        /// </summary>
        public static Embed Build(VampireCharacter c)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));

            var health = c.Health;
            var eb = new EmbedBuilder
            {
                Title = string.IsNullOrWhiteSpace(c.CharacterName)
                    ? "Безымянный вампир"
                    : c.CharacterName,
                Color = VampireSheetEmbed.DefaultColor,
                Description = "Шкала здоровья (V20 стр. 92)",
            };

            string trackText;
            int? penalty;
            bool isIncapacitated;

            if (health == null || health.Size == 0)
            {
                trackText = "_шкала не задана_";
                penalty = null;
                isIncapacitated = false;
            }
            else
            {
                trackText = $"`{health.Render()}` (размер: {health.Size})";
                penalty = health.TablePenalty;
                isIncapacitated = health.IsIncapacitated;
            }

            eb.AddField(new EmbedFieldBuilder
            {
                Name = "Шкала",
                Value = trackText,
                IsInline = false,
            });

            if (penalty.HasValue && (penalty.Value != 0 || isIncapacitated))
            {
                var penaltyText = isIncapacitated
                    ? "**Небоеспособен** (штраф: −0, действия ограничены рассказчиком)"
                    : $"**Штраф:** `{penalty.Value:+0;-0;0}`";
                eb.AddField(new EmbedFieldBuilder
                {
                    Name = "Состояние",
                    Value = penaltyText,
                    IsInline = false,
                });
            }

            eb.Footer = new EmbedFooterBuilder
            {
                Text = "VtM V20 · блок «Здоровье»",
            };

            return eb.Build();
        }

        /// <summary>
        /// Кнопки управления шкалой под блоком. Действия:
        /// нелетальный урон, летальный урон, агравированный урон,
        /// лечение на 1, закрытие блока.
        /// </summary>
        public static MessageComponent Components(VampireCharacter c)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            if (c.CharacterId == Guid.Empty)
                throw new ArgumentException("CharacterId обязателен", nameof(c));

            return VampireHealthComponents.Build(c.CharacterId);
        }
    }
}
