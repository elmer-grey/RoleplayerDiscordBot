using System;
using System.Text;
using Discord;

namespace RPBot.VtM;

/// <summary>
/// Отдельный embed «Описание» — Bio + аватар, показывается по кнопке под
/// основным листом персонажа (<see cref="VampireSheetEmbed"/>).
/// </summary>
/// <remarks>
/// <para>Используется, когда пользователь хочет подробное описание внешности
/// и предыстории персонажа. В основном embed'е Bio не выводится, чтобы
/// оставить место под характеристики / способности / дисциплины.</para>
/// <para>Если Bio пустое и аватара нет — возвращается <c>null</c>, и
/// бот может сообщить «описание недоступно».</para>
/// </remarks>
public static class VampireDescriptionEmbed
{
    /// <summary>Максимальная длина Bio (≤ Discord EmbedBuilder.MaxDescriptionLength).</summary>
    public const int MaxBioLength = 4000;

    /// <summary>
    /// Собрать embed описания персонажа.
    /// </summary>
    /// <param name="c">Персонаж.</param>
    /// <returns>Готовый embed или <c>null</c>, если нет ни Bio, ни аватара.</returns>
    public static Embed? Build(VampireCharacter c)
    {
        if (c == null) throw new ArgumentNullException(nameof(c));

        bool hasBio = !string.IsNullOrWhiteSpace(c.Bio);
        bool hasAvatar = !string.IsNullOrWhiteSpace(c.AvatarUrl)
                         && Uri.TryCreate(c.AvatarUrl, UriKind.Absolute, out var uri)
                         && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        if (!hasBio && !hasAvatar) return null;

        var eb = new EmbedBuilder
        {
            Title = string.IsNullOrWhiteSpace(c.CharacterName)
                ? "Безымянный вампир"
                : c.CharacterName,
            Color = VampireSheetEmbed.DefaultColor,
        };

        if (hasAvatar)
            eb.ThumbnailUrl = c.AvatarUrl;

        if (hasBio)
            eb.Description = Truncate(c.Bio, MaxBioLength);

        // Краткая шапка: игрок + клан/поколение.
        var headerParts = new StringBuilder();
        if (!string.IsNullOrEmpty(c.PlayerName))
            headerParts.Append("**Игрок:** ").Append(c.PlayerName).Append('\n');
        if (!string.IsNullOrWhiteSpace(c.Clan))
        {
            headerParts.Append("**Клан:** ").Append(c.Clan);
            if (c.Generation > 0 && c.Generation != 13)
                headerParts.Append(" (").Append(c.Generation).Append("-е поколение)");
            headerParts.Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(c.Nature))
            headerParts.Append("**Натура:** ").Append(c.Nature);
        if (headerParts.Length > 0)
        {
            eb.AddField(new EmbedFieldBuilder
            {
                Name = "════════ Идентификация ════════",
                Value = headerParts.ToString().TrimEnd(),
                IsInline = false,
            });
        }

        eb.Footer = new EmbedFooterBuilder
        {
            Text = "Описание по VtM V20 Anniversary · /vampire_show",
        };

        return eb.Build();
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s.Substring(0, Math.Max(0, max - 1)) + "…";
    }
}
