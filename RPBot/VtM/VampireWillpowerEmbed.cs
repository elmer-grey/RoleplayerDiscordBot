using System;
using Discord;

namespace RPBot.VtM
{
    /// <summary>
    /// Embed атрибута «Воля», отображаемый в DM под листом по кнопке «Воля».
    /// </summary>
    /// <remarks>
    /// <para>Показывает потолок (атрибут Воля) и текущий запас пунктов
    /// визуально закрашенными/пустыми точками.</para>
    /// <para>Если на этом ходу игрок уже тратил пункт воли через «Сопротивление»,
    /// бот показывает флаг <see cref="VampireCharacter.WillpowerSpentThisTurn"/>.</para>
    /// </remarks>
    public static class VampireWillpowerEmbed
    {
        /// <summary>
        /// Собрать embed блока «Воля».
        /// </summary>
#pragma warning disable CS0618 // Willpower/WillpowerPoints устарели для листа, но используются runtime-кнопкой «Воля» в DM.
        public static Embed Build(VampireCharacter c)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            c.EnsureWillpowerPointsValid();

            var eb = new EmbedBuilder
            {
                Title = string.IsNullOrWhiteSpace(c.CharacterName)
                    ? "Безымянный вампир"
                    : c.CharacterName,
                Color = VampireSheetEmbed.DefaultColor,
                Description = "Воля (V20 стр. 116)",
            };

            int ceiling = Math.Clamp(c.Willpower, 0, 10);
            int current = Math.Clamp(c.WillpowerPoints, 0, ceiling);
            int spent = ceiling - current;

            eb.AddField(new EmbedFieldBuilder
            {
                Name = "Потолок",
                Value = $"`{c.Willpower}`",
                IsInline = true,
            });

            eb.AddField(new EmbedFieldBuilder
            {
                Name = "Запас",
                Value = $"`{current}` / `{ceiling}` — {RenderDots(current, ceiling - current)}",
                IsInline = true,
            });

            if (spent > 0)
            {
                eb.AddField(new EmbedFieldBuilder
                {
                    Name = "Потрачено в этом ходу",
                    Value = c.WillpowerSpentThisTurn
                        ? $"**{spent}** (уже использовался «Сопротивление»)"
                        : $"**{spent}**",
                    IsInline = true,
                });
            }

            eb.Footer = new EmbedFooterBuilder
            {
                Text = "VtM V20 · блок «Воля»",
            };

            return eb.Build();
        }

        /// <summary>Кнопки под блоком «Воля».</summary>
        public static MessageComponent Components(VampireCharacter c)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            if (c.CharacterId == Guid.Empty)
                throw new ArgumentException("CharacterId обязателен", nameof(c));

            return VampireWillpowerComponents.Build(c.CharacterId);
        }

        private static string RenderDots(int filled, int empty)
        {
            if (filled + empty == 0) return "—";
            return new string('●', filled) + new string('○', empty);
        }
#pragma warning restore CS0618
    }
}
