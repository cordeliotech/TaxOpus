using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using TaxOpus.Core.Data;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Sources;

/// <summary>
/// New Mexico gross receipts tax (on the seller, usually passed on), from the Taxation and Revenue Department's
/// public-domain GIS layer for the rate period (RGIS, published yearly for July–June). Each location code carries
/// one combined rate: "Remainder of County" rows for unincorporated areas and one row per municipality (split by
/// county where a city straddles counties at different rates). Tribal areas and special districts (TIF/TIDD,
/// airports, water districts) are not mapped: tribal areas get the county's remainder rate, districts mostly share
/// their city's rate.
/// </summary>
public sealed partial class NewMexicoGrtSource : IStateRateSource
{
    public const string DataPageUrl = "https://www.tax.newmexico.gov/businesses/data-download/";
    private const string MetadataUrl = "https://gstore.unm.edu/apps/rgis/datasets/{0}/metadata/FGDC-STD-001-1998.html";

    public string StateCode => "NM";
    public string SourceCode => "NM-TRD";

    public string Describe(string stateName) =>
        "New Mexico TRD gross receipts tax rates by location code (public-domain GIS layer), assigned to ZIP codes by 2020 population";

    public async Task<List<TaxJurisdictionRate>> LoadAsync(SourceContext context, CancellationToken cancellationToken = default)
    {
        string path;
        if (context.Offline)
        {
            var cached = FindCachedLayer(context)
                ?? throw new FileNotFoundException($"No cached New Mexico rate layer covers {context.AsOf:yyyy-MM-dd}; run 'fetch' to download it.");
            path = Path.Combine(context.RawDirectory(StateCode), cached);
        }
        else
        {
            try
            {
                var url = await FindLayerUrlAsync(context, cancellationToken)
                    ?? throw new InvalidDataException($"No New Mexico rate layer linked from {DataPageUrl} covers {context.AsOf:yyyy-MM-dd}.");
                path = await context.FetchAsync(StateCode, url[(url.LastIndexOf('/') + 1)..], url, false, cancellationToken);
            }
            catch (Exception ex) when (SourceContext.IsDownloadFailure(ex, cancellationToken))
            {
                var cached = FindCachedLayer(context);
                if (cached == null) throw;
                path = Path.Combine(context.RawDirectory(StateCode), cached);
                Console.WriteLine($"  [WARN] NM: could not reach {DataPageUrl} ({ex.Message}); using cached {cached}.");
            }
        }

        var period = Period(Path.GetFileName(path))!.Value;
        return Build(ReadLocations(path), context.Crosswalk, period.From);
    }

    private string? FindCachedLayer(SourceContext context) =>
        context.CachedFiles(StateCode, "New_Mexico_gross_receipts_tax_*.csv")
            .FirstOrDefault(name => Period(name) is { } p && p.From <= context.AsOf && context.AsOf <= p.To);

    public record LocationRate(string LocationCode, string Name, string County, decimal RatePercent);

    public static List<TaxJurisdictionRate> Build(IReadOnlyList<LocationRate> locations, ZipCrosswalk crosswalk, DateOnly periodStart)
    {
        var state = StateDirectory.Get("NM");
        var effective = ZipRateAggregator.ToDateInt(periodStart);
        var countyFips = ArizonaTptSource.CountyCodes(crosswalk, state);
        var placeKeys = ArizonaTptSource.PlaceKeys(crosswalk, state);

        var countyRates = new Dictionary<string, AreaRate>();
        var placeRates = new Dictionary<string, AreaRate>();
        foreach (var loc in locations.Where(l => l.LocationCode.Length > 0 && !l.Name.Contains("TIF", StringComparison.Ordinal)))
        {
            if (!countyFips.TryGetValue(CrosswalkZipRates.NormalizeName(loc.County), out var county)) continue;
            // Los Alamos is a consolidated city and county, with no remainder row
            if (loc.Name.StartsWith("Remainder of County", StringComparison.OrdinalIgnoreCase) || loc.Name.Contains("City and County", StringComparison.OrdinalIgnoreCase))
            {
                countyRates[county] = new AreaRate(loc.RatePercent, effective);
            }
            else if (placeKeys.TryGetValue(MunicipalityName(loc.Name), out var place))
            {
                // Keyed by county too: Rio Rancho's rate differs between Sandoval and Bernalillo counties.
                placeRates[CrosswalkZipRates.PlaceInCounty(place, county)] = new AreaRate(loc.RatePercent, effective);
            }
        }

        return CrosswalkZipRates.Build(state, "NM-TRD", crosswalk, state.BaseStateRatePercent, countyRates, placeRates);
    }

    /// <summary>
    /// The layer's CSV export is a zip holding one CSV with columns name, county, locat_cdr (location code),
    /// locat_cd1, locat_cd2 (tribal codes), grt_rate, ... One row per polygon, so codes repeat.
    /// </summary>
    public static List<LocationRate> ReadLocations(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.Entries.Single(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)).Open());
        var header = SstParser.SplitCsvLine(reader.ReadLine() ?? string.Empty).Select(h => h.Trim()).ToList();
        int Col(string name) => header.IndexOf(name);
        var (name, county, code, rate) = (Col("name"), Col("county"), Col("locat_cdr"), Col("grt_rate"));

        var locations = new Dictionary<(string, string), LocationRate>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var f = SstParser.SplitCsvLine(line);
            if (f.Length <= rate || !decimal.TryParse(f[rate], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) continue;
            var loc = new LocationRate(f[code].Trim(), f[name].Trim(), f[county].Trim(), Math.Round(percent, 4));
            locations.TryAdd((loc.LocationCode, loc.County), loc);
        }
        return [.. locations.Values];
    }

    /// <summary>
    /// "Santa Fe (city)", "Eagle Nest*", "Rio Rancho (Sandoval)", "Village of Tijeras" to "SANTA FE", "EAGLE NEST",
    /// "RIO RANCHO", "TIJERAS". Special districts (TIDDs, airports, water districts) match no place and are skipped.
    /// </summary>
    private static string MunicipalityName(string name) =>
        CrosswalkZipRates.NormalizeName(Regex.Replace(name, @"\(.*?\)|\*|^(Village|Town|City) of ", string.Empty));

    /// <summary>The rate period in a layer name such as "New_Mexico_gross_receipts_tax_July_2026_June_2027".</summary>
    private static (DateOnly From, DateOnly To)? Period(string name)
    {
        var m = PeriodPattern().Match(name);
        if (!m.Success) return null;
        if (!DateTime.TryParseExact($"{m.Groups["m1"].Value} {m.Groups["y1"].Value}", "MMMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)) return null;
        if (!DateTime.TryParseExact($"{m.Groups["m2"].Value} {m.Groups["y2"].Value}", "MMMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)) return null;
        return (DateOnly.FromDateTime(from), DateOnly.FromDateTime(to.AddMonths(1).AddDays(-1)));
    }

    /// <summary>
    /// The data page links one RGIS dataset per rate period; each dataset's metadata page links its CSV export.
    /// </summary>
    private static async Task<string?> FindLayerUrlAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var page = await context.Http.GetStringAsync(DataPageUrl, cancellationToken);
        foreach (var uuid in DatasetId().Matches(page).Select(m => m.Groups["uuid"].Value).Distinct())
        {
            var metadata = await context.Http.GetStringAsync(string.Format(CultureInfo.InvariantCulture, MetadataUrl, uuid), cancellationToken);
            var csv = CsvLink().Match(metadata);
            if (csv.Success && Period(csv.Value) is { } p && p.From <= context.AsOf && context.AsOf <= p.To)
            {
                return csv.Value;
            }
        }
        return null;
    }

    [GeneratedRegex(@"rgis\.unm\.edu(?:%2F|/)rgis6(?:%2F|/)dataset\.html(?:%3F|\?)uuid(?:%3D|=)(?<uuid>[0-9a-f\-]{36})", RegexOptions.IgnoreCase)]
    private static partial Regex DatasetId();

    [GeneratedRegex(@"https://gstore\.unm\.edu/apps/rgis/datasets/[0-9a-f\-]{36}/New_Mexico_gross_receipts_tax_[A-Za-z0-9_]+\.derived\.csv", RegexOptions.IgnoreCase)]
    private static partial Regex CsvLink();

    [GeneratedRegex(@"_(?<m1>[A-Z][a-z]+)_(?<y1>\d{4})_(?<m2>[A-Z][a-z]+)_(?<y2>\d{4})")]
    private static partial Regex PeriodPattern();
}
