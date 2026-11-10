using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// Statements imported in any order, any overlap and from any bank: every operation is stored once, never twice and
/// never lost, and the money moved between the person's accounts (of one bank, or of two banks in two currencies)
/// is one movement, out of the spending and the income, drawn as an arrow from one account to the other.
/// </summary>
/// <remarks>
/// The scenarios, one account:
/// <list type="number">
/// <item>two periods one after the other (1 January to 15 October, then 16 October to 10 November);</item>
/// <item>two statements sharing their last and first day;</item>
/// <item>part of a period, then the whole period (what is missing comes in, nothing twice), and the other way round;</item>
/// <item>two periods crossing each other, in either order;</item>
/// <item>a period inside another, in either order;</item>
/// <item>the same statement in another file (CSV export, other wording), and the very same file twice;</item>
/// <item>identical operations on one day (two coffees), counted one for one;</item>
/// <item>one day, one amount, two different operations;</item>
/// <item>the same operation dated differently in two files (purchase day, posting day), and two real purchases on both
/// sides of the end of a statement;</item>
/// <item>two crossing files opened together, saved one after the other;</item>
/// <item>a statement deleted, then imported again;</item>
/// <item>operations corrected by hand, then a crossing statement;</item>
/// <item>an operation entered by hand, then the statement holding it (both stay; entering the same one warns).</item>
/// </list>
/// Two banks: a transfer between them in one currency (paired whatever the order of the imports, waiting outside
/// while its other half is not imported, not paired twice by crossing statements), across the end of a month, two
/// transfers of one amount to two accounts, a transfer between two currencies (paired at the rate of its day, within the
/// banks' margin; never with a salary; waiting for the rates when there are none), two accounts with one name, and
/// every bank together adding up to the banks one by one.
/// </remarks>
[Collection("categories")]
public class ImportOverlapTests
{
    private static DateOnly D(int month, int day) => new(2026, month, day);

    /// <summary>A Canadian chequing account from 1 January to 10 November 2026: rent, salaries, groceries, a coffee, a phone bill.</summary>
    private static List<FakeTransaction> Year()
    {
        var list = new List<FakeTransaction>();
        for (var d = D(1, 1); d <= D(11, 10); d = d.AddDays(1))
        {
            if (d.Day == 1) list.Add(new(d, "RENT EXAMPLE PROPERTIES", -1400m));
            if (d.Day is 15 || d == new DateOnly(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month)))
                list.Add(new(d, "PAYROLL DEPOSIT EXAMPLE CORP", 2100m));
            if (d.DayNumber % 4 == 0) list.Add(new(d, "METRO #123 MONTREAL QC", -(40m + d.DayNumber % 37 + d.Day / 100m)));
            if (d.DayNumber % 9 == 0) list.Add(new(d, "CAFE EXEMPLE MONTREAL", -4.75m));
            if (d.Day == 20) list.Add(new(d, "TELEPHONE EXEMPLE INC", -65.50m));
        }
        return list;
    }

    private static List<FakeTransaction> Between(IEnumerable<FakeTransaction> all, DateOnly from, DateOnly to) =>
        [.. all.Where(t => t.Date >= from && t.Date <= to)];

    private static Spec Statement(string bank, List<FakeTransaction> txs, DateOnly from, DateOnly to, decimal opening = 1000m,
        Style style = Style.EnglishWithdrawalsDeposits, string currency = "CAD") =>
        new(style, bank, from, to, opening, txs, currency);

    /// <summary>The same operations as a bank's CSV export: "Date,Description,Amount", wording in its own case and form.</summary>
    private static byte[] Csv(IEnumerable<FakeTransaction> txs, Func<string, string>? wording = null)
    {
        var sb = new StringBuilder("Date,Description,Amount\n");
        foreach (var t in txs)
            sb.Append(CultureInfo.InvariantCulture, $"{t.Date:yyyy-MM-dd},\"{(wording ?? (l => l))(t.Label)}\",{t.Amount.ToString("0.00", CultureInfo.InvariantCulture)}\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Imports a file into an account (a new one at <paramref name="bank"/> when null), as the person does.</summary>
    private static async Task<CommitResult> Import(TempStore s, byte[] file, string name, long? account, long bank, string currency = "CAD",
        AccountKind kind = AccountKind.Bank, string? accountName = null)
    {
        var d = await s.Imports.ParseAsync(name, file);
        s.Imports.Choose(d.Id, account, account is null ? kind : null, bank);
        return s.Imports.Commit(new CommitRequest(d.Id, account, accountName, currency, account is null ? kind : null, [], BankId: account is null ? bank : null));
    }

    private static Task<CommitResult> Import(TempStore s, Spec spec, long? account, long bank, AccountKind kind = AccountKind.Bank, string? accountName = null) =>
        Import(s, Render(spec), $"{spec.BankName} {spec.Start:yyyy-MM-dd} {spec.End:yyyy-MM-dd}.pdf", account, bank, spec.Currency, kind, accountName);

    /// <summary>What one account holds: each operation by its day and amount, as many times as it is stored.</summary>
    private static List<(DateOnly, decimal)> Stored(TempStore s, long account) =>
        [.. s.Db.AllTransactions().Where(t => t.AccountId == account).Select(t => (t.Date, t.Amount)).Order()];

    private static List<(DateOnly, decimal)> Expected(IEnumerable<FakeTransaction> txs) => [.. txs.Select(t => (t.Date, t.Amount)).Order()];

    // ======================================================================================== one account

    [Fact]
    public async Task Two_periods_one_after_the_other_keep_every_operation_once()
    {
        using var s = new TempStore();
        var year = Year();
        var a = Between(year, D(1, 1), D(10, 15));
        var b = Between(year, D(10, 16), D(11, 10));
        var first = await Import(s, Statement("Northwind Bank", a, D(1, 1), D(10, 15)), null, s.Bank);
        var second = await Import(s, Statement("Northwind Bank", b, D(10, 16), D(11, 10), 1000m + a.Sum(t => t.Amount)), first.AccountId, s.Bank);
        Assert.Equal((a.Count, 0), (first.Added, first.SkippedDuplicates));
        Assert.Equal((b.Count, 0), (second.Added, second.SkippedDuplicates));
        Assert.Equal(Expected(year), Stored(s, first.AccountId));
        // The totals of the whole time: every purchase once, every salary once.
        var sum = s.Summaries.Summarize(D(1, 1), D(11, 10), Granularity.Range, "CAD");
        Assert.Equal(-year.Where(t => t.Amount < 0).Sum(t => t.Amount), sum.Expenses);
        Assert.Equal(year.Where(t => t.Amount > 0).Sum(t => t.Amount), sum.Earnings);
    }

    [Fact]
    public async Task Two_statements_sharing_a_day_hold_that_day_once()
    {
        using var s = new TempStore();
        var year = Year();
        var day = D(10, 15);
        Assert.Contains(year, t => t.Date == day);
        var first = await Import(s, Statement("Northwind Bank", Between(year, D(9, 1), day), D(9, 1), day), null, s.Bank);
        var second = await Import(s, Statement("Northwind Bank", Between(year, day, D(10, 31)), day, D(10, 31)), first.AccountId, s.Bank);
        Assert.Equal(year.Count(t => t.Date == day), second.SkippedDuplicates);
        Assert.Equal(Expected(Between(year, D(9, 1), D(10, 31))), Stored(s, first.AccountId));
    }

    [Fact]
    public async Task Part_of_a_period_then_the_whole_period_adds_only_what_is_missing()
    {
        using var s = new TempStore();
        var october = Between(Year(), D(10, 1), D(10, 31));
        // An export of the first twenty days, one line in three left out (a partial download).
        var part = Between(october, D(10, 1), D(10, 20)).Where((_, i) => i % 3 != 0).ToList();
        var first = await Import(s, Csv(part), "export.csv", null, s.Bank);
        var full = await Import(s, Statement("Northwind Bank", october, D(10, 1), D(10, 31)), first.AccountId, s.Bank);
        Assert.Equal(part.Count, full.SkippedDuplicates);
        Assert.Equal(october.Count - part.Count, full.Added);
        Assert.Equal(Expected(october), Stored(s, first.AccountId));
    }

    [Fact]
    public async Task The_whole_period_then_part_of_it_adds_nothing()
    {
        using var s = new TempStore();
        var october = Between(Year(), D(10, 1), D(10, 31));
        var full = await Import(s, Statement("Northwind Bank", october, D(10, 1), D(10, 31)), null, s.Bank);
        var part = Between(october, D(10, 5), D(10, 25)).Where((_, i) => i % 2 == 0).ToList();
        var again = await Import(s, Csv(part), "export.csv", full.AccountId, s.Bank);
        Assert.Equal((0, part.Count), (again.Added, again.SkippedDuplicates));
        Assert.Equal(Expected(october), Stored(s, full.AccountId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_crossing_periods_in_either_order_hold_their_common_days_once(bool laterFirst)
    {
        using var s = new TempStore();
        var year = Year();
        var a = Statement("Northwind Bank", Between(year, D(9, 1), D(10, 20)), D(9, 1), D(10, 20));
        var b = Statement("Northwind Bank", Between(year, D(10, 1), D(11, 10)), D(10, 1), D(11, 10));
        var (one, two) = laterFirst ? (b, a) : (a, b);
        var first = await Import(s, one, null, s.Bank);
        var second = await Import(s, two, first.AccountId, s.Bank);
        Assert.Equal(Between(year, D(10, 1), D(10, 20)).Count, second.SkippedDuplicates);
        Assert.Equal(Expected(Between(year, D(9, 1), D(11, 10))), Stored(s, first.AccountId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_period_inside_another_in_either_order(bool insideFirst)
    {
        using var s = new TempStore();
        var year = Year();
        var outer = Statement("Northwind Bank", Between(year, D(1, 1), D(10, 31)), D(1, 1), D(10, 31));
        var inner = Statement("Northwind Bank", Between(year, D(3, 1), D(4, 30)), D(3, 1), D(4, 30));
        var first = await Import(s, insideFirst ? inner : outer, null, s.Bank);
        var second = await Import(s, insideFirst ? outer : inner, first.AccountId, s.Bank);
        Assert.Equal(insideFirst ? outer.Transactions.Count - inner.Transactions.Count : 0, second.Added);
        Assert.Equal(inner.Transactions.Count, second.SkippedDuplicates);
        Assert.Equal(Expected(outer.Transactions), Stored(s, first.AccountId));
    }

    [Fact]
    public async Task The_same_statement_in_another_file_or_twice_adds_nothing()
    {
        using var s = new TempStore();
        var october = Between(Year(), D(10, 1), D(10, 31));
        var spec = Statement("Northwind Bank", october, D(10, 1), D(10, 31));
        var first = await Import(s, spec, null, s.Bank);
        // The very same file: refused.
        var again = await s.Imports.ParseAsync("again.pdf", Render(spec));
        Assert.True(again.AlreadyImported);
        Assert.Throws<InvalidOperationException>(() => s.Imports.Commit(new CommitRequest(again.Id, first.AccountId, null, "CAD", null, [])));
        // The bank's CSV export of the same month, its wording shorter and in another case: nothing new.
        var export = await Import(s, Csv(october, l => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(l.ToLowerInvariant()).Replace(" Montreal Qc", "").Replace(" Montreal", "")),
            "export.csv", first.AccountId, s.Bank);
        Assert.Equal((0, october.Count), (export.Added, export.SkippedDuplicates));
        Assert.Equal(Expected(october), Stored(s, first.AccountId));
    }

    [Fact]
    public async Task Identical_operations_on_one_day_are_counted_one_for_one()
    {
        using var s = new TempStore();
        var day = D(10, 7);
        var coffee = new FakeTransaction(day, "CAFE EXEMPLE MONTREAL", -4.75m);
        var month = new List<FakeTransaction> { new(D(10, 2), "METRO #123 MONTREAL QC", -52.10m), coffee, coffee, new(D(10, 12), "PAYROLL DEPOSIT EXAMPLE CORP", 2100m) };
        // An export holding one of the two coffees, then the statement holding both: the second coffee comes in.
        var first = await Import(s, Csv([month[0], coffee]), "export.csv", null, s.Bank);
        var full = await Import(s, Statement("Northwind Bank", month, D(10, 1), D(10, 31)), first.AccountId, s.Bank);
        Assert.Equal((2, 2), (full.Added, full.SkippedDuplicates));
        Assert.Equal(2, s.Db.AllTransactions().Count(t => t.Date == day && t.Amount == -4.75m));
        // Both coffees again in another export: nothing new.
        var again = await Import(s, Csv([coffee, coffee]), "again.csv", first.AccountId, s.Bank);
        Assert.Equal((0, 2), (again.Added, again.SkippedDuplicates));
        Assert.Equal(Expected(month), Stored(s, first.AccountId));
    }

    [Fact]
    public async Task One_day_one_amount_two_different_operations_are_both_kept()
    {
        using var s = new TempStore();
        var month = new List<FakeTransaction>
        {
            new(D(10, 9), "METRO #123 MONTREAL QC", -50m),
            new(D(10, 9), "TELEPHONE EXEMPLE INC", -50m),
            new(D(10, 15), "PAYROLL DEPOSIT EXAMPLE CORP", 2100m),
        };
        var first = await Import(s, Csv([month[0]]), "export.csv", null, s.Bank);
        var full = await Import(s, Statement("Northwind Bank", month, D(10, 1), D(10, 31)), first.AccountId, s.Bank);
        Assert.Equal((2, 1), (full.Added, full.SkippedDuplicates));
        Assert.Equal(Expected(month), Stored(s, first.AccountId));
    }

    [Fact]
    public async Task The_same_operation_dated_differently_in_two_files_is_one_operation()
    {
        using var s = new TempStore();
        var october = new List<FakeTransaction>
        {
            new(D(10, 3), "METRO #123 MONTREAL QC", -52.10m),
            new(D(10, 14), "LIBRAIRIE EXEMPLE", -62.10m),
            new(D(10, 30), "CAFE EXEMPLE MONTREAL", -4.75m),
        };
        var first = await Import(s, Statement("Northwind Bank", october, D(10, 1), D(10, 31)), null, s.Bank);
        // The card export dates each purchase the day the bank posted it (two days later), and goes on into November.
        var export = new List<FakeTransaction>
        {
            new(D(10, 16), "LIBRAIRIE EXEMPLE", -62.10m),
            new(D(11, 1), "CAFE EXEMPLE MONTREAL", -4.75m),
            new(D(11, 4), "CAFE EXEMPLE MONTREAL", -4.75m),
        };
        var r = await Import(s, Csv(export), "export.csv", first.AccountId, s.Bank);
        // The book is the same purchase; the coffee of 30 October is the one of 1 November; the coffee of 4 November is new.
        Assert.Equal((1, 2), (r.Added, r.SkippedDuplicates));
        Assert.Equal(4, Stored(s, first.AccountId).Count);
    }

    [Fact]
    public async Task Two_real_purchases_on_both_sides_of_the_end_of_a_statement_stay_two()
    {
        using var s = new TempStore();
        var first = await Import(s, Statement("Northwind Bank", [new(D(10, 14), "CAFE EXEMPLE MONTREAL", -4.75m)], D(10, 1), D(10, 15)), null, s.Bank);
        var second = await Import(s, Statement("Northwind Bank", [new(D(10, 16), "CAFE EXEMPLE MONTREAL", -4.75m)], D(10, 16), D(10, 31)), first.AccountId, s.Bank);
        Assert.Equal((1, 0), (second.Added, second.SkippedDuplicates));
        Assert.Equal(2, Stored(s, first.AccountId).Count);
    }

    [Fact]
    public async Task Two_crossing_files_opened_together_are_saved_without_doubles()
    {
        using var s = new TempStore();
        var year = Year();
        var account = (await Import(s, Statement("Northwind Bank", Between(year, D(8, 1), D(8, 31)), D(8, 1), D(8, 31)), null, s.Bank)).AccountId;
        var a = await s.Imports.ParseAsync("a.pdf", Render(Statement("Northwind Bank", Between(year, D(9, 1), D(10, 20)), D(9, 1), D(10, 20))));
        var b = await s.Imports.ParseAsync("b.pdf", Render(Statement("Northwind Bank", Between(year, D(10, 1), D(11, 10)), D(10, 1), D(11, 10))));
        // Both drafts chosen for the account before either is saved: both see nothing of the other yet.
        s.Imports.Choose(a.Id, account, null);
        s.Imports.Choose(b.Id, account, null);
        Assert.DoesNotContain(b.Rows, r => r.Duplicate);
        s.Imports.Commit(new CommitRequest(a.Id, account, null, "CAD", null, []));
        var rb = s.Imports.Commit(new CommitRequest(b.Id, account, null, "CAD", null, []));
        Assert.Equal(Between(year, D(10, 1), D(10, 20)).Count, rb.SkippedDuplicates);
        Assert.Equal(Expected(Between(year, D(8, 1), D(11, 10))), Stored(s, account));
    }

    [Fact]
    public async Task A_statement_deleted_then_imported_again_comes_back_once()
    {
        using var s = new TempStore();
        var october = Between(Year(), D(10, 1), D(10, 31));
        var spec = Statement("Northwind Bank", october, D(10, 1), D(10, 31));
        var first = await Import(s, spec, null, s.Bank);
        s.Db.DeleteStatement(first.StatementId);
        s.Transactions.ReapplyRules();
        Assert.Empty(Stored(s, first.AccountId));
        var again = await Import(s, spec, first.AccountId, s.Bank);
        Assert.Equal((october.Count, 0), (again.Added, again.SkippedDuplicates));
        Assert.Equal(Expected(october), Stored(s, first.AccountId));
    }

    [Fact]
    public async Task Operations_corrected_by_hand_are_still_recognised_and_keep_the_corrections()
    {
        using var s = new TempStore();
        var october = Between(Year(), D(10, 1), D(10, 31));
        var first = await Import(s, Statement("Northwind Bank", october, D(10, 1), D(10, 31)), null, s.Bank);
        var phone = s.Db.AllTransactions().Single(t => t.RawLabel.StartsWith("TELEPHONE", StringComparison.Ordinal));
        s.Transactions.Update(phone.Id, new TransactionInput(phone.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), phone.Amount, "My phone", "subscriptions", phone.AccountId, "work line"));
        var again = await Import(s, Csv(Between(october, D(10, 15), D(10, 31))), "export.csv", first.AccountId, s.Bank);
        Assert.Equal(0, again.Added);
        var kept = s.Db.GetTransaction(phone.Id)!;
        Assert.Equal(("My phone", "subscriptions", "work line"), (kept.Brand, kept.CategoryId, kept.Note));
        Assert.Equal(Expected(october), Stored(s, first.AccountId));
    }

    [Fact]
    public async Task An_operation_entered_by_hand_stays_and_the_person_is_told_of_the_same_one()
    {
        using var s = new TempStore();
        var first = await Import(s, Statement("Northwind Bank", [new(D(9, 3), "METRO #123 MONTREAL QC", -12m)], D(9, 1), D(9, 30)), null, s.Bank);
        // Entered by hand: what is entered by hand is the person's, whatever the files say later.
        var coffee = s.Transactions.Create(new TransactionInput("2026-10-14", -4.75m, "Cafe Exemple", "restaurants", first.AccountId, null));
        var october = new List<FakeTransaction>
        {
            new(D(10, 14), "CAFE EXEMPLE MONTREAL", -4.75m),
            new(D(10, 25), "METRO #123 MONTREAL QC", -61.40m),
        };
        var r = await Import(s, Statement("Northwind Bank", october, D(10, 1), D(10, 31)), first.AccountId, s.Bank);
        Assert.Equal((2, 0), (r.Added, r.SkippedDuplicates));
        Assert.Equal(4, Stored(s, first.AccountId).Count);
        // Entering exactly the same operation again (same day, amount, brand and account): told, not refused. The day
        // has two already: the one entered by hand and the statement's line (named "Cafe Exemple" too).
        Assert.Equal(2, s.Transactions.SameOnDay("2026-10-14", -4.75m, "cafe exemple", first.AccountId, null));
        Assert.Equal(1, s.Transactions.SameOnDay("2026-10-14", -4.75m, "Cafe Exemple", first.AccountId, coffee.Id));
        Assert.Equal(0, s.Transactions.SameOnDay("2026-10-14", -5.75m, "Cafe Exemple", first.AccountId, null));
        Assert.Equal(0, s.Transactions.SameOnDay("2026-10-15", -4.75m, "Cafe Exemple", first.AccountId, null));
        s.Transactions.Create(new TransactionInput("2026-10-14", -4.75m, "Cafe Exemple", "restaurants", first.AccountId, null));
        Assert.Equal(3, s.Transactions.SameOnDay("2026-10-14", -4.75m, "Cafe Exemple", first.AccountId, null));
    }

    // ======================================================================================== several banks

    /// <summary>A second Canadian bank, and a French bank in euros.</summary>
    private static (long Northwind, long National, long Exemple) Banks(TempStore s)
    {
        var northwind = s.Db.AddBank(new Bank { Name = "Northwind Bank", Country = "Canada", System = BankingSystem.NorthAmerica, Currency = "CAD" });
        var exemple = s.Db.AddBank(new Bank { Name = "Banque Exemple", Country = "France", System = BankingSystem.France, Currency = "EUR" });
        return (northwind, s.Bank, exemple);
    }

    /// <summary>Invented rates in the European Central Bank's layout (units per euro), newest first.</summary>
    private static ExchangeRates Rates(TempStore s, string csv = "Date,CAD,\n2026-10-12,1.6100,\n2026-10-09,1.6000,\n2026-10-08,1.6000,\n2026-10-07,1.5900,\n")
    {
        var rates = new ExchangeRates(s.Db);
        rates.Save(RateSource.Ecb, Encoding.UTF8.GetBytes(csv));
        s.Transactions.Convert = (amount, from, to, day) => rates.Convert(amount, from, to, day)?.Amount;
        return rates;
    }

    private static string Box(FlowDiagram d, string account) => d.Nodes.Single(n => n.Name == account).Id;

    /// <summary>Northwind chequing sends 500 to National chequing on 20 October (received the 21st); the rest is spending and pay.</summary>
    private static (Spec Northwind, Spec NationalOct, Spec NationalNov) OneCurrency()
    {
        var northwind = new List<FakeTransaction>
        {
            new(D(10, 3), "METRO #123 MONTREAL QC", -52.10m),
            new(D(10, 15), "PAYROLL DEPOSIT EXAMPLE CORP", 2100m),
            new(D(10, 20), "ONLINE TRANSFER TO NATIONAL BANK", -500m),
        };
        var nationalOct = new List<FakeTransaction>
        {
            new(D(10, 21), "ONLINE TRANSFER FROM NORTHWIND BANK", 500m),
            new(D(10, 25), "LIBRAIRIE EXEMPLE", -38.20m),
        };
        // In November National sends 300 back; Northwind's November is not imported yet.
        var nationalNov = new List<FakeTransaction>
        {
            new(D(11, 4), "ONLINE TRANSFER TO NORTHWIND BANK", -300m),
            new(D(11, 6), "CAFE EXEMPLE MONTREAL", -4.75m),
        };
        return (Statement("Northwind Bank", northwind, D(10, 1), D(10, 31)),
            Statement("National Bank of Canada", nationalOct, D(10, 1), D(10, 31)),
            Statement("National Bank of Canada", nationalNov, D(11, 1), D(11, 30)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_transfer_between_two_banks_is_one_movement_whatever_the_order_of_the_imports(bool nationalFirst)
    {
        using var s = new TempStore();
        var (northwindBank, nationalBank, _) = Banks(s);
        var (nw, natOct, natNov) = OneCurrency();
        long northwind, national;
        if (nationalFirst)
        {
            national = (await Import(s, natOct, null, nationalBank, accountName: "National chequing")).AccountId;
            await Import(s, natNov, national, nationalBank);
            northwind = (await Import(s, nw, null, northwindBank, accountName: "Northwind chequing")).AccountId;
        }
        else
        {
            northwind = (await Import(s, nw, null, northwindBank, accountName: "Northwind chequing")).AccountId;
            national = (await Import(s, natOct, null, nationalBank, accountName: "National chequing")).AccountId;
            await Import(s, natNov, national, nationalBank);
        }
        var txs = s.Db.AllTransactions();
        var sent = txs.Single(t => t.AccountId == northwind && t.Amount == -500m);
        var received = txs.Single(t => t.AccountId == national && t.Amount == 500m);
        Assert.Equal((received.Id, sent.Id), (sent.LinkedId, received.LinkedId));
        Assert.All([sent, received], t => Assert.Equal(Categories.Transfers, t.CategoryId));

        // October, every bank together: the transfer is neither spending nor income, and the banks add up.
        var r = s.Summaries.Report(D(10, 1), D(10, 31), Granularity.Month, "CAD", Rates(s));
        Assert.Equal(52.10m + 38.20m, r.Global.Expenses);
        Assert.Equal(2100m, r.Global.Earnings);
        Assert.Equal(r.Global.Expenses, r.Banks.Sum(b => b.Summary.Expenses));
        Assert.Equal(r.Global.Earnings, r.Banks.Sum(b => b.Summary.Earnings));
        // One movement, drawn once from one account to the other, each inside the box of its bank.
        var m = Assert.Single(r.Global.Movements);
        Assert.Equal(("Northwind chequing", "National chequing", 500m), (m.From, m.To, m.Amount));
        var d = r.Global.Diagram;
        var arrow = Assert.Single(d.Arrows);
        Assert.Equal((Box(d, "Northwind chequing"), Box(d, "National chequing"), 500m, false), (arrow.From, arrow.To, arrow.Amount, arrow.Outside));
        Assert.Equal(["National Bank of Canada", "Northwind Bank"], d.Banks.Select(b => b.Name).Order());
        AssertInside(d, "Northwind chequing", "Northwind Bank");
        AssertInside(d, "National chequing", "National Bank of Canada");
        // The diagram is drawn once, for every bank: the banks' parts carry none.
        Assert.All(r.Banks, b => Assert.Empty(b.Summary.Diagram.Nodes));

        // November: Northwind's half is not imported yet, so the 300 goes outside the app.
        var nov = s.Summaries.Report(D(11, 1), D(11, 30), Granularity.Month, "CAD", Rates(s));
        Assert.Single(nov.Global.Movements, x => x.ToOutside && x.Amount == 300m);
        // Northwind's November comes: the two halves are paired, from National to Northwind.
        await Import(s, Statement("Northwind Bank", [new(D(11, 5), "ONLINE TRANSFER FROM NATIONAL BANK", 300m)], D(11, 1), D(11, 30)), northwind, northwindBank);
        nov = s.Summaries.Report(D(11, 1), D(11, 30), Granularity.Month, "CAD", Rates(s));
        var back = Assert.Single(nov.Global.Movements);
        Assert.Equal(("National chequing", "Northwind chequing", false, false), (back.From, back.To, back.FromOutside, back.ToOutside));
        Assert.Equal(4.75m, nov.Global.Expenses);
        Assert.Equal(0m, nov.Global.Earnings);
    }

    private static void AssertInside(FlowDiagram d, string account, string bank)
    {
        var n = d.Nodes.Single(x => x.Name == account);
        var b = d.Banks.Single(x => x.Name == bank);
        Assert.InRange(n.X - n.W / 2, b.X - b.W / 2, b.X + b.W / 2);
        Assert.InRange(n.X + n.W / 2, b.X - b.W / 2, b.X + b.W / 2);
        Assert.InRange(n.Y - n.H / 2, b.Y - b.H / 2, b.Y + b.H / 2);
        Assert.InRange(n.Y + n.H / 2, b.Y - b.H / 2, b.Y + b.H / 2);
    }

    [Fact]
    public async Task Crossing_statements_of_both_banks_add_nothing_and_pair_nothing_twice()
    {
        using var s = new TempStore();
        var (northwindBank, nationalBank, _) = Banks(s);
        var (nw, natOct, natNov) = OneCurrency();
        var northwind = (await Import(s, nw, null, northwindBank, accountName: "Northwind chequing")).AccountId;
        var national = (await Import(s, natOct, null, nationalBank, accountName: "National chequing")).AccountId;
        var before = s.Db.AllTransactions().Select(t => (t.Id, t.LinkedId, t.CategoryId)).Order().ToList();
        // Each bank's export of the second half of October again.
        Assert.Equal(0, (await Import(s, Csv(nw.Transactions.Where(t => t.Date.Day >= 15)), "nw.csv", northwind, northwindBank)).Added);
        Assert.Equal(0, (await Import(s, Csv(natOct.Transactions.Where(t => t.Date.Day >= 15)), "nat.csv", national, nationalBank)).Added);
        Assert.Equal(before, s.Db.AllTransactions().Select(t => (t.Id, t.LinkedId, t.CategoryId)).Order().ToList());
        var r = s.Summaries.Report(D(10, 1), D(10, 31), Granularity.Month, "CAD", Rates(s));
        Assert.Single(r.Global.Movements);
        Assert.Single(r.Global.Diagram.Arrows);
    }

    [Fact]
    public async Task A_transfer_across_the_end_of_a_month_is_in_neither_month_s_spending_or_income()
    {
        using var s = new TempStore();
        var (northwindBank, nationalBank, _) = Banks(s);
        await Import(s, Statement("Northwind Bank", [new(D(10, 31), "ONLINE TRANSFER TO NATIONAL BANK", -250m)], D(10, 1), D(10, 31)), null, northwindBank, accountName: "Northwind chequing");
        await Import(s, Statement("National Bank of Canada", [new(D(11, 2), "ONLINE TRANSFER FROM NORTHWIND BANK", 250m)], D(11, 1), D(11, 30)), null, nationalBank, accountName: "National chequing");
        var rates = Rates(s);
        var oct = s.Summaries.Report(D(10, 1), D(10, 31), Granularity.Month, "CAD", rates);
        var nov = s.Summaries.Report(D(11, 1), D(11, 30), Granularity.Month, "CAD", rates);
        Assert.Equal((0m, 0m, 0m, 0m), (oct.Global.Expenses, oct.Global.Earnings, nov.Global.Expenses, nov.Global.Earnings));
        // Each month shows the movement between the two accounts (its other half is in the other month).
        Assert.Contains(oct.Global.Movements, m => m.From == "Northwind chequing" && m.To == "National chequing");
        Assert.Contains(nov.Global.Movements, m => m.From == "Northwind chequing" && m.To == "National chequing");
    }

    [Fact]
    public async Task Two_transfers_of_one_amount_go_each_to_its_own_account()
    {
        using var s = new TempStore();
        var (northwindBank, nationalBank, _) = Banks(s);
        var nw = (await Import(s, Statement("Northwind Bank",
        [
            new(D(10, 5), "ONLINE TRANSFER TO NATIONAL BANK", -200m),
            new(D(10, 6), "ONLINE TRANSFER TO NATIONAL SAVINGS", -200m),
        ], D(10, 1), D(10, 31)), null, northwindBank, accountName: "Northwind chequing")).AccountId;
        var chequing = (await Import(s, Statement("National Bank of Canada", [new(D(10, 5), "ONLINE TRANSFER FROM NORTHWIND BANK", 200m)], D(10, 1), D(10, 31)),
            null, nationalBank, accountName: "National chequing")).AccountId;
        var savings = (await Import(s, Statement("National Bank of Canada", [new(D(10, 8), "ONLINE TRANSFER FROM NORTHWIND BANK", 200m)], D(10, 1), D(10, 31)),
            null, nationalBank, AccountKind.Savings, "National savings")).AccountId;
        var txs = s.Db.AllTransactions().ToDictionary(t => t.Id);
        var first = txs.Values.Single(t => t.AccountId == nw && t.Date == D(10, 5));
        var second = txs.Values.Single(t => t.AccountId == nw && t.Date == D(10, 6));
        Assert.Equal(chequing, txs[first.LinkedId!.Value].AccountId);
        Assert.Equal(savings, txs[second.LinkedId!.Value].AccountId);
        // Drawn: two arrows from Northwind to two accounts inside National's box.
        var d = s.Summaries.Report(D(10, 1), D(10, 31), Granularity.Month, "CAD", Rates(s)).Global.Diagram;
        Assert.Equal(2, d.Arrows.Count(a => a.From == Box(d, "Northwind chequing")));
        AssertInside(d, "National chequing", "National Bank of Canada");
        AssertInside(d, "National savings", "National Bank of Canada");
    }

    [Fact]
    public async Task Transfers_between_accounts_of_one_bank_stay_inside_its_box()
    {
        using var s = new TempStore();
        var (_, nationalBank, _) = Banks(s);
        await Import(s, Statement("National Bank of Canada", [new(D(10, 5), "ONLINE TRANSFER TO SAVINGS", -400m), new(D(10, 9), "METRO #123 MONTREAL QC", -20m)], D(10, 1), D(10, 31)),
            null, nationalBank, accountName: "National chequing");
        await Import(s, Statement("National Bank of Canada", [new(D(10, 5), "ONLINE TRANSFER FROM CHEQUING", 400m)], D(10, 1), D(10, 31)),
            null, nationalBank, AccountKind.Savings, "National savings");
        var d = s.Summaries.Report(D(10, 1), D(10, 31), Granularity.Month, "CAD", Rates(s)).Global.Diagram;
        var bank = Assert.Single(d.Banks);
        var arrow = Assert.Single(d.Arrows);
        Assert.Equal((Box(d, "National chequing"), Box(d, "National savings")), (arrow.From, arrow.To));
        // Every point of the arrow, and its amount, inside the bank's box.
        foreach (Match p in Regex.Matches(arrow.Path, @"-?\d+(\.\d+)?\s-?\d+(\.\d+)?"))
        {
            var xy = p.Value.Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            Assert.InRange(xy[0], bank.X - bank.W / 2, bank.X + bank.W / 2);
            Assert.InRange(xy[1], bank.Y - bank.H / 2, bank.Y + bank.H / 2);
        }
        Assert.InRange(arrow.LabelY, bank.Y - bank.H / 2, bank.Y + bank.H / 2);
    }

    /// <summary>500 EUR sent from the French bank on 8 October, 790.40 CAD received in Canada on 9 October (1.6000 that day, 1.2% kept by the banks).</summary>
    private static async Task<(long Euro, long Cad)> TwoCurrencies(TempStore s, decimal received = 790.40m, int sentDay = 8)
    {
        var (northwindBank, _, exemple) = Banks(s);
        var euro = (await Import(s, Statement("Banque Exemple",
        [
            new(D(10, 9), "CB BOULANGERIE PAUL", -12.40m),
            new(D(10, sentDay), "VIR SEPA EMIS NORTHWIND BANK", -500m),
        ], D(10, 1), D(10, 31), style: Style.FrenchDebitCredit, currency: "EUR"), null, exemple, accountName: "Compte courant")).AccountId;
        var cad = (await Import(s, Statement("Northwind Bank",
        [
            new(D(10, sentDay + 1), "WIRE TRANSFER RECEIVED BANQUE EXEMPLE", received),
            new(D(10, 15), "PAYROLL DEPOSIT EXAMPLE CORP", 2100m),
        ], D(10, 1), D(10, 31)), null, northwindBank, accountName: "Northwind chequing")).AccountId;
        return (euro, cad);
    }

    [Fact]
    public async Task A_transfer_between_two_currencies_is_one_movement_shown_in_the_display_currency()
    {
        using var s = new TempStore();
        var rates = Rates(s);
        var (euro, cad) = await TwoCurrencies(s);
        var txs = s.Db.AllTransactions();
        var sent = txs.Single(t => t.AccountId == euro && t.Amount == -500m);
        var received = txs.Single(t => t.AccountId == cad && t.Amount == 790.40m);
        Assert.Equal(received.Id, sent.LinkedId);

        // In Canadian dollars: the 500 EUR are 800.00 CAD at the rate of the 8th; neither spending nor income.
        var r = s.Summaries.Report(D(10, 1), D(10, 31), Granularity.Month, "CAD", rates);
        Assert.Equal(Math.Round(12.40m * 1.6m, 2), r.Global.Expenses);
        Assert.Equal(2100m, r.Global.Earnings);
        var m = Assert.Single(r.Global.Movements);
        Assert.Equal(("Compte courant", "Northwind chequing", 800.00m), (m.From, m.To, m.Amount));
        var d = r.Global.Diagram;
        var arrow = Assert.Single(d.Arrows);
        Assert.Equal((Box(d, "Compte courant"), Box(d, "Northwind chequing"), 800.00m), (arrow.From, arrow.To, arrow.Amount));
        AssertInside(d, "Compte courant", "Banque Exemple");
        AssertInside(d, "Northwind chequing", "Northwind Bank");

        // In euros: the same movement, 500.00 EUR.
        var eur = s.Summaries.Report(D(10, 1), D(10, 31), Granularity.Month, "EUR", rates);
        Assert.Equal(500.00m, Assert.Single(eur.Global.Diagram.Arrows).Amount);
        Assert.Equal(12.40m, eur.Global.Expenses);
    }

    [Fact]
    public async Task A_salary_of_the_same_value_is_never_taken_for_the_other_half()
    {
        using var s = new TempStore();
        Rates(s);
        var (northwindBank, _, exemple) = Banks(s);
        await Import(s, Statement("Banque Exemple", [new(D(10, 8), "VIR SEPA EMIS NORTHWIND BANK", -500m)], D(10, 1), D(10, 31),
            style: Style.FrenchDebitCredit, currency: "EUR"), null, exemple, accountName: "Compte courant");
        await Import(s, Statement("Northwind Bank", [new(D(10, 9), "PAYROLL DEPOSIT EXAMPLE CORP", 798m)], D(10, 1), D(10, 31)), null, northwindBank, accountName: "Northwind chequing");
        Assert.All(s.Db.AllTransactions(), t => Assert.Null(t.LinkedId));
    }

    [Fact]
    public async Task An_amount_too_far_from_the_rate_is_not_the_other_half()
    {
        using var s = new TempStore();
        Rates(s);
        // 700 CAD for 500 EUR at 1.60: 12.5% off, far more than banks keep.
        await TwoCurrencies(s, received: 700m);
        Assert.All(s.Db.AllTransactions(), t => Assert.Null(t.LinkedId));
    }

    [Fact]
    public async Task Without_a_rate_for_its_day_a_transfer_between_currencies_waits_for_the_rates()
    {
        using var s = new TempStore();
        // Rates from 7 October only; the transfer is on the 2nd.
        var rates = Rates(s);
        await TwoCurrencies(s, received: 790.40m, sentDay: 2);
        Assert.All(s.Db.AllTransactions(), t => Assert.Null(t.LinkedId));
        // The rates of the 1st and 2nd arrive: the halves are paired.
        rates.Save(RateSource.Ecb, Encoding.UTF8.GetBytes("Date,CAD,\n2026-10-12,1.6100,\n2026-10-09,1.6000,\n2026-10-08,1.6000,\n2026-10-07,1.5900,\n2026-10-02,1.6000,\n2026-10-01,1.6000,\n"));
        s.Transactions.ReapplyRules();
        Assert.Equal(2, s.Db.AllTransactions().Count(t => t.LinkedId is not null));
    }

    [Fact]
    public async Task Two_accounts_with_one_name_at_two_banks_are_two_boxes()
    {
        using var s = new TempStore();
        var (northwindBank, nationalBank, _) = Banks(s);
        await Import(s, Statement("Northwind Bank", [new(D(10, 20), "ONLINE TRANSFER TO NATIONAL BANK", -500m)], D(10, 1), D(10, 31)), null, northwindBank, accountName: "Chequing");
        await Import(s, Statement("National Bank of Canada", [new(D(10, 21), "ONLINE TRANSFER FROM NORTHWIND BANK", 500m)], D(10, 1), D(10, 31)), null, nationalBank, accountName: "Chequing");
        var d = s.Summaries.Report(D(10, 1), D(10, 31), Granularity.Month, "CAD", Rates(s)).Global.Diagram;
        Assert.Equal(2, d.Nodes.Count(n => n.Name == "Chequing"));
        var arrow = Assert.Single(d.Arrows);
        Assert.NotEqual(arrow.From, arrow.To);
        Assert.Equal(2, d.Banks.Count);
    }

    // ======================================================================================== four banks, Wise between them

    /// <summary>
    /// The statements of <see cref="FourBanksGenerator"/> imported, each into a new account at its bank, with invented
    /// rates: 1 EUR = 1.60 CAD = 1.10 USD every day.
    /// </summary>
    private static async Task<(ExchangeRates Rates, Dictionary<string, long> Accounts)> FourBanks(TempStore s)
    {
        var rates = new ExchangeRates(s.Db);
        var csv = new StringBuilder("Date,CAD,USD,\n");
        for (var d = D(10, 31); d >= D(9, 25); d = d.AddDays(-1)) csv.Append(CultureInfo.InvariantCulture, $"{d:yyyy-MM-dd},1.6000,1.1000,\n");
        rates.Save(RateSource.Ecb, Encoding.UTF8.GetBytes(csv.ToString()));
        s.Transactions.Convert = (amount, from, to, day) => rates.Convert(amount, from, to, day)?.Amount;
        var banks = new Dictionary<string, long>();
        var ids = new Dictionary<string, long>();
        foreach (var f in FourBanksGenerator.Statements())
        {
            if (!banks.TryGetValue(f.Bank, out var bank))
                banks[f.Bank] = bank = s.Db.AddBank(new Bank { Name = f.Bank, Country = f.Country, System = f.System, Currency = f.Currency });
            ids[f.Account] = (await Import(s, f.Bytes, f.FileName, null, bank, f.Currency, f.Kind, f.Account)).AccountId;
        }
        return (rates, ids);
    }

    [Fact]
    public async Task Four_banks_in_three_currencies_with_Wise_between_them()
    {
        using var s = new TempStore();
        var (rates, ids) = await FourBanks(s);
        var txs = s.Db.AllTransactions();
        Transaction Op(string account, decimal amount) => txs.Single(t => t.AccountId == ids[account] && t.Amount == amount);
        void Paired(string from, decimal sent, string to, decimal received)
        {
            var (o, i) = (Op(from, sent), Op(to, received));
            Assert.True(o.LinkedId == i.Id && i.LinkedId == o.Id, $"{from} {sent} and {to} {received} are not one movement");
        }
        // Into Wise, between its currencies, out of it; inside each bank; one bank to another without Wise.
        Paired("Northwind chequing", -2000m, "Wise CAD", 2000m);
        Paired("Wise CAD", -1985m, "Wise EUR", 1228.22m);
        Paired("Wise EUR", -1228.22m, "Compte courant", 1228.22m);
        Paired("Contoso checking", -1500m, "Wise USD", 1500m);
        Paired("Wise USD", -1490m, "Wise CAD", 2120.00m);
        Paired("Compte courant", -820m, "Northwind chequing", 1296.26m);
        Paired("Northwind chequing", -900m, "Northwind savings", 900m);
        Paired("Northwind chequing", -1240m, "Northwind card", 1240m);
        Paired("Compte courant", -600m, "Livret A", 600m);
        Paired("Contoso checking", -700m, "Contoso savings", 700m);
        Paired("Contoso checking", -830m, "Contoso card", 830m);
        // The exchanges inside Wise are only found the way of the transfer services: that way paired the most, it was kept.
        Assert.Equal("transfer services", TransferMatcher.LastWay);

        // October in Canadian dollars: only the purchases are spending, only the salaries income.
        var r = s.Summaries.Report(D(10, 1), D(10, 31), Granularity.Month, "CAD", rates);
        decimal Cad(decimal amount, string currency) => rates.Convert(amount, currency, "CAD", D(10, 15))!.Amount;
        Assert.Equal(80m + 16.99m + Cad(12.40m, "EUR") + Cad(45m, "USD") + Cad(25m, "USD"), r.Global.Expenses);
        Assert.Equal(4200m + Cad(2500m, "EUR") + Cad(3000m, "USD"), r.Global.Earnings);
        Assert.Equal(r.Global.Expenses, r.Banks.Sum(b => b.Summary.Expenses));
        // What the banks kept: 820.00 EUR are 1,312.00 CAD at the day's rate, 1,296.26 CAD arrived.
        var direct = r.Global.Movements.Single(m => m.From == "Compte courant" && m.To == "Northwind chequing");
        Assert.Equal((820m, "EUR", 1296.26m, "CAD", 15.74m), (direct.Sent, direct.SentCurrency, direct.Received, direct.ReceivedCurrency, direct.Lost));
        // Inside Wise: 1,985.00 CAD are 1,240.63 EUR at the day's rate, 1,228.22 EUR arrived: 12.41 EUR kept, 19.86 CAD.
        var exchange = r.Global.Movements.Single(m => m.From == "Wise CAD" && m.To == "Wise EUR");
        Assert.Equal(Cad(12.41m, "EUR"), exchange.Lost);
        // Investments go outside the app.
        Assert.Equal(2, r.Global.Movements.Count(m => m.ToOutside && m.CategoryId == Categories.Investments));

        // The diagram: four banks holding their accounts, the outside box, readable, at most one crossing.
        var d = r.Global.Diagram;
        Assert.Equal(["Banque Exemple", "Contoso Bank", "Northwind Bank", "Wise"], d.Banks.Select(b => b.Name).Order());
        Assert.Equal(11, d.Nodes.Count(n => !n.Outside));
        Assert.Single(d.Nodes, n => n.Outside);
        Assert.Contains(d.Arrows, a => a.Lost == 15.74m);
        DiagramChecks.AssertReadable(d);
        Assert.True(DiagramChecks.Crossings(d) <= 1, $"{DiagramChecks.Crossings(d)} crossings");
    }
}
