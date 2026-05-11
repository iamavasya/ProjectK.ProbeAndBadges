namespace ProjectK.ProbeAndBadges.AspNetDemo;

internal sealed class BadgeSearchRequest
{
    public string? Text { get; init; }
    public string? Country { get; init; }
    public string? Specialization { get; init; }
    public string? Status { get; init; }
    public int? Level { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; } = 24;
}
