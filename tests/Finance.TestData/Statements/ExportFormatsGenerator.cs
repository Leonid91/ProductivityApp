using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// The same five invented operations (a Canadian chequing account, March 2026) in every kind of file the import takes:
/// Excel (.xlsx with real date cells), OpenDocument (.ods, .odt), Word (.docx), a web page saved as ".xls", OFX / QFX /
/// QBO (old SGML and XML) and QIF (month-first and day-first dates). The same operations must come out of each.
/// </summary>
public static class ExportFormatsGenerator
{
    public static readonly (DateOnly Date, string Label, decimal Amount)[] Ops =
    [
        (new DateOnly(2026, 3, 2), "PAYROLL DEPOSIT EXAMPLE CORP", 2140.55m),
        (new DateOnly(2026, 3, 3), "TIM HORTONS #2345 TORONTO ON", -4.85m),
        (new DateOnly(2026, 3, 5), "SOBEYS #4521 BRAMPTON ON", -86.12m),
        (new DateOnly(2026, 3, 9), "HYDRO ONE NETWORKS", -131.06m),
        (new DateOnly(2026, 3, 12), "E-TRANSFER RECEIVED A SAMPLE", 120.00m),
    ];

    public const decimal Opening = 1000.00m;

    private static string X(string s) => System.Security.SecurityElement.Escape(s);

    /// <summary>An entry of a fixed date: the same file, to the byte, every time it is made (the app knows a file by its bytes).</summary>
    private static ZipArchiveEntry Dated(ZipArchiveEntry entry)
    {
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return entry;
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, text) in entries)
            {
                using var w = new StreamWriter(Dated(zip.CreateEntry(name)).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        return ms.ToArray();
    }

    /// <summary>
    /// An Excel workbook: a title row, a header, dates as real date cells (serial numbers styled as dates), numbers as
    /// numbers, shown with <paramref name="moneyFormat"/> when given (a number format such as "#,##0.00 [$€-40C]").
    /// </summary>
    public static byte[] Xlsx(string? moneyFormat = null)
    {
        var money = moneyFormat is null ? "" : " s=\"2\"";
        var rows = new StringBuilder();
        rows.Append("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Account activity - Example Bank</t></is></c></row>");
        rows.Append("<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Date</t></is></c><c r=\"B2\" t=\"inlineStr\"><is><t>Description</t></is></c><c r=\"C2\" t=\"inlineStr\"><is><t>Withdrawals</t></is></c><c r=\"D2\" t=\"inlineStr\"><is><t>Deposits</t></is></c><c r=\"E2\" t=\"inlineStr\"><is><t>Balance</t></is></c></row>");
        var bal = Opening;
        var r = 3;
        foreach (var (date, label, amount) in Ops)
        {
            bal += amount;
            var serial = date.ToDateTime(TimeOnly.MinValue).ToOADate().ToString(CultureInfo.InvariantCulture);
            rows.Append(CultureInfo.InvariantCulture, $"<row r=\"{r}\"><c r=\"A{r}\" s=\"1\"><v>{serial}</v></c><c r=\"B{r}\" t=\"inlineStr\"><is><t>{X(label)}</t></is></c>");
            if (amount < 0) rows.Append(CultureInfo.InvariantCulture, $"<c r=\"C{r}\"{money}><v>{(-amount).ToString(CultureInfo.InvariantCulture)}</v></c>");
            else rows.Append(CultureInfo.InvariantCulture, $"<c r=\"D{r}\"{money}><v>{amount.ToString(CultureInfo.InvariantCulture)}</v></c>");
            rows.Append(CultureInfo.InvariantCulture, $"<c r=\"E{r}\"{money}><v>{bal.ToString(CultureInfo.InvariantCulture)}</v></c></row>");
            r++;
        }
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return Zip(
            ("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>"""),
            ("_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>"""),
            ("xl/workbook.xml", $"""<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="{ns}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Activity" sheetId="1" r:id="rId1"/></sheets></workbook>"""),
            ("xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>"""),
            ("xl/styles.xml", $"""<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="{ns}"><numFmts count="1"><numFmt numFmtId="164" formatCode="{X(moneyFormat ?? "0.00")}"/></numFmts><fonts count="1"><font/></fonts><fills count="1"><fill/></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/><xf numFmtId="164" applyNumberFormat="1"/></cellXfs></styleSheet>"""),
            ("xl/worksheets/sheet1.xml", $"""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="{ns}"><sheetData>{rows}</sheetData></worksheet>"""));
    }

    /// <summary>The cells of the operations table: a header, then one row per operation with its running balance.</summary>
    private static List<string[]> Cells()
    {
        var list = new List<string[]> { new[] { "Date", "Description", "Withdrawals", "Deposits", "Balance" } };
        var bal = Opening;
        foreach (var (date, label, amount) in Ops)
        {
            bal += amount;
            list.Add([date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), label, amount < 0 ? (-amount).ToString("0.00", CultureInfo.InvariantCulture) : "",
                amount > 0 ? amount.ToString("0.00", CultureInfo.InvariantCulture) : "", bal.ToString("#,##0.00", CultureInfo.InvariantCulture)]);
        }
        return list;
    }

    /// <summary>The operations table as an OpenDocument spreadsheet or text document; its numbers are money in <paramref name="currency"/> when given.</summary>
    public static byte[] OpenDocument(bool text, string? currency = null)
    {
        var table = new StringBuilder("<table:table table:name=\"Activity\">");
        foreach (var row in Cells())
        {
            table.Append("<table:table-row>");
            foreach (var c in row)
            {
                if (decimal.TryParse(c.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && !c.Contains('-'))
                {
                    var value = v.ToString(CultureInfo.InvariantCulture);
                    table.Append(currency is null
                        ? $"<table:table-cell office:value-type=\"float\" office:value=\"{value}\"><text:p>{c}</text:p></table:table-cell>"
                        : $"<table:table-cell office:value-type=\"currency\" office:currency=\"{currency}\" office:value=\"{value}\"><text:p>{c}</text:p></table:table-cell>");
                }
                else if (DateOnly.TryParseExact(c, "yyyy-MM-dd", out _))
                    table.Append(CultureInfo.InvariantCulture, $"<table:table-cell office:value-type=\"date\" office:date-value=\"{c}\"><text:p>{c}</text:p></table:table-cell>");
                else table.Append(CultureInfo.InvariantCulture, $"<table:table-cell office:value-type=\"string\"><text:p>{X(c)}</text:p></table:table-cell>");
            }
            // Empty cells repeated to the end of a sheet, as spreadsheet programs write them.
            table.Append("<table:table-cell table:number-columns-repeated=\"1019\"/></table:table-row>");
        }
        table.Append("<table:table-row table:number-rows-repeated=\"1048000\"><table:table-cell table:number-columns-repeated=\"1024\"/></table:table-row></table:table>");
        var body = text ? $"<office:text><text:p>Example Bank statement</text:p>{table}</office:text>" : $"<office:spreadsheet>{table}</office:spreadsheet>";
        var mime = text ? "application/vnd.oasis.opendocument.text" : "application/vnd.oasis.opendocument.spreadsheet";
        return Zip(("mimetype", mime),
            ("content.xml", $"""<?xml version="1.0" encoding="UTF-8"?><office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"><office:body>{body}</office:body></office:document-content>"""));
    }

    public static byte[] Docx()
    {
        var t = new StringBuilder("<w:tbl>");
        foreach (var row in Cells()) t.Append("<w:tr>" + string.Concat(row.Select(c => $"<w:tc><w:p><w:r><w:t>{X(c)}</w:t></w:r></w:p></w:tc>")) + "</w:tr>");
        t.Append("</w:tbl>");
        return Zip(("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>"""),
            ("word/document.xml", $"""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Example Bank</w:t></w:r></w:p>{t}</w:body></w:document>"""));
    }

    /// <summary>A web page saved as ".xls", as some banks' "Excel" download is: a summary table, then the operations.</summary>
    public static byte[] HtmlXls()
    {
        var sb = new StringBuilder("<html><head><style>td{mso-number-format:\"\\@\"}</style></head><body><table><tr><td>Account</td><td>Example Bank 1234</td></tr></table><table>");
        foreach (var row in Cells()) sb.Append("<tr>" + string.Concat(row.Select(c => $"<td>{System.Net.WebUtility.HtmlEncode(c)}</td>")) + "</tr>");
        sb.Append("</table></body></html>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static byte[] Ofx(bool xml, bool card)
    {
        var trns = string.Concat(Ops.Select((o, i) => xml
            ? $"<STMTTRN><TRNTYPE>{(o.Amount < 0 ? "DEBIT" : "CREDIT")}</TRNTYPE><DTPOSTED>{o.Date:yyyyMMdd}120000.000[-5:EST]</DTPOSTED><TRNAMT>{o.Amount.ToString(CultureInfo.InvariantCulture)}</TRNAMT><FITID>{i}</FITID><NAME>{X(o.Label)}</NAME></STMTTRN>"
            : $"<STMTTRN>\r\n<TRNTYPE>{(o.Amount < 0 ? "DEBIT" : "CREDIT")}\r\n<DTPOSTED>{o.Date:yyyyMMdd}\r\n<TRNAMT>{o.Amount.ToString(CultureInfo.InvariantCulture)}\r\n<FITID>{i}\r\n<NAME>{o.Label}\r\n</STMTTRN>\r\n"));
        var ledger = (Opening + Ops.Sum(o => o.Amount)).ToString(CultureInfo.InvariantCulture);
        var from = card ? "<CCACCTFROM><ACCTID>4500000000001234</ACCTID></CCACCTFROM>" : "<BANKACCTFROM><BANKID>000000001</BANKID><ACCTID>00000123456</ACCTID><ACCTTYPE>CHECKING</ACCTTYPE></BANKACCTFROM>";
        var body = $"<OFX><SIGNONMSGSRSV1><SONRS><FI><ORG>Example Bank</ORG></FI></SONRS></SIGNONMSGSRSV1><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>CAD</CURDEF>{from}<BANKTRANLIST><DTSTART>20260301<DTEND>20260331{trns}</BANKTRANLIST><LEDGERBAL><BALAMT>{ledger}<DTASOF>20260331</LEDGERBAL></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        if (xml) body = body.Replace("<DTSTART>20260301<DTEND>20260331", "<DTSTART>20260301</DTSTART><DTEND>20260331</DTEND>").Replace("<BALAMT>" + ledger + "<DTASOF>20260331", "<BALAMT>" + ledger + "</BALAMT><DTASOF>20260331</DTASOF>");
        var head = xml ? "<?xml version=\"1.0\" encoding=\"UTF-8\"?><?OFX OFXHEADER=\"200\" VERSION=\"220\"?>" : "OFXHEADER:100\r\nDATA:OFXSGML\r\nVERSION:102\r\nCHARSET:1252\r\n\r\n";
        return Encoding.UTF8.GetBytes(head + body);
    }

    /// <summary>QIF with month/day'yy dates and grouped amounts, as US software writes it.</summary>
    public static byte[] QifMonthFirst() =>
        Encoding.ASCII.GetBytes("!Type:Bank\r\n" + string.Concat(Ops.Select(o => $"D{o.Date.Month}/{o.Date.Day}'{o.Date:yy}\r\nT{o.Amount.ToString("#,##0.00", CultureInfo.InvariantCulture)}\r\nP{o.Label}\r\n^\r\n")));

    /// <summary>A card's QIF with day/month/year dates, the label in the memo, and one more line (a fee on 13 March).</summary>
    public static byte[] QifDayFirstCard() =>
        Encoding.ASCII.GetBytes("!Type:CCard\n" + string.Concat(Ops.Select(o => $"D{o.Date:dd/MM/yyyy}\nT{o.Amount.ToString(CultureInfo.InvariantCulture)}\nM{o.Label}\n^\n")) + "D13/03/2026\nT-1.00\nPFEE\n^\n");

    /// <summary>The operations in every office and web table format, by the name of the file (theories take the name).</summary>
    public static readonly IReadOnlyDictionary<string, Func<byte[]>> OfficeFiles = new Dictionary<string, Func<byte[]>>
    {
        ["activity.xlsx"] = () => Xlsx(),
        ["activity.ods"] = () => OpenDocument(text: false),
        ["statement.odt"] = () => OpenDocument(text: true),
        ["statement.docx"] = Docx,
        ["activity.xls"] = HtmlXls,
        ["activity.html"] = HtmlXls,
    };
}
