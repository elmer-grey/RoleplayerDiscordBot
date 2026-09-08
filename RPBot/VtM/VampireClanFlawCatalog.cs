using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RPBot.VtM;

/// <summary>
/// Хранилище словаря клановых изъянов VtM V20 (стр. 50-87) на диске +
/// копирование шаблона из embedded-ресурса при первом запуске.
/// <para>Файловая структура:
/// <c>%LOCALAPPDATA%/RPBot/Data/vampire/clan_flaws.json</c>
/// (см. <see cref="BotConfig.GetDataDirectory"/>).</para>
/// <para>Изначально хранит только полные тексты изъянов (<see cref="GetClanFlawLong"/>);
/// краткие формулировки (<see cref="GetClanFlawShort"/>) и клановые дисциплины
/// пока остаются в <see cref="VampireParameterCatalog"/>, т. к. они короткие
/// и логически связаны с проверками клана.</para>
/// </summary>
public static class VampireClanFlawCatalog
{
    private const string ResourceName = "RPBot.Resources.clan_flaws.v20.json";
    private const string TargetRelativePath = "vampire/clan_flaws.json";

    /// <summary>Запись об изъяне клана.</summary>
    public sealed class ClanFlawEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name_ru")] public string NameRu { get; set; } = "";
        [JsonPropertyName("flaw_short")] public string FlawShort { get; set; } = "";
        [JsonPropertyName("flaw_long")] public string FlawLong { get; set; } = "";
    }

    public sealed class ClanFlawsDocument
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("clans")] public ClanFlawEntry[] Clans { get; set; } = Array.Empty<ClanFlawEntry>();
    }

    /// <summary>Абсолютный путь к файлу на диске.</summary>
    public static string FilePath =>
        Path.Combine(BotConfig.GetDataDirectory(), TargetRelativePath);

    /// <summary>
    /// Гарантирует, что файл словаря существует. Если нет — копирует шаблон.
    /// </summary>
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

    /// <summary>Загрузить словарь с диска. Пустой массив при отсутствии файла.</summary>
    public static ClanFlawsDocument Load()
    {
        EnsureSeeded();
        if (!File.Exists(FilePath))
            return new ClanFlawsDocument();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<ClanFlawsDocument>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new ClanFlawsDocument();
        }
        catch (JsonException)
        {
            return new ClanFlawsDocument();
        }
    }

    /// <summary>Все записи, отсортированные по имени клана.</summary>
    public static IReadOnlyList<ClanFlawEntry> All()
        => Load().Clans.OrderBy(c => c.NameRu).ToList();

    /// <summary>Найти запись по русскому имени клана. Регистронезависимо.</summary>
    public static ClanFlawEntry? FindByName(string? clanName)
    {
        if (string.IsNullOrWhiteSpace(clanName)) return null;
        return Array.Find(Load().Clans,
            c => string.Equals(c.NameRu, clanName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Полный текст изъяна клана по имени. Пустая строка для неизвестных.</summary>
    public static string GetClanFlawLong(string clanName)
        => FindByName(clanName)?.FlawLong ?? "";
}
