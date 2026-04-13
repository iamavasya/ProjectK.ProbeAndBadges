namespace ProjectK.ProbeAndBadges.Abstractions;

public interface IBadgesCatalog
{
    BadgesMetadata Metadata { get; }

    IReadOnlyList<Badge> GetAll();

    Badge? GetById(string id);

    IReadOnlyList<Badge> Query(BadgeQuery query);
}
