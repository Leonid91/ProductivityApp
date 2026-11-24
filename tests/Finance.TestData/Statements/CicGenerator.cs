using System.Globalization;
using System.IO.Compression;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Fictitious statements in the layouts of the CIC (a French bank, on the same online banking as the Crédit Mutuel):
/// <list type="bullet">
/// <item>the monthly PDF "Relevé et informations bancaires": every account of the person in one file (the current
/// account, then the Livret A), each under "C/C EUROCOMPTE ... N° ... en euros" with "Date | Date valeur | Opération |
/// Débit EUROS | Crédit EUROS", the opening balance as the first line ("SOLDE CREDITEUR AU ..."), the closing one as
/// the last, "Total des mouvements" between them, amounts written "1.234,56", a card payment on three lines (the
/// payment, the shop, the card), several pages;</item>
/// <item>the CSV export ("Date;Date de valeur;Débit;Crédit;Libellé;Solde"): ISO-8859-15, dates day/month/year, debits
/// written negative in the Débit column, the running balance, the oldest operation first;</item>
/// <item>the Excel export: a sheet of the accounts ("Compte | R.I.B. | Solde | Dev"), then a sheet per account with
/// lines of title above "Date | Valeur | Libellé | Débit | Crédit | Solde" and the balance under the operations, dates
/// as date cells, amounts as numbers;</item>
/// <item>the Money (OFX) and Quicken (QIF) exports.</item>
/// </list>
/// French cards are debit cards: card payments come straight out of the current account. The person, the branch, the
/// account numbers, the employer and the amounts are invented.
/// </summary>
public static class CicGenerator
{
    /// <param name="Detail">The lines printed under the operation in the PDF (the shop, the card); the CSV writes them
    /// after it on the same line.</param>
    public sealed record Line(DateOnly Date, DateOnly Value, string Label, string[] Detail, decimal Amount)
    {
        public string Full => string.Join(' ', new[] { Label }.Concat(Detail));
    }

    public sealed record Account(string Title, string Number, decimal Opening, List<Line> Lines)
    {
        public decimal Closing => Opening + Lines.Sum(l => l.Amount);
        public decimal Debits => -Lines.Where(l => l.Amount < 0).Sum(l => l.Amount);
        public decimal Credits => Lines.Where(l => l.Amount > 0).Sum(l => l.Amount);
    }

    public sealed record Statement(DateOnly Start, DateOnly End, int Number, List<Account> Accounts);

    private static readonly (string Town, string Shop, decimal Min, decimal Max)[] Card =
    [
        ("PARIS", "CARREFOUR CITY", 6.40m, 74.90m),
        ("PARIS", "BOULANGERIE DU MARCHE", 1.90m, 11.60m),
        ("PARIS 11", "FRANPRIX EXEMPLE", 4.15m, 39.80m),
        ("PARIS", "PHARMACIE DE LA GARE", 5.20m, 31.45m),
        ("PARIS", "SNCF CONNECT", 22.00m, 118.00m),
        ("NANTERRE", "STATION TOTALENERGIES", 38.00m, 71.30m),
        ("PARIS", "CAFE DE L'EXEMPLE", 3.40m, 26.00m),
        ("VILLE EXEMPLE", "FNAC EXEMPLE", 14.99m, 89.99m),
    ];

    private const string CardNumber = "CARTE 0742XXXX5521";

    public static Statement Sample(int year = 2025, int month = 10, int seed = 11)
    {
        var rng = new Random(seed);
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        DateOnly Day(int d) => start.AddDays(Math.Min(d, end.Day) - 1);
        Line Op(int d, string label, decimal amount, params string[] detail) => new(Day(d), Day(d), label, detail, amount);
        var current = new List<Line>
        {
            Op(1, "VIR SEPA SALAIRE EXEMPLE SAS", 2380.00m, "SALAIRE " + start.ToString("MMMM yyyy", Fr).ToUpperInvariant()),
            Op(4, "PRLV SEPA EDF CLIENTS PARTICULIERS", -64.00m, "REF MANDAT EX00001234"),
            Op(5, "VIR SEPA LOYER SCI EXEMPLE", -760.00m),
            Op(7, "RETRAIT DAB " + Day(7).ToString("ddMM", Fr) + " PARIS 11", -40.00m, CardNumber),
            Op(9, "PRLV SEPA FREE MOBILE", -15.99m),
            Op(12, "VIR LIVRET A", -150.00m),
            Op(14, "VIR INST M PAUL EXEMPLE", 28.50m, "REMBOURSEMENT CINEMA"),
            Op(19, "CHQ. 7654321", -52.00m),
            Op(22, "PRLV SEPA MUTUELLE EXEMPLE", -38.70m),
            Op(27, "F COTIS EUROCOMPTE DUO CONFORT", -11.50m),
        };
        for (var i = 0; i < 24; i++)
        {
            var c = Card[rng.Next(Card.Length)];
            var amount = Math.Round(c.Min + (decimal)rng.NextDouble() * (c.Max - c.Min), 2);
            var paid = Day(1 + rng.Next(end.Day - 1));
            // Booked a day or two after the payment; the label keeps the day of the payment.
            var booked = paid.AddDays(1 + rng.Next(2));
            if (booked > end) booked = end;
            current.Add(new Line(booked, booked, $"PAIEMENT CB {paid:ddMM} {c.Town}", [c.Shop, CardNumber], -amount));
        }
        current = [.. current.OrderBy(l => l.Date)];
        var livret = new List<Line> { new(Day(12), Day(12), "VIR LIVRET A", ["DE C/C EUROCOMPTE DUO CONFORT"], 150.00m) };
        return new Statement(start, end, month, [
            new Account("C/C EUROCOMPTE DUO CONFORT", "00020123401", 845.32m, current),
            new Account("LIVRET A", "00020123402", 4210.00m, livret),
        ]);
    }

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>"1.234,56", as the CIC prints amounts.</summary>
    private static string Money(decimal v)
    {
        var s = Math.Abs(v).ToString("#,##0.00", CultureInfo.InvariantCulture);
        return s.Replace(",", "\u0001").Replace(".", ",").Replace("\u0001", ".");
    }

    public static byte[] Pdf(Statement s)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.MarginVertical(32);
            page.MarginHorizontal(34);
            page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(8.5f));
            page.Header().Row(r =>
            {
                r.RelativeItem().Column(h =>
                {
                    h.Item().Text("CIC").FontSize(18).Bold();
                    h.Item().Text("CIC PARIS EXEMPLE");
                    h.Item().Text("12 BOULEVARD DE L'EXEMPLE 75011 PARIS");
                    h.Item().Text("Tél : 01 00 00 00 00");
                });
                r.AutoItem().AlignRight().Column(h =>
                {
                    h.Item().AlignRight().Text("RELEVÉ ET INFORMATIONS BANCAIRES").FontSize(11).Bold();
                    h.Item().AlignRight().Text($"Du {s.Start:dd/MM/yyyy} au {s.End:dd/MM/yyyy}");
                    h.Item().AlignRight().Text(t =>
                    {
                        t.Span($"Relevé N° {s.Number} - Page ");
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
            });
            page.Content().Column(col =>
            {
                col.Item().PaddingTop(10).AlignRight().Column(a =>
                {
                    a.Item().Text("M ALEX SAMPLE");
                    a.Item().Text("8 RUE DE L'EXEMPLE");
                    a.Item().Text("75011 PARIS");
                });
                foreach (var a in s.Accounts)
                {
                    col.Item().PaddingTop(16).Text($"{a.Title} N° {a.Number} en euros").Bold();
                    col.Item().Text($"M ALEX SAMPLE - IBAN : FR76 3006 6100 0100 {a.Number[..4]} {a.Number[4..8]} {a.Number[8..]}0 66 - BIC : CMCIFRPP").FontSize(7.5f);
                    col.Item().PaddingTop(4).Table(t =>
                    {
                        t.ColumnsDefinition(cd => { cd.ConstantColumn(52); cd.ConstantColumn(52); cd.RelativeColumn(); cd.ConstantColumn(72); cd.ConstantColumn(72); });
                        static IContainer Head(IContainer x) => x.Background("#E8E8E8").Border(0.5f).Padding(2);
                        t.Header(hd =>
                        {
                            hd.Cell().Element(Head).Text("Date").Bold();
                            hd.Cell().Element(Head).Text("Date valeur").Bold();
                            hd.Cell().Element(Head).Text("Opération").Bold();
                            hd.Cell().Element(Head).AlignRight().Text("Débit EUROS").Bold();
                            hd.Cell().Element(Head).AlignRight().Text("Crédit EUROS").Bold();
                        });
                        static IContainer Cell(IContainer x) => x.BorderLeft(0.5f).BorderRight(0.5f).PaddingHorizontal(2).PaddingVertical(1.5f);
                        void Balance(DateOnly day, decimal v)
                        {
                            t.Cell().Element(Cell).Text("");
                            t.Cell().Element(Cell).Text("");
                            t.Cell().Element(Cell).Text($"SOLDE {(v < 0 ? "DEBITEUR" : "CREDITEUR")} AU {day:dd/MM/yyyy}").Bold();
                            t.Cell().Element(Cell).AlignRight().Text(v < 0 ? Money(v) : "").Bold();
                            t.Cell().Element(Cell).AlignRight().Text(v >= 0 ? Money(v) : "").Bold();
                        }
                        Balance(s.Start.AddDays(-1), a.Opening);
                        foreach (var l in a.Lines)
                        {
                            t.Cell().Element(Cell).Text(l.Date.ToString("dd/MM/yyyy", Fr));
                            t.Cell().Element(Cell).Text(l.Value.ToString("dd/MM/yyyy", Fr));
                            t.Cell().Element(Cell).Column(d =>
                            {
                                d.Item().Text(l.Label);
                                foreach (var more in l.Detail) d.Item().Text(more);
                            });
                            t.Cell().Element(Cell).AlignRight().Text(l.Amount < 0 ? Money(l.Amount) : "");
                            t.Cell().Element(Cell).AlignRight().Text(l.Amount > 0 ? Money(l.Amount) : "");
                        }
                        t.Cell().Element(Cell).BorderTop(0.5f).Text("");
                        t.Cell().Element(Cell).BorderTop(0.5f).Text("");
                        t.Cell().Element(Cell).BorderTop(0.5f).Text("Total des mouvements").Bold();
                        t.Cell().Element(Cell).BorderTop(0.5f).AlignRight().Text(Money(a.Debits)).Bold();
                        t.Cell().Element(Cell).BorderTop(0.5f).AlignRight().Text(Money(a.Credits)).Bold();
                        Balance(s.End, a.Closing);
                    });
                    col.Item().PaddingTop(3).Text("Sous réserve des opérations en cours. Pensez à vérifier votre relevé.").FontSize(7);
                }
            });
            page.Footer().Text("CIC - Crédit Industriel et Commercial Exemple - Société anonyme - RCS Paris 000 000 000 - ORIAS 00 000 000").FontSize(6);
        })).WithMetadata(new DocumentMetadata { CreationDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), ModifiedDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) })
        .GeneratePdf();
    }

    /// <summary>The CSV export: ISO-8859-15, ";", dates day/month/year, debits negative in Débit, the running balance.</summary>
    public static byte[] Csv(Account a)
    {
        static string N(decimal v) => v.ToString("0.00", Fr);
        var sb = new StringBuilder("Date;Date de valeur;Débit;Crédit;Libellé;Solde\r\n");
        var balance = a.Opening;
        foreach (var l in a.Lines)
        {
            balance += l.Amount;
            sb.Append(CultureInfo.InvariantCulture, $"{l.Date:dd/MM/yyyy};{l.Value:dd/MM/yyyy};{(l.Amount < 0 ? N(l.Amount) : "")};{(l.Amount > 0 ? N(l.Amount) : "")};{l.Full};{N(balance)}\r\n");
        }
        return Latin9().GetBytes(sb.ToString());
    }

    /// <summary>An entry of a fixed date: the same file, to the byte, every time it is made (the app knows a file by its bytes).</summary>
    private static ZipArchiveEntry Dated(ZipArchiveEntry entry)
    {
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return entry;
    }

    /// <summary>The Excel export: the accounts, then a sheet per account (title lines, the operations, the balance).</summary>
    public static byte[] Xlsx(Statement s)
    {
        static string X(string v) => System.Security.SecurityElement.Escape(v)!;
        static string Str(string cell, string v) => $"<c r=\"{cell}\" t=\"inlineStr\"><is><t>{X(v)}</t></is></c>";
        static string Num(string cell, decimal v, int style = 0) => $"<c r=\"{cell}\"{(style > 0 ? $" s=\"{style}\"" : "")}><v>{v.ToString(CultureInfo.InvariantCulture)}</v></c>";
        static string Date(string cell, DateOnly d) => $"<c r=\"{cell}\" s=\"1\"><v>{d.ToDateTime(TimeOnly.MinValue).ToOADate().ToString(CultureInfo.InvariantCulture)}</v></c>";
        var sheets = new List<(string Name, string Rows)>();
        var accounts = new StringBuilder();
        accounts.Append($"<row r=\"1\">{Str("A1", "M ALEX SAMPLE")}</row>");
        accounts.Append($"<row r=\"2\">{Str("A2", $"Situation au {s.End:dd/MM/yyyy}")}</row>");
        accounts.Append($"<row r=\"3\">{Str("A3", "Compte")}{Str("B3", "R.I.B.")}{Str("C3", "Solde")}{Str("D3", "Dev")}</row>");
        for (var i = 0; i < s.Accounts.Count; i++)
        {
            var a = s.Accounts[i];
            var r = 4 + i;
            accounts.Append($"<row r=\"{r}\">{Str($"A{r}", a.Title)}{Str($"B{r}", $"30066 10001 {a.Number} 66")}{Num($"C{r}", a.Closing, 2)}{Str($"D{r}", "EUR")}</row>");
        }
        sheets.Add(("Comptes", accounts.ToString()));
        foreach (var a in s.Accounts)
        {
            var rows = new StringBuilder();
            rows.Append($"<row r=\"1\">{Str("A1", $"{a.Title} N° {a.Number}")}</row>");
            rows.Append($"<row r=\"2\">{Str("A2", "M ALEX SAMPLE")}</row>");
            rows.Append($"<row r=\"3\">{Str("A3", $"Opérations du {s.Start:dd/MM/yyyy} au {s.End:dd/MM/yyyy}")}</row>");
            rows.Append($"<row r=\"5\">{Str("A5", "Date")}{Str("B5", "Valeur")}{Str("C5", "Libellé")}{Str("D5", "Débit")}{Str("E5", "Crédit")}{Str("F5", "Solde")}</row>");
            var r = 6;
            var balance = a.Opening;
            foreach (var l in a.Lines)
            {
                balance += l.Amount;
                rows.Append($"<row r=\"{r}\">{Date($"A{r}", l.Date)}{Date($"B{r}", l.Value)}{Str($"C{r}", l.Full)}");
                rows.Append(l.Amount < 0 ? Num($"D{r}", l.Amount, 2) : Num($"E{r}", l.Amount, 2));
                rows.Append($"{Num($"F{r}", balance, 2)}</row>");
                r++;
            }
            r++;
            rows.Append($"<row r=\"{r}\">{Str($"C{r}", $"Solde au {s.End:dd/MM/yyyy}")}{Num($"F{r}", a.Closing, 2)}</row>");
            sheets.Add((a.Title.Length > 31 ? a.Title[..31] : a.Title, rows.ToString()));
        }
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var entries = new List<(string, string)>
        {
            ("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>"""
                + string.Concat(sheets.Select((_, i) => $"""<Override PartName="/xl/worksheets/sheet{i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""")) + "</Types>"),
            ("_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>"""),
            ("xl/workbook.xml", $"""<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="{ns}" xmlns:r="{rel}"><sheets>""" + string.Concat(sheets.Select((sh, i) => $"""<sheet name="{X(sh.Name)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""")) + "</sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">"""
                + string.Concat(sheets.Select((_, i) => $"""<Relationship Id="rId{i + 1}" Type="{rel}/worksheet" Target="worksheets/sheet{i + 1}.xml"/>"""))
                + $"""<Relationship Id="rId{sheets.Count + 1}" Type="{rel}/styles" Target="styles.xml"/></Relationships>"""),
            ("xl/styles.xml", $"""<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="{ns}"><numFmts count="1"><numFmt numFmtId="164" formatCode="#,##0.00 [$€-40C]"/></numFmts><fonts count="1"><font/></fonts><fills count="1"><fill/></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/><xf numFmtId="164" applyNumberFormat="1"/></cellXfs></styleSheet>"""),
        };
        for (var i = 0; i < sheets.Count; i++)
            entries.Add(($"xl/worksheets/sheet{i + 1}.xml", $"""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="{ns}"><sheetData>{sheets[i].Rows}</sheetData></worksheet>"""));
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, text) in entries)
            {
                using var w = new StreamWriter(Dated(zip.CreateEntry(name)).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        return ms.ToArray();
    }

    /// <summary>The Money export (OFX 1.0.2, SGML).</summary>
    public static byte[] Ofx(Statement s, Account a)
    {
        var sb = new StringBuilder("OFXHEADER:100\r\nDATA:OFXSGML\r\nVERSION:102\r\nSECURITY:NONE\r\nENCODING:USASCII\r\nCHARSET:1252\r\nCOMPRESSION:NONE\r\nOLDFILEUID:NONE\r\nNEWFILEUID:NONE\r\n\r\n");
        sb.Append("<OFX><SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>20251101<LANGUAGE>FRA</SONRS></SIGNONMSGSRSV1>\r\n");
        sb.Append($"<BANKMSGSRSV1><STMTTRNRS><TRNUID>00000000<STATUS><CODE>0<SEVERITY>INFO</STATUS><STMTRS><CURDEF>EUR<BANKACCTFROM><BANKID>30066<BRANCHID>10001<ACCTID>{a.Number}<ACCTTYPE>CHECKING</BANKACCTFROM>\r\n");
        sb.Append($"<BANKTRANLIST><DTSTART>{s.Start:yyyyMMdd}<DTEND>{s.End:yyyyMMdd}\r\n");
        var n = 1;
        foreach (var l in a.Lines)
            sb.Append($"<STMTTRN><TRNTYPE>{(l.Amount < 0 ? "DEBIT" : "CREDIT")}<DTPOSTED>{l.Date:yyyyMMdd}<TRNAMT>{l.Amount.ToString("0.00", CultureInfo.InvariantCulture)}<FITID>{s.Start:yyyyMM}{n++:0000}<NAME>{l.Label}{(l.Detail.Length == 0 ? "" : "<MEMO>" + string.Join(' ', l.Detail))}</STMTTRN>\r\n");
        sb.Append($"</BANKTRANLIST><LEDGERBAL><BALAMT>{a.Closing.ToString("0.00", CultureInfo.InvariantCulture)}<DTASOF>{s.End:yyyyMMdd}</LEDGERBAL></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>\r\n");
        return Encoding.ASCII.GetBytes(new string(sb.ToString().Normalize(NormalizationForm.FormD).Where(ch => ch < 128).ToArray()));
    }

    /// <summary>The Quicken export (QIF): dates day/month/year, amounts with a decimal comma.</summary>
    public static byte[] Qif(Account a)
    {
        var sb = new StringBuilder("!Type:Bank\r\n");
        foreach (var l in a.Lines)
            sb.Append($"D{l.Date:dd/MM/yyyy}\r\nT{l.Amount.ToString("0.00", Fr)}\r\nP{l.Full}\r\n^\r\n");
        return Latin9().GetBytes(sb.ToString());
    }

    private static Encoding Latin9()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("iso-8859-15");
    }
}
