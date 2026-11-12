using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Fictitious statements in the layouts of Revolut (an online bank holding several currencies in one account), for
/// tests: the PDF statement of one currency ("EUR Statement", a "Balance summary" table, then "Account transactions
/// from ... to ...": Date | Description | Money out | Money in | Balance, dates as "3 Jan 2026", amounts with their
/// sign of currency, grey detail lines under a description (card, reference, rate, fee), then the "Reverted" table of
/// operations that never moved money) and the CSV export of the app (Type, Product, Started Date, Completed Date,
/// Description, Amount, Fee, Currency, State, Balance: dates with their time, the fee in a column of its own, pending
/// and reverted lines without a balance, every currency and product in the one file), in English or in French, and
/// the export of a business account. The person, the numbers and the amounts are invented.
/// </summary>
public static class RevolutGenerator
{
    /// <param name="Fee">What the bank took, besides the amount (the balance loses both).</param>
    /// <param name="State">COMPLETED, or PENDING / REVERTED / DECLINED: the money did not move.</param>
    public sealed record Line(DateOnly Date, string Type, string Description, decimal Amount, decimal Fee = 0, string State = "COMPLETED", string Detail = "");

    public sealed record Account(string Product, string Currency, decimal Opening, List<Line> Lines)
    {
        public List<Line> Done => [.. Lines.Where(l => l.State == "COMPLETED")];
        public decimal Closing => Opening + Done.Sum(l => l.Amount - l.Fee);
        public decimal MoneyOut => Done.Sum(l => (l.Amount < 0 ? -l.Amount : 0) + l.Fee);
        public decimal MoneyIn => Done.Where(l => l.Amount > 0).Sum(l => l.Amount);
    }

    private static readonly (string Type, string Description, string Detail, decimal Min, decimal Max)[] Spending =
    [
        ("CARD_PAYMENT", "Lidl", "Card: 516732******1234", 6.20m, 84.10m),
        ("CARD_PAYMENT", "Boulangerie Exemple", "Card: 516732******1234", 1.80m, 12.50m),
        ("CARD_PAYMENT", "Uber", "Card: 516732******1234", 7.40m, 31.00m),
        ("CARD_PAYMENT", "Spotify", "Card: 516732******1234", 10.99m, 10.99m),
        ("CARD_PAYMENT", "Example Rail", "Card: 516732******1234", 14.00m, 96.00m),
        ("TRANSFER", "To Jordan Example", "Reference: dinner", 10.00m, 150.00m),
    ];

    /// <summary>A month of a current account in euros, another in dollars and a savings pocket in euros.</summary>
    public static List<Account> Sample(int year = 2026, int month = 1, int count = 26, int seed = 5)
    {
        var rng = new Random(seed);
        var start = new DateOnly(year, month, 1);
        var days = start.AddMonths(1).AddDays(-1).Day;
        var eur = new List<Line>
        {
            new(start.AddDays(1), "TOPUP", "Apple Pay Top-Up by *4321", 600.00m),
            new(start.AddDays(4), "TRANSFER", "From Example Corp", 2140.55m, Detail: "Reference: salary"),
            new(start.AddDays(6), "ATM", "Cash at Example Bank ATM", -200.00m, Fee: 4.00m),
            new(start.AddDays(8), "EXCHANGE", "Exchanged to USD", -500.00m, Fee: 2.50m, Detail: "Revolut Rate €1.00 = $1.0810 (ECB rate €1.00 = $1.0800)"),
            new(start.AddDays(9), "TRANSFER", "To pocket Holidays", -300.00m),
            new(start.AddDays(12), "CARD_PAYMENT", "Hotel Imaginaire", -180.00m, State: "REVERTED", Detail: "Card: 516732******1234"),
            new(start.AddDays(15), "CARD_REFUND", "Refund from Example Rail", 23.40m),
            new(start.AddDays(20), "FEE", "Premium plan fee", 0m, Fee: 7.99m),
            new(start.AddDays(days - 1), "CARD_PAYMENT", "Librairie Fictive", -19.90m, State: "PENDING", Detail: "Card: 516732******1234"),
        };
        for (var i = 0; i < count; i++)
        {
            var s = Spending[rng.Next(Spending.Length)];
            var amount = s.Min == s.Max ? s.Min : Math.Round(s.Min + (decimal)rng.NextDouble() * (s.Max - s.Min), 2);
            eur.Add(new Line(start.AddDays(rng.Next(2, days - 1)), s.Type, s.Description, -amount, Detail: s.Detail));
        }
        var usd = new List<Line>
        {
            new(start.AddDays(8), "EXCHANGE", "Exchanged from EUR", 540.50m),
            new(start.AddDays(10), "CARD_PAYMENT", "Example Books NYC", -42.17m, Detail: "Card: 516732******1234"),
            new(start.AddDays(11), "CARD_PAYMENT", "Sample Diner", -31.05m, State: "DECLINED"),
            new(start.AddDays(18), "CARD_PAYMENT", "Metro Card Example", -33.00m, Fee: 0.33m),
        };
        var pocket = new List<Line>
        {
            new(start.AddDays(9), "TRANSFER", "To pocket Holidays", 300.00m),
            new(start.AddDays(25), "INTEREST", "Interest earned", 0.41m),
        };
        return
        [
            new Account("Current", "EUR", 412.36m, [.. eur.OrderBy(l => l.Date)]),
            new Account("Current", "USD", 0m, usd),
            new Account("Savings", "EUR", 1500.00m, pocket),
        ];
    }

    private static string N(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The export of the app: every product and currency in one file, by the time each operation was completed.</summary>
    public static byte[] Csv(List<Account> accounts, bool french = false)
    {
        var sb = new StringBuilder(french
            ? "Type,Produit,Date de début,Date de fin,Description,Montant,Frais,Devise,État,Solde\n"
            : "Type,Product,Started Date,Completed Date,Description,Amount,Fee,Currency,State,Balance\n");
        string State(string s) => !french ? s : s switch { "COMPLETED" => "TERMINÉ", "PENDING" => "EN ATTENTE", "REVERTED" => "ANNULÉ", _ => "REFUSÉ" };
        string Product(string p) => !french ? p : p == "Savings" ? "Épargne" : "Actuel";
        var rows = new List<(DateOnly Date, int Account, int Order, string Text)>();
        for (var a = 0; a < accounts.Count; a++)
        {
            var acc = accounts[a];
            var balance = acc.Opening;
            for (var i = 0; i < acc.Lines.Count; i++)
            {
                var l = acc.Lines[i];
                var done = l.State == "COMPLETED";
                if (done) balance += l.Amount - l.Fee;
                var started = $"{l.Date:yyyy-MM-dd} {8 + i % 12:00}:{(i * 7) % 60:00}:{(i * 13) % 60:00}";
                var completed = l.State == "PENDING" ? "" : $"{l.Date.AddDays(l.Type == "CARD_PAYMENT" && done ? 1 : 0):yyyy-MM-dd} {9 + i % 12:00}:{(i * 11) % 60:00}:{(i * 17) % 60:00}";
                var description = l.Description.Contains(',') ? $"\"{l.Description}\"" : l.Description;
                rows.Add((l.Date, a, i, $"{l.Type},{Product(acc.Product)},{started},{completed},{description},{N(l.Amount)},{N(l.Fee)},{acc.Currency},{State(l.State)},{(done ? N(balance) : "")}"));
            }
        }
        foreach (var r in rows.OrderBy(r => r.Date).ThenBy(r => r.Account).ThenBy(r => r.Order)) sb.Append(r.Text).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>The export of a business account: the amount before conversion beside the amount paid, the fee, the account named on each line.</summary>
    public static byte[] BusinessCsv(Account acc)
    {
        var sb = new StringBuilder("Date started (UTC),Date completed (UTC),ID,Type,State,Description,Reference,Payer,Card number,Orig currency,Orig amount,Payment currency,Amount,Total amount,Exchange rate,Fee,Fee currency,Balance,Account,Beneficiary account number\n");
        var balance = acc.Opening;
        for (var i = 0; i < acc.Lines.Count; i++)
        {
            var l = acc.Lines[i];
            var done = l.State == "COMPLETED";
            if (done) balance += l.Amount - l.Fee;
            // Bought abroad: the amount in the shop's currency (another number) before the amount paid.
            var orig = l.Type == "CARD_PAYMENT" && i % 3 == 0 ? ("GBP", N(Math.Round(l.Amount * 0.86m, 2))) : (acc.Currency, N(l.Amount));
            sb.Append(CultureInfo.InvariantCulture, $"{l.Date:yyyy-MM-dd},{(l.State == "PENDING" ? "" : $"{l.Date:yyyy-MM-dd}")},{Guid.Empty.ToString()[..8]}-{i:0000},{l.Type},{l.State},{l.Description},,Alex Sample,,{orig.Item1},{orig.Item2},{acc.Currency},{N(l.Amount)},{N(l.Amount - l.Fee)},,{N(l.Fee)},{acc.Currency},{(done ? N(balance) : "")},Main,\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string Sign(string currency) => currency switch { "EUR" => "€", "USD" => "$", "GBP" => "£", _ => currency + " " };
    private static string Money(decimal v, string currency) => (v < 0 ? "-" : "") + Sign(currency) + Math.Abs(v).ToString("#,##0.00", CultureInfo.InvariantCulture);
    private static string Day(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    private static string LongDay(DateOnly d) => d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The PDF statement of one currency of the account.</summary>
    public static byte[] Pdf(Account acc)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var start = new DateOnly(acc.Lines[0].Date.Year, acc.Lines[0].Date.Month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var c = acc.Currency;
        static IContainer Head(IContainer x) => x.BorderBottom(0.8f).PaddingVertical(4);
        static IContainer Cell(IContainer x) => x.BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(4);
        return Document.Create(d => d.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(36);
            page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(8.5f));
            page.Header().Row(r =>
            {
                r.RelativeItem().Text("Revolut").FontSize(20).Bold();
                r.AutoItem().AlignRight().Column(a =>
                {
                    a.Item().AlignRight().Text($"{c} Statement").FontSize(13).Bold();
                    a.Item().AlignRight().Text($"Generated on the {Day(end.AddDays(2))}");
                    a.Item().AlignRight().Text("Revolut Bank UAB");
                });
            });
            page.Content().PaddingTop(12).Column(col =>
            {
                col.Item().Row(r =>
                {
                    r.RelativeItem().Column(a =>
                    {
                        a.Item().Text("ALEX SAMPLE").Bold();
                        a.Item().Text("12 rue de l'Exemple");
                        a.Item().Text("75000 Sampleville");
                    });
                    r.AutoItem().Column(a =>
                    {
                        a.Item().Text("IBAN  LT00 0000 0000 0000 0000");
                        a.Item().Text("BIC  REVOLT21");
                    });
                });
                col.Item().PaddingTop(16).Text("Balance summary").FontSize(12).Bold();
                col.Item().PaddingTop(4).Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.ConstantColumn(90); cd.ConstantColumn(80); cd.ConstantColumn(80); cd.ConstantColumn(90); });
                    foreach (var h in new[] { "Product", "Opening balance", "Money out", "Money in", "Closing balance" })
                        (h == "Product" ? t.Cell().Element(Head) : t.Cell().Element(Head).AlignRight()).Text(h).Bold();
                    foreach (var name in new[] { $"Account ({acc.Product} Account)", "Total" })
                    {
                        t.Cell().Element(Cell).Text(name);
                        t.Cell().Element(Cell).AlignRight().Text(Money(acc.Opening, c));
                        t.Cell().Element(Cell).AlignRight().Text(Money(acc.MoneyOut, c));
                        t.Cell().Element(Cell).AlignRight().Text(Money(acc.MoneyIn, c));
                        t.Cell().Element(Cell).AlignRight().Text(Money(acc.Closing, c));
                    }
                });
                col.Item().PaddingTop(4).Text("The balance on your statement might differ from the balance shown in your app. The statement balance only reflects completed transactions.").FontSize(7).FontColor(Colors.Grey.Darken1);

                col.Item().PaddingTop(16).Text($"Account transactions from {LongDay(start)} to {LongDay(end)}").FontSize(12).Bold();
                col.Item().PaddingTop(4).Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.ConstantColumn(70); cd.RelativeColumn(); cd.ConstantColumn(80); cd.ConstantColumn(80); cd.ConstantColumn(80); });
                    t.Header(h =>
                    {
                        h.Cell().Element(Head).Text("Date").Bold();
                        h.Cell().Element(Head).Text("Description").Bold();
                        h.Cell().Element(Head).AlignRight().Text("Money out").Bold();
                        h.Cell().Element(Head).AlignRight().Text("Money in").Bold();
                        h.Cell().Element(Head).AlignRight().Text("Balance").Bold();
                    });
                    var balance = acc.Opening;
                    foreach (var l in acc.Done)
                    {
                        // The fee is part of the money out; a line under the description says how much it was.
                        var moved = l.Amount - l.Fee;
                        balance += moved;
                        t.Cell().Element(Cell).Text(Day(l.Date));
                        t.Cell().Element(Cell).Column(x =>
                        {
                            x.Item().Text(l.Description);
                            if (l.Detail.Length > 0) x.Item().Text(l.Detail).FontSize(7.5f).FontColor(Colors.Grey.Darken1);
                            if (l.Fee != 0) x.Item().Text($"Fee: {Money(l.Fee, c)}").FontSize(7.5f).FontColor(Colors.Grey.Darken1);
                        });
                        t.Cell().Element(Cell).AlignRight().Text(moved < 0 ? Money(-moved, c) : "");
                        t.Cell().Element(Cell).AlignRight().Text(moved > 0 ? Money(moved, c) : "");
                        t.Cell().Element(Cell).AlignRight().Text(Money(balance, c));
                    }
                });

                var undone = acc.Lines.Where(l => l.State == "REVERTED").ToList();
                if (undone.Count > 0)
                {
                    col.Item().PaddingTop(16).Text($"Reverted from {LongDay(start)} to {LongDay(end)}").FontSize(12).Bold();
                    col.Item().PaddingTop(4).Table(t =>
                    {
                        t.ColumnsDefinition(cd => { cd.ConstantColumn(70); cd.RelativeColumn(); cd.ConstantColumn(80); cd.ConstantColumn(80); });
                        t.Cell().Element(Head).Text("Start date").Bold();
                        t.Cell().Element(Head).Text("Description").Bold();
                        t.Cell().Element(Head).AlignRight().Text("Money out").Bold();
                        t.Cell().Element(Head).AlignRight().Text("Money in").Bold();
                        foreach (var l in undone)
                        {
                            t.Cell().Element(Cell).Text(Day(l.Date));
                            t.Cell().Element(Cell).Text(l.Description);
                            t.Cell().Element(Cell).AlignRight().Text(l.Amount < 0 ? Money(-l.Amount, c) : "");
                            t.Cell().Element(Cell).AlignRight().Text(l.Amount > 0 ? Money(l.Amount, c) : "");
                        }
                    });
                }
            });
            page.Footer().Column(f =>
            {
                f.Item().Text("Report lost or stolen card  +370 5 214 3608          Get help directly in app").FontSize(7).FontColor(Colors.Grey.Darken1);
                f.Item().Text("Revolut Bank UAB is a credit institution licensed in the Republic of Lithuania. Deposits are protected up to €100,000.").FontSize(7).FontColor(Colors.Grey.Darken1);
                f.Item().AlignRight().Text(x => { x.Span("Page ").FontSize(7); x.CurrentPageNumber().FontSize(7); x.Span(" of ").FontSize(7); x.TotalPages().FontSize(7); });
            });
        })).GeneratePdf();
    }
}
