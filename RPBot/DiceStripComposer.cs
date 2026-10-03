using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

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
/// <para><b>Производительность (audit 03.10.2026):</b> под нагрузкой
/// (25+ команд /roll подряд) GC-давление от Bitmap/PNG-энкода приводило к
/// 3-секундным паузам и «Приложение не отвечает». Оптимизации:
/// <list type="bullet">
///   <item><see cref="DieImageCacheImpl"/> — L1-кэш <see cref="Image"/> по
///   (path, mtime). Повторные броски тех же граней не открывают файл заново.</item>
///   <item><see cref="LruStripCache"/> — L2-кэш готовых PNG-strip'ов по хэшу
///   (diceType, sorted values). Повторный бросок тех же значений = готовый
///   MemoryStream из памяти.</item>
/// </list>
/// </para>
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
    /// <param name="diceType">Тип куба: <c>d6</c>, <c>d20</c> и т.п.</param>
    /// <param name="missing">Список значений, для которых файл не найден.</param>
    /// <returns>Поток с PNG-данными, либо <c>null</c> если ничего не склеено (нет ни одного файла).</returns>
    /// <remarks>
    /// Возвращённый <see cref="MemoryStream"/> принадлежит вызывающему —
    /// после использования должен быть Dispose'нут (через <c>using</c>). Для L2-кэша
    /// поток отдаётся «как есть» — несколько вызывающих могут читать из него
    /// параллельно до первого Dispose.
    /// </remarks>
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

        // L2: готовые strip'ы. Если все значения есть на диске и для такого набора
        // уже склеен strip — отдаём из памяти. Ключ сортируется, чтобы {1,2} и {2,1}
        // попадали в один кэш-слот.
        var cacheKey = BuildStripCacheKey(folder, values);
        if (cacheKey != null && StripResultCache.TryGet(cacheKey, out var cached))
        {
            return cached;
        }

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
                // L1: кэш Image.FromFile по (path, mtime). Повторные открытия того же
                // файла больше не делаем — отдаём из памяти.
                var img = DieImageCache.GetOrLoad(path);
                if (img != null)
                    loaded.Add((v, img));
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

            // capacity = totalW*totalH/4 — для 32bppArgb это точный размер пиксельных данных.
            var ms = new MemoryStream(capacity: totalW * totalH / 4);
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            // Сохраняем в L2-кэш ТОЛЬКО если все значения найдены, иначе strip неполный —
            // повторный набор значений может склеиться иначе после починки диска.
            if (cacheKey != null && missing.Count == 0)
                StripResultCache.Put(cacheKey, ms);

            return ms;
        }
        finally
        {
            // Image из DieImageCache НЕ dispos'им — ими владеет кэш.
            foreach (var (_, img) in loaded)
            {
                if (!DieImageCache.Owns(img))
                {
                    try { img.Dispose(); } catch { /* best effort */ }
                }
            }
        }
    }

    /// <summary>
    /// Построить ключ кэша готовых strip'ов. Возвращает <c>null</c> если кэшировать
    /// нельзя (пустые значения). Ключ включает folder и отсортированные значения —
    /// порядок не важен для визуала, и это даёт большее переиспользование кэша.
    /// </summary>
    private static string BuildStripCacheKey(string folder, IReadOnlyList<int> values)
    {
        if (values == null || values.Count == 0) return null!;
        var sorted = values.OrderBy(v => v).ToArray();
        var sb = new System.Text.StringBuilder(folder.Length + 16);
        sb.Append(folder).Append('|');
        for (int i = 0; i < sorted.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(sorted[i]);
        }
        return sb.ToString();
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

    /// <summary>
    /// L2-кэш готовых PNG-strip'ов. Максимум 256 записей, LRU.
    /// </summary>
    private static readonly LruStripCache StripResultCache = new(capacity: 256);

    /// <summary>
    /// L1-кэш отдельных <see cref="Image"/> по (path, mtime).
    /// </summary>
    private static readonly DieImageCacheImpl DieImageCache = new();
}

/// <summary>
/// LRU-кэш готовых PNG-strip'ов по строковому ключу.
/// </summary>
internal sealed class LruStripCache
{
    private readonly int _capacity;
    private readonly ConcurrentDictionary<string, LinkedListNode<Entry>> _lookup;
    private readonly LinkedList<Entry> _lru = new();
    private readonly object _lruLock = new();

    public LruStripCache(int capacity)
    {
        _capacity = capacity;
        _lookup = new ConcurrentDictionary<string, LinkedListNode<Entry>>();
    }

    public bool TryGet(string key, out MemoryStream stream)
    {
        stream = null!;
        if (!_lookup.TryGetValue(key, out var node)) return false;
        lock (_lruLock)
        {
            if (node.List == null) return false;
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
        stream = node.Value.Stream;
        return true;
    }

    public void Put(string key, MemoryStream stream)
    {
        lock (_lruLock)
        {
            if (_lookup.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                try { existing.Value.Stream.Dispose(); } catch { }
                existing.Value = new Entry(key, stream);
                _lru.AddFirst(existing);
                return;
            }

            while (_lru.Count >= _capacity)
            {
                var oldest = _lru.Last;
                if (oldest == null) break;
                _lru.RemoveLast();
                _lookup.TryRemove(oldest.Value.Key, out _);
                try { oldest.Value.Stream.Dispose(); } catch { }
            }

            var node = new LinkedListNode<Entry>(new Entry(key, stream));
            _lru.AddFirst(node);
            _lookup[key] = node;
        }
    }

    private sealed class Entry
    {
        public string Key;
        public MemoryStream Stream;
        public Entry(string key, MemoryStream stream) { Key = key; Stream = stream; }
    }
}

/// <summary>
/// L1-кэш <see cref="Image"/> по (path, lastWriteTimeUtc).
/// </summary>
internal sealed class DieImageCacheImpl
{
    private readonly ConcurrentDictionary<string, CachedImage> _cache = new();

    public Image? GetOrLoad(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return null;
        var mtime = fi.LastWriteTimeUtc.Ticks;
        var key = path;

        if (_cache.TryGetValue(key, out var cached) && cached.Mtime == mtime)
            return cached.Image;

        // Image.FromFile на Windows держит файл залоченным до Dispose.
        // Чтобы этого избежать — читаем файл в byte[] и используем FromStream.
        try
        {
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var ms = new MemoryStream((int)fs.Length))
            {
                fs.CopyTo(ms);
                bytes = ms.ToArray();
            }
            using var imgStream = new MemoryStream(bytes, writable: false);
            var copy = Image.FromStream(imgStream, useEmbeddedColorManagement: false, validateImageData: false);

            var entry = new CachedImage { Image = copy, Mtime = mtime };
            _cache.AddOrUpdate(key,
                entry,
                (_, existing) =>
                {
                    if (existing.Mtime == mtime)
                    {
                        try { copy.Dispose(); } catch { }
                        return existing;
                    }
                    try { existing.Image.Dispose(); } catch { }
                    return entry;
                });
            return _cache[key].Image;
        }
        catch
        {
            return null;
        }
    }

    public bool Owns(Image img)
    {
        foreach (var kv in _cache)
        {
            if (ReferenceEquals(kv.Value.Image, img)) return true;
        }
        return false;
    }

    private sealed class CachedImage
    {
        public Image Image = null!;
        public long Mtime;
    }
}