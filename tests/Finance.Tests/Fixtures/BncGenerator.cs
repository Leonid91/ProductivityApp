using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Fictitious month at a Banque Nationale-style bank, in the layouts of its online exports: the PDF
/// "Historique de mes transactions" and the CSV files, for a Mastercard, a chequing and a high-interest savings
/// account. The three accounts are coherent: the card is paid from chequing, money moves between chequing and
/// savings. Every merchant amount, person and number is invented.
/// </summary>
public static class BncGenerator
{
    public enum BncAccount { Card, Chequing, Savings }

    public sealed record Line(DateOnly Date, string Description, string Category, decimal Amount);

    public sealed record Month(List<Line> Card, List<Line> Chequing, List<Line> Savings, decimal ChequingOpening, decimal SavingsOpening)
    {
        public List<Line> Of(BncAccount a) => a switch { BncAccount.Card => Card, BncAccount.Chequing => Chequing, _ => Savings };
        public decimal Opening(BncAccount a) => a switch { BncAccount.Chequing => ChequingOpening, BncAccount.Savings => SavingsOpening, _ => 0 };
    }

    private static readonly (string Name, string Category, decimal Min, decimal Max)[] Shops =
    [
        ("IGA", "Epicerie", 4.10m, 78.40m),
        ("Metro Plus", "Epicerie", 12.00m, 96.50m),
        ("Uber", "Location d'auto et taxi", 9.80m, 24.60m),
        ("Transit Exemple", "Transport en commun", 3.75m, 7.00m),
        ("Pharmaprix", "Pharmacie", 6.20m, 41.30m),
        ("Tim Hortons", "Restauration rapide", 2.40m, 14.90m),
        ("Le Petit Bistro", "Restaurants", 21.00m, 64.00m),
        ("Cafe Exemple", "Cafes", 3.90m, 9.20m),
        ("Netflix", "Divertissement", 20.69m, 20.69m),
        ("Librairie Fictive", "Magasinage", 11.50m, 58.00m),
        ("Telecom Imaginaire", "Internet", 45.00m, 45.00m),
        ("Salon Modele", "Salon de coiffure", 38.00m, 72.00m),
    ];

    public static Month Sample(int year, int month, int seed)
    {
        var rng = new Random(seed);
        var start = new DateOnly(year, month, 1);
        var days = DateTime.DaysInMonth(year, month);
        decimal Pick(decimal min, decimal max) => min == max ? min : Math.Round(min + (decimal)rng.NextDouble() * (max - min), 2);

        var card = new List<Line>();
        for (var i = 0; i < 26; i++)
        {
            var s = Shops[rng.Next(Shops.Length)];
            card.Add(new Line(start.AddDays(rng.Next(days)), s.Name, s.Category, -Pick(s.Min, s.Max)));
        }
        var chequing = new List<Line>
        {
            new(start.AddDays(14), "Depot de paie Exemple", "Revenus", 2310.45m),
            new(start.AddDays(days - 2), "Depot de paie Exemple", "Revenus", 2310.45m),
            new(start.AddDays(10), "Frais mensuels fixes", "Frais", 0m),
            new(start.AddDays(10), "Service Virement INTERAC", "Frais", 0m),
            new(start.AddDays(20), "Paiement de facture", "Investissements", -500.00m),
            new(start.AddDays(25), "Protection Exemple", "Protection antifraude", -9.95m),
        };
        // Three card payments, each received on the card a day before it leaves chequing (as banks post them).
        foreach (var d in new[] { 8, 16, 24 })
        {
            var amount = Pick(120m, 420m);
            card.Add(new Line(start.AddDays(d), "Paiement recu merci!", "Paiement carte de credit", amount));
            chequing.Add(new Line(start.AddDays(d + 1), "Paiement Mastercard", "Paiement carte de credit", -amount));
        }
        var moved = 250.00m;
        chequing.Add(new Line(start.AddDays(27), "Transfert entre comptes", "Transfert", moved));
        var savings = new List<Line>
        {
            new(start.AddDays(10), "Paiement des interets", "Revenu d'interets", 3.17m),
            new(start.AddDays(27), "Transfert entre comptes", "Transfert", -moved),
        };
        return new Month(Order(card), Order(chequing), Order(savings), 1840.32m, 6200.00m);
    }

    private static List<Line> Order(List<Line> l) => l.OrderBy(x => x.Date).ToList();

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-CA");

    private static string Money(decimal v, bool sign) =>
        (sign && v > 0 ? "+" : "") + Math.Abs(v).ToString("#,##0.00", Fr).Replace(' ', ' ').Replace(' ', ' ') + " $";

    /// <summary>The PDF export, newest operation first, as the bank prints it.</summary>
    public static byte[] Pdf(Month m, BncAccount a)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var lines = m.Of(a);
        var balances = new List<decimal>();
        var b = m.Opening(a);
        foreach (var l in lines) { b += l.Amount; balances.Add(b); }
        var rows = lines.Zip(balances).Reverse().ToList();
        var withBalance = a != BncAccount.Card;
        var last = lines.Max(l => l.Date);
        return Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.MarginHorizontal(38);
            page.MarginVertical(36);
            page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(7.5f));
            page.Content().Column(col =>
            {
                col.Item().AlignRight().Text($"Généré le {last.AddDays(28).ToString("d MMMM yyyy", Fr)}").FontSize(9);
                col.Item().PaddingTop(24).Row(r =>
                {
                    r.RelativeItem(2).Column(x =>
                    {
                        x.Item().Text(a switch { BncAccount.Card => "Mastercard Exemple Plus", BncAccount.Chequing => "Compte Chèques", _ => "Compte Épargne à intérêt élevé" }).FontSize(7.5f);
                        x.Item().Text(a == BncAccount.Card ? "************1234" : "00-000-00").FontSize(10).Bold();
                    });
                    if (a != BncAccount.Card)
                        r.RelativeItem().Column(x => { x.Item().Text("No de compte"); x.Item().Text("0000-1").FontSize(10).Bold(); });
                    r.RelativeItem().Column(x => { x.Item().Text("Solde actuel"); x.Item().Text(Money(withBalance ? balances[^1] : 312.15m, false)).FontSize(10).Bold(); });
                    r.RelativeItem().Column(x => { x.Item().Text(a == BncAccount.Card ? "Crédit disponible" : "Solde disponible"); x.Item().Text(Money(withBalance ? balances[^1] : 4687.85m, false)).FontSize(10).Bold(); });
                });
                col.Item().PaddingTop(14).Text("Transactions passées").FontSize(7.5f);
                col.Item().Text("Alex Sample").FontSize(10).Bold();
                col.Item().PaddingTop(16).Text("Historique de mes transactions").FontSize(9.5f).Bold();
                col.Item().PaddingTop(8).Table(t =>
                {
                    t.ColumnsDefinition(cd =>
                    {
                        cd.ConstantColumn(75); cd.RelativeColumn(1.6f); cd.RelativeColumn(1.2f); cd.ConstantColumn(80);
                        if (withBalance) cd.ConstantColumn(80);
                    });
                    t.Header(h =>
                    {
                        h.Cell().PaddingVertical(4).Text("Date").SemiBold();
                        h.Cell().PaddingVertical(4).Text("Description").SemiBold();
                        h.Cell().PaddingVertical(4).Text("Catégorie").SemiBold();
                        h.Cell().PaddingVertical(4).AlignRight().Text("Montant").SemiBold();
                        if (withBalance) h.Cell().PaddingVertical(4).AlignRight().Text("Solde").SemiBold();
                    });
                    foreach (var (l, bal) in rows)
                    {
                        IContainer Cell(IContainer x) => x.BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(4);
                        t.Cell().Element(Cell).Text(l.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        t.Cell().Element(Cell).Text(l.Description);
                        t.Cell().Element(Cell).Text(l.Category);
                        t.Cell().Element(Cell).AlignRight().Text(Money(l.Amount, true));
                        if (withBalance) t.Cell().Element(Cell).AlignRight().Text(Money(bal, false));
                    }
                });
            });
        })).GeneratePdf();
    }

    /// <summary>The CSV export, newest operation first, with the bank's own quoting.</summary>
    public static byte[] Csv(Month m, BncAccount a)
    {
        static string Q(string s) => s.Contains(' ') || s.Contains('!') || s.Contains('\'') ? $"\"{s}\"" : s;
        static string N(decimal v) => $"\"{v.ToString("0.0#", CultureInfo.InvariantCulture)}\"";
        var sb = new StringBuilder();
        var lines = m.Of(a);
        var balances = new List<decimal>();
        var b = m.Opening(a);
        foreach (var l in lines) { b += l.Amount; balances.Add(b); }
        sb.Append(a == BncAccount.Card ? "Date;\"Numero de Carte\";Description;Categorie;Debit;Credit\n" : "Date;Description;Categorie;Debit;Credit;Solde\n");
        foreach (var (l, bal) in lines.Zip(balances).Reverse())
        {
            var debit = l.Amount < 0 ? -l.Amount : 0;
            var credit = l.Amount > 0 ? l.Amount : 0;
            var date = $"\"{l.Date:yyyy-MM-dd}\"";
            sb.Append(a == BncAccount.Card
                ? $"{date};\"************1234\";{Q(l.Description)};{Q(l.Category)};{N(debit)};{N(credit)}\n"
                : $"{date};{Q(l.Description)};{Q(l.Category)};{N(debit)};{N(credit)};\"{bal.ToString("0.00", CultureInfo.InvariantCulture)}\"\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static string FileName(BncAccount a, string ext) => a switch
    {
        BncAccount.Card => $"2025-10-01-120000.{ext}",
        BncAccount.Chequing => $"2025-10-01-120100-debit.{ext}",
        _ => $"2025-10-01-120200-highinvacc.{ext}",
    };
}
