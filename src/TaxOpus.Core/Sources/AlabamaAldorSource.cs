using System.Globalization;
using TaxOpus.Core.Data;
using TaxOpus.Core.Geo;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Sources;

/// <summary>
/// Alabama: 4% state plus county and city taxes from ALDOR's monthly rate file, which has no geography of its own.
/// The geography is assembled from public sources:
/// <list type="bullet">
/// <item>city limits: the Census Bureau's current incorporated places (TIGER/Line);</item>
/// <item>police jurisdictions (PJs), where a city may tax at up to half its rate: 3 miles beyond the city's limits
/// for cities of 6,000 or more people, 1.5 miles otherwise, frozen at the 2021 limits by Act 2021-297 and assigned
/// to the nearest city where they overlap. Whether a city taxes its PJ, and at what rate, comes from the rate
/// file's PJ and PJ_Rate columns;</item>
/// <item>county taxes that apply only inside or outside certain cities' limits or PJs:
/// data/reference/alabama_county_zones.csv, from ALDOR's county code explanations.</item>
/// </list>
/// PJ reductions in half-mile steps and sub-city districts (Spanish Fort, Millbrook) are not modelled.
/// </summary>
public sealed class AlabamaAldorSource : IPointRateSource
{
    public const string RatesUrl = "https://www.revenue.alabama.gov/wp-content/uploads/2024/03/taxrates_current.csv";
    public const string ZonesFile = "alabama_county_zones.csv";
    private const string PlaceUrl = "https://www2.census.gov/geo/tiger/TIGER{0}/PLACE/tl_{0}_01_place.zip";
    private const string CountyUrl = "https://www2.census.gov/geo/tiger/TIGER{0}/COUNTY/tl_{0}_us_county.zip";
    private const int PoliceJurisdictionYear = 2021;
    private const decimal StateRate = 4m;

    public string StateCode => "AL";
    public string SourceCode => "AL-DOR";

    public string Describe(string stateName) =>
        "ALDOR's city and county rates over Census city limits, police jurisdictions computed from the 2021 limits, and ALDOR's county zones, weighted by 2020 Census blocks";

    public async Task<List<TaxJurisdictionRate>> LoadAsync(SourceContext context, CancellationToken cancellationToken = default)
    {
        var state = StateDirectory.Get(StateCode);
        var model = await LoadModelAsync(context, cancellationToken);
        foreach (var name in model.UnmatchedCities)
        {
            Console.WriteLine(name.Contains(" DIST", StringComparison.Ordinal)
                ? $"  [INFO] AL: \"{name}\" is a district inside a city with no public boundary; its rate is left out."
                : $"  [WARN] AL: no Census city limits for \"{name}\" in ALDOR's rate file; its city tax is left out.");
        }
        foreach (var name in model.UnmatchedZoneNames)
        {
            Console.WriteLine($"  [WARN] AL: {ZonesFile} names \"{name}\", which is not a Census city; rules naming it never match.");
        }
        var blocks = await CensusBlocks.LoadAsync(context, state.Fips, cancellationToken);
        return PolygonZipRates.Build(state, SourceCode, blocks, model.RateAt, context.Crosswalk);
    }

    public async Task<Func<double, double, RateComponents?>> LoadPointLookupAsync(SourceContext context, CancellationToken cancellationToken = default) =>
        (await LoadModelAsync(context, cancellationToken)).RateAt;

    private async Task<AlabamaRateModel> LoadModelAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var state = StateDirectory.Get(StateCode);
        var ratesPath = await context.FetchAsync(StateCode, "taxrates_current.csv", RatesUrl, refresh: true, cancellationToken);
        var currentPlaces = await LatestTigerAsync(context, "AL", "tl_{0}_01_place.zip", PlaceUrl, cancellationToken);
        var pjPlaces = await context.FetchAsync(StateCode, $"tl_{PoliceJurisdictionYear}_01_place.zip", string.Format(CultureInfo.InvariantCulture, PlaceUrl, PoliceJurisdictionYear), false, cancellationToken);
        var counties = await LatestTigerAsync(context, "census", "tl_{0}_us_county.zip", CountyUrl, cancellationToken);

        return new AlabamaRateModel(
            AlabamaRates.Read(ratesPath, context.AsOf),
            AlabamaRates.ReadZones(Path.Combine(context.ReferenceDirectory, ZonesFile)),
            Incorporated(currentPlaces),
            Incorporated(pjPlaces),
            context.Crosswalk.PlacePopulations(state.Fips),
            Shapefile.ReadPolygons(counties, ["STATEFP", "COUNTYFP"], a => a[0] == state.Fips).Select(c => (c.Polygon, c.Attributes[1])));
    }

    // Incorporated places only (class C*, functional status A); Census-designated places are not cities.
    private static IEnumerable<(GeoPolygon Polygon, string Geoid, string Name)> Incorporated(string placeZip) =>
        Shapefile.ReadPolygons(placeZip, ["GEOID", "NAME", "CLASSFP", "FUNCSTAT"], a => a[2].StartsWith('C') && a[3] == "A")
            .Select(p => (p.Polygon, p.Attributes[0], p.Attributes[1]));

    /// <summary>The newest TIGER/Line vintage up to the as-of year (each year's files appear in the autumn).</summary>
    private static async Task<string> LatestTigerAsync(SourceContext context, string folder, string namePattern, string urlPattern, CancellationToken cancellationToken)
    {
        for (var year = context.AsOf.Year; year >= context.AsOf.Year - 2; year--)
        {
            var name = string.Format(CultureInfo.InvariantCulture, namePattern, year);
            try
            {
                return await context.FetchAsync(folder, name, string.Format(CultureInfo.InvariantCulture, urlPattern, year), false, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or FileNotFoundException)
            {
            }
        }
        throw new FileNotFoundException($"No TIGER/Line file like {namePattern} for {context.AsOf.Year - 2}-{context.AsOf.Year}.");
    }
}

/// <summary>
/// ALDOR's general sales tax (ST / GENER) rows in effect on a date, by city and county code.
/// </summary>
public sealed class AlabamaRates
{
    /// <param name="Counties">County numbers (ALDOR's alphabetical 1–67) where this city code applies.</param>
    /// <param name="Name">The rate file's name without qualifiers such as " WITHIN LEE CO" or " S&amp;U".</param>
    public record CityRate(string Code, string Name, IReadOnlySet<int> Counties, decimal Rate, decimal PoliceJurisdictionRate, int EffectiveDate);

    public record CountyRate(string Code, int County, decimal Rate, int EffectiveDate);

    public required IReadOnlyList<CityRate> Cities { get; init; }
    public required IReadOnlyList<CountyRate> Counties { get; init; }

    /// <summary>
    /// Columns: Locality Code, Locality Name, County Number, TaxType, Rate Type, Administered, Active Date,
    /// Inactive Date, Rate, Indicator, PJ, County Code, PJ_Rate. City rows repeat once per county code the city
    /// touches (the same levy, not extra ones). "UNABATED" codes are abatement accounting and are skipped.
    /// </summary>
    public static AlabamaRates Read(string path, DateOnly asOf)
    {
        var asOfInt = ZipRateAggregator.ToDateInt(asOf);
        var rows = File.ReadLines(path).Skip(1)
            .Select(SstParser.SplitCsvLine)
            .Where(f => f.Length >= 13 && f[3].Trim() == "ST" && f[4].Trim() == "GENER" && !f[1].Trim().StartsWith("UNABA", StringComparison.Ordinal))
            .Select(f => (Code: f[0].Trim(), Name: f[1].Trim(), County: Int(f[2]), Active: Int(f[6]), Inactive: Int(f[7]), Rate: Dec(f[8]), Pj: f[10].Trim() == "Y", CountyCode: f[11].Trim(), PjRate: Dec(f[12])))
            .Where(r => r.Active <= asOfInt && (r.Inactive == 0 || r.Inactive > asOfInt))
            .ToList();

        // A code's rows can carry several effective dates; keep each code's latest in effect.
        var current = rows.GroupBy(r => (r.Code, r.CountyCode)).Select(g => g.MaxBy(r => r.Active)).ToList();

        var countyOfCode = current.Where(r => r.Code.StartsWith('7')).GroupBy(r => r.Code).ToDictionary(g => g.Key, g => g.First().County);
        var counties = current.Where(r => r.Code.StartsWith('7'))
            .GroupBy(r => r.Code)
            .Select(g => new CountyRate(g.Key, g.First().County, g.Sum(r => r.Rate), g.Max(r => r.Active)))
            .ToList();
        var cities = current.Where(r => r.Code.StartsWith('9'))
            .GroupBy(r => r.Code)
            .Select(g => new CityRate(
                g.Key,
                CityName(g.First().Name),
                g.Select(r => countyOfCode.GetValueOrDefault(r.CountyCode)).Where(c => c > 0).ToHashSet(),
                g.Max(r => r.Rate),
                g.Any(r => r.Pj) ? g.Where(r => r.Pj).Max(r => r.PjRate) : 0m,
                g.Max(r => r.Active)))
            .ToList();

        return new AlabamaRates { Cities = cities, Counties = counties };
    }

    /// <summary>Zone rules by county code, from data/reference/alabama_county_zones.csv.</summary>
    public static Dictionary<string, string> ReadZones(string path)
    {
        var zones = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path).Where(l => l.Length > 0 && !l.StartsWith('#')).Skip(1))
        {
            var f = SstParser.SplitCsvLine(line);
            if (f.Length < 3) continue;
            zones[f[0].Trim()] = f[2].Trim();
        }
        return zones;
    }

    /// <summary>"PHENIX CITY WITHIN LEE CO", "FAIRFIELD S&amp;U", "WESTOVER SALES &amp; USE" to the city's name.</summary>
    private static string CityName(string name)
    {
        foreach (var suffix in new[] { " WITHIN", " S&U", " SALES" })
        {
            var at = name.IndexOf(suffix, StringComparison.Ordinal);
            if (at > 0) name = name[..at];
        }
        return name;
    }

    private static int Int(string value) => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static decimal Dec(string value) => decimal.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0m;
}

/// <summary>
/// The rate at a point: state, the county codes whose zones contain it, and the city's rate (inside its limits)
/// or police jurisdiction rate (outside all limits, within the nearest city's PJ).
/// </summary>
public sealed class AlabamaRateModel
{
    private const double MilesPerDegreeLatitude = 69.0;
    private const double LargeCityRadiusMiles = 3.0;
    private const double SmallCityRadiusMiles = 1.5;
    private const int LargeCityPopulation = 6000;

    private readonly AlabamaRates _rates;
    private readonly Dictionary<string, string> _zones;
    private readonly ILookup<int, (AlabamaRates.CountyRate Code, Func<string?, string?, bool> Applies)> _countyCodes;
    private readonly Dictionary<string, List<AlabamaRates.CityRate>> _citiesByName;
    private readonly PolygonIndex<string> _cityLimits;
    private readonly HashSet<string> _cityKeys;
    private readonly HashSet<string> _pjCityKeys = [];
    private readonly PolygonIndex<int> _pjCityLimits;
    private readonly PolygonIndex<int> _counties;
    private readonly List<(string Name, double RadiusMiles)> _pjCities = [];
    private readonly Dictionary<(int, int), List<(int City, double X1, double Y1, double X2, double Y2)>> _pjEdges = [];
    private const double EdgeCell = 0.02;

    public AlabamaRateModel(
        AlabamaRates rates,
        Dictionary<string, string> zones,
        IEnumerable<(GeoPolygon Polygon, string Geoid, string Name)> currentCities,
        IEnumerable<(GeoPolygon Polygon, string Geoid, string Name)> policeJurisdictionCities,
        IReadOnlyDictionary<string, int> placePopulations,
        IEnumerable<(GeoPolygon Polygon, string CountyFips)> counties)
    {
        _rates = rates;
        _zones = zones;
        _countyCodes = rates.Counties.ToLookup(c => c.County, c => (c, Compile(zones.GetValueOrDefault(c.Code, "all"))));
        _citiesByName = rates.Cities.GroupBy(c => Key(c.Name)).ToDictionary(g => g.Key, g => g.ToList());
        var cities = currentCities.Select(c => (c.Polygon, Key: Key(c.Name))).ToList();
        _cityLimits = new PolygonIndex<string>(cities.Select(c => (c.Polygon, c.Key)));
        _cityKeys = cities.Select(c => c.Key).ToHashSet();
        _counties = new PolygonIndex<int>(counties.Select(c => (c.Polygon, (int.Parse(c.CountyFips, CultureInfo.InvariantCulture) + 1) / 2)));

        var pj = new List<(GeoPolygon, int)>();
        foreach (var (polygon, geoid, name) in policeJurisdictionCities)
        {
            var radius = placePopulations.GetValueOrDefault(geoid) >= LargeCityPopulation ? LargeCityRadiusMiles : SmallCityRadiusMiles;
            _pjCities.Add((Key(name), radius));
            _pjCityKeys.Add(Key(name));
            pj.Add((polygon, _pjCities.Count - 1));
            foreach (var (x1, y1, x2, y2) in polygon.Edges())
            {
                for (var cx = Cell(Math.Min(x1, x2)); cx <= Cell(Math.Max(x1, x2)); cx++)
                for (var cy = Cell(Math.Min(y1, y2)); cy <= Cell(Math.Max(y1, y2)); cy++)
                {
                    if (!_pjEdges.TryGetValue((cx, cy), out var list)) _pjEdges[(cx, cy)] = list = [];
                    list.Add((_pjCities.Count - 1, x1, y1, x2, y2));
                }
            }
        }
        _pjCityLimits = new PolygonIndex<int>(pj);
    }

    /// <summary>
    /// Rate-file cities with a tax that match no current Census city (renamed, newly incorporated, or a district
    /// inside a city).
    /// </summary>
    public IEnumerable<string> UnmatchedCities =>
        _rates.Cities.Where(c => (c.Rate > 0 || c.PoliceJurisdictionRate > 0) && !_cityKeys.Contains(Key(c.Name)) && !(Key(c.Name).EndsWith("CITY", StringComparison.Ordinal) && _cityKeys.Contains(Key(c.Name)[..^4])))
            .Select(c => c.Name).Distinct().Order();

    /// <summary>City names in the zone rules that match no current or 2021 Census city.</summary>
    public IEnumerable<string> UnmatchedZoneNames =>
        _zones.Values.SelectMany(RuleNames).Where(n => n != "*" && !_cityKeys.Contains(Key(n)) && !_pjCityKeys.Contains(Key(n))).Distinct().Order();

    /// <summary>
    /// Compares city names across ALDOR, Census and the zone rules: upper case letters without spaces, so "La Fayette"
    /// matches "LAFAYETTE", plus ALDOR's abbreviations.
    /// </summary>
    public static string Key(string name)
    {
        var normal = CrosswalkZipRates.NormalizeName(name);
        return Aliases.GetValueOrDefault(normal, normal).Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static readonly Dictionary<string, string> Aliases = new() { ["OWENS CROSS RD"] = "OWENS CROSS ROADS" };

    private static IEnumerable<string> RuleNames(string rule) =>
        rule == "all" ? [] : rule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(c => c[(c.IndexOf(':') + 1)..].Split(',', StringSplitOptions.TrimEntries));

    public RateComponents? RateAt(double x, double y)
    {
        if (!_counties.TryFind(x, y, out var county)) return null;

        string? cityLimits = _cityLimits.TryFind(x, y, out var city) ? city : null;
        var policeJurisdiction = cityLimits == null ? NearestPoliceJurisdiction(x, y) : null;

        var countyRate = 0m;
        var effective = 0;
        foreach (var (code, applies) in _countyCodes[county])
        {
            if (!applies(cityLimits, policeJurisdiction)) continue;
            countyRate += code.Rate;
            effective = Math.Max(effective, code.EffectiveDate);
        }

        var cityRate = 0m;
        if ((cityLimits ?? policeJurisdiction) is { } name && FindCity(name, county) is { } levy)
        {
            cityRate = cityLimits != null ? levy.Rate : levy.PoliceJurisdictionRate;
            effective = Math.Max(effective, levy.EffectiveDate);
        }

        var countyFips = (county * 2 - 1).ToString("000", CultureInfo.InvariantCulture);
        return new RateComponents(4m, countyRate, cityRate, 0m, countyFips, effective);
    }

    private AlabamaRates.CityRate? FindCity(string name, int county)
    {
        // ALDOR names a few cities "TUSCALOOSA CITY", "MADISON CITY"; Census has Tuscaloosa, Madison
        if (!_citiesByName.TryGetValue(name, out var levies) && !_citiesByName.TryGetValue(name + "CITY", out levies)) return null;
        return levies.FirstOrDefault(l => l.Counties.Contains(county)) ?? levies[0];
    }

    /// <summary>The nearest city whose police jurisdiction (2021 limits plus its radius) contains the point.</summary>
    private string? NearestPoliceJurisdiction(double x, double y)
    {
        if (_pjCityLimits.TryFind(x, y, out var inside)) return _pjCities[inside].Name; // de-annexed since 2021

        var milesPerDegreeLongitude = MilesPerDegreeLatitude * Math.Cos(y * Math.PI / 180);
        var reachX = LargeCityRadiusMiles / milesPerDegreeLongitude;
        var reachY = LargeCityRadiusMiles / MilesPerDegreeLatitude;
        var best = (City: -1, Miles: double.MaxValue);
        var seen = new Dictionary<int, double>();

        for (var cx = Cell(x - reachX); cx <= Cell(x + reachX); cx++)
        for (var cy = Cell(y - reachY); cy <= Cell(y + reachY); cy++)
        {
            if (!_pjEdges.TryGetValue((cx, cy), out var edges)) continue;
            foreach (var (c, x1, y1, x2, y2) in edges)
            {
                var miles = SegmentMiles(x, y, x1, y1, x2, y2, milesPerDegreeLongitude);
                if (miles < seen.GetValueOrDefault(c, double.MaxValue)) seen[c] = miles;
            }
        }

        foreach (var (c, miles) in seen)
        {
            if (miles <= _pjCities[c].RadiusMiles && miles < best.Miles) best = (c, miles);
        }
        return best.City >= 0 ? _pjCities[best.City].Name : null;
    }

    /// <summary>
    /// Evaluates a zone rule such as "notcl:TROY" or "notcl:*;notpj:OPELIKA,PHENIX CITY" (see alabama_county_zones.csv)
    /// for a point inside <paramref name="cityLimits"/> or <paramref name="policeJurisdiction"/> (city keys, or null).
    /// </summary>
    public static bool Matches(string rule, string? cityLimits, string? policeJurisdiction) => Compile(rule)(cityLimits, policeJurisdiction);

    private static Func<string?, string?, bool> Compile(string rule)
    {
        var conditions = new List<Func<string?, string?, bool>>();
        foreach (var condition in rule == "all" ? [] : rule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = condition.IndexOf(':');
            var names = condition[(colon + 1)..].Split(',', StringSplitOptions.TrimEntries).Select(n => n == "*" ? n : Key(n)).ToHashSet();
            bool In(string? name) => name != null && (names.Contains("*") || names.Contains(name));
            conditions.Add(condition[..colon] switch
            {
                "cl" => (cl, _) => In(cl),
                "notcl" => (cl, _) => !In(cl),
                "pj" => (_, pj) => In(pj),
                "notpj" => (_, pj) => !In(pj),
                "clpj" => (cl, pj) => In(cl) || In(pj),
                _ => throw new InvalidDataException($"Unknown zone condition \"{condition}\".")
            });
        }
        return (cl, pj) => conditions.TrueForAll(c => c(cl, pj));
    }

    private static double SegmentMiles(double x, double y, double x1, double y1, double x2, double y2, double milesPerDegreeLongitude)
    {
        // Local flat projection in miles around the point
        var (px, py) = (0.0, 0.0);
        var (ax, ay) = ((x1 - x) * milesPerDegreeLongitude, (y1 - y) * MilesPerDegreeLatitude);
        var (bx, by) = ((x2 - x) * milesPerDegreeLongitude, (y2 - y) * MilesPerDegreeLatitude);
        var (dx, dy) = (bx - ax, by - ay);
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lengthSquared, 0, 1);
        var (cx, cy) = (ax + t * dx, ay + t * dy);
        return Math.Sqrt(cx * cx + cy * cy);
    }

    private static int Cell(double v) => (int)Math.Floor(v / EdgeCell);
}
