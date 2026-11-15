using System.Globalization;
using System.Text;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Sources;

/// <summary>
/// Texas: every active sales tax permit location (about a million, from the Comptroller's "All Permitted Sales Tax
/// Locations and Local Sales Tax Responsibility" on the Texas Open Data Portal, public domain), each with the city,
/// county, transit and special purpose district authorities that tax it, priced with the Comptroller's quarterly
/// rate file. Each ZIP gets the rate of most of its business locations, so ZIPs are weighted by businesses rather
/// than residents. Texas sellers collect local tax where they do business (origin-based) for in-state sales.
/// </summary>
public sealed class TexasComptrollerSource : IStateRateSource
{
    public const string LocationsUrl = "https://data.texas.gov/resource/3kx8-uryv.csv";
    public const string RatesBaseUrl = "https://comptroller.texas.gov/data/edi/sales-tax/";
    public const string LocationsFile = "tx_locations.csv";
    private const int PageSize = 50_000;
    private const int MinimumLocations = 500_000; // fewer means a broken download, not a smaller Texas
    private const decimal LocalCapPercent = 2m;
    // A ZIP that isn't a 2020 Census ZIP area needs this many locations: fewer are mostly mistyped ZIPs
    private const int MinLocationsOutsideCensusZips = 10;

    private static readonly string[] AuthorityColumns =
    [
        "city_taid", "unique_taid", "county_taid", "mass_transit_auth1_taid", "mass_transit_auth2_taid",
        "special_purp_dist1_taid", "special_purp_dist2_taid", "special_purp_dist3_taid", "special_purp_dist4_taid"
    ];

    public string StateCode => "TX";
    public string SourceCode => "TX-CPA";

    public string Describe(string stateName) =>
        "the Texas Comptroller's quarterly local rates for the tax authorities at each permitted sales tax location";

    public async Task<List<TaxJurisdictionRate>> LoadAsync(SourceContext context, CancellationToken cancellationToken = default)
    {
        var rates = await FetchRatesAsync(context, cancellationToken);
        var locations = await FetchLocationsAsync(context, cancellationToken);

        using var reader = new StreamReader(locations);
        var result = Build(reader, rates, context.Crosswalk.Counties(StateDirectory.Get(StateCode).Fips).Keys.ToHashSet());
        var skipped = result.UnknownAuthority + result.OverCap;
        var level = skipped * 1000 > result.Locations ? "WARN" : "INFO";
        Console.WriteLine($"  [{level}] TX: left out {result.UnknownAuthority} location(s) with an authority code not in the {rates.Quarter / 10} Q{rates.Quarter % 10} rate file and {result.OverCap} over the {LocalCapPercent}% local cap, of {result.Locations}, and {result.RareZips} ZIP(s) outside the Census ZIP areas with fewer than {MinLocationsOutsideCensusZips} locations.");
        return result.Records;
    }

    /// <param name="Quarter">yyyyq, e.g. 20264 for October–December 2026.</param>
    /// <param name="PercentByAuthority">Rate by tax authority code: 2xxxxxx city, 3xxxxxx transit, 4xxxxxx county,
    /// 5xxxxxx special purpose district, 6xxxxxx a combined area where a city and districts overlap.</param>
    public sealed record TexasRates(int Quarter, decimal StatePercent, IReadOnlyDictionary<string, decimal> PercentByAuthority)
    {
        public DateOnly QuarterStart => new(Quarter / 10, (Quarter % 10 - 1) * 3 + 1, 1);
    }

    /// <summary>
    /// The Comptroller's EDI rate file (taxrates.txt, tab-separated). The first row gives the quarter (yyyyq) and
    /// the state rate; rows of filing due dates follow; then one row per locality: name, city code, city rate,
    /// county name, county code, county rate, district name, district code, district rate, transit name, transit
    /// code, transit rate. Rates are fractions and missing codes "n/a". A locality row holds one district and one
    /// transit authority, so a code may appear only on another locality's row; together they cover them all.
    /// </summary>
    public static TexasRates ReadRates(string path)
    {
        var lines = File.ReadLines(path, Encoding.Latin1).Select(l => l.Split('\t')).ToList();
        var header = lines[0];
        var quarter = int.Parse(header[1].Trim(), CultureInfo.InvariantCulture);
        var statePercent = decimal.Parse(header[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) * 100m;

        var rates = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var f in lines.Skip(1).Where(f => f.Length >= 12))
        {
            foreach (var (code, rate) in new[] { (f[1], f[2]), (f[4], f[5]), (f[7], f[8]), (f[10], f[11]) })
            {
                var c = code.Trim();
                if (c.Length == 7 && c.All(char.IsAsciiDigit) && decimal.TryParse(rate.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction))
                {
                    rates[c] = fraction * 100m;
                }
            }
        }
        return new TexasRates(quarter, statePercent, rates);
    }

    public record BuildResult(List<TaxJurisdictionRate> Records, int Locations, int UnknownAuthority, int OverCap, int RareZips);

    /// <summary>
    /// Columns (from <see cref="DownloadLocationsAsync"/>): loc_zip, loc_city, loc_county (the Comptroller's
    /// alphabetical county number, 1–254), then the authority codes. A location in a combined area has its
    /// combined code instead of a city code, so adding up every code never counts a levy twice.
    /// </summary>
    /// <param name="censusZips">2020 Census ZIP areas (ZCTAs) in Texas. Other ZIPs (PO box and new ZIPs, and
    /// mistyped ones) are kept only with enough locations.</param>
    public static BuildResult Build(TextReader locations, TexasRates rates, IReadOnlySet<string> censusZips)
    {
        var state = StateDirectory.Get("TX");
        var effective = ZipRateAggregator.ToDateInt(rates.QuarterStart);
        var header = SstParser.SplitCsvLine(locations.ReadLine() ?? string.Empty);
        int Column(string name) => Array.IndexOf(header, name) is var i and >= 0 ? i : throw new InvalidDataException($"{LocationsFile} has no {name} column.");
        var (zipAt, cityAt, countyAt) = (Column("loc_zip"), Column("loc_city"), Column("loc_county"));
        var authorityAt = AuthorityColumns.Select(Column).ToArray();

        var aggregator = new ZipRateAggregator();
        var perZip = new Dictionary<string, int>(StringComparer.Ordinal);
        int count = 0, unknown = 0, overCap = 0;
        string? line;
        while ((line = locations.ReadLine()) != null)
        {
            var f = SstParser.SplitCsvLine(line);
            if (f.Length < header.Length) continue;
            var zip = f[zipAt].Trim();
            if (zip.Length != 5 || !zip.All(char.IsAsciiDigit)) continue;
            count++;

            decimal city = 0m, county = 0m, district = 0m;
            var known = true;
            for (var k = 0; k < AuthorityColumns.Length && known; k++)
            {
                var code = f[authorityAt[k]].Trim();
                if (code.Length == 0) continue;
                if (!rates.PercentByAuthority.TryGetValue(code, out var percent))
                {
                    known = false;
                    continue;
                }
                switch (AuthorityColumns[k])
                {
                    case "city_taid" or "unique_taid": city += percent; break;
                    case "county_taid": county += percent; break;
                    default: district += percent; break;
                }
            }
            if (!known) { unknown++; continue; }
            if (city + county + district > LocalCapPercent) { overCap++; continue; }

            aggregator.Add(zip, ZipRateAggregator.ZipRecord, new RateComponents(rates.StatePercent, county, city, district, CountyFips(f[countyAt]), effective), f[cityAt].Trim());
            perZip[zip] = perZip.GetValueOrDefault(zip) + 1;
        }

        var records = aggregator.Build(state, "TX-CPA", includeCity: true, minShareForRange: 0.01);
        var kept = records.Where(r => censusZips.Contains(r.PostalCode) || perZip[r.PostalCode] >= MinLocationsOutsideCensusZips).ToList();
        return new BuildResult(kept, count, unknown, overCap, records.Count - kept.Count);
    }

    /// <summary>
    /// The FIPS code for the Comptroller's county number. Both number the 254 counties alphabetically, the FIPS code
    /// in odd numbers, but the Census Bureau sorts McCulloch, McLennan and McMullen as "Mac" (before Madison) and the
    /// Comptroller after Maverick.
    /// </summary>
    public static string CountyFips(string comptrollerNumber)
    {
        if (!int.TryParse(comptrollerNumber.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 254) return string.Empty;
        var alphabetical = n switch
        {
            >= 154 and <= 159 => n + 3, // Madison .. Maverick
            >= 160 and <= 162 => n - 6, // McCulloch, McLennan, McMullen
            _ => n
        };
        return (2 * alphabetical - 1).ToString("000", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The rate file for the as-of quarter: the current taxrates.txt, or taxrates{yy}{q}.txt for an earlier quarter.
    /// Rates change only at the start of a quarter.
    /// </summary>
    private static async Task<TexasRates> FetchRatesAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var current = ReadRates(await context.FetchAsync("TX", "taxrates.txt", RatesBaseUrl + "taxrates.txt", refresh: true, cancellationToken));
        var asOfQuarter = context.AsOf.Year * 10 + (context.AsOf.Month + 2) / 3;
        if (asOfQuarter > current.Quarter)
        {
            Console.WriteLine($"  [WARN] TX: the newest rate file is for {current.Quarter / 10} Q{current.Quarter % 10}, before {context.AsOf:yyyy-MM-dd}; using it.");
            return current;
        }
        if (asOfQuarter == current.Quarter) return current;

        var name = $"taxrates{asOfQuarter / 10 % 100:00}{asOfQuarter % 10}.txt";
        return ReadRates(await context.FetchAsync("TX", name, RatesBaseUrl + name, refresh: false, cancellationToken));
    }

    // The locations change daily (new permits, closures), so online runs download them again; a failed download
    // keeps the cached copy.
    private static async Task<string> FetchLocationsAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var path = Path.Combine(context.RawDirectory("TX"), LocationsFile);
        if (context.Offline)
        {
            return File.Exists(path) ? path : throw new FileNotFoundException($"{LocationsFile} is not cached in {context.RawDirectory("TX")}; run 'fetch' to download it.");
        }

        try
        {
            await DownloadLocationsAsync(context.Http, path, cancellationToken);
        }
        catch (Exception ex) when (File.Exists(path) && (SourceContext.IsDownloadFailure(ex, cancellationToken) || ex is InvalidDataException))
        {
            Console.WriteLine($"  [WARN] TX: could not download the permitted locations ({ex.Message}); using the copy from {File.GetLastWriteTimeUtc(path):yyyy-MM-dd}.");
        }
        return path;
    }

    /// <summary>
    /// Active Texas locations, a page at a time, with only the columns needed: no business names or owner addresses.
    /// </summary>
    public static async Task DownloadLocationsAsync(HttpClient http, string path, CancellationToken cancellationToken)
    {
        var select = string.Join(",", new[] { "loc_zip", "loc_city", "loc_county" }.Concat(AuthorityColumns));
        var partial = path + ".part";
        var rows = 0;
        await using (var writer = new StreamWriter(partial, false, new UTF8Encoding(false)))
        {
            for (var offset = 0; ; offset += PageSize)
            {
                var url = $"{LocationsUrl}?$select={Uri.EscapeDataString(select)}" +
                          $"&$where={Uri.EscapeDataString("out_of_business_date IS NULL AND loc_state='TX'")}" +
                          $"&$order=:id&$limit={PageSize}&$offset={offset}";
                var page = (await HttpRetry.GetStringWithRetryAsync(http, url, cancellationToken)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (offset == 0 && page.Length > 0) await writer.WriteLineAsync(page[0]);
                foreach (var line in page.Skip(1)) await writer.WriteLineAsync(line.TrimEnd('\r'));
                rows += Math.Max(0, page.Length - 1);
                if (page.Length - 1 < PageSize) break;
            }
        }

        if (rows < MinimumLocations)
        {
            File.Delete(partial);
            throw new InvalidDataException($"only {rows} locations downloaded");
        }
        File.Move(partial, path, overwrite: true);
    }
}
