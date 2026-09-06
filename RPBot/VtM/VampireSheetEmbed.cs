using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Discord;

namespace RPBot.VtM;

/// <summary>
/// Чистый конструктор embed'а листа персонажа по VtM V20 Anniversary, стр. 92.
/// </summary>
/// <remarks>
/// <para>Структура листа (как на официальной странице персонажа V20):</para>
/// <list type="bullet">
/// <item>Заголовок: имя персонажа + аватар thumbnail; в footer — V20 стр. 92.</item>
/// <item>Description: краткое Bio.</item>
/// <item>Шапка V20: Хроника / Натура / Маска / Амплуа / Клан / Поколение / Сир.</item>
/// <item>Характеристики (9 шт) — один центрированный блок, три колонки inline
/// (Физические / Социальные / Ментальные).</item>
/// <item>Способности (30 шт) — один центрированный блок, три колонки inline
/// (Таланты / Навыки / Знания). При value ≥ 4 показывается специализация в скобках.</item>
/// <item>Преимущества — один центрированный блок, три колонки inline
/// (Дисциплины / Факты биографии / Добродетели с 5 ячейками).</item>
/// <item>Нижний ряд, 3 колонки inline:
/// <list type="number">
/// <item>Достоинства и Недостатки (Merits + Flaws с ценами).</item>
/// <item>Человечность / Воля / Голод (+ Кровь).</item>
/// <item>Здоровье (V20 стр. 92, штраф по таблице) / Изъян / Опыт.</item>
/// </list>
/// </item>
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
            Color = DefaultColor,
        };

        // Bio НЕ выводится в основном листе — место нужно под важные данные.
        // Полное описание с фото показывается отдельным embed'ом по кнопке «Описание»:
        // см. <see cref="VampireDescriptionEmbed"/>.

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

        var headerLines = BuildHeaderLines(character);
        if (headerLines.Count > 0)
        {
            eb.AddField(new EmbedFieldBuilder
            {
                Name = "════════ Идентификация ════════",
                Value = string.Join("\n", headerLines),
                IsInline = false,
            });
        }

        // Характеристики — три inline-колонки (заголовок + два пустых для колоночной вёрстки).
        eb.AddField(new EmbedFieldBuilder
        {
            Name = "──────── Характеристики ────────",
            Value = BuildCharacteristicsBlock(character),
            IsInline = true,
        });
        eb.AddField(new EmbedFieldBuilder { Name = "\u200B", Value = "\u200B", IsInline = true });
        eb.AddField(new EmbedFieldBuilder { Name = "\u200B", Value = "\u200B", IsInline = true });

        // Способности: три колонки (Таланты / Навыки / Знания) со специализациями при value ≥ 4.
        eb.AddField(new EmbedFieldBuilder
        {
            Name = "──────── Способности ────────",
            Value = BuildAbilityColumn(character, VampireParameterCatalog.Talents),
            IsInline = true,
        });
        eb.AddField(new EmbedFieldBuilder
        {
            Name = "\u200B",
            Value = BuildAbilityColumn(character, VampireParameterCatalog.Skills),
            IsInline = true,
        });
        eb.AddField(new EmbedFieldBuilder
        {
            Name = "\u200B",
            Value = BuildAbilityColumn(character, VampireParameterCatalog.Knowledges),
            IsInline = true,
        });

        // Преимущества: три колонки (Дисциплины / Факты биографии / Добродетели).
        eb.AddField(new EmbedFieldBuilder
        {
            Name = "──────── Преимущества ────────",
            Value = BuildAdvantagesColumn(character),
            IsInline = true,
        });
        eb.AddField(new EmbedFieldBuilder
        {
            Name = "\u200B",
            Value = BuildBackgroundsColumn(character),
            IsInline = true,
        });
        eb.AddField(new EmbedFieldBuilder
        {
            Name = "\u200B",
            Value = BuildVirtuesColumn(character),
            IsInline = true,
        });

        // Нижний ряд: Достоинства-Недостатки / Суть / Здоровье-Изъян-Опыт.
        eb.AddField(new EmbedFieldBuilder
        {
            Name = "──────── Достоинства и недостатки ────────",
            Value = BuildMeritsFlawsBlock(character),
            IsInline = true,
        });
        eb.AddField(new EmbedFieldBuilder
        {
            Name = "──────── Суть ────────",
            Value = BuildEssenceBlock(character),
            IsInline = true,
        });
        eb.AddField(new EmbedFieldBuilder
        {
            Name = "──────── Здоровье и опыт ────────",
            Value = BuildHealthExperienceBlock(character),
            IsInline = true,
        });

        eb.Footer = new EmbedFooterBuilder
        {
            Text = "Лист персонажа по VtM V20 Anniversary, стр. 92 · /vampire_show",
        };

        return eb.Build();
    }

    // ─── Блоки ────────────────────────────────────────────────────────────

    private static List<string> BuildHeaderLines(VampireCharacter c)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(c.Chronicle))
            lines.Add($"**Хроника:** {c.Chronicle}");
        if (!string.IsNullOrWhiteSpace(c.Nature))
            lines.Add($"**Натура:** {c.Nature}");
        if (!string.IsNullOrWhiteSpace(c.Demeanor))
            lines.Add($"**Маска:** {c.Demeanor}");
        if (!string.IsNullOrWhiteSpace(c.Concept))
            lines.Add($"**Амплуа:** {c.Concept}");
        // Клан/поколение выводим, только если задан клан (поколение — опциональный атрибут клана).
        if (!string.IsNullOrWhiteSpace(c.Clan))
        {
            var clanLine = new StringBuilder("**Клан:** ").Append(c.Clan);
            if (c.Generation > 0 && c.Generation != 13) // 13 = default новообращённого, не показываем без клана
                clanLine.Append($" ({c.Generation}-е поколение)");
            lines.Add(clanLine.ToString());
        }
        else if (c.Generation > 0 && c.Generation != 13)
        {
            lines.Add($"**Поколение:** {c.Generation}-е");
        }
        if (!string.IsNullOrWhiteSpace(c.Sire))
            lines.Add($"**Сир:** {c.Sire}");
        return lines;
    }

    /// <summary>Сводный блок «Характеристики» — 9 строк.</summary>
    public static string BuildCharacteristicsBlock(VampireCharacter c)
    {
        var sb = new StringBuilder();
        AppendCategorySection(sb, "Физические", VampireParameterCatalog.Physical, c, isCharacteristic: true);
        AppendCategorySection(sb, "Социальные", VampireParameterCatalog.Social, c, isCharacteristic: true);
        AppendCategorySection(sb, "Ментальные", VampireParameterCatalog.Mental, c, isCharacteristic: true);
        return sb.ToString();
    }

    private static void AppendCategorySection(StringBuilder sb, string title, IReadOnlyList<string> names, VampireCharacter c, bool isCharacteristic)
    {
        sb.Append("**").Append(title).AppendLine(":**");
        foreach (var name in names)
        {
            int value = c.GetAttributeValue(name);
            sb.Append("`").Append(PadRight(name, 13)).Append("` ");
            // Привлекательность у Носферату/Самеди зачёркнута изъяном клана (всегда 0).
            if (name == "Привлекательность"
                && (c.Clan == "Носферату" || c.Clan == "Последователь Сета"))
            {
                    sb.Append("̶○̶○̶○̶○̶○̶ (зачёркнуто изъяном)");
            }
            else
            {
                    int dots = Math.Min(value, 5);
                    string dotsStr = DotsString(dots, 5);
                    if (dots >= 4
                        && c.Specializations != null
                        && c.Specializations.TryGetValue(name, out var spec)
                        && !string.IsNullOrWhiteSpace(spec))
                    {
                        sb.Append(dotsStr).Append(" (").Append(spec).Append(')');
                    }
                    else
                    {
                        sb.Append(dotsStr);
                    }
                }
                sb.AppendLine();
            }
        }

    /// <summary>Столбец способностей (Таланты / Навыки / Знания) со специализациями при value ≥ 4.</summary>
    public static string BuildAbilityColumn(VampireCharacter c, IReadOnlyList<string> names)
    {
        var sb = new StringBuilder();
        string groupName = GroupNameForAbilities(names);
        if (!string.IsNullOrEmpty(groupName))
            sb.Append("**").Append(groupName).AppendLine(":**");
        foreach (var name in names)
        {
            int value = c.GetAbilityValue(name);
            sb.Append("`").Append(PadRight(name, 13)).Append("` ");
            int dots = Math.Min(value, 5);
            string dotsStr = DotsString(dots, 5);
            if (dots >= 4
                && c.Specializations != null
                && c.Specializations.TryGetValue(name, out var spec)
                && !string.IsNullOrWhiteSpace(spec))
            {
                sb.Append(dotsStr).Append(" (").Append(spec).Append(')');
            }
            else
            {
                sb.Append(dotsStr);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Колонка «Дисциплины» (до 6 строк).</summary>
    public static string BuildAdvantagesColumn(VampireCharacter c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("**Дисциплины:**");
        // Собираем объединённое множество имён: Шаг 4.1 + freebie.
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (c.Disciplines != null) foreach (var k in c.Disciplines.Keys) names.Add(k);
        if (c.FreebieDisciplines != null) foreach (var k in c.FreebieDisciplines.Keys) names.Add(k);
        if (names.Count > 0)
        {
            int shown = 0;
            foreach (var name in names)
            {
                if (shown >= 6) break;
                int value = VampireAdvantagesResolver.GetDisciplineValue(c, name);
                sb.Append("`").Append(PadRight(name, 13)).Append("` ");
                sb.AppendLine(DotsString(Math.Clamp(value, 0, 5), 5));
                shown++;
            }
        }
        else
        {
            sb.AppendLine("`—`");
        }
        return sb.ToString();
    }

    /// <summary>Колонка «Факты биографии» (Backgrounds) — до 6 строк.</summary>
    public static string BuildBackgroundsColumn(VampireCharacter c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("**Факты биографии:**");
        // Собираем объединённое множество имён: Шаг 4.2 + freebie.
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (c.Backgrounds != null) foreach (var k in c.Backgrounds.Keys) names.Add(k);
        if (c.FreebieBackgrounds != null) foreach (var k in c.FreebieBackgrounds.Keys) names.Add(k);
        if (names.Count > 0)
        {
            int shown = 0;
            foreach (var name in names)
            {
                if (shown >= 6) break;
                int rank = Math.Clamp(VampireAdvantagesResolver.GetBackgroundRank(c, name), 1, 5);
                sb.Append("• ").Append(name).Append(' ').Append(DotsString(rank, 5)).AppendLine();
                shown++;
            }
        }
        else
        {
            sb.AppendLine("`—`");
        }
        return sb.ToString();
    }

    /// <summary>Колонка «Добродетели»: Совесть/Решимость, Самоконтроль/Инстинкты, Смелость — по 5 ячеек.</summary>
    public static string BuildVirtuesColumn(VampireCharacter c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("**Добродетели:**");
        foreach (var slot in VirtueSlots)
        {
            int value = 0;
            if (c.Virtues != null)
            {
                if (c.Virtues.TryGetValue(slot.Primary, out var p)) value = Math.Max(value, p);
                if (!string.IsNullOrEmpty(slot.Alt) && c.Virtues.TryGetValue(slot.Alt, out var a)) value = Math.Max(value, a);
            }
            string label = string.IsNullOrEmpty(slot.Alt) ? slot.Primary : $"{slot.Primary}/{slot.Alt}";
            sb.Append("`").Append(PadRight(label, 18)).Append("` ");
            sb.AppendLine(DotsString(Math.Clamp(value, 0, 5), 5));
        }
        return sb.ToString();
    }

    private static readonly IReadOnlyList<(string Primary, string Alt)> VirtueSlots = new[]
    {
        ("Совесть", "Решимость"),
        ("Самоконтроль", "Инстинкты"),
        ("Смелость", ""),
    };

    /// <summary>Колонка «Достоинства и недостатки» — Merits + Flaws с ценами.</summary>
    public static string BuildMeritsFlawsBlock(VampireCharacter c)
    {
        var sb = new StringBuilder();
        bool hasMerits = c.Merits != null && c.Merits.Count > 0;
        bool hasFlaws = c.Flaws != null && c.Flaws.Count > 0;
        if (!hasMerits && !hasFlaws)
        {
            sb.AppendLine("`—`");
            return sb.ToString();
        }
        if (hasMerits)
        {
            sb.AppendLine("**Достоинства**");
            foreach (var kv in c.Merits!)
                sb.Append("• ").Append(kv.Key).Append(" — ").Append(kv.Value).AppendLine();
        }
        if (hasFlaws)
        {
            sb.AppendLine("**Недостатки**");
            foreach (var kv in c.Flaws!)
                sb.Append("• ").Append(kv.Key).Append(" — ").Append(kv.Value).AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Колонка «Суть»: Человечность / Воля / Голод. Кровь не показывается — в V20 её роль играет Голод.</summary>
    public static string BuildEssenceBlock(VampireCharacter c)
    {
        var sb = new StringBuilder();
        sb.Append("`").Append(PadRight("Человечность", 13)).Append("` ");
        sb.AppendLine(DotsString(VampireFinishingResolver.ComputeHumanity(c), 10));
        sb.Append("`").Append(PadRight("Воля", 13)).Append("` ");
        sb.AppendLine(DotsString(VampireFinishingResolver.ComputeWillpower(c), 10));
        sb.Append("`").Append(PadRight("Голод", 13)).Append("` ");
        sb.AppendLine(DotsString(Math.Clamp(c.Hunger, 0, 5), 5));
        return sb.ToString();
    }

    /// <summary>Колонка «Здоровье и опыт»: V20 стр. 92 шкала + штраф по таблице + Изъян + Опыт.</summary>
    public static string BuildHealthExperienceBlock(VampireCharacter c)
    {
        var sb = new StringBuilder();
        if (c.Health != null)
        {
            sb.AppendLine(BuildHealthTableV20(c.Health));
        }
        else
        {
            sb.AppendLine("`Здоровье не инициализировано`");
        }
        if (!string.IsNullOrWhiteSpace(c.Weakness))
        {
            sb.Append("**Изъян:** ").AppendLine(c.Weakness);
        }
        sb.Append("**Опыт:** ").Append(c.ExperienceCurrent).Append(" / ").Append(c.ExperienceTotal);
        return sb.ToString();
    }

    /// <summary>Шкала здоровья в формате V20 стр. 92: рендер шкалы + штраф по таблице.</summary>
    public static string BuildHealthTableV20(HealthState h)
    {
        var sb = new StringBuilder();
        sb.Append("**Здоровье:** ").AppendLine(h.Render());
        int? penalty = h.TablePenalty;
        if (penalty.HasValue)
        {
            int p = penalty.Value;
            if (p < 0)
                sb.Append("**Штраф:** -").Append(-p);
            else
                sb.Append("**Штраф:** 0");
        }
                else
                {
                    sb.Append("**Штраф:** —");
                }
        if (h.IsIncapacitated) sb.Append("  ·  *Небоеспособен*");
        if (h.IsTorpor) sb.Append("  ·  *Торпор*");
        if (h.IsDestroyed) sb.Append("  ·  *Уничтожен*");
        if (h.IsDead) sb.Append("  ·  *Финальная смерть*");
        return sb.ToString();
    }
    // ─── Утилиты ──────────────────────────────────────────────────────────

    private static string GroupNameForAbilities(IReadOnlyList<string> names)
    {
        if (ReferenceEquals(names, VampireParameterCatalog.Talents)) return "Таланты";
        if (ReferenceEquals(names, VampireParameterCatalog.Skills)) return "Навыки";
        if (ReferenceEquals(names, VampireParameterCatalog.Knowledges)) return "Знания";
        return "";
    }

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
        return s.Length >= width ? s : s + new string(' ', width - s.Length);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s.Substring(0, Math.Max(0, max - 1)) + "…";
    }
}
