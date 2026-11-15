using System.Text;
using TaxOpus.Core.Geo;
using TaxOpus.Core.Sources;
using Xunit;

namespace TaxOpus.Tests;

/// <summary>
/// The polygon engine: point-in-polygon, the Census block table reader, and California's rate areas.
/// </summary>
public class GeoTests
{
    private static readonly DateOnly Oct2026 = new(2026, 10, 24);

    [Fact]
    public void PolygonHolesAndPartsUseTheEvenOddRule()
    {
        // A 10x10 square with a 2x2 hole, plus a separate 1x1 part to the east
        var polygon = new GeoPolygon(
        [
            [(0, 0), (10, 0), (10, 10), (0, 10)],
            [(4, 4), (6, 4), (6, 6), (4, 6)],
            [(20, 0), (21, 0), (21, 1), (20, 1)]
        ]);

        Assert.True(polygon.Contains(1, 1));
        Assert.False(polygon.Contains(5, 5));   // in the hole
        Assert.True(polygon.Contains(20.5, 0.5)); // second part
        Assert.False(polygon.Contains(15, 5));
        Assert.False(polygon.Contains(-1, 5));
    }

    [Fact]
    public void IndexFindsTheFeatureContainingAPoint()
    {
        var index = new PolygonIndex<string>(
        [
            (new GeoPolygon([[(0, 0), (1, 0), (1, 1), (0, 1)]]), "west"),
            (new GeoPolygon([[(1, 0), (2, 0), (2, 1), (1, 1)]]), "east")
        ]);

        Assert.True(index.TryFind(1.5, 0.5, out var found));
        Assert.Equal("east", found);
        Assert.False(index.TryFind(3, 0.5, out _));
    }

    [Fact]
    public void DbfReaderReturnsTheRequestedFields()
    {
        var table = BuildDbf(("GEOID20", 15), ("POP20", 12), [("060750124011000", "52"), ("060750124011001", "0")]);

        var rows = Dbf.Read(new MemoryStream(table), "POP20", "GEOID20").ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(["52", "060750124011000"], rows[0]);
    }

    [Fact]
    public async Task CaliforniaRatesComeFromTheRateAreaContainingEachBlock()
    {
        // San Francisco is one rate area (a consolidated city and county); ZIP 94102's 154 blocks all fall inside it.
        var records = await new CaliforniaCdtfaSource().LoadAsync(Context());

        var civicCenter = Assert.Single(records, r => r.PostalCode == "94102");
        Assert.Equal(8.625m, civicCenter.CombinedRatePercent);
        Assert.Equal(7.25m, civicCenter.StateRatePercent);
        Assert.Equal("San Francisco", civicCenter.City);
        Assert.Equal("075", civicCenter.County); // FIPS, from CDTFA's county number 38
        Assert.Equal("CA-CDTFA", civicCenter.Source);
    }

    [Fact]
    public async Task CaliforniaAnswersForASinglePoint()
    {
        // San Francisco City Hall; CDTFA's own lookup gives 8.625% (tax area 380019400000)
        var rateAt = await new CaliforniaCdtfaSource().LoadPointLookupAsync(Context());

        Assert.Equal(8.625m, rateAt(-122.4193, 37.7793)?.Combined);
        Assert.Null(rateAt(-118.2437, 34.0522)); // Los Angeles is not in the fixture
    }

    private static SourceContext Context() =>
        new(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data"), Oct2026, offline: true, new HttpClient());

    private static byte[] BuildDbf((string Name, byte Length) first, (string Name, byte Length) second, (string, string)[] records)
    {
        var fields = new[] { first, second };
        var recordLength = (short)(1 + fields.Sum(f => f.Length));
        var headerLength = (short)(32 + 32 * fields.Length + 1);
        var bytes = new List<byte>();
        var header = new byte[32];
        header[0] = 3;
        BitConverter.GetBytes(records.Length).CopyTo(header, 4);
        BitConverter.GetBytes(headerLength).CopyTo(header, 8);
        BitConverter.GetBytes(recordLength).CopyTo(header, 10);
        bytes.AddRange(header);
        foreach (var (name, length) in fields)
        {
            var descriptor = new byte[32];
            Encoding.ASCII.GetBytes(name).CopyTo(descriptor, 0);
            descriptor[11] = (byte)'C';
            descriptor[16] = length;
            bytes.AddRange(descriptor);
        }
        bytes.Add(0x0D);
        foreach (var (a, b) in records)
        {
            bytes.Add((byte)' ');
            bytes.AddRange(Encoding.ASCII.GetBytes(a.PadRight(first.Length)));
            bytes.AddRange(Encoding.ASCII.GetBytes(b.PadLeft(second.Length)));
        }
        return [.. bytes];
    }
}
