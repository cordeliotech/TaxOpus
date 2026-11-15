using System.Globalization;
using System.Text;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;

namespace TaxOpus.Core.Data;

/// <summary>
/// A rate that applies throughout a county or place, and when it took effect.
/// </summary>
/// <param name="CombinedPercent">The full rate (state + local), or for a place add-on, the part added to the county's rate.</param>
/// <param name="EffectiveDate">yyyyMMdd; 0 when unknown.</param>
public readonly record struct AreaRate(decimal CombinedPercent, int EffectiveDate = 0);

/// <summary>
/// Turns rates published by county and city into one rate per ZIP code, splitting each ZIP by 2020 population.
/// For states whose local taxes follow county lines (Virginia, Pennsylvania, Florida) or county and city limits
/// (Arizona, New Mexico, Mississippi, Myrtle Beach).
/// </summary>
public static class CrosswalkZipRates
{
    // Population shares put a few people in many neighbouring areas; ignore shares under 1% for a ZIP's range.
    private const double MinShareForRange = 0.01;

    /// <summary>Key for a place rate that applies only in one county (New Mexico's Rio Rancho differs by county).</summary>
    public static string PlaceInCounty(string placeKey, string countyFips) => placeKey + "|" + countyFips;

    /// <param name="countyRates">Full rates by county FIPS; counties not listed get the state rate.</param>
    /// <param name="placeRates">Full rates by state + place FIPS (7 digits), replacing the county's rate inside the
    /// place; a <see cref="PlaceInCounty"/> key overrides the place's rate in that county.</param>
    /// <param name="placeAddOns">Rates added to the county's rate inside a place (Arizona city taxes).</param>
    public static List<TaxJurisdictionRate> Build(
        UsStateInfo state,
        string source,
        ZipCrosswalk crosswalk,
        decimal stateRatePercent,
        IReadOnlyDictionary<string, AreaRate> countyRates,
        IReadOnlyDictionary<string, AreaRate>? placeRates = null,
        IReadOnlyDictionary<string, AreaRate>? placeAddOns = null)
    {
        placeRates ??= new Dictionary<string, AreaRate>();
        placeAddOns ??= new Dictionary<string, AreaRate>();
        var ratedPlaceKeys = placeRates.Keys.Select(k => k.Split('|')[0]).Concat(placeAddOns.Keys).ToHashSet();
        var counties = crosswalk.Counties(state.Fips);
        var places = ratedPlaceKeys.Count > 0 ? crosswalk.Places(state.Fips) : new Dictionary<string, List<ZipCrosswalk.Link>>();
        var aggregator = new ZipRateAggregator();

        foreach (var (zip, countyLinks) in counties)
        {
            var city = ZipCrosswalk.CityFromZipName(countyLinks[0].ZipName);
            var ratedPlaces = places.TryGetValue(zip, out var placeLinks)
                ? placeLinks.Where(p => ratedPlaceKeys.Contains(p.Code)).ToList()
                : [];
            var outsideRatedPlaces = Math.Max(0, 1 - ratedPlaces.Sum(p => p.Share));

            // The crosswalk gives a ZIP's split by county and by place separately; treat them as independent.
            // Most ZIPs lie in one county, where this is exact.
            foreach (var county in countyLinks)
            {
                var countyRate = countyRates.TryGetValue(county.Code, out var rate) ? rate : new AreaRate(stateRatePercent);
                foreach (var place in ratedPlaces)
                {
                    var placeRate = placeRates.TryGetValue(PlaceInCounty(place.Code, county.Code), out var inCounty) ? inCounty
                        : placeRates.TryGetValue(place.Code, out var full) ? full
                        : placeAddOns.TryGetValue(place.Code, out var addOn) ? new AreaRate(countyRate.CombinedPercent + addOn.CombinedPercent, addOn.EffectiveDate)
                        : countyRate;
                    aggregator.Add(zip, ZipRateAggregator.ZipRecord,
                        Components(stateRatePercent, countyRate, placeRate, county.Code), city, county.Share * place.Share);
                }

                aggregator.Add(zip, ZipRateAggregator.ZipRecord,
                    Components(stateRatePercent, countyRate, null, county.Code), city, county.Share * outsideRatedPlaces);
            }
        }

        return aggregator.Build(state, source, includeCity: true, minShareForRange: MinShareForRange);
    }

    /// <summary>
    /// Upper case, letters and spaces only, accents removed: "Española city, NM" and "ESPANOLA" compare equal
    /// once the place type is stripped with <paramref name="suffixes"/>.
    /// </summary>
    public static string NormalizeName(string name, params string[] suffixes)
    {
        var plain = new StringBuilder();
        foreach (var ch in name.Split(',')[0].Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            plain.Append(char.IsAsciiLetter(ch) ? char.ToUpperInvariant(ch) : ' ');
        }

        var result = string.Join(' ', plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var suffix in suffixes)
        {
            if (result.EndsWith(" " + suffix, StringComparison.Ordinal)) return result[..^(suffix.Length + 1)];
        }
        return result;
    }

    private static RateComponents Components(decimal stateRate, AreaRate countyRate, AreaRate? placeRate, string countyFips)
    {
        var county = countyRate.CombinedPercent - stateRate;
        var place = placeRate is { } p ? p.CombinedPercent - countyRate.CombinedPercent : 0m;
        var effective = Math.Max(countyRate.EffectiveDate, placeRate?.EffectiveDate ?? 0);
        return new RateComponents(stateRate, county, place, 0m, countyFips[2..], effective);
    }
}
