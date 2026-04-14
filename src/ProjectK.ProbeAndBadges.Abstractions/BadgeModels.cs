namespace ProjectK.ProbeAndBadges.Abstractions;

public sealed record Badge(
    string Id,
    string Title,
    string ImagePath,
    string Country,
    string Specialization,
    string Status,
    int? Level,
    string LastUpdated,
    string SeekerRequirements,
    string InstructorRequirements,
    IReadOnlyList<string> FixNotes
);

public sealed record BadgesMetadata(
    string ParserVersion,
    string ToolAuthor,
    string ParserComment,
    DateTimeOffset? ParsedAtUtc,
    string SourceUrl,
    bool FixerEnabled,
    string FixerMode,
    int TotalBadges
);

public sealed record BadgeQuery(
    string? Text = null,
    string? Country = null,
    string? Specialization = null,
    string? Status = null,
    int? Level = null
);

public sealed record Probe(
    string Id,
    string Title,
    IReadOnlyList<ProbePoint> Points,
    IReadOnlyList<ProbeSection>? Sections = null
);

public sealed record ProbePoint(
    string Id,
    string Title
);

public sealed record ProbeSection(
    string Id,
    string Code,
    string Title,
    IReadOnlyList<ProbePoint> Points
);
