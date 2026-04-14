# ProjectK.ProbeAndBadges

Read-only .NET library for loading and querying Plast badges data.

It provides:
- a stable catalog API over badges metadata and entities
- a separate probes catalog API with probe sections and grouped responses
- flexible data source loading (embedded, file, zip, URL, latest release)
- optional runtime extraction of badge images from release zip
- host-friendly dependency injection registration

## Target framework

- .NET 10

## Quick start

Register services in your host application:

```csharp
using ProjectK.ProbeAndBadges.DependencyInjection;

builder.Services.AddProbeAndBadgesApi();
```

Resolve and use the catalog:

```csharp
using ProjectK.ProbeAndBadges.Abstractions;

var catalog = app.Services.GetRequiredService<IBadgesCatalog>();

var all = catalog.GetAll();
var byId = catalog.GetById("avtoznavstvo-i");
var filtered = catalog.Query(new BadgeQuery(Text: "актор", Country: "Україна"));

var probesCatalog = app.Services.GetRequiredService<IProbesCatalog>();
var probes = probesCatalog.GetAll();
var probe1Points = probesCatalog.GetPointsByProbeId("probe-1");
var probe1Sections = probesCatalog.GetSectionsByProbeId("probe-1");
var sectionPoints = probesCatalog.GetSectionPointsByProbeId("probe-1", "А");
```

Probes seed currently contains 3 probes; points are available for the first two probes.

## Probes API quick reference

The demo host exposes grouped probes routes that are convenient for frontend rendering:

- `GET /api/probes`
- `GET /api/probes/{probeId}/points`
- `GET /api/probes/{probeId}/sections`
- `GET /api/probes/{probeId}/sections/{sectionKey}/points` where `sectionKey` can be section code (`А`, `Б`, `В`, ...) or section id
- `GET /api/probes/{probeId}/grouped` (probe + sections + nested points in one payload)

Grouped frontend demo page:

- `GET /probes.html`

## Common configuration

Override only what you need:

```csharp
using ProjectK.ProbeAndBadges;
using ProjectK.ProbeAndBadges.DependencyInjection;

builder.Services.AddProbeAndBadgesApi(options =>
{
    options.DataSource = BadgesDataSource.Auto;
    options.EnableLatestReleaseEtagCache = true;
    options.EnableLocalReleaseZipCache = true;
    options.VerifyReleaseZipChecksum = true;

    options.EnableAssetsExtraction = true;
    options.MissingImagesPolicy = AssetsMissingImagesPolicy.Warn;
});
```

## Data source modes

- Auto: resolves latest release asset, with optional fallback to embedded seed
- EmbeddedSeed: loads bundled seed dataset
- JsonFile: loads badges JSON from local file path
- ReleaseDataZip: loads badges JSON entry from local zip file
- ReleaseDataZipUrl: downloads zip from URL and reads badges JSON entry

## Runtime assets

When assets extraction is enabled and zip source path is available, the library extracts images to a local snapshot directory and exposes state via IBadgesAssetsStore.

Typical host mapping to public path:

```csharp
using Microsoft.Extensions.FileProviders;
using ProjectK.ProbeAndBadges.Abstractions;

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

## Samples

Run ASP.NET end-to-end demo:

```powershell
dotnet run --project samples/ProjectK.ProbeAndBadges.AspNetDemo/ProjectK.ProbeAndBadges.AspNetDemo.csproj
```

Run console demo:

```powershell
dotnet run --project samples/ProjectK.ProbeAndBadges.ConsoleDemo/ProjectK.ProbeAndBadges.ConsoleDemo.csproj
```

## Documentation

- Public contract: docs/public-api.md
- Runtime assets flow: docs/assets-runtime.md
- Host integration guide: docs/host-integration.md
