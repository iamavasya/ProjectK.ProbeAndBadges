namespace ProjectK.ProbeAndBadges;

public enum BadgesDataSource
{
    Auto,
    EmbeddedSeed,
    JsonFile,
    ReleaseDataZip,
    ReleaseDataZipUrl
}

public enum AssetsMissingImagesPolicy
{
    Fail,
    Warn
}

public sealed class BadgesCatalogOptions
{
    // Auto mode tries to load the latest release data zip from GitHub first.
    // If it fails and AutoFallbackToEmbeddedSeed is true, embedded seed data is used.
    public BadgesDataSource DataSource { get; set; } = BadgesDataSource.Auto;

    // Used when DataSource = JsonFile.
    public string? JsonFilePath { get; set; }

    // Used when DataSource = ReleaseDataZip.
    public string? ReleaseDataZipPath { get; set; }

    // Used when DataSource = ReleaseDataZipUrl.
    public string? ReleaseDataZipUrl { get; set; }

    // Used when DataSource = Auto and ReleaseDataZipUrl is not set.
    public string LatestReleaseApiUrl { get; set; } = "https://api.github.com/repos/iamavasya/PlastBadgesParser/releases/latest";

    // Wildcard pattern for selecting release data asset in latest release.
    public string LatestReleaseAssetNamePattern { get; set; } = "plast-badges-data-*.zip";

    // Local disk cache for latest release metadata and downloaded zip.
    public string LocalCacheDirectoryPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectK.ProbeAndBadges", "cache");

    public bool EnableLatestReleaseEtagCache { get; set; } = true;

    public bool EnableLocalReleaseZipCache { get; set; } = true;

    // Entry path inside release zip archive.
    public string ReleaseDataZipEntryPath { get; set; } = "badges.json";

    public int RemoteRequestTimeoutSeconds { get; set; } = 30;

    // If true, tries to verify zip with checksum asset from release (if present).
    public bool VerifyReleaseZipChecksum { get; set; } = true;

    // If true and checksum asset is missing, loading fails.
    public bool RequireReleaseZipChecksumAsset { get; set; } = false;

    public bool AutoFallbackToEmbeddedSeed { get; set; } = true;

    // If true, extracts badge images from the resolved zip source into local assets snapshot storage.
    public bool EnableAssetsExtraction { get; set; } = true;

    // Local storage for extracted assets snapshots.
    public string AssetsRootDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectK.ProbeAndBadges", "assets");

    // Folder name expected inside zip and exposed later by host application.
    public string AssetsImagesDirectoryName { get; set; } = "badges_images";

    // Number of extracted snapshots to keep on disk.
    public int AssetsSnapshotRetentionCount { get; set; } = 2;

    // Max wait time for cross-process extraction lock.
    public int AssetExtractionLockTimeoutSeconds { get; set; } = 30;

    // If true, validates that all non-empty badge ImagePath entries resolve to extracted files.
    public bool ValidateExtractedImagesAgainstCatalog { get; set; } = true;

    // Controls behavior when image files referenced by badges are missing in extracted snapshot.
    public AssetsMissingImagesPolicy MissingImagesPolicy { get; set; } = AssetsMissingImagesPolicy.Fail;

    // Maximum count of image files allowed to be extracted from zip.
    public int MaxExtractedImageEntries { get; set; } = 5000;

    // Maximum allowed uncompressed bytes for a single extracted image file.
    public long MaxExtractedSingleImageBytes { get; set; } = 10L * 1024 * 1024;

    // Maximum allowed total uncompressed bytes across all extracted image files.
    public long MaxExtractedImagesTotalBytes { get; set; } = 200L * 1024 * 1024;

    // Number of persisted downloaded-*.zip files to keep in local cache.
    public int DownloadedZipRetentionCount { get; set; } = 5;

    public bool EnableQueryCache { get; set; } = true;
}
