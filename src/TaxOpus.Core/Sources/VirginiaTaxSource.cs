using System.Globalization;
using System.Text.RegularExpressions;
using TaxOpus.Core.Data;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Sources;

/// <summary>
/// Virginia: 5.3% everywhere, more in the Northern Virginia, Hampton Roads, Central Virginia and Historic Triangle
/// regions and a few localities with an extra 1%. Virginia Tax publishes rates by locality (FIPS) in a workbook
/// with one tab per period; localities are counties and independent cities, so ZIPs map by county.
/// </summary>
public sealed partial class VirginiaTaxSource : IStateRateSource
{
    public const string Url = "https://www.tax.virginia.gov/sites/default/files/inline-files/sales-and-use-tax-rates-by-locality-by-date.xlsx";
    private const string FileName = "sales-and-use-tax-rates-by-locality-by-date.xlsx";

    public string StateCode => "VA";
    public string SourceCode => "VA-TAX";

    public string Describe(string stateName) =>
        "Virginia Tax rates by locality, assigned to ZIP codes by 2020 population";

    public async Task<List<TaxJurisdictionRate>> LoadAsync(SourceContext context, CancellationToken cancellationToken = default)
    {
        var path = await context.FetchAsync(StateCode, FileName, Url, refresh: true, cancellationToken);
        var state = StateDirectory.Get(StateCode);
        return CrosswalkZipRates.Build(state, SourceCode, context.Crosswalk, state.BaseStateRatePercent, ReadRates(path, context.AsOf));
    }

    /// <summary>Rates by locality FIPS from the tab whose period covers <paramref name="asOf"/>.</summary>
    public static Dictionary<string, AreaRate> ReadRates(string path, DateOnly asOf)
    {
        var sheetCount = XlsxReader.SheetNames(path).Count;
        for (var i = sheetCount - 1; i >= 0; i--)
        {
            var rows = XlsxReader.ReadRows(path, i);
            var period = rows.SelectMany(r => r).Select(c => PeriodPattern().Match(c)).FirstOrDefault(m => m.Success);
            if (period == null) continue;

            var from = ParseDate(period.Groups["from"].Value);
            var to = period.Groups["to"].Value.Equals("Present", StringComparison.OrdinalIgnoreCase) ? DateOnly.MaxValue : ParseDate(period.Groups["to"].Value);
            if (asOf < from || asOf > to) continue;

            // Columns: (blank), Name, Code (FIPS), General Sales (fraction), ...
            var effective = ZipRateAggregator.ToDateInt(from);
            var rates = new Dictionary<string, AreaRate>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                if (r.Length <= 3 || r[2].Length != 5 || !r[2].StartsWith("51", StringComparison.Ordinal)) continue;
                rates[r[2]] = new AreaRate(Math.Round(decimal.Parse(r[3], NumberStyles.Float, CultureInfo.InvariantCulture) * 100m, 4), effective);
            }
            return rates;
        }

        throw new InvalidDataException($"No tab in {Path.GetFileName(path)} covers {asOf:yyyy-MM-dd}.");
    }

    // Tab titles vary: "January 1, 2026 - Present", "Jul 1, 2005 - Dec 31, 2010", "From October 1, 2020 - Jun 30 2021".
    private static DateOnly ParseDate(string text) =>
        DateOnly.ParseExact(text.Trim().Replace(",", string.Empty, StringComparison.Ordinal), ["MMMM d yyyy", "MMM d yyyy"], CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?<from>[A-Za-z]+ \d{1,2},? \d{4})\s*(?:-|to)\s*(?<to>Present|[A-Za-z]+ \d{1,2},? \d{4})")]
    private static partial Regex PeriodPattern();
}
