using System;
using System.Text;
using Discord;

namespace RPBot.VtM;

/// <summary>
/// Отдельный embed «Клан» — клановые дисциплины + клановый изъян.
/// Показывается по кнопке «Клан» под листом персонажа.
/// </summary>
/// <remarks>
/// <para>Если клан не задан — возвращается <c>null</c>.</para>
/// <para>Изъян выводится полным текстом (V20 стр. 50-87), не короткой выжимкой.</para>
/// </remarks>
public static class VampireClanEmbed
{
    /// <summary>Лимит Discord на значение одного embed-поля (символов).</summary>
    public const int MaxFieldValue = 1024;

    /// <summary>
    /// Собрать embed клана для персонажа.
    /// </summary>
    /// <param name="c">Персонаж.</param>
    /// <returns>Готовый embed или <c>null</c>, если клан не задан.</returns>
    public static Embed? Build(VampireCharacter c)
    {
        if (c == null) throw new ArgumentNullException(nameof(c));
        if (string.IsNullOrWhiteSpace(c.Clan)) return null;

        var eb = new EmbedBuilder
        {
            Title = $"Клан: {c.Clan}",
            Color = VampireSheetEmbed.DefaultColor,
        };

        var disciplines = VampireParameterCatalog.GetClanDisciplines(c.Clan);
        if (disciplines.Count > 0)
        {
            var discs = string.Join(", ", disciplines);
            eb.AddField("Клановые дисциплины: " + discs, "▫️ На шаге 4 «Преимущества» можно вкладывать пункты только в них (3 пункта на распределение).", inline: false);
        }
        else
        {
            eb.AddField("Клановые дисциплины", "Каитиф — без клановых дисциплин. Свободными пунктами на шаге 4 можно вкладывать в любые (с одобрения рассказчика).", inline: false);
        }

        var flawShort = VampireParameterCatalog.GetClanFlawShort(c.Clan);
        var flawLong  = VampireParameterCatalog.GetClanFlawLong(c.Clan);
        if (!string.IsNullOrEmpty(flawLong))
        {
            eb.AddField("Клановый изъян (кратко)", flawShort, inline: false);
            eb.AddField("Клановый изъян (полностью)", Truncate(flawLong, MaxFieldValue), inline: false);
        }

        if (c.Generation > 0 && c.Generation != 13)
            eb.Footer = new EmbedFooterBuilder { Text = $"{c.Clan}, {c.Generation}-е поколение" };

        return eb.Build();
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        return s.Substring(0, max - 3) + "...";
    }
}
