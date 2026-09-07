using System;
using System.Linq;
using System.Text;
using Discord;

namespace RPBot.VtM
{
    /// <summary>
    /// Карточка ярости — отдельное окно, которое открывается кнопкой «🔥 Ярость» под листом.
    /// </summary>
    /// <remarks>
    /// Не путать с листом — здесь только информация о добродетелях, пуле, накопленных успехах
    /// (если подавление идёт несколько ходов) и текущих атавизмах. Кнопки броска — отдельные,
    /// живут в <see cref="VampireFrenzyComponents"/>.
    /// </remarks>
    public static class VampireFrenzyEmbed
    {
        /// <summary>Заголовок embed.</summary>
        public const string Title = "🔥 Зверь в ярости";

        /// <summary>Собрать embed с описанием ярости/ротшрека и кнопкой броска.</summary>
        /// <param name="character">Персонаж.</param>
        /// <param name="statusLine">Дополнительный статус (например, последний результат броска).</param>
        public static Embed Build(VampireCharacter character, string? statusLine = null)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));

            var eb = new EmbedBuilder
            {
                Title = Title,
                Color = Color.DarkRed,
                Description = BuildDescription(character),
            };

            eb.AddField(
                name: "🩸 Ярость",
                value: BuildFrenzyBlock(character),
                inline: true);

            eb.AddField(
                name: "🌑 Ротшрек",
                value: BuildRotschreckBlock(character),
                inline: true);

            eb.AddField(
                name: "🐾 Атавизмы Гангрела",
                value: BuildAtavismBlock(character),
                inline: false);

            if (!string.IsNullOrWhiteSpace(statusLine))
                eb.AddField(name: "📜 Последний бросок", value: statusLine, inline: false);

            eb.WithFooter("V20 стр. 322-325 • #42 «Зверь в ярости»");
            return eb.Build();
        }

        private static string BuildDescription(VampireCharacter character)
        {
            var sb = new StringBuilder();
            sb.Append("Ярость — реакция на **гнев, провокацию, голод**. ");
            sb.Append("Ротшрек — паника от **огня, солнца**. ");
            sb.AppendLine("Проверка добродетели против сложности:");
            sb.AppendLine("• ≥5 успехов (суммарно) — полностью подавить Зверя;");
            sb.AppendLine("• иначе — сдерживать на N успехов ходов;");
            sb.AppendLine("• провал — Зверь вырывается.");
            sb.AppendLine();
            sb.Append($"Клан: **{character.Clan ?? "—"}**");
            return sb.ToString();
        }

        private static string BuildFrenzyBlock(VampireCharacter character)
        {
            var sb = new StringBuilder();
            sb.Append("Самоконтроль / Инстинкты: **");
            sb.Append(GetVirtue(character, VampireParameterCatalog.VirtueSelfControl));
            sb.Append("**");
            if (VampireFrenzyResolver.IsInstinctDriven(character))
                sb.Append(" *(Инстинкты — всегда впадает)*");
            sb.AppendLine();
            sb.AppendLine("Стимулы (3..8):");
            sb.AppendLine("• Запах крови: 3");
            sb.AppendLine("• Вид крови / домогательство / угроза / оскорбление: 4");
            sb.AppendLine("• Провокация / вкус крови: 6");
            sb.AppendLine("• Близкие в опасности: 7");
            sb.AppendLine("• Публичное унижение: 8");
            sb.AppendLine();
            sb.Append("Ужасный поступок → сложность = `max(3, [9 − совесть])`");
            return sb.ToString();
        }

        private static string BuildRotschreckBlock(VampireCharacter character)
        {
            var sb = new StringBuilder();
            sb.Append("Смелость: **");
            sb.Append(GetVirtue(character, VampireParameterCatalog.VirtueCourage));
            sb.AppendLine("**");
            sb.AppendLine("Стимулы (3..9):");
            sb.AppendLine("• Тлеющая сигарета: 3");
            sb.AppendLine("• Горящий факел: 5");
            sb.AppendLine("• Костёр: 6");
            sb.AppendLine("• Рассеянный свет / ожог: 7");
            sb.AppendLine("• Прямой солнечный свет: 8");
            sb.AppendLine("• Пылающий пожар: 9");
            return sb.ToString();
        }

        private static string BuildAtavismBlock(VampireCharacter character)
        {
            var list = character.ActiveAtavisms;
            if (list == null || list.Count == 0)
                return "Нет активных атавизмов.\n*У Гангрела атавизм добавляется автоматически при входе в ярость.*";

            return string.Join("\n", list.Select((a, i) => $"{i + 1}. {a}"));
        }

        private static int GetVirtue(VampireCharacter character, string virtueName)
        {
            if (character.Virtues == null) return 0;
            return character.Virtues.TryGetValue(virtueName, out var v) ? v : 0;
        }
    }
}
