using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ProjectK.ProbeAndBadges;

internal static class ReleaseDataZipAdapter
{
    private const string LatestReleaseCacheManifestFileName = "latest-release-cache.json";
    private static readonly HttpClient SharedHttpClient = new();

    static ReleaseDataZipAdapter()
    {
        SharedHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectK.ProbeAndBadges/1.0");
        SharedHttpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public sealed record LatestReleaseRequest(
        string LatestReleaseApiUrl,
        string AssetNamePattern,
        string EntryPath,
        TimeSpan Timeout,
        string LocalCacheDirectoryPath,
        bool EnableEtagCache,
        bool EnableZipCache,
        bool VerifyChecksum,
        bool RequireChecksumAsset);

    public static BadgesDataLoadResult ReadBadgesJsonFromFile(string zipPath, string entryPath, ILogger logger)
    {
        var fullZipPath = Path.GetFullPath(zipPath);
        logger.LogInformation("Loading badges json from local release zip: {ZipPath}", fullZipPath);

        if (!File.Exists(fullZipPath))
        {
            throw new FileNotFoundException($"Release data zip was not found: {fullZipPath}");
        }

        using var archive = ZipFile.OpenRead(fullZipPath);
        var json = ReadEntryContent(archive, fullZipPath, entryPath);
        var sha256 = ComputeSha256HexFromFile(fullZipPath);
        return new BadgesDataLoadResult(json, fullZipPath, sha256);
    }

    public static BadgesDataLoadResult ReadBadgesJsonFromUrl(
        string zipUrl,
        string entryPath,
        TimeSpan timeout,
        string localCacheDirectoryPath,
        bool persistDownloadedZip,
        int downloadedZipRetentionCount,
        ILogger logger)
    {
        if (!Uri.TryCreate(zipUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException($"ReleaseDataZipUrl must be an absolute http/https URL. Value: '{zipUrl}'");
        }

        logger.LogInformation("Downloading release zip from URL: {ZipUrl}, Timeout={Timeout}", zipUrl, timeout);

        using var cts = new CancellationTokenSource(timeout);
        using var response = SharedHttpClient
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .GetAwaiter()
            .GetResult();
        response.EnsureSuccessStatusCode();

        using var remoteStream = response.Content.ReadAsStream(cts.Token);
        using var zipBuffer = new MemoryStream();
        remoteStream.CopyTo(zipBuffer);
        var zipBytes = zipBuffer.ToArray();

        logger.LogDebug("Downloaded release zip bytes: {Bytes}", zipBytes.Length);

        var json = ReadEntryFromZipBytes(zipBytes, zipUrl, entryPath);
        var sha256 = ComputeSha256Hex(zipBytes);
        var sourceZipPath = persistDownloadedZip
            ? SaveDownloadedZip(localCacheDirectoryPath, sha256, zipBytes, downloadedZipRetentionCount, logger)
            : null;

        return new BadgesDataLoadResult(json, sourceZipPath, sha256);
    }

    public static BadgesDataLoadResult ReadBadgesJsonFromLatestRelease(
        LatestReleaseRequest request,
        ILogger logger)
    {
        var cacheManifest = LoadCacheManifest(request.LocalCacheDirectoryPath, logger);

        try
        {
            return ReadFromLatestReleaseCore(request, cacheManifest, logger);
        }
        catch (Exception ex) when (request.EnableZipCache && TryReadCachedZip(cacheManifest, request.EntryPath, logger, out var cachedResult))
        {
            logger.LogWarning(ex, "Latest release fetch failed. Loaded badges json from local cached release zip.");
            return cachedResult;
        }
    }

    private static BadgesDataLoadResult ReadFromLatestReleaseCore(
        LatestReleaseRequest request,
        LatestReleaseCacheManifest cacheManifest,
        ILogger logger)
    {
        var uri = ValidateHttpHttpsUri(request.LatestReleaseApiUrl, "LatestReleaseApiUrl");
        var normalizedPattern = NormalizeAssetNamePattern(request.AssetNamePattern);

        logger.LogInformation(
            "Resolving latest release data asset. ApiUrl={ApiUrl}, Pattern={Pattern}, EtagCache={EtagCache}, ZipCache={ZipCache}, VerifyChecksum={VerifyChecksum}",
            request.LatestReleaseApiUrl,
            normalizedPattern,
            request.EnableEtagCache,
            request.EnableZipCache,
            request.VerifyChecksum);

        using var response = SendLatestReleaseRequest(
            uri,
            request.EnableEtagCache ? cacheManifest.ETag : null,
            request.Timeout,
            logger);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return HandleNotModifiedResponse(cacheManifest, request.EntryPath, request.EnableZipCache, logger);
        }

        response.EnsureSuccessStatusCode();

        var resolved = ResolveAssetsFromApiResponse(response, normalizedPattern, request.Timeout);
        if (string.IsNullOrWhiteSpace(resolved.ZipAssetUrl) || string.IsNullOrWhiteSpace(resolved.ZipAssetName))
        {
            throw new InvalidDataException($"No release asset matched pattern '{normalizedPattern}' in latest release response.");
        }

        logger.LogInformation("Resolved latest release data asset: {AssetName}", resolved.ZipAssetName);

        var zipBytes = DownloadBytes(resolved.ZipAssetUrl, request.Timeout);
        var calculatedSha256 = ComputeSha256Hex(zipBytes);

        logger.LogDebug("Downloaded release data zip size={Bytes} bytes, SHA256={Sha256}", zipBytes.Length, calculatedSha256);

        VerifyReleaseChecksumIfNeeded(
            request.VerifyChecksum,
            request.RequireChecksumAsset,
            resolved.ChecksumAssetUrl,
            calculatedSha256,
            request.Timeout,
            logger);

        var json = ReadEntryFromZipBytes(zipBytes, resolved.ZipAssetUrl, request.EntryPath);

        if (request.EnableZipCache || request.EnableEtagCache)
        {
            return PersistLatestReleaseCache(
                request.LocalCacheDirectoryPath,
                response.Headers.ETag?.ToString(),
                resolved,
                zipBytes,
                calculatedSha256,
                json,
                logger);
        }

        return new BadgesDataLoadResult(json, null, calculatedSha256);
    }

    private static Uri ValidateHttpHttpsUri(string url, string optionName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException($"{optionName} must be an absolute http/https URL. Value: '{url}'");
        }

        return uri;
    }

    private static string NormalizeAssetNamePattern(string assetNamePattern)
    {
        return string.IsNullOrWhiteSpace(assetNamePattern)
            ? "plast-badges-data-*.zip"
            : assetNamePattern.Trim();
    }

    private static HttpResponseMessage SendLatestReleaseRequest(Uri uri, string? etag, TimeSpan timeout, ILogger logger)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        if (!string.IsNullOrWhiteSpace(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            logger.LogDebug("Sending If-None-Match with ETag={ETag}", etag);
        }

        return SharedHttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .GetAwaiter()
            .GetResult();
    }

    private static BadgesDataLoadResult HandleNotModifiedResponse(
        LatestReleaseCacheManifest cacheManifest,
        string entryPath,
        bool enableZipCache,
        ILogger logger)
    {
        logger.LogInformation("Latest release returned 304 Not Modified.");
        if (enableZipCache && TryReadCachedZip(cacheManifest, entryPath, logger, out var cachedResult))
        {
            logger.LogInformation("Loaded badges json from local cache after 304 response.");
            return cachedResult;
        }

        throw new InvalidDataException("Latest release API returned 304, but no cached zip snapshot is available.");
    }

    private static (string ZipAssetName, string ZipAssetUrl, string? ChecksumAssetUrl) ResolveAssetsFromApiResponse(
        HttpResponseMessage response,
        string normalizedPattern,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var stream = response.Content.ReadAsStream(cts.Token);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Latest release payload does not contain 'assets' array.");
        }

        return ResolveAssetsFromRelease(assetsElement, normalizedPattern);
    }

    private static void VerifyReleaseChecksumIfNeeded(
        bool verifyChecksum,
        bool requireChecksumAsset,
        string? checksumAssetUrl,
        string calculatedSha256,
        TimeSpan timeout,
        ILogger logger)
    {
        if (!verifyChecksum)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(checksumAssetUrl))
        {
            var checksumText = DownloadString(checksumAssetUrl, timeout);
            var expectedChecksum = TryParseChecksum(checksumText);
            if (string.IsNullOrWhiteSpace(expectedChecksum))
            {
                throw new InvalidDataException($"Checksum asset '{checksumAssetUrl}' does not contain a valid SHA-256 value.");
            }

            if (!string.Equals(expectedChecksum, calculatedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Downloaded release data zip failed SHA-256 checksum verification.");
            }

            logger.LogInformation("Release zip checksum verification passed.");
            return;
        }

        if (requireChecksumAsset)
        {
            throw new InvalidDataException("Checksum verification is required, but checksum asset was not found in latest release.");
        }

        logger.LogWarning("Checksum verification enabled but checksum asset was not found. Proceeding because checksum asset is optional.");
    }

    private static BadgesDataLoadResult PersistLatestReleaseCache(
        string localCacheDirectoryPath,
        string? etag,
        (string ZipAssetName, string ZipAssetUrl, string? ChecksumAssetUrl) resolved,
        byte[] zipBytes,
        string calculatedSha256,
        string json,
        ILogger logger)
    {
        Directory.CreateDirectory(localCacheDirectoryPath);

        var cachedZipFileName = string.IsNullOrWhiteSpace(resolved.ZipAssetName)
            ? "latest-data.zip"
            : resolved.ZipAssetName;
        var cachedZipPath = Path.Combine(localCacheDirectoryPath, cachedZipFileName);

        File.WriteAllBytes(cachedZipPath, zipBytes);

        var newManifest = new LatestReleaseCacheManifest
        {
            ETag = etag ?? string.Empty,
            ZipAssetName = resolved.ZipAssetName,
            ZipAssetUrl = resolved.ZipAssetUrl,
            ChecksumAssetUrl = resolved.ChecksumAssetUrl,
            CachedZipPath = cachedZipPath,
            CachedZipSha256 = calculatedSha256,
            CachedAtUtc = DateTimeOffset.UtcNow
        };

        SaveCacheManifest(localCacheDirectoryPath, newManifest, logger);
        logger.LogInformation("Stored latest release cache. CachedZipPath={CachedZipPath}", cachedZipPath);

        return new BadgesDataLoadResult(json, cachedZipPath, calculatedSha256);
    }

    private static (string ZipAssetName, string ZipAssetUrl, string? ChecksumAssetUrl) ResolveAssetsFromRelease(JsonElement assetsElement, string normalizedPattern)
    {
        var regex = WildcardToRegex(normalizedPattern);

        var assets = assetsElement
            .EnumerateArray()
            .Select(MapReleaseAsset)
            .ToArray();

        var zipAsset = assets
            .FirstOrDefault(asset => regex.IsMatch(asset.Name) && !string.IsNullOrWhiteSpace(asset.DownloadUrl));

        if (string.IsNullOrWhiteSpace(zipAsset.Name))
        {
            return (string.Empty, string.Empty, null);
        }

        var checksumCandidates = new[]
        {
            $"{zipAsset.Name}.sha256",
            $"{zipAsset.Name}.sha256.txt",
            $"{zipAsset.Name}.sha256sum"
        };

        var checksumAssetUrl = assets
            .Where(asset => checksumCandidates.Any(candidate => string.Equals(candidate, asset.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(asset => asset.DownloadUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

        return (zipAsset.Name, zipAsset.DownloadUrl, checksumAssetUrl);
    }

    private static ReleaseAssetInfo MapReleaseAsset(JsonElement asset)
    {
        var name = TryGetStringProperty(asset, "name");
        var downloadUrl = TryGetStringProperty(asset, "browser_download_url");
        return new ReleaseAssetInfo(name, downloadUrl);
    }

    private static string TryGetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement)
            ? propertyElement.GetString() ?? string.Empty
            : string.Empty;
    }

    private static Regex WildcardToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static byte[] DownloadBytes(string url, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var response = SharedHttpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .GetAwaiter()
            .GetResult();
        response.EnsureSuccessStatusCode();

        using var stream = response.Content.ReadAsStream(cts.Token);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string DownloadString(string url, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var response = SharedHttpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .GetAwaiter()
            .GetResult();
        response.EnsureSuccessStatusCode();

        using var stream = response.Content.ReadAsStream(cts.Token);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ReadEntryFromZipBytes(byte[] zipBytes, string source, string entryPath)
    {
        using var zipBuffer = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(zipBuffer, ZipArchiveMode.Read);
        return ReadEntryContent(archive, source, entryPath);
    }

    private static string ComputeSha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeSha256HexFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SaveDownloadedZip(
        string localCacheDirectoryPath,
        string sha256,
        byte[] zipBytes,
        int downloadedZipRetentionCount,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(localCacheDirectoryPath))
        {
            throw new InvalidDataException("LocalCacheDirectoryPath must be provided when persisting downloaded zip.");
        }

        Directory.CreateDirectory(localCacheDirectoryPath);
        var filePath = Path.Combine(localCacheDirectoryPath, $"downloaded-{sha256}.zip");

        if (!File.Exists(filePath))
        {
            File.WriteAllBytes(filePath, zipBytes);
            logger.LogInformation("Stored downloaded zip for local reuse. ZipPath={ZipPath}", filePath);
        }
        else
        {
            logger.LogDebug("Downloaded zip already exists in local cache. ZipPath={ZipPath}", filePath);
        }

        CleanupDownloadedZipCache(localCacheDirectoryPath, filePath, downloadedZipRetentionCount, logger);

        return filePath;
    }

    private static void CleanupDownloadedZipCache(
        string localCacheDirectoryPath,
        string activeZipPath,
        int downloadedZipRetentionCount,
        ILogger logger)
    {
        if (downloadedZipRetentionCount < 0)
        {
            throw new InvalidDataException("DownloadedZipRetentionCount must be zero or greater.");
        }

        if (!Directory.Exists(localCacheDirectoryPath))
        {
            return;
        }

        var normalizedActivePath = Path.GetFullPath(activeZipPath);
        var files = new DirectoryInfo(localCacheDirectoryPath)
            .EnumerateFiles("downloaded-*.zip", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        var keep = files
            .Take(downloadedZipRetentionCount)
            .Select(file => file.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        keep.Add(normalizedActivePath);

        foreach (var file in files)
        {
            if (keep.Contains(file.FullName))
            {
                continue;
            }

            try
            {
                file.Delete();
                logger.LogDebug("Deleted old downloaded zip cache file. ZipPath={ZipPath}", file.FullName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete old downloaded zip cache file. ZipPath={ZipPath}", file.FullName);
            }
        }
    }

    private static string? TryParseChecksum(string checksumText)
    {
        var match = Regex.Match(checksumText ?? string.Empty, "(?<sha>[0-9a-fA-F]{64})", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["sha"].Value.ToLowerInvariant() : null;
    }

    private static LatestReleaseCacheManifest LoadCacheManifest(string localCacheDirectoryPath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(localCacheDirectoryPath))
        {
            return new LatestReleaseCacheManifest();
        }

        var manifestPath = Path.Combine(localCacheDirectoryPath, LatestReleaseCacheManifestFileName);
        if (!File.Exists(manifestPath))
        {
            logger.LogDebug("Latest release cache manifest not found. Path={ManifestPath}", manifestPath);
            return new LatestReleaseCacheManifest();
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<LatestReleaseCacheManifest>(json) ?? new LatestReleaseCacheManifest();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read latest release cache manifest. Ignoring cache manifest and continuing.");
            return new LatestReleaseCacheManifest();
        }
    }

    private static void SaveCacheManifest(string localCacheDirectoryPath, LatestReleaseCacheManifest manifest, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(localCacheDirectoryPath))
        {
            return;
        }

        Directory.CreateDirectory(localCacheDirectoryPath);
        var manifestPath = Path.Combine(localCacheDirectoryPath, LatestReleaseCacheManifestFileName);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(manifestPath, json);
        logger.LogDebug("Latest release cache manifest updated. Path={ManifestPath}", manifestPath);
    }

    private static bool TryReadCachedZip(LatestReleaseCacheManifest manifest, string entryPath, ILogger logger, out BadgesDataLoadResult cachedResult)
    {
        cachedResult = new BadgesDataLoadResult(string.Empty, null, null);

        if (string.IsNullOrWhiteSpace(manifest.CachedZipPath) || !File.Exists(manifest.CachedZipPath))
        {
            logger.LogDebug("No cached release zip available for fallback.");
            return false;
        }

        var bytes = File.ReadAllBytes(manifest.CachedZipPath);
        if (!string.IsNullOrWhiteSpace(manifest.CachedZipSha256))
        {
            var currentSha = ComputeSha256Hex(bytes);
            if (!string.Equals(currentSha, manifest.CachedZipSha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Cached zip checksum mismatch. Cache path={CachePath}", manifest.CachedZipPath);
                return false;
            }
        }

        var badgesJson = ReadEntryFromZipBytes(bytes, manifest.CachedZipPath, entryPath);
        cachedResult = new BadgesDataLoadResult(badgesJson, manifest.CachedZipPath, manifest.CachedZipSha256);
        logger.LogInformation("Using cached release zip snapshot from {CachePath}", manifest.CachedZipPath);
        return true;
    }

    private sealed class LatestReleaseCacheManifest
    {
        public string ETag { get; set; } = string.Empty;

        public string ZipAssetName { get; set; } = string.Empty;

        public string ZipAssetUrl { get; set; } = string.Empty;

        public string? ChecksumAssetUrl { get; set; }

        public string CachedZipPath { get; set; } = string.Empty;

        public string CachedZipSha256 { get; set; } = string.Empty;

        public DateTimeOffset CachedAtUtc { get; set; }
    }

    private readonly record struct ReleaseAssetInfo(string Name, string DownloadUrl);

    private static string ReadEntryContent(ZipArchive archive, string source, string entryPath)
    {
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, entryPath, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            throw new InvalidDataException($"Entry '{entryPath}' was not found in release zip '{source}'.");
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
