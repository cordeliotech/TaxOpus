using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace TaxOpus.Core.Parsers;

/// <summary>
/// Reads cell text from .xlsx worksheets (an .xlsx file is a zip of XML parts), enough for the rate tables states
/// publish. Numbers come back as stored, e.g. "5.2999999999999999E-2"; dates come back as Excel serial numbers.
/// </summary>
public static class XlsxReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static IReadOnlyList<string> SheetNames(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return Sheets(zip).Select(s => s.Name).ToList();
    }

    /// <summary>
    /// The rows of a worksheet, each cell at its column position (missing cells are empty strings).
    /// </summary>
    public static List<string[]> ReadRows(string path, int sheetIndex = 0)
    {
        using var zip = ZipFile.OpenRead(path);
        var sheet = Sheets(zip)[sheetIndex];
        var sharedStrings = ReadSharedStrings(zip);
        var doc = Load(zip, sheet.Part);
        var rows = new List<string[]>();

        foreach (var row in doc.Descendants(Main + "row"))
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements(Main + "c"))
            {
                var column = ColumnIndex((string?)cell.Attribute("r") ?? string.Empty, cells.Count);
                while (cells.Count < column) cells.Add(string.Empty);
                cells.Add(CellText(cell, sharedStrings));
            }
            rows.Add([.. cells]);
        }
        return rows;
    }

    /// <summary>
    /// Excel stores dates as days since 1899-12-30.
    /// </summary>
    public static DateOnly? FromSerialDate(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial > 0
            ? DateOnly.FromDateTime(new DateTime(1899, 12, 30).AddDays(Math.Floor(serial)))
            : null;

    private static string CellText(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants(Main + "t").Select(t => t.Value));
        }

        var value = cell.Element(Main + "v")?.Value ?? string.Empty;
        return type == "s" && int.TryParse(value, out var index) && index >= 0 && index < sharedStrings.Count ? sharedStrings[index] : value;
    }

    private static int ColumnIndex(string reference, int fallback)
    {
        var index = 0;
        var letters = 0;
        foreach (var ch in reference)
        {
            if (ch is < 'A' or > 'Z') break;
            index = index * 26 + (ch - 'A' + 1);
            letters++;
        }
        return letters == 0 ? fallback : index - 1;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return [];
        using var stream = entry.Open();
        return XDocument.Load(stream).Root!.Elements(Main + "si")
            .Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value)))
            .ToList();
    }

    private static List<(string Name, string Part)> Sheets(ZipArchive zip)
    {
        var workbook = Load(zip, "xl/workbook.xml");
        var targets = Load(zip, "xl/_rels/workbook.xml.rels").Root!.Elements(PackageRels + "Relationship")
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);

        return workbook.Descendants(Main + "sheet")
            .Select(s =>
            {
                var target = targets[(string)s.Attribute(OfficeRels + "id")!].TrimStart('/');
                if (target.StartsWith("../", StringComparison.Ordinal)) target = target["../".Length..];
                return ((string)s.Attribute("name")!, target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target);
            })
            .ToList();
    }

    private static XDocument Load(ZipArchive zip, string part)
    {
        var entry = zip.GetEntry(part) ?? throw new InvalidDataException($"The workbook has no {part}.");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }
}
