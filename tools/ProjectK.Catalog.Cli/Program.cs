using System.Text;
using ProjectK.Catalog;
using ProjectK.Catalog.Cli;

Console.OutputEncoding = Encoding.UTF8;

const string Usage = """
    catalog — tools for the Plast catalog (format v2)

      convert-v1 --badges <badges.json> --images <dir> --probes <probes.seed.json>
                 --awards-images <dir> --out <dir>
          Convert the parser's output and the library seeds into a v2 catalog folder.
          Prints what to check by hand and which parser images went unused.

      validate <dir|zip> [--against <dir>]
          Check a catalog, or a krai contribution against the main catalog, offline.
          The catalog service runs the same checks on every write.

      pack <dir> --out <zip>
          Pack a catalog folder into a zip (the format contributions are sent in).
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return 0;
}

try
{
    return args[0] switch
    {
        "convert-v1" => ConvertV1(Options.Parse(args[1..])),
        "validate" => Validate(Options.Parse(args[1..])),
        "pack" => Pack(Options.Parse(args[1..])),
        _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
    };
}
catch (Exception ex) when (ex is ArgumentException or InvalidDataException or System.Text.Json.JsonException or IOException)
{
    return Fail(ex.Message);
}

static int ConvertV1(Options o)
{
    var converter = new V1Converter();
    var awardsDir = o.Required("awards-images");
    var awardLevels = Directory.EnumerateFiles(awardsDir, "award*.png")
        .Select(f => Path.GetFileNameWithoutExtension(f))
        .Where(n => !n.EndsWith("-bw", StringComparison.Ordinal))
        .Select(n => int.Parse(n["award".Length..], System.Globalization.CultureInfo.InvariantCulture))
        .ToList();

    var bundle = converter.Convert(
        File.ReadAllText(o.Required("badges")),
        File.ReadAllText(o.Required("probes")),
        awardLevels);
    bundle = bundle with { Source = bundle.Source! with { GeneratedAtUtc = DateTimeOffset.UtcNow } };

    var sourceImages = o.Required("images");
    var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var name in bundle.Badges.Select(b => b.Images.Color).OfType<string>().Distinct())
    {
        var path = Path.Combine(sourceImages, converter.ImageSources.GetValueOrDefault(name, name));
        if (File.Exists(path)) images[name] = File.ReadAllBytes(path);
        else converter.Report.Add($"зображення {name} відсутнє у {sourceImages}");
    }

    foreach (var level in awardLevels)
    {
        images[V1Converter.AwardImageName(level, true)] = File.ReadAllBytes(Path.Combine(awardsDir, $"award{level}.png"));
        images[V1Converter.AwardImageName(level, false)] = File.ReadAllBytes(Path.Combine(awardsDir, $"award{level}-bw.png"));
    }

    var unused = Directory.EnumerateFiles(sourceImages).Select(Path.GetFileName).OfType<string>()
        .Where(n => !converter.ImageSources.ContainsValue(n)).Order().ToList();

    var package = new CatalogPackage { Bundle = bundle, Images = images };
    var report = package.Validate();
    var output = o.Required("out");
    package.WriteFolder(output);

    foreach (var note in converter.Report) Console.WriteLine($"check: {note}");
    foreach (var name in unused) Console.WriteLine($"unused image: {name}");
    Console.WriteLine($"Wrote {output}: {bundle.Badges.Count} badges, {bundle.Probes.Count} probes, {bundle.Awards.Count} awards, {images.Count} images.");
    return PrintReport(report);
}

static int Validate(Options o)
{
    var package = Load(o.Positional(0));
    CatalogPackage? reference = o.Optional("against") is { } against ? CatalogPackage.ReadFolder(against) : null;
    var report = package.Validate(reference?.Bundle, reference?.ImageNames);
    return PrintReport(report);
}

static int Pack(Options o)
{
    var package = CatalogPackage.ReadFolder(o.Positional(0));
    using var output = File.Create(o.Required("out"));
    package.WriteZip(output);
    return 0;
}

static CatalogPackage Load(string path)
{
    if (Directory.Exists(path)) return CatalogPackage.ReadFolder(path);
    using var zip = File.OpenRead(path);
    return CatalogPackage.ReadZip(zip);
}

static int PrintReport(ValidationReport report)
{
    foreach (var issue in report.Issues.OrderBy(i => i.IsError ? 0 : 1))
    {
        (issue.IsError ? Console.Error : Console.Out).WriteLine(issue);
    }

    var errors = report.Errors.Count();
    Console.WriteLine($"{errors} errors, {report.Warnings.Count()} warnings.");
    return errors == 0 ? 0 : 1;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
