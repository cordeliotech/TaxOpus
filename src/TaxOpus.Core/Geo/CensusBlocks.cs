using System.Globalization;
using System.IO.Compression;
using System.Text;
using TaxOpus.Core.Sources;

namespace TaxOpus.Core.Geo;

/// <summary>
/// 2020 Census blocks for a state: an interior point, the population, and the ZIP Code Tabulation Area each block
/// belongs to (ZCTAs are built from whole blocks). Placing blocks inside a state's rate polygons gives
/// population-weighted rates per ZIP. 2020 data never changes, so each state's blocks are extracted once into
/// data/raw/census/blocks_&lt;fips&gt;.csv.
/// </summary>
public static class CensusBlocks
{
    public record Block(string Geoid, double Longitude, double Latitude, int Population, string Zcta);

    private const string TigerUrl = "https://www2.census.gov/geo/tiger/TIGER2020/TABBLOCK20/tl_2020_{0}_tabblock20.zip";
    private const string RelationshipFile = "tab20_zcta520_tabblock20_natl.txt";
    private const string RelationshipUrl = "https://www2.census.gov/geo/docs/maps-data/data/rel2020/zcta520/" + RelationshipFile;
    private const string Folder = "census";

    public static async Task<List<Block>> LoadAsync(SourceContext context, string stateFips, CancellationToken cancellationToken)
    {
        var cache = Path.Combine(context.RawDirectory(Folder), $"blocks_{stateFips}.csv");
        if (!File.Exists(cache) || new FileInfo(cache).Length == 0)
        {
            var tiger = await context.FetchAsync(Folder, $"tl_2020_{stateFips}_tabblock20.zip", string.Format(CultureInfo.InvariantCulture, TigerUrl, stateFips), false, cancellationToken);
            var relationship = await context.FetchAsync(Folder, RelationshipFile, RelationshipUrl, false, cancellationToken);
            var zctas = ReadZctas(relationship, stateFips);
            await using var writer = new StreamWriter(cache + ".part");
            await writer.WriteLineAsync("geoid,lon,lat,pop,zcta");
            foreach (var (geoid, lon, lat, pop) in ReadTigerBlocks(tiger))
            {
                await writer.WriteLineAsync(string.Join(',', geoid, lon.ToString("R", CultureInfo.InvariantCulture), lat.ToString("R", CultureInfo.InvariantCulture), pop, zctas.GetValueOrDefault(geoid, string.Empty)));
            }
            writer.Close();
            File.Move(cache + ".part", cache, overwrite: true);
        }

        return File.ReadLines(cache).Skip(1).Select(line => line.Split(','))
            .Select(f => new Block(f[0], double.Parse(f[1], CultureInfo.InvariantCulture), double.Parse(f[2], CultureInfo.InvariantCulture), int.Parse(f[3], CultureInfo.InvariantCulture), f[4]))
            .ToList();
    }

    /// <summary>Block GEOID to ZCTA for one state, from the national block-to-ZCTA relationship file (pipe-delimited).</summary>
    public static Dictionary<string, string> ReadZctas(string relationshipPath, string stateFips)
    {
        var zctas = new Dictionary<string, string>();
        using var reader = new StreamReader(relationshipPath, Encoding.UTF8);
        var header = (reader.ReadLine() ?? string.Empty).TrimStart('﻿').Split('|');
        var zctaColumn = Array.IndexOf(header, "GEOID_ZCTA5_20");
        var blockColumn = Array.IndexOf(header, "GEOID_TABBLOCK_20");
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var f = line.Split('|');
            if (f.Length <= Math.Max(zctaColumn, blockColumn) || !f[blockColumn].StartsWith(stateFips, StringComparison.Ordinal) || f[zctaColumn].Length != 5) continue;
            zctas[f[blockColumn]] = f[zctaColumn];
        }
        return zctas;
    }

    /// <summary>
    /// GEOID20, INTPTLON20, INTPTLAT20 and POP20 from the attribute table (.dbf) of a TIGER/Line 2020 block shapefile zip.
    /// </summary>
    public static IEnumerable<(string Geoid, double Lon, double Lat, int Pop)> ReadTigerBlocks(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.Single(e => e.Name.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase));
        using var stream = new BufferedStream(entry.Open(), 1 << 20);
        foreach (var record in Dbf.Read(stream, "GEOID20", "INTPTLON20", "INTPTLAT20", "POP20"))
        {
            yield return (record[0], double.Parse(record[1], CultureInfo.InvariantCulture), double.Parse(record[2], CultureInfo.InvariantCulture),
                int.TryParse(record[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pop) ? pop : 0);
        }
    }
}

/// <summary>
/// Reads selected fields from a dBASE (.dbf) table: a header with field descriptors, then fixed-width records.
/// </summary>
public static class Dbf
{
    public static IEnumerable<string[]> Read(Stream stream, params string[] fieldNames)
    {
        var header = new byte[32];
        stream.ReadExactly(header);
        var recordCount = BitConverter.ToInt32(header, 4);
        var headerLength = BitConverter.ToInt16(header, 8);
        var recordLength = BitConverter.ToInt16(header, 10);
        if (headerLength < 32)
        {
            throw new InvalidDataException($"Invalid DBF header length: {headerLength}");
        }

        var descriptors = new byte[headerLength - 32];
        stream.ReadExactly(descriptors);
        var fields = new List<(string Name, int Offset, int Length)>();
        var offset = 1; // each record starts with a deletion flag
        for (var i = 0; i + 32 <= descriptors.Length && descriptors[i] != 0x0D; i += 32)
        {
            var name = Encoding.ASCII.GetString(descriptors, i, 11).TrimEnd('\0', ' ');
            var length = descriptors[i + 16];
            fields.Add((name, offset, length));
            offset += length;
        }

        var wanted = fieldNames.Select(n => fields.FindIndex(f => f.Name == n)).ToArray();
        if (wanted.Any(i => i < 0))
        {
            throw new InvalidDataException($"The table has no {string.Join(", ", fieldNames.Where((_, k) => wanted[k] < 0))} field.");
        }

        var record = new byte[recordLength];
        for (var r = 0; r < recordCount; r++)
        {
            stream.ReadExactly(record);
            if (record[0] == (byte)'*') continue; // deleted
            yield return wanted.Select(i => Encoding.ASCII.GetString(record, fields[i].Offset, fields[i].Length).Trim()).ToArray();
        }
    }
}
