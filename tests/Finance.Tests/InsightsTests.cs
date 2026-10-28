using System.Globalization;
using System.Text;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Reports;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// Recurring payments, movements From / To and category scope, checked over many generated months
/// (fictitious data in the Banque Nationale CSV layout), never on one hand-picked example.
/// </summary>
public class InsightsTests
{
    private sealed record Line(DateOnly Date, string Label, string Category, decimal Amount);

    /// <summary>A Banque Nationale style chequing export (newest first, running balance), from a list of lines.</summary>
    private static byte[] ChequingCsv(IEnumerable<Line> lines, decimal opening)
    {
        var ordered = lines.OrderBy(l => l.Date).ToList();
        var rows = new List<string>();
        var balance = opening;
        foreach (var l in ordered)
        {
            balance += l.Amount;
            rows.Add(string.Join(';',
                Q(l.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), Q(l.Label), Q(l.Category),
                Q(l.Amount < 0 ? (-l.Amount).ToString(CultureInfo.InvariantCulture) : "0"),
                Q(l.Amount > 0 ? l.Amount.ToString(CultureInfo.InvariantCulture) : "0"),
                Q(balance.ToString("0.00", CultureInfo.InvariantCulture))));
        }
        rows.Reverse();
        return Encoding.UTF8.GetBytes("Date;Description;Categorie;Debit;Credit;Solde\n" + string.Join('\n', rows) + "\n");
        static string Q(string s) => "\"" + s + "\"";
    }

    /// <summary>
    /// A year of a fictitious chequing account: rent on the 1st-5th, insurance, phone, salary twice a month, groceries,
    /// a café at the same price every month, transfers to savings and to another person.
    /// </summary>
    private static List<Line> Year(int seed)
    {
        var rng = new Random(seed);
        var lines = new List<Line>();
        for (var m = 1; m <= 12; m++)
        {
            var rentDay = 1 + seed % 5;
            lines.Add(new(new DateOnly(2026, m, rentDay), "Rent Prop Mgmt", "Hypotheque et loyer", -(1200 + seed * 10)));
            lines.Add(new(new DateOnly(2026, m, rentDay), "Assurance Habitation Desjardins", "Assurances", -42.17m));
            lines.Add(new(new DateOnly(2026, m, 12), "Videotron", "Internet", -(61.50m + (m % 3) * 0.25m)));
            lines.Add(new(new DateOnly(2026, m, 14), "Employeur Inc", "Revenus", 2100.55m));
            lines.Add(new(new DateOnly(2026, m, 28), "Employeur Inc", "Revenus", 2100.55m + (m == 12 ? 300 : 0)));
            lines.Add(new(new DateOnly(2026, m, 9), "Cafe Olimpico", "Cafes", -4.75m));
            for (var g = 0; g < 4; g++)
                lines.Add(new(new DateOnly(2026, m, 6 + g * 5), "Super C", "Epicerie", -Math.Round((decimal)(20 + rng.NextDouble() * 80), 2)));
            lines.Add(new(new DateOnly(2026, m, 20), "Virement INTERAC", "Transfert", -Math.Round((decimal)(10 + rng.NextDouble() * 50), 2)));
        }
        return lines;
    }

    private static async Task<CommitResult> ImportCsv(TempStore s, byte[] csv, string name, long? account, AccountKind kind = AccountKind.Bank)
    {
        var draft = await s.Imports.ParseAsync(name, csv);
        s.Imports.Choose(draft.Id, account, account is null ? kind : null);
        return s.Imports.Commit(new CommitRequest(draft.Id, account, null, "CAD", account is null ? kind : null, []));
    }

    [Fact]
    public async Task Recurring_payments_and_income_are_found_and_everyday_purchases_are_not()
    {
        using var s = new TempStore();
        var all = Year(2).Where(l => l.Date.Month <= 4).ToList();
        await ImportCsv(s, ChequingCsv(all, 3000), "jan-apr.csv", null);
        for (var m = 1; m <= 4; m++)
        {
            var sum = s.Summaries.Summarize(new DateOnly(2026, m, 1), Formatting.MonthEnd(2026, m), Granularity.Month, "CAD");
            var recurring = sum.Transactions.Where(t => t.Recurring is not null).Select(t => t.Brand).Distinct().ToList();
            Assert.Contains(recurring, b => b.StartsWith("Rent", StringComparison.Ordinal));
            Assert.Contains(recurring, b => b.Contains("Desjardins", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(recurring, b => b.StartsWith("Vid", StringComparison.OrdinalIgnoreCase) && b.EndsWith("otron", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(recurring, b => b.Contains("Employeur", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recurring, b => b.Contains("Super C", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recurring, b => b.Contains("Olimpico", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(sum.Transactions, t => t.Recurring is not null && Categories.IsTransfer(t.CategoryId));
            Assert.Equal(-sum.Transactions.Where(t => t.Recurring is not null && t.Amount < 0).Sum(t => t.Amount), sum.RecurringExpenses);
            Assert.Equal(sum.Transactions.Where(t => t.Recurring is not null && t.Amount > 0).Sum(t => t.Amount), sum.RecurringIncome);
            Assert.Equal(sum.RecurringExpenses, sum.RecurringExpensePie.Sum(x => x.Amount));
            Assert.Equal(4201.10m, sum.RecurringIncome);
            // Subscription burden: the recurring payments without the rent, and its share of earnings.
            var rent = -sum.Transactions.Where(t => t.Recurring is not null && t.CategoryId == "housing").Sum(t => t.Amount);
            Assert.True(rent > 0);
            Assert.Equal(sum.RecurringExpenses - rent, sum.RecurringWithoutHousing);
            Assert.Equal((double)(sum.RecurringWithoutHousing / sum.Earnings), sum.RecurringWithoutHousingShare!.Value, 9);
            Assert.Equal((double)(sum.Expenses / sum.Earnings), sum.ExpensesShare!.Value, 9);
        }
        var year = s.Summaries.Summarize(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), Granularity.Year, "CAD");
        Assert.Equal(4 * 4201.10m, year.RecurringIncome);
    }

    [Fact]
    public async Task Movements_name_both_accounts_and_the_outside()
    {
        using var s = new TempStore();
        var chq = new List<Line>
        {
            new(new DateOnly(2026, 3, 2), "Transfert entre comptes", "Transfert", -250m),
            new(new DateOnly(2026, 3, 5), "Paiement Mastercard", "Paiement carte de credit", -80.10m),
            new(new DateOnly(2026, 3, 9), "Virement INTERAC", "Transfert", -35m),
            new(new DateOnly(2026, 3, 10), "Paiement de facture", "Investissements", -500m),
            new(new DateOnly(2026, 3, 15), "Employeur Inc", "Revenus", 2000m),
        };
        var sav = new List<Line> { new(new DateOnly(2026, 3, 2), "Transfert entre comptes", "Transfert", 250m) };
        var c = await ImportCsv(s, ChequingCsv(chq, 1000), "chq.csv", null);
        s.Db.UpdateAccount(new Account { Id = c.AccountId, Name = "Chequing", Currency = "CAD", Kind = AccountKind.Bank });
        var v = await ImportCsv(s, ChequingCsv(sav, 5000), "sav.csv", null, AccountKind.Savings);
        s.Db.UpdateAccount(new Account { Id = v.AccountId, Name = "Savings", Currency = "CAD", Kind = AccountKind.Savings });
        var card = "Date;\"Numero de Carte\";Description;Categorie;Debit;Credit\n\"2026-03-05\";\"************1111\";\"Paiement recu merci!\";\"Paiement carte de credit\";\"0\";\"80.10\"\n\"2026-03-04\";\"************1111\";IGA;Epicerie;\"80.10\";\"0\"\n";
        var k = await ImportCsv(s, Encoding.UTF8.GetBytes(card), "card.csv", null, AccountKind.CreditCard);
        s.Db.UpdateAccount(new Account { Id = k.AccountId, Name = "Card", Currency = "CAD", Kind = AccountKind.CreditCard });

        var sum = s.Summaries.Summarize(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), Granularity.Month, "CAD");
        Assert.Contains(sum.Movements, m => m.From == "Chequing" && m.To == "Savings" && m.Amount == 250m && !m.FromOutside && !m.ToOutside);
        Assert.Contains(sum.Movements, m => m.From == "Chequing" && m.To == "Card" && m.Amount == 80.10m && m.OtherId is not null);
        Assert.Contains(sum.Movements, m => m.From == "Chequing" && m.To == Movements.Outside && m.Amount == 35m && m.ToOutside);
        Assert.Contains(sum.Movements, m => m.From == "Chequing" && m.To == Movements.OutsideInvestments && m.Amount == 500m);
        // Each movement once: four movements from six transfer lines (two pairs, two single halves).
        Assert.Equal(4, sum.Movements.Count);
        Assert.Equal(sum.Movements.Sum(m => m.Amount), sum.Flows.Sum(f => f.Amount));
        // The card purchase of the same amount stays spending.
        Assert.Equal(80.10m, sum.Expenses);
        // Diagram: one box per own account (name, then type), one outside blob naming who is behind it,
        // one arrow per direction between two places, each with its head pointing at the destination.
        var d = sum.Diagram;
        Assert.Equal(["Card", "Chequing", "Savings"], d.Nodes.Where(n => !n.Outside).Select(n => n.Name).Order());
        Assert.Equal("Credit card", d.Nodes.Single(n => n.Name == "Card").Type);
        Assert.Equal("Savings account", d.Nodes.Single(n => n.Name == "Savings").Type);
        var blob = d.Nodes.Single(n => n.Outside);
        Assert.Contains(blob.Parties, x => x.Name == "Investments" && x.Sent == 500m);
        Assert.Equal(535m, blob.Parties.Sum(x => x.Sent));
        Assert.Equal(sum.Flows.Select(f => (f.FromOutside ? "out" : f.From, f.ToOutside ? "out" : f.To)).Distinct().Count(), d.Arrows.Count);
        Assert.Contains(d.Arrows, a => a.From == "Chequing" && a.To == "Savings" && a.Amount == 250m && !a.Outside);
        Assert.Contains(d.Arrows, a => a.From == "Chequing" && a.To == FlowDiagram.OutsideId && a.Amount == 535m && a.Outside);
        Assert.All(d.Arrows, a => { Assert.StartsWith("M", a.Path); Assert.EndsWith("Z", a.Head); });
        Assert.All(d.Nodes, n => Assert.InRange(n.Y + n.H / 2, 0, d.Height));
        Assert.Equal("Chequing account", sum.Movements.First(m => m.From == "Chequing").FromType);
        Assert.Null(sum.Movements.First(m => m.ToOutside).ToType);

        var pdf = ReportBuilder.Build(sum, "DD month YYYY", new DateTime(2026, 4, 1));
        Assert.True(pdf.Length > 1000);
    }

    [Fact]
    public async Task Category_change_for_one_operation_or_for_the_whole_brand()
    {
        using var s = new TempStore();
        var lines = Enumerable.Range(1, 6).Select(m => new Line(new DateOnly(2026, m, 3), "Boutique Zeta", "Magasinage", -19.99m - m)).ToList();
        await ImportCsv(s, ChequingCsv(lines, 1000), "a.csv", null);
        var txs = s.Db.AllTransactions();

        var changed = s.Transactions.SetCategory(txs[0].Id, "gifts", allOfBrand: false);
        Assert.Equal(0, changed);
        Assert.Single(s.Db.AllTransactions(), t => t.CategoryId == "gifts");

        changed = s.Transactions.SetCategory(txs[1].Id, "entertainment", allOfBrand: true);
        Assert.Equal(5, changed);
        Assert.All(s.Db.AllTransactions(), t => Assert.Equal("entertainment", t.CategoryId));
        // Future operations of the brand follow.
        var later = new List<Line> { new(new DateOnly(2026, 7, 3), "Boutique Zeta", "Magasinage", -5m) };
        await ImportCsv(s, ChequingCsv(later, 2000), "b.csv", null);
        Assert.All(s.Db.AllTransactions(), t => Assert.Equal("entertainment", t.CategoryId));
    }

    [Fact]
    public void Pie_colours_stay_distinct_for_many_slices()
    {
        var colours = Enumerable.Range(0, 40).Select(Palette.Series).ToList();
        Assert.Equal(40, colours.Distinct().Count());
        Assert.All(colours, c => Assert.Matches("^#[0-9a-f]{6}$", c));
    }

    [Fact]
    public void Every_day_of_every_month_is_a_bar()
    {
        using var s = new TempStore();
        for (var m = 1; m <= 12; m++)
        {
            var sum = s.Summaries.Summarize(new DateOnly(2027, m, 1), Formatting.MonthEnd(2027, m), Granularity.Month, "CAD");
            Assert.Equal(Enumerable.Range(1, DateTime.DaysInMonth(2027, m)).Select(d => d.ToString(CultureInfo.InvariantCulture)), sum.Series.Select(b => b.Label));
        }
    }
}
