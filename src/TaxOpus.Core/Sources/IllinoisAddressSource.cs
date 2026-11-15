using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Sources;

/// <summary>
/// Illinois DOR's address-specific rate file: every mailing address with its combined general merchandise rate
/// (about 94 million rows, 1.8 GB zipped). Each ZIP gets the rate of most of its addresses. Illinois publishes only
/// combined rates, so the local part isn't split by jurisdiction. In-state retailers' local taxes are based on
/// where the sale is made (retailers' occupation tax), not the buyer's address.
/// </summary>
public sealed partial class IllinoisAddressSource : IStateRateSource
{
    public const string PageUrl = "https://tax.illinois.gov/research/taxrates/machine-readable-file-address-specific.html";

    public string StateCode => "IL";
    public string SourceCode => "IL-DOR";

    public string Describe(string stateName) =>
        "the Illinois Department of Revenue's address-specific rate file (combined rates)";

    public async Task<List<TaxJurisdictionRate>> LoadAsync(SourceContext context, CancellationToken cancellationToken = default)
    {
        string path;
        DateOnly effective;
        if (!context.Offline)
        {
            try
            {
                var page = await HttpRetry.GetStringWithRetryAsync(context.Http, PageUrl, cancellationToken);
                var links = FileLink().Matches(page).Select(m => (Url: m.Value, Name: m.Value[(m.Value.LastIndexOf('/') + 1)..]));
                var selected = SelectForDate(links, context.AsOf)
                    ?? throw new FileNotFoundException("No Illinois address rate file effective by " + context.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                path = await context.FetchAsync(StateCode, selected.Name, selected.Url, false, cancellationToken);
                effective = selected.Effective;
            }
            catch (Exception ex) when (SourceContext.IsDownloadFailure(ex, cancellationToken))
            {
                var cached = SelectForDate(context.CachedFiles(StateCode, "AllAddressRates*.zip").Select(name => (Url: name, Name: name)), context.AsOf);
                if (cached == null) throw;
                path = Path.Combine(context.RawDirectory(StateCode), cached.Value.Name);
                effective = cached.Value.Effective;
                Console.WriteLine($"  [WARN] IL: could not reach {PageUrl} ({ex.Message}); using cached {cached.Value.Name}.");
            }
        }
        else
        {
            var cached = SelectForDate(context.CachedFiles(StateCode, "AllAddressRates*.zip").Select(name => (Url: name, Name: name)), context.AsOf)
                ?? throw new FileNotFoundException("No Illinois address rate file effective by " + context.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            path = Path.Combine(context.RawDirectory(StateCode), cached.Name);
            effective = cached.Effective;
        }

        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.Entries.Single(e => e.Length > 0).Open());
        return Parse(reader, effective);
    }

    private static (string Url, string Name, DateOnly Effective)? SelectForDate(IEnumerable<(string Url, string Name)> files, DateOnly asOf)
    {
        var match = files
            .Select(f => (f.Url, f.Name, Effective: FileDate(f.Name)))
            .Where(f => f.Effective is { } d && d <= asOf)
            .OrderByDescending(f => f.Effective).ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        return match.Name == null ? null : (match.Url, match.Name, match.Effective!.Value);
    }

    /// <summary>
    /// Columns: fstrAddress, fstrCity, fstrState, fstrZip, fstrZipPlus4, fstrLocCode, fstrDistrict, fcurHigh_rate,
    /// fcurDrug_rate, fcurGrocery_rate. Rates are percents. Fields are taken from the right so a comma in the
    /// street address can't shift them.
    /// </summary>
    public static List<TaxJurisdictionRate> Parse(TextReader reader, DateOnly fileEffective)
    {
        const int FieldsAfterAddress = 9;
        var state = StateDirectory.Get("IL");
        var counts = new Dictionary<(string Zip, string Rate), int>();
        var cities = new Dictionary<(string Zip, string City), int>();
        var ends = new int[FieldsAfterAddress];

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // ends[k] = index of the comma before field (k + 1) counting from the address
            var pos = line.Length;
            var found = 0;
            for (; found < FieldsAfterAddress; found++)
            {
                if (pos <= 0) break;
                pos = line.LastIndexOf(',', pos - 1);
                if (pos < 0) break;
                ends[FieldsAfterAddress - 1 - found] = pos;
            }
            if (found < FieldsAfterAddress) continue;

            var zip = line.Substring(ends[2] + 1, ends[3] - ends[2] - 1);
            if (zip.Length != 5 || !zip.All(char.IsAsciiDigit)) continue; // header row and malformed rows
            var rate = line.Substring(ends[6] + 1, ends[7] - ends[6] - 1);
            var city = line.Substring(ends[0] + 1, ends[1] - ends[0] - 1);

            counts[(zip, rate)] = counts.GetValueOrDefault((zip, rate)) + 1;
            cities[(zip, city)] = cities.GetValueOrDefault((zip, city)) + 1;
        }

        var effective = ZipRateAggregator.ToDateInt(fileEffective);
        var mainCity = cities.GroupBy(c => c.Key.Zip).ToDictionary(g => g.Key, g => g.MaxBy(c => c.Value).Key.City);
        var aggregator = new ZipRateAggregator();
        foreach (var ((zip, rateText), count) in counts)
        {
            if (!decimal.TryParse(rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)) continue;
            var components = new RateComponents(state.BaseStateRatePercent, 0m, 0m, 0m, string.Empty, effective, rate);
            aggregator.Add(zip, ZipRateAggregator.AddressRecord, components, mainCity.GetValueOrDefault(zip, string.Empty), count);
        }

        return aggregator.Build(state, "IL-DOR", includeCity: true);
    }

    /// <summary>AllAddressRatesJul2026.zip and AllAddressRatesJan2026_2.zip take effect on the 1st of that month.</summary>
    private static DateOnly? FileDate(string name)
    {
        var m = FileDatePattern().Match(name);
        return m.Success && DateTime.TryParseExact(m.Groups["mon"].Value + m.Groups["year"].Value, "MMMyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? DateOnly.FromDateTime(d)
            : null;
    }

    [GeneratedRegex(@"https://www\.revenue\.state\.il\.us/taxrates/AllAddressRates[A-Za-z0-9_]+\.zip", RegexOptions.IgnoreCase)]
    private static partial Regex FileLink();

    [GeneratedRegex(@"AllAddressRates(?<mon>[A-Za-z]{3})(?<year>\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex FileDatePattern();
}
