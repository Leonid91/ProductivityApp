using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Fictitious statements in the layouts of Fortuneo (a French online bank), as they are printed and exported in
/// 2021-2026:
/// <list type="bullet">
/// <item>the monthly PDF "Relevé de Compte N° 8 - 2026" of the "COMPTE COURANT", "Arrêté au 31 AOÛT 2026": the table
/// "Date | Date de Valeur | Opération | Débit | Crédit", the operation's day without its year ("05/08"), its value date
/// with it ("05/08/2026"), "ANCIEN SOLDE CRÉDITEUR AU dd/mm/yyyy" first, "TOTAL DES OPÉRATIONS DU RELEVÉ" then
/// "NOUVEAU SOLDE CRÉDITEUR AU dd/mm/yyyy" last, amounts "2 000,00" (a space between thousands), card payments
/// "CARTE dd/mm SHOP TOWN", labels on two lines, the date it was issued written up the left margin;</item>
/// <item>the CSV export ("Date opération;Date valeur;libellé;Débit;Crédit;"): ISO-8859-1, dates day/month/year, debits
/// negative in Débit, credits in Crédit, a ";" at the end of every line, the newest operation first.</item>
/// </list>
/// French cards are debit cards: card payments come straight out of the current account. The person, the account
/// numbers, the employer and the amounts are invented.
/// </summary>
public static class FortuneoGenerator
{
    public sealed record Line(DateOnly Date, DateOnly Value, string Label, string? Detail, decimal Amount);

    public sealed record Statement(DateOnly Start, DateOnly End, int Number, decimal Opening, List<Line> Lines)
    {
        public decimal Closing => Opening + Lines.Sum(l => l.Amount);
        public decimal Debits => -Lines.Where(l => l.Amount < 0).Sum(l => l.Amount);
        public decimal Credits => Lines.Where(l => l.Amount > 0).Sum(l => l.Amount);
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

    public static Statement Sample(int year = 2026, int month = 8, int seed = 9)
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
        for (var i = 0; i < 20; i++)
        {
            var s = Shops[rng.Next(Shops.Length)];
            var amount = s.Min == s.Max ? s.Min : Math.Round(s.Min + (decimal)rng.NextDouble() * (s.Max - s.Min), 2);
            var paid = Day(1 + rng.Next(end.Day - 2));
            var booked = paid.AddDays(1 + rng.Next(2));
            if (booked > end) booked = end;
            lines.Add(new Line(booked, booked, $"CARTE {paid.ToString("dd/MM", Fr)} {s.Shop}", null, -amount));
        }
        return new Statement(start, end, month, 2048.15m, [.. lines.OrderBy(l => l.Date)]);
    }

    /// <summary>"2 000,00", as Fortuneo prints amounts.</summary>
    private static string Money(decimal v) => Math.Abs(v).ToString("#,##0.00", CultureInfo.InvariantCulture).Replace(",", " ").Replace(".", ",");

    public static byte[] Pdf(Statement s)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var arrete = s.End.ToString("d MMMM yyyy", Fr).ToUpper(Fr);
        return Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.MarginVertical(30);
            page.MarginLeft(46);
            page.MarginRight(32);
            page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(8));
            page.Background().PaddingLeft(14).PaddingTop(300).Width(12).RotateLeft().Text($"Émis le {s.End.AddDays(1):dd/MM/yyyy}").FontSize(6);
            page.Header().Row(r =>
            {
                r.RelativeItem().Column(h =>
                {
                    h.Item().Text("Fortuneo").FontSize(18).Bold();
                    h.Item().Text("Fortuneo - Tour Ariane - 5 place de la Pyramide - 92088 Paris La Défense Cedex").FontSize(6.5f);
                });
                r.AutoItem().AlignRight().Column(h =>
                {
                    h.Item().AlignRight().Text($"Relevé de Compte N° {s.Number} - {s.End.Year}").FontSize(11).Bold();
                    h.Item().AlignRight().Text($"Arrêté au {arrete}");
                    h.Item().AlignRight().Text(t => { t.Span("Page "); t.CurrentPageNumber(); t.Span("/"); t.TotalPages(); });
                });
            });
            page.Content().Column(col =>
            {
                col.Item().PaddingTop(10).AlignRight().Column(a =>
                {
                    a.Item().Text("M ALEX SAMPLE");
                    a.Item().Text("8 RUE DE L'EXEMPLE");
                    a.Item().Text("69003 LYON");
                });
                col.Item().PaddingTop(10).Text("COMPTE COURANT").Bold();
                col.Item().Text("Titulaire(s) du compte : M ALEX SAMPLE");
                col.Item().Text("Numero de compte : 14806 00000 12345678901 23 - B IC FTNOFRP1XXX").FontSize(7);
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
                    Balance($"ANCIEN SOLDE {(s.Opening < 0 ? "DÉBITEUR" : "CRÉDITEUR")} AU {s.Start.AddDays(-1):dd/MM/yyyy}", s.Opening);
                    foreach (var l in s.Lines)
                    {
                        t.Cell().Element(Cell).Text(l.Date.ToString("dd/MM", Fr));
                        t.Cell().Element(Cell).Text(l.Value.ToString("dd/MM/yyyy", Fr));
                        t.Cell().Element(Cell).Column(d => { d.Item().Text(l.Label); if (l.Detail is { } more) d.Item().Text(more); });
                        t.Cell().Element(Cell).AlignRight().Text(l.Amount < 0 ? Money(l.Amount) : "");
                        t.Cell().Element(Cell).AlignRight().Text(l.Amount > 0 ? Money(l.Amount) : "");
                    }
                    t.Cell().Element(Cell).BorderTop(0.6f).Text("");
                    t.Cell().Element(Cell).BorderTop(0.6f).Text("");
                    t.Cell().Element(Cell).BorderTop(0.6f).Text("TOTAL DES OPÉRATIONS DU RELEVÉ").Bold();
                    t.Cell().Element(Cell).BorderTop(0.6f).AlignRight().Text(Money(s.Debits)).Bold();
                    t.Cell().Element(Cell).BorderTop(0.6f).AlignRight().Text(Money(s.Credits)).Bold();
                    Balance($"NOUVEAU SOLDE {(s.Closing < 0 ? "DÉBITEUR" : "CRÉDITEUR")} AU {s.End:dd/MM/yyyy}", s.Closing);
                });
                col.Item().PaddingTop(8).Text("Total facturé ce mois : 2,00 €").FontSize(7);
            });
            page.Footer().Text("Fortuneo est une marque commerciale exemple - SA au capital de 0 000 000 € - RCS Brest 000 000 000").FontSize(6);
        })).WithMetadata(new DocumentMetadata { CreationDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), ModifiedDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) })
        .GeneratePdf();
    }

    /// <summary>The CSV export: ISO-8859-1, debits negative in Débit, credits in Crédit, the newest operation first.</summary>
    public static byte[] Csv(Statement s)
    {
        var sb = new StringBuilder("Date opération;Date valeur;libellé;Débit;Crédit;\r\n");
        foreach (var l in Enumerable.Reverse(s.Lines))
        {
            var amount = l.Amount.ToString("0.00", CultureInfo.InvariantCulture).Replace(".", ",");
            sb.Append(CultureInfo.InvariantCulture, $"{l.Date:dd/MM/yyyy};{l.Value:dd/MM/yyyy};{l.Label}{(l.Detail is { } d ? " " + d : "")};{(l.Amount < 0 ? amount : "")};{(l.Amount > 0 ? amount : "")};\r\n");
        }
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
