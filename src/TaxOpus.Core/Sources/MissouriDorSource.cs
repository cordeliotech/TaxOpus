using System.Globalization;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Sources;

/// <summary>
/// Missouri DOR's quarterly rate and boundary databases (RSMo 144.637). The boundary file has the SST layout up to
/// column 28 and puts DOR's jurisdiction code in column 69; the jurisdiction total-rate workbook gives each code's
/// combined rate. Uses the sales tax rate, which in-state sellers charge (out-of-state sellers' use tax rates differ).
/// </summary>
public sealed class MissouriDorSource : IStateRateSource
{
    public const string BaseUrl = "https://dor.mo.gov/taxation/business/tax-types/sales-use/documents/";
    private const int JurisdictionCodeField = 69;

    public string StateCode => "MO";
    public string SourceCode => "MO-DOR";

    public string Describe(string stateName) =>
        "the Missouri Department of Revenue's rate and boundary databases (sales tax rates)";

    public async Task<List<TaxJurisdictionRate>> LoadAsync(SourceContext context, CancellationToken cancellationToken = default)
    {
        // Files are named by quarter (q4-2026); the current quarter's may not be posted yet, so fall back one quarter.
        var quarter = new DateOnly(context.AsOf.Year, (context.AsOf.Month - 1) / 3 * 3 + 1, 1);
        foreach (var candidate in new[] { quarter, quarter.AddMonths(-3) })
        {
            var suffix = $"q{(candidate.Month - 1) / 3 + 1}-{candidate.Year}";
            try
            {
                var boundary = await context.FetchAsync(StateCode, $"dor-boundary-database-{suffix}.csv", BaseUrl + $"dor-boundary-database-{suffix}.csv", false, cancellationToken);
                var totals = await context.FetchAsync(StateCode, $"dor-jurisdiction-total-rate-{suffix}.xlsx", BaseUrl + $"dor-jurisdiction-total-rate-{suffix}.xlsx", false, cancellationToken);
                var rates = await context.FetchAsync(StateCode, $"dor-rate-database-{suffix}.xlsx", BaseUrl + $"dor-rate-database-{suffix}.xlsx", false, cancellationToken);

                using var reader = new StreamReader(boundary);
                return Parse(reader, ReadTotals(totals), ReadRates(rates, context.AsOf), context.AsOf);
            }
            catch (Exception ex) when (ex is FileNotFoundException or HttpRequestException)
            {
                if (candidate != quarter) throw;
            }
        }
        return [];
    }

    /// <param name="totals">Combined sales rate (percent) by DOR jurisdiction code, e.g. "40736-079-000".</param>
    /// <param name="rates">Component rates in effect, keyed like the SST rate file (type 45 state, 00 county, 01 city, 63 district).</param>
    public static List<TaxJurisdictionRate> Parse(
        TextReader boundaryReader,
        IReadOnlyDictionary<string, JurisdictionTotal> totals,
        IReadOnlyDictionary<(string Type, string Code), SstParser.SstRate> rates,
        DateOnly asOf)
    {
        var state = StateDirectory.Get("MO");
        var stateRate = rates.GetValueOrDefault(("45", state.Fips));
        var aggregator = new ZipRateAggregator();

        foreach (var parts in SstParser.ReadBoundaryRows(boundaryReader, ZipRateAggregator.ToDateInt(asOf)))
        {
            if (parts.Length <= JurisdictionCodeField || !totals.TryGetValue(parts[JurisdictionCodeField].Trim(), out var total)) continue;
            var rowZips = SstParser.ReadZips(parts, out var recordType, out var city);
            if (rowZips.Count == 0) continue;

            var county = rates.GetValueOrDefault(("00", total.CountyFips));
            var place = total.PlaceFips != "00000" ? rates.GetValueOrDefault(("01", total.PlaceFips)) : null;
            var districts = total.DistrictCodes.Select(d => rates.GetValueOrDefault(("63", d))).Where(r => r != null).ToList();
            var effective = new[] { stateRate, county, place }.Concat(districts).Max(r => r?.BeginDate ?? 0);

            // The total is authoritative; whatever the listed components don't explain is reported as districts.
            var statePercent = stateRate?.RatePercent ?? state.BaseStateRatePercent;
            var countyPercent = county?.RatePercent ?? 0m;
            var placePercent = place?.RatePercent ?? 0m;
            var components = new RateComponents(statePercent, countyPercent, placePercent,
                total.SalesPercent - statePercent - countyPercent - placePercent, total.CountyFips, effective, total.SalesPercent);

            foreach (var zip in rowZips)
            {
                aggregator.Add(zip, recordType, components, city, namedDirectly: rowZips.Count == 1);
            }
        }

        return aggregator.Build(state, "MO-DOR");
    }

    public record JurisdictionTotal(string CountyFips, string PlaceFips, IReadOnlyList<string> DistrictCodes, decimal SalesPercent);

    /// <summary>
    /// Columns: State Fips, County Fips, City Fips, District Code 1–5, Jurisdiction Code, Sales, Use, Food, Food, Domestic, AMJ Rate.
    /// </summary>
    public static Dictionary<string, JurisdictionTotal> ReadTotals(string path)
    {
        var totals = new Dictionary<string, JurisdictionTotal>(StringComparer.Ordinal);
        foreach (var r in XlsxReader.ReadRows(path).Skip(1))
        {
            if (r.Length <= 9 || !r[8].Contains('-')) continue;
            totals[r[8].Trim()] = new JurisdictionTotal(
                r[1].Trim(), r[2].Trim(), r[3..8].Where(d => d.Length > 0).ToList(), Percent(r[9]));
        }
        return totals;
    }

    /// <summary>
    /// Columns: state, type, fips, interstate_sales, intrastate_sales, interstate_food_drug, intrastate_food_drug,
    /// startdate, enddate. In-state sellers' sales tax is the intrastate rate.
    /// </summary>
    public static Dictionary<(string Type, string Code), SstParser.SstRate> ReadRates(string path, DateOnly asOf)
    {
        var asOfInt = ZipRateAggregator.ToDateInt(asOf);
        var rates = new Dictionary<(string Type, string Code), SstParser.SstRate>();
        foreach (var r in XlsxReader.ReadRows(path).Skip(1).Where(r => r.Length >= 9))
        {
            if (!int.TryParse(r[7], out var begin) || !int.TryParse(r[8], out var end) || asOfInt < begin || asOfInt > end) continue;
            rates[(r[1].Trim().PadLeft(2, '0'), r[2].Trim())] = new SstParser.SstRate(Percent(r[4]), begin, end);
        }
        return rates;
    }

    private static decimal Percent(string fraction) =>
        Math.Round(decimal.Parse(fraction, NumberStyles.Float, CultureInfo.InvariantCulture) * 100m, 4);
}
