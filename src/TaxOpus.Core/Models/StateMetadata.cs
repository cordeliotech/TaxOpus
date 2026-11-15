namespace TaxOpus.Core.Models;

public record UsStateInfo(
    string Code,
    string Name,
    string Fips,
    bool IsSstMember,
    decimal BaseStateRatePercent,
    string AgencyName
);

public static class StateDirectory
{
    public static readonly IReadOnlyDictionary<string, UsStateInfo> States = new Dictionary<string, UsStateInfo>(StringComparer.OrdinalIgnoreCase)
    {
        ["AL"] = new("AL", "Alabama", "01", false, 4.00m, "Alabama Department of Revenue"),
        ["AK"] = new("AK", "Alaska", "02", false, 0.00m, "Alaska Department of Revenue"),
        ["AZ"] = new("AZ", "Arizona", "04", false, 5.60m, "Arizona Department of Revenue"),
        ["AR"] = new("AR", "Arkansas", "05", true, 6.50m, "Arkansas Department of Finance and Administration"),
        ["CA"] = new("CA", "California", "06", false, 7.25m, "California Department of Tax and Fee Administration (CDTFA)"),
        ["CO"] = new("CO", "Colorado", "08", false, 2.90m, "Colorado Department of Revenue"),
        ["CT"] = new("CT", "Connecticut", "09", false, 6.35m, "Connecticut Department of Revenue Services"),
        ["DE"] = new("DE", "Delaware", "10", false, 0.00m, "Delaware Division of Revenue"),
        ["FL"] = new("FL", "Florida", "12", false, 6.00m, "Florida Department of Revenue"),
        ["GA"] = new("GA", "Georgia", "13", true, 4.00m, "Georgia Department of Revenue"),
        ["HI"] = new("HI", "Hawaii", "15", false, 4.00m, "Hawaii Department of Taxation"),
        ["ID"] = new("ID", "Idaho", "16", false, 6.00m, "Idaho State Tax Commission"),
        ["IL"] = new("IL", "Illinois", "17", false, 6.25m, "Illinois Department of Revenue"),
        ["IN"] = new("IN", "Indiana", "18", true, 7.00m, "Indiana Department of Revenue"),
        ["IA"] = new("IA", "Iowa", "19", true, 6.00m, "Iowa Department of Revenue"),
        ["KS"] = new("KS", "Kansas", "20", true, 6.50m, "Kansas Department of Revenue"),
        ["KY"] = new("KY", "Kentucky", "21", true, 6.00m, "Kentucky Department of Revenue"),
        // Raised from 4.45% on 2025-01-01; set to return to 4.75% on 2030-01-01
        ["LA"] = new("LA", "Louisiana", "22", false, 5.00m, "Louisiana Department of Revenue"),
        ["ME"] = new("ME", "Maine", "23", false, 5.50m, "Maine Revenue Services"),
        ["MD"] = new("MD", "Maryland", "24", false, 6.00m, "Comptroller of Maryland"),
        ["MA"] = new("MA", "Massachusetts", "25", false, 6.25m, "Massachusetts Department of Revenue"),
        ["MI"] = new("MI", "Michigan", "26", true, 6.00m, "Michigan Department of Treasury"),
        ["MN"] = new("MN", "Minnesota", "27", true, 6.875m, "Minnesota Department of Revenue"),
        ["MS"] = new("MS", "Mississippi", "28", false, 7.00m, "Mississippi Department of Revenue"),
        ["MO"] = new("MO", "Missouri", "29", false, 4.225m, "Missouri Department of Revenue"),
        ["MT"] = new("MT", "Montana", "30", false, 0.00m, "Montana Department of Revenue"),
        ["NE"] = new("NE", "Nebraska", "31", true, 5.50m, "Nebraska Department of Revenue"),
        ["NV"] = new("NV", "Nevada", "32", true, 6.85m, "Nevada Department of Taxation"),
        ["NH"] = new("NH", "New Hampshire", "33", false, 0.00m, "New Hampshire Department of Revenue Administration"),
        ["NJ"] = new("NJ", "New Jersey", "34", true, 6.625m, "New Jersey Division of Taxation"),
        // Gross receipts tax, cut from 5.00% on 2023-07-01
        ["NM"] = new("NM", "New Mexico", "35", false, 4.875m, "New Mexico Taxation and Revenue Department"),
        ["NY"] = new("NY", "New York", "36", false, 4.00m, "New York State Department of Taxation and Finance"),
        ["NC"] = new("NC", "North Carolina", "37", true, 4.75m, "North Carolina Department of Revenue"),
        ["ND"] = new("ND", "North Dakota", "38", true, 5.00m, "North Dakota Office of State Tax Commissioner"),
        ["OH"] = new("OH", "Ohio", "39", true, 5.75m, "Ohio Department of Taxation"),
        ["OK"] = new("OK", "Oklahoma", "40", true, 4.50m, "Oklahoma Tax Commission"),
        ["OR"] = new("OR", "Oregon", "41", false, 0.00m, "Oregon Department of Revenue"),
        ["PA"] = new("PA", "Pennsylvania", "42", false, 6.00m, "Pennsylvania Department of Revenue"),
        ["RI"] = new("RI", "Rhode Island", "44", true, 7.00m, "Rhode Island Division of Taxation"),
        ["SC"] = new("SC", "South Carolina", "45", false, 6.00m, "South Carolina Department of Revenue"),
        ["SD"] = new("SD", "South Dakota", "46", true, 4.20m, "South Dakota Department of Revenue"),
        ["TN"] = new("TN", "Tennessee", "47", true, 7.00m, "Tennessee Department of Revenue"),
        ["TX"] = new("TX", "Texas", "48", false, 6.25m, "Texas Comptroller of Public Accounts"),
        ["UT"] = new("UT", "Utah", "49", true, 6.10m, "Utah State Tax Commission"),
        ["VT"] = new("VT", "Vermont", "50", true, 6.00m, "Vermont Department of Taxes"),
        ["VA"] = new("VA", "Virginia", "51", false, 5.30m, "Virginia Department of Taxation"),
        ["WA"] = new("WA", "Washington", "53", true, 6.50m, "Washington State Department of Revenue"),
        ["WV"] = new("WV", "West Virginia", "54", true, 6.00m, "West Virginia State Tax Department"),
        ["WI"] = new("WI", "Wisconsin", "55", true, 5.00m, "Wisconsin Department of Revenue"),
        ["WY"] = new("WY", "Wyoming", "56", true, 4.00m, "Wyoming Department of Revenue"),
        // The scheduled increase to 7% was postponed: 6% through 2027-09-30 (DC Office of Tax and Revenue)
        ["DC"] = new("DC", "District of Columbia", "11", false, 6.00m, "DC Office of Tax and Revenue")
    };

    /// <summary>The state's entry, or a placeholder for a code that isn't a state.</summary>
    public static UsStateInfo Get(string stateCode)
    {
        var code = stateCode.ToUpperInvariant();
        return States.TryGetValue(code, out var info) ? info : new UsStateInfo(code, code, "00", false, 0m, $"{code} Department of Revenue");
    }
}
