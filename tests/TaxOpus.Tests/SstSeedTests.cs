using System.Text.RegularExpressions;
using TaxOpus.Core.Data;
using TaxOpus.Core.Exporters;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;
using Xunit;

namespace TaxOpus.Tests;

/// <summary>
/// Tenant seeds built from SST ZIP rates, using the real-row fixtures in Fixtures/Sst.
/// </summary>
public class SstSeedTests
{
    private static readonly DateOnly Oct2026 = new(2026, 10, 24);

    [Fact]
    public void GivesEveryZipItsOwnRuleAndEachDistinctRateOneRate()
    {
        var records = Parse("WY");
        var seed = ZipTenantSeedProvider.GenerateTenantSeed("WY", records, Oct2026);

        Assert.Equal("SST", seed.Source);
        Assert.Equal(records.Count + 1, seed.Rules.Count);
        Assert.Equal(seed.Rates.Count, seed.Rates.Select(r => r.RatePercent).Distinct().Count());

        var jackson = Assert.Single(seed.Rules, r => r.Name == "WY 83001");
        Assert.Equal("83001*", jackson.PostalCodePattern);
        Assert.Equal(string.Empty, jackson.City);
        Assert.Equal(7.0m, seed.Rates.Single(r => r.Key == jackson.RateKey).RatePercent);
        Assert.Equal("Wyoming Sales Tax (7.00%)", seed.Rates.Single(r => r.Key == jackson.RateKey).Name);
    }

    [Fact]
    public void CatchAllUsesTheLowestRateInTheState()
    {
        var records = Parse("WY");
        var seed = ZipTenantSeedProvider.GenerateTenantSeed("WY", records, Oct2026);

        var catchAll = Assert.Single(seed.Rules, r => r.PostalCodePattern.Length == 0);
        Assert.Equal(ZipTenantSeedProvider.CatchAllPriority, catchAll.Priority);
        Assert.Equal(records.Min(r => r.MinCombinedRatePercent), seed.Rates.Single(r => r.Key == catchAll.RateKey).RatePercent);
    }

    [Fact]
    public void DescribesTheRateAndAnyRangeInsideTheZip()
    {
        var seed = ZipTenantSeedProvider.GenerateTenantSeed("WI", Parse("WI"), Oct2026);
        var milwaukee = Assert.Single(seed.Rules, r => r.Name == "WI 53202");

        Assert.Equal("Milwaukee 53202: state 5.00% + county 0.90% + city 2.00%. Parts of this ZIP are taxed from 5.90% to 7.90%.", milwaukee.Description);
        Assert.Equal(5.9m, milwaukee.MinRatePercent);
        Assert.Equal(7.9m, milwaukee.MaxRatePercent);
    }

    private static List<TaxJurisdictionRate> Parse(string state)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sst");
        var rates = SstParser.ParseRates(File.ReadAllText(Path.Combine(dir, $"{state}_rates.csv")), Oct2026);
        return SstParser.ParseBoundaryAndMerge(state, File.ReadAllText(Path.Combine(dir, $"{state}_boundary.csv")), rates, Oct2026);
    }
}
