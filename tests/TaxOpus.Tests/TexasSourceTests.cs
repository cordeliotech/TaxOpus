using TaxOpus.Core.Sources;
using Xunit;

namespace TaxOpus.Tests;

/// <summary>
/// Texas, offline against Fixtures/Data: the Comptroller's 2026 Q4 rate file as published, the permitted
/// locations of six ZIPs as published on the Texas Open Data Portal, and the crosswalk rows for those ZIPs.
/// </summary>
public class TexasSourceTests
{
    private static readonly DateOnly Oct2026 = new(2026, 10, 26);
    private static readonly string DataRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data");

    [Fact]
    public void RateFileGivesEveryAuthoritysRate()
    {
        var rates = TexasComptrollerSource.ReadRates(Path.Combine(DataRoot, "raw", "tx", "taxrates.txt"));

        Assert.Equal(20264, rates.Quarter);
        Assert.Equal(new DateOnly(2026, 10, 1), rates.QuarterStart);
        Assert.Equal(6.25m, rates.StatePercent);
        Assert.Equal(1943, rates.PercentByAuthority.Count);
        Assert.Equal(1m, rates.PercentByAuthority["3057994"]);      // Dallas MTA (DART)
        Assert.Equal(0.375m, rates.PercentByAuthority["3015664"]);  // San Antonio ATD, listed only on some rows
        Assert.Equal(0.5m, rates.PercentByAuthority["4014004"]);    // Bell County
    }

    [Theory]
    [InlineData("76701", "8.25", "0.5", "1.5", "0", "309")]       // Waco: McLennan County, numbered after Maverick by the Comptroller
    [InlineData("78203", "8.25", "0", "1.125", "0.875", "029")]   // San Antonio: VIA 0.5% + ATD 0.375%
    [InlineData("78632", "8.25", "0.5", "0", "1.5", "177")]       // Harwood: Gonzales County ESDs 1 and 2
    [InlineData("78140", "8.25", "0.5", "1.5", "0", "177")]       // Nixon city
    [InlineData("77399", "6.75", "0.5", "0", "0", "373")]         // Livingston PO boxes: not a Census ZIP area, but 140 locations
    public async Task ZipsGetTheRateOfMostOfTheirLocations(string zip, string combined, string county, string city, string district, string countyFips)
    {
        var records = await new TexasComptrollerSource().LoadAsync(Context());

        var record = Assert.Single(records, r => r.PostalCode == zip);
        Assert.Equal(decimal.Parse(combined), record.CombinedRatePercent);
        Assert.Equal(decimal.Parse(county), record.CountyRatePercent);
        Assert.Equal(decimal.Parse(city), record.CityRatePercent);
        Assert.Equal(decimal.Parse(district), record.SpecialDistrictRatePercent);
        Assert.Equal(countyFips, record.County);
        Assert.Equal("TX-CPA", record.Source);
        Assert.Equal(new DateOnly(2026, 10, 1), record.EffectiveDate);
    }

    [Fact]
    public async Task AMistypedZipWithOneLocationIsLeftOut()
    {
        var records = await new TexasComptrollerSource().LoadAsync(Context());

        Assert.DoesNotContain(records, r => r.PostalCode == "76300");
        Assert.Equal(5, records.Count);
    }

    [Theory]
    [InlineData("57", "113")]   // Dallas
    [InlineData("153", "305")]  // Lynn, the last county before the M block
    [InlineData("154", "313")]  // Madison
    [InlineData("159", "323")]  // Maverick
    [InlineData("160", "307")]  // McCulloch
    [InlineData("162", "311")]  // McMullen
    [InlineData("163", "325")]  // Medina
    [InlineData("254", "507")]  // Zavala
    [InlineData("0", "")]
    public void ComptrollerCountyNumbersBecomeFipsCodes(string number, string fips) =>
        Assert.Equal(fips, TexasComptrollerSource.CountyFips(number));

    private static SourceContext Context() => new(DataRoot, Oct2026, offline: true, new HttpClient());
}
