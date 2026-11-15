using System.IO.Compression;
using System.Text.RegularExpressions;
using TaxOpus.Core.Models;
using TaxOpus.Core.Sources;

namespace TaxOpus.Core.Downloader;

public sealed class SstDownloader : IDisposable
{
    private const string BaseRatesUrl = "https://www.streamlinedsalestax.org/ratesandboundry/Rates/";
    private const string BaseBoundaryUrl = "https://www.streamlinedsalestax.org/ratesandboundry/Boundary/";

    private readonly bool _ownsHttp;
    private readonly HttpClient _http;

    public SstDownloader(HttpClient? httpClient = null)
    {
        _ownsHttp = httpClient == null;
        if (httpClient != null)
        {
            _http = httpClient;
        }
        else
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; TaxOpus/1.0)");
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    public record SstFileEntry(string StateCode, string FileName, string DownloadUrl, bool IsZip);

    /// <summary>
    /// Scrapes SST directory listing for the latest available rate or boundary files.
    /// </summary>
    public async Task<List<SstFileEntry>> DiscoverFilesAsync(bool isRatesDirectory, CancellationToken cancellationToken = default, DateOnly? asOf = null)
    {
        var targetUrl = isRatesDirectory ? BaseRatesUrl : BaseBoundaryUrl;
        var prefix = isRatesDirectory ? "R" : "B";

        string html;
        try
        {
            html = await HttpRetry.GetStringWithRetryAsync(_http, targetUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[WARN] Could not retrieve directory listing from {targetUrl}: {ex.Message}");
            return [];
        }

        // Matches href="/ratesandboundry/Rates/ARR2026Q4AUG28.csv"
        var matches = Regex.Matches(html, @"href=""[^""]*/(?<filename>([A-Za-z]{2})" + prefix + @"[^""]+\.(csv|zip))""", RegexOptions.IgnoreCase);
        var candidates = new List<(SstFileName Parsed, string FileName, string DownloadUrl, bool IsZip)>();

        foreach (Match match in matches)
        {
            var fileName = match.Groups["filename"].Value;
            if (!SstFileName.TryParse(fileName, out var parsed) || parsed.IsRates != isRatesDirectory) continue;
            if (!StateDirectory.States.ContainsKey(parsed.StateCode)) continue;

            var fullUrl = new Uri(new Uri(targetUrl), fileName).ToString();
            var isZip = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            candidates.Add((parsed, fileName, fullUrl, isZip));
        }

        return SelectBestPerState(candidates, asOf);
    }

    /// <summary>
    /// Finds the newest rate or boundary file per state among files already downloaded to <paramref name="directory"/>.
    /// </summary>
    public static List<SstFileEntry> FindCachedFiles(string directory, bool isRatesDirectory, DateOnly? asOf = null)
    {
        if (!Directory.Exists(directory)) return [];

        var candidates = new List<(SstFileName Parsed, string FileName, string DownloadUrl, bool IsZip)>();
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var fileName = Path.GetFileName(path);
            if (!SstFileName.TryParse(fileName, out var parsed) || parsed.IsRates != isRatesDirectory) continue;
            if (!StateDirectory.States.ContainsKey(parsed.StateCode)) continue;

            var isZip = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            candidates.Add((parsed, fileName, new Uri(path).ToString(), isZip));
        }

        return SelectBestPerState(candidates, asOf);
    }

    private static List<SstFileEntry> SelectBestPerState(
        List<(SstFileName Parsed, string FileName, string DownloadUrl, bool IsZip)> candidates,
        DateOnly? asOf)
    {
        var entriesByState = new Dictionary<string, SstFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(c => c.Parsed.StateCode, StringComparer.OrdinalIgnoreCase))
        {
            var pool = group.ToList();
            if (asOf is { } targetDate)
            {
                var targetQuarter = (targetDate.Year, Quarter: (targetDate.Month - 1) / 3 + 1);
                var eligible = pool.Where(c => (c.Parsed.Year, c.Parsed.Quarter).CompareTo(targetQuarter) <= 0).ToList();
                if (eligible.Count > 0) pool = eligible;
            }

            foreach (var c in pool)
            {
                if (!entriesByState.TryGetValue(group.Key, out var existing) || IsNewer(c.Parsed, c.IsZip, existing))
                {
                    entriesByState[group.Key] = new SstFileEntry(c.Parsed.StateCode, c.FileName, c.DownloadUrl, c.IsZip);
                }
            }
        }

        return entriesByState.Values.OrderBy(e => e.StateCode, StringComparer.Ordinal).ToList();
    }

    private static bool IsNewer(SstFileName candidate, bool candidateIsZip, SstFileEntry existing)
    {
        if (!SstFileName.TryParse(existing.FileName, out var current)) return true;
        var comparison = candidate.SortKey.CompareTo(current.SortKey);
        return comparison > 0 || (comparison == 0 && candidateIsZip && !existing.IsZip);
    }

    /// <summary>
    /// Downloads an SST rate or boundary file and extracts it if it's a zip archive.
    /// Returns the local path of the resulting CSV file.
    /// </summary>
    public async Task<string?> DownloadAndExtractAsync(SstFileEntry entry, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var localFilePath = Path.Combine(outputDirectory, entry.FileName);

        if (!File.Exists(localFilePath) || new FileInfo(localFilePath).Length == 0)
        {
            var partial = $"{localFilePath}.{Guid.NewGuid():N}.part";
            try
            {
                using var response = await HttpRetry.GetWithRetryAsync(_http, entry.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true))
                {
                    await response.Content.CopyToAsync(file, cancellationToken);
                }
                File.Move(partial, localFilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (File.Exists(partial)) File.Delete(partial);
                Console.WriteLine($"[WARN] Failed downloading {entry.DownloadUrl}: {ex.Message}");
                return null;
            }
        }

        if (entry.IsZip)
        {
            var extractedCsvName = Path.GetFileNameWithoutExtension(entry.FileName) + ".csv";
            var extractedCsvPath = Path.Combine(outputDirectory, extractedCsvName);

            if (!File.Exists(extractedCsvPath) || new FileInfo(extractedCsvPath).Length == 0)
            {
                var partialCsv = $"{extractedCsvPath}.{Guid.NewGuid():N}.part";
                try
                {
                    using var archive = ZipFile.OpenRead(localFilePath);
                    var csvEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
                    if (csvEntry == null)
                    {
                        Console.WriteLine($"[WARN] No CSV/TXT entry found inside {localFilePath}.");
                        return null;
                    }
                    csvEntry.ExtractToFile(partialCsv, overwrite: true);
                    File.Move(partialCsv, extractedCsvPath, overwrite: true);
                    return extractedCsvPath;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (File.Exists(partialCsv)) File.Delete(partialCsv);
                    Console.WriteLine($"[WARN] Failed to unzip {localFilePath}: {ex.Message}");
                    return null;
                }
            }
            return extractedCsvPath;
        }

        return localFilePath;
    }
}
