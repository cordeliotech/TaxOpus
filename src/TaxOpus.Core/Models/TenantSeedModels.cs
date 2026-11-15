namespace TaxOpus.Core.Models;

/// <summary>
/// Pre-filled seed package for a single tenant in a specific state.
/// </summary>
public record TenantStateTaxSeed
{
    public string StateCode { get; init; } = string.Empty;
    public string StateName { get; init; } = string.Empty;
    public string TaxAgencyName { get; init; } = string.Empty;
    public string TaxAgencyCode { get; init; } = string.Empty;
    public decimal DefaultStateRatePercent { get; init; }

    /// <summary>
    /// Where the rates come from: "SST", a state source code such as "FL-DOR", or "BaselineCurated".
    /// </summary>
    public string Source { get; init; } = "BaselineCurated";

    /// <summary>
    /// What the rates come from, for seed scripts and descriptions (e.g. "Florida DOR county surtax rates, ...").
    /// </summary>
    public string SourceDescription { get; init; } = "TaxOpus curated baseline (state rate plus a few metro ZIP prefixes; no other local rates)";

    /// <summary>
    /// Date the SST rates were computed for; null for curated rates, which carry no date.
    /// </summary>
    public DateOnly? RatesAsOf { get; init; }

    public List<TenantTaxRateSeed> Rates { get; init; } = [];
    public List<TenantTaxRuleSeed> Rules { get; init; } = [];
}

public record TenantTaxRateSeed
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public decimal RatePercent { get; init; }
    public decimal RateDecimal => RatePercent / 100m;
    public string AppliesTo { get; init; } = "All"; // "All", "Material", "Service"
    public string Description { get; init; } = string.Empty;
}

public record TenantTaxRuleSeed
{
    public string Name { get; init; } = string.Empty;
    public string RateKey { get; init; } = string.Empty;
    public string CountryCode { get; init; } = "US";
    public string StateCode { get; init; } = string.Empty;
    /// <summary>
    /// ZIP code glob such as "787*". Empty matches any postal code.
    /// </summary>
    public string PostalCodePattern { get; init; } = string.Empty;

    /// <summary>
    /// Exact city name to match. Leave empty for rules that match by ZIP code.
    /// with the customer's city as written, so a region name here would stop the rule from ever matching.
    /// </summary>
    public string City { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable name of the area the rule covers (e.g. "Austin Metro").
    /// </summary>
    public string RegionName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Lowest and highest combined rates inside the area, when known (SST ZIP rules); they differ when the
    /// ZIP spans jurisdictions with different rates.
    /// </summary>
    public decimal? MinRatePercent { get; init; }

    public decimal? MaxRatePercent { get; init; }

    /// <summary>
    /// Date the area's current rate took effect, when known (SST ZIP rules). When a re-run finds the rate
    /// changed, the old rule ends the day before and a new one starts on this date.
    /// </summary>
    public DateOnly? EffectiveFrom { get; init; }

    public string AppliesTo { get; init; } = "All";
    public int Priority { get; init; } = 100;
}
