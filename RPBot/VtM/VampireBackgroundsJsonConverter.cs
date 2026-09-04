using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RPBot.VtM;

/// <summary>
/// JsonConverter для <see cref="VampireCharacter.Backgrounds"/>:
/// поддерживает и старый формат (массив строк), и новый (объект name→rank).
/// </summary>
/// <remarks>
/// <para>VtM V20 Шаг 4.2: 5 пунктов на факты биографии, у каждого факта
/// есть ранг 1..5. Раньше хранили как <c>List&lt;string&gt;</c>, теперь —
/// <c>Dictionary&lt;string,int&gt;</c>.</para>
/// <para>При чтении: если JSON-массив — каждая строка мигрирует как факт
/// с рангом 1. Старые значения формата «Имя N» (с пробелом и числом в конце)
/// тоже распознаются.</para>
/// <para>При записи: всегда пишется как объект.</para>
/// </remarks>
public sealed class VampireBackgroundsJsonConverter : JsonConverter<Dictionary<string, int>>
{
    public override Dictionary<string, int> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return new Dictionary<string, int>();

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            // Устаревший формат: ["Стая", "Ресурсы 2"].
            var list = JsonSerializer.Deserialize<List<string>>(ref reader, options)
                       ?? new List<string>();
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var raw in list)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var (name, rank) = SplitLegacyEntry(raw.Trim());
                if (string.IsNullOrEmpty(name)) continue;
                // Дубликат имени — суммировать ранги (cap 5 берётся на стороне резолвера).
                if (result.TryGetValue(name, out var prev))
                    result[name] = Math.Clamp(prev + rank, 1, 5);
                else
                    result[name] = rank;
            }
            return result;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var name = reader.GetString();
                if (string.IsNullOrWhiteSpace(name)) { reader.Skip(); continue; }
                reader.Read();
                int rank = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v)
                    ? v : 1;
                result[name] = Math.Clamp(rank, 1, 5);
            }
            return result;
        }

        // Неизвестный формат — пропускаем.
        reader.Skip();
        return new Dictionary<string, int>();
    }

    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<string, int> value,
        JsonSerializerOptions options)
    {
        if (value == null || value.Count == 0)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }
        writer.WriteStartObject();
        foreach (var kv in value)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            writer.WritePropertyName(kv.Key);
            writer.WriteNumberValue(Math.Clamp(kv.Value, 1, 5));
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Разобрать запись старого формата: «Стая» → («Стая», 1),
    /// «Ресурсы 2» → («Ресурсы», 2).
    /// </summary>
    private static (string Name, int Rank) SplitLegacyEntry(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return ("", 1);
        var span = entry.AsSpan().TrimEnd();
        // Ищем последний пробел + число (1..5).
        for (int i = span.Length - 1; i > 0; i--)
        {
            if (span[i] != ' ') continue;
            var tail = span[(i + 1)..];
            if (int.TryParse(tail, out var rank) && rank >= 1 && rank <= 5)
            {
                return (span[..i].ToString(), rank);
            }
        }
        return (entry, 1);
    }
}
