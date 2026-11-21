using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Fictitious statements in every form Fortuneo (a French online bank) gives them in 2021-2026:
/// <list type="bullet">
/// <item>the monthly PDF "Relevé de Compte N° 8 - 2026" of the "COMPTE COURANT", "Arrêté au 31 AOÛT 2026": the table
/// "Date | Date de Valeur | Opération | Débit | Crédit", the operation's day without its year ("05/08"), its value date
/// with it ("05/08/2026"), "ANCIEN SOLDE CRÉDITEUR AU dd/mm/yyyy" first ("ANCIEN SOLDE" alone on the first statement,
/// "DÉBITEUR" when overdrawn), "TOTAL DES OPÉRATIONS DU RELEVÉ" then "NOUVEAU SOLDE CRÉDITEUR AU dd/mm/yyyy" last,
/// amounts "2 000,00", card payments "CARTE dd/mm SHOP TOWN", labels on two lines, several pages, the date it was issued
/// up the left margin; the month of a deferred debit card taken in one line;</item>
/// <item>the same for a "Relevé Livret +", whose account has no IBAN ("Compte 12345678 01");</item>
/// <item>the monthly PDF of a deferred debit card ("Relevé de Carte"), each operation, then what is taken from the
/// current account ("MONTANT DÉBITÉ LE dd/mm/yyyy");</item>
/// <item>the exports of the operations: CSV ("Date opération;Date valeur;libellé;Débit;Crédit;", ISO-8859-1, debits
/// negative in Débit), the same separated by tabs ("Libre"), Excel (a table in an .xls file), Quicken (QIF) and Money
/// (OFX); the newest operation first.</item>
/// </list>
/// The person, the account and card numbers, the employer and the amounts are invented.
/// </summary>
public static class FortuneoGenerator
{
    public sealed record Line(DateOnly Date, DateOnly Value, string Label, string? Detail, decimal Amount)
    {
        public string Full => Label + (Detail is { } d ? " " + d : "");
    }

    /// <param name="First">The account's first statement: "ANCIEN SOLDE" without a date.</param>
    /// <param name="Livret">A Livret +: titled so, its account without an IBAN.</param>
    public sealed record Statement(DateOnly Start, DateOnly End, int Number, decimal Opening, List<Line> Lines, bool First = false, bool Livret = false)
    {
        public decimal Closing => Opening + Lines.Sum(l => l.Amount);
        public decimal Debits => -Lines.Where(l => l.Amount < 0).Sum(l => l.Amount);
        public decimal Credits => Lines.Where(l => l.Amount > 0).Sum(l => l.Amount);
    }

    public sealed record Card(DateOnly Start, DateOnly End, int Number, List<Line> Lines)
    {
        /// <summary>What the month costs, taken from the current account at its end.</summary>
        public decimal Total => -Lines.Sum(l => l.Amount);
    }

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private static readonly (string Shop, decimal Min, decimal Max)[] Shops =
    [
        ("BOULANGERIE TEST LYON", 1.60m, 9.80m),
        ("CARREFOUR MARKET LYON 3", 12.40m, 96.30m),
        ("TCL LYON", 2.10m, 2.10m),
        ("PHARMACIE BELLECOUR LYON", 3.90m, 27.60m),
        ("CINE EXEMPLE VILLEURBANNE", 8.90m, 21.40m),
        ("STATION ESSO LYON", 41.00m, 72.50m),
    ];

    /// <summary>
    /// A month of the current account: <paramref name="cards"/> card payments (many make several pages);
    /// <paramref name="deferred"/>: the month of a deferred debit card taken in one line at its end.
    /// </summary>
    public static Statement Sample(int year = 2026, int month = 8, int seed = 9, int cards = 20, decimal opening = 2048.15m, bool first = false, Card? deferred = null)
    {
        var rng = new Random(seed);
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        DateOnly Day(int d) => start.AddDays(Math.Min(d, end.Day) - 1);
        Line Op(int d, string label, decimal amount, string? detail = null) => new(Day(d), Day(d), label, detail, amount);
        var lines = new List<Line>
        {
            Op(1, "VIR SEPA EXEMPLE SAS", 2210.00m, "SALAIRE " + start.ToString("MMMM yyyy", Fr).ToUpperInvariant()),
            Op(2, "PRLV SEPA ENGIE", -58.00m, "FR12ZZZ123456"),
            Op(4, "ECH PRET 0000123456", -412.37m),
            Op(6, "PRLV SEPA BOUYGUES TELECOM", -16.99m),
            Op(10, "VIR INST M CAMILLE EXEMPLE", 35.00m, "PART RESTAURANT"),
            Op(14, "VIREMENT VERS FORTUNEO LIVRET +", -150.00m),
            Op(20, "COTISATION CARTE GOLD MASTERCARD", -2.00m),
            Op(24, "VIR SEPA CAF DU RHONE", 1063.20m),
        };
        if (deferred is not null) lines.Add(Op(end.Day, $"CARTE X{deferred.Number:0000} DEBIT DIFFERE", -deferred.Total, $"RELEVE DU {deferred.Start:dd/MM} AU {deferred.End:dd/MM}"));
        for (var i = 0; i < cards; i++)
        {
            var s = Shops[rng.Next(Shops.Length)];
            var amount = s.Min == s.Max ? s.Min : Math.Round(s.Min + (decimal)rng.NextDouble() * (s.Max - s.Min), 2);
            var paid = Day(1 + rng.Next(end.Day - 2));
            var booked = paid.AddDays(1 + rng.Next(2));
            if (booked > end) booked = end;
            lines.Add(new Line(booked, booked, $"CARTE {paid.ToString("dd/MM", Fr)} {s.Shop}", null, -amount));
        }
        return new Statement(start, end, month, first ? 0m : opening, [.. lines.OrderBy(l => l.Date)], First: first);
    }

    /// <summary>A month of a Livret +: transfers in and out, the interest of the fortnights.</summary>
    public static Statement Livret(int year = 2026, int month = 8)
    {
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        return new Statement(start, end, month, 8310.40m,
        [
            new(start.AddDays(13), start.AddDays(15), "VIREMENT DEPUIS COMPTE COURANT", null, 150.00m),
            new(start.AddDays(21), start.AddDays(20), "VIREMENT VERS COMPTE COURANT", "PROJET VACANCES", -600.00m),
            new(end, end, "INTERETS", "LIVRET + " + month.ToString("00", CultureInfo.InvariantCulture) + "/" + year.ToString(CultureInfo.InvariantCulture), 12.37m),
        ], Livret: true);
    }

    /// <summary>A month of a deferred debit card: payments, then a refund.</summary>
    public static Card DeferredCard(int year = 2026, int month = 8, int seed = 14)
    {
        var rng = new Random(seed);
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var lines = new List<Line>();
        for (var i = 0; i < 14; i++)
        {
            var s = Shops[rng.Next(Shops.Length)];
            var amount = s.Min == s.Max ? s.Min : Math.Round(s.Min + (decimal)rng.NextDouble() * (s.Max - s.Min), 2);
            var paid = start.AddDays(rng.Next(end.Day - 1));
            lines.Add(new Line(paid, paid.AddDays(1), $"CARTE {paid.ToString("dd/MM", Fr)} {s.Shop}", null, -amount));
        }
        lines.Add(new Line(start.AddDays(17), start.AddDays(18), "AVOIR CARTE " + start.AddDays(16).ToString("dd/MM", Fr) + " DECATHLON LYON", null, 29.99m));
        return new Card(start, end, 4321, [.. lines.OrderBy(l => l.Date)]);
    }

    /// <summary>"2 000,00", as Fortuneo prints amounts.</summary>
    private static string Money(decimal v) => Math.Abs(v).ToString("#,##0.00", CultureInfo.InvariantCulture).Replace(",", " ").Replace(".", ",");

    private static void Layout(PageDescriptor page, string title, DateOnly end)
    {
        page.Size(PageSizes.A4);
        page.MarginVertical(30);
        page.MarginLeft(46);
        page.MarginRight(32);
        page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(8));
        page.Background().PaddingLeft(14).PaddingTop(300).Width(12).RotateLeft().Text($"Émis le {end.AddDays(1):dd/MM/yyyy}").FontSize(6);
        page.Header().Row(r =>
        {
            r.RelativeItem().Column(h =>
            {
                h.Item().Text("Fortuneo").FontSize(18).Bold();
                h.Item().Text("SITE INTERNET www.fortuneo.fr - SERVICE CLIENTS TEL 0 800 000 000").FontSize(6.5f);
            });
            r.AutoItem().AlignRight().Column(h =>
            {
                h.Item().AlignRight().Text(title).FontSize(11).Bold();
                h.Item().AlignRight().Text($"Arrêté au {end.ToString("d MMMM yyyy", Fr).ToUpper(Fr)}");
                h.Item().AlignRight().Text(t => { t.Span("Page "); t.CurrentPageNumber(); t.Span("/"); t.TotalPages(); });
            });
        });
        page.Footer().Text("Fortuneo est une marque commerciale exemple - SA au capital de 0 000 000 € - RCS Brest 000 000 000").FontSize(6);
    }

    /// <summary>The table of a statement: the balance before, the operations, the totals, the balance after.</summary>
    private static void Operations(ColumnDescriptor col, string opening, decimal openingValue, IReadOnlyList<Line> lines, string total, (decimal Debits, decimal Credits) totals, string closing, decimal closingValue, bool withValueDates = true)
    {
        col.Item().PaddingTop(6).Table(t =>
        {
            t.ColumnsDefinition(cd => { cd.ConstantColumn(40); cd.ConstantColumn(62); cd.RelativeColumn(); cd.ConstantColumn(66); cd.ConstantColumn(66); });
            static IContainer Head(IContainer x) => x.Background("#EEEEEE").PaddingVertical(2).PaddingHorizontal(2);
            t.Header(hd =>
            {
                hd.Cell().Element(Head).Text("Date").Bold();
                hd.Cell().Element(Head).Text("Date de Valeur").Bold();
                hd.Cell().Element(Head).Text("Opération").Bold();
                hd.Cell().Element(Head).AlignRight().Text("Débit").Bold();
                hd.Cell().Element(Head).AlignRight().Text("Crédit").Bold();
            });
            static IContainer Cell(IContainer x) => x.PaddingVertical(2).PaddingHorizontal(2);
            void Balance(string label, decimal v)
            {
                t.Cell().Element(Cell).Text("");
                t.Cell().Element(Cell).Text("");
                t.Cell().Element(Cell).Text(label).Bold();
                t.Cell().Element(Cell).AlignRight().Text(v < 0 ? Money(v) : "").Bold();
                t.Cell().Element(Cell).AlignRight().Text(v >= 0 ? Money(v) : "").Bold();
            }
            if (opening.Length > 0) Balance(opening, openingValue);
            foreach (var l in lines)
            {
                t.Cell().Element(Cell).Text(l.Date.ToString("dd/MM", Fr));
                t.Cell().Element(Cell).Text(withValueDates ? l.Value.ToString("dd/MM/yyyy", Fr) : "");
                t.Cell().Element(Cell).Column(d => { d.Item().Text(l.Label); if (l.Detail is { } more) d.Item().Text(more); });
                t.Cell().Element(Cell).AlignRight().Text(l.Amount < 0 ? Money(l.Amount) : "");
                t.Cell().Element(Cell).AlignRight().Text(l.Amount > 0 ? Money(l.Amount) : "");
            }
            t.Cell().Element(Cell).BorderTop(0.6f).Text("");
            t.Cell().Element(Cell).BorderTop(0.6f).Text("");
            t.Cell().Element(Cell).BorderTop(0.6f).Text(total).Bold();
            t.Cell().Element(Cell).BorderTop(0.6f).AlignRight().Text(Money(totals.Debits)).Bold();
            t.Cell().Element(Cell).BorderTop(0.6f).AlignRight().Text(Money(totals.Credits)).Bold();
            Balance(closing, closingValue);
        });
    }

    private static string Side(decimal v) => v < 0 ? "DÉBITEUR" : "CRÉDITEUR";

    public static byte[] Pdf(Statement s)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(c => c.Page(page =>
        {
            Layout(page, s.Livret ? $"Relevé Livret + N° {s.Number} - {s.End.Year}" : $"Relevé de Compte N° {s.Number} - {s.End.Year}", s.End);
            page.Content().Column(col =>
            {
                col.Item().PaddingTop(10).AlignRight().Column(a =>
                {
                    a.Item().Text("M ALEX SAMPLE");
                    a.Item().Text("8 RUE DE L'EXEMPLE");
                    a.Item().Text("69003 LYON");
                });
                if (s.Livret)
                {
                    col.Item().PaddingTop(10).Text("LIVRET +").Bold();
                    col.Item().Text("Compte 12345678 01");
                }
                else
                {
                    col.Item().PaddingTop(10).Text("COMPTE COURANT").Bold();
                    col.Item().Text("Titulaire(s) du compte : M ALEX SAMPLE");
                    col.Item().Text("NUMÉRO DE COMPTE 14806 00000 12345678901 23 - B IC FTNOFRP1XXX").FontSize(7);
                }
                var opening = s.First ? "ANCIEN SOLDE" : $"ANCIEN SOLDE {Side(s.Opening)} AU {s.Start.AddDays(-1):dd/MM/yyyy}";
                Operations(col, opening, s.Opening, s.Lines, "TOTAL DES OPÉRATIONS DU RELEVÉ", (s.Debits, s.Credits), $"NOUVEAU SOLDE {Side(s.Closing)} AU {s.End:dd/MM/yyyy}", s.Closing);
                col.Item().PaddingTop(8).Text("Total facturé ce mois : 2,00 €").FontSize(7);
            });
        })).WithMetadata(new DocumentMetadata { CreationDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), ModifiedDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) })
        .GeneratePdf();
    }

    /// <summary>The month of a deferred debit card: each operation, then what is taken from the current account.</summary>
    public static byte[] CardPdf(Card c)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var debits = -c.Lines.Where(l => l.Amount < 0).Sum(l => l.Amount);
        var credits = c.Lines.Where(l => l.Amount > 0).Sum(l => l.Amount);
        return Document.Create(d => d.Page(page =>
        {
            Layout(page, $"Relevé de Carte N° {c.End.Month} - {c.End.Year}", c.End);
            page.Content().Column(col =>
            {
                col.Item().PaddingTop(10).AlignRight().Text("M ALEX SAMPLE");
                col.Item().PaddingTop(10).Text($"CARTE GOLD MASTERCARD N° 5132 XXXX XXXX {c.Number:0000} - DÉBIT DIFFÉRÉ").Bold();
                col.Item().Text("Compte débité : COMPTE COURANT 14806 00000 12345678901 23").FontSize(7);
                Operations(col, "", 0, c.Lines, "TOTAL DES OPÉRATIONS DU RELEVÉ", (debits, credits), $"MONTANT DÉBITÉ LE {c.End:dd/MM/yyyy}", -c.Total);
            });
        })).WithMetadata(new DocumentMetadata { CreationDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), ModifiedDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) })
        .GeneratePdf();
    }

    private static string FrAmount(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture).Replace(".", ",");

    /// <summary>The CSV export: ISO-8859-1, debits negative in Débit, credits in Crédit, the newest operation first.</summary>
    public static byte[] Csv(Statement s) => Delimited(s.Lines, ';');

    /// <summary>The "Libre" export: the same columns, separated by tabs.</summary>
    public static byte[] Libre(Statement s) => Delimited(s.Lines, '\t');

    private static byte[] Delimited(IReadOnlyList<Line> lines, char sep)
    {
        var sb = new StringBuilder($"Date opération{sep}Date valeur{sep}libellé{sep}Débit{sep}Crédit{sep}\r\n");
        foreach (var l in Enumerable.Reverse(lines))
            sb.Append(CultureInfo.InvariantCulture, $"{l.Date:dd/MM/yyyy}{sep}{l.Value:dd/MM/yyyy}{sep}{l.Full}{sep}{(l.Amount < 0 ? FrAmount(l.Amount) : "")}{sep}{(l.Amount > 0 ? FrAmount(l.Amount) : "")}{sep}\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>The Excel export: a table in an .xls file (HTML), the same columns as the CSV.</summary>
    public static byte[] Xls(Statement s)
    {
        var sb = new StringBuilder("<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\"></head><body><table border=\"1\">\r\n");
        sb.Append("<tr><th>Date opération</th><th>Date valeur</th><th>Libellé</th><th>Débit</th><th>Crédit</th></tr>\r\n");
        foreach (var l in Enumerable.Reverse(s.Lines))
            sb.Append(CultureInfo.InvariantCulture, $"<tr><td>{l.Date:dd/MM/yyyy}</td><td>{l.Value:dd/MM/yyyy}</td><td>{System.Net.WebUtility.HtmlEncode(l.Full)}</td><td>{(l.Amount < 0 ? FrAmount(l.Amount) : "")}</td><td>{(l.Amount > 0 ? FrAmount(l.Amount) : "")}</td></tr>\r\n");
        sb.Append("</table></body></html>\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>The Quicken export (QIF): dates day/month/year, amounts with a decimal comma.</summary>
    public static byte[] Qif(Statement s)
    {
        var sb = new StringBuilder("!Type:Bank\r\n");
        foreach (var l in s.Lines)
            sb.Append(CultureInfo.InvariantCulture, $"D{l.Date:dd/MM/yyyy}\r\nT{FrAmount(l.Amount)}\r\nP{l.Full}\r\n^\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>The Money export (OFX 1.02, SGML).</summary>
    public static byte[] Ofx(Statement s)
    {
        var sb = new StringBuilder("OFXHEADER:100\r\nDATA:OFXSGML\r\nVERSION:102\r\nSECURITY:NONE\r\nENCODING:USASCII\r\nCHARSET:1252\r\nCOMPRESSION:NONE\r\nOLDFILEUID:NONE\r\nNEWFILEUID:NONE\r\n\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"<OFX><SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>{s.End.AddDays(1):yyyyMMdd}<LANGUAGE>FRA</SONRS></SIGNONMSGSRSV1>\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"<BANKMSGSRSV1><STMTTRNRS><TRNUID>0<STATUS><CODE>0<SEVERITY>INFO</STATUS><STMTRS><CURDEF>EUR<BANKACCTFROM><BANKID>14806<BRANCHID>00000<ACCTID>{(s.Livret ? "1234567801" : "12345678901")}<ACCTTYPE>{(s.Livret ? "SAVINGS" : "CHECKING")}</BANKACCTFROM>\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"<BANKTRANLIST><DTSTART>{s.Start:yyyyMMdd}<DTEND>{s.End:yyyyMMdd}\r\n");
        var n = 1;
        foreach (var l in s.Lines)
            sb.Append(CultureInfo.InvariantCulture, $"<STMTTRN><TRNTYPE>{(l.Amount < 0 ? "DEBIT" : "CREDIT")}<DTPOSTED>{l.Date:yyyyMMdd}<TRNAMT>{l.Amount.ToString("0.00", CultureInfo.InvariantCulture)}<FITID>FTN{s.Start:yyyyMM}{n++:0000}<NAME>{l.Label}{(l.Detail is { } d ? "<MEMO>" + d : "")}</STMTTRN>\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"</BANKTRANLIST><LEDGERBAL><BALAMT>{s.Closing.ToString("0.00", CultureInfo.InvariantCulture)}<DTASOF>{s.End:yyyyMMdd}</LEDGERBAL></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>\r\n");
        return Encoding.ASCII.GetBytes(new string(sb.ToString().Normalize(NormalizationForm.FormD).Where(ch => ch < 128).ToArray()));
    }
}
