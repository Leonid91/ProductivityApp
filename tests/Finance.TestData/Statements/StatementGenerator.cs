using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Makes fictitious bank and card statements for tests and demos. Every bank, person, address and
/// account number here is invented; no statement belongs to anyone.
/// </summary>
public static class StatementGenerator
{
    public enum Style
    {
        /// <summary>French bank: Date | Libellé | Débit | Crédit | Solde, "1 234,56", dd/mm dates.</summary>
        FrenchDebitCredit,
        /// <summary>English bank: Date | Description | Withdrawals | Deposits | Balance, "1,234.56", "Oct 03" dates.</summary>
        EnglishWithdrawalsDeposits,
        /// <summary>English credit card: Trans. date | Post. date | Description | Amount, payments marked CR.</summary>
        EnglishCard,
        /// <summary>French bank with one signed amount column and a value date: Date opé. | Date valeur | Libellé | Montant.</summary>
        FrenchSignedAmount,
        /// <summary>Unusual: label first, then amount, then a day-month date at the right, headers no reader knows.</summary>
        Atypical,
    }

    public sealed record FakeTransaction(DateOnly Date, string Label, decimal Amount, string? SecondLine = null);

    public sealed record Spec(
        Style Style, string BankName, DateOnly Start, DateOnly End, decimal Opening, List<FakeTransaction> Transactions,
        string Currency = "CAD")
    {
        public decimal Closing => Style == Style.EnglishCard
            ? Opening - Transactions.Sum(t => t.Amount)
            : Opening + Transactions.Sum(t => t.Amount);
    }

    private static readonly (string Label, decimal Min, decimal Max, string? Second)[] EnglishExpenses =
    [
        ("TIM HORTONS #2345 MONTREAL QC", 2.15m, 14.80m, null),
        ("IGA EXTRA 08123 LAVAL QC", 23.40m, 168.90m, null),
        ("NETFLIX.COM 866-579-7172 ON", 18.99m, 18.99m, null),
        ("UBER *TRIP HELP.UBER.COM", 9.50m, 38.75m, null),
        ("AMZN MKTP CA*2K3J49QW1", 12.99m, 142.35m, "WWW.AMAZON.CA ON"),
        ("SAQ SELECTION 23045", 17.50m, 64.20m, null),
        ("PHARMAPRIX #0123", 6.49m, 58.10m, null),
        ("STM-METRO BERRI-UQAM", 3.75m, 97.00m, null),
        ("HYDRO-QUEBEC", 54.32m, 131.18m, "PAIEMENT FACTURE"),
        ("COSTCO WHOLESALE #503", 64.00m, 289.45m, null),
        ("SHELL C12345 BROSSARD", 38.20m, 86.40m, null),
        ("SPOTIFY P0A1B2C3D4", 11.99m, 11.99m, null),
        ("CINEPLEX ODEON FORUM", 14.25m, 41.80m, null),
        ("RESTAURANT LE PETIT BISTRO", 24.60m, 88.15m, null),
        ("BOULANGERIE DU COIN", 4.25m, 19.60m, null),
        ("VIDEOTRON LTEE", 72.45m, 72.45m, null),
        ("RETRAIT GUICHET 4412", 20.00m, 120.00m, null),
        ("FRAIS MENSUELS DU COMPTE", 4.95m, 4.95m, null),
        ("DOLLARAMA #1187", 3.45m, 27.60m, null),
        ("STARBUCKS 01234 TORONTO ON", 4.85m, 9.70m, null),
    ];

    private static readonly (string Label, decimal Min, decimal Max, string? Second)[] FrenchExpenses =
    [
        ("CB CARREFOUR MARKET", 18.40m, 132.60m, "PARIS 11"),
        ("CB MONOPRIX", 6.20m, 54.80m, null),
        ("PRLV SEPA EDF", 61.33m, 98.71m, "REF CLIENT 0000000000"),
        ("CB SNCF INTERNET", 19.00m, 112.00m, null),
        ("CB BOULANGERIE PAUL", 3.40m, 12.90m, null),
        ("CB FNAC", 14.99m, 189.00m, null),
        ("PRLV SEPA FREE MOBILE", 19.99m, 19.99m, null),
        ("RETRAIT DAB", 20.00m, 110.00m, null),
        ("CB RATP NAVIGO", 86.40m, 86.40m, null),
        ("CB PHARMACIE CENTRALE", 7.80m, 42.15m, null),
        ("FRAIS TENUE DE COMPTE", 2.50m, 2.50m, null),
        ("CB RESTAURANT LE ZINC", 22.50m, 76.00m, null),
        ("PRLV SEPA MAIF ASSURANCE", 38.12m, 38.12m, null),
        ("CB LIDL", 12.30m, 64.90m, null),
    ];

    public static Spec Sample(Style style, int year = 2025, int month = 10, int count = 34, int seed = 7)
    {
        var rng = new Random(seed);
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var french = style is Style.FrenchDebitCredit or Style.FrenchSignedAmount or Style.Atypical;
        var pool = french ? FrenchExpenses : EnglishExpenses;
        var list = new List<FakeTransaction>();
        var days = Enumerable.Range(0, count).Select(_ => rng.Next(0, end.Day)).OrderBy(x => x).ToList();
        foreach (var d in days)
        {
            var p = pool[rng.Next(pool.Length)];
            var amount = p.Min == p.Max ? p.Min : Math.Round(p.Min + (decimal)rng.NextDouble() * (p.Max - p.Min), 2);
            list.Add(new FakeTransaction(start.AddDays(d), p.Label, -amount, rng.Next(4) == 0 ? p.Second : null));
        }
        if (style == Style.EnglishCard)
        {
            list.Insert(list.Count / 2, new FakeTransaction(start.AddDays(14), "PAYMENT - THANK YOU", 750.00m));
            list.Add(new FakeTransaction(end.AddDays(-2), "AMZN MKTP CA REFUND", 23.99m));
            list = list.OrderBy(t => t.Date).ToList();
            return new Spec(style, "Fabrikam Card Services", start, end, 812.47m, list);
        }
        // Income: two pay days and a transfer received.
        list.Add(new FakeTransaction(start.AddDays(4), french ? "VIR SEPA RECU SALAIRE EXEMPLE SAS" : "PAYROLL DEPOSIT EXAMPLE CORP", french ? 1845.20m : 2140.55m));
        list.Add(new FakeTransaction(start.AddDays(18), french ? "VIR SEPA RECU SALAIRE EXEMPLE SAS" : "PAYROLL DEPOSIT EXAMPLE CORP", french ? 1845.20m : 2140.55m));
        list.Add(new FakeTransaction(start.AddDays(9), french ? "VIREMENT RECU DE A. SAMPLE" : "E-TRANSFER RECEIVED A SAMPLE", 120.00m));
        list.Add(new FakeTransaction(start.AddDays(2), french ? "PRLV SEPA LOYER SCI EXEMPLE" : "RENT PAYMENT SAMPLE PROPERTIES", french ? -925.00m : -1350.00m));
        list = list.OrderBy(t => t.Date).ThenBy(t => t.Amount).ToList();
        var bank = style switch
        {
            Style.FrenchDebitCredit => "Banque Exemple",
            Style.FrenchSignedAmount => "Caisse Fictive",
            Style.Atypical => "Mutuelle Imaginaire",
            _ => "Northwind Bank",
        };
        return new Spec(style, bank, start, end, french ? 1532.18m : 2861.04m, list, french ? "EUR" : "CAD");
    }

    // ---------------------------------------------------------------- rendering

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-CA");

    private static string FrMoney(decimal v) => v.ToString("#,##0.00", Fr).Replace(' ', ' ').Replace(' ', ' ');
    private static string EnMoney(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<byte[]>> Rendered = new(StringComparer.Ordinal);

    /// <summary>
    /// The statement printed as a PDF. A statement of the same content is drawn once per test run: every caller gets the
    /// same bytes, which nobody writes to.
    /// </summary>
    public static byte[] Render(Spec spec) => Rendered.GetOrAdd(Key(spec), _ => new Lazy<byte[]>(() => Draw(spec))).Value;

    /// <summary>Everything a statement prints from: two specs of the same key print the same PDF.</summary>
    private static string Key(Spec s) => string.Join('|', s.Style, s.BankName, s.Start, s.End, s.Opening.ToString(CultureInfo.InvariantCulture), s.Currency,
        string.Join(';', s.Transactions.Select(t => string.Join('/', t.Date.DayNumber, t.Label, t.Amount.ToString(CultureInfo.InvariantCulture), t.SecondLine))));

    private static byte[] Draw(Spec spec)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(42);
            page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(8.5f));
            page.Header().Element(h => Header(h, spec));
            page.Content().PaddingTop(12).Element(b => Body(b, spec));
            page.Footer().AlignRight().Text(t => { t.Span(spec.Style is Style.EnglishCard or Style.EnglishWithdrawalsDeposits ? "Page " : "Page "); t.CurrentPageNumber(); t.Span(" / "); t.TotalPages(); });
        }))
        // A fixed date in the file: the same statement rendered twice is byte for byte the same file, as a real one is.
        .WithMetadata(new DocumentMetadata { CreationDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ModifiedDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) })
        .GeneratePdf();
    }

    private static void Header(IContainer h, Spec s)
    {
        h.Column(col =>
        {
            col.Item().Row(r =>
            {
                r.RelativeItem().Text(s.BankName).FontSize(16).Bold();
                r.AutoItem().AlignRight().Column(a =>
                {
                    a.Item().Text("Alex Sample");
                    a.Item().Text("123 Example Street");
                    a.Item().Text("Sampletown");
                });
            });
            switch (s.Style)
            {
                case Style.FrenchDebitCredit:
                    col.Item().PaddingTop(8).Text("Relevé de compte courant");
                    col.Item().Text("Compte n° 00012 34567 8901");
                    col.Item().Text($"Période du {s.Start.ToString("d MMMM yyyy", Fr)} au {s.End.ToString("d MMMM yyyy", Fr)}");
                    col.Item().PaddingTop(4).Text($"Solde précédent au {s.Start.AddDays(-1).ToString("dd/MM/yyyy", Fr)} : {FrMoney(s.Opening)} €");
                    break;
                case Style.FrenchSignedAmount:
                    col.Item().PaddingTop(8).Text("Relevé d'opérations");
                    col.Item().Text("Compte de dépôt n° 98765 43210 5678");
                    col.Item().Text($"Du {s.Start.ToString("dd/MM/yyyy", Fr)} au {s.End.ToString("dd/MM/yyyy", Fr)}");
                    break;
                case Style.Atypical:
                    col.Item().PaddingTop(8).Text("Votre relevé mensuel");
                    col.Item().Text($"Mois de {s.Start.ToString("MMMM yyyy", Fr)}");
                    col.Item().Text("Contrat 55 66 77 8899");
                    break;
                case Style.EnglishCard:
                    col.Item().PaddingTop(8).Text("Credit card statement");
                    col.Item().Text("Card number 4500 **** **** 1234");
                    col.Item().Text($"Statement period: {s.Start.ToString("MMM d, yyyy", En)} to {s.End.ToString("MMM d, yyyy", En)}");
                    col.Item().Text("Credit limit $5,000.00     Minimum payment $10.00");
                    col.Item().PaddingTop(4).Text($"Previous balance ${EnMoney(s.Opening)}");
                    col.Item().Text($"New balance ${EnMoney(s.Closing)}");
                    break;
                default:
                    col.Item().PaddingTop(8).Text("Chequing account statement");
                    col.Item().Text("Account number 01234-5678901");
                    col.Item().Text($"Statement period: {s.Start.ToString("MMMM d, yyyy", En)} to {s.End.ToString("MMMM d, yyyy", En)}");
                    break;
            }
        });
    }

    private static void Body(IContainer b, Spec s)
    {
        switch (s.Style)
        {
            case Style.FrenchDebitCredit: FrenchDebitCreditTable(b, s); break;
            case Style.EnglishWithdrawalsDeposits: EnglishBankTable(b, s); break;
            case Style.EnglishCard: CardTable(b, s); break;
            case Style.FrenchSignedAmount: SignedTable(b, s); break;
            case Style.Atypical: AtypicalTable(b, s); break;
        }
    }

    private static IContainer Cell(IContainer c) => c.BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(2.5f).PaddingHorizontal(3);
    private static IContainer Head(IContainer c) => c.Background(Colors.Grey.Lighten3).PaddingVertical(4).PaddingHorizontal(3);

    private static void Label(IContainer c, FakeTransaction t) => c.Column(col =>
    {
        col.Item().Text(t.Label);
        if (t.SecondLine is not null) col.Item().Text(t.SecondLine).FontSize(7.5f);
    });

    private static void FrenchDebitCreditTable(IContainer b, Spec s)
    {
        b.Table(t =>
        {
            t.ColumnsDefinition(c => { c.ConstantColumn(48); c.RelativeColumn(); c.ConstantColumn(66); c.ConstantColumn(66); c.ConstantColumn(72); });
            t.Header(h =>
            {
                h.Cell().Element(Head).Text("Date").Bold();
                h.Cell().Element(Head).Text("Libellé de l'opération").Bold();
                h.Cell().Element(Head).AlignRight().Text("Débit").Bold();
                h.Cell().Element(Head).AlignRight().Text("Crédit").Bold();
                h.Cell().Element(Head).AlignRight().Text("Solde").Bold();
            });
            t.Cell().Element(Cell).Text(s.Start.ToString("dd/MM", Fr));
            t.Cell().Element(Cell).Text("SOLDE PRECEDENT");
            t.Cell().Element(Cell); t.Cell().Element(Cell);
            t.Cell().Element(Cell).AlignRight().Text(FrMoney(s.Opening));
            var bal = s.Opening;
            foreach (var x in s.Transactions)
            {
                bal += x.Amount;
                t.Cell().Element(Cell).Text(x.Date.ToString("dd/MM", Fr));
                t.Cell().Element(Cell).Element(c => Label(c, x));
                t.Cell().Element(Cell).AlignRight().Text(x.Amount < 0 ? FrMoney(-x.Amount) : "");
                t.Cell().Element(Cell).AlignRight().Text(x.Amount > 0 ? FrMoney(x.Amount) : "");
                t.Cell().Element(Cell).AlignRight().Text(FrMoney(bal));
            }
            t.Cell().Element(Cell).Text(s.End.ToString("dd/MM", Fr));
            t.Cell().Element(Cell).Text("NOUVEAU SOLDE").Bold();
            t.Cell().Element(Cell); t.Cell().Element(Cell);
            t.Cell().Element(Cell).AlignRight().Text(FrMoney(s.Closing)).Bold();
        });
    }

    private static void EnglishBankTable(IContainer b, Spec s)
    {
        b.Column(col =>
        {
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(c => { c.ConstantColumn(46); c.RelativeColumn(); c.ConstantColumn(72); c.ConstantColumn(72); c.ConstantColumn(76); });
                t.Header(h =>
                {
                    h.Cell().Element(Head).Text("Date").Bold();
                    h.Cell().Element(Head).Text("Description").Bold();
                    h.Cell().Element(Head).AlignRight().Text("Withdrawals ($)").Bold();
                    h.Cell().Element(Head).AlignRight().Text("Deposits ($)").Bold();
                    h.Cell().Element(Head).AlignRight().Text("Balance ($)").Bold();
                });
                t.Cell().Element(Cell).Text(s.Start.ToString("MMM dd", En).Replace(".", ""));
                t.Cell().Element(Cell).Text("Opening balance");
                t.Cell().Element(Cell); t.Cell().Element(Cell);
                t.Cell().Element(Cell).AlignRight().Text(EnMoney(s.Opening));
                var bal = s.Opening;
                // Balance printed once per day, on the day's last row, like many Canadian banks.
                for (var i = 0; i < s.Transactions.Count; i++)
                {
                    var x = s.Transactions[i];
                    bal += x.Amount;
                    var lastOfDay = i == s.Transactions.Count - 1 || s.Transactions[i + 1].Date != x.Date;
                    var firstOfDay = i == 0 || s.Transactions[i - 1].Date != x.Date;
                    t.Cell().Element(Cell).Text(firstOfDay ? x.Date.ToString("MMM dd", En).Replace(".", "") : "");
                    t.Cell().Element(Cell).Element(c => Label(c, x));
                    t.Cell().Element(Cell).AlignRight().Text(x.Amount < 0 ? EnMoney(-x.Amount) : "");
                    t.Cell().Element(Cell).AlignRight().Text(x.Amount > 0 ? EnMoney(x.Amount) : "");
                    t.Cell().Element(Cell).AlignRight().Text(lastOfDay ? EnMoney(bal) : "");
                }
                t.Cell().Element(Cell).Text(s.End.ToString("MMM dd", En).Replace(".", ""));
                t.Cell().Element(Cell).Text("Closing balance").Bold();
                t.Cell().Element(Cell); t.Cell().Element(Cell);
                t.Cell().Element(Cell).AlignRight().Text(EnMoney(s.Closing)).Bold();
            });
        });
    }

    private static void CardTable(IContainer b, Spec s)
    {
        b.Table(t =>
        {
            t.ColumnsDefinition(c => { c.ConstantColumn(52); c.ConstantColumn(52); c.RelativeColumn(); c.ConstantColumn(80); });
            t.Header(h =>
            {
                h.Cell().Element(Head).Text("Trans. date").Bold();
                h.Cell().Element(Head).Text("Post. date").Bold();
                h.Cell().Element(Head).Text("Description").Bold();
                h.Cell().Element(Head).AlignRight().Text("Amount ($)").Bold();
            });
            foreach (var x in s.Transactions)
            {
                t.Cell().Element(Cell).Text(x.Date.ToString("MMM d", En).Replace(".", ""));
                t.Cell().Element(Cell).Text(x.Date.AddDays(1).ToString("MMM d", En).Replace(".", ""));
                t.Cell().Element(Cell).Element(c => Label(c, x));
                t.Cell().Element(Cell).AlignRight().Text(x.Amount > 0 ? EnMoney(x.Amount) + " CR" : EnMoney(-x.Amount));
            }
        });
    }

    private static void SignedTable(IContainer b, Spec s)
    {
        b.Column(col =>
        {
            col.Item().PaddingBottom(6).Text($"Solde au {s.Start.ToString("dd/MM/yyyy", Fr)}    {FrMoney(s.Opening)} EUR");
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(c => { c.ConstantColumn(56); c.ConstantColumn(56); c.RelativeColumn(); c.ConstantColumn(80); });
                t.Header(h =>
                {
                    h.Cell().Element(Head).Text("Date opé.").Bold();
                    h.Cell().Element(Head).Text("Date valeur").Bold();
                    h.Cell().Element(Head).Text("Libellé").Bold();
                    h.Cell().Element(Head).AlignRight().Text("Montant").Bold();
                });
                foreach (var x in s.Transactions)
                {
                    t.Cell().Element(Cell).Text(x.Date.ToString("dd/MM/yyyy", Fr));
                    t.Cell().Element(Cell).Text(x.Date.AddDays(1).ToString("dd/MM/yyyy", Fr));
                    t.Cell().Element(Cell).Element(c => Label(c, x));
                    t.Cell().Element(Cell).AlignRight().Text(x.Amount < 0 ? "-" + FrMoney(-x.Amount) : "+" + FrMoney(x.Amount));
                }
            });
            col.Item().PaddingTop(6).Text($"Solde au {s.End.ToString("dd/MM/yyyy", Fr)}    {FrMoney(s.Closing)} EUR");
        });
    }

    private static void AtypicalTable(IContainer b, Spec s)
    {
        b.Column(col =>
        {
            col.Item().PaddingBottom(6).Text($"Ancien solde : {FrMoney(s.Opening)}");
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(c => { c.RelativeColumn(); c.ConstantColumn(70); c.ConstantColumn(70); c.ConstantColumn(50); });
                t.Header(h =>
                {
                    h.Cell().Element(Head).Text("Quoi").Bold();
                    h.Cell().Element(Head).AlignRight().Text("Sorties").Bold();
                    h.Cell().Element(Head).AlignRight().Text("Rentrées").Bold();
                    h.Cell().Element(Head).AlignRight().Text("Quand").Bold();
                });
                foreach (var x in s.Transactions)
                {
                    t.Cell().Element(Cell).Element(c => Label(c, x));
                    t.Cell().Element(Cell).AlignRight().Text(x.Amount < 0 ? FrMoney(-x.Amount) : "");
                    t.Cell().Element(Cell).AlignRight().Text(x.Amount > 0 ? FrMoney(x.Amount) : "");
                    t.Cell().Element(Cell).AlignRight().Text(x.Date.ToString("dd.MM", Fr));
                }
            });
            col.Item().PaddingTop(6).Text($"Nouveau solde : {FrMoney(s.Closing)}");
        });
    }

    /// <summary>Turns each page into a picture inside a new PDF, like a paper statement put through a scanner.</summary>
    public static byte[] AsScan(byte[] pdf)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var count = UglyToad.PdfPig.PdfDocument.Open(pdf).NumberOfPages;
        var images = Enumerable.Range(0, count).Select(i => Pdf.PdfReader.RenderPagePngAsync(pdf, i, 1700).GetAwaiter().GetResult()).ToList();
        return Document.Create(c =>
        {
            foreach (var img in images)
                c.Page(p => { p.Size(PageSizes.Letter); p.Margin(0); p.Content().Image(img).FitArea(); });
        }).WithMetadata(new DocumentMetadata { CreationDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ModifiedDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }).GeneratePdf();
    }
}
