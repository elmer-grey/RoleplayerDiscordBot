using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace RPBot.VtM;

/// <summary>
/// Хелпер для апгрейда JSON-справочников VtM с диска по <c>schema_version</c>
/// из embedded-ресурса.
/// </summary>
/// <remarks>
/// <para>Контракт: каждый справочник имеет в embedded-JSON поле
/// <c>schema_version</c> (int). На диске пользователя лежит копия, которая
/// могла устареть. <see cref="UpgradeIfStaleAsync"/> сравнивает версию на
/// диске и в embedded — если на диске старее, пересеивает файл
/// целиком из embedded (полная перезапись, без merge).</para>
///
/// <para>Стратегия:
/// <list type="bullet">
///   <item>disk &lt; embedded → пересеять из embedded;</item>
///   <item>disk = embedded → no-op;</item>
///   <item>disk &gt; embedded → no-op (защита от случайного даунгрейда);</item>
///   <item>disk отсутствует → <see cref="EnsureSeededAsync"/> (первый запуск);</item>
///   <item>embedded отсутствует → no-op (ошибка сборки, не блокируем).</item>
/// </list>
/// </para>
///
/// <para>Файл на диске может быть «битым» или без <c>schema_version</c> —
/// в этом случае версия считается равной 0, и при embedded &gt; 0 файл
/// будет пересеян. Это лечит случай, когда пользователь обновился с
/// версии бота, где поле называлось <c>version</c> (без префикса).</para>
/// </remarks>
public static class CatalogUpdater
{
    /// <summary>
    /// Результат проверки версии и (если нужно) пересева.
    /// </summary>
    public enum UpgradeResult
    {
        /// <summary>Файл отсутствовал — выполнен первичный сидинг.</summary>
        Seeded,
        /// <summary>Версия на диске была меньше embedded — файл пересеян.</summary>
        Upgraded,
        /// <summary>Версия на диске совпала с embedded — ничего не делали.</summary>
        UpToDate,
        /// <summary>Версия на диске больше embedded — оставлено как есть.</summary>
        DowngradeRefused,
        /// <summary>embedded-ресурс не найден — на диске оставлено то, что было.</summary>
        ResourceMissing,
    }

    /// <summary>
    /// Сравнить версию на диске с embedded и при необходимости пересеять.
    /// </summary>
    /// <param name="filePath">Абсолютный путь к JSON на диске.</param>
    /// <param name="resourceName">Полное имя embedded-ресурса
    /// (например, <c>RPBot.Resources.derangements.v20.json</c>).</param>
    /// <param name="assembly">Сборка, в которой искать ресурс. По умолчанию —
    /// сборка, содержащая <see cref="CatalogUpdater"/>.</param>
    public static UpgradeResult UpgradeIfStaleAsync(
        string filePath,
        string resourceName,
        Assembly? assembly = null)
    {
        assembly ??= Assembly.GetExecutingAssembly();

        var embeddedVersion = ReadEmbeddedSchemaVersion(assembly, resourceName);
        if (embeddedVersion is null)
        {
            // embedded не нашли — не блокируем бота. Если на диске что-то есть, оставляем.
            return File.Exists(filePath) ? UpgradeResult.UpToDate : UpgradeResult.ResourceMissing;
        }

        if (!File.Exists(filePath))
            return SeedFromResource(filePath, resourceName, assembly) ? UpgradeResult.Seeded : UpgradeResult.ResourceMissing;

        var diskVersion = ReadDiskSchemaVersion(filePath);

        if (diskVersion >= embeddedVersion.Value)
        {
            // Disk свежее или равен — ничего не трогаем. Если disk>embedded — отказ от даунгрейда.
            return diskVersion > embeddedVersion.Value ? UpgradeResult.DowngradeRefused : UpgradeResult.UpToDate;
        }

        // Disk старее — пересеиваем.
        return SeedFromResource(filePath, resourceName, assembly) ? UpgradeResult.Upgraded : UpgradeResult.ResourceMissing;
    }

    /// <summary>
    /// Скопировать embedded-ресурс в <paramref name="filePath"/>, перезаписав
    /// существующий файл. Создаёт родительский каталог при необходимости.
    /// </summary>
    private static bool SeedFromResource(string filePath, string resourceName, Assembly assembly)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return false;

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var fs = File.Create(filePath);
            stream.CopyTo(fs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Прочитать <c>schema_version</c> из embedded-ресурса. Возвращает
    /// <c>null</c>, если ресурс не найден или поле отсутствует/битое.
    /// </summary>
    private static int? ReadEmbeddedSchemaVersion(Assembly assembly, string resourceName)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            using var doc = JsonDocument.Parse(stream);
            return ExtractSchemaVersion(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Прочитать <c>schema_version</c> из файла на диске. Возвращает 0,
    /// если файла нет, JSON битый, или поле отсутствует.
    /// </summary>
    /// <remarks>
    /// Обратная совместимость: если на диске лежит старый формат с полем
    /// <c>version</c> (без префикса <c>schema_</c>), читаем его как
    /// <c>schema_version</c>. Это лечит обновление с билда, где поле
    /// ещё называлось <c>version</c>.
    /// </remarks>
    private static int ReadDiskSchemaVersion(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var doc = JsonDocument.Parse(stream);
            return ExtractSchemaVersion(doc.RootElement) ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Достать значение поля <c>schema_version</c> из корня JSON. Если
    /// такого поля нет — fallback на старое имя <c>version</c>.
    /// </summary>
    private static int? ExtractSchemaVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        // Новый формат
        if (root.TryGetProperty("schema_version", out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n))
        {
            return n;
        }

        // Старый формат (для обратной совместимости со старыми билдами)
        if (root.TryGetProperty("version", out var legacy)
            && legacy.ValueKind == JsonValueKind.Number
            && legacy.TryGetInt32(out var ln))
        {
            return ln;
        }

        return null;
    }
}
