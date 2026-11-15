using System.Net;
using TaxOpus.Core.Exporters;
using TaxOpus.Core.Models;
using TaxOpus.Core.Services;
using TaxOpus.Core.Sources;
using Xunit;

namespace TaxOpus.Tests;

/// <summary>
/// What a run does when a state's data can't be fetched or read: it keeps the state's last good rates rather than
/// dropping to the curated baseline, which would retire tenants' ZIP rules on the next seed.
/// </summary>
public class LastGoodDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "taxopus-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AStateThatFailsKeepsTheLastExportsRates()
    {
        // The last export had Alabama ZIP rates as of July; this run has none of Alabama's files
        var exportDir = Path.Combine(_root, "exports");
        await MasterJsonExporter.ExportAsync([Rate("35203", 10m), Rate("36863", 10m)], exportDir, new DateOnly(2026, 7, 15));

        var result = await new TaxPipelineService().RunAsync(_root, offline: true, asOf: new DateOnly(2026, 10, 24));

        Assert.Equal(["AL"], result.KeptStates);
        var export = await MasterJsonExporter.ReadAsync(Path.Combine(exportDir, "us_sales_tax_combined.json"));
        Assert.Equal(new DateOnly(2026, 7, 15), export!.Manifest.RatesAsOfFor("AL"));
        Assert.Equal(new DateOnly(2026, 10, 24), export.Manifest.RatesAsOfFor("TX"));
        Assert.Equal(["35203", "36863"], export.Records.Where(r => r.StateCode == "AL").Select(r => r.PostalCode));
        var stateJson = await File.ReadAllTextAsync(Path.Combine(exportDir, "by_state", "AL.json"));
        Assert.Contains("\"35203\"", stateJson);

        // A second failing run keeps them again, still dated July
        result = await new TaxPipelineService().RunAsync(_root, offline: true, asOf: new DateOnly(2026, 11, 1));

        Assert.Equal(["AL"], result.KeptStates);
        export = await MasterJsonExporter.ReadAsync(Path.Combine(exportDir, "us_sales_tax_combined.json"));
        Assert.Equal(new DateOnly(2026, 7, 15), export!.Manifest.RatesAsOfFor("AL"));
    }

    [Fact]
    public async Task RatesComputedForALaterDateAreNotKept()
    {
        var exportDir = Path.Combine(_root, "exports");
        await MasterJsonExporter.ExportAsync([Rate("35203", 10m)], exportDir, new DateOnly(2026, 10, 24));

        var result = await new TaxPipelineService().RunAsync(_root, offline: true, asOf: new DateOnly(2026, 7, 1));

        Assert.Empty(result.KeptStates);
        var export = await MasterJsonExporter.ReadAsync(Path.Combine(exportDir, "us_sales_tax_combined.json"));
        Assert.All(export!.Records.Where(r => r.StateCode == "AL"), r => Assert.Equal("BaselineCurated", r.Source));
    }

    [Fact]
    public async Task AFailedRefreshUsesTheCachedCopy()
    {
        var context = new SourceContext(_root, new DateOnly(2026, 10, 24), offline: false, new HttpClient(new FailingHandler()));
        var cached = Path.Combine(context.RawDirectory("AL"), "taxrates_current.csv");
        await File.WriteAllTextAsync(cached, "cached");

        var path = await context.FetchAsync("AL", "taxrates_current.csv", "https://example.invalid/taxrates_current.csv", refresh: true, CancellationToken.None);

        Assert.Equal(cached, path);
        Assert.Equal("cached", await File.ReadAllTextAsync(path));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            context.FetchAsync("AL", "never_downloaded.csv", "https://example.invalid/never_downloaded.csv", refresh: true, CancellationToken.None));
    }

    [Fact]
    public async Task NewYorkLoadsWhenItsPublicationCheckFails()
    {
        // Online runs look for a newer Publication 718; the rates themselves are in the curated table
        var context = new SourceContext(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data"), new DateOnly(2026, 10, 24), offline: false, new HttpClient(new FailingHandler()));

        var records = await new CuratedRatesSource("NY").LoadAsync(context);

        Assert.Equal(8.875m, Assert.Single(records, r => r.PostalCode == "11201").CombinedRatePercent);
    }

    private static TaxJurisdictionRate Rate(string zip, decimal combined) => new()
    {
        StateCode = "AL",
        PostalCode = zip,
        City = "Birmingham",
        County = "073",
        StateRatePercent = 4m,
        CountyRatePercent = 2m,
        CityRatePercent = combined - 6m,
        CombinedRatePercent = combined,
        MinCombinedRatePercent = combined,
        MaxCombinedRatePercent = combined,
        AuthorityName = "Alabama Department of Revenue",
        Source = "AL-DOR",
        EffectiveDate = new DateOnly(2025, 1, 1)
    };

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
    }
}
