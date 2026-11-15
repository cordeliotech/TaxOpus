using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TaxOpus.Core.Data;
using TaxOpus.Core.Geo;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Sources;

/// <summary>
/// Colorado: CDOR's location codes (Colorado_Jurisdiction_Codes_Rates_*.xlsx, about 450 codes, each a county plus a
/// municipality or the unincorporated area, plus special districts, with a total rate) placed on public boundaries:
/// Census counties, DOLA municipal boundaries, DOLA special districts (RTD, fire protection, health service, metro,
/// ambulance, early childhood and public improvement districts) and the Pikes Peak RTA. CDOR publishes no boundaries
/// for its codes. A point's code is the one for its county and municipality whose districts contain it
/// (data/reference/colorado_location_codes.csv says which districts set codes apart). Home-rule cities' rates are in
/// the totals, but they collect their own tax.
/// </summary>
public sealed partial class ColoradoCdorSource : IPointRateSource
{
    public const string RatesPageUrl = "https://tax.colorado.gov/how-to-look-up-sales-use-tax-rates";
    private const string DolaServices = "https://services3.arcgis.com/DgjqnJA1rgO92Soi/arcgis/rest/services";
    public const string MunicipalitiesUrl = DolaServices + "/DOLA_Municipalities_(Boundaries_Dissolved)/FeatureServer/0";
    public const string DistrictsUrl = DolaServices + "/All_Active_DOLA_Districts_-_private_staging_area_view/FeatureServer/0";
    public const string PprtaUrl = "https://services1.arcgis.com/0plDVQODvYjBRQXP/arcgis/rest/services/PPRTA_Boundary/FeatureServer/0";
    public const string CountiesUrl = "https://tigerweb.geo.census.gov/arcgis/rest/services/TIGERweb/State_County/MapServer/1";
    public const string ReferenceFile = "colorado_location_codes.csv";
    public const string RtdId = "64116";

    public string StateCode => "CO";
    public string SourceCode => "CO-CDOR";

    public string Describe(string stateName) =>
        "CDOR's location code rates placed on Census county, DOLA municipal and special district and PPRTA boundaries, " +
        "with 2020 Census blocks to weight each ZIP by population";

    public async Task<List<TaxJurisdictionRate>> LoadAsync(SourceContext context, CancellationToken cancellationToken = default)
    {
        var model = await LoadModelAsync(context, cancellationToken);
        var state = StateDirectory.Get(StateCode);
        var blocks = await CensusBlocks.LoadAsync(context, state.Fips, cancellationToken);
        return PolygonZipRates.Build(state, SourceCode, blocks, model.RateAt, context.Crosswalk);
    }

    public async Task<Func<double, double, RateComponents?>> LoadPointLookupAsync(SourceContext context, CancellationToken cancellationToken = default) =>
        (await LoadModelAsync(context, cancellationToken)).RateAt;

    public async Task<ColoradoRateModel> LoadModelAsync(SourceContext context, CancellationToken cancellationToken = default)
    {
        var (workbook, period) = await FetchWorkbookAsync(context, cancellationToken);
        var overrides = ReadOverrides(Path.Combine(context.ReferenceDirectory, ReferenceFile));
        var districtIds = overrides.Values.SelectMany(o => o.Districts).Where(d => d != ColoradoRateModel.Pprta).Append(RtdId).Distinct().Order().ToList();

        var counties = await LayerAsync(context, "co_counties.geojson", CountiesUrl, "STATE='08'", "GEOID,BASENAME", cancellationToken);
        var municipalities = await LayerAsync(context, "co_municipalities.geojson", MunicipalitiesUrl, "1=1", "city,first_city", cancellationToken);
        var districts = await LayerAsync(context, "co_districts.geojson", DistrictsUrl,
            $"lgid IN ({string.Join(",", districtIds.Select(id => $"'{id}'"))})", "lgid,lgname", cancellationToken);
        var pprta = await LayerAsync(context, "co_pprta.geojson", PprtaUrl, "1=1", "*", cancellationToken);

        var missing = districtIds.Except(districts.Select(d => Text(d.Properties, "lgid"))).ToList();
        if (missing.Count > 0)
        {
            Console.WriteLine($"  [WARN] CO: no boundary for district(s) {string.Join(", ", missing)}; codes needing them are never chosen. Run 'fetch' after changing {ReferenceFile}.");
        }

        var model = new ColoradoRateModel(ReadLocationCodes(workbook), overrides, period.From, counties, municipalities, districts, pprta);
        if (model.UnmappedCodes.Count > 0)
        {
            Console.WriteLine($"  [WARN] CO: {model.UnmappedCodes.Count} code(s) name a municipality with no DOLA boundary and are never used " +
                $"({string.Join(", ", model.UnmappedCodes.Take(3))}{(model.UnmappedCodes.Count > 3 ? ", ..." : "")}); give the DOLA name in {ReferenceFile}.");
        }
        return model;
    }

    public record LocationCode(string Code, string Name, string County, decimal TotalPercent, IReadOnlyList<(string Type, decimal Percent)> Components);

    /// <summary>
    /// The "By Juris Codes" sheet: Jurisdiction Code, Location Code (name), County, Self Collected Home Rule,
    /// three exemption columns, Total Rate, then (Tax Type, Rate, Service Fee Rate) for each component.
    /// </summary>
    public static List<LocationCode> ReadLocationCodes(string workbookPath)
    {
        var sheets = XlsxReader.SheetNames(workbookPath);
        var sheet = sheets.ToList().FindIndex(n => n.Contains("Juris", StringComparison.OrdinalIgnoreCase));
        if (sheet < 0)
        {
            throw new InvalidDataException($"No jurisdiction sheet found in {Path.GetFileName(workbookPath)}.");
        }
        var codes = new List<LocationCode>();
        foreach (var row in XlsxReader.ReadRows(workbookPath, sheet).Skip(1))
        {
            if (row.Length < 8 || row[0].Trim().Length != 6 || !decimal.TryParse(row[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var total)) continue;
            var components = new List<(string, decimal)>();
            for (var i = 8; i + 1 < row.Length; i += 3)
            {
                if (row[i].Trim().Length > 0 && decimal.TryParse(row[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
                {
                    components.Add((row[i].Trim(), Math.Round(rate, 4)));
                }
            }
            codes.Add(new LocationCode(row[0].Trim(), row[1].Trim(), row[2].Trim(), Math.Round(total, 4), components));
        }
        return codes;
    }

    public record CodeOverride(string Code, string? Municipality, IReadOnlyList<string> Districts, bool NoGeometry);

    /// <summary>Code,Municipality,Districts,NoGeometry,Note; lines starting with # are comments.</summary>
    public static Dictionary<string, CodeOverride> ReadOverrides(string path) =>
        File.ReadLines(path)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Skip(1)
            .Select(SstParser.SplitCsvLine)
            .Where(f => f.Length >= 4 && f[0].Trim().Length == 6)
            .ToDictionary(f => f[0].Trim(), f => new CodeOverride(
                f[0].Trim(),
                f[1].Trim().Length > 0 ? f[1].Trim() : null,
                f[2].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                f[3].Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)));

    // Boundary layers change (annexations, new districts), so online runs refresh them; offline runs use the cache.
    private static async Task<List<GeoJson.Feature>> LayerAsync(SourceContext context, string fileName, string layerUrl, string where, string outFields, CancellationToken cancellationToken)
    {
        var path = Path.Combine(context.RawDirectory("CO"), fileName);
        if (!context.Offline)
        {
            try
            {
                await ColoradoArcGis.DownloadGeoJsonAsync(context.Http, layerUrl, where, outFields, path, cancellationToken);
            }
            catch (Exception ex) when (File.Exists(path) && ex is HttpRequestException or InvalidDataException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"  [WARN] CO: could not refresh {fileName} ({ex.Message}); using the cached copy.");
            }
        }
        else if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{fileName} is not cached in {context.RawDirectory("CO")}; run 'fetch' to download it.");
        }
        return GeoJson.ReadFeatures(path);
    }

    /// <summary>
    /// The workbook for the half-year covering the as-of date. tax.colorado.gov often answers scripts with a
    /// Cloudflare challenge, so a failed download falls back to the newest cached workbook covering the date.
    /// </summary>
    private static async Task<(string Path, (DateOnly From, DateOnly To) Period)> FetchWorkbookAsync(SourceContext context, CancellationToken cancellationToken)
    {
        if (!context.Offline)
        {
            try
            {
                var page = await context.Http.GetStringAsync(RatesPageUrl, cancellationToken);
                var link = WorkbookLink().Matches(page).Select(m => m.Value)
                    .Select(url => (Url: url, Period: Period(url)))
                    .FirstOrDefault(l => l.Period is { } p && p.From <= context.AsOf && context.AsOf <= p.To);
                if (link.Url != null)
                {
                    var url = link.Url.StartsWith("http", StringComparison.Ordinal) ? link.Url : "https://tax.colorado.gov" + link.Url;
                    await context.FetchAsync("CO", url[(url.LastIndexOf('/') + 1)..], url, false, cancellationToken);
                }
                else
                {
                    Console.WriteLine($"  [WARN] CO: {RatesPageUrl} links no rate workbook covering {context.AsOf:yyyy-MM-dd}; using the cached copy.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Usually Cloudflare's bot challenge (403); a browser can download the workbook into data/raw/co
                Console.WriteLine($"  [WARN] CO: could not download CDOR's rate workbook ({ex.Message}); using the cached copy.");
            }
        }

        // The newest workbook starting on or before the date. One for an earlier half-year is still far closer than
        // the statewide baseline, so it is used with a warning until someone saves the new one.
        var cached = context.CachedFiles("CO", "Colorado_Jurisdiction_Codes_Rates_*.xlsx")
            .Select(name => (Name: name, Period: Period(name)))
            .Where(f => f.Period is { } p && p.From <= context.AsOf)
            .OrderByDescending(f => f.Period!.Value.From)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (cached.Name == null)
        {
            throw new FileNotFoundException($"No CDOR rate workbook for {context.AsOf:yyyy-MM-dd} or earlier is cached in {context.RawDirectory("CO")}. Download it from {RatesPageUrl}.");
        }
        if (cached.Period!.Value.To < context.AsOf)
        {
            Console.WriteLine($"  [WARN] CO: the newest cached workbook ({cached.Name}) ended {cached.Period.Value.To:yyyy-MM-dd}; save the current one from {RatesPageUrl} into {context.RawDirectory("CO")}.");
        }
        return (Path.Combine(context.RawDirectory("CO"), cached.Name), cached.Period.Value);
    }

    /// <summary>"..._July-Dec2026.xlsx" and "..._Jan-June2026_Apr26.xlsx" cover July–December and January–June.</summary>
    public static (DateOnly From, DateOnly To)? Period(string name)
    {
        var m = PeriodPattern().Match(name);
        if (!m.Success) return null;
        var year = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
        if (!DateTime.TryParseExact(m.Groups["m1"].Value[..3], "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)) return null;
        if (!DateTime.TryParseExact(m.Groups["m2"].Value[..3], "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last)) return null;
        return (new DateOnly(year, first.Month, 1), new DateOnly(year, last.Month, 1).AddMonths(1).AddDays(-1));
    }

    internal static string Text(IReadOnlyDictionary<string, JsonElement> properties, string name) =>
        properties.TryGetValue(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.GetRawText() : string.Empty;

    [GeneratedRegex(@"[^""'\s]*Colorado_Jurisdiction_Codes_Rates_[A-Za-z0-9_\-]+\.xlsx", RegexOptions.IgnoreCase)]
    private static partial Regex WorkbookLink();

    [GeneratedRegex(@"_(?<m1>[A-Za-z]{3,9})-(?<m2>[A-Za-z]{3,9})(?<y>\d{4})")]
    private static partial Regex PeriodPattern();
}

/// <summary>
/// Picks the CDOR location code for a point: its county and municipality give the candidate codes, and among those
/// whose districts contain the point, the most specific wins.
/// </summary>
public sealed class ColoradoRateModel
{
    public const string Pprta = "PPRTA";

    public record Candidate(ColoradoCdorSource.LocationCode Row, IReadOnlyList<string> Districts, bool NoGeometry);

    /// <param name="Municipality">The DOLA municipality containing the point (normalized), or empty.</param>
    /// <param name="Outcome">"single" (the only code for the place), "boundaries" (decided by district boundaries),
    /// "fallback" (no candidate's districts contain the point, so the least specific code), "composed" (a municipality
    /// with no code in this county: the unincorporated code plus the city's rate), or "no city code" (a municipality
    /// CDOR lists no code for, so no city tax: the unincorporated code).</param>
    public record Match(ColoradoCdorSource.LocationCode Row, string Outcome, decimal TotalPercent, decimal CityPercent, string CountyFips, string Municipality);

    private readonly int _effective;
    private readonly PolygonIndex<(string Fips, string Name)> _counties;
    private readonly PolygonIndex<string> _municipalities;
    private readonly PolygonIndex<string> _districts;
    private readonly PolygonIndex<bool> _pprta;
    private readonly Dictionary<string, string> _countyPrefix = [];
    private readonly Dictionary<(string Prefix, string Municipality), List<Candidate>> _groups = [];
    private readonly Dictionary<string, decimal> _cityRates = [];

    public IReadOnlyList<ColoradoCdorSource.LocationCode> Codes { get; }

    /// <summary>Codes naming a municipality that has no DOLA boundary; they are never chosen.</summary>
    public IReadOnlyList<string> UnmappedCodes { get; }

    public ColoradoRateModel(
        IReadOnlyList<ColoradoCdorSource.LocationCode> codes,
        IReadOnlyDictionary<string, ColoradoCdorSource.CodeOverride> overrides,
        DateOnly effective,
        IEnumerable<GeoJson.Feature> counties,
        IEnumerable<GeoJson.Feature> municipalities,
        IEnumerable<GeoJson.Feature> districts,
        IEnumerable<GeoJson.Feature> pprta)
    {
        Codes = codes;
        _effective = ZipRateAggregator.ToDateInt(effective);
        _counties = new PolygonIndex<(string, string)>(counties.Select(f => (f.Polygon, (ColoradoCdorSource.Text(f.Properties, "GEOID"), Normalize(ColoradoCdorSource.Text(f.Properties, "BASENAME"))))));
        var municipalityFeatures = municipalities.Select(f => (f.Polygon, Normalize(ColoradoCdorSource.Text(f.Properties, "first_city")))).ToList();
        _municipalities = new PolygonIndex<string>(municipalityFeatures);
        _districts = new PolygonIndex<string>(districts.Select(f => (f.Polygon, ColoradoCdorSource.Text(f.Properties, "lgid"))));
        _pprta = new PolygonIndex<bool>(pprta.Select(f => (f.Polygon, true)));
        var municipalityNames = municipalityFeatures.Select(m => m.Item2).ToHashSet();

        var unmapped = new List<string>();
        foreach (var row in codes)
        {
            var prefix = row.Code[..2];
            _countyPrefix.TryAdd(Normalize(row.County), prefix);
            overrides.TryGetValue(row.Code, out var o);
            var municipality = o?.Municipality == "-" ? string.Empty
                : o?.Municipality is { } named ? Normalize(named)
                : IsUnincorporatedCode(row.Code) ? string.Empty
                : Normalize(row.Name);
            if (municipality.Length > 0 && !municipalityNames.Contains(municipality))
            {
                unmapped.Add($"{row.Code} {row.Name}");
            }

            if (municipality.Length > 0 && Component(row, "City") is var city and > 0 && o?.NoGeometry != true)
            {
                _cityRates.TryAdd(municipality, city);
            }

            var key = (prefix, municipality);
            if (!_groups.TryGetValue(key, out var group)) _groups[key] = group = [];
            group.Add(new Candidate(row, o?.Districts ?? [], o?.NoGeometry == true));
        }
        UnmappedCodes = unmapped;

        // Codes for the same place that differ only by RTD (or, in El Paso County, the PPRTA) are told apart by
        // those boundaries: the code with the tax needs the point inside.
        foreach (var (key, group) in _groups.ToList())
        {
            _groups[key] = group.Select(c =>
            {
                var districts = c.Districts.ToList();
                if (Has(c.Row, "RTD") && group.Any(other => !Has(other.Row, "RTD"))) districts.Add(ColoradoCdorSource.RtdId);
                if (key.Prefix == "04" && Has(c.Row, "RTA") && group.Any(other => !Has(other.Row, "RTA")) && !districts.Contains(Pprta)) districts.Add(Pprta);
                return c with { Districts = districts };
            }).ToList();
        }
    }

    public RateComponents? RateAt(double longitude, double latitude) =>
        Locate(longitude, latitude) is { } match ? Components(match) : null;

    public Match? Locate(double longitude, double latitude)
    {
        if (!_counties.TryFind(longitude, latitude, out var county) || !_countyPrefix.TryGetValue(county.Name, out var prefix)) return null;
        var districts = _districts.FindAll(longitude, latitude).ToHashSet();
        if (_pprta.TryFind(longitude, latitude, out _)) districts.Add(Pprta);

        var municipality = _municipalities.TryFind(longitude, latitude, out var name) ? name : string.Empty;
        if (_groups.TryGetValue((prefix, municipality), out var group))
        {
            var (row, outcome) = Choose(group, districts);
            return new Match(row, outcome, row.TotalPercent, Component(row, "City"), county.Fips, municipality);
        }

        if (!_groups.TryGetValue((prefix, string.Empty), out var unincorporated))
        {
            // Denver and Broomfield are all city: a point their city boundary misses is a sliver between layers
            var (row, _) = Choose(_groups.Where(g => g.Key.Prefix == prefix).SelectMany(g => g.Value).ToList(), districts);
            return new Match(row, "fallback", row.TotalPercent, Component(row, "City"), county.Fips, municipality);
        }

        // A municipality without a code in this county: the county's unincorporated code plus the city's own rate
        var (baseRow, _) = Choose(unincorporated, districts);
        return _cityRates.TryGetValue(municipality, out var cityRate)
            ? new Match(baseRow, "composed", baseRow.TotalPercent + cityRate, cityRate, county.Fips, municipality)
            : new Match(baseRow, "no city code", baseRow.TotalPercent, 0m, county.Fips, municipality);
    }

    private static (ColoradoCdorSource.LocationCode Row, string Outcome) Choose(List<Candidate> group, HashSet<string> districts)
    {
        // A code without a boundary for what sets it apart is used only when it is the place's only code
        var fits = group.Where(c => (group.Count == 1 || !c.NoGeometry) && c.Districts.All(districts.Contains)).ToList();
        if (fits.Count > 0)
        {
            return (fits.OrderByDescending(c => c.Districts.Count).ThenBy(c => c.Row.Code, StringComparer.Ordinal).First().Row, group.Count == 1 ? "single" : "boundaries");
        }
        // Nothing fits: the least specific code
        return (group.OrderBy(c => c.NoGeometry).ThenBy(c => c.Districts.Count).ThenBy(c => c.Row.Code, StringComparer.Ordinal).First().Row, "fallback");
    }

    private RateComponents Components(Match match)
    {
        var state = Component(match.Row, "State");
        var county = Component(match.Row, "County") + Component(match.Row, "Cnty");
        return new RateComponents(state, county, match.CityPercent, match.TotalPercent - state - county - match.CityPercent, match.CountyFips[2..], _effective, match.TotalPercent);
    }

    private static decimal Component(ColoradoCdorSource.LocationCode row, string type) =>
        row.Components.Where(c => c.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).Sum(c => c.Percent);

    private static bool Has(ColoradoCdorSource.LocationCode row, string type) =>
        row.Components.Any(c => c.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && c.Percent > 0);

    private static bool IsUnincorporatedCode(string code) => code[2..] is "0206" or "0208" or "0209" or "0210";

    /// <summary>
    /// "GREEN MTN FALLS", "CHERRY HILLS VLG (MAIL VIA ENGLEWOOD)", "Mount Crested Butte", "PITKIN (in Basalt)" to
    /// "GREEN MOUNTAIN FALLS", "CHERRY HILLS VILLAGE", "MOUNT CRESTED BUTTE", "PITKIN".
    /// </summary>
    internal static string Normalize(string name)
    {
        var text = Regex.Replace(name.ToUpperInvariant(), @"\(.*?(\)|$)", " ");
        text = Regex.Replace(text, @"[^A-Z]+", " ");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w switch
        {
            "MT" => "MOUNT",
            "MTN" => "MOUNTAIN",
            "VLG" => "VILLAGE",
            _ => w
        });
        return string.Join(' ', words);
    }
}
