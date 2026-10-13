using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace ProjectK.Catalog;

public static class CatalogJson
{
    /// <summary>camelCase, indented, Cyrillic written as is so the file stays readable and diffs
    /// stay small. Unknown properties are rejected: a typo in a hand-filled template should fail
    /// validation, not silently disappear.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static CatalogBundle Deserialize(string json)
    {
        return JsonSerializer.Deserialize<CatalogBundle>(json, Options)
            ?? throw new JsonException("catalog.json is empty.");
    }

    public static CatalogBundle Deserialize(Stream stream)
    {
        return JsonSerializer.Deserialize<CatalogBundle>(stream, Options)
            ?? throw new JsonException("catalog.json is empty.");
    }

    public static string Serialize(CatalogBundle bundle)
    {
        return JsonSerializer.Serialize(bundle, Options) + "\n";
    }
}
