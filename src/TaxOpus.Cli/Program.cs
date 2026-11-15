using System.Globalization;
using System.Text.Json;
using TaxOpus.Core.Data;
using TaxOpus.Core.Exporters;
using TaxOpus.Core.Models;
using TaxOpus.Core.Services;
using TaxOpus.Core.Sources;

DateOnly? asOf = null;
string? dataDirOverride = null;
var positional = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (arg == "--as-of")
    {
        if (i + 1 >= args.Length || !DateOnly.TryParseExact(args[i + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedAsOf))
        {
            Console.Error.WriteLine("Error: --as-of requires a date in yyyy-MM-dd format.");
            return 1;
        }
        asOf = parsedAsOf;
        i++;
    }
    else if (arg == "--data-dir")
    {
        if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
        {
            Console.Error.WriteLine("Error: --data-dir requires a directory path.");
            return 1;
        }
        dataDirOverride = Path.GetFullPath(args[++i]);
    }
    else if (arg is "-h" or "--help")
    {
        PrintUsage();
        return 0;
    }
    else if (arg.StartsWith('-'))
    {
        Console.Error.WriteLine($"Error: unknown option '{arg}'.");
        PrintUsage(Console.Error);
        return 1;
    }
    else
    {
        positional.Add(arg);
    }
}

var command = positional.Count > 0 ? positional[0].ToLowerInvariant() : "all";
var dataDir = dataDirOverride ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data"));
if (dataDirOverride == null && command is not ("seed-tenant" or "lookup") && positional.Count > 1 && !string.IsNullOrWhiteSpace(positional[1]))
{
    dataDir = Path.GetFullPath(positional[1]);
}

if (command is "all" or "fetch" or "process")
{
    Console.WriteLine("==========================================================");
    Console.WriteLine(" TaxOpus - Sales Tax Aggregation & Tenant Seeding Utility ");
    Console.WriteLine("==========================================================");
}

using var pipeline = new TaxPipelineService();

switch (command)
{
    case "fetch":
    case "process":
    case "all":
        // "process" works from the files already in data/raw; the others download the latest first.
        var result = await pipeline.RunAsync(dataDir, offline: command == "process", asOf: asOf);
        Console.WriteLine();
        Console.WriteLine($"Summary: {result.TotalRecords} records across {result.StatesProcessed} states (SST live feeds: {result.SstStatesDownloaded}).");
        if (result.KeptStates.Count > 0)
        {
            Console.Error.WriteLine($"Kept the last export's rates for {string.Join(", ", result.KeptStates)}; see the warnings above.");
        }
        if (result.FailedStates is { Count: > 0 })
        {
            Console.Error.WriteLine($"Fell back to baseline for {string.Join(", ", result.FailedStates.Keys.Order(StringComparer.Ordinal))}; see the warnings above.");
        }
        Console.WriteLine($"Artifacts: {result.ExportDirectory}");
        return result.KeptStates.Count > 0 || result.FailedStates is { Count: > 0 } ? 2 : 0;

    case "seed-tenant":
    {
        var state = positional.Count > 1 ? positional[1].ToUpperInvariant() : "TX";
        if (!StateDirectory.States.ContainsKey(state))
        {
            Console.Error.WriteLine($"Error: unknown state code '{state}'.");
            return 1;
        }

        Console.Error.WriteLine($"Generating seed preview for state: {state}");
        var seed = await LoadSeedAsync(state, dataDir, asOf);
        if (seed == null) return 1;

        Console.WriteLine(JsonSerializer.Serialize(seed, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    case "lookup":
    {
        // lookup <STATE> <longitude> <latitude>, or lookup <STATE> with "longitude,latitude" lines on stdin.
        // States whose rates come from boundary polygons only; uses the cached files.
        var lookupState = positional.Count > 1 ? positional[1].ToUpperInvariant() : string.Empty;
        if (StateRateSources.All.OfType<IPointRateSource>().FirstOrDefault(s => s.StateCode == lookupState) is not { } pointSource)
        {
            Console.Error.WriteLine($"No point lookup for '{lookupState}'; available: {string.Join(", ", StateRateSources.All.OfType<IPointRateSource>().Select(s => s.StateCode))}");
            return 1;
        }
        using var lookupHttp = new HttpClient();
        var lookupContext = new SourceContext(dataDir, asOf ?? DateOnly.FromDateTime(DateTime.UtcNow), offline: true, lookupHttp);
        Func<double, double, TaxOpus.Core.Parsers.RateComponents?> rateAt;
        try
        {
            rateAt = await pointSource.LoadPointLookupAsync(lookupContext);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        IEnumerable<string> points = positional.Count > 3 ? [$"{positional[2]},{positional[3]}"] : ReadStdin();
        foreach (var point in points)
        {
            if (string.IsNullOrWhiteSpace(point)) continue;
            var f = point.Split(',', StringSplitOptions.TrimEntries);
            if (f.Length < 2 || !double.TryParse(f[0], CultureInfo.InvariantCulture, out var lon) || !double.TryParse(f[1], CultureInfo.InvariantCulture, out var lat))
            {
                Console.Error.WriteLine($"[WARN] Skipping invalid coordinate line: '{point}'");
                continue;
            }
            var found = rateAt(lon, lat);
            // longitude,latitude,combined,state,county,city,district,county FIPS (blank when outside the state)
            Console.WriteLine(found is { } c
                ? string.Join(',', f[0], f[1], Invariant(c.Combined), Invariant(c.State), Invariant(c.County), Invariant(c.Place), Invariant(c.District), c.CountyFips)
                : $"{f[0]},{f[1]},");
        }
        return 0;
    }

    default:
        Console.Error.WriteLine($"Error: unknown command '{command}'.");
        PrintUsage(Console.Error);
        return 1;
}

static void PrintUsage(TextWriter? writer = null)
{
    (writer ?? Console.Out).WriteLine(
        "Usage: TaxOpus.Cli [all|fetch|process] [data_dir] [--data-dir <dir>] [--as-of yyyy-MM-dd] | seed-tenant <STATE> [--data-dir <dir>] | lookup <STATE> [longitude latitude] [--data-dir <dir>] [--as-of yyyy-MM-dd]");
}

// Builds the seed the pipeline would write: from the exported ZIP-level rates when the state has them,
// otherwise from the curated baseline.
static async Task<TenantStateTaxSeed?> LoadSeedAsync(string state, string dataDir, DateOnly? requestedAsOf)
{
    var masterPath = Path.Combine(dataDir, "exports", "us_sales_tax_combined.json");
    if (File.Exists(masterPath))
    {
        MasterJsonExporter.CombinedExportPayload? export;
        try
        {
            export = await MasterJsonExporter.ReadAsync(masterPath);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.Error.WriteLine($"Error: could not read '{masterPath}' ({ex.Message}).");
            return null;
        }

        if (export != null && export.Manifest.RatesAsOfFor(state) is { } ratesAsOf)
        {
            if (requestedAsOf is { } req && req != ratesAsOf)
            {
                Console.Error.WriteLine($"[WARN] --as-of {req:yyyy-MM-dd} ignored for seed-tenant; using exported snapshot date {ratesAsOf:yyyy-MM-dd}.");
            }

            var zipRecords = export.Records.Where(r => ZipTenantSeedProvider.IsZipLevel(r) && string.Equals(r.StateCode, state, StringComparison.OrdinalIgnoreCase)).ToList();
            if (zipRecords.Count > 0)
            {
                Console.Error.WriteLine($"Using ZIP-level rates ({zipRecords[0].Source}) as of {ratesAsOf:yyyy-MM-dd} from {masterPath}");
                return ZipTenantSeedProvider.GenerateTenantSeed(state, zipRecords, ratesAsOf);
            }
        }
    }

    Console.Error.WriteLine("Using the curated baseline (no ZIP-level rates exported for this state)");
    return BaselineStateTaxProvider.GenerateTenantSeed(state);
}

static IEnumerable<string> ReadStdin()
{
    string? line;
    while ((line = Console.In.ReadLine()) != null) yield return line;
}

static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);
