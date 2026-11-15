using System.Globalization;
using System.Text.RegularExpressions;
using TaxOpus.Core.Data;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Sources;

/// <summary>
/// Alaska has no state sales tax; boroughs and cities do. The Alaska Remote Seller Sales Tax Commission (ARSSTC)
/// publishes a monthly rate sheet by ZIP code for its members, with an inside-city (I) and outside-city (O) row
/// where a ZIP crosses city limits. Each ZIP is split between those rates by 2020 population inside the city.
/// Taxes of non-member jurisdictions (collected only from local sellers) are not in the sheet.
/// </summary>
public sealed partial class AlaskaArsstcSource : IStateRateSource
{
    public const string PageUrl = "https://arsstc.org/business-sellers/tax-rates/";

    public string StateCode => "AK";
    public string SourceCode => "AK-ARSSTC";

    public string Describe(string stateName) =>
        "the ARSSTC rate sheet by ZIP code (member boroughs and cities), split by 2020 population inside city limits";

    public async Task<List<TaxJurisdictionRate>> LoadAsync(SourceContext context, CancellationToken cancellationToken = default)
    {
        string? fileName = null;
        if (!context.Offline)
        {
            try
            {
                var page = await HttpRetry.GetStringWithRetryAsync(context.Http, PageUrl, cancellationToken);
                var links = SheetLink().Matches(page).Select(m => (Url: m.Value, Name: m.Value[(m.Value.LastIndexOf('/') + 1)..]));
                var url = Newest(links.Select(l => (l.Url, l.Name)), context.AsOf, returnUrl: true)
                    ?? throw new InvalidDataException("No ARSSTC rate sheet with ZIP codes is linked from " + PageUrl);
                fileName = url[(url.LastIndexOf('/') + 1)..];
                await context.FetchAsync(StateCode, fileName, url, false, cancellationToken);
            }
            catch (Exception ex) when (SourceContext.IsDownloadFailure(ex, cancellationToken))
            {
                fileName = Newest(context.CachedFiles(StateCode, "*.xlsx").Select(f => (Url: f, Name: f)), context.AsOf);
                if (fileName == null) throw;
                Console.WriteLine($"  [WARN] AK: could not reach {PageUrl} ({ex.Message}); using cached {fileName}.");
            }
        }
        else
        {
            fileName = Newest(context.CachedFiles(StateCode, "*.xlsx").Select(f => (Url: f, Name: f)), context.AsOf)
                ?? throw new FileNotFoundException("No ARSSTC rate sheet is cached; run 'fetch' to download it.");
        }

        var path = Path.Combine(context.RawDirectory(StateCode), fileName);
        return Build(ReadSheet(path), context.Crosswalk);
    }

    public record SheetRow(string Zip, string CityName, string InOutCity, decimal CountyRate, decimal CityRate, decimal LocalRate, decimal Total, int EffectiveDate);

    public static List<TaxJurisdictionRate> Build(IEnumerable<SheetRow> sheet, ZipCrosswalk crosswalk)
    {
        var state = StateDirectory.Get("AK");
        var places = crosswalk.Places(state.Fips);
        var aggregator = new ZipRateAggregator();

        foreach (var zipRows in sheet.GroupBy(r => r.Zip))
        {
            var zip = zipRows.Key;
            var inside = zipRows.Where(r => r.InOutCity == "I").GroupBy(r => NormalizeCity(r.CityName)).ToDictionary(g => g.Key, g => g.First());
            // Rows outside any taxing city carry the borough rate; take the most common one.
            var outside = zipRows.Where(r => r.InOutCity != "I").GroupBy(r => r.Total)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.First()).FirstOrDefault();

            var links = places.GetValueOrDefault(zip) ?? [];
            var insideShare = 0.0;
            foreach (var (city, row) in inside)
            {
                var share = links.Where(l => !l.Name.Contains(" CDP", StringComparison.Ordinal) && NormalizeCity(l.Name) == city).Sum(l => l.Share);
                // Without population data for the ZIP, count each city once against the rest of the ZIP.
                if (links.Count == 0) share = 1.0 / (inside.Count + (outside != null ? 1 : 0));
                insideShare += share;
                aggregator.Add(zip, ZipRateAggregator.ZipRecord, Components(row), Title(row.CityName), share);
            }

            if (outside != null)
            {
                aggregator.Add(zip, ZipRateAggregator.ZipRecord, Components(outside), Title(outside.CityName), Math.Max(0, 1 - insideShare));
            }
        }

        return aggregator.Build(state, "AK-ARSSTC", includeCity: true, minShareForRange: 0.01);
    }

    /// <summary>
    /// Columns by header name: zip, city, inoutcity, countysalesrate, citysalesrate, localsalesrate, totalsalestax,
    /// effectivedate (Excel serial date). Rates are percents.
    /// </summary>
    public static List<SheetRow> ReadSheet(string path)
    {
        var rows = XlsxReader.ReadRows(path);
        var header = rows[0].Select((name, i) => (name: name.Trim().ToLowerInvariant(), i)).ToDictionary(x => x.name, x => x.i);
        string Cell(string[] r, string name) => header.TryGetValue(name, out var i) && i < r.Length ? r[i].Trim() : string.Empty;
        decimal Rate(string[] r, string name) => decimal.TryParse(Cell(r, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Round(v, 4) : 0m;

        return rows.Skip(1)
            .Where(r => Cell(r, "zip").Length == 5)
            .Select(r => new SheetRow(
                Cell(r, "zip"), Cell(r, "city"), Cell(r, "inoutcity").ToUpperInvariant(),
                Rate(r, "countysalesrate"), Rate(r, "citysalesrate"), Rate(r, "localsalesrate"), Rate(r, "totalsalestax"),
                XlsxReader.FromSerialDate(Cell(r, "effectivedate")) is { } d ? ZipRateAggregator.ToDateInt(d) : 0))
            .ToList();
    }

    private static RateComponents Components(SheetRow row) =>
        new(0m, row.CountyRate, row.CityRate, row.LocalRate, string.Empty, row.EffectiveDate, row.Total);

    /// <summary>"Homer city, AK", "Juneau city and borough, AK", "HOMER" all become "HOMER".</summary>
    private static string NormalizeCity(string name)
    {
        var city = name.Split(',')[0].ToUpperInvariant();
        foreach (var suffix in new[] { " CITY AND BOROUGH", " CITY", " BOROUGH", " MUNICIPALITY" })
        {
            if (city.EndsWith(suffix, StringComparison.Ordinal)) city = city[..^suffix.Length];
        }
        return city.Trim();
    }

    private static string Title(string city) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(city.ToLowerInvariant());

    /// <summary>Newest sheet ("...-10-1-2026.xlsx" or "...-10-1-26.xlsx") dated on or before <paramref name="asOf"/>.</summary>
    private static string? Newest(IEnumerable<(string Url, string Name)> files, DateOnly asOf, bool returnUrl = false) =>
        files
            .Select(f => (f, Date: SheetDate(f.Name)))
            .Where(x => x.Date is { } d && d <= asOf && x.f.Name.Contains("Zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Date)
            .Select(x => returnUrl ? x.f.Url : x.f.Name)
            .FirstOrDefault();

    private static DateOnly? SheetDate(string name)
    {
        var m = SheetDatePattern().Match(name);
        if (!m.Success) return null;
        var year = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
        if (year < 100) year += 2000;
        return new DateOnly(year, int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"https://arsstc\.org/wp-content/uploads/[^""'\s]+\.xlsx", RegexOptions.IgnoreCase)]
    private static partial Regex SheetLink();

    [GeneratedRegex(@"-(?<m>\d{1,2})-(?<d>\d{1,2})-(?<y>\d{2,4})\.xlsx$", RegexOptions.IgnoreCase)]
    private static partial Regex SheetDatePattern();
}
