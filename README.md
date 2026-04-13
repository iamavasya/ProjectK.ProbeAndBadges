# ProjectK.ProbeAndBadges

Read-only .NET library for loading and querying Plast badges data.

It provides:
- a stable catalog API over badges metadata and entities
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
```

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
