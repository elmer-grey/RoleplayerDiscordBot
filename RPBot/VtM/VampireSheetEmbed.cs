using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Discord;

namespace RPBot.VtM;

/// <summary>
/// Чистый конструктор embed'а листа персонажа по V20 Anniversary.
/// </summary>
/// <remarks>
/// <para>Структура листа (как на официальной странице персонажа V20):</para>
/// <list type="bullet">
/// <item>Заголовок: имя персонажа + клан/поколение (из Bio).</item>
/// <item>Характеристики (Сила, Ловкость, Выносливость / Обаяние, Манипуляция, Привлекательность /
/// Восприятие, Интеллект, Смекалка) — 9 точек в три колонки, по 5 точек на атрибут (+1 «изначальная»
/// для характеристик, итого максимум 5 видимых).</item>
/// <item>Атрибуты: 10 талантов, 10 навыков, 10 знаний — каждый до 5 точек.</item>
/// <item>Добродетели: Совесть/Самоконтроль/Смелость (1..5).</item>
/// <item>Человечность (Humanity) — отдельный блок, default 7.</item>
/// <item>Воля и Голод — отдельный блок, до 10 круглых точек.</item>
/// <item>Шкала здоровья — буфер «S / / X A» в строку.</item>
/// <item>Опыт (Exp) — строка вида «Текущий / Всего».</item>
/// <item>Фон + Дисциплины — текстовые списки.</item>
/// </list>
/// <para>Источник истины для имён параметров — <see cref="VampireParameterCatalog"/>.</para>
/// </remarks>
public static class VampireSheetEmbed
{
    private const string DotFilled = "●";
    private const string DotEmpty = "○";

    /// <summary>Цвет рамки embed'а по умолчанию (тёмно-красный, в тон VtM).</summary>
    public static readonly Color DefaultColor = new Color(0x8B0000);

    /// <summary>
    /// Собрать embed листа персонажа.
    /// </summary>
    /// <param name="character">Персонаж. Не должен быть <c>null</c>.</param>
    /// <returns>Готовый Discord <see cref="Embed"/>.</returns>
    public static Embed Build(VampireCharacter character)
    {
        if (character == null) throw new ArgumentNullException(nameof(character));

        var eb = new EmbedBuilder
        {
            Title = string.IsNullOrWhiteSpace(character.CharacterName)
                ? "Безымянный вампир"
                : character.CharacterName,
            Color = DefaultColor
        };

        if (!string.IsNullOrWhiteSpace(character.Bio))
            eb.Description = Truncate(character.Bio, EmbedBuilder.MaxDescriptionLength);

        if (!string.IsNullOrWhiteSpace(character.AvatarUrl) &&
            Uri.TryCreate(character.AvatarUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            eb.ThumbnailUrl = character.AvatarUrl;
        }

        eb.Author = new EmbedAuthorBuilder
        {
            Name = string.IsNullOrEmpty(character.PlayerName)
                ? "Игрок не указан"
                : character.PlayerName
        };

        eb.AddField(new EmbedFieldBuilder
        {
            Name = "════════ Характеристики ════════",
            Value = BuildCharacteristicsBlock(character),
            IsInline = false
        });

        eb.AddField(new EmbedFieldBuilder
        {
            Name = "════════ Таланты ════════",
            Value = BuildAbilityBlock(character, VampireParameterCatalog.Talents),
            IsInline = true
        });
        eb.AddField(new EmbedFieldBuilder
        {
            Name = "════════ Навыки ════════",
            Value = BuildAbilityBlock(character, VampireParameterCatalog.Skills),
            IsInline = true
        });
        eb.AddField(new EmbedFieldBuilder
        {
            Name = "════════ Знания ════════",
            Value = BuildAbilityBlock(character, VampireParameterCatalog.Knowledges),
            IsInline = true
        });

        eb.AddField(new EmbedFieldBuilder
        {
            Name = "════════ Добродетели ════════",
            Value = BuildVirtuesBlock(character),
            IsInline = true
        });
        eb.AddField(new EmbedFieldBuilder
        {
            Name = "════════ Прочее ════════",
            Value = BuildMiscBlock(character),
            IsInline = true
        });

        if (character.Health != null)
        {
            eb.AddField(new EmbedFieldBuilder
            {
                Name = "════════ Здоровье ════════",
                Value = BuildHealthBlock(character.Health),
                IsInline = false
            });
        }

        if (character.Disciplines != null && character.Disciplines.Count > 0)
        {
            eb.AddField(new EmbedFieldBuilder
            {
                Name = "════════ Дисциплины ════════",
                Value = BuildDisciplinesBlock(character.Disciplines),
                IsInline = false
            });
        }

        if (character.Backgrounds != null && character.Backgrounds.Count > 0)
        {
            eb.AddField(new EmbedFieldBuilder
            {
                Name = "════════ Фон ════════",
                Value = string.Join("\n", character.Backgrounds.Select(b => $"• {b}")),
                IsInline = false
            });
        }

        eb.Footer = new EmbedFooterBuilder
        {
            Text = "Лист персонажа по VtM V20 Anniversary · /vampire_show"
        };

        return eb.Build();
    }

    // ─── Блоки ────────────────────────────────────────────────────────────

    private static string BuildCharacteristicsBlock(VampireCharacter c)
    {
        var sb = new StringBuilder();
        AppendCharacteristicRow(sb, "Физические", VampireParameterCatalog.Physical, c);
        sb.AppendLine();
        AppendCharacteristicRow(sb, "Социальные", VampireParameterCatalog.Social, c);
        sb.AppendLine();
        AppendCharacteristicRow(sb, "Ментальные", VampireParameterCatalog.Mental, c);
        return sb.ToString();
    }

    private static void AppendCharacteristicRow(StringBuilder sb, string title, IReadOnlyList<string> names, VampireCharacter c)
    {
        sb.Append("**").Append(title).AppendLine(":**");
        foreach (var name in names)
        {
            int value = c.Attributes.TryGetValue(name, out var v) ? v : 0;
            sb.Append("`").Append(PadRight(name, 16)).Append("` ");
            sb.AppendLine(DotsString(c.DisplayDots(name, isCharacteristic: true), 5));
        }
    }

    private static string BuildAbilityBlock(VampireCharacter c, IReadOnlyList<string> names)
    {
        var sb = new StringBuilder();
        foreach (var name in names)
        {
            int value = c.Attributes.TryGetValue(name, out var v) ? v : 0;
            sb.Append("`").Append(PadRight(name, 22)).Append("` ");
            sb.AppendLine(DotsString(Math.Min(value, 5), 5));
        }
        return sb.ToString();
    }

    private static string BuildVirtuesBlock(VampireCharacter c)
    {
        var sb = new StringBuilder();
        foreach (var name in VampireParameterCatalog.Virtues)
        {
            int value = c.Virtues.TryGetValue(name, out var v) ? v : 0;
            sb.Append("`").Append(PadRight(name, 12)).Append("` ");
            sb.AppendLine(DotsString(Math.Min(value, 5), 5));
        }
        return sb.ToString();
    }

    private static string BuildMiscBlock(VampireCharacter c)
    {
        var sb = new StringBuilder();
        sb.Append("`").Append(PadRight("Человечность", 14)).Append("` ");
        sb.AppendLine(DotsString(Math.Clamp(c.Humanity, 0, 10), 10));
        sb.Append("`").Append(PadRight("Воля", 14)).Append("` ");
        sb.AppendLine(DotsString(c.WillpowerPoints, Math.Max(c.Willpower, c.WillpowerPoints)));
        sb.Append("`").Append(PadRight("Голод", 14)).Append("` ");
        sb.AppendLine(DotsString(Math.Clamp(c.Hunger, 0, 5), 5));
        sb.Append("`").Append(PadRight("Опыт", 14)).Append("` ");
        sb.Append(c.ExperienceCurrent).Append(" / ").Append(c.ExperienceTotal);
        return sb.ToString();
    }

    private static string BuildHealthBlock(HealthState h)
    {
        var sb = new StringBuilder();
        sb.Append("Шкала: ").AppendLine(h.Render());
        sb.Append("Штраф: **-").Append(h.Penalty).Append("**");
        if (h.IsTorpor) sb.Append("  ·  *Торпор*");
        if (h.IsDestroyed) sb.Append("  ·  *Уничтожен*");
        if (h.IsDead) sb.Append("  ·  *Финальная смерть*");
        return sb.ToString();
    }

    private static string BuildDisciplinesBlock(IReadOnlyDictionary<string, int> disciplines)
    {
        var sb = new StringBuilder();
        foreach (var kv in disciplines)
        {
            sb.Append("`").Append(PadRight(kv.Key, 18)).Append("` ");
            sb.AppendLine(DotsString(Math.Clamp(kv.Value, 0, 5), 5));
        }
        return sb.ToString();
    }

    // ─── Утилиты ──────────────────────────────────────────────────────────

    /// <summary>Строка из <paramref name="filled"/> закрашенных и пустых точек суммарной длины <paramref name="total"/>.</summary>
    public static string DotsString(int filled, int total)
    {
        if (total < 0) total = 0;
        if (filled < 0) filled = 0;
        if (filled > total) filled = total;
        return string.Concat(Enumerable.Repeat(DotFilled, filled)) +
               string.Concat(Enumerable.Repeat(DotEmpty, total - filled));
    }

    private static string PadRight(string s, int width)
    {
        if (s == null) s = "";
        // Учитываем, что имена — кириллица; PadRight считает по .NET-символам, не по ширине,
        // но для фиксированной таблицы это приемлемо.
        return s.Length >= width ? s : s + new string(' ', width - s.Length);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s.Substring(0, Math.Max(0, max - 1)) + "…";
    }
}
