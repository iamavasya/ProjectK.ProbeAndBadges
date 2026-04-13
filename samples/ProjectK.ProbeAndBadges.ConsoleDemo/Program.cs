using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProjectK.ProbeAndBadges;
using ProjectK.ProbeAndBadges.Abstractions;
using ProjectK.ProbeAndBadges.DependencyInjection;

var services = new ServiceCollection();

services.AddLogging(builder =>
{
    builder.ClearProviders();
    builder.SetMinimumLevel(LogLevel.Debug);
    builder.AddSimpleConsole(options =>
    {
        options.TimestampFormat = "HH:mm:ss ";
        options.SingleLine = true;
    });
});

services.AddProbeAndBadgesApi(options =>
{
    options.DataSource = BadgesDataSource.Auto;
    options.RemoteRequestTimeoutSeconds = 20;
    options.EnableLatestReleaseEtagCache = true;
    options.EnableLocalReleaseZipCache = true;
    options.VerifyReleaseZipChecksum = true;
    options.RequireReleaseZipChecksumAsset = false;
    options.AutoFallbackToEmbeddedSeed = true;
    options.EnableQueryCache = true;
});

using var provider = services.BuildServiceProvider();
var catalog = provider.GetRequiredService<IBadgesCatalog>();

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

var all = catalog.GetAll();

Console.WriteLine();
Console.WriteLine("=== Dataset metadata ===");
Console.WriteLine(JsonSerializer.Serialize(catalog.Metadata, jsonOptions));

Console.WriteLine();
Console.WriteLine("=== First 10 badges ===");
Console.WriteLine(JsonSerializer.Serialize(all.Take(10), jsonOptions));

if (all.Count == 0)
{
    Console.WriteLine("No badges loaded. Stop demo.");
    return;
}

var sampleId = all[Math.Min(3, all.Count - 1)].Id;
var oneById = catalog.GetById(sampleId);

Console.WriteLine();
Console.WriteLine($"=== One badge by id ({sampleId}) ===");
Console.WriteLine(JsonSerializer.Serialize(oneById, jsonOptions));

var pivot = all[0];
var query = new BadgeQuery(
    Text: string.Empty,
    Country: pivot.Country,
    Specialization: string.Empty,
    Status: pivot.Status,
    Level: pivot.Level);

var result = catalog.Query(query);
var resultAgain = catalog.Query(query);

Console.WriteLine();
Console.WriteLine("=== Query case (country + status + level) ===");
Console.WriteLine(JsonSerializer.Serialize(new
{
    Query = query,
    Total = result.Count,
    Preview = result.Take(5).Select(b => new
    {
        b.Id,
        b.Title,
        b.Country,
        b.Status,
        b.Level
    })
}, jsonOptions));

Console.WriteLine();
Console.WriteLine($"=== Query cache check === Same reference: {ReferenceEquals(result, resultAgain)}");
