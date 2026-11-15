using System.Text;
using System.Text.Json;

namespace TaxOpus.Core.Sources;

/// <summary>
/// Downloads features from a public ArcGIS feature or map service into one GeoJSON FeatureCollection in WGS 84,
/// paging past the service's record limit.
/// </summary>
internal static class ColoradoArcGis
{
    public static async Task DownloadGeoJsonAsync(HttpClient http, string layerUrl, string where, string outFields, string path, CancellationToken cancellationToken, int pageSize = 200, string orderByFields = "OBJECTID")
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var features = new List<string>();
        for (var offset = 0; ;)
        {
            var order = string.IsNullOrEmpty(orderByFields) ? string.Empty : $"&orderByFields={Uri.EscapeDataString(orderByFields)}";
            var url = $"{layerUrl}/query?where={Uri.EscapeDataString(where)}&outFields={Uri.EscapeDataString(outFields)}" +
                      $"&returnGeometry=true&outSR=4326&f=geojson{order}&resultOffset={offset}&resultRecordCount={pageSize}";
            using var doc = JsonDocument.Parse(await HttpRetry.GetStringWithRetryAsync(http, url, cancellationToken));
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                throw new InvalidDataException($"{layerUrl} returned an error: {error.GetRawText()}");
            }

            var page = doc.RootElement.GetProperty("features");
            var count = page.GetArrayLength();
            if (count == 0) break;

            features.AddRange(page.EnumerateArray().Select(f => f.GetRawText()));
            offset += count;

            var more = doc.RootElement.TryGetProperty("properties", out var props) && props.TryGetProperty("exceededTransferLimit", out var exceeded) && exceeded.GetBoolean();
            if (count < pageSize && !more) break;
        }

        var partial = path + ".part";
        await File.WriteAllTextAsync(partial, "{\"type\":\"FeatureCollection\",\"features\":[" + string.Join(",", features) + "]}", new UTF8Encoding(false), cancellationToken);
        File.Move(partial, path, overwrite: true);
    }
}
