# Host Integration Guide

This document shows how to integrate the library in an ASP.NET host.

## 1. Register Services (Zero-Config)

Use defaults from `BadgesCatalogOptions`:

```csharp
using ProjectK.ProbeAndBadges.DependencyInjection;

builder.Services.AddProbeAndBadgesApi();
```

## 2. Register Services (Custom Options)

Override only what you need:

```csharp
using ProjectK.ProbeAndBadges;
using ProjectK.ProbeAndBadges.DependencyInjection;

builder.Services.AddProbeAndBadgesApi(options =>
{
    options.DataSource = BadgesDataSource.Auto;
    options.EnableAssetsExtraction = true;
    options.MissingImagesPolicy = AssetsMissingImagesPolicy.Warn;
    options.EnableLocalReleaseZipCache = true;
    options.EnableLatestReleaseEtagCache = true;
    options.DownloadedZipRetentionCount = 5;
});
```

## 3. Map Extracted Images to Public Path

Use `IBadgesAssetsStore` to expose extracted images as static files.

```csharp
using Microsoft.Extensions.FileProviders;
using ProjectK.ProbeAndBadges.Abstractions;

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var assetsStore = app.Services.GetRequiredService<IBadgesAssetsStore>();
if (assetsStore.HasAssets && !string.IsNullOrWhiteSpace(assetsStore.AssetsDirectoryPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(assetsStore.AssetsDirectoryPath),
        RequestPath = "/badges_images"
    });
}
```

This makes `ImagePath` values like `/badges_images/my-badge.svg` available for frontend rendering.

## 4. Use Catalog in Endpoints

### Minimal API

```csharp
using ProjectK.ProbeAndBadges.Abstractions;

app.MapGet("/api/meta", (IBadgesCatalog catalog, IBadgesAssetsStore assets) => Results.Ok(new
{
    catalog.Metadata,
    assets.AssetsSnapshot,
    assets.AssetsDirectoryPath,
    assets.HasAssets
}));

app.MapGet("/api/badges/{id}", (IBadgesCatalog catalog, string id) =>
{
    var badge = catalog.GetById(id);
    return badge is null ? Results.NotFound() : Results.Ok(badge);
});

app.MapGet("/api/search", (IBadgesCatalog catalog, string? text, string? country, string? specialization, string? status, int? level) =>
{
    var query = new BadgeQuery(text, country, specialization, status, level);
    return Results.Ok(catalog.Query(query));
});

app.MapGet("/api/probes", (IProbesCatalog probes) => Results.Ok(probes.GetAll()));

app.MapGet("/api/probes/{probeId}/points", (IProbesCatalog probes, string probeId) =>
{
    var probe = probes.GetById(probeId);
    return probe is null ? Results.NotFound() : Results.Ok(probes.GetPointsByProbeId(probeId));
});

app.MapGet("/api/probes/{probeId}/sections", (IProbesCatalog probes, string probeId) =>
{
    var probe = probes.GetById(probeId);
    return probe is null ? Results.NotFound() : Results.Ok(probes.GetSectionsByProbeId(probeId));
});

app.MapGet("/api/probes/{probeId}/sections/{sectionKey}/points", (IProbesCatalog probes, string probeId, string sectionKey) =>
{
    var probe = probes.GetById(probeId);
    if (probe is null)
    {
        return Results.NotFound();
    }

    var points = probes.GetSectionPointsByProbeId(probeId, sectionKey);
    return points.Count == 0 ? Results.NotFound() : Results.Ok(points);
});

app.MapGet("/api/probes/{probeId}/grouped", (IProbesCatalog probes, string probeId) =>
{
    var probe = probes.GetById(probeId);
    if (probe is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        probe.Id,
        probe.Title,
        probe.Points,
        probe.Sections
    });
});
```

### Controller/Service usage

```csharp
using ProjectK.ProbeAndBadges.Abstractions;

public sealed class BadgesFacade
{
    private readonly IBadgesCatalog _catalog;
    private readonly IBadgesAssetsStore _assets;

    public BadgesFacade(IBadgesCatalog catalog, IBadgesAssetsStore assets)
    {
        _catalog = catalog;
        _assets = assets;
    }

    public BadgesMetadata GetMetadata() => _catalog.Metadata;

    public string? GetAssetsPath() => _assets.AssetsDirectoryPath;

    public IReadOnlyList<Badge> Find(BadgeQuery query) => _catalog.Query(query);
}

public sealed class ProbesFacade
{
    private readonly IProbesCatalog _probes;

    public ProbesFacade(IProbesCatalog probes)
    {
        _probes = probes;
    }

    public IReadOnlyList<ProbeSection> GetSections(string probeId) => _probes.GetSectionsByProbeId(probeId);

    public IReadOnlyList<ProbePoint> GetSectionPoints(string probeId, string sectionKey) =>
        _probes.GetSectionPointsByProbeId(probeId, sectionKey);
}
```

## 5. Operational Notes

- `IBadgesAssetsStore.AssetsSnapshot` contains extraction metadata for diagnostics.
- `IBadgesAssetsStore.AssetsDirectoryPath` is host-internal. Do not expose physical paths in public API responses.
- If extraction is unavailable at startup, image URLs can temporarily return `404` until extraction succeeds.
