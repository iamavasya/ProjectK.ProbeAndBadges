using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectK.ProbeAndBadges.Abstractions;

namespace ProjectK.ProbeAndBadges;

public sealed class BadgesCatalog : IBadgesCatalog, IBadgesAssetsStore
{
    private const string EmbeddedSeedResourceName = "ProjectK.ProbeAndBadges.Data.badges.seed.json";

    private readonly IReadOnlyList<Badge> _badges;
    private readonly Dictionary<string, Badge> _idIndex;
    private readonly Dictionary<string, IReadOnlyList<Badge>> _countryIndex;
    private readonly Dictionary<string, IReadOnlyList<Badge>> _specializationIndex;
    private readonly Dictionary<string, IReadOnlyList<Badge>> _statusIndex;
    private readonly Dictionary<int, IReadOnlyList<Badge>> _levelIndex;
    private readonly ConcurrentDictionary<BadgeQueryCacheKey, IReadOnlyList<Badge>> _queryCache = new();
    private readonly bool _enableQueryCache;
    private readonly ILogger _logger;

    public BadgesMetadata Metadata { get; }

    public string? AssetsDirectoryPath { get; }

    public BadgesAssetsSnapshotInfo? AssetsSnapshot { get; }

    public bool HasAssets =>
        !string.IsNullOrWhiteSpace(AssetsDirectoryPath) &&
        Directory.Exists(AssetsDirectoryPath);

    public BadgesCatalog(BadgesCatalogOptions? options = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        var effectiveOptions = options ?? new BadgesCatalogOptions();

        _logger.LogInformation(
            "Initializing badges catalog. Source={DataSource}, QueryCache={EnableQueryCache}",
            effectiveOptions.DataSource,
            effectiveOptions.EnableQueryCache);

        var (meta, badges, assetsSnapshot) = LoadDataset(effectiveOptions, _logger);

        Metadata = meta;
        AssetsSnapshot = assetsSnapshot;
        AssetsDirectoryPath = assetsSnapshot?.ImagesDirectoryPath;
        _badges = badges.ToList().AsReadOnly();
        _idIndex = badges
            .GroupBy(b => b.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _countryIndex = BuildStringIndex(badges, b => b.Country);
        _specializationIndex = BuildStringIndex(badges, b => b.Specialization);
        _statusIndex = BuildStringIndex(badges, b => b.Status);
        _levelIndex = BuildLevelIndex(badges);
        _enableQueryCache = effectiveOptions.EnableQueryCache;

        _logger.LogInformation(
            "Badges catalog initialized. ParserVersion={ParserVersion}, TotalBadges={TotalBadges}, CountryIndex={CountryIndex}, SpecializationIndex={SpecializationIndex}, StatusIndex={StatusIndex}, LevelIndex={LevelIndex}, AssetsDirectoryPath={AssetsDirectoryPath}",
            Metadata.ParserVersion,
            _badges.Count,
            _countryIndex.Count,
            _specializationIndex.Count,
            _statusIndex.Count,
            _levelIndex.Count,
            AssetsDirectoryPath);
    }

    public IReadOnlyList<Badge> GetAll() => _badges;

    public Badge? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _idIndex.TryGetValue(id, out var badge) ? badge : null;
    }

    public IReadOnlyList<Badge> Query(BadgeQuery query)
    {
        var normalized = BadgeQueryCacheKey.From(query);

        if (_enableQueryCache && _queryCache.TryGetValue(normalized, out var cached))
        {
            _logger.LogDebug("Badges query cache hit. Query={@Query}, ResultCount={ResultCount}", normalized, cached.Count);
            return cached;
        }

        var candidates = GetCandidateSet(normalized);
        var filtered = candidates.Where(b =>
            MatchesText(b, normalized.Text) &&
            MatchesStringFilter(b.Country, normalized.Country) &&
            MatchesStringFilter(b.Specialization, normalized.Specialization) &&
            MatchesStringFilter(b.Status, normalized.Status) &&
            MatchesLevelFilter(b.Level, normalized.Level));

        var result = filtered.ToArray().AsReadOnly();

        if (_enableQueryCache)
        {
            _queryCache.TryAdd(normalized, result);
            _logger.LogDebug("Badges query cached. Query={@Query}, ResultCount={ResultCount}", normalized, result.Count);
        }

        _logger.LogDebug("Badges query evaluated. Query={@Query}, ResultCount={ResultCount}", normalized, result.Count);

        return result;
    }

    private IEnumerable<Badge> GetCandidateSet(BadgeQueryCacheKey query)
    {
        var candidates = new List<IReadOnlyList<Badge>>();

        if (query.Level.HasValue && _levelIndex.TryGetValue(query.Level.Value, out var levelSet))
        {
            candidates.Add(levelSet);
        }

        if (!string.IsNullOrWhiteSpace(query.Country) && _countryIndex.TryGetValue(query.Country, out var countrySet))
        {
            candidates.Add(countrySet);
        }

        if (!string.IsNullOrWhiteSpace(query.Specialization) && _specializationIndex.TryGetValue(query.Specialization, out var specializationSet))
        {
            candidates.Add(specializationSet);
        }

        if (!string.IsNullOrWhiteSpace(query.Status) && _statusIndex.TryGetValue(query.Status, out var statusSet))
        {
            candidates.Add(statusSet);
        }

        if (candidates.Count == 0)
        {
            return _badges;
        }

        return candidates.OrderBy(set => set.Count).First();
    }

    private static (BadgesMetadata Metadata, Badge[] Badges, BadgesAssetsSnapshotInfo? AssetsSnapshot) LoadDataset(BadgesCatalogOptions options, ILogger logger)
    {
        var loadResult = ReadJson(options, logger);
        var json = loadResult.BadgesJson;
        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("Badges", out _))
        {
            var export = JsonSerializer.Deserialize<BadgesExportDto>(json, serializerOptions)
                ?? throw new InvalidDataException("Unable to deserialize badges export object.");

            var badges = MapBadges(export.Badges ?? new List<BadgeDto>());
            var metadata = MapMetadata(export.Meta, badges.Length);
            var assetsSnapshot = BadgesAssetsExtractor.TryExtractAssets(loadResult, options, metadata, badges, logger);
            logger.LogInformation("Loaded badges from object format. TotalBadges={TotalBadges}, ParserVersion={ParserVersion}", badges.Length, metadata.ParserVersion);
            return (metadata, badges, assetsSnapshot);
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            var legacyBadges = JsonSerializer.Deserialize<List<BadgeDto>>(json, serializerOptions) ?? new List<BadgeDto>();
            var badges = MapBadges(legacyBadges);
            var metadata = new BadgesMetadata(
                ParserVersion: "legacy",
                ToolAuthor: "unknown",
                ParserComment: "Loaded from legacy badges array format.",
                ParsedAtUtc: null,
                SourceUrl: string.Empty,
                FixerEnabled: false,
                FixerMode: "unknown",
                TotalBadges: badges.Length
            );

            var assetsSnapshot = BadgesAssetsExtractor.TryExtractAssets(loadResult, options, metadata, badges, logger);

            logger.LogWarning("Loaded badges from legacy array format. TotalBadges={TotalBadges}", badges.Length);

            return (metadata, badges, assetsSnapshot);
        }

        throw new InvalidDataException("Unsupported badges json format. Expected object with Badges or array.");
    }

    private static BadgesDataLoadResult ReadJson(BadgesCatalogOptions options, ILogger logger)
    {
        return options.DataSource switch
        {
            BadgesDataSource.Auto => ReadJsonAuto(options, logger),
            BadgesDataSource.EmbeddedSeed => ReadEmbeddedJson(logger),
            BadgesDataSource.JsonFile => ReadJsonFromFile(options.JsonFilePath, logger),
            BadgesDataSource.ReleaseDataZip => ReadJsonFromZip(options.ReleaseDataZipPath, options.ReleaseDataZipEntryPath, logger),
            BadgesDataSource.ReleaseDataZipUrl => ReadJsonFromZipUrl(
                options.ReleaseDataZipUrl,
                options.ReleaseDataZipEntryPath,
                options.RemoteRequestTimeoutSeconds,
                options.LocalCacheDirectoryPath,
                options.EnableLocalReleaseZipCache,
                options.EnableAssetsExtraction,
                options.DownloadedZipRetentionCount,
                logger),
            _ => throw new InvalidOperationException($"Unsupported data source: {options.DataSource}")
        };
    }

    private static BadgesDataLoadResult ReadJsonAuto(BadgesCatalogOptions options, ILogger logger)
    {
        var safeTimeout = options.RemoteRequestTimeoutSeconds > 0 ? options.RemoteRequestTimeoutSeconds : 30;

        logger.LogInformation(
            "Auto data source mode started. HasDirectZipUrl={HasDirectZipUrl}, LatestReleaseApiUrl={LatestReleaseApiUrl}, TimeoutSeconds={TimeoutSeconds}",
            !string.IsNullOrWhiteSpace(options.ReleaseDataZipUrl),
            options.LatestReleaseApiUrl,
            safeTimeout);

        try
        {
            if (!string.IsNullOrWhiteSpace(options.ReleaseDataZipUrl))
            {
                logger.LogInformation("Auto mode selected direct release zip URL.");
                return ReadJsonFromZipUrl(
                    options.ReleaseDataZipUrl,
                    options.ReleaseDataZipEntryPath,
                    safeTimeout,
                    options.LocalCacheDirectoryPath,
                    options.EnableLocalReleaseZipCache,
                    options.EnableAssetsExtraction,
                    options.DownloadedZipRetentionCount,
                    logger);
            }

            return ReadJsonFromLatestReleaseApi(
                options.LatestReleaseApiUrl,
                options.LatestReleaseAssetNamePattern,
                options.ReleaseDataZipEntryPath,
                safeTimeout,
                options.LocalCacheDirectoryPath,
                options.EnableLatestReleaseEtagCache,
                options.EnableLocalReleaseZipCache || options.EnableAssetsExtraction,
                options.VerifyReleaseZipChecksum,
                options.RequireReleaseZipChecksumAsset,
                logger);
        }
        catch (Exception ex) when (options.AutoFallbackToEmbeddedSeed)
        {
            logger.LogWarning(ex, "Auto mode failed. Falling back to embedded seed dataset.");
            return ReadEmbeddedJson(logger);
        }
        catch when (options.AutoFallbackToEmbeddedSeed)
        {
            return ReadEmbeddedJson(logger);
        }
    }

    private static BadgesDataLoadResult ReadEmbeddedJson(ILogger logger)
    {
        logger.LogInformation("Loading embedded seed badges dataset.");
        var assembly = typeof(BadgesCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedSeedResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{EmbeddedSeedResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return new BadgesDataLoadResult(reader.ReadToEnd(), null, null);
    }

    private static BadgesDataLoadResult ReadJsonFromFile(string? jsonFilePath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(jsonFilePath))
        {
            throw new InvalidDataException("JsonFilePath must be provided when DataSource is JsonFile.");
        }

        var fullPath = Path.GetFullPath(jsonFilePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Badges json file not found: {fullPath}");
        }

        logger.LogInformation("Loading badges dataset from json file: {JsonFilePath}", fullPath);

        return new BadgesDataLoadResult(File.ReadAllText(fullPath), null, null);
    }

    private static BadgesDataLoadResult ReadJsonFromZip(string? releaseZipPath, string releaseZipEntryPath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(releaseZipPath))
        {
            throw new InvalidDataException("ReleaseDataZipPath must be provided when DataSource is ReleaseDataZip.");
        }

        var entryPath = string.IsNullOrWhiteSpace(releaseZipEntryPath)
            ? "badges.json"
            : releaseZipEntryPath;

        return ReleaseDataZipAdapter.ReadBadgesJsonFromFile(releaseZipPath, entryPath, logger);
    }

    private static BadgesDataLoadResult ReadJsonFromZipUrl(
        string? releaseZipUrl,
        string releaseZipEntryPath,
        int timeoutSeconds,
        string localCacheDirectoryPath,
        bool enableZipCache,
        bool enableAssetsExtraction,
        int downloadedZipRetentionCount,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(releaseZipUrl))
        {
            throw new InvalidDataException("ReleaseDataZipUrl must be provided when DataSource is ReleaseDataZipUrl.");
        }

        var entryPath = string.IsNullOrWhiteSpace(releaseZipEntryPath)
            ? "badges.json"
            : releaseZipEntryPath;

        var safeTimeout = timeoutSeconds > 0 ? timeoutSeconds : 30;
        var shouldPersistDownloadedZip = enableZipCache || enableAssetsExtraction;

        return ReleaseDataZipAdapter.ReadBadgesJsonFromUrl(
            releaseZipUrl,
            entryPath,
            TimeSpan.FromSeconds(safeTimeout),
            localCacheDirectoryPath,
            shouldPersistDownloadedZip,
            downloadedZipRetentionCount,
            logger);
    }

    private static BadgesDataLoadResult ReadJsonFromLatestReleaseApi(
        string latestReleaseApiUrl,
        string latestReleaseAssetNamePattern,
        string releaseZipEntryPath,
        int timeoutSeconds,
        string localCacheDirectoryPath,
        bool enableEtagCache,
        bool enableZipCache,
        bool verifyChecksum,
        bool requireChecksumAsset,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(latestReleaseApiUrl))
        {
            throw new InvalidDataException("LatestReleaseApiUrl must be provided for automatic data source mode.");
        }

        var entryPath = string.IsNullOrWhiteSpace(releaseZipEntryPath)
            ? "badges.json"
            : releaseZipEntryPath;

        var safeTimeout = timeoutSeconds > 0 ? timeoutSeconds : 30;

        return ReleaseDataZipAdapter.ReadBadgesJsonFromLatestRelease(
            latestReleaseApiUrl,
            latestReleaseAssetNamePattern,
            entryPath,
            TimeSpan.FromSeconds(safeTimeout),
            localCacheDirectoryPath,
            enableEtagCache,
            enableZipCache,
            verifyChecksum,
                requireChecksumAsset,
                logger);
    }

    private static Badge[] MapBadges(List<BadgeDto> badges)
    {
        return badges.Select(b => new Badge(
                Id: b.Id ?? string.Empty,
                Title: b.Title ?? string.Empty,
                ImagePath: b.ImagePath ?? string.Empty,
                Country: b.Country ?? string.Empty,
                Specialization: b.Specialization ?? string.Empty,
                Status: b.Status ?? string.Empty,
                Level: b.Level,
                LastUpdated: b.LastUpdated ?? string.Empty,
                SeekerRequirements: b.SeekerRequirements ?? string.Empty,
                InstructorRequirements: b.InstructorRequirements ?? string.Empty,
                FixNotes: (b.FixNotes ?? new List<string>()).AsReadOnly())
            )
            .ToArray();
    }

    private static BadgesMetadata MapMetadata(MetaDto? meta, int fallbackCount)
    {
        DateTimeOffset? parsedAt = null;
        if (!string.IsNullOrWhiteSpace(meta?.ParsedAtUtc) && DateTimeOffset.TryParse(meta.ParsedAtUtc, out var parsed))
        {
            parsedAt = parsed;
        }

        return new BadgesMetadata(
            ParserVersion: meta?.ParserVersion ?? "unknown",
            ToolAuthor: meta?.ToolAuthor ?? "unknown",
            ParserComment: meta?.ParserComment ?? string.Empty,
            ParsedAtUtc: parsedAt,
            SourceUrl: meta?.SourceUrl ?? string.Empty,
            FixerEnabled: meta?.FixerEnabled ?? false,
            FixerMode: meta?.FixerMode ?? "unknown",
            TotalBadges: meta?.TotalBadges ?? fallbackCount
        );
    }

    private static Dictionary<string, IReadOnlyList<Badge>> BuildStringIndex(
        IEnumerable<Badge> badges,
        Func<Badge, string> selector)
    {
        return badges
            .Where(b => !string.IsNullOrWhiteSpace(selector(b)))
            .GroupBy(b => selector(b), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Badge>)g.ToArray().AsReadOnly(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<int, IReadOnlyList<Badge>> BuildLevelIndex(IEnumerable<Badge> badges)
    {
        return badges
            .Where(b => b.Level.HasValue)
            .GroupBy(b => b.Level!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Badge>)g.ToArray().AsReadOnly());
    }

    private static bool MatchesText(Badge badge, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return ContainsIgnoreCase(badge.Id, text) ||
               ContainsIgnoreCase(badge.Title, text) ||
               ContainsIgnoreCase(badge.Country, text) ||
               ContainsIgnoreCase(badge.Specialization, text) ||
               ContainsIgnoreCase(badge.Status, text);
    }

    private static bool MatchesStringFilter(string value, string filter)
    {
        return string.IsNullOrWhiteSpace(filter) || string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesLevelFilter(int? value, int? filter)
    {
        return !filter.HasValue || value == filter.Value;
    }

    private static bool ContainsIgnoreCase(string source, string term)
    {
        return source.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct BadgeQueryCacheKey(
        string Text,
        string Country,
        string Specialization,
        string Status,
        int? Level)
    {
        public static BadgeQueryCacheKey From(BadgeQuery query)
        {
            return new BadgeQueryCacheKey(
                Normalize(query.Text),
                Normalize(query.Country),
                Normalize(query.Specialization),
                Normalize(query.Status),
                query.Level);
        }

        private static string Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToLowerInvariant();
        }
    }

    private sealed class BadgesExportDto
    {
        public MetaDto? Meta { get; set; }

        public List<BadgeDto>? Badges { get; set; }
    }

    private sealed class MetaDto
    {
        public string? ParserVersion { get; set; }

        public string? ToolAuthor { get; set; }

        public string? ParserComment { get; set; }

        public string? ParsedAtUtc { get; set; }

        public string? SourceUrl { get; set; }

        public bool? FixerEnabled { get; set; }

        public string? FixerMode { get; set; }

        public int? TotalBadges { get; set; }
    }

    private sealed class BadgeDto
    {
        public string? Id { get; set; }

        public string? Title { get; set; }

        public string? ImagePath { get; set; }

        public string? Country { get; set; }

        public string? Specialization { get; set; }

        public string? Status { get; set; }

        public int? Level { get; set; }

        public string? LastUpdated { get; set; }

        public string? SeekerRequirements { get; set; }

        public string? InstructorRequirements { get; set; }

        public List<string>? FixNotes { get; set; }
    }
}
