# Assets Runtime Flow

This document describes how badge image assets are resolved and extracted at runtime.

## End-to-end flow

1. Catalog resolves dataset JSON from one of the configured sources.
2. Loader returns BadgesDataLoadResult with:
   - BadgesJson
   - SourceZipPath (when available)
   - SourceZipSha256
3. If assets extraction is enabled and SourceZipPath exists:
   - extractor validates zip structure
   - images are extracted into snapshot storage
   - manifest is updated
   - old snapshots are cleaned by retention policy
4. Catalog exposes:
   - AssetsDirectoryPath
   - AssetsSnapshot

## SourceZipPath availability

SourceZipPath is currently available for:
- ReleaseDataZip (local zip file)
- Auto/latest release when zip cache is enabled (or forced for extraction)
- ReleaseDataZipUrl when persistDownloadedZip is enabled (internally enabled when local zip cache or assets extraction is enabled)

If SourceZipPath is not available, extraction is skipped.

## Extraction options

BadgesCatalogOptions controls runtime behavior:

- EnableAssetsExtraction (default: true)
- AssetsRootDirectory
- AssetsImagesDirectoryName (default: badges_images)
- AssetsSnapshotRetentionCount (default: 2)
- AssetExtractionLockTimeoutSeconds (default: 30)
- ValidateExtractedImagesAgainstCatalog (default: true)
- MissingImagesPolicy (Fail or Warn)
- MaxExtractedImageEntries (default: 5000)
- MaxExtractedSingleImageBytes (default: 10 MB)
- MaxExtractedImagesTotalBytes (default: 200 MB)
- DownloadedZipRetentionCount (default: 5)

## Safety guarantees

Extractor enforces:
- path traversal protection for zip entries
- rooted/drive-qualified path rejection
- extension whitelist for images (.svg, .png, .jpg, .jpeg, .webp)
- per-file and total extracted bytes limits
- max extracted entry count
- cross-process extraction lock

## Retention behavior

Two retention policies are active:

1. Assets snapshots retention
- keeps recent snapshots in assets root by AssetsSnapshotRetentionCount

2. Downloaded URL zip retention
- keeps recent downloaded-*.zip files in local cache by DownloadedZipRetentionCount
- always keeps the currently active downloaded zip file

## Host-facing contracts

For host integration, prefer reading:
- IBadgesAssetsStore.AssetsDirectoryPath
- IBadgesAssetsStore.AssetsSnapshot

The host can later map AssetsDirectoryPath to a public static URL path (for example /badges_images).

For end-to-end host registration and endpoint examples, see `docs/host-integration.md`.
