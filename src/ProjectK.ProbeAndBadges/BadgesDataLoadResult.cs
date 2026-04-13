namespace ProjectK.ProbeAndBadges;

internal sealed record BadgesDataLoadResult(
    string BadgesJson,
    string? SourceZipPath,
    string? SourceZipSha256);
