using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ExcelDataReader;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Parsing;

/// <summary>
/// Gets the table of operations out of the other files banks let people download: Excel workbooks (.xlsx, .xls),
/// OpenDocument spreadsheets and text documents (.ods, .odt), Word documents (.docx) and web pages saved as ".xls" or
/// ".html" (some banks' "Excel" export is a web page). Every cell becomes text (dates as yyyy-mm-dd, numbers with a
/// dot), then the table is read like a CSV export.
/// </summary>
public static partial class TableFiles
{
    static TableFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>What kind of table file this is ("Excel", "OpenDocument", "Word", "Web page"), or null.</summary>
    public static string? Kind(byte[] bytes, string fileName)
    {
        if (bytes.Length < 8) return null;
        // Old Excel (.xls): an OLE compound file.
        if (bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0) return "Excel";
        if (bytes[0] == 'P' && bytes[1] == 'K')
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                if (zip.GetEntry("xl/workbook.xml") is not null) return "Excel";
                if (zip.GetEntry("word/document.xml") is not null) return "Word";
                if (zip.GetEntry("content.xml") is not null && zip.GetEntry("mimetype") is { } m)
                {
                    using var r = new StreamReader(m.Open());
                    if (r.ReadToEnd().StartsWith("application/vnd.oasis.opendocument", StringComparison.Ordinal)) return "OpenDocument";
                }
            }
            catch (InvalidDataException e) { ProductivityApp.Finance.Services.Diagnostics.Report("TableFiles.Kind (not a zip package)", e); return null; }
            return null;
        }
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".csv" or ".tsv" or ".txt" or ".qif" or ".ofx" or ".qfx" or ".qbo") return null;
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        if (Regex.IsMatch(head, @"<\s*(html|table|!doctype html)", RegexOptions.IgnoreCase)) return "Web page";
        return null;
    }

    /// <summary>The rows of the table of operations in the file (several sheets or tables of the same layout follow each other).</summary>
    public static List<List<string>> Read(byte[] bytes, string fileName) => Pick(Tables(bytes, fileName)).SelectMany(t => t).ToList();

    /// <summary>
    /// The accounts of the file: one per table of operations when the tables are titled with different account numbers
    /// (an export with a sheet per account), otherwise the one table of operations (a statement split over pages or
    /// sheets). Title: the account's name as written above its operations, or "" for a single account.
    /// </summary>
    public static List<(string Title, List<List<string>> Table)> ReadAccounts(byte[] bytes, string fileName)
    {
        var picked = Pick(Tables(bytes, fileName));
        var titled = picked.Select(t => (Title: AccountTitle(t), Rows: t)).ToList();
        if (titled.Count > 1 && titled.All(t => t.Title is not null) && titled.Select(t => t.Title!.Value.Number).Distinct().Count() == titled.Count)
            return [.. titled.Select(t => (t.Title!.Value.Name, t.Rows))];
        return [("", picked.SelectMany(t => t).ToList())];
    }

    private static List<List<List<string>>> Tables(byte[] bytes, string fileName) => Kind(bytes, fileName) switch
    {
        "Excel" => Excel(bytes),
        "OpenDocument" => OpenDocument(bytes),
        "Word" => Word(bytes),
        "Web page" => Html(CsvStatementReader.Decode(bytes)),
        _ => [],
    };

    [GeneratedRegex(@"(?<![\dA-Z])(N[°O]?\s*:?\s*)?(\d[\d ]{5,}\d)(?![\d])", RegexOptions.IgnoreCase)]
    private static partial Regex AccountNumberText();

    /// <summary>
    /// The account a table is about, from the lines above its first operation: a line holding an account number (six
    /// digits or more) and a name ("C/C EUROCOMPTE DUO N° 00020123401" gives "C/C EUROCOMPTE DUO"), or null.
    /// </summary>
    private static (string Name, string Number)? AccountTitle(List<List<string>> table)
    {
        foreach (var row in table)
        {
            if (row.Any(c => DateParser.TryParse(c, out _))) break;
            var text = string.Join(' ', row.Where(c => c.Length > 0)).Trim();
            var m = AccountNumberText().Match(text);
            if (!m.Success) continue;
            var name = text[..m.Index].Trim(' ', '-', ':');
            if (name.Length == 0) name = text.Remove(m.Index, m.Length).Trim(' ', '-', ':');
            if (name.Length == 0) continue;
            return (name, m.Groups[2].Value.Replace(" ", ""));
        }
        return null;
    }

    /// <summary>
    /// The tables that hold the operations: the one with the most dated lines with an amount, and every other table of
    /// the same width (a statement split over pages), in order. Summary tables and headers of the bank are left out.
    /// </summary>
    private static List<List<List<string>>> Pick(List<List<List<string>>> tables)
    {
        static int Score(List<List<string>> t) => t.Count(r => r.Any(c => DateParser.TryParse(c, out _)) && r.Any(c => Regex.IsMatch(c, @"\d[.,]\d{2}\b|^-?\d+(\.\d+)?$")));
        static int Width(List<List<string>> t) => t.Where(r => r.Count(c => c.Length > 0) >= 2).Select(r => r.Count).GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var scored = tables.Select(t => (Table: t, Score: Score(t), Width: Width(t))).ToList();
        var best = scored.OrderByDescending(x => x.Score).FirstOrDefault();
        if (best.Table is null || best.Score == 0) return tables;
        return [.. scored.Where(x => x.Width == best.Width && (x.Score > 0 || ReferenceEquals(x.Table, best.Table) || x.Table.Count <= 2)).Select(x => x.Table)];
    }

    private static List<List<List<string>>> Excel(byte[] bytes)
    {
        var tables = new List<List<List<string>>>();
        using var reader = ExcelReaderFactory.CreateReader(new MemoryStream(bytes));
        do
        {
            var rows = new List<List<string>>();
            while (reader.Read())
            {
                var row = new List<string>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++) row.Add(Cell(reader.GetValue(i)));
                // Trailing empty cells are not columns.
                while (row.Count > 0 && row[^1].Length == 0) row.RemoveAt(row.Count - 1);
                if (row.Count > 0) rows.Add(row);
            }
            tables.Add(rows);
        } while (reader.NextResult());
        return tables;
    }

    private static string Cell(object? value) => value switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        double v => v.ToString("0.############", CultureInfo.InvariantCulture),
        decimal v => v.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        _ => TextNorm.CollapseSpaces(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
    };

    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

    /// <summary>Every table of an OpenDocument spreadsheet (.ods) or text document (.odt).</summary>
    private static List<List<List<string>>> OpenDocument(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var stream = zip.GetEntry("content.xml")!.Open();
        var doc = XDocument.Load(stream);
        var tables = new List<List<List<string>>>();
        foreach (var table in doc.Descendants(Table + "table"))
        {
            var rows = new List<List<string>>();
            foreach (var tr in table.Descendants(Table + "table-row"))
            {
                // Rows and cells repeated by the file (empty ones at the end of a sheet are repeated thousands of times).
                var row = new List<string>();
                foreach (var td in tr.Elements().Where(e => e.Name == Table + "table-cell" || e.Name == Table + "covered-table-cell"))
                {
                    var repeat = Math.Min(int.TryParse((string?)td.Attribute(Table + "number-columns-repeated"), out var n) ? n : 1, 64);
                    var value = OdsValue(td);
                    for (var i = 0; i < repeat; i++) row.Add(value);
                }
                while (row.Count > 0 && row[^1].Length == 0) row.RemoveAt(row.Count - 1);
                if (row.Count == 0) continue;
                var times = Math.Min(int.TryParse((string?)tr.Attribute(Table + "number-rows-repeated"), out var m) ? m : 1, 1000);
                for (var i = 0; i < times; i++) rows.Add(row);
            }
            tables.Add(rows);
        }
        return tables;
    }

    private static string OdsValue(XElement td)
    {
        var type = (string?)td.Attribute(Office + "value-type");
        if (type is "date" && (string?)td.Attribute(Office + "date-value") is { Length: >= 10 } d) return d[..10];
        if (type is "float" or "currency" or "percentage" && (string?)td.Attribute(Office + "value") is { } v
            && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return x.ToString("0.############", CultureInfo.InvariantCulture);
        return TextNorm.CollapseSpaces(string.Join(' ', td.Elements(TextNs + "p").Select(p => p.Value)));
    }

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Every table of a Word document.</summary>
    private static List<List<List<string>>> Word(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var stream = zip.GetEntry("word/document.xml")!.Open();
        var doc = XDocument.Load(stream);
        return doc.Descendants(W + "tbl")
            .Where(t => !t.Ancestors(W + "tbl").Any())
            .Select(t => t.Elements(W + "tr")
                .Select(tr => tr.Elements(W + "tc").Select(tc => TextNorm.CollapseSpaces(string.Join(' ', tc.Elements(W + "p").Select(p => string.Concat(p.Descendants(W + "t").Select(x => x.Value)))))).ToList())
                .Where(r => r.Any(c => c.Length > 0))
                .ToList())
            .ToList();
    }

    [GeneratedRegex(@"<table\b.*?</table\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlTable();
    [GeneratedRegex(@"<tr\b.*?(?=<tr\b|</table\s*>|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlRow();
    [GeneratedRegex(@"<t[dh]\b[^>]*>(.*?)(?=<t[dh]\b|</tr\s*>|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlCell();
    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex Tag();

    /// <summary>Every table of a web page (innermost tables only, so a layout table around them is not read twice).</summary>
    private static List<List<List<string>>> Html(string html)
    {
        html = Regex.Replace(html, @"<(script|style)\b.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var tables = new List<List<List<string>>>();
        foreach (Match t in HtmlTable().Matches(html))
        {
            var body = t.Value;
            // An outer table holding other tables: its inner tables are read instead.
            var inner = body.IndexOf("<table", 6, StringComparison.OrdinalIgnoreCase);
            if (inner > 0) { tables.AddRange(Html(body[inner..])); continue; }
            var rows = new List<List<string>>();
            foreach (Match tr in HtmlRow().Matches(body))
            {
                var cells = HtmlCell().Matches(tr.Value)
                    .Select(c => TextNorm.CollapseSpaces(WebUtility.HtmlDecode(Tag().Replace(Regex.Replace(c.Groups[1].Value, @"<br\s*/?>", " ", RegexOptions.IgnoreCase), " "))).Trim())
                    .ToList();
                if (cells.Any(c => c.Length > 0)) rows.Add(cells);
            }
            tables.Add(rows);
        }
        return tables;
    }
}
