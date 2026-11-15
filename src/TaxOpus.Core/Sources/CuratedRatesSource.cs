using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using TaxOpus.Core.Data;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Sources;

/// <summary>
/// States that publish their local rates only as web pages or PDFs (Hawaii, Mississippi, New York, Pennsylvania,
/// South Carolina), or whose cities set and collect their own (Idaho's resort cities): rates by county and city
/// from data/reference/curated_local_rates.csv, assigned to ZIP codes by population.
/// </summary>
public sealed partial class CuratedRatesSource(string stateCode) : IStateRateSource
{
    public const string FileName = "curated_local_rates.csv";

    // Pages giving the date of the publication a state's rows were copied from (New York's Publication 718, the
    // Idaho Tax Commission's list of cities with a local tax). Online runs warn when the page shows a later date,
    // since the copied rows don't update themselves.
    private static readonly Dictionary<string, (string Url, Regex Pattern)> PublicationChecks = new()
    {
        ["NY"] = ("https://www.tax.ny.gov/pubs_and_bulls/publications/sales/local_rates_current.htm", NewYorkPublicationDate()),
        ["ID"] = ("https://tax.idaho.gov/taxes/sales-use/sales-tax/local-sales-tax/city-sales-tax/", IdahoCityListDate())
    };

    public string StateCode => stateCode;
    public string SourceCode => "CURATED";

    public string Describe(string stateName) =>
        $"{stateName}'s published county and city rates (data/reference/{FileName}), assigned to ZIP codes by 2020 population";

    public async Task<List<TaxJurisdictionRate>> LoadAsync(SourceContext context, CancellationToken cancellationToken = default)
    {
        var state = StateDirectory.Get(stateCode);
        var countyRates = new Dictionary<string, AreaRate>();
        var placeRates = new Dictionary<string, AreaRate>();
        var asOfInt = ZipRateAggregator.ToDateInt(context.AsOf);
        var rows = ReadRows(Path.Combine(context.ReferenceDirectory, FileName))
            .Where(r => r.State == state.Code && (r.EffectiveFrom == 0 || r.EffectiveFrom <= asOfInt))
            .OrderBy(r => r.EffectiveFrom)
            .ToList();

        foreach (var row in rows)
        {
            (row.Level == "Place" ? placeRates : countyRates)[row.Fips] = new AreaRate(row.CombinedPercent, row.EffectiveFrom);
        }

        if (!context.Offline && PublicationChecks.TryGetValue(state.Code, out var check))
        {
            // Only a check: the rates are in the curated table, so an unreachable page doesn't stop them
            try
            {
                await WarnIfNewerPublicationAsync(context, check.Url, check.Pattern, rows, cancellationToken);
            }
            catch (Exception ex) when (SourceContext.IsDownloadFailure(ex, cancellationToken))
            {
                Console.WriteLine($"  [WARN] {stateCode}: could not check {check.Url} for newer rates ({ex.Message}).");
            }
        }

        return countyRates.Count + placeRates.Count == 0
            ? []
            : CrosswalkZipRates.Build(state, SourceCode, context.Crosswalk, state.BaseStateRatePercent, countyRates, placeRates);
    }

    private async Task WarnIfNewerPublicationAsync(SourceContext context, string url, Regex pattern, IReadOnlyList<CuratedRow> rows, CancellationToken cancellationToken)
    {
        var page = WebUtility.HtmlDecode(Regex.Replace(await HttpRetry.GetStringWithRetryAsync(context.Http, url, cancellationToken), "<[^>]+>|\\s+", " "));
        var published = pattern.Match(page);
        var copied = rows.Select(r => CopiedEffective().Match(r.Source)).Where(m => m.Success).Select(m => DateOnly.Parse(m.Groups["date"].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty().Max();
        if (published.Success && DateOnly.TryParse(published.Groups["date"].Value, CultureInfo.InvariantCulture, out var publishedDate) && publishedDate > copied)
        {
            Console.WriteLine($"  [WARN] {stateCode}: {url} is dated {publishedDate:yyyy-MM-dd}, after the {copied:yyyy-MM-dd} version the {stateCode} rows in data/reference/{FileName} were copied from. Update them.");
        }
    }

    public record CuratedRow(string State, string Level, string Fips, string Name, decimal CombinedPercent, int EffectiveFrom, string Source);

    public static IEnumerable<CuratedRow> ReadRows(string path)
    {
        // State,Level,Fips,Name,CombinedRatePercent,EffectiveFrom,Source; lines starting with # are comments
        foreach (var line in File.ReadLines(path).Where(l => l.Length > 0 && !l.StartsWith('#')).Skip(1))
        {
            var f = SstParser.SplitCsvLine(line);
            if (f.Length < 6) continue;
            var effective = DateOnly.TryParseExact(f[5], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? ZipRateAggregator.ToDateInt(date)
                : 0;
            yield return new CuratedRow(f[0], f[1], f[2], f[3], decimal.Parse(f[4], CultureInfo.InvariantCulture), effective, f.Length > 6 ? f[6] : string.Empty);
        }
    }

    [GeneratedRegex(@"Publication 718\)\s*-\s*Effective (?<date>[A-Za-z]+ \d{1,2}, \d{4})")]
    private static partial Regex NewYorkPublicationDate();

    [GeneratedRegex(@"last updated:\s*(?<date>[A-Za-z]+ \d{1,2}, \d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex IdahoCityListDate();

    /// <summary>The "effective yyyy-MM-dd" or "updated yyyy-MM-dd" note in a row's Source column.</summary>
    [GeneratedRegex(@"(?:effective|updated) (?<date>\d{4}-\d{2}-\d{2})")]
    private static partial Regex CopiedEffective();
}
