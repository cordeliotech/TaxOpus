using TaxOpus.Core.Geo;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Data;

/// <summary>
/// Rates per ZIP code from a state's rate polygons: every 2020 Census block is placed in the polygon containing its
/// interior point, and each ZIP's rate is the one covering most of its population. Exact wherever the polygons are
/// (district lines that don't follow ZIP codes included), down to the size of a Census block.
/// </summary>
public static class PolygonZipRates
{
    // Unpopulated blocks still count a little, so a ZIP with no residents (an industrial area) gets a rate.
    private const double UnpopulatedBlockWeight = 0.001;
    private const double MinShareForRange = 0.01;

    /// <param name="rateAt">The rate components for a block's point, or null where no polygon applies.</param>
    public static List<TaxJurisdictionRate> Build(
        UsStateInfo state,
        string source,
        IEnumerable<CensusBlocks.Block> blocks,
        Func<double, double, RateComponents?> rateAt,
        ZipCrosswalk crosswalk)
    {
        var zipNames = crosswalk.Counties(state.Fips).ToDictionary(z => z.Key, z => ZipCrosswalk.CityFromZipName(z.Value[0].ZipName));
        var aggregator = new ZipRateAggregator();

        foreach (var block in blocks)
        {
            if (block.Zcta.Length != 5 || rateAt(block.Longitude, block.Latitude) is not { } components) continue;
            aggregator.Add(block.Zcta, ZipRateAggregator.ZipRecord, components, zipNames.GetValueOrDefault(block.Zcta, string.Empty),
                Math.Max(block.Population, UnpopulatedBlockWeight));
        }

        return aggregator.Build(state, source, includeCity: true, minShareForRange: MinShareForRange);
    }
}
