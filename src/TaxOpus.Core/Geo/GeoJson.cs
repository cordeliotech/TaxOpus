using System.Text.Json;

namespace TaxOpus.Core.Geo;

/// <summary>
/// Reads Polygon and MultiPolygon features (longitude/latitude, WGS 84) from a GeoJSON FeatureCollection.
/// </summary>
public static class GeoJson
{
    public record Feature(GeoPolygon Polygon, IReadOnlyDictionary<string, JsonElement> Properties);

    public static List<Feature> ReadFeatures(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var features = new List<Feature>();

        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object) continue;
            var coordinates = geometry.GetProperty("coordinates");
            var rings = geometry.GetProperty("type").GetString() switch
            {
                "Polygon" => Rings(coordinates),
                "MultiPolygon" => coordinates.EnumerateArray().SelectMany(Rings).ToList(),
                _ => []
            };
            if (rings.Count == 0) continue;

            var properties = feature.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
                ? props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : [];
            features.Add(new Feature(new GeoPolygon(rings), properties));
        }
        return features;
    }

    private static List<IReadOnlyList<(double X, double Y)>> Rings(JsonElement polygon) =>
        polygon.EnumerateArray()
            .Select(ring => (IReadOnlyList<(double X, double Y)>)ring.EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList())
            .ToList();
}
