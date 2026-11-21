using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Fictitious statements in the layouts of Scotiabank (a large Canadian bank), for tests: the chequing statement
/// ("Here's what happened in your account": Date | Transactions | Amounts withdrawn | Amounts deposited | Balance,
/// dates without a year, a two-line description, a balance on every line, negative balances, several pages), the
/// credit card statement (REF.# | TRANS. DATE | POST DATE | DETAILS | AMOUNT($), credits written "57.96-", the summary
/// in a panel beside the table, a period over two months) and the CSV exports of online banking (the older one
/// without a header, the newer one with Filter / Sub-description / Type of Transaction). The person, the address,
/// the account numbers, the employer and the amounts are invented.
/// </summary>
public static class ScotiaGenerator
{
    public sealed record Line(DateOnly Date, string Type, string Detail, decimal Amount);

    public sealed record Statement(DateOnly Start, DateOnly End, decimal Opening, List<Line> Lines)
    {
        public decimal Closing => Opening + Lines.Sum(l => l.Amount);
        public decimal Withdrawals => -Lines.Where(l => l.Amount < 0).Sum(l => l.Amount);
        public decimal Deposits => Lines.Where(l => l.Amount > 0).Sum(l => l.Amount);
    }

    private static readonly (string Type, string Detail, decimal Min, decimal Max)[] Spending =
    [
        ("Point of sale purchase", "Tim Hortons #3126 Brampton ONCD", 2.35m, 14.80m),
        ("Point of sale purchase", "Sobeys #4521 Brampton ON", 23.40m, 168.90m),
        ("Point of sale purchase", "Opos 13.52 Example Telecom Ottawa ON", 13.52m, 13.52m),
        ("Point of sale purchase", "Shoppers Drug Mart #1234 Toronto ON", 6.49m, 58.10m),
        ("ABM withdrawal", "Sample Mall 24-7 Sampletown ON", 20.00m, 120.00m),
        ("Withdrawal", "1234567 MB-Email Money Trf", 25.00m, 300.00m),
        ("Service charge", "MB-Email Money Trf", 1.00m, 1.00m),
        ("Bill payment", "Hydro One Networks", 54.32m, 131.18m),
        ("Insurance", "Example Life Assurance", 38.12m, 38.12m),
        ("Credit Card/LOC payment", "", 95.00m, 400.00m),
        ("Point of sale purchase", "Uber Canada/Ubertrip Toronto ON", 9.50m, 38.75m),
    ];

    public static Statement Chequing(int year = 2026, int month = 1, int count = 44, int seed = 3)
    {
        var rng = new Random(seed);
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var lines = new List<Line>
        {
            new(start.AddDays(10), "Credit memo", "Example Corp Wire Payment", 6300.00m),
            new(start.AddDays(10), "Service charge", "11112222 33334444 ScotiaWire/MTS Fee", -15.00m),
            new(start.AddDays(11), "Point of sale purchase", "Example Furniture Brampton ON", -5925.00m),
            new(start.AddDays(11), "Deposit", "76543210 MB-Email Money Trf", 100.00m),
            new(start.AddDays(14), "Payroll dep.", "Example Corp", 2140.55m),
            new(start.AddDays(24), "Interest", "", 0.42m),
        };
        for (var i = 0; i < count; i++)
        {
            var s = Spending[rng.Next(Spending.Length)];
            var amount = s.Min == s.Max ? s.Min : Math.Round(s.Min + (decimal)rng.NextDouble() * (s.Max - s.Min), 2);
            lines.Add(new Line(start.AddDays(rng.Next(0, end.Day)), s.Type, s.Detail, -amount));
        }
        // Banks list a day's operations in the order they were posted; the balance can go below zero.
        return new Statement(start, end, -5.80m, lines.OrderBy(l => l.Date).ToList());
    }

    private static string Money(decimal v) => (v < 0 ? "-" : "") + Math.Abs(v).ToString("#,##0.00", CultureInfo.InvariantCulture);
    private static string Dollars(decimal v) => (v < 0 ? "-$" : "$") + Math.Abs(v).ToString("#,##0.00", CultureInfo.InvariantCulture);
    private static string Day(DateOnly d) => d.ToString("MMM d", CultureInfo.InvariantCulture);

    public static byte[] ChequingPdf(Statement s)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(40);
            page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(8.5f));
            page.Header().Column(h =>
            {
                h.Item().Row(r =>
                {
                    r.RelativeItem().Text("Scotiabank").FontSize(18).Bold();
                    r.AutoItem().AlignRight().Column(a =>
                    {
                        a.Item().Text("Your account number:");
                        a.Item().Text("00000 12345 67").Bold();
                    });
                });
            });
            page.Content().PaddingTop(10).Column(col =>
            {
                col.Item().Text("ALEX SAMPLE");
                col.Item().Text("123 Example Street");
                col.Item().Text("Sampletown, ON  A1A 1A1");
                col.Item().PaddingTop(14).Text("Your Basic Banking Plan account summary").FontSize(13).Bold();
                col.Item().PaddingTop(6).Width(330).Column(sum =>
                {
                    sum.Item().Row(r => { r.RelativeItem().Text($"Opening Balance {s.Start.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)}").Bold(); r.AutoItem().Text(Dollars(s.Opening)).Bold(); });
                    sum.Item().Row(r => { r.RelativeItem().PaddingLeft(10).Text("Minus total withdrawals"); r.AutoItem().Text(Dollars(s.Withdrawals)); });
                    sum.Item().Row(r => { r.RelativeItem().PaddingLeft(10).Text("Plus total deposits"); r.AutoItem().Text(Dollars(s.Deposits)); });
                    sum.Item().BorderTop(0.6f).Row(r => { r.RelativeItem().Text($"Closing Balance on {s.End.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)}").Bold(); r.AutoItem().Text(Dollars(s.Closing)).Bold(); });
                });
                col.Item().PaddingTop(14).Text("Here's what happened in your account this statement period").FontSize(11).Bold();
                col.Item().PaddingTop(6).Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.ConstantColumn(42); cd.RelativeColumn(); cd.ConstantColumn(78); cd.ConstantColumn(78); cd.ConstantColumn(70); });
                    static IContainer Head(IContainer x) => x.BorderBottom(0.6f).PaddingVertical(3);
                    static IContainer Cell(IContainer x) => x.BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten1).PaddingVertical(2.5f);
                    t.Header(hd =>
                    {
                        hd.Cell().Element(Head).Text("Date");
                        hd.Cell().Element(Head).Text("Transactions");
                        hd.Cell().Element(Head).AlignRight().Column(x => { x.Item().AlignRight().Text("Amounts"); x.Item().AlignRight().Text("withdrawn ($)"); });
                        hd.Cell().Element(Head).AlignRight().Column(x => { x.Item().AlignRight().Text("Amounts"); x.Item().AlignRight().Text("deposited ($)"); });
                        hd.Cell().Element(Head).AlignRight().Text("Balance ($)");
                    });
                    t.Cell().Element(Cell).Text(Day(s.Start));
                    t.Cell().Element(Cell).Text("Opening Balance");
                    t.Cell().Element(Cell); t.Cell().Element(Cell);
                    t.Cell().Element(Cell).AlignRight().Text(Money(s.Opening));
                    var bal = s.Opening;
                    foreach (var l in s.Lines)
                    {
                        bal += l.Amount;
                        t.Cell().Element(Cell).Text(Day(l.Date));
                        t.Cell().Element(Cell).Column(d => { d.Item().Text(l.Type); if (l.Detail.Length > 0) d.Item().Text(l.Detail); });
                        t.Cell().Element(Cell).AlignRight().Text(l.Amount < 0 ? Money(-l.Amount) : "");
                        t.Cell().Element(Cell).AlignRight().Text(l.Amount > 0 ? Money(l.Amount) : "");
                        t.Cell().Element(Cell).AlignRight().Text(Money(bal));
                    }
                    t.Cell().Element(Cell).Text(Day(s.End));
                    t.Cell().Element(Cell).Text("Closing Balance");
                    t.Cell().Element(Cell); t.Cell().Element(Cell);
                    t.Cell().Element(Cell).AlignRight().Text(Money(s.Closing));
                });
            });
            page.Footer().Row(r =>
            {
                r.RelativeItem().AlignCenter().Text("Continued on next page");
                r.AutoItem().Text(t => { t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages(); });
            });
        })).WithMetadata(new DocumentMetadata { CreationDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ModifiedDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) })
        .GeneratePdf();
    }

    /// <summary>
    /// The chequing e-statement as Scotiabank prints it in 2026: the period runs from the last day of the month before,
    /// "Amounts" on a line of its own above "Date | Transactions | withdrawn ($) | deposited ($) | Balance ($)", the
    /// dollar sign apart from its number in the summary and on the closing line, and amounts under one dollar written
    /// without their 0 (".42").
    /// </summary>
    public static Statement EStatement(int year = 2026, int month = 8, int seed = 11)
    {
        var rng = new Random(seed);
        var start = new DateOnly(year, month, 1).AddDays(-1);
        var end = new DateOnly(year, month, 1).AddMonths(1).AddDays(-2);
        var lines = new List<Line>
        {
            new(start, "Point of sale purchase", "Sobeys #4521 Brampton ON", -64.18m),
            new(start.AddDays(9), "Deposit", "76543210 MB-Email Money Trf", 250.00m),
            new(start.AddDays(14), "Payroll dep.", "Example Corp", 2140.55m),
            new(start.AddDays(20), "Service charge", "MB-Email Money Trf", -0.50m),
            new(end, "Interest", "", 0.42m),
        };
        for (var i = 0; i < 14; i++)
        {
            var x = Spending[rng.Next(Spending.Length)];
            var amount = x.Min == x.Max ? x.Min : Math.Round(x.Min + (decimal)rng.NextDouble() * (x.Max - x.Min), 2);
            lines.Add(new Line(start.AddDays(rng.Next(1, end.DayNumber - start.DayNumber)), x.Type, x.Detail, -amount));
        }
        return new Statement(start, end, 1204.37m, lines.OrderBy(l => l.Date).ToList());
    }

    /// <summary>An amount as the e-statement prints it: under one dollar without its 0 (".42").</summary>
    private static string EMoney(decimal v)
    {
        var text = Money(v);
        return text.StartsWith("0.", StringComparison.Ordinal) ? text[1..] : text.StartsWith("-0.", StringComparison.Ordinal) ? "-" + text[2..] : text;
    }

    public static byte[] EStatementPdf(Statement s)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var en = CultureInfo.InvariantCulture;
        return Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.MarginHorizontal(73);
            page.MarginVertical(40);
            page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(9));
            page.Header().Text("Scotiabank").FontSize(18).Bold();
            page.Content().PaddingTop(150).Column(col =>
            {
                col.Item().Text("Your Preferred Package account summary").FontSize(14).Bold();
                col.Item().PaddingTop(12).Width(370).Column(sum =>
                {
                    void Line(string label, decimal v, bool bold, float indent) => sum.Item().Row(r =>
                    {
                        var t = r.RelativeItem().PaddingLeft(indent).Text(label);
                        var d = r.ConstantItem(20).Text("$");
                        var a = r.ConstantItem(50).AlignRight().Text(Money(v));
                        if (bold) { t.Bold(); d.Bold(); a.Bold(); }
                    });
                    Line($"Opening Balance on {s.Start.ToString("MMMM d, yyyy", en)}", s.Opening, true, 0);
                    Line("Minus total withdrawals", s.Withdrawals, false, 12);
                    Line("Plus total deposits", s.Deposits, false, 12);
                    Line($"Closing Balance on {s.End.ToString("MMMM d, yyyy", en)}", s.Closing, true, 0);
                });
                col.Item().PaddingTop(30).Text("Here's what happened in your account this statement period").FontSize(10).Bold();
                col.Item().PaddingTop(8).Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.ConstantColumn(40); cd.RelativeColumn(); cd.ConstantColumn(66); cd.ConstantColumn(66); cd.ConstantColumn(64); });
                    static IContainer Head(IContainer x) => x.PaddingVertical(1);
                    static IContainer Cell(IContainer x) => x.PaddingVertical(4);
                    t.Header(hd =>
                    {
                        hd.Cell().Element(Head); hd.Cell().Element(Head);
                        hd.Cell().Element(Head).AlignRight().Text("Amounts");
                        hd.Cell().Element(Head).AlignRight().Text("Amounts");
                        hd.Cell().Element(Head);
                        hd.Cell().Element(Head).Text("Date");
                        hd.Cell().Element(Head).Text("Transactions");
                        hd.Cell().Element(Head).AlignRight().Text("withdrawn ($)");
                        hd.Cell().Element(Head).AlignRight().Text("deposited ($)");
                        hd.Cell().Element(Head).AlignRight().Text("Balance ($)");
                    });
                    t.Cell().Element(Cell).Text(Day(s.Start));
                    t.Cell().Element(Cell).Text("Opening Balance");
                    t.Cell().Element(Cell); t.Cell().Element(Cell);
                    t.Cell().Element(Cell).AlignRight().Text(EMoney(s.Opening));
                    var bal = s.Opening;
                    foreach (var l in s.Lines)
                    {
                        bal += l.Amount;
                        t.Cell().Element(Cell).Text(Day(l.Date));
                        t.Cell().Element(Cell).Column(d => { d.Item().Text(l.Type); if (l.Detail.Length > 0) d.Item().Text(l.Detail); });
                        t.Cell().Element(Cell).AlignRight().Text(l.Amount < 0 ? EMoney(-l.Amount) : "");
                        t.Cell().Element(Cell).AlignRight().Text(l.Amount > 0 ? EMoney(l.Amount) : "");
                        t.Cell().Element(Cell).AlignRight().Text(EMoney(bal));
                    }
                    t.Cell().Element(Cell).Text(Day(s.End));
                    t.Cell().Element(Cell).Text("Closing Balance");
                    t.Cell().Element(Cell); t.Cell().Element(Cell);
                    t.Cell().Element(Cell).AlignRight().Row(r => { r.AutoItem().PaddingRight(6).Text("$"); r.AutoItem().Text(EMoney(s.Closing)); });
                });
            });
            page.Footer().AlignRight().Text(t => { t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages(); });
        })).WithMetadata(new DocumentMetadata { CreationDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), ModifiedDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) })
        .GeneratePdf();
    }

    /// <summary>A card statement: purchases are positive amounts, payments and returns are credits ("57.96-").</summary>
    public sealed record Card(DateOnly Start, DateOnly End, decimal Previous, List<(DateOnly Trans, DateOnly Post, string Detail, string? Second, decimal Charge)> Lines)
    {
        public decimal Credits => -Lines.Where(l => l.Charge < 0).Sum(l => l.Charge);
        public decimal Debits => Lines.Where(l => l.Charge > 0).Sum(l => l.Charge);
        public decimal New => Previous + Debits - Credits;
    }

    public static Card VisaCard(int year = 2026, int seed = 5)
    {
        var rng = new Random(seed);
        var start = new DateOnly(year, 4, 19);
        var end = new DateOnly(year, 5, 21);
        var lines = new List<(DateOnly, DateOnly, string, string?, decimal)>
        {
            (new DateOnly(year, 5, 14), new DateOnly(year, 5, 14), "PAYMENT-THANK YOU SCOTIABANK TRANSIT", "70409 SAMPLETOWN AB", -57.96m),
            (new DateOnly(year, 4, 25), new DateOnly(year, 4, 26), "CREDIT VOUCHER/RETURN", "EXAMPLE OUTFITTERS TORONTO ON", -70.00m),
        };
        string[] shops = ["TIM HORTONS #2345 CALGARY AB", "SOBEYS #4521 CALGARY AB", "NETFLIX.COM 866-579-7172 ON", "PETRO-CANADA 12345 AIRDRIE AB", "AMZN MKTP CA*2K3J49QW1 WWW.AMAZON.CA ON", "SHOPPERS DRUG MART #1234 CALGARY AB"];
        for (var i = 0; i < 16; i++)
        {
            var d = start.AddDays(rng.Next(0, end.DayNumber - start.DayNumber - 1));
            lines.Add((d, d.AddDays(1), shops[rng.Next(shops.Length)], null, Math.Round(5 + (decimal)rng.NextDouble() * 140, 2)));
        }
        lines.Add((end, end, "ANNUAL FEE", null, 39.00m));
        lines.Add((end, end, "INTEREST CHARGES-PURCHASE", null, 11.11m));
        return new Card(start, end, 4728.08m, lines.OrderBy(l => l.Item2).ThenBy(l => l.Item1).ToList());
    }

    public static byte[] CardPdf(Card s)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var en = CultureInfo.InvariantCulture;
        return Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(36);
            page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(7.5f));
            page.Content().Row(row =>
            {
                row.RelativeItem(55).Column(left =>
                {
                    left.Item().Text("Scotiabank").FontSize(18).Bold();
                    left.Item().PaddingTop(30).Text("ALEX SAMPLE");
                    left.Item().Text("PO BOX 1234");
                    left.Item().Text("SAMPLETOWN AB  T0C 0C0");
                    left.Item().PaddingTop(40).Text("This statement covers transactions posted to your account during the Statement Period.");
                    left.Item().PaddingTop(14).Text("Transactions since your last statement").FontSize(10).Bold();
                    left.Item().PaddingTop(4).Table(t =>
                    {
                        t.ColumnsDefinition(cd => { cd.ConstantColumn(24); cd.ConstantColumn(34); cd.ConstantColumn(34); cd.RelativeColumn(); cd.ConstantColumn(52); });
                        static IContainer Head(IContainer x) => x.BorderBottom(0.6f).PaddingVertical(2);
                        t.Header(hd =>
                        {
                            hd.Cell().Element(Head).Text("REF.#");
                            hd.Cell().Element(Head).Text("TRANS. DATE");
                            hd.Cell().Element(Head).Text("POST DATE");
                            hd.Cell().Element(Head).Text("DETAILS");
                            hd.Cell().Element(Head).AlignRight().Text("AMOUNT($)");
                        });
                        var n = 0;
                        foreach (var l in s.Lines)
                        {
                            n++;
                            t.Cell().PaddingVertical(2).Text(n.ToString("000", en));
                            t.Cell().PaddingVertical(2).Text(l.Trans.ToString("MMM d", en));
                            t.Cell().PaddingVertical(2).Text(l.Post.ToString("MMM d", en));
                            t.Cell().PaddingVertical(2).Column(d => { d.Item().Text(l.Detail); if (l.Second is not null) d.Item().Text(l.Second); });
                            t.Cell().PaddingVertical(2).AlignRight().Text(l.Charge < 0 ? Math.Abs(l.Charge).ToString("#,##0.00", en) + "-" : l.Charge.ToString("#,##0.00", en));
                        }
                    });
                    left.Item().PaddingTop(4).Row(r => { r.RelativeItem().Text("SUB-TOTAL CREDITS"); r.AutoItem().Text("$" + s.Credits.ToString("#,##0.00", en) + "-"); });
                    left.Item().Row(r => { r.RelativeItem().Text("SUB-TOTAL DEBITS"); r.AutoItem().Text("$" + s.Debits.ToString("#,##0.00", en)); });
                });
                row.ConstantItem(18);
                row.RelativeItem(40).Column(right =>
                {
                    right.Item().Row(r => { r.RelativeItem().Text("Statement Period").Bold(); r.AutoItem().Text($"{s.Start.ToString("MMM d, yyyy", en)} - {s.End.ToString("MMM d, yyyy", en)}"); });
                    right.Item().Row(r => { r.RelativeItem().Text("Statement date").Bold(); r.AutoItem().Text(s.End.ToString("MMM d, yyyy", en)); });
                    right.Item().Row(r => { r.RelativeItem().Text("Account #").Bold(); r.AutoItem().Text("4500 000 000 000 123"); });
                    right.Item().PaddingTop(8).Text("Scotiabank Value VISA* card").FontSize(13).Bold();
                    right.Item().PaddingTop(10).Row(r => { r.RelativeItem().Text("Payment due date").Bold(); r.AutoItem().Text(s.End.AddDays(27).ToString("MMM d, yyyy", en)); });
                    right.Item().Row(r => { r.RelativeItem().Text("Total minimum payment").Bold(); r.AutoItem().Text("$64.80").Bold(); });
                    right.Item().PaddingTop(6).Row(r => { r.RelativeItem().Text($"Previous balance, {s.Start.AddDays(-1).ToString("MMM d/yy", en)}"); r.AutoItem().Text("$" + s.Previous.ToString("#,##0.00", en)); });
                    right.Item().Row(r => { r.RelativeItem().Text("Payments/credits      -"); r.AutoItem().Text("$" + s.Credits.ToString("#,##0.00", en)); });
                    right.Item().Row(r => { r.RelativeItem().Text("Purchases/charges     +"); r.AutoItem().Text("$" + s.Debits.ToString("#,##0.00", en)); });
                    right.Item().PaddingTop(4).Row(r => { r.RelativeItem().Text("New balance           =").Bold(); r.AutoItem().Text("$" + s.New.ToString("#,##0.00", en)).Bold(); });
                    right.Item().PaddingTop(4).Row(r => { r.RelativeItem().Text("Credit limit"); r.AutoItem().Text("$5,500.00"); });
                    right.Item().Row(r => { r.RelativeItem().Text("Credit available"); r.AutoItem().Text("$" + (5500 - s.New).ToString("#,##0.00", en)); });
                    right.Item().PaddingTop(60).Text("Interest Information").Bold();
                    right.Item().Text("Annual interest rates as of statement date:");
                    right.Item().Row(r => { r.RelativeItem().Text("Cash advances"); r.AutoItem().Text("12.99%"); });
                    right.Item().Row(r => { r.RelativeItem().Text("Purchases"); r.AutoItem().Text("12.99%"); });
                });
            });
        })).WithMetadata(new DocumentMetadata { CreationDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ModifiedDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) })
        .GeneratePdf();
    }

    /// <summary>The older online banking export: no header; Date, Amount, "-", Type, Description; newest first.</summary>
    public static byte[] OldCsv(Statement s)
    {
        var sb = new StringBuilder();
        foreach (var l in s.Lines.AsEnumerable().Reverse())
            sb.Append(CultureInfo.InvariantCulture, $"{l.Date.Month}/{l.Date.Day}/{l.Date.Year},{l.Amount.ToString("0.##", CultureInfo.InvariantCulture)},\"-\",\"{l.Type}\",\"{l.Detail}\"\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// The newer export: Filter, Date, Description, Sub-description, Type of Transaction, Amount, Balance; amounts
    /// without a sign, Debit or Credit saying which way the money went; newest first.
    /// </summary>
    public static byte[] NewCsv(Statement s)
    {
        var sb = new StringBuilder("Filter,Date,Description,Sub-description,Type of Transaction,Amount,Balance\r\n");
        var bal = s.Closing;
        foreach (var l in s.Lines.AsEnumerable().Reverse())
        {
            sb.Append(CultureInfo.InvariantCulture, $",{l.Date:yyyy-MM-dd},{l.Type},\"{l.Detail}\",{(l.Amount < 0 ? "Debit" : "Credit")},{Math.Abs(l.Amount).ToString("0.00", CultureInfo.InvariantCulture)},{bal.ToString("0.00", CultureInfo.InvariantCulture)}\r\n");
            bal -= l.Amount;
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
