using System.Linq;
using System.Text;

namespace RPBot.VtM
{
    /// <summary>
    /// Текстовая отрисовка блока «Мораль» (V20 стр. 92, 313+, 333).
    /// </summary>
    public static class VampireMoralityEmbed
    {
        /// <summary>
        /// Сформировать описание морального состояния для блока листа.
        /// </summary>
        public static string BuildBlock(VampireCharacter c)
        {
            if (c == null) return "Персонажа нет.";

            var sb = new StringBuilder();
            if (string.IsNullOrEmpty(c.Path))
            {
                sb.AppendLine("**Человечность/Путь:** _Человечность_");
            }
            else
            {
                sb.Append("**Человечность/Путь:** _").Append(c.Path).AppendLine("_");
                if (c.PathRating > 0)
                    sb.Append("**Значение:** ").Append(c.PathRating).AppendLine();
            }

            var derangements = c.Derangements?
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct()
                .ToList() ?? new();

            sb.Append("**Расстройства:** ");
            if (derangements.Count == 0)
            {
                sb.AppendLine("_нет_");
            }
            else
            {
                sb.AppendLine();
                foreach (var d in derangements)
                {
                    sb.Append("• ").Append(d);
                    var hint = VampireDerangementCatalog.Describe(d);
                    if (!string.IsNullOrEmpty(hint))
                        sb.Append(" — ").Append(hint);
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }
    }
}
