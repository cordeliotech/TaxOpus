using System.Globalization;
using System.Text;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Data;

/// <summary>
/// Population-weighted links from ZIP codes (2020 ZCTAs) to counties and places, from the Geocorr 2022 files in
/// data/reference/geocorr2022 (see the README there).
/// </summary>
public sealed class ZipCrosswalk
{
    /// <param name="Code">County FIPS (5 digits), or state + place FIPS (7 digits; "99999" place = outside any place).</param>
    /// <param name="Share">Share of the ZIP's 2020 population in this county or place.</param>
    /// <param name="ZipName">USPS name for the ZIP, e.g. "Pittsburgh, PA".</param>
    /// <param name="Population">2020 population of the part of the ZIP in this county or place.</param>
    public record Link(string Zip, string Code, string Name, string ZipName, double Share, int Population = 0);

    public const string OutsideAnyPlace = "99999";

    private readonly ILookup<string, Link> _countiesByState;
    private readonly ILookup<string, Link> _placesByState;

    private ZipCrosswalk(ILookup<string, Link> countiesByState, ILookup<string, Link> placesByState)
    {
        _countiesByState = countiesByState;
        _placesByState = placesByState;
    }

    public static ZipCrosswalk Load(string referenceDirectory)
    {
        var dir = Path.Combine(referenceDirectory, "geocorr2022");
        // County file: zcta, county, CountyName, ZIPName, pop20, afact
        var counties = ReadRows(Path.Combine(dir, "zcta_to_county.csv"))
            .Where(r => r.Length >= 6 && r[0].Trim().Length == 5)
            .Select(r => new Link(r[0].Trim(), r[1].Trim(), r[2].Trim(), r[3].Trim(), ParseShare(r[5]), ParsePopulation(r[4])))
            .ToLookup(l => l.Code[..2]);
        // Place file: zcta, state, place, stab, PlaceName, ZIPName, pop20, afact
        var places = ReadRows(Path.Combine(dir, "zcta_to_place.csv"))
            .Where(r => r.Length >= 8 && r[0].Trim().Length == 5)
            .Select(r => new Link(r[0].Trim(), r[1].Trim() + r[2].Trim(), r[4].Trim(), r[5].Trim(), ParseShare(r[7]), ParsePopulation(r[6])))
            .ToLookup(l => l.Code[..2]);
        return new ZipCrosswalk(counties, places);
    }

    public static ZipCrosswalk FromLinks(IEnumerable<Link> counties, IEnumerable<Link> places) =>
        new(counties.ToLookup(l => l.Code[..2]), places.ToLookup(l => l.Code[..2]));

    /// <summary>County shares of each ZIP in the state, normalised to the part of the ZIP inside the state.</summary>
    public Dictionary<string, List<Link>> Counties(string stateFips) => GroupNormalised(_countiesByState[stateFips]);

    /// <summary>Place shares of each ZIP in the state, normalised the same way (includes the "outside any place" part).</summary>
    public Dictionary<string, List<Link>> Places(string stateFips) => GroupNormalised(_placesByState[stateFips]);

    /// <summary>"Pittsburgh, PA" or "Tougaloo, MS (PO boxes)" to "Pittsburgh" / "Tougaloo".</summary>
    public static string CityFromZipName(string zipName)
    {
        var comma = zipName.IndexOf(',');
        var city = comma > 0 ? zipName[..comma] : zipName;
        return city.StartsWith('[') ? string.Empty : city.Trim();
    }

    private static Dictionary<string, List<Link>> GroupNormalised(IEnumerable<Link> links) =>
        links.GroupBy(l => l.Zip).ToDictionary(g => g.Key, g =>
        {
            var total = g.Sum(l => l.Share);
            return g.Select(l => l with { Share = total > 0 ? l.Share / total : 0 }).ToList();
        });

    /// <summary>2020 population of each place in the state (summed over the ZIPs it touches).</summary>
    public Dictionary<string, int> PlacePopulations(string stateFips) =>
        _placesByState[stateFips].GroupBy(l => l.Code).ToDictionary(g => g.Key, g => g.Sum(l => l.Population));

    private static int ParsePopulation(string value) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var population) ? population : 0;

    private static double ParseShare(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var share) ? share : 0;

    private static IEnumerable<string[]> ReadRows(string path)
    {
        // Geocorr writes Latin-1 ("Española"); two header rows: names, then labels.
        using var reader = new StreamReader(path, Encoding.Latin1);
        reader.ReadLine();
        reader.ReadLine();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            yield return SstParser.SplitCsvLine(line);
        }
    }
}
