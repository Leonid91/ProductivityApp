using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Fictitious statements in the layouts of BoursoBank (formerly Boursorama Banque, a French online bank), as they are
/// printed and exported in 2021-2026:
/// <list type="bullet">
/// <item>the monthly PDF "Extrait de votre compte": "MOUVEMENTS EN EUR", the period "du dd/mm/yyyy au dd/mm/yyyy",
/// the table "Date opération | Libellé | Valeur | Débit | Crédit" with dates day/month/year, the opening balance as its
/// first line ("SOLDE AU : dd/mm/yyyy"), the closing one as its last ("Nouveau solde en EUR :"), amounts written
/// "1.234,56", card payments "CARTE dd/mm/yy SHOP CB*1234" on two lines, the notice after the table ("A réception de ce
/// relevé ...");</item>
/// <item>the older CSV export ("dateOp;dateVal;label;category;categoryParent;supplierFound;amount;comment;accountNum;
/// accountLabel;accountbalance"): UTF-8 with a byte order mark, ";", dates year-month-day, amounts "-1 100,50" (signed,
/// a space between thousands), the account's balance on every line, the newest operation first;</item>
/// <item>the newer CSV export with French headers ("Date Opération";"Date Valeur";Libellé;"Libellé Suggéré";Catégorie;
/// "Catégorie Parente";Solde;Commentaire;"Numéro Compte";"Libellé Compte";Solde;Pointage): the first "Solde" is the
/// amount of the operation, the second the balance after it (written "1234.56").</item>
/// </list>
/// French cards are debit cards: card payments come straight out of the current account. The person, the account
/// numbers, the employer and the amounts are invented.
/// </summary>
public static class BoursobankGenerator
{
    public sealed record Line(DateOnly Date, DateOnly Value, string Label, string? Detail, decimal Amount, string Category, string Parent, string Suggested);

    public sealed record Statement(DateOnly Start, DateOnly End, string Number, decimal Opening, List<Line> Lines)
    {
        public decimal Closing => Opening + Lines.Sum(l => l.Amount);
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

    public static Statement Sample(int year = 2025, int month = 9, int seed = 7)
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
        for (var i = 0; i < 18; i++)
        {
            var s = Shops[rng.Next(Shops.Length)];
            var amount = Math.Round(s.Min + (decimal)rng.NextDouble() * (s.Max - s.Min), 2);
            var paid = Day(1 + rng.Next(end.Day - 2));
            var booked = paid.AddDays(1 + rng.Next(2));
            if (booked > end) booked = end;
            lines.Add(new Line(booked, booked, $"CARTE {paid.ToString("dd/MM/yy", Fr)} {s.Shop} CB*4821", null, -amount, s.Category, s.Parent, s.Suggested));
        }
        return new Statement(start, end, "00040123456", 1384.27m, [.. lines.OrderBy(l => l.Date)]);
    }

    /// <summary>"1.234,56", as the PDF prints amounts.</summary>
    private static string Money(decimal v) => Math.Abs(v).ToString("#,##0.00", CultureInfo.InvariantCulture).Replace(",", "\u0001").Replace(".", ",").Replace("\u0001", ".");

    /// <summary>"-1 234,56", as the CSV exports write amounts.</summary>
    private static string CsvMoney(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture).Replace(",", " ").Replace(".", ",");

    public static byte[] Pdf(Statement s)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.MarginVertical(30);
            page.MarginHorizontal(32);
            page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(8));
            page.Header().Row(r =>
            {
                r.RelativeItem().Column(h =>
                {
                    h.Item().Text("BoursoBank").FontSize(18).Bold();
                    h.Item().Text("44 rue Traversière - CS 80134 - 92772 Boulogne-Billancourt Cedex");
                });
                r.AutoItem().AlignRight().Column(h =>
                {
                    h.Item().AlignRight().Text("Extrait de votre compte").FontSize(11).Bold();
                    h.Item().AlignRight().Text($"du {s.Start:dd/MM/yyyy} au {s.End:dd/MM/yyyy}");
                    h.Item().AlignRight().Text(t => { t.Span("Page "); t.CurrentPageNumber(); t.Span(" / "); t.TotalPages(); });
                });
            });
            page.Content().Column(col =>
            {
                col.Item().PaddingTop(10).AlignRight().Column(a =>
                {
                    a.Item().Text("M ALEX SAMPLE");
                    a.Item().Text("8 RUE DE L'EXEMPLE");
                    a.Item().Text("75015 PARIS");
                });
                col.Item().PaddingTop(12).Text($"BOURSOBANK ESSENTIEL N° {s.Number}").Bold();
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
            sb.Append(CultureInfo.InvariantCulture, $"{l.Date:yyyy-MM-dd};{l.Value:yyyy-MM-dd};{Quote(l.Label + (l.Detail is { } d ? " " + d : ""))};{Quote(l.Category)};{Quote(l.Parent)};{Quote(l.Suggested.ToLowerInvariant())};{Quote(CsvMoney(l.Amount))};;{s.Number};BoursoBank;{s.Closing.ToString("0.00", CultureInfo.InvariantCulture)}\n");
        return Utf8WithMark(sb.ToString());
    }

    /// <summary>The newer export: French column names, two named "Solde" (the operation, then the balance after it).</summary>
    public static byte[] NewCsv(Statement s)
    {
        var sb = new StringBuilder("\"Date Opération\";\"Date Valeur\";Libellé;\"Libellé Suggéré\";Catégorie;\"Catégorie Parente\";Solde;Commentaire;\"Numéro Compte\";\"Libellé Compte\";Solde;Pointage\n");
        var balance = s.Closing;
        foreach (var l in Enumerable.Reverse(s.Lines))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{l.Date:yyyy-MM-dd};{l.Value:yyyy-MM-dd};{Quote(l.Label + (l.Detail is { } d ? " " + d : ""))};{Quote(l.Suggested)};{Quote(l.Category)};{Quote(l.Parent)};{Quote(CsvMoney(l.Amount))};;{s.Number};BoursoBank;{balance.ToString("0.00", CultureInfo.InvariantCulture)};Non\n");
            balance -= l.Amount;
        }
        return Utf8WithMark(sb.ToString());
    }
}
