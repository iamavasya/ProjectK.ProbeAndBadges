using System.IO.Compression;

namespace ProjectK.Catalog;

/// <summary>
/// A bundle with its images: <c>catalog.json</c> plus <c>images/&lt;file&gt;</c>, either as a
/// folder or as a zip. Zips come from outside, so reading one is bounded: entry count, sizes,
/// and names are checked before anything is unpacked, and nothing is written to disk.
/// </summary>
public sealed class CatalogPackage
{
    public const string CatalogFileName = "catalog.json";
    public const string ImagesFolder = "images";

    public const int MaxEntries = 2000;
    public const long MaxCatalogBytes = 10 * 1024 * 1024;
    public const long MaxTotalBytes = 200 * 1024 * 1024;

    public required CatalogBundle Bundle { get; init; }
    public Dictionary<string, byte[]> Images { get; init; } = new(StringComparer.Ordinal);

    public IReadOnlySet<string> ImageNames => Images.Keys.ToHashSet(StringComparer.Ordinal);

    public static CatalogPackage ReadFolder(string folder)
    {
        var bundle = CatalogJson.Deserialize(File.ReadAllText(Path.Combine(folder, CatalogFileName)));
        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var imagesPath = Path.Combine(folder, ImagesFolder);
        if (Directory.Exists(imagesPath))
        {
            foreach (var file in Directory.EnumerateFiles(imagesPath))
            {
                images[Path.GetFileName(file)] = File.ReadAllBytes(file);
            }
        }

        return new CatalogPackage { Bundle = bundle, Images = images };
    }

    /// <summary>Reads a zip. Throws <see cref="InvalidDataException"/> with a message fit for the
    /// sender when the archive breaks a limit or carries something other than the catalog.</summary>
    public static CatalogPackage ReadZip(Stream zip)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException($"Archive has {archive.Entries.Count} entries; the limit is {MaxEntries}.");
        }

        // Some tools put everything under one top folder ("prague/catalog.json"); accept that.
        var root = FindRoot(archive);
        CatalogBundle? bundle = null;
        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/') || IsJunk(name)) continue;
            if (!name.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unexpected file '{name}'. The archive holds catalog.json and images/ only.");
            }

            var relative = name[root.Length..];
            total += entry.Length;
            if (total > MaxTotalBytes)
            {
                throw new InvalidDataException($"Archive unpacks to more than {MaxTotalBytes / 1024 / 1024} MB.");
            }

            if (relative == CatalogFileName)
            {
                if (entry.Length > MaxCatalogBytes) throw new InvalidDataException("catalog.json is larger than 10 MB.");
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                CopyBounded(stream, buffer, MaxCatalogBytes);
                buffer.Position = 0;
                bundle = CatalogJson.Deserialize(buffer);
            }
            else if (relative.StartsWith(ImagesFolder + "/", StringComparison.Ordinal)
                && relative.Count(c => c == '/') == 1
                && ImageSafety.IsValidFileName(relative[(ImagesFolder.Length + 1)..]))
            {
                if (entry.Length > ImageSafety.MaxImageBytes)
                {
                    throw new InvalidDataException($"{relative} is larger than {ImageSafety.MaxImageBytes / 1024} KB.");
                }

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                CopyBounded(stream, buffer, ImageSafety.MaxImageBytes);
                images[relative[(ImagesFolder.Length + 1)..]] = buffer.ToArray();
            }
            else
            {
                throw new InvalidDataException($"Unexpected file '{name}'. The archive holds catalog.json and images/<file> only; image names use latin letters, digits, '.', '_' and '-'.");
            }
        }

        return new CatalogPackage
        {
            Bundle = bundle ?? throw new InvalidDataException("Archive has no catalog.json."),
            Images = images,
        };
    }

    public void WriteFolder(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, ImagesFolder));
        File.WriteAllText(Path.Combine(folder, CatalogFileName), CatalogJson.Serialize(Bundle));
        foreach (var (name, content) in Images)
        {
            File.WriteAllBytes(Path.Combine(folder, ImagesFolder, name), content);
        }
    }

    public void WriteZip(Stream output)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        using (var writer = new StreamWriter(archive.CreateEntry(CatalogFileName).Open()))
        {
            writer.Write(CatalogJson.Serialize(Bundle));
        }

        foreach (var (name, content) in Images.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            using var stream = archive.CreateEntry($"{ImagesFolder}/{name}").Open();
            stream.Write(content);
        }
    }

    /// <summary>Full check: the catalog itself (against <paramref name="reference"/> when it is a
    /// contribution) and every image file it carries.</summary>
    public ValidationReport Validate(CatalogBundle? reference = null, IReadOnlySet<string>? referenceImages = null)
    {
        var available = ImageNames.ToHashSet(StringComparer.Ordinal);
        if (referenceImages is not null) available.UnionWith(referenceImages);

        var report = CatalogValidator.Validate(Bundle, available, reference);
        foreach (var (name, content) in Images)
        {
            report.Issues.AddRange(ImageSafety.Check(name, content));
        }

        return report;
    }

    private static string FindRoot(ZipArchive archive)
    {
        var catalog = archive.Entries
            .Select(e => e.FullName.Replace('\\', '/'))
            .Where(n => !IsJunk(n) && (n == CatalogFileName || n.EndsWith("/" + CatalogFileName, StringComparison.Ordinal)))
            .OrderBy(n => n.Length)
            .FirstOrDefault();
        if (catalog is null) return string.Empty;
        var root = catalog[..^CatalogFileName.Length];
        return root.Count(c => c == '/') <= 1 ? root : string.Empty;
    }

    private static bool IsJunk(string name)
    {
        return name.StartsWith("__MACOSX/", StringComparison.Ordinal)
            || name.Contains("/__MACOSX/", StringComparison.Ordinal)
            || name.EndsWith(".DS_Store", StringComparison.Ordinal)
            || name.EndsWith("Thumbs.db", StringComparison.Ordinal);
    }

    // entry.Length comes from the zip header and can lie; count the bytes actually inflated.
    private static void CopyBounded(Stream source, Stream destination, long limit)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += read;
            if (copied > limit) throw new InvalidDataException($"A file unpacks to more than {limit / 1024} KB.");
            destination.Write(buffer, 0, read);
        }
    }
}
