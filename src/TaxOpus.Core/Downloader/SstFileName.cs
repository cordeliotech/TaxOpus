using System.Globalization;
using System.Text.RegularExpressions;

namespace TaxOpus.Core.Downloader;

/// <summary>
/// An SST rate or boundary file name such as ARR2026Q4AUG28.csv: state, R (rates) or B (boundaries),
/// the year and quarter the file is for, and the month and day it was published.
/// </summary>
public partial record SstFileName(string StateCode, bool IsRates, int Year, int Quarter, DateOnly PublishedOn)
{
    [GeneratedRegex(@"^(?<state>[A-Za-z]{2})(?<kind>[RBrb])(?<year>\d{4})Q(?<quarter>[1-4])(?<month>[A-Za-z]{3})(?<day>\d{1,2})\.(csv|zip)$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool TryParse(string fileName, out SstFileName result)
    {
        result = null!;
        var match = Pattern().Match(fileName);
        if (!match.Success) return false;

        if (!DateTime.TryParseExact(match.Groups["month"].Value, "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthDate)) return false;

        var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        var quarter = int.Parse(match.Groups["quarter"].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);

        // Files are published ahead of their quarter, so a Q1 file published in Oct-Dec was in the previous year
        // (a 2027 Q1 file published in November went out in November 2026).
        var publishedYear = quarter == 1 && monthDate.Month >= 10 ? year - 1 : year;
        if (day < 1 || day > DateTime.DaysInMonth(publishedYear, monthDate.Month)) return false;

        result = new SstFileName(
            match.Groups["state"].Value.ToUpperInvariant(),
            char.ToUpperInvariant(match.Groups["kind"].Value[0]) == 'R',
            year,
            quarter,
            new DateOnly(publishedYear, monthDate.Month, day));
        return true;
    }

    /// <summary>
    /// Orders files oldest to newest: by the quarter they cover, then by publication date.
    /// </summary>
    public (int Year, int Quarter, DateOnly PublishedOn) SortKey => (Year, Quarter, PublishedOn);
}
