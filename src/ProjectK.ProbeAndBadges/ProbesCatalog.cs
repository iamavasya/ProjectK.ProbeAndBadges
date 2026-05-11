using System.Text.Json;
using ProjectK.ProbeAndBadges.Abstractions;

namespace ProjectK.ProbeAndBadges;

public sealed class ProbesCatalog : IProbesCatalog
{
    private const string EmbeddedSeedResourceName = "ProjectK.ProbeAndBadges.Data.probes.seed.json";

    private readonly IReadOnlyList<Probe> _probes;
    private readonly Dictionary<string, Probe> _idIndex;

    public ProbesCatalog()
    {
        _probes = LoadFromEmbeddedSeed();
        _idIndex = _probes
            .ToDictionary(probe => probe.Id, probe => probe, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Probe> GetAll() => _probes;

    public Probe? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _idIndex.TryGetValue(id, out var probe) ? probe : null;
    }

    public IReadOnlyList<ProbePoint> GetPointsByProbeId(string probeId)
    {
        var probe = GetById(probeId);
        return probe?.Points ?? Array.Empty<ProbePoint>();
    }

    public IReadOnlyList<ProbeSection> GetSectionsByProbeId(string probeId)
    {
        var probe = GetById(probeId);
        return probe?.Sections ?? Array.Empty<ProbeSection>();
    }

    public IReadOnlyList<ProbePoint> GetSectionPointsByProbeId(string probeId, string sectionKey)
    {
        if (string.IsNullOrWhiteSpace(sectionKey))
        {
            return Array.Empty<ProbePoint>();
        }

        var section = GetSectionsByProbeId(probeId)
            .FirstOrDefault(item =>
                string.Equals(item.Id, sectionKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Code, sectionKey, StringComparison.OrdinalIgnoreCase));

        return section?.Points ?? Array.Empty<ProbePoint>();
    }

    private static IReadOnlyList<Probe> LoadFromEmbeddedSeed()
    {
        var assembly = typeof(ProbesCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedSeedResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{EmbeddedSeedResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var dto = JsonSerializer.Deserialize<ProbesExportDto>(json, serializerOptions)
            ?? throw new InvalidDataException("Unable to deserialize probes seed data.");

        return (dto.Probes ?? new List<ProbeDto>())
            .Select(MapProbe)
            .ToArray()
            .AsReadOnly();
    }

    private static Probe MapProbe(ProbeDto probe)
    {
        var sections = (probe.Sections ?? new List<ProbeSectionDto>())
            .Select(section => new ProbeSection(
                Id: section.Id ?? string.Empty,
                Code: section.Code ?? string.Empty,
                Title: section.Title ?? string.Empty,
                Points: (section.Points ?? new List<ProbePointDto>())
                    .Select(point => new ProbePoint(
                        Id: point.Id ?? string.Empty,
                        Title: point.Title ?? string.Empty))
                    .ToArray()
                    .AsReadOnly()))
            .ToArray()
            .AsReadOnly();

        var directPoints = (probe.Points ?? new List<ProbePointDto>())
            .Select(point => new ProbePoint(
                Id: point.Id ?? string.Empty,
                Title: point.Title ?? string.Empty))
            .ToList();

        if (directPoints.Count == 0)
        {
            foreach (var section in sections)
            {
                foreach (var sectionPoint in section.Points)
                {
                    directPoints.Add(sectionPoint);
                }
            }
        }

        return new Probe(
            Id: probe.Id ?? string.Empty,
            Title: probe.Title ?? string.Empty,
            Points: directPoints
                .ToArray()
                .AsReadOnly(),
            Sections: sections);
    }

    private sealed record ProbesExportDto(
        List<ProbeDto>? Probes);

    private sealed record ProbeDto(
        string? Id,
        string? Title,
        List<ProbePointDto>? Points,
        List<ProbeSectionDto>? Sections);

    private sealed record ProbeSectionDto(
        string? Id,
        string? Code,
        string? Title,
        List<ProbePointDto>? Points);

    private sealed record ProbePointDto(
        string? Id,
        string? Title);
}