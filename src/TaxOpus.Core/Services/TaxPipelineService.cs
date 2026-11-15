using System.Text.Json;
using TaxOpus.Core.Data;
using TaxOpus.Core.Downloader;
using TaxOpus.Core.Exporters;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;
using TaxOpus.Core.Sources;

namespace TaxOpus.Core.Services;

public sealed class TaxPipelineService : IDisposable
{
    private readonly bool _ownsHttp;
    private readonly HttpClient _http;
    private readonly SstDownloader _downloader;

    public TaxPipelineService(SstDownloader? downloader = null, HttpClient? httpClient = null)
    {
        _ownsHttp = httpClient == null;
        _http = httpClient ?? CreateHttpClient();
        _downloader = downloader ?? new SstDownloader(_http);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // Some state sites (Colorado, Florida) refuse requests without a browser-like user agent.
    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; TaxOpus/1.0)");
        return http;
    }

    public record PipelineExecutionResult(
        int TotalRecords,
        int StatesProcessed,
        int SstStatesDownloaded,
        string ExportDirectory,
        IReadOnlyList<string> KeptStates,
        IReadOnlyDictionary<string, string>? FailedStates = null
    );

    /// <summary>
    /// Downloads SST files, merges with state baselines, and generates combined exports and tenant seed templates.
    /// With <paramref name="offline"/> set, uses the SST files already in data/raw/sst instead of downloading.
    /// Rates are computed as of <paramref name="asOf"/> (default: today, UTC).
    /// </summary>
    public async Task<PipelineExecutionResult> RunAsync(
        string dataRootDirectory,
        bool offline = false,
        DateOnly? asOf = null,
        CancellationToken cancellationToken = default)
    {
        var asOfDate = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rawDir = Path.Combine(dataRootDirectory, "raw", "sst");
        var exportDir = Path.Combine(dataRootDirectory, "exports");

        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(exportDir);

        var allRecords = new List<TaxJurisdictionRate>();
        var sstDownloadedCount = 0;
        // Why a state got no ZIP-level rates this run
        var problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var statewideOnlyStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastExport = await ReadLastExportAsync(exportDir, cancellationToken);

        List<SstDownloader.SstFileEntry> rateEntries;
        List<SstDownloader.SstFileEntry> boundaryEntries;
        if (offline)
        {
            Console.WriteLine($"[INFO] Using cached SST files in {rawDir} (as of {asOfDate:yyyy-MM-dd})...");
            rateEntries = SstDownloader.FindCachedFiles(rawDir, isRatesDirectory: true, asOfDate);
            boundaryEntries = SstDownloader.FindCachedFiles(rawDir, isRatesDirectory: false, asOfDate);
        }
        else
        {
            Console.WriteLine($"[INFO] Discovering SST state files (as of {asOfDate:yyyy-MM-dd})...");
            rateEntries = await _downloader.DiscoverFilesAsync(isRatesDirectory: true, cancellationToken, asOfDate);
            boundaryEntries = await _downloader.DiscoverFilesAsync(isRatesDirectory: false, cancellationToken, asOfDate);
        }

        var boundaryByState = boundaryEntries.ToDictionary(b => b.StateCode, StringComparer.OrdinalIgnoreCase);

        // Process each discovered SST state
        foreach (var rateEntry in rateEntries)
        {
            if (!boundaryByState.TryGetValue(rateEntry.StateCode, out var boundaryEntry))
            {
                Report(problems, rateEntry.StateCode, "SST boundary file missing");
                continue;
            }

            Console.WriteLine($"[INFO] {(offline ? "Parsing" : "Downloading and parsing")} SST for {rateEntry.StateCode}...");
            var ratePath = await _downloader.DownloadAndExtractAsync(rateEntry, rawDir, cancellationToken);
            var boundaryPath = await _downloader.DownloadAndExtractAsync(boundaryEntry, rawDir, cancellationToken);

            if (ratePath != null && boundaryPath != null && File.Exists(ratePath) && File.Exists(boundaryPath))
            {
                try
                {
                    // Boundary files run to hundreds of megabytes, so stream them rather than reading them whole.
                    using var rateReader = new StreamReader(ratePath);
                    var parsedRates = SstParser.ParseRates(rateReader, asOfDate);
                    using var boundaryReader = new StreamReader(boundaryPath);
                    var stateRecords = SstParser.ParseBoundaryAndMerge(rateEntry.StateCode, boundaryReader, parsedRates, asOfDate);
                    if (stateRecords.Count == 0)
                    {
                        using var statewideReader = new StreamReader(boundaryPath);
                        if (SstParser.IsStatewideOnly(statewideReader, asOfDate))
                        {
                            statewideOnlyStates.Add(rateEntry.StateCode);
                            Console.WriteLine($"  [INFO] {rateEntry.StateCode}: SST lists one statewide rate; using the curated baseline.");
                        }
                        else
                        {
                            Report(problems, rateEntry.StateCode, $"no ZIP codes in effect on {asOfDate:yyyy-MM-dd}");
                        }
                        continue;
                    }

                    allRecords.AddRange(stateRecords);
                    sstDownloadedCount++;
                    Console.WriteLine($"  ✓ {rateEntry.StateCode}: Parsed {stateRecords.Count} ZIP code rates ({rateEntry.FileName}, {boundaryEntry.FileName}).");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Report(problems, rateEntry.StateCode, $"SST files failed ({ex.Message})");
                }
            }
            else
            {
                Report(problems, rateEntry.StateCode, "SST files could not be downloaded");
            }
        }

        // States outside SST (or SST states without ZIP-level files) that publish their own rate data
        var sourceContext = new SourceContext(dataRootDirectory, asOfDate, offline, _http);
        foreach (var source in StateRateSources.All)
        {
            if (allRecords.Any(r => string.Equals(r.StateCode, source.StateCode, StringComparison.OrdinalIgnoreCase))) continue;

            Console.WriteLine($"[INFO] {(offline ? "Parsing" : "Downloading and parsing")} {source.SourceCode} for {source.StateCode}...");
            try
            {
                var records = await source.LoadAsync(sourceContext, cancellationToken);
                if (records.Count == 0)
                {
                    Report(problems, source.StateCode, $"{source.SourceCode} gave no ZIP code rates");
                    continue;
                }
                allRecords.AddRange(records);
                problems.Remove(source.StateCode);
                Console.WriteLine($"  ✓ {source.StateCode}: Parsed {records.Count} ZIP code rates ({source.SourceCode}).");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Report(problems, source.StateCode, $"{source.SourceCode} failed ({ex.Message})");
            }
        }

        // A state that had ZIP-level rates keeps them when this run can't compute new ones (a site down, a file
        // missing or changed): dropping it to the baseline would retire its tenants' ZIP rules on the next seed.
        var keptRatesAsOf = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);
        var computedStates = allRecords.Select(r => r.StateCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (stateCode, kept) in lastExport.Where(e => !computedStates.Contains(e.Key) && !statewideOnlyStates.Contains(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var reason = problems.ContainsKey(stateCode) ? string.Empty : "no ZIP code rates this run; ";
            if (kept.AsOf > asOfDate)
            {
                Console.WriteLine($"  [WARN] {stateCode}: {reason}the last export's rates are for {kept.AsOf:yyyy-MM-dd}, after {asOfDate:yyyy-MM-dd}; using the curated baseline.");
                problems.Remove(stateCode);
                continue;
            }
            allRecords.AddRange(kept.Records);
            keptRatesAsOf[stateCode] = kept.AsOf;
            problems.Remove(stateCode);
            Console.WriteLine($"  [WARN] {stateCode}: {reason}keeping the {kept.Records.Count} ZIP code rates as of {kept.AsOf:yyyy-MM-dd} from the last export.");
        }
        foreach (var stateCode in problems.Keys.Where(p => !computedStates.Contains(p)).Order(StringComparer.Ordinal))
        {
            Console.WriteLine($"  [INFO] {stateCode}: using the curated baseline.");
        }

        var zipRecordsByState = allRecords
            .GroupBy(r => r.StateCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<TaxJurisdictionRate>)g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Add curated baseline rates for states not covered or supplemental
        Console.WriteLine($"[INFO] Augmenting with curated state baselines for all {StateDirectory.States.Count} jurisdictions...");
        foreach (var (stateCode, stateInfo) in StateDirectory.States)
        {
            if (!zipRecordsByState.ContainsKey(stateCode))
            {
                var seed = BaselineStateTaxProvider.GenerateTenantSeed(stateCode);
                foreach (var rule in seed.Rules)
                {
                    var rate = seed.Rates.FirstOrDefault(r => r.Key == rule.RateKey) ?? seed.Rates.First();
                    allRecords.Add(new TaxJurisdictionRate
                    {
                        CountryCode = "US",
                        StateCode = stateCode,
                        PostalCode = string.IsNullOrEmpty(rule.PostalCodePattern) ? "*" : rule.PostalCodePattern,
                        City = string.IsNullOrEmpty(rule.RegionName) ? stateInfo.Name : rule.RegionName,
                        County = string.Empty,
                        StateRatePercent = stateInfo.BaseStateRatePercent,
                        CountyRatePercent = Math.Max(0m, rate.RatePercent - stateInfo.BaseStateRatePercent),
                        CityRatePercent = 0m,
                        SpecialDistrictRatePercent = 0m,
                        CombinedRatePercent = rate.RatePercent,
                        MinCombinedRatePercent = rate.RatePercent,
                        MaxCombinedRatePercent = rate.RatePercent,
                        AuthorityName = stateInfo.AgencyName,
                        Source = "BaselineCurated",
                        EffectiveDate = asOfDate
                    });
                }
            }
        }

        Console.WriteLine($"[INFO] Exporting combined master datasets ({allRecords.Count} total records)...");
        await MasterJsonExporter.ExportAsync(allRecords, exportDir, asOfDate, keptRatesAsOf, cancellationToken);
        await MasterCsvExporter.ExportAsync(allRecords, Path.Combine(exportDir, "us_sales_tax_combined.csv"), cancellationToken);

        var statesProcessed = allRecords.Select(r => r.StateCode).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        Console.WriteLine($"[SUCCESS] Export complete! Generated master JSON and CSV for {statesProcessed} states at: {exportDir}");

        return new PipelineExecutionResult(
            TotalRecords: allRecords.Count,
            StatesProcessed: statesProcessed,
            SstStatesDownloaded: sstDownloadedCount,
            ExportDirectory: exportDir,
            KeptStates: [.. keptRatesAsOf.Keys.Order(StringComparer.Ordinal)],
            FailedStates: problems.Count > 0 ? new Dictionary<string, string>(problems, StringComparer.OrdinalIgnoreCase) : null
        );
    }

    private static void Report(Dictionary<string, string> problems, string stateCode, string reason)
    {
        problems[stateCode] = reason;
        Console.WriteLine($"  [WARN] {stateCode}: {reason}.");
    }

    /// <summary>The ZIP-level rates in the last combined export, by state, with the date each was computed for.</summary>
    private static async Task<Dictionary<string, (List<TaxJurisdictionRate> Records, DateOnly AsOf)>> ReadLastExportAsync(string exportDir, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (List<TaxJurisdictionRate>, DateOnly)>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(exportDir, "us_sales_tax_combined.json");
        if (!File.Exists(path)) return result;

        MasterJsonExporter.CombinedExportPayload? export;
        try
        {
            export = await MasterJsonExporter.ReadAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[WARN] Could not read the last export ({ex.Message}); states that fail this run will use the curated baseline.");
            return result;
        }
        if (export == null) return result;

        foreach (var state in export.Records.Where(ZipTenantSeedProvider.IsZipLevel).GroupBy(r => r.StateCode, StringComparer.OrdinalIgnoreCase))
        {
            if (export.Manifest.RatesAsOfFor(state.Key) is { } asOf) result[state.Key] = (state.ToList(), asOf);
        }
        return result;
    }
}
