using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProjectK.ProbeAndBadges.Abstractions;

namespace ProjectK.ProbeAndBadges;

internal static class BadgesAssetsExtractor
{
    private const string ManifestFileName = "assets-snapshot-manifest.json";
    private const string SnapshotsDirectoryName = "snapshots";

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg",
        ".png",
        ".jpg",
        ".jpeg",
        ".webp"
    };

    public static BadgesAssetsSnapshotInfo? TryExtractAssets(
        BadgesDataLoadResult loadResult,
        BadgesCatalogOptions options,
        BadgesMetadata metadata,
        IReadOnlyList<Badge> badges,
        ILogger logger)
    {
        if (!options.EnableAssetsExtraction)
        {
            logger.LogDebug("Assets extraction is disabled by configuration.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(loadResult.SourceZipPath) || !File.Exists(loadResult.SourceZipPath))
        {
            logger.LogDebug("Assets extraction skipped because source zip path is unavailable.");
            return null;
        }

        var sourceZipPath = Path.GetFullPath(loadResult.SourceZipPath);
        var sourceZipSha256 = string.IsNullOrWhiteSpace(loadResult.SourceZipSha256)
            ? ComputeSha256HexFromFile(sourceZipPath)
            : loadResult.SourceZipSha256;

        var assetsRoot = Path.GetFullPath(options.AssetsRootDirectory);
        Directory.CreateDirectory(assetsRoot);

        using var extractionMutex = new Mutex(false, BuildExtractionMutexName(assetsRoot));
        var lockTimeoutMs = Math.Max(0, options.AssetExtractionLockTimeoutSeconds) * 1000;
        if (!extractionMutex.WaitOne(lockTimeoutMs))
        {
            throw new TimeoutException($"Timed out waiting for assets extraction lock for '{assetsRoot}'.");
        }

        try
        {
            var existingManifest = LoadManifest(assetsRoot, logger);
            if (existingManifest is not null &&
                string.Equals(existingManifest.SourceZipSha256, sourceZipSha256, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(existingManifest.ImagesDirectoryPath))
            {
                logger.LogInformation(
                    "Assets extraction skipped. Existing snapshot is up to date. ImagesDirectoryPath={ImagesDirectoryPath}",
                    existingManifest.ImagesDirectoryPath);

                return ToSnapshotInfo(existingManifest);
            }

            var snapshotsRoot = Path.Combine(assetsRoot, SnapshotsDirectoryName);
            Directory.CreateDirectory(snapshotsRoot);

            var snapshotDirectoryPath = Path.Combine(snapshotsRoot, sourceZipSha256);
            var tempSnapshotDirectoryPath = Path.Combine(snapshotsRoot, $"{sourceZipSha256}.tmp-{Guid.NewGuid():N}");

            var extractedImagesCount = 0;

            try
            {
                if (Directory.Exists(tempSnapshotDirectoryPath))
                {
                    Directory.Delete(tempSnapshotDirectoryPath, recursive: true);
                }

                Directory.CreateDirectory(tempSnapshotDirectoryPath);

                extractedImagesCount = ExtractImages(
                    sourceZipPath,
                    tempSnapshotDirectoryPath,
                    options.AssetsImagesDirectoryName,
                    options.ReleaseDataZipEntryPath,
                    options.MaxExtractedImageEntries,
                    options.MaxExtractedSingleImageBytes,
                    options.MaxExtractedImagesTotalBytes);

                if (extractedImagesCount <= 0)
                {
                    throw new InvalidDataException("No images were extracted from source zip.");
                }

                var extractedImagesDirectoryPath = Path.Combine(tempSnapshotDirectoryPath, options.AssetsImagesDirectoryName);

                if (options.ValidateExtractedImagesAgainstCatalog)
                {
                    ValidateExtractedImages(
                        extractedImagesDirectoryPath,
                        badges,
                        options.MissingImagesPolicy,
                        logger);
                }

                if (Directory.Exists(snapshotDirectoryPath))
                {
                    Directory.Delete(snapshotDirectoryPath, recursive: true);
                }

                Directory.Move(tempSnapshotDirectoryPath, snapshotDirectoryPath);

                var finalImagesDirectoryPath = Path.Combine(snapshotDirectoryPath, options.AssetsImagesDirectoryName);

                var manifest = new AssetsSnapshotManifest
                {
                    SourceZipPath = sourceZipPath,
                    SourceZipSha256 = sourceZipSha256,
                    ParserVersion = metadata.ParserVersion,
                    ExtractedAtUtc = DateTimeOffset.UtcNow,
                    SnapshotDirectoryPath = snapshotDirectoryPath,
                    ImagesDirectoryPath = finalImagesDirectoryPath,
                    ImagesCount = extractedImagesCount
                };

                SaveManifest(assetsRoot, manifest, logger);
                CleanupOldSnapshots(snapshotsRoot, snapshotDirectoryPath, options.AssetsSnapshotRetentionCount, logger);

                logger.LogInformation(
                    "Assets extraction completed. ImagesCount={ImagesCount}, ImagesDirectoryPath={ImagesDirectoryPath}",
                    extractedImagesCount,
                    finalImagesDirectoryPath);

                return ToSnapshotInfo(manifest);
            }
            catch
            {
                TryDeleteDirectory(tempSnapshotDirectoryPath, logger);
                throw;
            }
        }
        finally
        {
            extractionMutex.ReleaseMutex();
        }
    }

    private static int ExtractImages(
        string sourceZipPath,
        string extractionRoot,
        string imagesDirectoryName,
        string badgesEntryPath,
        int maxExtractedImageEntries,
        long maxExtractedSingleImageBytes,
        long maxExtractedImagesTotalBytes)
    {
        ValidateExtractionLimitsConfiguration(
            maxExtractedImageEntries,
            maxExtractedSingleImageBytes,
            maxExtractedImagesTotalBytes);

        var normalizedImagesPrefix = NormalizeImagesPrefix(imagesDirectoryName);
        var normalizedBadgesEntryPath = NormalizeBadgesEntryPath(badgesEntryPath);

        var extractedCount = 0;
        long totalExtractedBytes = 0;
        var extractionRootFullPath = Path.GetFullPath(extractionRoot);

        using var archive = ZipFile.OpenRead(sourceZipPath);

        var badgesEntryExists = archive.Entries.Any(entry =>
            string.Equals(entry.FullName.Replace('\\', '/').TrimStart('/'), normalizedBadgesEntryPath, StringComparison.OrdinalIgnoreCase));

        if (!badgesEntryExists)
        {
            throw new InvalidDataException($"Zip '{sourceZipPath}' does not contain required entry '{normalizedBadgesEntryPath}'.");
        }

        foreach (var entry in archive.Entries)
        {
            if (!TryGetNormalizedImageEntryPath(entry, normalizedImagesPrefix, out var normalizedEntryPath))
            {
                continue;
            }

            var nextTotalBytes = ValidateImageEntryLimits(
                entry,
                normalizedEntryPath,
                extractedCount,
                totalExtractedBytes,
                maxExtractedImageEntries,
                maxExtractedSingleImageBytes,
                maxExtractedImagesTotalBytes);

            var destinationPath = ResolveDestinationPath(extractionRoot, extractionRootFullPath, normalizedEntryPath);
            CopyZipEntryToFile(entry, destinationPath);

            totalExtractedBytes = nextTotalBytes;
            extractedCount++;
        }

        return extractedCount;
    }

    private static void ValidateExtractionLimitsConfiguration(
        int maxExtractedImageEntries,
        long maxExtractedSingleImageBytes,
        long maxExtractedImagesTotalBytes)
    {
        if (maxExtractedImageEntries <= 0)
        {
            throw new InvalidDataException("MaxExtractedImageEntries must be greater than zero.");
        }

        if (maxExtractedSingleImageBytes <= 0)
        {
            throw new InvalidDataException("MaxExtractedSingleImageBytes must be greater than zero.");
        }

        if (maxExtractedImagesTotalBytes <= 0)
        {
            throw new InvalidDataException("MaxExtractedImagesTotalBytes must be greater than zero.");
        }
    }

    private static string NormalizeImagesPrefix(string imagesDirectoryName)
    {
        var normalizedImagesPrefix = imagesDirectoryName.Trim().Trim('/').Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalizedImagesPrefix))
        {
            throw new InvalidDataException("AssetsImagesDirectoryName must be a non-empty folder name.");
        }

        return normalizedImagesPrefix;
    }

    private static string NormalizeBadgesEntryPath(string badgesEntryPath)
    {
        return string.IsNullOrWhiteSpace(badgesEntryPath)
            ? "badges.json"
            : badgesEntryPath.Trim().Trim('/').Replace('\\', '/');
    }

    private static bool TryGetNormalizedImageEntryPath(ZipArchiveEntry entry, string normalizedImagesPrefix, out string normalizedEntryPath)
    {
        normalizedEntryPath = entry.FullName.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalizedEntryPath) || normalizedEntryPath.EndsWith('/'))
        {
            return false;
        }

        ValidateSafeRelativePath(normalizedEntryPath);

        if (!normalizedEntryPath.StartsWith($"{normalizedImagesPrefix}/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(normalizedEntryPath);
        return AllowedImageExtensions.Contains(extension);
    }

    private static long ValidateImageEntryLimits(
        ZipArchiveEntry entry,
        string normalizedEntryPath,
        int extractedCount,
        long totalExtractedBytes,
        int maxExtractedImageEntries,
        long maxExtractedSingleImageBytes,
        long maxExtractedImagesTotalBytes)
    {
        if (entry.Length > maxExtractedSingleImageBytes)
        {
            throw new InvalidDataException(
                $"Image '{normalizedEntryPath}' exceeds MaxExtractedSingleImageBytes ({entry.Length} > {maxExtractedSingleImageBytes}).");
        }

        if (extractedCount + 1 > maxExtractedImageEntries)
        {
            throw new InvalidDataException(
                $"Extracted image entries exceed MaxExtractedImageEntries ({maxExtractedImageEntries}).");
        }

        var nextTotalBytes = totalExtractedBytes + entry.Length;
        if (nextTotalBytes > maxExtractedImagesTotalBytes)
        {
            throw new InvalidDataException(
                $"Extracted image bytes exceed MaxExtractedImagesTotalBytes ({nextTotalBytes} > {maxExtractedImagesTotalBytes}).");
        }

        return nextTotalBytes;
    }

    private static string ResolveDestinationPath(string extractionRoot, string extractionRootFullPath, string normalizedEntryPath)
    {
        var destinationPath = Path.GetFullPath(Path.Combine(extractionRoot, normalizedEntryPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!destinationPath.StartsWith(extractionRootFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Zip entry path escapes extraction root: '{normalizedEntryPath}'.");
        }

        return destinationPath;
    }

    private static void CopyZipEntryToFile(ZipArchiveEntry entry, string destinationPath)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException($"Unable to resolve destination directory for '{entry.FullName}'.");

        Directory.CreateDirectory(destinationDirectory);

        using var inputStream = entry.Open();
        using var outputStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        inputStream.CopyTo(outputStream);
    }

    private static void ValidateExtractedImages(
        string imagesDirectoryPath,
        IReadOnlyList<Badge> badges,
        AssetsMissingImagesPolicy missingImagesPolicy,
        ILogger logger)
    {
        if (!Directory.Exists(imagesDirectoryPath))
        {
            throw new InvalidDataException($"Extracted images directory does not exist: {imagesDirectoryPath}");
        }

        var availableFiles = new HashSet<string>(
            Directory
                .GetFiles(imagesDirectoryPath, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Where(fileName => !string.IsNullOrWhiteSpace(fileName))!,
            StringComparer.OrdinalIgnoreCase);

        var missingFiles = badges
            .Select(badge => badge.ImagePath)
            .Where(imagePath => !string.IsNullOrWhiteSpace(imagePath))
            .Select(imagePath => Path.GetFileName(imagePath.Replace('\\', '/')))
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .Select(fileName => fileName!)
            .Where(fileName => !availableFiles.Contains(fileName))
            .ToList();

        if (missingFiles.Count > 0)
        {
            var sample = string.Join(", ", missingFiles.Take(5));
            if (missingImagesPolicy == AssetsMissingImagesPolicy.Warn)
            {
                logger.LogWarning(
                    "Extracted assets validation warning. MissingImagesCount={MissingImagesCount}, Sample={MissingSample}",
                    missingFiles.Count,
                    sample);
                return;
            }

            throw new InvalidDataException(
                $"Extracted assets validation failed. Missing {missingFiles.Count} image files. Sample: {sample}");
        }
    }

    private static BadgesAssetsSnapshotInfo ToSnapshotInfo(AssetsSnapshotManifest manifest)
    {
        return new BadgesAssetsSnapshotInfo(
            ImagesDirectoryPath: manifest.ImagesDirectoryPath,
            SourceZipSha256: manifest.SourceZipSha256,
            ExtractedAtUtc: manifest.ExtractedAtUtc,
            ImagesCount: manifest.ImagesCount);
    }

    private static void ValidateSafeRelativePath(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal) ||
                                    string.Equals(segment, ".", StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Zip entry contains unsafe path segment: '{relativePath}'.");
        }

        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
        {
            throw new InvalidDataException($"Zip entry contains rooted or drive-qualified path: '{relativePath}'.");
        }
    }

    private static AssetsSnapshotManifest? LoadManifest(string assetsRoot, ILogger logger)
    {
        var manifestPath = Path.Combine(assetsRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<AssetsSnapshotManifest>(json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read assets manifest. A new snapshot will be extracted.");
            return null;
        }
    }

    private static void SaveManifest(string assetsRoot, AssetsSnapshotManifest manifest, ILogger logger)
    {
        var manifestPath = Path.Combine(assetsRoot, ManifestFileName);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(manifestPath, json);
        logger.LogDebug("Assets manifest updated. Path={ManifestPath}", manifestPath);
    }

    private static void CleanupOldSnapshots(string snapshotsRoot, string currentSnapshotPath, int retentionCount, ILogger logger)
    {
        if (!Directory.Exists(snapshotsRoot))
        {
            return;
        }

        var safeRetentionCount = Math.Max(1, retentionCount);

        var snapshotDirectories = new DirectoryInfo(snapshotsRoot)
            .EnumerateDirectories()
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .ToList();

        var keep = snapshotDirectories
            .Take(safeRetentionCount)
            .Select(directory => directory.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        keep.Add(Path.GetFullPath(currentSnapshotPath));

        foreach (var directoryPath in snapshotDirectories.Select(directory => directory.FullName))
        {
            if (keep.Contains(directoryPath))
            {
                continue;
            }

            TryDeleteDirectory(directoryPath, logger);
        }
    }

    private static void TryDeleteDirectory(string directoryPath, ILogger logger)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            Directory.Delete(directoryPath, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete temporary or old assets directory: {DirectoryPath}", directoryPath);
        }
    }

    private static string BuildExtractionMutexName(string assetsRoot)
    {
        var hash = ComputeSha256Hex(Encoding.UTF8.GetBytes(assetsRoot));
        return $"ProjectK.ProbeAndBadges.Assets.{hash[..16]}";
    }

    private static string ComputeSha256HexFromFile(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeSha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class AssetsSnapshotManifest
    {
        public string SourceZipPath { get; set; } = string.Empty;

        public string SourceZipSha256 { get; set; } = string.Empty;

        public string ParserVersion { get; set; } = string.Empty;

        public DateTimeOffset ExtractedAtUtc { get; set; }

        public string SnapshotDirectoryPath { get; set; } = string.Empty;

        public string ImagesDirectoryPath { get; set; } = string.Empty;

        public int ImagesCount { get; set; }
    }
}
