using System.Text;
using System.Text.RegularExpressions;

namespace ProjectK.Catalog;

/// <summary>
/// Checks image files that come from outside. Images are served publicly from the catalog's
/// origin, so an SVG with a script in it would run there: anything active is rejected rather
/// than cleaned, and the sender fixes the file.
/// </summary>
public static partial class ImageSafety
{
    public const long MaxImageBytes = 2 * 1024 * 1024;
    public const long WarnImageBytes = 512 * 1024;

    public static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".webp"] = "image/webp",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
        };

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"<\s*script|<\s*foreignObject|<\s*iframe|<\s*embed|<\s*object|\son[a-z]+\s*=|javascript:|<!ENTITY|(?:xlink:)?href\s*=\s*[""']\s*(?:https?:|//|data:text)", RegexOptions.IgnoreCase)]
    private static partial Regex ActiveSvgContent();

    public static bool IsValidFileName(string name)
    {
        return FileNamePattern().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);
    }

    public static string? ContentTypeOf(string name)
    {
        return ContentTypes.TryGetValue(Path.GetExtension(name), out var type) ? type : null;
    }

    /// <summary>Returns problems with the file; empty when it may be published. Large files
    /// produce a warning only.</summary>
    public static IEnumerable<CatalogIssue> Check(string name, byte[] content)
    {
        var path = $"images/{name}";

        if (!IsValidFileName(name))
        {
            yield return CatalogIssue.Error(path, "File name may contain only latin letters, digits, '.', '_' and '-'.");
            yield break;
        }

        if (ContentTypeOf(name) is null)
        {
            yield return CatalogIssue.Error(path, "Only .svg, .png, .webp and .jpg images are accepted.");
            yield break;
        }

        if (content.LongLength > MaxImageBytes)
        {
            yield return CatalogIssue.Error(path, $"Image is {content.LongLength / 1024} KB; the limit is {MaxImageBytes / 1024} KB.");
        }
        else if (content.LongLength > WarnImageBytes)
        {
            yield return CatalogIssue.Warning(path, $"Image is {content.LongLength / 1024} KB; consider optimizing it (svgo, pngquant).");
        }

        if (name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var text = Encoding.UTF8.GetString(content);
            if (ActiveSvgContent().IsMatch(text))
            {
                yield return CatalogIssue.Error(path, "SVG contains scripts, event handlers, entities, embedded documents or external links.");
            }
        }
        else if (!HasExpectedSignature(name, content))
        {
            yield return CatalogIssue.Error(path, "File content does not match its extension.");
        }
    }

    private static bool HasExpectedSignature(string name, byte[] content)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".png" => content.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
            ".jpg" or ".jpeg" => content.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            ".webp" => content.Length > 12
                && content.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && content.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };
    }
}
