using System.Globalization;
using TaxOpus.Core.Models;

namespace TaxOpus.Core.Data;

public static class BaselineStateTaxProvider
{
    public record StateBaseline(
        string StateCode,
        string StateName,
        decimal StateRatePercent,
        decimal AverageCombinedRatePercent,
        string AgencyName,
        List<(string RegionName, string ZipPattern, decimal RatePercent)> MajorJurisdictions
    );

    public static readonly IReadOnlyDictionary<string, StateBaseline> Baselines = new Dictionary<string, StateBaseline>(StringComparer.OrdinalIgnoreCase)
    {
        ["TX"] = new("TX", "Texas", 6.25m, 8.25m, "Texas Comptroller of Public Accounts", [
            ("Austin Metro", "787*", 8.25m),
            ("Houston Metro", "770*", 8.25m),
            ("Dallas Metro", "752*", 8.25m),
            ("Fort Worth", "761*", 8.25m),
            ("San Antonio", "782*", 8.25m),
            ("El Paso", "799*", 8.25m),
            ("Texas General State Rate", "7*", 6.25m)
        ]),
        ["CA"] = new("CA", "California", 7.25m, 8.85m, "California Department of Tax and Fee Administration (CDTFA)", [
            ("Los Angeles County", "900*", 9.50m),
            ("San Francisco", "941*", 8.625m),
            ("San Jose / Santa Clara", "951*", 9.375m),
            ("San Diego", "921*", 7.75m),
            ("Sacramento", "958*", 8.75m),
            ("Orange County", "928*", 7.75m),
            ("California Base", "9*", 7.25m)
        ]),
        ["FL"] = new("FL", "Florida", 6.00m, 7.02m, "Florida Department of Revenue", [
            ("Miami-Dade County", "331*", 7.00m),
            ("Broward County (Ft Lauderdale)", "333*", 7.00m),
            ("Palm Beach County", "334*", 7.00m),
            ("Orange County (Orlando)", "328*", 6.50m),
            ("Hillsborough County (Tampa)", "336*", 7.50m),
            ("Duval County (Jacksonville)", "322*", 7.50m),
            ("Florida Base", "3*", 6.00m)
        ]),
        ["NC"] = new("NC", "North Carolina", 4.75m, 7.00m, "North Carolina Department of Revenue", [
            ("Wake County (Raleigh)", "276*", 7.25m),
            ("Mecklenburg County (Charlotte)", "282*", 7.25m),
            ("Durham County", "277*", 7.50m),
            ("Guilford County (Greensboro)", "274*", 6.75m),
            ("Forsyth County (Winston-Salem)", "271*", 7.00m),
            ("North Carolina Base", "27*", 6.75m),
            ("North Carolina Base 28", "28*", 6.75m)
        ]),
        ["WA"] = new("WA", "Washington", 6.50m, 9.38m, "Washington State Department of Revenue", [
            ("Seattle (King County)", "981*", 10.35m),
            ("Bellevue (King County)", "980*", 10.20m),
            ("Tacoma (Pierce County)", "984*", 10.30m),
            ("Spokane (Spokane County)", "992*", 9.00m),
            ("Washington Base", "98*", 6.50m),
            ("Washington Base 99", "99*", 6.50m)
        ]),
        ["NY"] = new("NY", "New York", 4.00m, 8.53m, "New York State Department of Taxation and Finance", [
            ("New York City", "100*", 8.875m),
            ("Nassau County (Long Island)", "115*", 8.625m),
            ("Suffolk County (Long Island)", "117*", 8.625m),
            ("Westchester County", "106*", 8.375m),
            ("Erie County (Buffalo)", "142*", 8.75m),
            ("Monroe County (Rochester)", "146*", 8.00m),
            ("New York State Base", "1*", 4.00m)
        ]),
        ["OH"] = new("OH", "Ohio", 5.75m, 7.24m, "Ohio Department of Taxation", [
            ("Franklin County (Columbus)", "432*", 7.50m),
            ("Cuyahoga County (Cleveland)", "441*", 8.00m),
            ("Hamilton County (Cincinnati)", "452*", 7.80m),
            ("Ohio Base", "43*", 5.75m),
            ("Ohio Base 44", "44*", 5.75m),
            ("Ohio Base 45", "45*", 5.75m)
        ]),
        ["GA"] = new("GA", "Georgia", 4.00m, 7.40m, "Georgia Department of Revenue", [
            ("Fulton County (Atlanta)", "303*", 8.90m),
            ("DeKalb County", "300*", 8.00m),
            ("Georgia Base", "30*", 4.00m),
            ("Georgia Base 31", "31*", 4.00m)
        ]),
        ["IL"] = new("IL", "Illinois", 6.25m, 8.86m, "Illinois Department of Revenue", [
            ("Chicago (Cook County)", "606*", 10.25m),
            ("Cook County Suburbs", "600*", 10.00m),
            ("DuPage County", "601*", 8.25m),
            ("Illinois Base", "6*", 6.25m)
        ]),
        ["CO"] = new("CO", "Colorado", 2.90m, 7.81m, "Colorado Department of Revenue", [
            ("Denver County", "802*", 8.81m),
            ("Arapahoe County (Aurora)", "800*", 8.50m),
            ("El Paso County (Colorado Springs)", "809*", 8.25m),
            ("Boulder County", "803*", 9.05m),
            ("Colorado Base", "80*", 2.90m),
            ("Colorado Base 81", "81*", 2.90m)
        ])
    };

    /// <summary>
    /// Generates a standardized tenant tax seed package for the given state.
    /// </summary>
    public static TenantStateTaxSeed GenerateTenantSeed(string stateCode)
    {
        var upperState = stateCode.ToUpperInvariant();
        if (!StateDirectory.States.TryGetValue(upperState, out var stateInfo))
        {
            stateInfo = new UsStateInfo(upperState, upperState, "00", false, 0m, $"{upperState} Department of Revenue");
        }

        var baseline = Baselines.TryGetValue(upperState, out var b) ? b : null;
        var defaultRatePercent = baseline?.AverageCombinedRatePercent ?? stateInfo.BaseStateRatePercent;

        var rates = new List<TenantTaxRateSeed>
        {
            new()
            {
                Key = $"{upperState}_DEFAULT",
                Name = $"{stateInfo.Name} Standard Rate ({FormatPercent(defaultRatePercent)}%)",
                RatePercent = defaultRatePercent,
                AppliesTo = "All",
                Description = $"Standard combined sales tax rate for {stateInfo.Name}."
            }
        };

        // A separate state-only rate is only worth offering when it differs from the standard rate.
        if (stateInfo.BaseStateRatePercent != defaultRatePercent)
        {
            rates.Add(new TenantTaxRateSeed
            {
                Key = $"{upperState}_STATE_ONLY",
                Name = $"{stateInfo.Name} Base State Rate ({FormatPercent(stateInfo.BaseStateRatePercent)}%)",
                RatePercent = stateInfo.BaseStateRatePercent,
                AppliesTo = "All",
                Description = $"Statutory base sales tax rate collected for the state of {stateInfo.Name}."
            });
        }

        var rules = new List<TenantTaxRuleSeed>();

        if (baseline != null && baseline.MajorJurisdictions.Count > 0)
        {
            // Two rules with the same ZIP pattern can't be told apart, so only one of them could ever apply.
            var duplicate = baseline.MajorJurisdictions.GroupBy(j => j.ZipPattern).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
            {
                throw new InvalidOperationException(
                    $"{upperState} baseline lists ZIP pattern {duplicate.Key} more than once ({string.Join(", ", duplicate.Select(j => j.RegionName))}).");
            }

            var priority = 10;
            foreach (var jur in baseline.MajorJurisdictions)
            {
                var rateKey = $"{upperState}_{jur.ZipPattern.Replace("*", "X")}";
                rates.Add(new TenantTaxRateSeed
                {
                    Key = rateKey,
                    Name = $"{jur.RegionName}, {upperState} ({FormatPercent(jur.RatePercent)}%)",
                    RatePercent = jur.RatePercent,
                    AppliesTo = "All",
                    Description = $"{jur.RegionName} combined rate (ZIP codes {jur.ZipPattern})."
                });

                rules.Add(new TenantTaxRuleSeed
                {
                    Name = $"{jur.RegionName}, {upperState}",
                    RateKey = rateKey,
                    CountryCode = "US",
                    StateCode = upperState,
                    PostalCodePattern = jur.ZipPattern,
                    City = string.Empty,
                    RegionName = jur.RegionName,
                    Description = $"{jur.RegionName}: ZIP codes {jur.ZipPattern}.",
                    AppliesTo = "All",
                    Priority = priority
                });
                priority += 5;
            }
        }
        else
        {
            // Default statewide catch-all rule
            rules.Add(new TenantTaxRuleSeed
            {
                Name = $"{stateInfo.Name} Catch-All Rule",
                RateKey = $"{upperState}_DEFAULT",
                CountryCode = "US",
                StateCode = upperState,
                PostalCodePattern = string.Empty,
                City = string.Empty,
                RegionName = stateInfo.Name,
                Description = $"{stateInfo.Name}: all addresses in {upperState}.",
                AppliesTo = "All",
                Priority = 100
            });
        }

        return new TenantStateTaxSeed
        {
            StateCode = upperState,
            StateName = stateInfo.Name,
            TaxAgencyName = stateInfo.AgencyName,
            TaxAgencyCode = $"{upperState}_DOR",
            DefaultStateRatePercent = defaultRatePercent,
            Rates = rates,
            Rules = rules
        };
    }

    public static string FormatPercent(decimal percent) => percent.ToString("0.00##", CultureInfo.InvariantCulture);
}
