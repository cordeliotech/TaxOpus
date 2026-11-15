using System.Globalization;
using TaxOpus.Core.Models;

namespace TaxOpus.Core.Parsers;

/// <summary>
/// The rates that make up one combined rate. <see cref="Total"/>, when set, is the source's own combined rate and
/// takes precedence over the sum: some sources publish levies they don't break out (Illinois publishes only totals).
/// </summary>
/// <param name="EffectiveDate">yyyyMMdd date the newest of these rates took effect; 0 when unknown.</param>
public readonly record struct RateComponents(
    decimal State,
    decimal County,
    decimal Place,
    decimal District,
    string CountyFips,
    int EffectiveDate,
    decimal? Total = null)
{
    public decimal Combined => Math.Round(Total ?? State + County + Place + District, 4);
}

/// <summary>
/// Collects the rates found inside each ZIP code, from boundary rows, address points or population shares, and
/// reduces them to one record per ZIP: the rate carrying the most weight, plus the lowest and highest rates found.
/// </summary>
public sealed class ZipRateAggregator
{
    /// <summary>Street-address rows (boundary files) or address points.</summary>
    public const string AddressRecord = "A";
    public const string ZipPlus4Record = "4";
    /// <summary>Whole-ZIP rows, or population shares of a ZIP.</summary>
    public const string ZipRecord = "Z";

    private readonly Dictionary<string, ZipTally> _zips = new(StringComparer.Ordinal);

    /// <param name="namedDirectly">False when the ZIP came from expanding a ZIP range rather than being named.</param>
    public void Add(string zip, string recordType, RateComponents components, string city, double weight = 1, bool namedDirectly = true)
    {
        if (weight <= 0) return;
        if (!_zips.TryGetValue(zip, out var tally))
        {
            tally = new ZipTally();
            _zips[zip] = tally;
        }
        tally.Add(recordType, components, city, weight, namedDirectly);
    }

    /// <summary>
    /// One record per ZIP. Which rows decide a ZIP's rate depends on what the source publishes:
    /// <list type="bullet">
    /// <item>street-address rows, when there are some for most ZIPs;</item>
    /// <item>otherwise ZIP+4 rows (Minnesota and Utah publish no street rows; Wyoming publishes them only for
    /// special districts, which would otherwise outvote the rest of the ZIP);</item>
    /// <item>the whole-ZIP row only when there is nothing finer. SST states set it to the lowest rate in the ZIP
    /// (5.9% for Milwaukee's 53202, where the city's 7.9% covers almost every address).</item>
    /// </list>
    /// </summary>
    /// <param name="includeCity">Whether to fill in city names. By default only when street rows are comprehensive:
    /// where they cover just a state's special districts, their city name is the district's rather than the ZIP's.</param>
    /// <param name="minShareForRange">Leave rates covering less than this share of a ZIP's weight out of its
    /// lowest and highest rates (population shares put a handful of people in many neighbouring places).</param>
    public List<TaxJurisdictionRate> Build(UsStateInfo stateInfo, string source, bool? includeCity = null, double minShareForRange = 0)
    {
        // Count only ZIPs a row names directly: expanding ZIP ranges adds ZIPs no street row could ever list.
        var namedZips = _zips.Values.Count(z => z.NamedDirectly);
        var zipsWithAddressRows = _zips.Values.Count(z => z.Has(AddressRecord));
        var addressRowsAreComprehensive = namedZips > 0 && zipsWithAddressRows * 2 >= namedZips;
        string[] preference = addressRowsAreComprehensive
            ? [AddressRecord, ZipPlus4Record, ZipRecord]
            : [ZipPlus4Record, ZipRecord, AddressRecord];

        return _zips
            .OrderBy(z => z.Key, StringComparer.Ordinal)
            .Select(z => z.Value.ToRecord(z.Key, stateInfo, source, preference, includeCity ?? addressRowsAreComprehensive, minShareForRange))
            .ToList();
    }

    private sealed class ZipTally
    {
        // Weight per combined rate, kept separately for each record type (A, 4, Z).
        private readonly Dictionary<string, Dictionary<decimal, RateBucket>> _byRecordType = [];
        private readonly Dictionary<string, double> _cities = new(StringComparer.OrdinalIgnoreCase);

        public bool Has(string recordType) => _byRecordType.ContainsKey(recordType);

        /// <summary>True when at least one row names this ZIP itself rather than covering it with a range.</summary>
        public bool NamedDirectly { get; private set; }

        public void Add(string recordType, RateComponents components, string city, double weight, bool namedDirectly)
        {
            NamedDirectly |= namedDirectly;
            if (!_byRecordType.TryGetValue(recordType, out var byCombined))
            {
                byCombined = [];
                _byRecordType[recordType] = byCombined;
            }

            if (!byCombined.TryGetValue(components.Combined, out var bucket))
            {
                bucket = new RateBucket();
                byCombined[components.Combined] = bucket;
            }
            bucket.Add(components, weight);

            if (city.Length > 0)
            {
                _cities[city] = _cities.GetValueOrDefault(city) + weight;
            }
        }

        public TaxJurisdictionRate ToRecord(string zip, UsStateInfo stateInfo, string source, IReadOnlyList<string> recordTypePreference, bool includeCity, double minShareForRange)
        {
            var rows = recordTypePreference.Where(_byRecordType.ContainsKey).Select(t => _byRecordType[t]).First();

            // Most weight wins; on a tie take the higher rate so the ZIP is not under-collected.
            var main = rows
                .OrderByDescending(kv => kv.Value.Weight)
                .ThenByDescending(kv => kv.Key)
                .First().Value.Components;

            var totalWeight = _byRecordType.Values.Sum(r => r.Values.Sum(v => v.Weight));
            var rangeRates = _byRecordType.Values
                .SelectMany(r => r)
                .Where(kv => kv.Value.Weight >= minShareForRange * totalWeight || kv.Key == main.Combined)
                .Select(kv => kv.Key)
                .ToList();

            var city = !includeCity || _cities.Count == 0
                ? string.Empty
                : _cities.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).First().Key;

            return new TaxJurisdictionRate
            {
                CountryCode = "US",
                StateCode = stateInfo.Code.ToUpperInvariant(),
                PostalCode = zip,
                City = ToTitleCase(city),
                County = main.CountyFips,
                StateRatePercent = main.State,
                CountyRatePercent = main.County,
                CityRatePercent = main.Place,
                SpecialDistrictRatePercent = main.District,
                CombinedRatePercent = main.Combined,
                MinCombinedRatePercent = rangeRates.Min(),
                MaxCombinedRatePercent = rangeRates.Max(),
                AuthorityName = stateInfo.AgencyName,
                Source = source,
                EffectiveDate = FromDateInt(main.EffectiveDate)
            };
        }
    }

    /// <summary>
    /// Everything found at one combined rate. Different splits can add up to the same rate (a city's limits and its
    /// police jurisdiction, or two counties); the split carrying the most weight describes the rate, dated by the
    /// newest component of any of them.
    /// </summary>
    private sealed class RateBucket
    {
        private readonly Dictionary<RateComponents, double> _splits = [];
        private int _effectiveDate;

        public double Weight { get; private set; }

        public RateComponents Components =>
            _splits.OrderByDescending(s => s.Value).First().Key with { EffectiveDate = _effectiveDate };

        public void Add(RateComponents components, double weight)
        {
            var split = components with { EffectiveDate = 0 };
            _splits[split] = _splits.GetValueOrDefault(split) + weight;
            _effectiveDate = Math.Max(_effectiveDate, components.EffectiveDate);
            Weight += weight;
        }
    }

    public static int ToDateInt(DateOnly date) => date.Year * 10000 + date.Month * 100 + date.Day;

    public static DateOnly? FromDateInt(int value) =>
        DateOnly.TryParseExact(value.ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static string ToTitleCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
    }
}
