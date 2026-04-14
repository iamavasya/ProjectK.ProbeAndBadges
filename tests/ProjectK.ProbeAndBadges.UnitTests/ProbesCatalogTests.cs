using ProjectK.ProbeAndBadges.Abstractions;

namespace ProjectK.ProbeAndBadges.UnitTests;

public class ProbesCatalogTests
{
    [Fact]
    public void GetAll_ReturnsThreeProbes_FromSeed()
    {
        IProbesCatalog sut = new ProbesCatalog();

        var all = sut.GetAll();

        Assert.Equal(3, all.Count);
        Assert.Contains(all, probe => probe.Id == "probe-1");
        Assert.Contains(all, probe => probe.Id == "probe-2");
        Assert.Contains(all, probe => probe.Id == "probe-3");
    }

    [Fact]
    public void GetPointsByProbeId_ReturnsPoints_ForFirstProbe()
    {
        IProbesCatalog sut = new ProbesCatalog();

        var points = sut.GetPointsByProbeId("probe-1");

        Assert.True(points.Count >= 40);
        Assert.Contains(points, point => point.Title.Contains("Три Головні Обов’язки Пластуна", StringComparison.Ordinal));
        Assert.Contains(points, point => point.Title.Contains("принципи інформаційної безпеки", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetPointsByProbeId_ReturnsPoints_ForSecondProbe()
    {
        IProbesCatalog sut = new ProbesCatalog();

        var points = sut.GetPointsByProbeId("probe-2");

        Assert.True(points.Count >= 35);
        Assert.Contains(points, point => point.Title.Contains("Пластовий закон", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(points, point => point.Title.Contains("долікарської медичної допомоги", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetSectionsByProbeId_ReturnsSections_ForSecondProbe()
    {
        IProbesCatalog sut = new ProbesCatalog();

        var sections = sut.GetSectionsByProbeId("probe-2");

        Assert.NotEmpty(sections);
        Assert.Contains(sections, section => section.Code == "А");
        Assert.Contains(sections, section => section.Code == "Г");
        Assert.Contains(sections, section => section.Code == "Ґ");
    }

    [Fact]
    public void GetSectionPointsByProbeId_ReturnsPoints_ForSectionCode()
    {
        IProbesCatalog sut = new ProbesCatalog();

        var points = sut.GetSectionPointsByProbeId("probe-2", "Г");

        Assert.NotEmpty(points);
        Assert.Contains(points, point => point.Title.Contains("екологічну ситуацію", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetSectionPointsByProbeId_ReturnsPoints_ForSectionId()
    {
        IProbesCatalog sut = new ProbesCatalog();

        var points = sut.GetSectionPointsByProbeId("probe-2", "probe-2-section-ghe");

        Assert.NotEmpty(points);
        Assert.Contains(points, point => point.Title.Contains("долікарської медичної", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetSectionPointsByProbeId_ReturnsEmpty_ForUnknownSection()
    {
        IProbesCatalog sut = new ProbesCatalog();

        var points = sut.GetSectionPointsByProbeId("probe-2", "unknown-section");

        Assert.Empty(points);
    }

    [Fact]
    public void GetPointsByProbeId_ReturnsEmpty_ForThirdProbeWithoutPoints()
    {
        IProbesCatalog sut = new ProbesCatalog();

        var points = sut.GetPointsByProbeId("probe-3");

        Assert.Empty(points);
    }

    [Fact]
    public void GetPointsByProbeId_ReturnsEmpty_ForUnknownProbe()
    {
        IProbesCatalog sut = new ProbesCatalog();

        var points = sut.GetPointsByProbeId("unknown");

        Assert.Empty(points);
    }
}