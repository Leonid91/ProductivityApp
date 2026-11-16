using System.Globalization;
using System.IO.Compression;
using System.Text;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Pdf;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// Every kind of file the import takes, each made here with invented operations: Excel (.xlsx with real date cells,
/// .xls web-page exports), OpenDocument (.ods, .odt), Word (.docx), web pages, OFX / QFX / QBO, QIF, a picture of a
/// statement, and the statements and exports of a Scotiabank-style account. The same operations must come out of each.
/// </summary>
[Collection("categories")]
public class FormatTests
{
    private static readonly (DateOnly Date, string Label, decimal Amount)[] Ops =
    [
        (new DateOnly(2026, 3, 2), "PAYROLL DEPOSIT EXAMPLE CORP", 2140.55m),
        (new DateOnly(2026, 3, 3), "TIM HORTONS #2345 TORONTO ON", -4.85m),
        (new DateOnly(2026, 3, 5), "SOBEYS #4521 BRAMPTON ON", -86.12m),
        (new DateOnly(2026, 3, 9), "HYDRO ONE NETWORKS", -131.06m),
        (new DateOnly(2026, 3, 12), "E-TRANSFER RECEIVED A SAMPLE", 120.00m),
    ];

    private const decimal Opening = 1000.00m;

    private static void AssertOps(StatementParse p, bool balances = true)
    {
        Assert.Equal(Ops.Length, p.Rows.Count);
        for (var i = 0; i < Ops.Length; i++)
        {
            Assert.Equal(Ops[i].Date, p.Rows[i].Date);
            Assert.Equal(Ops[i].Amount, p.Rows[i].Amount);
            Assert.Contains(Ops[i].Label.Split(' ')[0], p.Rows[i].Label, StringComparison.OrdinalIgnoreCase);
        }
        if (balances) Assert.Equal(VerificationStatus.Verified, p.Status);
    }

    private static string X(string s) => System.Security.SecurityElement.Escape(s);

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, text) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        return ms.ToArray();
    }

    /// <summary>An Excel workbook: a title row, a header, dates as real date cells (serial numbers styled as dates), numbers as numbers.</summary>
    private static byte[] Xlsx()
    {
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
            if (amount < 0) rows.Append(CultureInfo.InvariantCulture, $"<c r=\"C{r}\"><v>{(-amount).ToString(CultureInfo.InvariantCulture)}</v></c>");
            else rows.Append(CultureInfo.InvariantCulture, $"<c r=\"D{r}\"><v>{amount.ToString(CultureInfo.InvariantCulture)}</v></c>");
            rows.Append(CultureInfo.InvariantCulture, $"<c r=\"E{r}\"><v>{bal.ToString(CultureInfo.InvariantCulture)}</v></c></row>");
            r++;
        }
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return Zip(
            ("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>"""),
            ("_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>"""),
            ("xl/workbook.xml", $"""<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="{ns}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Activity" sheetId="1" r:id="rId1"/></sheets></workbook>"""),
            ("xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>"""),
            ("xl/styles.xml", $"""<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="{ns}"><fonts count="1"><font/></fonts><fills count="1"><fill/></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/></cellXfs></styleSheet>"""),
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

    private static byte[] OpenDocument(bool text)
    {
        var table = new StringBuilder("<table:table table:name=\"Activity\">");
        foreach (var row in Cells())
        {
            table.Append("<table:table-row>");
            foreach (var c in row)
            {
                if (decimal.TryParse(c.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && !c.Contains('-'))
                    table.Append(CultureInfo.InvariantCulture, $"<table:table-cell office:value-type=\"float\" office:value=\"{v}\"><text:p>{c}</text:p></table:table-cell>");
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

    private static byte[] Docx()
    {
        var t = new StringBuilder("<w:tbl>");
        foreach (var row in Cells()) t.Append("<w:tr>" + string.Concat(row.Select(c => $"<w:tc><w:p><w:r><w:t>{X(c)}</w:t></w:r></w:p></w:tc>")) + "</w:tr>");
        t.Append("</w:tbl>");
        return Zip(("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>"""),
            ("word/document.xml", $"""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Example Bank</w:t></w:r></w:p>{t}</w:body></w:document>"""));
    }

    /// <summary>A web page saved as ".xls", as some banks' "Excel" download is: a summary table, then the operations.</summary>
    private static byte[] HtmlXls()
    {
        var sb = new StringBuilder("<html><head><style>td{mso-number-format:\"\\@\"}</style></head><body><table><tr><td>Account</td><td>Example Bank 1234</td></tr></table><table>");
        foreach (var row in Cells()) sb.Append("<tr>" + string.Concat(row.Select(c => $"<td>{System.Net.WebUtility.HtmlEncode(c)}</td>")) + "</tr>");
        sb.Append("</table></body></html>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] Ofx(bool xml, bool card)
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

    private static async Task<StatementParse> Read(TempStore s, string name, byte[] bytes) => (await s.Imports.ParseAsync(name, bytes)).Parse;

    [Fact]
    public async Task Excel_OpenDocument_Word_and_web_page_tables_give_the_same_operations()
    {
        using var s = new TempStore();
        AssertOps(await Read(s, "activity.xlsx", Xlsx()));
        AssertOps(await Read(s, "activity.ods", OpenDocument(text: false)));
        AssertOps(await Read(s, "statement.odt", OpenDocument(text: true)));
        AssertOps(await Read(s, "statement.docx", Docx()));
        AssertOps(await Read(s, "activity.xls", HtmlXls()));
        AssertOps(await Read(s, "activity.html", HtmlXls()));
        Assert.Equal("Excel", (await Read(s, "activity.xlsx", Xlsx())).Method);
    }

    [Theory]
    [InlineData("activity.ofx", false, false)]
    [InlineData("activity.qfx", false, true)]
    [InlineData("activity.qbo", true, false)]
    public async Task Ofx_qfx_and_qbo_files_old_and_xml_give_the_operations_and_the_closing_balance(string name, bool xml, bool card)
    {
        using var s = new TempStore();
        var p = await Read(s, name, Ofx(xml, card));
        AssertOps(p, balances: false);
        Assert.Equal("OFX", p.Method);
        Assert.Equal(Opening + Ops.Sum(o => o.Amount), p.ClosingBalance);
        Assert.Equal(Opening, p.OpeningBalance);
        Assert.Equal(card ? AccountKind.CreditCard : AccountKind.Bank, p.Kind);
        Assert.Equal("1234", card ? p.Last4 : "1234");
    }

    [Fact]
    public async Task Qif_files_give_the_operations_whatever_the_date_writing()
    {
        using var s = new TempStore();
        var us = "!Type:Bank\r\n" + string.Concat(Ops.Select(o => $"D{o.Date.Month}/{o.Date.Day}'{o.Date:yy}\r\nT{o.Amount.ToString("#,##0.00", CultureInfo.InvariantCulture)}\r\nP{o.Label}\r\n^\r\n"));
        AssertOps(await Read(s, "activity.qif", Encoding.ASCII.GetBytes(us)), balances: false);
        var dayFirst = "!Type:CCard\n" + string.Concat(Ops.Select(o => $"D{o.Date:dd/MM/yyyy}\nT{o.Amount.ToString(CultureInfo.InvariantCulture)}\nM{o.Label}\n^\n")) + "D13/03/2026\nT-1.00\nPFEE\n^\n";
        var p = await Read(s, "card.qif", Encoding.ASCII.GetBytes(dayFirst));
        Assert.Equal(Ops.Length + 1, p.Rows.Count);
        Assert.Equal(AccountKind.CreditCard, p.Kind);
        Assert.Equal(Ops[2].Date, p.Rows[2].Date);
    }

    [Fact]
    public async Task A_picture_of_a_statement_is_read_like_a_scan()
    {
        if (!PdfReader.OcrAvailable) return;
        using var s = new TempStore();
        var spec = StatementGenerator.Sample(StatementGenerator.Style.EnglishWithdrawalsDeposits, 2025, 10, 12, 21);
        var png = await PdfReader.RenderPagePngAsync(StatementGenerator.Render(spec), 0, 1700);
        var p = await Read(s, "statement photo.png", png);
        Assert.True(p.UsedOcr);
        Assert.Equal(VerificationStatus.Verified, p.Status);
        Assert.Equal(spec.Transactions.Count, p.Rows.Count);
        Assert.Equal(spec.Transactions.Sum(t => t.Amount), p.Rows.Sum(r => r.Amount));
    }

    [Fact]
    public async Task Unknown_files_are_refused_with_the_list_of_formats()
    {
        using var s = new TempStore();
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => s.Imports.ParseAsync("notes.bin", [1, 2, 3, 0, 5, 6, 7, 8, 9, 10, 11, 12, 13]));
        Assert.Contains("Excel", e.Message);
    }

    [Fact]
    public async Task Scotiabank_style_statements_and_exports_are_read_to_the_cent()
    {
        using var s = new TempStore();
        var chq = ScotiaGenerator.Chequing();
        var pdf = await Read(s, "chequing.pdf", ScotiaGenerator.ChequingPdf(chq));
        Assert.Equal(VerificationStatus.Verified, pdf.Status);
        Assert.Equal(AccountKind.Bank, pdf.Kind);
        Assert.Equal(chq.Lines.Count, pdf.Rows.Count);
        Assert.Equal((chq.Opening, chq.Closing), (pdf.OpeningBalance!.Value, pdf.ClosingBalance!.Value));
        Assert.Equal(-chq.Withdrawals, pdf.Rows.Where(r => r.Amount < 0).Sum(r => r.Amount));
        Assert.Equal(chq.Deposits, pdf.Rows.Where(r => r.Amount > 0).Sum(r => r.Amount));
        // The two lines of a description are one label: the kind of operation, then the shop.
        Assert.Contains(pdf.Rows, r => r.Label == "Point of sale purchase Tim Hortons #3126 Brampton ONCD");

        var newCsv = await Read(s, "Scotia new.csv", ScotiaGenerator.NewCsv(chq));
        Assert.Equal(VerificationStatus.Verified, newCsv.Status);
        Assert.Equal(pdf.Rows.Select(r => (r.Date, r.Amount)), newCsv.Rows.Select(r => (r.Date, r.Amount)));
        var oldCsv = await Read(s, "Scotia old.csv", ScotiaGenerator.OldCsv(chq));
        Assert.Equal(pdf.Rows.Select(r => (r.Date, r.Amount, r.Label)), oldCsv.Rows.Select(r => (r.Date, r.Amount, r.Label)));

        var card = ScotiaGenerator.VisaCard();
        var visa = await Read(s, "visa.pdf", ScotiaGenerator.CardPdf(card));
        Assert.Equal(VerificationStatus.Verified, visa.Status);
        Assert.Equal(AccountKind.CreditCard, visa.Kind);
        Assert.Equal(card.Lines.Count, visa.Rows.Count);
        Assert.Equal((card.Previous, card.New), (visa.OpeningBalance!.Value, visa.ClosingBalance!.Value));
        // Credits written "57.96-" are money back; the period runs over two months.
        Assert.Contains(visa.Rows, r => r.Amount == 57.96m && r.Date == new DateOnly(2026, 5, 14));
        Assert.Contains(visa.Rows, r => r.Amount == 70.00m && r.Date == new DateOnly(2026, 4, 25));
    }

    [Fact]
    public void A_new_bank_s_wording_gets_sensible_categories()
    {
        var c = new Classification.Classifier([]);
        Assert.Equal(Categories.Transfers, c.Classify("Withdrawal 1234567 MB-Email Money Trf", -88.32m).CategoryId);
        Assert.Equal("Interac e-Transfer", c.Classify("Deposit 76543210 MB-Email Money Trf", 100m).Brand);
        Assert.Equal("fees", c.Classify("Service charge MB-Email Money Trf", -1m).CategoryId);
        Assert.Equal("cash", c.Classify("ABM withdrawal Sample Mall 24-7 Sampletown ON", -60m).CategoryId);
        Assert.Equal(Categories.CardPayments, c.Classify("Credit Card/LOC payment", -95m).CategoryId);
        Assert.Equal(Categories.CardPayments, c.Classify("PAYMENT-THANK YOU SCOTIABANK TRANSIT", 57.96m).CategoryId);
        Assert.Equal("fees", c.Classify("ANNUAL FEE", -39m).CategoryId);
        // "Point of sale purchase" says nothing about the shop: two shops stay two merchants.
        var a = c.Classify("Point of sale purchase Example Furniture Brampton ON", -50m);
        var b = c.Classify("Point of sale purchase Example Garden Centre Brampton ON", -20m);
        Assert.NotEqual(a.MerchantKey, b.MerchantKey);
        Assert.Equal("Example Furniture", a.Brand);
    }
}
