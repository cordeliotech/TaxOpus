namespace TaxOpus.Core.Geo;

/// <summary>
/// A polygon (or multipolygon) in longitude/latitude, with its edges bucketed by latitude band so a
/// point-in-polygon test only looks at the edges near the point's latitude. Rate areas can have tens of
/// thousands of vertices, and a state has hundreds of thousands of Census blocks to place.
/// </summary>
public sealed class GeoPolygon
{
    private readonly double[] _edges; // x1, y1, x2, y2 per edge
    private readonly int[][] _bands;   // edge indexes per latitude band
    private readonly double _bandHeight;

    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }

    /// <param name="rings">Every ring of every part (outer rings and holes), each as (x, y) pairs. The even-odd
    /// rule over all rings handles holes without knowing which ring is which.</param>
    public GeoPolygon(IReadOnlyList<IReadOnlyList<(double X, double Y)>> rings)
    {
        var edges = new List<double>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var ring in rings)
        {
            for (var i = 0; i < ring.Count; i++)
            {
                var (x1, y1) = ring[i];
                var (x2, y2) = ring[(i + 1) % ring.Count];
                if (x1 == x2 && y1 == y2) continue;
                edges.Add(x1); edges.Add(y1); edges.Add(x2); edges.Add(y2);
                minX = Math.Min(minX, x1); maxX = Math.Max(maxX, x1);
                minY = Math.Min(minY, y1); maxY = Math.Max(maxY, y1);
            }
        }

        _edges = [.. edges];
        (MinX, MinY, MaxX, MaxY) = (minX, minY, maxX, maxY);

        var edgeCount = _edges.Length / 4;
        var bandCount = Math.Max(1, edgeCount / 8);
        _bandHeight = Math.Max((MaxY - MinY) / bandCount, 1e-12);
        var bands = Enumerable.Range(0, bandCount).Select(_ => new List<int>()).ToArray();
        for (var e = 0; e < edgeCount; e++)
        {
            var (low, high) = (Math.Min(_edges[e * 4 + 1], _edges[e * 4 + 3]), Math.Max(_edges[e * 4 + 1], _edges[e * 4 + 3]));
            for (var b = Band(low, bandCount); b <= Band(high, bandCount); b++) bands[b].Add(e);
        }
        _bands = bands.Select(b => b.ToArray()).ToArray();
    }

    public IEnumerable<(double X1, double Y1, double X2, double Y2)> Edges()
    {
        for (var e = 0; e < _edges.Length; e += 4) yield return (_edges[e], _edges[e + 1], _edges[e + 2], _edges[e + 3]);
    }

    public bool Contains(double x, double y)
    {
        if (x < MinX || x > MaxX || y < MinY || y > MaxY) return false;

        // Even-odd ray casting towards +x over the edges in the point's latitude band
        var inside = false;
        foreach (var e in _bands[Band(y, _bands.Length)])
        {
            var (x1, y1, x2, y2) = (_edges[e * 4], _edges[e * 4 + 1], _edges[e * 4 + 2], _edges[e * 4 + 3]);
            if ((y1 > y) != (y2 > y) && x < (x2 - x1) * (y - y1) / (y2 - y1) + x1)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private int Band(double y, int bandCount) => Math.Clamp((int)((y - MinY) / _bandHeight), 0, bandCount - 1);
}

/// <summary>
/// Features (polygons with their attributes) on a coarse grid, to find which features contain a point.
/// </summary>
public sealed class PolygonIndex<T>
{
    private const double CellSize = 0.05; // degrees, about 5 km
    private readonly List<(GeoPolygon Polygon, T Value)> _features;
    private readonly Dictionary<(int, int), List<int>> _cells = [];

    public PolygonIndex(IEnumerable<(GeoPolygon Polygon, T Value)> features)
    {
        _features = features.ToList();
        for (var i = 0; i < _features.Count; i++)
        {
            var p = _features[i].Polygon;
            for (var cx = Cell(p.MinX); cx <= Cell(p.MaxX); cx++)
            for (var cy = Cell(p.MinY); cy <= Cell(p.MaxY); cy++)
            {
                if (!_cells.TryGetValue((cx, cy), out var list)) _cells[(cx, cy)] = list = [];
                list.Add(i);
            }
        }
    }

    public int Count => _features.Count;

    /// <summary>Every feature containing the point (overlapping layers, such as special districts, can return several).</summary>
    public IEnumerable<T> FindAll(double x, double y)
    {
        if (!_cells.TryGetValue((Cell(x), Cell(y)), out var candidates)) yield break;
        foreach (var i in candidates)
        {
            if (_features[i].Polygon.Contains(x, y)) yield return _features[i].Value;
        }
    }

    public bool TryFind(double x, double y, out T value)
    {
        foreach (var found in FindAll(x, y))
        {
            value = found;
            return true;
        }
        value = default!;
        return false;
    }

    private static int Cell(double v) => (int)Math.Floor(v / CellSize);
}
