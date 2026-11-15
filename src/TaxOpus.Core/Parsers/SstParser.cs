using System.Globalization;
using TaxOpus.Core.Models;

namespace TaxOpus.Core.Parsers;

/// <summary>
/// Parses Streamlined Sales Tax (SST) rate and boundary files and combines them into one rate per ZIP code.
/// </summary>
public static class SstParser
{
    /// <summary>
    /// One row of an SST rate file, with the rate converted to a percentage (e.g. 4.00 for 4%).
    /// </summary>
    public record SstRate(decimal RatePercent, int BeginDate, int EndDate);

    // Rate file columns: state FIPS, jurisdiction type, jurisdiction code, general rate (intrastate),
    // general rate (interstate), food/drug rate (intrastate), food/drug rate (interstate), begin date, end date.
    private const int MinRateFields = 9;

    // Boundary file columns (0-based). "A" rows describe a street address range, "4" rows a ZIP+4 range
    // and "Z" rows a whole 5-digit ZIP. Address rows carry the ZIP in column 15, the others in columns 17-19.
    // Missouri's boundary file uses the same layout up to column 28.
    internal const int BeginDateField = 1;
    internal const int EndDateField = 2;
    private const int AddressCityField = 14;
    private const int AddressZipField = 15;
    private const int ZipLowField = 17;
    private const int ZipHighField = 19;
    internal const int CountyField = 24;
    internal const int PlaceField = 25;

    // Up to 20 special taxing districts follow, each as (code source, district code, type of taxing authority).
    private const int FirstDistrictField = 29;
    private const int DistrictFieldWidth = 3;
    private const int MaxDistricts = 20;

    private const string StateType = "45";
    private const string CountyType = "00";
    private const string PlaceType = "01";

    // A ZIP range wider than this spans a whole state (Indiana and New Jersey publish one 46001-47997 style row)
    // rather than real ZIP codes, so it is not expanded into individual ZIPs.
    private const int MaxExpandedZipRange = 200;

    /// <summary>
    /// Parses an SST rate file (CSV format).
    /// Returns map of (StateFips, JurisType, JurisCode) -> rate in effect on <paramref name="asOf"/> (default: today, UTC).
    /// </summary>
    public static Dictionary<(string StateFips, string JurisType, string JurisCode), SstRate> ParseRates(string rateCsvContent, DateOnly? asOf = null)
    {
        using var reader = new StringReader(rateCsvContent);
        return ParseRates(reader, asOf);
    }

    public static Dictionary<(string StateFips, string JurisType, string JurisCode), SstRate> ParseRates(TextReader reader, DateOnly? asOf = null)
    {
        var rates = new Dictionary<(string StateFips, string JurisType, string JurisCode), SstRate>();
        var asOfInt = ZipRateAggregator.ToDateInt(asOf ?? DateOnly.FromDateTime(DateTime.UtcNow));

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = SplitCsvLine(line);
            if (parts.Length < MinRateFields) continue;

            if (!decimal.TryParse(parts[3].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var rateFraction) || rateFraction < 0m) continue;
            if (!int.TryParse(parts[7].Trim(), CultureInfo.InvariantCulture, out var begin) || !int.TryParse(parts[8].Trim(), CultureInfo.InvariantCulture, out var end)) continue;
            if (begin > end || asOfInt < begin || asOfInt > end) continue;

            // SST publishes every rate as a decimal fraction (e.g. 0.04000 or .04000).
            var key = (parts[0].Trim().PadLeft(2, '0'), parts[1].Trim().PadLeft(2, '0'), parts[2].Trim());
            rates[key] = new SstRate(rateFraction * 100m, begin, end);
        }

        return rates;
    }

    /// <summary>
    /// Parses an SST boundary file and merges it with the rate table into one record per ZIP code.
    /// A ZIP that spans jurisdictions gets the rate of most of its address rows, with the lowest and highest
    /// rates found in the ZIP reported alongside (see <see cref="ZipRateAggregator.Build"/>).
    /// </summary>
    public static List<TaxJurisdictionRate> ParseBoundaryAndMerge(
        string stateCode,
        string boundaryCsvContent,
        Dictionary<(string StateFips, string JurisType, string JurisCode), SstRate> rates,
        DateOnly? asOf = null)
    {
        using var reader = new StringReader(boundaryCsvContent);
        return ParseBoundaryAndMerge(stateCode, reader, rates, asOf);
    }

    public static List<TaxJurisdictionRate> ParseBoundaryAndMerge(
        string stateCode,
        TextReader boundaryReader,
        Dictionary<(string StateFips, string JurisType, string JurisCode), SstRate> rates,
        DateOnly? asOf = null)
    {
        var stateInfo = StateDirectory.Get(stateCode);
        var asOfInt = ZipRateAggregator.ToDateInt(asOf ?? DateOnly.FromDateTime(DateTime.UtcNow));
        var lookup = new RateLookup(stateInfo.Fips, rates);
        var stateRate = lookup.Find(StateType, stateInfo.Fips, fallBackToAnyType: false)?.Rate;
        var aggregator = new ZipRateAggregator();

        foreach (var parts in ReadBoundaryRows(boundaryReader, asOfInt))
        {
            var rowZips = ReadZips(parts, out var recordType, out var city);
            if (rowZips.Count == 0) continue;

            var components = Combine(parts, lookup, stateRate);
            foreach (var zip in rowZips)
            {
                aggregator.Add(zip, recordType, components, city, namedDirectly: rowZips.Count == 1);
            }
        }

        return aggregator.Build(stateInfo, "SST");
    }

    /// <summary>
    /// True when every boundary row in effect spans the whole state (Indiana, Kentucky, Michigan, New Jersey and Rhode
    /// Island publish one 46001-47997 style row), so the state has a single statewide rate and no ZIP-level rates.
    /// </summary>
    public static bool IsStatewideOnly(TextReader boundaryReader, DateOnly asOf)
    {
        var rows = 0;
        foreach (var parts in ReadBoundaryRows(boundaryReader, ZipRateAggregator.ToDateInt(asOf)))
        {
            rows++;
            if (ReadZips(parts, out _, out _).Count > 0) return false;
        }
        return rows > 0;
    }

    /// <summary>
    /// Boundary rows in effect on <paramref name="asOfInt"/> (yyyyMMdd), split into fields. Skips header rows.
    /// </summary>
    internal static IEnumerable<string[]> ReadBoundaryRows(TextReader reader, int asOfInt)
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = SplitCsvLine(line);
            if (parts.Length <= PlaceField) continue;

            if (!int.TryParse(parts[BeginDateField].Trim(), out var begin) || !int.TryParse(parts[EndDateField].Trim(), out var end)) continue;
            if (asOfInt < begin || asOfInt > end) continue;

            yield return parts;
        }
    }

    /// <summary>
    /// The ZIP codes a boundary row covers: an address row's ZIP, or a ZIP+4 / whole-ZIP row's ZIP range.
    /// </summary>
    internal static List<string> ReadZips(string[] parts, out string recordType, out string city)
    {
        city = string.Empty;
        recordType = parts[0].Trim().ToUpperInvariant();

        if (recordType == ZipRateAggregator.AddressRecord)
        {
            var zip = parts[AddressZipField].Trim();
            if (!IsZip(zip)) return [];
            city = parts[AddressCityField].Trim();
            return [zip];
        }

        if (recordType is ZipRateAggregator.ZipPlus4Record or ZipRateAggregator.ZipRecord)
        {
            var low = parts[ZipLowField].Trim();
            var high = parts[ZipHighField].Trim();
            if (!IsZip(low)) return [];
            if (!IsZip(high) || high == low) return [low];

            var lowNumber = int.Parse(low, CultureInfo.InvariantCulture);
            var highNumber = int.Parse(high, CultureInfo.InvariantCulture);
            if (highNumber < lowNumber || highNumber - lowNumber > MaxExpandedZipRange) return [];
            return Enumerable.Range(lowNumber, highNumber - lowNumber + 1).Select(z => z.ToString("00000", CultureInfo.InvariantCulture)).ToList();
        }

        return [];
    }

    /// <summary>
    /// Adds up the rates that apply to one boundary row. The effective date is when the newest of those rates
    /// began, i.e. since when this combined rate has applied. The boundary row's own begin date is left out:
    /// states reissue rows without changing their rates (Milwaukee's were reissued in March 2024 for a
    /// January 2024 city tax).
    /// </summary>
    private static RateComponents Combine(string[] parts, RateLookup lookup, SstRate? stateRate)
    {
        var effective = stateRate?.BeginDate ?? 0;
        var countyFips = parts[CountyField].Trim();
        var county = 0m;
        var place = 0m;
        var district = 0m;

        // Track the rate rows already applied so a jurisdiction listed twice on one row is only counted once.
        var applied = new HashSet<(string StateFips, string JurisType, string JurisCode)> { (lookup.StateFips, StateType, lookup.StateFips) };

        if (countyFips.Length > 0 && lookup.Find(CountyType, countyFips, fallBackToAnyType: false) is { } countyMatch && applied.Add(countyMatch.Key))
        {
            county = countyMatch.Rate.RatePercent;
            effective = Math.Max(effective, countyMatch.Rate.BeginDate);
        }

        // Places are usually type 01, but some states key a place-column code under another type
        // (Wyoming's resort districts are type 69).
        var placeCode = parts[PlaceField].Trim();
        if (placeCode.Length > 0 && lookup.Find(PlaceType, placeCode, fallBackToAnyType: true) is { } placeMatch && applied.Add(placeMatch.Key))
        {
            place = placeMatch.Rate.RatePercent;
            effective = Math.Max(effective, placeMatch.Rate.BeginDate);
        }

        for (var i = 0; i < MaxDistricts; i++)
        {
            var codeField = FirstDistrictField + i * DistrictFieldWidth + 1;
            var typeField = codeField + 1;
            if (typeField >= parts.Length) break;

            var code = parts[codeField].Trim();
            if (code.Length == 0) continue;

            // The boundary file's type-of-taxing-authority code does not always match the rate file's jurisdiction
            // type (Washington's local codes are type 45 in the boundary file and 63 in the rate file), so fall back
            // to the district code alone. Local codes are unique within a state's rate file.
            var type = parts[typeField].Trim().PadLeft(2, '0');
            if (lookup.Find(type, code, fallBackToAnyType: true) is { } districtMatch && applied.Add(districtMatch.Key))
            {
                district += districtMatch.Rate.RatePercent;
                effective = Math.Max(effective, districtMatch.Rate.BeginDate);
            }
        }

        return new RateComponents(stateRate?.RatePercent ?? 0m, county, place, district, countyFips, effective);
    }

    private sealed class RateLookup
    {
        private readonly Dictionary<(string StateFips, string JurisType, string JurisCode), SstRate> _rates;
        private readonly Dictionary<string, (string StateFips, string JurisType, string JurisCode)> _localKeyByCode = new(StringComparer.OrdinalIgnoreCase);

        public RateLookup(string stateFips, Dictionary<(string StateFips, string JurisType, string JurisCode), SstRate> rates)
        {
            StateFips = stateFips;
            _rates = rates;
            foreach (var key in rates.Keys.OrderBy(k => k.JurisType, StringComparer.Ordinal))
            {
                if (key.StateFips == stateFips && key.JurisType != StateType && key.JurisType != CountyType)
                {
                    _localKeyByCode.TryAdd(key.JurisCode, key);
                }
            }
        }

        public string StateFips { get; }

        public ((string StateFips, string JurisType, string JurisCode) Key, SstRate Rate)? Find(string jurisType, string code, bool fallBackToAnyType)
        {
            var key = (StateFips, jurisType, code);
            if (_rates.TryGetValue(key, out var rate)) return (key, rate);
            if (fallBackToAnyType && _localKeyByCode.TryGetValue(code, out var localKey)) return (localKey, _rates[localKey]);
            return null;
        }
    }

    private static bool IsZip(string value) => value.Length == 5 && value.All(char.IsAsciiDigit);

    internal static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return [.. result];
    }
}
