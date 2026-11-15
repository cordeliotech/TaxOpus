using TaxOpus.Core.Downloader;
using Xunit;

namespace TaxOpus.Tests;

public class SstFileNameTests
{
    [Theory]
    [InlineData("GAR2026Q4OCT01.csv", "GAR2026Q4SEP17.csv")]  // October is newer, though "SEP" sorts after "OCT"
    [InlineData("WAR2026Q4NOV02.zip", "WAR2026Q4OCT01.csv")]
    [InlineData("WAR2027Q1NOV20.zip", "WAR2026Q4DEC01.csv")]  // a 2027 Q1 file published November 2026
    [InlineData("INR2008Q4MAY7.csv", "INR2005Q1JAN6.csv")]
    public void OrdersFilesByQuarterThenPublicationDate(string newer, string older)
    {
        Assert.True(SstFileName.TryParse(newer, out var newerName));
        Assert.True(SstFileName.TryParse(older, out var olderName));
        Assert.True(newerName.SortKey.CompareTo(olderName.SortKey) > 0);
    }

    [Fact]
    public void ParsesStateKindAndPublicationDate()
    {
        Assert.True(SstFileName.TryParse("WAR2027Q1NOV20.zip", out var name));
        Assert.Equal("WA", name.StateCode);
        Assert.True(name.IsRates);
        Assert.Equal(new DateOnly(2026, 11, 20), name.PublishedOn);

        Assert.True(SstFileName.TryParse("WAR2026Q1APR05.csv", out var springRevision));
        Assert.Equal(new DateOnly(2026, 4, 5), springRevision.PublishedOn);

        Assert.True(SstFileName.TryParse("KYR2012Q4Aug13.csv", out _));
        Assert.True(SstFileName.TryParse("WYR2026Q4AUG20.CSV", out _));
        Assert.True(SstFileName.TryParse("WYB2026Q4AUG20.csv", out var boundary));
        Assert.False(boundary.IsRates);
        Assert.False(SstFileName.TryParse("README.txt", out _));
    }

    [Fact]
    public void FindsTheNewestCachedFilePerState()
    {
        var dir = Directory.CreateTempSubdirectory("taxopus-sst-").FullName;
        try
        {
            foreach (var file in new[] { "GAR2026Q4SEP17.csv", "GAR2026Q4OCT01.csv", "GAR2026Q4OCT01.zip", "GAB2026Q4AUG20.csv", "WAR2026Q4AUG27.csv" })
            {
                File.WriteAllText(Path.Combine(dir, file), "");
            }

            var rates = SstDownloader.FindCachedFiles(dir, isRatesDirectory: true);

            Assert.Equal(["GAR2026Q4OCT01.zip", "WAR2026Q4AUG27.csv"], rates.Select(e => e.FileName));
            Assert.Equal(["GAB2026Q4AUG20.csv"], SstDownloader.FindCachedFiles(dir, isRatesDirectory: false).Select(e => e.FileName));

            File.WriteAllText(Path.Combine(dir, "GAR2026Q3JUN15.csv"), "");
            var q3Rates = SstDownloader.FindCachedFiles(dir, isRatesDirectory: true, asOf: new DateOnly(2026, 9, 15));
            Assert.Contains("GAR2026Q3JUN15.csv", q3Rates.Select(e => e.FileName));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
