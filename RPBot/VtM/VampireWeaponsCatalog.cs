using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RPBot.VtM;

/// <summary>
/// Хранилище словаря оружия VtM V20 на диске + копирование шаблона из
/// embedded-ресурса при первом запуске.
/// <para>Файловая структура:
/// <c>%LOCALAPPDATA%/RPBot/Data/vampire/weapons.json</c>
/// (см. <see cref="BotConfig.GetDataDirectory"/>).</para>
/// </summary>
public static class VampireWeaponsCatalog
{
    private const string ResourceName = "RPBot.Resources.weapons.v20.json";
    private const string TargetRelativePath = "vampire/weapons.json";

    public sealed class WeaponEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name_ru")] public string NameRu { get; set; } = "";
        [JsonPropertyName("name_en")] public string NameEn { get; set; } = "";
        [JsonPropertyName("damage")] public int Damage { get; set; }
        [JsonPropertyName("damage_type")] public string DamageType { get; set; } = "lethal";
        [JsonPropertyName("adds_strength")] public bool AddsStrength { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; } = "melee";
        [JsonPropertyName("range")] public int? Range { get; set; }
        [JsonPropertyName("rate")] public int? Rate { get; set; }
        [JsonPropertyName("clip")] public int? Clip { get; set; }
        [JsonPropertyName("_comment")] public string? Comment { get; set; }
    }

    public sealed class WeaponsDocument
    {
        /// <summary>
        /// Актуальная версия схемы. Источник истины — embedded JSON.
        /// При значении на диске меньше, чем в embedded — VampireStartService Шаг 0
        /// пересеет файл целиком.
        /// </summary>
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyName("weapons")] public WeaponEntry[] Weapons { get; set; } = Array.Empty<WeaponEntry>();
    }

    /// <summary>
    /// Абсолютный путь к файлу weapons.json на диске.
    /// </summary>
    public static string FilePath =>
        Path.Combine(BotConfig.GetDataDirectory(), TargetRelativePath);

    /// <summary>
    /// Гарантирует, что файл словаря существует на диске. Если нет —
    /// копирует шаблон из embedded-ресурса.
    /// </summary>
    /// <returns>
    /// <c>(true, filePath)</c>, если словарь существует (свежескопированный
    /// или уже был); <c>(false, filePath)</c>, если ресурс не найден.
    /// </returns>
    public static (bool Ok, string Path) EnsureSeeded()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        if (File.Exists(FilePath))
            return (true, FilePath);

        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName);
        if (stream == null)
            return (false, FilePath);

        using var fs = File.Create(FilePath);
        stream.CopyTo(fs);
        return (true, FilePath);
    }

    /// <summary>
    /// Сравнить версию на диске с embedded и при необходимости пересеять.
    /// Вызывается из <c>VampireStartService</c> Шаг 0.
    /// </summary>
    public static CatalogUpdater.UpgradeResult UpgradeIfStale()
        => CatalogUpdater.UpgradeIfStaleAsync(FilePath, ResourceName);

    /// <summary>
    /// Загрузить словарь оружия с диска. Если файла нет — возвращает
    /// пустой массив (без копирования). Для первичной инициализации
    /// используйте <see cref="EnsureSeeded"/>.
    /// </summary>
    public static WeaponsDocument Load()
    {
        if (!File.Exists(FilePath))
            return new WeaponsDocument();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<WeaponsDocument>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new WeaponsDocument();
        }
        catch (JsonException)
        {
            return new WeaponsDocument();
        }
    }

    public static WeaponEntry? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Array.Find(Load().Weapons,
            w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
