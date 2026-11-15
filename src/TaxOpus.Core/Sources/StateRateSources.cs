using TaxOpus.Core.Data;
using TaxOpus.Core.Models;

namespace TaxOpus.Core.Sources;

/// <summary>
/// A state's own published rate data, turned into one rate per ZIP code, for states outside SST.
/// </summary>
public interface IStateRateSource
{
    string StateCode { get; }

    /// <summary>Short code stored in <see cref="TaxJurisdictionRate.Source"/>.</summary>
    string SourceCode { get; }

    /// <summary>What the rates come from, for seed scripts and rate descriptions.</summary>
    string Describe(string stateName);

    Task<List<TaxJurisdictionRate>> LoadAsync(SourceContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// A source whose rates come from boundary polygons, so it can give the rate at a single point as well as per ZIP.
/// </summary>
public interface IPointRateSource : IStateRateSource
{
    /// <summary>A function from longitude/latitude (WGS 84) to the rate there, or null outside every polygon.</summary>
    Task<Func<double, double, Parsers.RateComponents?>> LoadPointLookupAsync(SourceContext context, CancellationToken cancellationToken = default);
}

public static class StateRateSources
{
    public static IReadOnlyList<IStateRateSource> All { get; } =
    [
        new CuratedRatesSource("HI"),
        new CuratedRatesSource("ID"),
        new CuratedRatesSource("LA"),
        new CuratedRatesSource("MS"),
        new CuratedRatesSource("NY"),
        new CuratedRatesSource("PA"),
        new CuratedRatesSource("SC"),
        new VirginiaTaxSource(),
        new FloridaSurtaxSource(),
        new MissouriDorSource(),
        new AlaskaArsstcSource(),
        new IllinoisAddressSource(),
        new ArizonaTptSource(),
        new NewMexicoGrtSource(),
        new CaliforniaCdtfaSource(),
        new AlabamaAldorSource(),
        new ColoradoCdorSource(),
        new TexasComptrollerSource()
    ];

    public static string Describe(string sourceCode, string stateCode)
    {
        var state = StateDirectory.Get(stateCode);
        if (sourceCode == "SST") return $"{state.Name}'s Streamlined Sales Tax rate and boundary files";
        var source = All.FirstOrDefault(s => s.SourceCode == sourceCode && string.Equals(s.StateCode, stateCode, StringComparison.OrdinalIgnoreCase));
        return source?.Describe(state.Name) ?? sourceCode;
    }
}

/// <summary>
/// Where sources keep their files: downloads are cached under data/raw/&lt;state&gt;, reference data is in
/// data/reference. Offline, only cached files are used.
/// </summary>
public sealed class SourceContext(string dataRoot, DateOnly asOf, bool offline, HttpClient http)
{
    private ZipCrosswalk? _crosswalk;

    public DateOnly AsOf => asOf;
    public bool Offline => offline;
    public HttpClient Http => http;
    public string ReferenceDirectory => Path.Combine(dataRoot, "reference");
    public ZipCrosswalk Crosswalk => _crosswalk ??= ZipCrosswalk.Load(ReferenceDirectory);

    public string RawDirectory(string stateCode)
    {
        var dir = Path.Combine(dataRoot, "raw", stateCode.ToLowerInvariant());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Local path of <paramref name="fileName"/> under data/raw/&lt;state&gt;, downloading it first unless offline.
    /// </summary>
    /// <param name="refresh">Download again even if cached, for files a state replaces under the same name. If the
    /// download fails, the cached copy is used.</param>
    public async Task<string> FetchAsync(string stateCode, string fileName, string url, bool refresh, CancellationToken cancellationToken)
    {
        var path = Path.Combine(RawDirectory(stateCode), fileName);
        var cached = File.Exists(path) && new FileInfo(path).Length > 0;
        if (Offline || (cached && !refresh))
        {
            return cached ? path : throw new FileNotFoundException($"{fileName} is not cached in {RawDirectory(stateCode)}; run 'fetch' to download it.");
        }

        try
        {
            await DownloadAsync(url, path, cancellationToken);
        }
        catch (Exception ex) when (cached && IsDownloadFailure(ex, cancellationToken))
        {
            Console.WriteLine($"  [WARN] {stateCode.ToUpperInvariant()}: could not refresh {fileName} ({ex.Message}); using the copy from {File.GetLastWriteTimeUtc(path):yyyy-MM-dd}.");
        }
        return path;
    }

    /// <summary>A site that is down, slow or refusing, as opposed to the run being cancelled.</summary>
    public static bool IsDownloadFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    public async Task DownloadAsync(string url, string path, CancellationToken cancellationToken)
    {
        using var response = await HttpRetry.GetWithRetryAsync(Http, url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await SaveAsync(response, path, cancellationToken);
    }

    public static async Task SaveAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        var partial = path + ".part";
        await using (var file = File.Create(partial))
        {
            await response.Content.CopyToAsync(file, cancellationToken);
        }
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>Newest cached file matching a pattern, for offline runs.</summary>
    public IEnumerable<string> CachedFiles(string stateCode, string searchPattern) =>
        Directory.EnumerateFiles(RawDirectory(stateCode), searchPattern).Select(Path.GetFileName)!;
}
