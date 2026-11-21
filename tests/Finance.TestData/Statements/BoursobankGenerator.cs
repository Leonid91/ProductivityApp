using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Fictitious statements in every form BoursoBank (Boursorama Banque until 2024, a French online bank) gives them in
/// 2021-2026:
/// <list type="bullet">
/// <item>the monthly PDF of an account, "Extrait de votre compte" (the current account, or a savings account named
/// "Compte d'épargne : LIVRET A"): "MOUVEMENTS EN EUR", the period "du dd/mm/yyyy au dd/mm/yyyy", the table
/// "Date opération | Libellé | Valeur | Débit | Crédit" with dates day/month/year, the opening balance as its first line
/// ("SOLDE AU : dd/mm/yyyy", in Débit when the account is overdrawn), the closing one as its last ("Nouveau solde en
/// EUR :"), amounts written "1.234,56", several pages (the table goes on under the heading of each page), the notice
/// after it ("A réception de ce relevé ..."); a deferred debit card's month taken in one line ("Relevé différé Carte
/// 4810********1234"); the Boursorama Banque name before 2024;</item>
/// <item>the monthly PDF of a deferred debit card, "Relevé de Carte": each payment ("PAIEMENT CARTE"), refund
/// ("AVOIR") and fee on a payment abroad ("CION OP.ETR"), with the card's number and the department of the shop, the
/// amount in the currency paid and in euros, and the month's total "A VOTRE DEBIT LE dd/mm/yyyy";</item>
/// <item>the CSV exports of an account or a card: the older one ("dateOp;dateVal;label;category;categoryParent;
/// supplierFound;amount;comment;accountNum;accountLabel;accountbalance", UTF-8 with a byte order mark, amounts
/// "-1 100,50", the account's balance at the export on every line, the newest operation first) and the newer one with
/// French headers, whose first "Solde" is the amount and the second the balance after it ("1234.56");</item>
/// <item>the Money (OFX) and Quicken (QIF) exports.</item>
/// </list>
/// The person, the account and card numbers, the employer and the amounts are invented.
/// </summary>
public static class BoursobankGenerator
{
    public sealed record Line(DateOnly Date, DateOnly Value, string Label, string? Detail, decimal Amount, string Category, string Parent, string Suggested)
    {
        public string Full => Label + (Detail is { } d ? " " + d : "");
    }

    /// <param name="Title">The account as the statement names it ("BOURSOBANK ESSENTIEL", "LIVRET A").</param>
    /// <param name="Savings">A savings account: "Compte d'épargne : ..." above its table.</param>
    /// <param name="Brand">"BoursoBank", or "Boursorama Banque" before 2024.</param>
    public sealed record Statement(DateOnly Start, DateOnly End, string Number, decimal Opening, List<Line> Lines, string Title = "BOURSOBANK ESSENTIEL", bool Savings = false, string Brand = "BoursoBank")
    {
        public decimal Closing => Opening + Lines.Sum(l => l.Amount);
    }

    /// <summary>An operation of a deferred debit card: paid on <paramref name="Date"/>, in <paramref name="Currency"/> when abroad.</summary>
    public sealed record CardLine(DateOnly Date, string Kind, string Place, string Shop, decimal Euros, decimal Paid, string Currency);

    public sealed record Card(DateOnly Start, DateOnly End, string Number, List<CardLine> Lines)
    {
        /// <summary>What the month costs, taken from the current account at its end (refunds count against it).</summary>
        public decimal Total => Lines.Sum(l => l.Kind == "AVOIR" ? -l.Euros : l.Euros);
    }

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private static readonly (string Shop, string Suggested, string Category, string Parent, decimal Min, decimal Max)[] Shops =
    [
        ("MONOPRIX EXEMPLE", "Monoprix", "Alimentation", "Vie quotidienne", 8.40m, 86.20m),
        ("BOULANGERIE DU PARC", "Boulangerie du Parc", "Alimentation", "Vie quotidienne", 1.80m, 12.40m),
        ("SNCF CONNECT", "SNCF", "Train", "Transports", 19.00m, 124.00m),
        ("PHARMACIE CENTRALE", "Pharmacie", "Santé", "Santé", 4.90m, 36.50m),
        ("CINEMA EXEMPLE", "Cinéma", "Loisirs", "Loisirs", 9.50m, 23.00m),
        ("RESTAURANT LE SAMPLE", "Restaurant", "Restaurants", "Loisirs", 14.00m, 68.00m),
    ];

    /// <summary>
    /// A month of the current account: <paramref name="cards"/> card payments (many make several pages);
    /// <paramref name="deferred"/>: the month of a deferred debit card taken in one line at its end.
    /// </summary>
    public static Statement Sample(int year = 2025, int month = 9, int seed = 7, int cards = 18, decimal opening = 1384.27m, Card? deferred = null, string brand = "BoursoBank")
    {
        var rng = new Random(seed);
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        DateOnly Day(int d) => start.AddDays(Math.Min(d, end.Day) - 1);
        Line Op(int d, string label, decimal amount, string category, string parent, string suggested, string? detail = null) => new(Day(d), Day(d), label, detail, amount, category, parent, suggested);
        var lines = new List<Line>
        {
            Op(1, "VIR SEPA EXEMPLE SAS", 2614.91m, "Salaires", "Revenus", "Salaire", "SALAIRE " + start.ToString("MMMM yyyy", Fr).ToUpperInvariant()),
            Op(3, "PRLV SEPA EDF CLIENTS PARTICULIERS", -71.00m, "Électricité", "Logement", "EDF"),
            Op(5, "VIR SEPA SCI EXEMPLE", -820.00m, "Loyer", "Logement", "Loyer", "LOYER"),
            Op(8, "RETRAIT DAB " + Day(8).ToString("dd/MM/yy", Fr) + " PARIS 15 CB*4821", -60.00m, "Retraits", "Vie quotidienne", "Retrait"),
            Op(11, "PRLV SEPA FREE MOBILE", -19.99m, "Téléphone", "Abonnements", "Free Mobile"),
            Op(15, "VIR INST M PAUL EXEMPLE", 1873.44m, "Virements reçus", "Revenus", "Paul Exemple", "REMBOURSEMENT VOITURE"),
            Op(18, "AVOIR " + Day(16).ToString("dd/MM/yy", Fr) + " FNAC EXEMPLE CB*4821", 24.99m, "Shopping", "Achats", "Fnac"),
            Op(26, "PRLV SEPA MUTUELLE EXEMPLE", -42.30m, "Mutuelle", "Santé", "Mutuelle"),
        };
        if (deferred is not null)
            lines.Add(new Line(end, end, "Relevé différé Carte " + deferred.Number, null, -deferred.Total, "Carte à débit différé", "Vie quotidienne", "Carte"));
        for (var i = 0; i < cards; i++)
        {
            var s = Shops[rng.Next(Shops.Length)];
            var amount = Math.Round(s.Min + (decimal)rng.NextDouble() * (s.Max - s.Min), 2);
            var paid = Day(1 + rng.Next(end.Day - 2));
            var booked = paid.AddDays(1 + rng.Next(2));
            if (booked > end) booked = end;
            lines.Add(new Line(booked, booked, $"CARTE {paid.ToString("dd/MM/yy", Fr)} {s.Shop} CB*4821", null, -amount, s.Category, s.Parent, s.Suggested));
        }
        return new Statement(start, end, "00040123456", opening, [.. lines.OrderBy(l => l.Date)], Brand: brand);
    }

    /// <summary>A month of a Livret A: a transfer in, one out, the year's interest.</summary>
    public static Statement Savings(int year = 2025, int month = 12)
    {
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        return new Statement(start, end, "00040987654", 6120.00m,
        [
            new(start.AddDays(4), start.AddDays(4), "VIR VIREMENT DEPUIS BOURSOBANK ESSENTIEL", null, 300.00m, "Épargne", "Épargne", "Épargne"),
            new(start.AddDays(17), start.AddDays(17), "VIR VIREMENT VERS BOURSOBANK ESSENTIEL", null, -450.00m, "Épargne", "Épargne", "Épargne"),
            new(end, end, "INTERETS CREDITEURS", "ANNEE " + year.ToString(CultureInfo.InvariantCulture), 143.18m, "Intérêts", "Revenus", "Intérêts"),
        ], Title: "LIVRET A", Savings: true);
    }

    /// <summary>A month of a deferred debit card: payments in France and abroad, a refund, a fee on a payment abroad.</summary>
    public static Card DeferredCard(int year = 2025, int month = 9, int seed = 12)
    {
        var rng = new Random(seed);
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var lines = new List<CardLine>();
        for (var i = 0; i < 16; i++)
        {
            var s = Shops[rng.Next(Shops.Length)];
            var amount = Math.Round(s.Min + (decimal)rng.NextDouble() * (s.Max - s.Min), 2);
            lines.Add(new CardLine(start.AddDays(rng.Next(end.Day)), "PAIEMENT CARTE", "75", s.Shop, amount, amount, "EUR"));
        }
        lines.Add(new CardLine(start.AddDays(12), "PAIEMENT CARTE", "", "AMAZON SAMPLE LONDON", 58.35m, 49.99m, "GBP"));
        lines.Add(new CardLine(start.AddDays(12), "CION OP.ETR", "", "AMAZON SAMPLE LONDON", 1.17m, 1.17m, "EUR"));
        lines.Add(new CardLine(start.AddDays(20), "AVOIR", "92", "DECATHLON EXEMPLE", 34.99m, 34.99m, "EUR"));
        return new Card(start, end, "4810********1234", [.. lines.OrderBy(l => l.Date)]);
    }

    /// <summary>"1.234,56", as the PDFs print amounts.</summary>
    private static string Money(decimal v) => Math.Abs(v).ToString("#,##0.00", CultureInfo.InvariantCulture).Replace(",", "\u0001").Replace(".", ",").Replace("\u0001", ".");

    /// <summary>"-1 234,56", as the CSV exports write amounts.</summary>
    private static string CsvMoney(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture).Replace(",", " ").Replace(".", ",");

    private static void Heading(PageDescriptor page, string brand, string title, DateOnly start, DateOnly end) => page.Header().Row(r =>
    {
        r.RelativeItem().Column(h =>
        {
            h.Item().Text(brand).FontSize(18).Bold();
            h.Item().Text("44 rue Traversière - CS 80134 - 92772 Boulogne-Billancourt Cedex");
        });
        r.AutoItem().AlignRight().Column(h =>
        {
            h.Item().AlignRight().Text(title).FontSize(11).Bold();
            h.Item().AlignRight().Text($"du {start:dd/MM/yyyy} au {end:dd/MM/yyyy}");
            h.Item().AlignRight().Text(t => { t.Span("Page "); t.CurrentPageNumber(); t.Span(" / "); t.TotalPages(); });
        });
    });

    public static byte[] Pdf(Statement s)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.MarginVertical(30);
            page.MarginHorizontal(32);
            page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(8));
            Heading(page, s.Brand, "Extrait de votre compte", s.Start, s.End);
            page.Content().Column(col =>
            {
                col.Item().PaddingTop(10).AlignRight().Column(a =>
                {
                    a.Item().Text("M ALEX SAMPLE");
                    a.Item().Text("8 RUE DE L'EXEMPLE");
                    a.Item().Text("75015 PARIS");
                });
                col.Item().PaddingTop(12).Text(s.Savings ? $"Compte d'épargne : {s.Title} N° {s.Number}" : $"{s.Title} N° {s.Number}").Bold();
                col.Item().Text($"I.B.A.N. FR76 4061 8803 6600 {s.Number[..4]} {s.Number[4..8]} {s.Number[8..]}1 94 - BIC : BOUSFRPPXXX").FontSize(7);
                col.Item().PaddingTop(8).Text("MOUVEMENTS EN EUR").Bold();
                col.Item().PaddingTop(4).Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.ConstantColumn(60); cd.RelativeColumn(); cd.ConstantColumn(56); cd.ConstantColumn(66); cd.ConstantColumn(66); });
                    static IContainer Head(IContainer x) => x.BorderBottom(0.6f).PaddingVertical(2);
                    t.Header(hd =>
                    {
                        hd.Cell().Element(Head).Text("Date opération").Bold();
                        hd.Cell().Element(Head).Text("Libellé").Bold();
                        hd.Cell().Element(Head).Text("Valeur").Bold();
                        hd.Cell().Element(Head).AlignRight().Text("Débit").Bold();
                        hd.Cell().Element(Head).AlignRight().Text("Crédit").Bold();
                    });
                    static IContainer Cell(IContainer x) => x.PaddingVertical(2);
                    t.Cell().Element(Cell).Text("");
                    t.Cell().Element(Cell).Text($"SOLDE AU : {s.Start.AddDays(-1):dd/MM/yyyy}").Bold();
                    t.Cell().Element(Cell).Text("");
                    t.Cell().Element(Cell).AlignRight().Text(s.Opening < 0 ? Money(s.Opening) : "").Bold();
                    t.Cell().Element(Cell).AlignRight().Text(s.Opening >= 0 ? Money(s.Opening) : "").Bold();
                    foreach (var l in s.Lines)
                    {
                        t.Cell().Element(Cell).Text(l.Date.ToString("dd/MM/yyyy", Fr));
                        t.Cell().Element(Cell).Column(d => { d.Item().Text(l.Label); if (l.Detail is { } more) d.Item().Text(more); });
                        t.Cell().Element(Cell).Text(l.Value.ToString("dd/MM/yyyy", Fr));
                        t.Cell().Element(Cell).AlignRight().Text(l.Amount < 0 ? Money(l.Amount) : "");
                        t.Cell().Element(Cell).AlignRight().Text(l.Amount > 0 ? Money(l.Amount) : "");
                    }
                    t.Cell().Element(Cell).BorderTop(0.6f).Text("");
                    t.Cell().Element(Cell).BorderTop(0.6f).Text("Nouveau solde en EUR :").Bold();
                    t.Cell().Element(Cell).BorderTop(0.6f).Text("");
                    t.Cell().Element(Cell).BorderTop(0.6f).AlignRight().Text(s.Closing < 0 ? Money(s.Closing) : "").Bold();
                    t.Cell().Element(Cell).BorderTop(0.6f).AlignRight().Text(s.Closing >= 0 ? Money(s.Closing) : "").Bold();
                });
                col.Item().PaddingTop(8).Text("A réception de ce relevé, vérifiez les opérations qui y figurent. Sans réclamation de votre part dans un délai d'un mois, elles seront considérées comme approuvées.").FontSize(6.5f);
            });
            page.Footer().Text($"{s.Brand} - S.A. au capital de 0 000 000 € - RCS Nanterre 000 000 000 - Exemple").FontSize(6);
        })).WithMetadata(new DocumentMetadata { CreationDate = new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero), ModifiedDate = new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero) })
        .GeneratePdf();
    }

    /// <summary>The month of a deferred debit card: each operation, then what is taken from the current account.</summary>
    public static byte[] CardPdf(Card c)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(d => d.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.MarginVertical(30);
            page.MarginHorizontal(32);
            page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(8));
            Heading(page, "BoursoBank", "Relevé de Carte", c.Start, c.End);
            page.Content().Column(col =>
            {
                col.Item().PaddingTop(10).AlignRight().Text("M ALEX SAMPLE");
                col.Item().PaddingTop(12).Text($"Carte VISA PREMIER N° {c.Number} - Débit différé").Bold();
                col.Item().Text("Compte débité : BOURSOBANK ESSENTIEL N° 00040123456").FontSize(7);
                col.Item().PaddingTop(8).Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.ConstantColumn(60); cd.RelativeColumn(); cd.ConstantColumn(80); cd.ConstantColumn(72); });
                    static IContainer Head(IContainer x) => x.BorderBottom(0.6f).PaddingVertical(2);
                    t.Header(hd =>
                    {
                        hd.Cell().Element(Head).Text("Date").Bold();
                        hd.Cell().Element(Head).Text("Libellé").Bold();
                        hd.Cell().Element(Head).AlignRight().Text("Montant devise").Bold();
                        hd.Cell().Element(Head).AlignRight().Text("Montant EUR").Bold();
                    });
                    static IContainer Cell(IContainer x) => x.PaddingVertical(2);
                    foreach (var l in c.Lines)
                    {
                        t.Cell().Element(Cell).Text(l.Date.ToString("dd/MM/yyyy", Fr));
                        t.Cell().Element(Cell).Text($"{l.Kind} {c.Number.Replace("*", "", StringComparison.Ordinal)[..4]}{(l.Place.Length > 0 ? " " + l.Place : "")} {l.Shop}");
                        t.Cell().Element(Cell).AlignRight().Text(Money(l.Paid) + (l.Currency == "EUR" ? "" : " " + l.Currency));
                        t.Cell().Element(Cell).AlignRight().Text(Money(l.Euros));
                    }
                    t.Cell().Element(Cell).BorderTop(0.6f).Text("");
                    t.Cell().Element(Cell).BorderTop(0.6f).Text($"A VOTRE DEBIT LE {c.End:dd/MM/yyyy}").Bold();
                    t.Cell().Element(Cell).BorderTop(0.6f).Text("");
                    t.Cell().Element(Cell).BorderTop(0.6f).AlignRight().Text(Money(c.Total)).Bold();
                });
                col.Item().PaddingTop(8).Text("Les avoirs viennent en déduction du montant débité.").FontSize(6.5f);
            });
            page.Footer().Text("BoursoBank - S.A. au capital de 0 000 000 € - RCS Nanterre 000 000 000 - Exemple").FontSize(6);
        })).WithMetadata(new DocumentMetadata { CreationDate = new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero), ModifiedDate = new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero) })
        .GeneratePdf();
    }

    private static string Quote(string v) => v.Contains(' ') || v.Contains(';') || v.Contains('"') || v.Any(ch => ch > 127) ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    private static byte[] Utf8WithMark(string text) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];

    /// <summary>The older export: English column names, the account's balance (at the export) on every line.</summary>
    public static byte[] OldCsv(Statement s)
    {
        var sb = new StringBuilder("dateOp;dateVal;label;category;categoryParent;supplierFound;amount;comment;accountNum;accountLabel;accountbalance\n");
        foreach (var l in Enumerable.Reverse(s.Lines))
            sb.Append(CultureInfo.InvariantCulture, $"{l.Date:yyyy-MM-dd};{l.Value:yyyy-MM-dd};{Quote(l.Full)};{Quote(l.Category)};{Quote(l.Parent)};{Quote(l.Suggested.ToLowerInvariant())};{Quote(CsvMoney(l.Amount))};;{s.Number};BoursoBank;{s.Closing.ToString("0.00", CultureInfo.InvariantCulture)}\n");
        return Utf8WithMark(sb.ToString());
    }

    /// <summary>The newer export: French column names, two named "Solde" (the operation, then the balance after it).</summary>
    public static byte[] NewCsv(Statement s)
    {
        var sb = new StringBuilder("\"Date Opération\";\"Date Valeur\";Libellé;\"Libellé Suggéré\";Catégorie;\"Catégorie Parente\";Solde;Commentaire;\"Numéro Compte\";\"Libellé Compte\";Solde;Pointage\n");
        var balance = s.Closing;
        foreach (var l in Enumerable.Reverse(s.Lines))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{l.Date:yyyy-MM-dd};{l.Value:yyyy-MM-dd};{Quote(l.Full)};{Quote(l.Suggested)};{Quote(l.Category)};{Quote(l.Parent)};{Quote(CsvMoney(l.Amount))};;{s.Number};BoursoBank;{balance.ToString("0.00", CultureInfo.InvariantCulture)};Non\n");
            balance -= l.Amount;
        }
        return Utf8WithMark(sb.ToString());
    }

    /// <summary>The older export of a deferred debit card: each operation signed (refunds positive), the card's label.</summary>
    public static byte[] CardCsv(Card c)
    {
        var sb = new StringBuilder("dateOp;dateVal;label;category;categoryParent;supplierFound;amount;comment;accountNum;accountLabel;accountbalance\n");
        foreach (var l in Enumerable.Reverse(c.Lines))
        {
            var amount = l.Kind == "AVOIR" ? l.Euros : -l.Euros;
            sb.Append(CultureInfo.InvariantCulture, $"{l.Date:yyyy-MM-dd};{l.Date:yyyy-MM-dd};{Quote($"{l.Kind} {l.Shop}")};Divers;Divers;;{Quote(CsvMoney(amount))};;{c.Number};\"CB VISA PREMIER\";\n");
        }
        return Utf8WithMark(sb.ToString());
    }

    /// <summary>The Money export (OFX 1.02, SGML): every operation of the account, its balance at the end.</summary>
    public static byte[] Ofx(Statement s)
    {
        var sb = new StringBuilder("OFXHEADER:100\r\nDATA:OFXSGML\r\nVERSION:102\r\nSECURITY:NONE\r\nENCODING:USASCII\r\nCHARSET:1252\r\nCOMPRESSION:NONE\r\nOLDFILEUID:NONE\r\nNEWFILEUID:NONE\r\n\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"<OFX><SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>{s.End.AddDays(1):yyyyMMdd}<LANGUAGE>FRA</SONRS></SIGNONMSGSRSV1>\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"<BANKMSGSRSV1><STMTTRNRS><TRNUID>0<STATUS><CODE>0<SEVERITY>INFO</STATUS><STMTRS><CURDEF>EUR<BANKACCTFROM><BANKID>40618<BRANCHID>80366<ACCTID>{s.Number}<ACCTTYPE>{(s.Savings ? "SAVINGS" : "CHECKING")}</BANKACCTFROM>\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"<BANKTRANLIST><DTSTART>{s.Start:yyyyMMdd}<DTEND>{s.End:yyyyMMdd}\r\n");
        var n = 1;
        foreach (var l in s.Lines)
            sb.Append(CultureInfo.InvariantCulture, $"<STMTTRN><TRNTYPE>{(l.Amount < 0 ? "DEBIT" : "CREDIT")}<DTPOSTED>{l.Date:yyyyMMdd}<TRNAMT>{l.Amount.ToString("0.00", CultureInfo.InvariantCulture)}<FITID>BRS{s.Start:yyyyMM}{n++:0000}<NAME>{l.Label}{(l.Detail is { } d ? "<MEMO>" + d : "")}</STMTTRN>\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"</BANKTRANLIST><LEDGERBAL><BALAMT>{s.Closing.ToString("0.00", CultureInfo.InvariantCulture)}<DTASOF>{s.End:yyyyMMdd}</LEDGERBAL></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>\r\n");
        return Encoding.ASCII.GetBytes(new string(sb.ToString().Normalize(NormalizationForm.FormD).Where(ch => ch < 128).ToArray()));
    }

    /// <summary>The Quicken export (QIF): dates day/month/year, amounts with a decimal comma.</summary>
    public static byte[] Qif(Statement s)
    {
        var sb = new StringBuilder("!Type:Bank\r\n");
        foreach (var l in s.Lines)
            sb.Append(CultureInfo.InvariantCulture, $"D{l.Date:dd/MM/yyyy}\r\nT{l.Amount.ToString("0.00", Fr)}\r\nP{l.Full}\r\n^\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
