using System.Globalization;
using System.Text.Json;
using TaxOpus.Core.Data;
using TaxOpus.Core.Geo;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Sources;

/// <summary>
/// California: CDTFA's sales and use tax rate areas, one polygon per tax area code with its combined rate (state,
/// county and city parts plus district taxes), published as a public ArcGIS layer for the current quarter (and the
/// next, about six weeks ahead). Census blocks placed in the polygons give population-weighted ZIP rates, so
/// district taxes that cover part of a city or an unincorporated area land where they apply.
/// </summary>
public sealed class CaliforniaCdtfaSource : IPointRateSource
{
    public const string LayerUrl = "https://services6.arcgis.com/snwvZ3EmaoXJiugR/arcgis/rest/services/California_Sales_and_Use_Tax_Rates/FeatureServer/0";

    public string StateCode => "CA";
    public string SourceCode => "CA-CDTFA";

    public string Describe(string stateName) =>
        "CDTFA's sales and use tax rate areas, with 2020 Census blocks placed in them to weight each ZIP by population";

    public async Task<List<TaxJurisdictionRate>> LoadAsync(SourceContext context, CancellationToken cancellationToken = default)
    {
        var state = StateDirectory.Get(StateCode);
        var rateAt = await LoadPointLookupAsync(context, cancellationToken);
        var blocks = await CensusBlocks.LoadAsync(context, state.Fips, cancellationToken);
        return PolygonZipRates.Build(state, SourceCode, blocks, rateAt, context.Crosswalk);
    }

    public async Task<Func<double, double, RateComponents?>> LoadPointLookupAsync(SourceContext context, CancellationToken cancellationToken = default)
    {
        string path;
        try
        {
            path = context.Offline ? Cached(context) : await DownloadQuarterAsync(context, cancellationToken);
        }
        catch (Exception ex) when (SourceContext.IsDownloadFailure(ex, cancellationToken) && context.CachedFiles("CA", "cdtfa_rates_*.geojson").Any())
        {
            path = Cached(context);
            Console.WriteLine($"  [WARN] CA: could not reach CDTFA ({ex.Message}); using the cached {Path.GetFileName(path)}.");
        }
        var index = new PolygonIndex<RateComponents>(ReadRateAreas(path));
        return (x, y) => index.TryFind(x, y, out var components) ? components : null;
    }

    /// <summary>
    /// Rate areas with their combined rate. CDTFA gives RATE plus StateRate, CountyRate and CityRate as fractions;
    /// what those parts don't cover is district taxes.
    /// </summary>
    public static IEnumerable<(GeoPolygon, RateComponents)> ReadRateAreas(string geoJsonPath) =>
        GeoJson.ReadFeatures(geoJsonPath).Select(f =>
        {
            var total = Percent(f.Properties, "RATE");
            var stateRate = Percent(f.Properties, "StateRate");
            var county = Percent(f.Properties, "CountyRate");
            var city = Percent(f.Properties, "CityRate");
            var start = f.Properties.TryGetValue("START_DATE", out var s) && s.ValueKind == JsonValueKind.Number
                ? ZipRateAggregator.ToDateInt(EpochDate(s.GetInt64()))
                : 0;
            // CDTFA numbers counties alphabetically from 1 (Alameda) to 58 (Yuba); their FIPS codes are the odd numbers
            var countyFips = f.Properties.TryGetValue("CountyCode", out var c) && int.TryParse(c.GetString(), CultureInfo.InvariantCulture, out var n)
                ? (2 * n - 1).ToString("000", CultureInfo.InvariantCulture)
                : string.Empty;
            return (f.Polygon, new RateComponents(stateRate, county, city, total - stateRate - county - city, countyFips, start, total));
        });

    // The layer holds the current quarter and, once announced, the next one; each publish is stamped with the date
    // its rates take effect. Download the newest stamp on or before the as-of date.
    private static async Task<string> DownloadQuarterAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var stamps = await context.Http.GetStringAsync($"{LayerUrl}/query?where=1%3D1&outFields=DateStamp&returnDistinctValues=true&returnGeometry=false&f=json", cancellationToken);
        var quarter = JsonDocument.Parse(stamps).RootElement.GetProperty("features").EnumerateArray()
            .Select(f => EpochDate(f.GetProperty("attributes").GetProperty("DateStamp").GetInt64()))
            .Where(d => d <= context.AsOf)
            .DefaultIfEmpty()
            .Max();
        if (quarter == default) throw new InvalidDataException($"CDTFA's layer has no rates in effect on {context.AsOf:yyyy-MM-dd}.");

        var fileName = $"cdtfa_rates_{quarter:yyyy-MM-dd}.geojson";
        var path = Path.Combine(context.RawDirectory("CA"), fileName);
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        var where = $"DateStamp >= DATE '{quarter:yyyy-MM-dd}' AND DateStamp < DATE '{quarter.AddDays(1):yyyy-MM-dd}'";
        await ColoradoArcGis.DownloadGeoJsonAsync(context.Http, LayerUrl, where, "*", path, cancellationToken, pageSize: 1000);
        return path;
    }

    private static string Cached(SourceContext context) =>
        context.CachedFiles("CA", "cdtfa_rates_*.geojson")
            .Select(name => (Name: name, Date: DateOnly.TryParseExact(name["cdtfa_rates_".Length..^".geojson".Length], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null))
            .Where(f => f.Date is { } d && d <= context.AsOf)
            .OrderByDescending(f => f.Date)
            .Select(f => Path.Combine(context.RawDirectory("CA"), f.Name))
            .FirstOrDefault() ?? throw new FileNotFoundException("No CDTFA rate areas are cached; run 'fetch' to download them.");

    // ArcGIS dates are milliseconds since 1970 UTC at local midnight in California: 07:00 UTC in summer, 08:00 in
    // winter. Shifting by 7 hours lands on the right day in both.
    private static DateOnly EpochDate(long milliseconds) =>
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime.AddHours(-7));

    private static decimal Percent(IReadOnlyDictionary<string, JsonElement> properties, string name) =>
        properties.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Round(v.GetDecimal() * 100m, 4) : 0m;
}
