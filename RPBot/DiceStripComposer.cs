using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

using DiscordColor = Discord.Color;

namespace RPBot;

/// <summary>
/// Склейка PNG-картинок отдельных граней кубика в один горизонтальный strip
/// (для отображения броска XdY одним embed'ом).
/// Используется в <see cref="RollDiceCommands"/>.
/// </summary>
/// <remarks>
/// <para>Аналогичный strip собирается в VtM-модуле
/// (<c>VampireDiceImageProvider</c>), но там тип фиксирован (d10) и есть
/// разные наборы regular/hunger/bonus. Здесь нужен один универсальный
/// набор на тип куба (d6/d20 и т.д.).</para>
/// <para>Все кубики берутся из подпапки <c>{numbersDir}/{diceType}/N.png</c>.
/// Если хотя бы один файл не найден, склейка возвращает <c>null</c> —
/// вызывающий решает, что делать (fallback на текст/несколько эмбедов).</para>
/// </remarks>
public static class DiceStripComposer
{
    /// <summary>Размер одного куба в склеенном strip'е (px). 168 — крупное превью, чтобы числа было видно без зума.</summary>
    public const int DieSize = 168;

    /// <summary>Горизонтальный отступ между кубиками (px).</summary>
    public const int Gap = 12;

    /// <summary>Внешний padding (px).</summary>
    public const int Padding = 10;

    /// <summary>
    /// Склеить значения <paramref name="values"/> в один PNG-strip.
    /// </summary>
    /// <param name="values">Выпавшие значения (1..max). Каждое значение ищется в файле <c>{numbersDir}/{diceType}/{value}.png</c>.</param>
    /// <param name="numbersDir">Корневая папка <c>Numbers/</c>.</param>
    /// <param name="diceType">Тип куба: <c>d6</c>, <c>d10</c>, <c>d20</c> и т.п.</param>
    /// <param name="missing">Список значений, для которых файл не найден.</param>
    /// <returns>Поток с PNG-данными, либо <c>null</c> если ничего не склеено (нет ни одного файла).</returns>
    public static MemoryStream? ComposeStrip(
        IReadOnlyList<int> values,
        string numbersDir,
        string diceType,
        out List<int> missing)
    {
        missing = new List<int>();
        if (values == null || values.Count == 0 || string.IsNullOrEmpty(diceType))
            return null;

        var folder = Path.Combine(numbersDir, diceType);
        var loaded = new List<(int value, Image img)>();
        try
        {
            foreach (var v in values)
            {
                var path = Path.Combine(folder, $"{v}.png");
                if (!File.Exists(path))
                {
                    missing.Add(v);
                    continue;
                }
                loaded.Add((v, Image.FromFile(path)));
            }

            if (loaded.Count == 0)
                return null;

            var totalW = Padding * 2 + loaded.Count * DieSize + Math.Max(0, loaded.Count - 1) * Gap;
            var totalH = Padding * 2 + DieSize;

            using var bmp = new Bitmap(totalW, totalH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CompositingMode = CompositingMode.SourceOver;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.Clear(Color.Transparent);

                int x = Padding;
                foreach (var (_, img) in loaded)
                {
                    g.DrawImage(img, x, Padding, DieSize, DieSize);
                    x += DieSize + Gap;
                }
            }

            var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            return ms;
        }
        finally
        {
            foreach (var (_, img) in loaded)
            {
                try { img.Dispose(); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Цвет embed по СРЕДНЕМУ арифметическому значению (требование пользователя).
    /// Линейная интерполяция красный→жёлтый→зелёный между minValue и maxValue.
    /// </summary>
    public static DiscordColor ColorForAverage(double average, int minValue, int maxValue)
    {
        if (maxValue <= minValue)
            return new DiscordColor(255, 255, 0);
        var t = (average - minValue) / (maxValue - minValue);
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        int r, g, b;
        if (t < 0.5)
        {
            r = 255;
            g = (int)(255 * (t * 2));
            b = 0;
        }
        else
        {
            r = (int)(255 * (1 - (t - 0.5) * 2));
            g = 255;
            b = 0;
        }
        return new DiscordColor(r, g, b);
    }
}
