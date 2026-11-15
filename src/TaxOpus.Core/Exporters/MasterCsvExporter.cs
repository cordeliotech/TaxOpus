using System.Globalization;
using System.Text;
using TaxOpus.Core.Models;

namespace TaxOpus.Core.Exporters;

public class MasterCsvExporter
{
    public static async Task ExportAsync(
        IReadOnlyList<TaxJurisdictionRate> records,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        await AtomicFile.WriteStreamAsync(outputPath, async (stream, ct) =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 65536, leaveOpen: true);
            await writer.WriteLineAsync("State,PostalCode,City,County,StateRate,CountyRate,CityRate,DistrictRate,CombinedRatePercent,CombinedRateDecimal,AuthorityName,Source,MinCombinedRatePercent,MaxCombinedRatePercent");

            foreach (var r in records.OrderBy(r => r.StateCode).ThenBy(r => r.PostalCode))
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteAsync(Escape(r.StateCode));
                await writer.WriteAsync(',');
                await writer.WriteAsync(Escape(r.PostalCode));
                await writer.WriteAsync(',');
                await writer.WriteAsync(Escape(r.City));
                await writer.WriteAsync(',');
                await writer.WriteAsync(Escape(r.County));
                await writer.WriteAsync(',');
                await writer.WriteAsync(r.StateRatePercent.ToString("0.0000", CultureInfo.InvariantCulture));
                await writer.WriteAsync(',');
                await writer.WriteAsync(r.CountyRatePercent.ToString("0.0000", CultureInfo.InvariantCulture));
                await writer.WriteAsync(',');
                await writer.WriteAsync(r.CityRatePercent.ToString("0.0000", CultureInfo.InvariantCulture));
                await writer.WriteAsync(',');
                await writer.WriteAsync(r.SpecialDistrictRatePercent.ToString("0.0000", CultureInfo.InvariantCulture));
                await writer.WriteAsync(',');
                await writer.WriteAsync(r.CombinedRatePercent.ToString("0.0000", CultureInfo.InvariantCulture));
                await writer.WriteAsync(',');
                await writer.WriteAsync(r.CombinedRateDecimal.ToString("0.000000", CultureInfo.InvariantCulture));
                await writer.WriteAsync(',');
                await writer.WriteAsync(Escape(r.AuthorityName));
                await writer.WriteAsync(',');
                await writer.WriteAsync(Escape(r.Source));
                await writer.WriteAsync(',');
                await writer.WriteAsync(r.MinCombinedRatePercent.ToString("0.0000", CultureInfo.InvariantCulture));
                await writer.WriteAsync(',');
                await writer.WriteLineAsync(r.MaxCombinedRatePercent.ToString("0.0000", CultureInfo.InvariantCulture));
            }
            await writer.FlushAsync(ct);
        }, cancellationToken);
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }
}
