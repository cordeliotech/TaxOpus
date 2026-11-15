using System.Buffers.Binary;
using System.IO.Compression;

namespace TaxOpus.Core.Geo;

/// <summary>
/// Reads polygon shapefiles (.shp geometry with its .dbf attributes) from a zip, as the Census Bureau publishes
/// TIGER/Line boundaries. Coordinates stay in the file's longitude/latitude (NAD83 for TIGER, within a metre or two
/// of WGS 84).
/// </summary>
public static class Shapefile
{
    private const int PolygonType = 5;

    /// <param name="filter">Applied to the requested attributes; features it rejects are skipped before their
    /// geometry is built.</param>
    public static List<(string[] Attributes, GeoPolygon Polygon)> ReadPolygons(string zipPath, string[] fields, Func<string[], bool>? filter = null)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        List<string[]> attributes;
        using (var dbf = new BufferedStream(Entry(zip, ".dbf").Open(), 1 << 20))
        {
            attributes = Dbf.Read(dbf, fields).ToList();
        }

        var features = new List<(string[], GeoPolygon)>();
        using var shp = new BufferedStream(Entry(zip, ".shp").Open(), 1 << 20);
        shp.ReadExactly(new byte[100]); // file header

        var recordHeader = new byte[8];
        for (var i = 0; i < attributes.Count; i++)
        {
            shp.ReadExactly(recordHeader);
            var content = new byte[BinaryPrimitives.ReadInt32BigEndian(recordHeader.AsSpan(4)) * 2]; // length in 16-bit words
            shp.ReadExactly(content);
            if (filter != null && !filter(attributes[i])) continue;
            if (BinaryPrimitives.ReadInt32LittleEndian(content) != PolygonType) continue;

            features.Add((attributes[i], new GeoPolygon(ReadRings(content))));
        }
        return features;
    }

    // Polygon record: type, bounding box (4 doubles), part count, point count, part start indexes, then x/y pairs.
    private static List<IReadOnlyList<(double X, double Y)>> ReadRings(byte[] content)
    {
        var span = content.AsSpan();
        var partCount = BinaryPrimitives.ReadInt32LittleEndian(span[36..]);
        var pointCount = BinaryPrimitives.ReadInt32LittleEndian(span[40..]);
        if (partCount < 0 || pointCount < 0 || 44 + 4L * partCount + 16L * pointCount > span.Length)
        {
            throw new InvalidDataException($"Corrupt shapefile polygon record (parts={partCount}, points={pointCount}, bytes={span.Length}).");
        }
        var parts = new int[partCount + 1];
        for (var p = 0; p < partCount; p++) parts[p] = BinaryPrimitives.ReadInt32LittleEndian(span[(44 + 4 * p)..]);
        parts[partCount] = pointCount;

        var pointsStart = 44 + 4 * partCount;
        var rings = new List<IReadOnlyList<(double X, double Y)>>(partCount);
        for (var p = 0; p < partCount; p++)
        {
            var ring = new List<(double X, double Y)>(parts[p + 1] - parts[p]);
            for (var k = parts[p]; k < parts[p + 1]; k++)
            {
                var at = pointsStart + 16 * k;
                ring.Add((BinaryPrimitives.ReadDoubleLittleEndian(span[at..]), BinaryPrimitives.ReadDoubleLittleEndian(span[(at + 8)..])));
            }
            rings.Add(ring);
        }
        return rings;
    }

    private static ZipArchiveEntry Entry(ZipArchive zip, string extension) =>
        zip.Entries.Single(e => e.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}
