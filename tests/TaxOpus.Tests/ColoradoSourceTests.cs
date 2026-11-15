using System.Globalization;
using System.Text.Json;
using TaxOpus.Core.Data;
using TaxOpus.Core.Geo;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;
using TaxOpus.Core.Sources;
using Xunit;
using LocationCode = TaxOpus.Core.Sources.ColoradoCdorSource.LocationCode;

namespace TaxOpus.Tests;

/// <summary>
/// Colorado, run offline against Fixtures/Data: CDOR's July–December 2026 rate workbook as published, the curated
/// code mapping, and excerpts of the boundary layers (El Paso, Pueblo and Eagle counties; Vail and Fountain; five
/// DOLA districts; the Pikes Peak RTA) and the Census blocks and crosswalk rows of ZIPs 81657 and 80817.
/// </summary>
public class ColoradoSourceTests
{
    private static readonly DateOnly Oct2026 = new(2026, 10, 25);
    private static readonly string DataRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data");
    private static readonly string Workbook = Path.Combine(DataRoot, "raw", "co", "Colorado_Jurisdiction_Codes_Rates_July-Dec2026.xlsx");

    [Fact]
    public void WorkbookComponentsAddUpToEachCodesTotal()
    {
        var codes = ColoradoCdorSource.ReadLocationCodes(Workbook);

        Assert.Equal(447, codes.Count);
        Assert.All(codes, c => Assert.Equal(c.TotalPercent, c.Components.Sum(x => x.Percent)));
        var denver = Assert.Single(codes, c => c.Code == "010006");
        Assert.Equal(9.15m, denver.TotalPercent);
        Assert.Equal(new[] { ("State", 2.9m), ("City", 5.15m), ("RTD", 1.0m), ("CD", 0.1m) }, denver.Components);
    }

    [Theory]
    [InlineData("Colorado_Jurisdiction_Codes_Rates_July-Dec2026.xlsx", "2026-07-01", "2026-12-31")]
    [InlineData("Colorado_Jurisdiction_Codes_Rates_Jan-June2026_Apr26.xlsx", "2026-01-01", "2026-06-30")]
    public void WorkbookNamesGiveTheHalfYearTheyCover(string name, string from, string to)
    {
        Assert.Equal((DateOnly.Parse(from), DateOnly.Parse(to)), ColoradoCdorSource.Period(name));
    }

    [Theory]
    [InlineData(-106.3742, 39.6403, "440060", "9.4")]    // Vail town hall
    [InlineData(-106.501011, 39.621183, "440068", "6.69")] // Eagle-Vail Metropolitan District
    [InlineData(-106.634715, 39.636357, "440209", "5.69")] // unincorporated Eagle in the Eagle River FPD
    [InlineData(-104.698969, 38.698206, "040099", "8.53")] // Fountain inside the Pikes Peak RTA
    [InlineData(-104.692092, 38.671824, "040031", "7.53")] // Fountain outside it
    [InlineData(-104.706842, 38.843778, "040208", "6.13")] // unincorporated El Paso in the Cimarron Hills FPD
    [InlineData(-104.648531, 38.379989, "020069", "5.9")]  // Pueblo West Metropolitan District
    [InlineData(-104.95, 38.15, "020206", "3.9")]          // unincorporated Pueblo County outside its districts
    public async Task PointsGetTheCodeForTheirPlaceAndDistricts(double longitude, double latitude, string code, string total)
    {
        // CDOR's own lookup gives the same codes and totals at all but the Vail and Pueblo County points
        var model = await new ColoradoCdorSource().LoadModelAsync(Context());

        var match = model.Locate(longitude, latitude);

        Assert.NotNull(match);
        Assert.Equal(code, match.Row.Code);
        Assert.Equal(decimal.Parse(total, CultureInfo.InvariantCulture), model.RateAt(longitude, latitude)?.Combined);
    }

    [Fact]
    public async Task PointLookupSplitsTheRateIntoLevels()
    {
        var rateAt = await new ColoradoCdorSource().LoadPointLookupAsync(Context());

        var vail = rateAt(-106.3742, 39.6403);
        Assert.Equal(new RateComponents(2.9m, 1.0m, 4.5m, 1.0m, "037", 20260701, 9.4m), vail);
        Assert.Null(rateAt(-104.9903, 39.7392)); // Denver is not in the fixture
    }

    [Fact]
    public async Task ZipRatesWeighBlocksByPopulation()
    {
        var records = await new ColoradoCdorSource().LoadAsync(Context());

        // Vail: the town, plus unincorporated Eagle County (4.9%) east and west of it
        var vail = Assert.Single(records, r => r.PostalCode == "81657");
        Assert.Equal(9.4m, vail.CombinedRatePercent);
        Assert.Equal(4.9m, vail.MinCombinedRatePercent);
        Assert.Equal("Vail", vail.City);
        // Fountain: most people live outside the RTA (7.53%); annexed parts inside it pay 8.53%, unincorporated El Paso 5.13%
        var fountain = Assert.Single(records, r => r.PostalCode == "80817");
        Assert.Equal(7.53m, fountain.CombinedRatePercent);
        Assert.Equal(5.13m, fountain.MinCombinedRatePercent);
        Assert.Equal(8.53m, fountain.MaxCombinedRatePercent);
        Assert.Equal("CO-CDOR", fountain.Source);
    }

    [Fact]
    public void CodesForOnePlaceAreToldApartByDistricts()
    {
        // County 99 (Testa): Alpha is split by RTD and has a LID code with no boundary; the unincorporated area has
        // a fire district code. Beta has a code only in county 98 (Testb), so its part in Testa is composed.
        LocationCode[] codes =
        [
            Code("990001", "ALPHA", "TESTA", ("State", 2.9m), ("City", 3m), ("RTD", 1m)),
            Code("990002", "ALPHA (CD ONLY)", "TESTA", ("State", 2.9m), ("City", 3m)),
            Code("990003", "ALPHA (IN LID)", "TESTA", ("State", 2.9m), ("City", 3m), ("RTD", 1m), ("LID", 0.5m)),
            Code("990206", "UNINCORPORATED", "TESTA", ("State", 2.9m), ("County", 1m)),
            Code("990208", "UNINCORPORATED (FPD)", "TESTA", ("State", 2.9m), ("County", 1m), ("FPD", 0.5m)),
            Code("980001", "BETA", "TESTB", ("State", 2.9m), ("City", 2m)),
            Code("980206", "UNINCORPORATED", "TESTB", ("State", 2.9m))
        ];
        var overrides = new Dictionary<string, ColoradoCdorSource.CodeOverride>
        {
            ["990003"] = new("990003", null, [], NoGeometry: true),
            ["990208"] = new("990208", null, ["11111"], NoGeometry: false)
        };
        var model = new ColoradoRateModel(codes, overrides, new DateOnly(2026, 7, 1),
            [Feature(0, 0, 10, 10, ("GEOID", "08999"), ("BASENAME", "Testa")), Feature(10, 0, 20, 10, ("GEOID", "08998"), ("BASENAME", "Testb"))],
            [Feature(1, 1, 4, 4, ("first_city", "Alpha")), Feature(8, 1, 12, 4, ("first_city", "Beta"))],
            [Feature(0, 0, 2, 10, ("lgid", ColoradoCdorSource.RtdId)), Feature(5, 5, 7, 7, ("lgid", "11111"))],
            []);

        Assert.Equal(("990001", "boundaries"), Locate(model, 1.5, 2));     // Alpha inside RTD; the LID code is never guessed
        Assert.Equal(("990002", "boundaries"), Locate(model, 3, 2));       // Alpha outside RTD
        Assert.Equal(("990208", "boundaries"), Locate(model, 6, 6));       // unincorporated, in the fire district
        Assert.Equal(("990206", "boundaries"), Locate(model, 6, 8.5));     // unincorporated elsewhere
        Assert.Equal(("990206", "composed"), Locate(model, 9, 2));         // Beta in Testa: 3.9% + Beta's 2%
        Assert.Equal(5.9m, model.RateAt(9, 2)?.Combined);
        Assert.Equal(2m, model.RateAt(9, 2)?.Place);
        Assert.Equal(("980001", "single"), Locate(model, 11, 2));          // Beta in Testb
        Assert.Null(model.Locate(30, 5));
    }

    private static (string, string) Locate(ColoradoRateModel model, double x, double y) =>
        model.Locate(x, y) is { } m ? (m.Row.Code, m.Outcome) : default;

    private static LocationCode Code(string code, string name, string county, params (string Type, decimal Percent)[] components) =>
        new(code, name, county, components.Sum(c => c.Percent), components);

    private static GeoJson.Feature Feature(double x1, double y1, double x2, double y2, params (string Name, string Value)[] properties)
    {
        var json = JsonSerializer.Serialize(properties.ToDictionary(p => p.Name, p => p.Value));
        var element = JsonDocument.Parse(json).RootElement;
        return new GeoJson.Feature(
            new GeoPolygon([[(x1, y1), (x2, y1), (x2, y2), (x1, y2)]]),
            element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
    }

    private static SourceContext Context() => new(DataRoot, Oct2026, offline: true, new HttpClient());
}
