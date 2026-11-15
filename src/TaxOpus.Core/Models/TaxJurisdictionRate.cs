namespace TaxOpus.Core.Models;

/// <summary>
/// Normalized sales tax rate for a specific geographic jurisdiction.
/// </summary>
public record TaxJurisdictionRate
{
    public string CountryCode { get; init; } = "US";
    public string StateCode { get; init; } = string.Empty;
    public string PostalCode { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string County { get; init; } = string.Empty;
    
    /// <summary>
    /// State sales tax rate in percentage (e.g. 4.00 for 4.00%).
    /// </summary>
    public decimal StateRatePercent { get; init; }
    
    /// <summary>
    /// County sales tax rate in percentage (e.g. 1.00 for 1.00%).
    /// </summary>
    public decimal CountyRatePercent { get; init; }
    
    /// <summary>
    /// City / Municipal sales tax rate in percentage.
    /// </summary>
    public decimal CityRatePercent { get; init; }
    
    /// <summary>
    /// Special taxing district (transit, hospital, school, emergency) rate in percentage.
    /// </summary>
    public decimal SpecialDistrictRatePercent { get; init; }
    
    /// <summary>
    /// Total combined sales tax rate in percentage (e.g. 8.25 for 8.25%).
    /// </summary>
    public decimal CombinedRatePercent { get; init; }

    /// <summary>
    /// Fractional combined rate (e.g. 0.0825 for 8.25%).
    /// </summary>
    public decimal CombinedRateDecimal => CombinedRatePercent / 100m;

    /// <summary>
    /// Lowest combined rate found anywhere in the postal code. Differs from <see cref="CombinedRatePercent"/>
    /// when the postal code spans jurisdictions with different rates.
    /// </summary>
    public decimal MinCombinedRatePercent { get; init; }

    /// <summary>
    /// Highest combined rate found anywhere in the postal code.
    /// </summary>
    public decimal MaxCombinedRatePercent { get; init; }

    public string AuthorityName { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public DateOnly? EffectiveDate { get; init; }
}
