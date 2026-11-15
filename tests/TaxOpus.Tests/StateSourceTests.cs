using System.Globalization;
using TaxOpus.Core.Data;
using TaxOpus.Core.Models;
using TaxOpus.Core.Sources;
using Xunit;

namespace TaxOpus.Tests;

/// <summary>
/// Non-SST state sources, run offline against Fixtures/Data: the states' own files (Virginia, Florida, Missouri
/// and ARSSTC workbooks as published; excerpts of Missouri's boundary file and Illinois's address file for the ZIPs
/// tested) and the Geocorr crosswalk rows for those ZIPs.
/// </summary>
public class StateSourceTests
{
    private static readonly DateOnly Oct2026 = new(2026, 10, 24);

    [Theory]
    [InlineData("PA", "19103", "8")]    // Philadelphia: 6% + 2%
    [InlineData("PA", "15212", "7")]    // Pittsburgh (Allegheny County): 6% + 1%
    [InlineData("PA", "17101", "6")]    // Harrisburg: no local tax
    [InlineData("SC", "29401", "9")]    // Charleston County
    [InlineData("SC", "29601", "6")]    // Greenville County: no local tax
    [InlineData("MS", "39201", "8")]    // Jackson: 1% special infrastructure tax
    [InlineData("MS", "39501", "7")]    // Gulfport: no local tax
    [InlineData("HI", "96813", "4.5")]  // Honolulu: 4% general excise tax + 0.5% county surcharge
    [InlineData("HI", "96742", "4")]    // Kalawao County: no surcharge
    [InlineData("NY", "11201", "8.875")] // Brooklyn (Kings County): New York City
    [InlineData("NY", "10701", "8.875")] // Yonkers: city rate above Westchester's
    [InlineData("NY", "10601", "8.375")] // White Plains: Westchester County, MCTD included
    [InlineData("NY", "14202", "8.75")]  // Buffalo (Erie County)
    [InlineData("NY", "11788", "8.75")]  // Suffolk County, raised March 1, 2025
    [InlineData("NY", "12866", "7")]     // Saratoga Springs
    [InlineData("ID", "83340", "8")]     // Ketchum: 1% local option tax + 1% supplemental
    [InlineData("ID", "83353", "9")]     // Sun Valley: 3% on retail sales
    [InlineData("ID", "83638", "7")]     // McCall city holds just over half of the ZIP's people
    [InlineData("ID", "83313", "6")]     // Bellevue: its local tax is on lodging only
    public async Task CuratedRatesFollowCountyAndCityLines(string state, string zip, string expected)
    {
        var record = await Zip(new CuratedRatesSource(state), zip);

        Assert.Equal(Percent(expected), record.CombinedRatePercent);
        Assert.Equal("CURATED", record.Source);
    }

    [Fact]
    public void CuratedTableCoversEveryCountyInSouthCarolinaAndNewYork()
    {
        var rows = CuratedRatesSource.ReadRows(Path.Combine(DataRoot, "reference", CuratedRatesSource.FileName)).ToList();

        Assert.Equal(15, rows.Count(r => r is { State: "ID", Level: "Place" })); // resort cities taxing all retail sales

        Assert.Equal(46, rows.Count(r => r is { State: "SC", Level: "County" }));
        Assert.Equal(62, rows.Count(r => r is { State: "NY", Level: "County" }));
        Assert.All(rows, r => Assert.InRange(r.CombinedPercent, 4m, 10m));
    }

    [Fact]
    public async Task SplitsAZipBetweenACityRateAndItsCounty()
    {
        // Myrtle Beach's 1% tourism fee applies inside the city; the rest of 29577 is Horry County at 8%.
        var myrtleBeach = await Zip(new CuratedRatesSource("SC"), "29577");
        Assert.Equal(9m, myrtleBeach.CombinedRatePercent);
        Assert.Equal(8m, myrtleBeach.MinCombinedRatePercent);
        Assert.Equal("Myrtle Beach", myrtleBeach.City);

        var tupelo = await Zip(new CuratedRatesSource("MS"), "38801");
        Assert.Equal(7.25m, tupelo.CombinedRatePercent);
        Assert.Equal(7m, tupelo.MinCombinedRatePercent);
    }

    [Fact]
    public async Task SplitsAZipBetweenCountiesByPopulation()
    {
        // Ambridge (15003): 93% of its people are in Beaver County (6%), 7% in Allegheny County (7%).
        var ambridge = await Zip(new CuratedRatesSource("PA"), "15003");

        Assert.Equal(6m, ambridge.CombinedRatePercent);
        Assert.Equal(7m, ambridge.MaxCombinedRatePercent);
    }

    [Theory]
    [InlineData("23185", "7")]    // Williamsburg: Historic Triangle
    [InlineData("22030", "6")]    // Fairfax: Northern Virginia
    [InlineData("24541", "6.3")]  // Danville: extra 1% local tax
    [InlineData("24011", "5.3")]  // Roanoke: statewide rate
    public async Task VirginiaRatesComeFromTheLocalityWorkbook(string zip, string expected)
    {
        var record = await Zip(new VirginiaTaxSource(), zip);

        Assert.Equal(Percent(expected), record.CombinedRatePercent);
    }

    [Fact]
    public void VirginiaUsesTheTabForTheAsOfDate()
    {
        // Pittsylvania County's extra 1% began July 1, 2023.
        var path = Path.Combine(DataRoot, "raw", "va", "sales-and-use-tax-rates-by-locality-by-date.xlsx");

        Assert.Equal(5.3m, VirginiaTaxSource.ReadRates(path, new DateOnly(2023, 6, 1))["51143"].CombinedPercent);
        Assert.Equal(6.3m, VirginiaTaxSource.ReadRates(path, Oct2026)["51143"].CombinedPercent);
    }

    [Theory]
    [InlineData("33130", "7")]    // Miami-Dade: 1% surtax
    [InlineData("33602", "7.5")]  // Hillsborough (Tampa): 1.5%
    [InlineData("34102", "6")]    // Collier (Naples): no surtax
    public async Task FloridaAddsTheCountySurtax(string zip, string expected)
    {
        var record = await Zip(new FloridaSurtaxSource(), zip);

        Assert.Equal(Percent(expected), record.CombinedRatePercent);
    }

    [Fact]
    public void FloridaSurtaxTableListsEveryCounty()
    {
        var surtaxes = FloridaSurtaxSource.ReadSurtaxes(Path.Combine(DataRoot, "raw", "fl", "FDOR_DiscretionarySalesSurtaxRates.xlsx")).ToList();

        Assert.Equal(67, surtaxes.Count); // Flagler is listed twice at the same rate
        Assert.Equal(0m, surtaxes.Single(s => s.County == "CITRUS").SurtaxPercent);
    }

    [Theory]
    [InlineData("63101", "9.679")]  // St. Louis city
    [InlineData("65802", "8.1")]    // Springfield
    public async Task MissouriUsesTheStatesCombinedRatePerJurisdiction(string zip, string expected)
    {
        var record = await Zip(new MissouriDorSource(), zip);

        Assert.Equal(Percent(expected), record.CombinedRatePercent);
        Assert.Equal(4.225m, record.StateRatePercent);
    }

    [Theory]
    [InlineData("99801", "5", "5", "5")]        // Juneau: one borough-wide rate
    [InlineData("99603", "7.85", "3", "7.85")]  // Homer: most of the ZIP's people live inside the city
    [InlineData("99615", "0", "0", "7")]        // Kodiak: most of the ZIP is outside the city, where there is no tax
    [InlineData("99901", "5.5", "2.5", "6.5")]  // Ketchikan, with Saxman and the borough
    public async Task AlaskaSplitsZipsAtCityLimitsByPopulation(string zip, string expected, string min, string max)
    {
        var record = await Zip(new AlaskaArsstcSource(), zip);

        Assert.Equal(Percent(expected), record.CombinedRatePercent);
        Assert.Equal(Percent(min), record.MinCombinedRatePercent);
        Assert.Equal(Percent(max), record.MaxCombinedRatePercent);
    }

    [Fact]
    public async Task IllinoisTakesTheRateOfMostAddresses()
    {
        var records = await new IllinoisAddressSource().LoadAsync(Context());

        // Chicago went to 10.5% when the regional transit tax rose 0.25% on August 1, 2026.
        Assert.Equal(10.5m, records.Single(r => r.PostalCode == "60601").CombinedRatePercent);
        var springfield = records.Single(r => r.PostalCode == "62701");
        Assert.Equal(10.25m, springfield.CombinedRatePercent);
        Assert.Equal(7.75m, springfield.MinCombinedRatePercent);
        Assert.Equal(new DateOnly(2026, 7, 1), springfield.EffectiveDate);
    }

    [Fact]
    public void IllinoisReadsFieldsFromTheRightOfEachLine()
    {
        // A comma inside the street address must not shift the ZIP and rate columns.
        var lines = """
            fstrAddress,fstrCity,fstrState,fstrZip,fstrZipPlus4,fstrLocCode,fstrDistrict,fcurHigh_rate,fcurDrug_rate,fcurGrocery_rate
            100 MAIN ST, UNIT 2,SPRINGFIELD,IL,62701,1234,08300001,R00,9.75,1,
            """;

        var record = Assert.Single(IllinoisAddressSource.Parse(new StringReader(lines), new DateOnly(2026, 7, 1)));

        Assert.Equal("62701", record.PostalCode);
        Assert.Equal(9.75m, record.CombinedRatePercent);
    }

    [Fact]
    public async Task SeedsDescribeAnUnsplitLocalRate()
    {
        var records = await new IllinoisAddressSource().LoadAsync(Context());
        var seed = ZipTenantSeedProvider.GenerateTenantSeed("IL", records, Oct2026);

        Assert.Equal("IL-DOR", seed.Source);
        Assert.StartsWith("Chicago 60601: state 6.25% + local 4.25%.", seed.Rules.Single(r => r.Name == "IL 60601").Description);
        Assert.Contains("Illinois Department of Revenue", seed.SourceDescription);
    }

    [Theory]
    [InlineData("85004", "9.1")]    // Phoenix: Maricopa County 6.3% (with state) + city 2.8%
    [InlineData("85701", "8.7")]    // Tucson: Pima County 6.1% + city 2.6%
    [InlineData("86001", "9.386")]  // Flagstaff: Coconino County 6.9% + city 2.486%
    [InlineData("85140", "8.95")]   // San Tan Valley: incorporated after 2020, matched to its Census-designated place
    public async Task ArizonaAddsCityRatesToTheCountyRate(string zip, string expected)
    {
        var record = await Zip(new ArizonaTptSource(), zip);

        Assert.Equal(Percent(expected), record.CombinedRatePercent);
        Assert.Equal("AZ-DOR", record.Source);
    }

    [Theory]
    [InlineData("87102", "7.625")]   // Albuquerque
    [InlineData("87501", "8.1875")]  // Santa Fe
    [InlineData("87124", "7.4375")]  // Rio Rancho's Sandoval County part (7.875% in Bernalillo County)
    [InlineData("87544", "7.6875")]  // Los Alamos: a consolidated city and county
    public async Task NewMexicoUsesTheLocationCodeRates(string zip, string expected)
    {
        var record = await Zip(new NewMexicoGrtSource(), zip);

        Assert.Equal(Percent(expected), record.CombinedRatePercent);
        Assert.Equal(4.875m, record.StateRatePercent);
    }

    [Theory]
    [InlineData("NY", "<p><a href=\"/pdf/publications/sales/pub718.pdf\">New York State Sales and Use Tax Rates by Jurisdiction</a>\n - (Publication 718) - Effective December 1, 2026</p>",
        "is dated 2026-12-01, after the 2025-03-01 version")]
    [InlineData("ID", "<footer><span>ver: 4.1.8 | last updated: November 5, 2026 at 09:12 am</span></footer>",
        "is dated 2026-11-05, after the 2026-10-15 version")]
    public async Task WarnsWhenTheStatePublishesNewerRatesThanTheCopiedOnes(string state, string page, string expected)
    {
        using var http = new HttpClient(new StubHandler(page));
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            await new CuratedRatesSource(state).LoadAsync(new SourceContext(DataRoot, Oct2026, offline: false, http));
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Contains(expected, output.ToString());
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private static string DataRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data");

    private static SourceContext Context() => new(DataRoot, Oct2026, offline: true, new HttpClient());

    private static async Task<TaxJurisdictionRate> Zip(IStateRateSource source, string zip)
    {
        var records = await source.LoadAsync(Context());
        return Assert.Single(records, r => r.PostalCode == zip);
    }

    private static decimal Percent(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
