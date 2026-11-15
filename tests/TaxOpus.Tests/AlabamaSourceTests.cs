using TaxOpus.Core.Geo;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;
using TaxOpus.Core.Sources;
using Xunit;

namespace TaxOpus.Tests;

/// <summary>
/// Alabama, offline against Fixtures/Data: ALDOR's rate rows for Chambers County, the TIGER/Line boundaries of
/// Chambers County and its cities (2026 limits, 2021 limits for police jurisdictions), and the Census blocks and
/// crosswalk rows of ZIPs 36862 (LaFayette) and 36863 (Lanett).
/// </summary>
public class AlabamaSourceTests
{
    private static readonly DateOnly Oct2026 = new(2026, 10, 24);

    [Theory]
    [InlineData(-85.1906, 32.8687, "10", "1", "5")]   // Lanett: county code 7009 inside city limits, city 5%
    [InlineData(-85.4013, 32.8998, "10", "1", "5")]   // LaFayette ("La Fayette" to Census)
    [InlineData(-85.4013, 32.8700, "7.5", "3.5", "0")] // LaFayette's police jurisdiction: 7409, which LaFayette does not tax
    [InlineData(-85.4013, 32.8550, "10", "6", "0")]   // beyond LaFayette's 1.5 miles (2,684 people): unincorporated 7109
    [InlineData(-85.4013, 32.7900, "10", "6", "0")]
    public async Task ChambersCountyRatesFollowCityLimitsAndPoliceJurisdictions(double lon, double lat, string combined, string county, string city)
    {
        var rateAt = await new AlabamaAldorSource().LoadPointLookupAsync(Context());

        var rate = rateAt(lon, lat);

        Assert.NotNull(rate);
        Assert.Equal(decimal.Parse(combined), rate.Value.Combined);
        Assert.Equal(decimal.Parse(county), rate.Value.County);
        Assert.Equal(decimal.Parse(city), rate.Value.Place);
        Assert.Equal("017", rate.Value.CountyFips);
    }

    [Fact]
    public async Task ZipRatesWeighEachBlockByPopulation()
    {
        var records = await new AlabamaAldorSource().LoadAsync(Context());

        // Lanett's city limits and the unincorporated rest of its ZIP are both 10%; the city's split describes it
        var lanett = Assert.Single(records, r => r.PostalCode == "36863");
        Assert.Equal(10m, lanett.CombinedRatePercent);
        Assert.Equal(5m, lanett.CityRatePercent);
        Assert.Equal(1m, lanett.CountyRatePercent);
        Assert.Equal("Lanett", lanett.City);

        // LaFayette's ZIP reaches into the city's untaxed police jurisdiction
        var lafayette = Assert.Single(records, r => r.PostalCode == "36862");
        Assert.Equal(10m, lafayette.CombinedRatePercent);
        Assert.Equal(7.5m, lafayette.MinCombinedRatePercent);
        Assert.Equal("AL-DOR", lafayette.Source);
    }

    [Fact]
    public void RateFileRowsCollapseToOneLevyPerCode()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data", "raw", "al", "taxrates_current.csv");

        var rates = AlabamaRates.Read(path, Oct2026);

        // Valley's rows repeat for county codes 7009 and 7309; they are one 5% levy, 2.5% in its police jurisdiction
        var valley = Assert.Single(rates.Cities, c => c.Code == "9665");
        Assert.Equal(5m, valley.Rate);
        Assert.Equal(2.5m, valley.PoliceJurisdictionRate);
        Assert.Equal([9], valley.Counties);
        Assert.Equal(6m, Assert.Single(rates.Counties, c => c.Code == "7109").Rate);
        Assert.DoesNotContain(rates.Counties, c => c.Code == "7809"); // UNABATED CHAMBERS COUNTY
    }

    [Theory]
    [InlineData("notcl:*;notpj:*", null, null, true)]
    [InlineData("notcl:*;notpj:*", "TUSCALOOSA", null, false)]
    [InlineData("pj:*", null, "NORTHPORT", true)]
    [InlineData("clpj:ARAB", null, "ARAB", true)]
    [InlineData("cl:LAFAYETTE", "LAFAYETTE", null, true)]
    [InlineData("notcl:PELL CITY", "PELLCITY", null, false)]
    [InlineData("pj:OPELIKA,PHENIX CITY", "PHENIXCITY", null, false)]
    public void ZoneRulesTestCityLimitsAndPoliceJurisdictions(string rule, string? cityLimits, string? policeJurisdiction, bool expected) =>
        Assert.Equal(expected, AlabamaRateModel.Matches(rule, cityLimits, policeJurisdiction));

    [Fact]
    public void CityNamesMatchAcrossSpellings()
    {
        Assert.Equal(AlabamaRateModel.Key("La Fayette"), AlabamaRateModel.Key("LAFAYETTE"));
        Assert.Equal(AlabamaRateModel.Key("Owens Cross Roads"), AlabamaRateModel.Key("OWENS CROSS RD"));
        Assert.NotEqual(AlabamaRateModel.Key("Valley"), AlabamaRateModel.Key("Valley Head"));
    }

    [Fact]
    public void PoliceJurisdictionsReachByPopulationAndGoToTheNearestCity()
    {
        // At 32.5°N a mile of longitude is 0.017185°. Bigtown (10,000 people) reaches 3 miles from its 2021
        // limits, since when it de-annexed its eastern strip; Smalltown (2,000) reaches 1.5 miles. They are
        // 3.49 miles apart, so their police jurisdictions overlap.
        const double mile = 0.017185;
        var model = new AlabamaRateModel(
            new AlabamaRates
            {
                Counties =
                [
                    new("7009", 9, 1m, 20210501), new("7109", 9, 6m, 20221001),
                    new("7309", 9, 3.5m, 20221001), new("7409", 9, 3.5m, 20221001)
                ],
                Cities =
                [
                    new("9001", "BIGTOWN", new HashSet<int> { 9 }, 5m, 2.5m, 20240101),
                    new("9002", "SMALLTOWN", new HashSet<int> { 9 }, 4m, 2m, 20240101)
                ]
            },
            new Dictionary<string, string>
            {
                ["7009"] = "cl:*", ["7109"] = "notcl:*;notpj:*", ["7309"] = "pj:BIGTOWN", ["7409"] = "pj:SMALLTOWN"
            },
            [(Box(-85.60, -85.52), "0100001", "Bigtown"), (Box(-85.44, -85.36), "0100002", "Smalltown")],
            [(Box(-85.60, -85.50), "0100001", "Bigtown"), (Box(-85.44, -85.36), "0100002", "Smalltown")],
            new Dictionary<string, int> { ["0100001"] = 10_000, ["0100002"] = 2_000 },
            [(new GeoPolygon([[(-86.0, 32.0), (-85.0, 32.0), (-85.0, 33.0), (-86.0, 33.0)]]), "017")]);

        Assert.Equal((5m, 1m), Split(model.RateAt(-85.56, 32.5)));               // inside Bigtown
        Assert.Equal((2.5m, 3.5m), Split(model.RateAt(-85.51, 32.5)));           // de-annexed since 2021: Bigtown's PJ
        Assert.Equal((2.5m, 3.5m), Split(model.RateAt(-85.50 + mile, 32.5)));    // 1 mile from Bigtown, 2.49 from Smalltown
        Assert.Equal((2m, 3.5m), Split(model.RateAt(-85.44 - mile, 32.5)));      // in both PJs, nearer Smalltown
        Assert.Equal((2m, 3.5m), Split(model.RateAt(-85.36 + mile, 32.5)));      // 1 mile east of Smalltown
        Assert.Equal((0m, 6m), Split(model.RateAt(-85.36 + 2 * mile, 32.5)));    // 2 miles: beyond Smalltown's reach
        Assert.Null(model.RateAt(-84.5, 32.5));                                  // outside the county
    }

    private static (decimal City, decimal County) Split(RateComponents? rate) => (rate!.Value.Place, rate.Value.County);

    private static GeoPolygon Box(double west, double east) =>
        new([[(west, 32.40), (east, 32.40), (east, 32.60), (west, 32.60)]]);

    private static SourceContext Context() =>
        new(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data"), Oct2026, offline: true, new HttpClient());
}
