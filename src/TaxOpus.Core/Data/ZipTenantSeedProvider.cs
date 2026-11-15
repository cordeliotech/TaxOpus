using System.Globalization;
using TaxOpus.Core.Models;
using TaxOpus.Core.Sources;

namespace TaxOpus.Core.Data;

/// <summary>
/// Builds a tenant tax seed from a state's ZIP-level rates (SST files or the state's own data): one rate per
/// distinct combined rate, one rule per ZIP code, and a statewide catch-all for ZIP codes the data doesn't list.
/// </summary>
public static class ZipTenantSeedProvider
{
    public const int ZipRulePriority = 10;
    public const int CatchAllPriority = 100;

    /// <summary>Source code of the curated statewide records, which carry no ZIP-level rates.</summary>
    public const string BaselineSource = "BaselineCurated";

    public static bool IsZipLevel(TaxJurisdictionRate record) => record.Source != BaselineSource;

    public static TenantStateTaxSeed GenerateTenantSeed(string stateCode, IReadOnlyCollection<TaxJurisdictionRate> zipLevelRecords, DateOnly ratesAsOf)
    {
        var upperState = stateCode.ToUpperInvariant();
        var zipRecords = zipLevelRecords
            .Where(r => IsZipLevel(r) && string.Equals(r.StateCode, upperState, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.PostalCode, StringComparer.Ordinal)
            .ToList();
        if (zipRecords.Count == 0)
        {
            throw new ArgumentException($"No ZIP-level rates for {upperState}.", nameof(zipLevelRecords));
        }

        var seenZips = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in zipRecords)
        {
            if (!seenZips.Add(r.PostalCode))
            {
                throw new ArgumentException($"Duplicate ZIP code '{r.PostalCode}' for {upperState}.", nameof(zipLevelRecords));
            }
        }

        var stateInfo = StateDirectory.Get(upperState);
        var source = zipRecords[0].Source;
        var sourceDescription = StateRateSources.Describe(source, upperState);

        // An address whose ZIP isn't listed gets the lowest rate found anywhere in the state: every address owes
        // at least that much, so the catch-all never over-collects.
        var floorRate = RoundRate(zipRecords.Min(r => r.MinCombinedRatePercent));
        var asOfText = ratesAsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var rates = zipRecords
            .Select(r => RoundRate(r.CombinedRatePercent))
            .Append(floorRate)
            .Distinct()
            .Order()
            .Select(rate => new TenantTaxRateSeed
            {
                Key = RateKey(upperState, rate),
                Name = $"{stateInfo.Name} Sales Tax ({BaselineStateTaxProvider.FormatPercent(rate)}%)",
                RatePercent = rate,
                AppliesTo = "All",
                Description = $"Combined state and local rate as of {asOfText}, from {sourceDescription}."
            })
            .ToList();

        var rules = zipRecords
            .Select(r => new TenantTaxRuleSeed
            {
                Name = $"{upperState} {r.PostalCode}",
                RateKey = RateKey(upperState, RoundRate(r.CombinedRatePercent)),
                CountryCode = "US",
                StateCode = upperState,
                // Trailing * so ZIP+4 postal codes (98101-1234) match too.
                PostalCodePattern = $"{r.PostalCode}*",
                City = string.Empty,
                RegionName = string.IsNullOrEmpty(r.City) ? $"ZIP {r.PostalCode}" : r.City,
                Description = Describe(r),
                MinRatePercent = r.MinCombinedRatePercent,
                MaxRatePercent = r.MaxCombinedRatePercent,
                EffectiveFrom = r.EffectiveDate,
                AppliesTo = "All",
                Priority = ZipRulePriority
            })
            .ToList();

        rules.Add(new TenantTaxRuleSeed
        {
            Name = $"{stateInfo.Name} Catch-All Rule",
            RateKey = RateKey(upperState, floorRate),
            CountryCode = "US",
            StateCode = upperState,
            PostalCodePattern = string.Empty,
            City = string.Empty,
            RegionName = stateInfo.Name,
            Description = $"ZIP codes not in the {stateInfo.Name} data: the lowest combined rate anywhere in {upperState} ({BaselineStateTaxProvider.FormatPercent(floorRate)}%).",
            AppliesTo = "All",
            Priority = CatchAllPriority
        });

        return new TenantStateTaxSeed
        {
            StateCode = upperState,
            StateName = stateInfo.Name,
            TaxAgencyName = stateInfo.AgencyName,
            TaxAgencyCode = $"{upperState}_DOR",
            DefaultStateRatePercent = floorRate,
            Source = source,
            SourceDescription = sourceDescription,
            RatesAsOf = ratesAsOf,
            Rates = rates,
            Rules = rules
        };
    }

    private static decimal RoundRate(decimal rate) =>
        Math.Round(rate, 4, MidpointRounding.AwayFromZero);

    private static string RateKey(string state, decimal rate) =>
        $"{state}_{RoundRate(rate).ToString("0.0000", CultureInfo.InvariantCulture)}";

    private static string Describe(TaxJurisdictionRate r)
    {
        var parts = new List<string> { $"state {BaselineStateTaxProvider.FormatPercent(r.StateRatePercent)}%" };
        if (r.CountyRatePercent != 0) parts.Add($"county {BaselineStateTaxProvider.FormatPercent(r.CountyRatePercent)}%");
        if (r.CityRatePercent != 0) parts.Add($"city {BaselineStateTaxProvider.FormatPercent(r.CityRatePercent)}%");
        if (r.SpecialDistrictRatePercent != 0) parts.Add($"districts {BaselineStateTaxProvider.FormatPercent(r.SpecialDistrictRatePercent)}%");
        // Sources that publish only combined rates (Illinois) leave the local part unsplit.
        var unsplit = r.CombinedRatePercent - r.StateRatePercent - r.CountyRatePercent - r.CityRatePercent - r.SpecialDistrictRatePercent;
        if (unsplit != 0) parts.Add($"local {BaselineStateTaxProvider.FormatPercent(unsplit)}%");

        var place = string.IsNullOrEmpty(r.City) ? $"ZIP {r.PostalCode}" : $"{r.City} {r.PostalCode}";
        var description = $"{place}: {string.Join(" + ", parts)}.";
        if (r.MinCombinedRatePercent != r.MaxCombinedRatePercent)
        {
            description += $" Parts of this ZIP are taxed from {BaselineStateTaxProvider.FormatPercent(r.MinCombinedRatePercent)}% to {BaselineStateTaxProvider.FormatPercent(r.MaxCombinedRatePercent)}%.";
        }
        return description;
    }
}
