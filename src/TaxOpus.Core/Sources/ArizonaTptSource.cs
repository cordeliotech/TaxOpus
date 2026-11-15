using System.Globalization;
using System.Text.RegularExpressions;
using TaxOpus.Core.Data;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Sources;

/// <summary>
/// Arizona transaction privilege tax (TPT), retail class (017), from ADOR's monthly rate table. County rows
/// (3-letter region codes) already include the 5.6% state rate; city and town rows (2-letter codes) are the city's
/// own rate, added to its county's. Reservation and special-district regions are not mapped: they carry their
/// county's rate for retail. In-state retail sales are sourced to the seller's location.
/// </summary>
public sealed partial class ArizonaTptSource : IStateRateSource
{
    public const string PageUrl = "https://azdor.gov/business/transaction-privilege-tax/tax-rate-table";
    private const string RetailBusinessCode = "017";

    public string StateCode => "AZ";
    public string SourceCode => "AZ-DOR";

    public string Describe(string stateName) =>
        "ADOR's transaction privilege tax rate table (retail), assigned to ZIP codes by 2020 population";

    public async Task<List<TaxJurisdictionRate>> LoadAsync(SourceContext context, CancellationToken cancellationToken = default)
    {
        string path;
        if (!context.Offline)
        {
            try
            {
                var page = await HttpRetry.GetStringWithRetryAsync(context.Http, PageUrl, cancellationToken);
                var links = FileLink().Matches(page).Select(m => (Url: m.Value, Name: m.Value[(m.Value.LastIndexOf('/') + 1)..]));
                var (url, name) = SelectForDate(links, context.AsOf)
                    ?? throw new FileNotFoundException("No Arizona TPT rate table dated by " + context.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                path = await context.FetchAsync(StateCode, name, url, false, cancellationToken);
            }
            catch (Exception ex) when (SourceContext.IsDownloadFailure(ex, cancellationToken))
            {
                var cached = SelectForDate(context.CachedFiles(StateCode, "TPT_RATETABLE_ALL_*.csv").Select(name => (Url: name, Name: name)), context.AsOf);
                if (cached == null) throw;
                path = Path.Combine(context.RawDirectory(StateCode), cached.Value.Name);
                Console.WriteLine($"  [WARN] AZ: could not reach {PageUrl} ({ex.Message}); using cached {cached.Value.Name}.");
            }
        }
        else
        {
            var cached = SelectForDate(context.CachedFiles(StateCode, "TPT_RATETABLE_ALL_*.csv").Select(name => (Url: name, Name: name)), context.AsOf)
                ?? throw new FileNotFoundException("No Arizona TPT rate table dated by " + context.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            path = Path.Combine(context.RawDirectory(StateCode), cached.Name);
        }

        return Build(ReadRetailRates(path, context.AsOf), context.Crosswalk);
    }

    private static (string Url, string Name)? SelectForDate(IEnumerable<(string Url, string Name)> files, DateOnly asOf)
    {
        var match = files
            .Select(f => (f.Url, f.Name, Date: FileDate(f.Name)))
            .Where(f => f.Date is { } d && d <= asOf)
            .OrderByDescending(f => f.Date)
            .FirstOrDefault();
        return match.Name == null ? null : (match.Url, match.Name);
    }

    public record RegionRate(string RegionCode, string RegionName, decimal RatePercent, int EffectiveDate);

    public static List<TaxJurisdictionRate> Build(IReadOnlyList<RegionRate> retail, ZipCrosswalk crosswalk)
    {
        var state = StateDirectory.Get("AZ");
        var countyFips = CountyCodes(crosswalk, state);
        var placeKeys = PlaceKeys(crosswalk, state);

        // Counties: 3-letter codes whose name is exactly a county's (not "HOPI (COCONINO)" or "... DISTRICT").
        var countyRates = new Dictionary<string, AreaRate>();
        foreach (var r in retail.Where(r => r.RegionCode.Length == 3))
        {
            if (countyFips.TryGetValue(CrosswalkZipRates.NormalizeName(r.RegionName), out var county))
            {
                countyRates[county] = new AreaRate(r.RatePercent, r.EffectiveDate);
            }
        }

        // Cities and towns: 2-letter codes, except special tax districts.
        var cityAddOns = new Dictionary<string, AreaRate>();
        foreach (var r in retail.Where(r => r.RegionCode.Length == 2 && !r.RegionName.Contains("DISTRICT", StringComparison.OrdinalIgnoreCase)))
        {
            if (placeKeys.TryGetValue(CrosswalkZipRates.NormalizeName(r.RegionName), out var place))
            {
                cityAddOns[place] = new AreaRate(r.RatePercent, r.EffectiveDate);
            }
        }

        return CrosswalkZipRates.Build(state, "AZ-DOR", crosswalk, state.BaseStateRatePercent, countyRates, placeAddOns: cityAddOns);
    }

    /// <summary>
    /// Columns: RegionCode, RegionName, BusinessCode, BusinessCodesName, TaxRate, TaxRateType, RateStartDate,
    /// RateEndDate. Rates are percents; dates look like "7/01/2025 0:00".
    /// </summary>
    public static List<RegionRate> ReadRetailRates(string path, DateOnly asOf)
    {
        var rates = new List<RegionRate>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = SstParser.SplitCsvLine(line);
            if (f.Length < 8 || f[2].Trim() != RetailBusinessCode || f[5].Trim() != "Percent") continue;
            if (ParseDate(f[6]) is not { } start || start > asOf || (ParseDate(f[7]) is { } end && end < asOf)) continue;
            rates.Add(new RegionRate(f[0].Trim(), f[1].Trim(), decimal.Parse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture), ZipRateAggregator.ToDateInt(start)));
        }
        return rates;
    }

    internal static Dictionary<string, string> CountyCodes(ZipCrosswalk crosswalk, UsStateInfo state) =>
        crosswalk.Counties(state.Fips).Values.SelectMany(l => l)
            .GroupBy(l => l.Code)
            .ToDictionary(g => CrosswalkZipRates.NormalizeName(g.First().Name.Replace(" " + state.Code, string.Empty, StringComparison.Ordinal)), g => g.Key);

    /// <summary>
    /// Places by normalised name ("Phoenix city, AZ" and "PHOENIX" both become "PHOENIX"). Incorporated places
    /// come first; a Census-designated place fills in only where no incorporated place has the name, for towns
    /// incorporated since 2020 (San Tan Valley, AZ).
    /// </summary>
    internal static Dictionary<string, string> PlaceKeys(ZipCrosswalk crosswalk, UsStateInfo state)
    {
        var keys = new Dictionary<string, string>();
        var places = crosswalk.Places(state.Fips).Values.SelectMany(l => l)
            .Where(l => !l.Code.EndsWith(ZipCrosswalk.OutsideAnyPlace, StringComparison.Ordinal))
            .OrderBy(l => l.Name.Contains(" CDP", StringComparison.Ordinal));
        foreach (var place in places)
        {
            keys.TryAdd(CrosswalkZipRates.NormalizeName(place.Name, "CITY", "TOWN", "VILLAGE", "CDP"), place.Code);
        }
        return keys;
    }

    private static DateOnly? ParseDate(string text) =>
        DateTime.TryParseExact(text.Trim(), ["M/dd/yyyy H:mm", "M/d/yyyy H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? DateOnly.FromDateTime(d)
            : null;

    /// <summary>TPT_RATETABLE_ALL_11012026.csv is the table for November 1, 2026.</summary>
    private static DateOnly? FileDate(string name)
    {
        var m = FileDatePattern().Match(name);
        return m.Success && DateOnly.TryParseExact(m.Groups["d"].Value, "MMddyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    [GeneratedRegex(@"https://azdor\.gov/sites/default/files/document/TPT_RATETABLE_ALL_\d{8}\.csv", RegexOptions.IgnoreCase)]
    private static partial Regex FileLink();

    [GeneratedRegex(@"TPT_RATETABLE_ALL_(?<d>\d{8})\.csv$", RegexOptions.IgnoreCase)]
    private static partial Regex FileDatePattern();
}
