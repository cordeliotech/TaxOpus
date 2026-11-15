using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaxOpus.Core.Models;

namespace TaxOpus.Core.Exporters;

public class MasterJsonExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <param name="RatesAsOf">The date the rates were computed for.</param>
    /// <param name="StateRatesAsOf">States whose rates were kept from an earlier export because this run could not
    /// compute them, with the date those rates were computed for.</param>
    public record TaxExportManifest(
        string GeneratedAtUtc,
        string Version,
        int TotalRecords,
        int TotalStates,
        List<string> StatesCovered,
        string? RatesAsOf = null,
        Dictionary<string, string>? StateRatesAsOf = null
    )
    {
        /// <summary>The date a state's rates were computed for, or null when the manifest doesn't say.</summary>
        public DateOnly? RatesAsOfFor(string stateCode) =>
            DateOnly.TryParseExact(StateRatesAsOf?.GetValueOrDefault(stateCode.ToUpperInvariant()) ?? RatesAsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;
    }

    public record CombinedExportPayload(
        TaxExportManifest Manifest,
        IReadOnlyList<TaxJurisdictionRate> Records
    );

    /// <summary>
    /// Reads a combined export written by <see cref="ExportAsync"/>.
    /// </summary>
    public static async Task<CombinedExportPayload?> ReadAsync(string masterPath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(masterPath);
        return await JsonSerializer.DeserializeAsync<CombinedExportPayload>(stream, JsonOptions, cancellationToken);
    }

    public static async Task ExportAsync(
        IReadOnlyList<TaxJurisdictionRate> records,
        string outputDirectory,
        DateOnly ratesAsOf,
        IReadOnlyDictionary<string, DateOnly>? keptStateRatesAsOf = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var statesDir = Path.Combine(outputDirectory, "by_state");
        Directory.CreateDirectory(statesDir);

        var statesCovered = records.Select(r => r.StateCode).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

        var manifest = new TaxExportManifest(
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            "1.1.0",
            records.Count,
            statesCovered.Count,
            statesCovered,
            ratesAsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            keptStateRatesAsOf is { Count: > 0 }
                ? keptStateRatesAsOf.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key.ToUpperInvariant(), k => k.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                : null
        );

        // 1. Write combined master JSON atomically (streaming)
        var masterPath = Path.Combine(outputDirectory, "us_sales_tax_combined.json");
        var payload = new CombinedExportPayload(manifest, records);
        await AtomicFile.WriteJsonAsync(masterPath, payload, JsonOptions, cancellationToken);

        // 2. Write individual state slices atomically and remove stale slices
        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in statesCovered)
        {
            var stateRecords = records.Where(r => string.Equals(r.StateCode, state, StringComparison.OrdinalIgnoreCase)).ToList();
            var fileName = $"{state.ToUpperInvariant()}.json";
            expectedFiles.Add(fileName);
            var stateFilePath = Path.Combine(statesDir, fileName);
            await AtomicFile.WriteJsonAsync(stateFilePath, stateRecords, JsonOptions, cancellationToken);
        }

        foreach (var existing in Directory.EnumerateFiles(statesDir, "*.json"))
        {
            if (!expectedFiles.Contains(Path.GetFileName(existing)))
            {
                File.Delete(existing);
            }
        }
    }
}
