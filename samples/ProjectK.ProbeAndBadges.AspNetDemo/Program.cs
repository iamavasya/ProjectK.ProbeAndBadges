using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Http;
using ProjectK.ProbeAndBadges.AspNetDemo;
using ProjectK.ProbeAndBadges;
using ProjectK.ProbeAndBadges.Abstractions;
using ProjectK.ProbeAndBadges.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Zero-config registration: uses BadgesCatalogOptions defaults.
builder.Services.AddProbeAndBadgesApi();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var assetsStore = app.Services.GetRequiredService<IBadgesAssetsStore>();
if (assetsStore.HasAssets && !string.IsNullOrWhiteSpace(assetsStore.AssetsDirectoryPath))
{
    app.Logger.LogInformation(
        "Serving extracted badge images from {AssetsDirectoryPath} at request path /badges_images",
        assetsStore.AssetsDirectoryPath);

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(assetsStore.AssetsDirectoryPath),
        RequestPath = "/badges_images"
    });
}
else
{
    app.Logger.LogWarning(
        "Assets directory path is unavailable. Image paths may return 404 until extraction succeeds.");
}

app.MapGet("/api/meta", (IBadgesCatalog catalog, IBadgesAssetsStore store) =>
{
    return Results.Ok(new
    {
        catalog.Metadata,
        store.AssetsSnapshot,
        store.AssetsDirectoryPath,
        store.HasAssets
    });
});

app.MapGet("/api/badges", (IBadgesCatalog catalog, int take = 24) =>
{
    var safeTake = Math.Clamp(take, 1, 200);
    return Results.Ok(catalog.GetAll().Take(safeTake));
});

app.MapGet(
    "/api/search",
    (
        IBadgesCatalog catalog,
        [AsParameters] BadgeSearchRequest request) =>
    {
        var safeSkip = Math.Max(0, request.Skip);
        var safeTake = Math.Clamp(request.Take, 1, 200);

        var query = new BadgeQuery(
            Text: request.Text,
            Country: request.Country,
            Specialization: request.Specialization,
            Status: request.Status,
            Level: request.Level);

        var all = catalog.Query(query);
        var total = all.Count;
        var items = all.Skip(safeSkip).Take(safeTake).ToArray();

        return Results.Ok(new
        {
            total,
            skip = safeSkip,
            take = safeTake,
            items
        });
    });

app.MapGet("/api/filter-options", (IBadgesCatalog catalog) =>
{
    var all = catalog.GetAll();

    var countries = all
        .Select(b => b.Country)
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v)
        .ToArray();

    var specializations = all
        .Select(b => b.Specialization)
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v)
        .ToArray();

    var statuses = all
        .Select(b => b.Status)
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v)
        .ToArray();

    var levels = all
        .Where(b => b.Level.HasValue)
        .Select(b => b.Level!.Value)
        .Distinct()
        .OrderBy(v => v)
        .ToArray();

    return Results.Ok(new
    {
        countries,
        specializations,
        statuses,
        levels
    });
});

app.MapGet("/api/badges/{id}", (IBadgesCatalog catalog, string id) =>
{
    var badge = catalog.GetById(id);
    return badge is null ? Results.NotFound() : Results.Ok(badge);
});

app.MapGet("/api/probes", (IProbesCatalog catalog) =>
{
    var probes = catalog
        .GetAll()
        .Select(probe => new
        {
            probe.Id,
            probe.Title,
            pointsCount = probe.Points.Count,
            sectionsCount = probe.Sections?.Count ?? 0
        });

    return Results.Ok(probes);
});

app.MapGet("/api/probes/{probeId}/sections", (IProbesCatalog catalog, string probeId) =>
{
    var probe = catalog.GetById(probeId);
    if (probe is null)
    {
        return Results.NotFound();
    }

    var sections = catalog
        .GetSectionsByProbeId(probeId)
        .Select(section => new
        {
            section.Id,
            section.Code,
            section.Title,
            pointsCount = section.Points.Count
        });

    return Results.Ok(sections);
});

app.MapGet("/api/probes/{probeId}/grouped", (IProbesCatalog catalog, string probeId) =>
{
    var probe = catalog.GetById(probeId);
    if (probe is null)
    {
        return Results.NotFound();
    }

    var sections = catalog
        .GetSectionsByProbeId(probeId)
        .Select(section => new
        {
            section.Id,
            section.Code,
            section.Title,
            points = section.Points,
            pointsCount = section.Points.Count
        })
        .ToArray();

    return Results.Ok(new
    {
        probe.Id,
        probe.Title,
        pointsCount = probe.Points.Count,
        sectionsCount = sections.Length,
        sections
    });
});

app.MapGet("/api/probes/{probeId}/sections/{sectionKey}/points", (IProbesCatalog catalog, string probeId, string sectionKey) =>
{
    var probe = catalog.GetById(probeId);
    if (probe is null)
    {
        return Results.NotFound();
    }

    var sectionPoints = catalog.GetSectionPointsByProbeId(probeId, sectionKey);
    if (sectionPoints.Count == 0)
    {
        return Results.NotFound();
    }

    return Results.Ok(sectionPoints);
});

app.MapGet("/api/probes/{probeId}/points", (IProbesCatalog catalog, string probeId) =>
{
    var probe = catalog.GetById(probeId);
    if (probe is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(catalog.GetPointsByProbeId(probeId));
});

await app.RunAsync();
