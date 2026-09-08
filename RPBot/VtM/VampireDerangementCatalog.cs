using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RPBot.VtM;

/// <summary>
/// Хранилище словаря психических расстройств VtM V20 (стр. 313-317) на диске
/// + копирование шаблона из embedded-ресурса при первом запуске.
/// <para>Файловая структура:
/// <c>%LOCALAPPDATA%/RPBot/Data/vampire/derangements.json</c>
/// (см. <see cref="BotConfig.GetDataDirectory"/>).</para>
/// <para>Рассказчик может добавить расстройство не из каталога — поле
/// <see cref="VampireCharacter.Derangements"/> это позволяет. Каталог нужен
/// только для UI-выбора.</para>
/// </summary>
public static class VampireDerangementCatalog
{
    private const string ResourceName = "RPBot.Resources.derangements.v20.json";
    private const string TargetRelativePath = "vampire/derangements.json";

    /// <summary>Запись расстройства.</summary>
    public sealed class DerangementEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name_ru")] public string NameRu { get; set; } = "";
        [JsonPropertyName("summary")] public string Summary { get; set; } = "";
        [JsonPropertyName("effect")] public string Effect { get; set; } = "";
    }

    public sealed class DerangementsDocument
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("derangements")] public DerangementEntry[] Derangements { get; set; } = Array.Empty<DerangementEntry>();
    }

    /// <summary>
    /// Абсолютный путь к файлу derangements.json на диске.
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

    /// <summary>Загрузить словарь расстройств с диска. Пустой массив при отсутствии файла.</summary>
    public static DerangementsDocument Load()
    {
        EnsureSeeded();
        if (!File.Exists(FilePath))
            return new DerangementsDocument();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<DerangementsDocument>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new DerangementsDocument();
        }
        catch (JsonException)
        {
            return new DerangementsDocument();
        }
    }

    /// <summary>Все расстройства из словаря, отсортированные по имени.</summary>
    public static IReadOnlyList<DerangementEntry> All()
        => Load().Derangements.OrderBy(d => d.NameRu).ToList();

    /// <summary>Найти запись по id (пустая строка/null → null).</summary>
    public static DerangementEntry? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Array.Find(Load().Derangements,
            d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Является ли имя валидным расстройством из каталога.</summary>
    public static bool IsKnown(string name)
        => !string.IsNullOrEmpty(name) && All().Any(d => d.NameRu == name);

    /// <summary>Короткое описание эффекта для UI-подсказки. Пустая строка для неизвестных.</summary>
    public static string Describe(string name)
        => All().FirstOrDefault(d => d.NameRu == name)?.Effect ?? "";
}
