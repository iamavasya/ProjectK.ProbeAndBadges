namespace ProjectK.ProbeAndBadges.Abstractions;

public interface IBadgesAssetsStore
{
    string? AssetsDirectoryPath { get; }

    bool HasAssets { get; }

    BadgesAssetsSnapshotInfo? AssetsSnapshot { get; }
}
