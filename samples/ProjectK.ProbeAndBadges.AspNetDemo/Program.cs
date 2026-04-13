using Microsoft.Extensions.FileProviders;
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
        string? text,
        string? country,
        string? specialization,
        string? status,
        int? level,
        int skip = 0,
        int take = 24) =>
    {
        var safeSkip = Math.Max(0, skip);
        var safeTake = Math.Clamp(take, 1, 200);

        var query = new BadgeQuery(
            Text: text,
            Country: country,
            Specialization: specialization,
            Status: status,
            Level: level);

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

app.Run();
