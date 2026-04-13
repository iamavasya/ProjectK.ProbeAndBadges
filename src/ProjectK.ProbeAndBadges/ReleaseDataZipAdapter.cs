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
        string latestReleaseApiUrl,
        string assetNamePattern,
        string entryPath,
        TimeSpan timeout,
        string localCacheDirectoryPath,
        bool enableEtagCache,
        bool enableZipCache,
        bool verifyChecksum,
        bool requireChecksumAsset,
        ILogger logger)
    {
        var cacheManifest = LoadCacheManifest(localCacheDirectoryPath, logger);

        try
        {
            return ReadFromLatestReleaseCore(
                latestReleaseApiUrl,
                assetNamePattern,
                entryPath,
                timeout,
                localCacheDirectoryPath,
                enableEtagCache,
                enableZipCache,
                verifyChecksum,
                requireChecksumAsset,
                cacheManifest,
                logger);
        }
        catch (Exception ex) when (enableZipCache && TryReadCachedZip(cacheManifest, entryPath, logger, out var cachedResult))
        {
            logger.LogWarning(ex, "Latest release fetch failed. Loaded badges json from local cached release zip.");
            return cachedResult;
        }
    }

    private static BadgesDataLoadResult ReadFromLatestReleaseCore(
        string latestReleaseApiUrl,
        string assetNamePattern,
        string entryPath,
        TimeSpan timeout,
        string localCacheDirectoryPath,
        bool enableEtagCache,
        bool enableZipCache,
        bool verifyChecksum,
        bool requireChecksumAsset,
        LatestReleaseCacheManifest cacheManifest,
        ILogger logger)
    {
        if (!Uri.TryCreate(latestReleaseApiUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException($"LatestReleaseApiUrl must be an absolute http/https URL. Value: '{latestReleaseApiUrl}'");
        }

        var normalizedPattern = string.IsNullOrWhiteSpace(assetNamePattern)
            ? "plast-badges-data-*.zip"
            : assetNamePattern.Trim();

        logger.LogInformation(
            "Resolving latest release data asset. ApiUrl={ApiUrl}, Pattern={Pattern}, EtagCache={EtagCache}, ZipCache={ZipCache}, VerifyChecksum={VerifyChecksum}",
            latestReleaseApiUrl,
            normalizedPattern,
            enableEtagCache,
            enableZipCache,
            verifyChecksum);

        using var cts = new CancellationTokenSource(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (enableEtagCache && !string.IsNullOrWhiteSpace(cacheManifest.ETag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cacheManifest.ETag);
            logger.LogDebug("Sending If-None-Match with ETag={ETag}", cacheManifest.ETag);
        }

        using var response = SharedHttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .GetAwaiter()
            .GetResult();

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            logger.LogInformation("Latest release returned 304 Not Modified.");
            if (enableZipCache && TryReadCachedZip(cacheManifest, entryPath, logger, out var cachedResult))
            {
                logger.LogInformation("Loaded badges json from local cache after 304 response.");
                return cachedResult;
            }

            throw new InvalidDataException("Latest release API returned 304, but no cached zip snapshot is available.");
        }

        response.EnsureSuccessStatusCode();

        using var stream = response.Content.ReadAsStream(cts.Token);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Latest release payload does not contain 'assets' array.");
        }

        var resolved = ResolveAssetsFromRelease(assetsElement, normalizedPattern);
        if (string.IsNullOrWhiteSpace(resolved.ZipAssetUrl) || string.IsNullOrWhiteSpace(resolved.ZipAssetName))
        {
            throw new InvalidDataException($"No release asset matched pattern '{normalizedPattern}' in latest release response.");
        }

        logger.LogInformation("Resolved latest release data asset: {AssetName}", resolved.ZipAssetName);

        var zipBytes = DownloadBytes(resolved.ZipAssetUrl, timeout);
        var calculatedSha256 = ComputeSha256Hex(zipBytes);

        logger.LogDebug("Downloaded release data zip size={Bytes} bytes, SHA256={Sha256}", zipBytes.Length, calculatedSha256);

        if (verifyChecksum)
        {
            if (!string.IsNullOrWhiteSpace(resolved.ChecksumAssetUrl))
            {
                var checksumText = DownloadString(resolved.ChecksumAssetUrl, timeout);
                var expectedChecksum = TryParseChecksum(checksumText);
                if (string.IsNullOrWhiteSpace(expectedChecksum))
                {
                    throw new InvalidDataException($"Checksum asset '{resolved.ChecksumAssetUrl}' does not contain a valid SHA-256 value.");
                }

                if (!string.Equals(expectedChecksum, calculatedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Downloaded release data zip failed SHA-256 checksum verification.");
                }

                logger.LogInformation("Release zip checksum verification passed.");
            }
            else if (requireChecksumAsset)
            {
                throw new InvalidDataException("Checksum verification is required, but checksum asset was not found in latest release.");
            }
            else
            {
                logger.LogWarning("Checksum verification enabled but checksum asset was not found. Proceeding because checksum asset is optional.");
            }
        }

        var json = ReadEntryFromZipBytes(zipBytes, resolved.ZipAssetUrl, entryPath);

        if (enableZipCache || enableEtagCache)
        {
            Directory.CreateDirectory(localCacheDirectoryPath);
            var cachedZipFileName = string.IsNullOrWhiteSpace(resolved.ZipAssetName)
                ? "latest-data.zip"
                : resolved.ZipAssetName;
            var cachedZipPath = Path.Combine(localCacheDirectoryPath, cachedZipFileName);

            File.WriteAllBytes(cachedZipPath, zipBytes);

            var etag = response.Headers.ETag?.ToString() ?? string.Empty;
            var newManifest = new LatestReleaseCacheManifest
            {
                ETag = etag,
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

        return new BadgesDataLoadResult(json, null, calculatedSha256);
    }

    private static (string ZipAssetName, string ZipAssetUrl, string? ChecksumAssetUrl) ResolveAssetsFromRelease(JsonElement assetsElement, string normalizedPattern)
    {
        var regex = WildcardToRegex(normalizedPattern);
        string zipAssetName = string.Empty;
        string zipAssetUrl = string.Empty;
        string? checksumAssetUrl = null;

        foreach (var asset in assetsElement.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

            if (!regex.IsMatch(name))
            {
                continue;
            }

            var browserDownloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString() ?? string.Empty
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(browserDownloadUrl))
            {
                zipAssetName = name;
                zipAssetUrl = browserDownloadUrl;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(zipAssetName))
        {
            return (string.Empty, string.Empty, null);
        }

        var checksumCandidates = new[]
        {
            $"{zipAssetName}.sha256",
            $"{zipAssetName}.sha256.txt",
            $"{zipAssetName}.sha256sum"
        };

        foreach (var asset in assetsElement.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

            if (!checksumCandidates.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var browserDownloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString() ?? string.Empty
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(browserDownloadUrl))
            {
                checksumAssetUrl = browserDownloadUrl;
                break;
            }
        }

        return (zipAssetName, zipAssetUrl, checksumAssetUrl);
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
