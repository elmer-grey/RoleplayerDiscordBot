using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Discord;

namespace RPBot.VtM;

/// <summary>
/// Результат подготовки PNG-вложений для броска VtM.
/// </summary>
/// <param name="Files">Найденные PNG-файлы. Если файл не найден — соответствующий кубик пропускается.</param>
/// <param name="MissingValues">Список значений (1..10), для которых PNG не нашёлся (для fallback-текста).</param>
public sealed record VampireDiceImages(IReadOnlyList<FileAttachment> Files, IReadOnlyList<int> MissingValues)
{
    /// <summary>Есть ли хотя бы одна картинка (имеет смысл показывать сообщение).</summary>
    public bool HasAny => Files.Count > 0;
}

/// <summary>
/// Сборщик PNG-картинок d10 для броска VtM.
/// </summary>
/// <remarks>
/// <para>По согласованию 2026-09-09: в embed'е броска VtM-броска картинки
/// «первые 5 обычные и последние 2 голодные» идут отдельным сообщением
/// (Discord ограничивает один embed одной картинкой).</para>
///
/// <para>Файлы ищутся в <c>%LocalAppData%\RPBot\Data\Numbers\d10_regular\N.png</c>
/// и <c>%LocalAppData%\RPBot\Data\Numbers\d10_hunger\N.png</c>.</para>
///
/// <para>Если файлов нет — возвращается пустой набор и список «пропущенных
/// значений». Embed остаётся текстовым (без падения).</para>
/// </remarks>
public static class VampireDiceImageProvider
{
    /// <summary>Имя подпапки для regular d10.</summary>
    public const string RegularFolder = "d10_regular";

    /// <summary>Имя подпапки для hunger d10.</summary>
    public const string HungerFolder = "d10_hunger";

    /// <summary>
    /// Собрать PNG-вложения для броска: сначала все regular-кубики,
    /// затем все hunger-кубики (по согласованию 2026-09-09: regular слева,
    /// hunger справа; если файл не найден — кубик пропускается).
    /// </summary>
    /// <param name="regularDice">Значения regular-кубиков (1..10).</param>
    /// <param name="hungerDice">Значения hunger-кубиков (1..10).</param>
    /// <param name="numbersDir">
    /// Корневая папка с подпапками <see cref="RegularFolder"/>/<see cref="HungerFolder"/>.
    /// Если <c>null</c> — используется <c>LocalAppData\RPBot\Data\Numbers</c>.
    /// </param>
    public static VampireDiceImages Build(
        IReadOnlyList<int> regularDice,
        IReadOnlyList<int> hungerDice,
        string? numbersDir = null)
    {
        var files = new List<FileAttachment>();
        var missing = new List<int>();

        var root = numbersDir
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RPBot", "Data", "Numbers");

        BuildForSet(regularDice, Path.Combine(root, RegularFolder), "reg", files, missing);
        BuildForSet(hungerDice, Path.Combine(root, HungerFolder), "hun", files, missing);

        return new VampireDiceImages(files, missing);
    }

    private static void BuildForSet(
        IReadOnlyList<int> dice,
        string folder,
        string prefix,
        List<FileAttachment> files,
        List<int> missing)
    {
        if (dice == null || dice.Count == 0) return;
        if (!Directory.Exists(folder))
        {
            // Папка целиком отсутствует — все значения считаем пропущенными.
            foreach (var d in dice) if (d >= 1 && d <= 10) missing.Add(d);
            return;
        }

        for (int i = 0; i < dice.Count; i++)
        {
            var value = dice[i];
            if (value < 1 || value > 10)
            {
                // d10 — только 1..10. Остальные значения пропускаем.
                continue;
            }
            var path = Path.Combine(folder, $"{value}.png");
            if (!File.Exists(path))
            {
                missing.Add(value);
                continue;
            }
            var uniqueName = $"{prefix}_{i + 1}_{value}.png";
            files.Add(new FileAttachment(path, uniqueName));
        }
    }
}
