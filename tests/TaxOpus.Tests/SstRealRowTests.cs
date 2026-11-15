using System.Globalization;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;
using Xunit;

namespace TaxOpus.Tests;

/// <summary>
/// Parser tests on rows copied unchanged from the SST rate and boundary files in data/raw/sst
/// (fixtures in Fixtures/Sst, sampled so each ZIP gives the same result as the full state file).
/// Dates are fixed so the tests don't change as rates do.
/// </summary>
public class SstRealRowTests
{
    private static readonly DateOnly Oct2026 = new(2026, 10, 24);
    private static readonly DateOnly Jun2025 = new(2025, 6, 1);
    private static readonly DateOnly Jun2023 = new(2023, 6, 1);

    [Theory]
    [InlineData("NV", "89101", "8.375")]  // Las Vegas: Nevada's state row is 0%; the whole rate is a special district
    [InlineData("WI", "53202", "7.9")]    // Milwaukee: 5% state + 0.9% county + 2% city
    [InlineData("WA", "98101", "10.55")]  // Seattle: 6.5% state + 4.05% city
    [InlineData("OK", "74103", "8.517")]  // Tulsa: county and city rates
    [InlineData("TN", "37203", "9.75")]   // Nashville: jurisdiction types written without leading zeros
    [InlineData("MN", "55401", "9.025")]  // Minneapolis: Minnesota publishes only ZIP+4 and lowercase "z" rows
    [InlineData("OH", "44113", "8.0")]    // Cleveland: rates written as .05750, plus the transit district
    [InlineData("IA", "50309", "7.0")]    // Des Moines: 1% local option tax
    [InlineData("WV", "25301", "7.0")]    // Charleston: municipal tax; the whole-ZIP row carries the 6% state minimum
    [InlineData("VT", "05401", "7.0")]    // Burlington: 1% local option tax
    [InlineData("WY", "83001", "7.0")]    // Jackson: street rows cover only Teton Village's resort district
    public void CombinedRateMatchesThePublishedRate(string state, string zip, string expected)
    {
        var record = ParseZip(state, zip, Oct2026);

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), record.CombinedRatePercent);
        Assert.Equal("SST", record.Source);
    }

    [Theory]
    [InlineData("WI", "53202", "2023-06-01", "5.5")]    // before Milwaukee's 2% city tax (January 2024)
    [InlineData("WA", "98101", "2023-06-01", "10.25")]
    [InlineData("WA", "98101", "2025-06-01", "10.35")]
    [InlineData("TN", "37203", "2023-06-01", "9.25")]   // before Nashville's transit surtax (February 2025)
    [InlineData("MN", "55401", "2023-06-01", "8.025")]  // before the metro transportation tax (October 2023)
    [InlineData("WY", "83001", "2023-06-01", "6.0")]
    public void UsesTheRatesInEffectOnTheAsOfDate(string state, string zip, string asOf, string expected)
    {
        var record = ParseZip(state, zip, DateOnly.ParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture));

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), record.CombinedRatePercent);
    }

    [Fact]
    public void SplitsMilwaukeeIntoStateCountyAndCity()
    {
        var record = ParseZip("WI", "53202", Oct2026);

        Assert.Equal("Milwaukee", record.City);
        Assert.Equal("079", record.County);
        Assert.Equal(5.0m, record.StateRatePercent);
        Assert.Equal(0.9m, record.CountyRatePercent);
        Assert.Equal(2.0m, record.CityRatePercent);
        Assert.Equal(0m, record.SpecialDistrictRatePercent);
        // The city tax began 2024-01-01; Milwaukee's boundary rows were reissued on 2024-03-11.
        Assert.Equal(new DateOnly(2024, 1, 1), record.EffectiveDate);
    }

    [Fact]
    public void ReportsTheRangeOfRatesInsideAZip()
    {
        // A few 53202 rows sit outside the city and carry only the 5.9% state + county rate.
        var milwaukee = ParseZip("WI", "53202", Oct2026);
        Assert.Equal(5.9m, milwaukee.MinCombinedRatePercent);
        Assert.Equal(7.9m, milwaukee.MaxCombinedRatePercent);

        // Tulsa's 74103 has a 9.4% pocket.
        var tulsa = ParseZip("OK", "74103", Oct2026);
        Assert.Equal(8.517m, tulsa.CombinedRatePercent);
        Assert.Equal(9.4m, tulsa.MaxCombinedRatePercent);
    }

    [Fact]
    public void PrefersStreetRowsOverTheWholeZipRow()
    {
        // Vermont's whole-ZIP row for Burlington carries the 6% state rate; its street rows add the 1% local tax.
        var record = ParseZip("VT", "05401", Oct2026);

        Assert.Equal(7.0m, record.CombinedRatePercent);
        Assert.Equal(6.0m, record.MinCombinedRatePercent);
        Assert.Equal("Burlington", record.City);
    }

    [Fact]
    public void IgnoresSparseStreetRowsAndTheirCityNames()
    {
        // Wyoming publishes street rows only for special districts. Teton Village's resort district rows
        // must not set the rate (or the city name) for the rest of Jackson's 83001.
        var record = ParseZip("WY", "83001", Oct2026);

        Assert.Equal(7.0m, record.CombinedRatePercent);
        Assert.Equal(9.0m, record.MaxCombinedRatePercent);
        Assert.Equal(string.Empty, record.City);
    }

    [Fact]
    public void AddsSeattlesCityRate()
    {
        var record = ParseZip("WA", "98101", Oct2026);

        Assert.Equal(6.5m, record.StateRatePercent);
        Assert.Equal(4.05m, record.CityRatePercent);
        Assert.Equal("Seattle", record.City);
    }

    [Theory]
    [InlineData("2025-06-01", "10.3")]
    [InlineData("2026-10-24", "10.5")]
    public void FindsDistrictRatesWhoseTypeDiffersBetweenFiles(string asOf, string expected)
    {
        // Auburn's local rate is location code L1702: type 45 in the boundary file but type 63 in the rate file.
        var date = DateOnly.ParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rates = SstParser.ParseRates(ReadFixture("WA_rates.csv"), date);
        var record = Assert.Single(SstParser.ParseBoundaryAndMerge("WA", ReadFixture("WA_auburn_boundary.csv"), rates, date));

        Assert.Equal("98001", record.PostalCode);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), record.CombinedRatePercent);
        Assert.Equal(record.CombinedRatePercent - 6.5m, record.SpecialDistrictRatePercent);
    }

    [Fact]
    public void SkipsBoundaryRowsOutsideTheirEffectiveDates()
    {
        // Real Washington row for 98001, effective 2021-10-01 to 2021-12-31.
        var rates = SstParser.ParseRates(ReadFixture("WA_rates.csv"), Oct2026);
        var boundary = ReadFixture("WA_expired_boundary.csv");

        Assert.Empty(SstParser.ParseBoundaryAndMerge("WA", boundary, rates, Oct2026));
        Assert.Single(SstParser.ParseBoundaryAndMerge("WA", boundary, rates, new DateOnly(2021, 11, 1)));
    }

    [Fact]
    public void DoesNotExpandStatewideZipRanges()
    {
        // Indiana publishes one row for 46001-47997; the pipeline falls back to a single statewide record.
        var rates = SstParser.ParseRates(ReadFixture("IN_rates.csv"), Oct2026);

        Assert.Equal(7.0m, rates[("18", "45", "18")].RatePercent);
        Assert.Empty(SstParser.ParseBoundaryAndMerge("IN", ReadFixture("IN_boundary.csv"), rates, Oct2026));
    }

    [Fact]
    public void ParsesRateRowsAsOfADate()
    {
        var wisconsin = ReadFixture("WI_rates.csv");

        Assert.Equal(0.5m, SstParser.ParseRates(wisconsin, Jun2023)[("55", "00", "079")].RatePercent);
        Assert.Equal(0.9m, SstParser.ParseRates(wisconsin, Jun2025)[("55", "00", "079")].RatePercent);

        // Ohio writes rates without a leading zero (.05750).
        Assert.Equal(5.75m, SstParser.ParseRates(ReadFixture("OH_rates.csv"), Oct2026)[("39", "45", "39")].RatePercent);
    }

    private static TaxJurisdictionRate ParseZip(string state, string zip, DateOnly asOf)
    {
        var rates = SstParser.ParseRates(ReadFixture($"{state}_rates.csv"), asOf);
        var records = SstParser.ParseBoundaryAndMerge(state, ReadFixture($"{state}_boundary.csv"), rates, asOf);
        return Assert.Single(records, r => r.PostalCode == zip);
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sst", name));
}
