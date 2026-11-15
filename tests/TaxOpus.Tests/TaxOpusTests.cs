using System.Text.RegularExpressions;
using TaxOpus.Core.Data;
using TaxOpus.Core.Exporters;
using TaxOpus.Core.Models;
using TaxOpus.Core.Parsers;
using Xunit;

namespace TaxOpus.Tests;

public class TaxOpusTests
{
    [Fact]
    public void StateDirectory_ContainsAll50StatesAndDc()
    {
        Assert.True(StateDirectory.States.Count >= 51);
        Assert.True(StateDirectory.States.ContainsKey("TX"));
        Assert.True(StateDirectory.States.ContainsKey("CA"));
        Assert.True(StateDirectory.States.ContainsKey("NC"));
        Assert.True(StateDirectory.States.ContainsKey("WY"));
    }

    [Fact]
    public void SstParser_CorrectlyParsesRates()
    {
        var sampleRateCsv = """
        56,45,56,0.04000,0.04000,0.04000,0.04000,20060701,99991231
        56,00,039,0.02000,0.02000,0.02000,0.02000,20171001,99991231
        56,69,02290,0.02000,0.02000,0.02000,0.02000,20081001,99991231
        """;

        var rates = SstParser.ParseRates(sampleRateCsv);

        Assert.Equal(4.00m, rates[("56", "45", "56")].RatePercent);
        Assert.Equal(2.00m, rates[("56", "00", "039")].RatePercent);
        Assert.Equal(2.00m, rates[("56", "69", "02290")].RatePercent);
    }

    [Fact]
    public void SstParser_CorrelatesBoundaryAndComputesCombinedRate()
    {
        // First row of the Wyoming boundary file: Teton Village's resort district is keyed as type 69 in the rate file.
        var sampleRateCsv = """
        56,45,56,0.04000,0.04000,0.04000,0.04000,20060701,99991231
        56,00,039,0.02000,0.02000,0.02000,0.02000,20171001,99991231
        56,69,02290,0.02000,0.02000,0.02000,0.02000,20081001,99991231
        """;

        var sampleBoundaryCsv = """
        A,19010101,99991231,3330,3340,E,W,CODY,Ln,,,,,,Teton Village,83001,,,,,,56039,56,56,039,02290,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,
        """;

        var rates = SstParser.ParseRates(sampleRateCsv);
        var records = SstParser.ParseBoundaryAndMerge("WY", sampleBoundaryCsv, rates);

        Assert.Single(records);
        var rec = records[0];
        Assert.Equal("WY", rec.StateCode);
        Assert.Equal("83001", rec.PostalCode);
        Assert.Equal("Teton Village", rec.City);
        Assert.Equal(4.00m, rec.StateRatePercent);
        Assert.Equal(2.00m, rec.CountyRatePercent);
        Assert.Equal(2.00m, rec.CityRatePercent);
        Assert.Equal(8.00m, rec.CombinedRatePercent);
        Assert.Equal(0.08m, rec.CombinedRateDecimal);
    }

    [Fact]
    public void SstParser_RecognizesAStatewideOnlyBoundaryFile()
    {
        // Indiana's boundary file: one ZIP+4 row covering the whole state (plus its whole-ZIP twin)
        const string indiana = "4,20050101,29991231,,,,,,,,,,,,,,,46001,0,47997,9999,,18,18,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,";
        const string wyoming = "A,19010101,99991231,3330,3340,E,W,CODY,Ln,,,,,,Teton Village,83001,,,,,,56039,56,56,039,02290,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,";
        var asOf = new DateOnly(2026, 10, 25);

        Assert.True(SstParser.IsStatewideOnly(new StringReader(indiana), asOf));
        Assert.False(SstParser.IsStatewideOnly(new StringReader(indiana + "\n" + wyoming), asOf));
        Assert.False(SstParser.IsStatewideOnly(new StringReader(indiana), new DateOnly(2004, 1, 1))); // nothing in effect
    }

    [Fact]
    public void ZipRateAggregator_DescribesATiedRateByItsHeaviestSplit()
    {
        // Downtown Tuscaloosa: the city's limits (3% county + 3% city) and its police jurisdiction (4.5% + 1.5%,
        // changed more recently) both total 10%; the limits hold most of the ZIP's people.
        var aggregator = new ZipRateAggregator();
        aggregator.Add("35401", ZipRateAggregator.ZipRecord, new RateComponents(4m, 3m, 3m, 0m, "125", 20191001), "", weight: 900);
        aggregator.Add("35401", ZipRateAggregator.ZipRecord, new RateComponents(4m, 4.5m, 1.5m, 0m, "125", 20251101), "", weight: 100);

        var record = Assert.Single(aggregator.Build(StateDirectory.Get("AL"), "test"));

        Assert.Equal(10m, record.CombinedRatePercent);
        Assert.Equal(3m, record.CountyRatePercent);
        Assert.Equal(3m, record.CityRatePercent);
        Assert.Equal(new DateOnly(2025, 11, 1), record.EffectiveDate); // the newest component of either split
    }

    public static TheoryData<string> AllStates => [.. StateDirectory.States.Keys];

    [Theory]
    [MemberData(nameof(AllStates))]
    public void BaselineSeed_GivesEveryRuleItsOwnRate(string state)
    {
        var seed = BaselineStateTaxProvider.GenerateTenantSeed(state);

        Assert.Equal(seed.Rates.Count, seed.Rates.Select(r => r.Key).Distinct().Count());
        Assert.Equal(seed.Rules.Count, seed.Rules.Select(r => r.PostalCodePattern).Distinct().Count());
        Assert.All(seed.Rules, rule => Assert.Single(seed.Rates, rate => rate.Key == rule.RateKey));
        // ZIP rules leave City blank so they match any city in the ZIP.
        Assert.All(seed.Rules, rule => Assert.Equal(string.Empty, rule.City));
    }

    [Fact]
    public void BaselineSeed_GeorgiaZipRulesKeepTheirOwnRates()
    {
        // DeKalb, Gwinnett and Cobb used to share 300*; only the first could ever match, and all three got its rate.
        var georgia = BaselineStateTaxProvider.GenerateTenantSeed("GA");
        var dekalb = Assert.Single(georgia.Rules, r => r.PostalCodePattern == "300*");
        Assert.Equal(8.00m, georgia.Rates.Single(r => r.Key == dekalb.RateKey).RatePercent);
    }

    [Fact]
    public async Task MasterExporters_WriteAtomicallyAndRemoveStaleStateSlices()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"taxopus-export-test-{Guid.NewGuid():N}");
        try
        {
            var byStateDir = Path.Combine(tempDir, "by_state");
            Directory.CreateDirectory(byStateDir);
            var staleSlice = Path.Combine(byStateDir, "ZZ.json");
            await File.WriteAllTextAsync(staleSlice, "[]");

            var records = new List<TaxJurisdictionRate>
            {
                new()
                {
                    StateCode = "WY",
                    PostalCode = "83001",
                    City = "Teton Village",
                    County = "039",
                    StateRatePercent = 4.00m,
                    CountyRatePercent = 2.00m,
                    CityRatePercent = 2.00m,
                    SpecialDistrictRatePercent = 0m,
                    CombinedRatePercent = 8.00m,
                    AuthorityName = "Wyoming Department of Revenue",
                    Source = "SSTGB",
                    MinCombinedRatePercent = 8.00m,
                    MaxCombinedRatePercent = 8.00m
                }
            };

            await MasterJsonExporter.ExportAsync(records, tempDir, new DateOnly(2026, 10, 24));
            await MasterCsvExporter.ExportAsync(records, Path.Combine(tempDir, "us_sales_tax_combined.csv"));

            Assert.False(File.Exists(staleSlice));
            Assert.True(File.Exists(Path.Combine(byStateDir, "WY.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, "us_sales_tax_combined.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, "us_sales_tax_combined.csv")));
            Assert.Empty(Directory.EnumerateFiles(tempDir, "*.part", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ZipTenantSeedProvider_RoundsRatesConsistentlyAndRejectsDuplicateZips()
    {
        var records = new List<TaxJurisdictionRate>
        {
            new()
            {
                StateCode = "WY", PostalCode = "83001", City = "Jackson", County = "039",
                StateRatePercent = 4.00m, CountyRatePercent = 2.00m, CityRatePercent = 0m, SpecialDistrictRatePercent = 0m,
                CombinedRatePercent = 6.00001m, MinCombinedRatePercent = 6.00001m, MaxCombinedRatePercent = 6.00001m,
                AuthorityName = "WY DOR", Source = "SSTGB"
            },
            new()
            {
                StateCode = "WY", PostalCode = "83002", City = "Kelly", County = "039",
                StateRatePercent = 4.00m, CountyRatePercent = 2.00m, CityRatePercent = 0m, SpecialDistrictRatePercent = 0m,
                CombinedRatePercent = 6.00002m, MinCombinedRatePercent = 6.00002m, MaxCombinedRatePercent = 6.00002m,
                AuthorityName = "WY DOR", Source = "SSTGB"
            }
        };

        var seed = ZipTenantSeedProvider.GenerateTenantSeed("WY", records, new DateOnly(2026, 10, 24));
        Assert.Single(seed.Rates);
        Assert.Equal("WY_6.0000", seed.Rates[0].Key);

        records.Add(records[0]);
        Assert.Throws<ArgumentException>(() =>
            ZipTenantSeedProvider.GenerateTenantSeed("WY", records, new DateOnly(2026, 10, 24)));
    }

    [Fact]
    public async Task MasterCsvExporter_QuotesCarriageReturns()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"taxopus-csv-{Guid.NewGuid():N}.csv");
        try
        {
            await MasterCsvExporter.ExportAsync(
            [
                new TaxJurisdictionRate
                {
                    StateCode = "WY", PostalCode = "83001", City = "Line1\rLine2", County = "039",
                    StateRatePercent = 4m, CountyRatePercent = 2m, CityRatePercent = 0m, SpecialDistrictRatePercent = 0m,
                    CombinedRatePercent = 6m, AuthorityName = "WY DOR", Source = "SSTGB",
                    MinCombinedRatePercent = 6m, MaxCombinedRatePercent = 6m
                }
            ], tempFile);

            var csv = await File.ReadAllTextAsync(tempFile);
            Assert.Contains("\"Line1\rLine2\"", csv);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task CuratedRatesSource_LoadsLouisianaParishesAndCities()
    {
        var dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data"));
        using var http = new HttpClient();
        var context = new TaxOpus.Core.Sources.SourceContext(dataDir, new DateOnly(2026, 11, 14), offline: true, http);
        var source = new TaxOpus.Core.Sources.CuratedRatesSource("LA");

        var records = await source.LoadAsync(context);

        Assert.True(records.Count > 400);
        var nola = records.First(r => r.PostalCode == "70112");
        Assert.Equal("LA", nola.StateCode);
        Assert.Equal(10.00m, nola.CombinedRatePercent);
    }
}
