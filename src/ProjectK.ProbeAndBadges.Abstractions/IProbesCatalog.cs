namespace ProjectK.ProbeAndBadges.Abstractions;

public interface IProbesCatalog
{
    IReadOnlyList<Probe> GetAll();

    Probe? GetById(string id);

    IReadOnlyList<ProbePoint> GetPointsByProbeId(string probeId);

    IReadOnlyList<ProbeSection> GetSectionsByProbeId(string probeId);

    IReadOnlyList<ProbePoint> GetSectionPointsByProbeId(string probeId, string sectionKey);
}