namespace ProjectK.ProbeAndBadges.Abstractions;

public sealed record BadgesAssetsSnapshotInfo(
    string ImagesDirectoryPath,
    string SourceZipSha256,
    DateTimeOffset ExtractedAtUtc,
    int ImagesCount
);
