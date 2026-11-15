using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using TaxOpus.Core.Data;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Sources;

/// <summary>
/// Florida: 6% plus a county discretionary sales surtax (0–2%). Florida DOR's surtax table comes from its
/// Address/Jurisdiction Database site as a workbook; surtaxes start and end only on January 1 (s. 212.054(5)).
/// The surtax applies only to the first $5,000 of each item, which a single combined rate can't express.
/// </summary>
public sealed partial class FloridaSurtaxSource : IStateRateSource
{
    public const string Url = "https://pointmatch.floridarevenue.com/General/DiscretionarySalesSurtaxRates.aspx";
    private const string FileName = "FDOR_DiscretionarySalesSurtaxRates.xlsx";

    public string StateCode => "FL";
    public string SourceCode => "FL-DOR";

    public string Describe(string stateName) =>
        "Florida DOR county surtax rates, assigned to ZIP codes by 2020 population (the surtax applies to the first $5,000 of each item)";

    public async Task<List<TaxJurisdictionRate>> LoadAsync(SourceContext context, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(context.RawDirectory(StateCode), FileName);
        var cached = File.Exists(path) && new FileInfo(path).Length > 0;
        if (!context.Offline)
        {
            try
            {
                await DownloadAsync(context, path, cancellationToken);
            }
            catch (Exception ex) when (cached && SourceContext.IsDownloadFailure(ex, cancellationToken))
            {
                Console.WriteLine($"  [WARN] FL: could not refresh {FileName} ({ex.Message}); using the copy from {File.GetLastWriteTimeUtc(path):yyyy-MM-dd}.");
            }
        }
        else if (!cached)
        {
            throw new FileNotFoundException($"{FileName} is not cached; run 'fetch' to download it.");
        }

        var state = StateDirectory.Get(StateCode);
        var countyFips = context.Crosswalk.Counties(state.Fips).Values.SelectMany(l => l)
            .GroupBy(l => l.Code)
            .ToDictionary(g => NormalizeCounty(g.First().Name.Replace(" FL", string.Empty, StringComparison.Ordinal)), g => g.Key);

        var effective = ZipRateAggregator.ToDateInt(new DateOnly(context.AsOf.Year, 1, 1));
        var countyRates = new Dictionary<string, AreaRate>();
        foreach (var (county, surtax) in ReadSurtaxes(path))
        {
            if (!countyFips.TryGetValue(NormalizeCounty(county), out var fips))
            {
                throw new InvalidDataException($"Florida county \"{county}\" isn't in the ZIP crosswalk.");
            }
            countyRates[fips] = new AreaRate(state.BaseStateRatePercent + surtax, effective);
        }

        return CrosswalkZipRates.Build(state, SourceCode, context.Crosswalk, state.BaseStateRatePercent, countyRates);
    }

    /// <summary>
    /// County name and surtax percent, from rows like ("MIAMI-DADE", "1.00%"). The table can list a county twice
    /// (Flagler, both at 1.00%); a county with two different rates is an error.
    /// </summary>
    public static IEnumerable<(string County, decimal SurtaxPercent)> ReadSurtaxes(string path) =>
        XlsxReader.ReadRows(path)
            .Where(r => r.Length >= 2 && r[1].TrimEnd().EndsWith('%'))
            .Select(r => (County: r[0].Trim(), Rate: decimal.Parse(r[1].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture)))
            .GroupBy(r => r.County)
            .Select(g => g.Select(r => r.Rate).Distinct().Count() == 1
                ? (g.Key, g.First().Rate)
                : throw new InvalidDataException($"Florida's surtax table gives {g.Key} different rates."));

    // The table downloads through an ASP.NET postback: load the page, then post its form state with "Download All".
    private static async Task DownloadAsync(SourceContext context, string path, CancellationToken cancellationToken)
    {
        var page = await context.Http.GetStringAsync(Url, cancellationToken);
        var fields = HiddenField().Matches(page).ToDictionary(m => m.Groups["name"].Value, m => WebUtility.HtmlDecode(m.Groups["value"].Value));
        fields["ctl00$BodyContent$gridView_DownloadLink"] = "Download All";

        using var response = await context.Http.PostAsync(Url, new FormUrlEncodedContent(fields), cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType?.Contains("spreadsheetml", StringComparison.Ordinal) != true)
        {
            throw new InvalidDataException("Florida's surtax download did not return a workbook.");
        }
        await SourceContext.SaveAsync(response, path, cancellationToken);
    }

    private static string NormalizeCounty(string name) => new(name.ToUpperInvariant().Where(char.IsAsciiLetter).ToArray());

    [GeneratedRegex(@"<input[^>]*name=""(?<name>__[A-Z]+)""[^>]*value=""(?<value>[^""]*)""")]
    private static partial Regex HiddenField();
}
