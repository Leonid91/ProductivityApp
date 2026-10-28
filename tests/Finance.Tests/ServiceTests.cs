using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Reports;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Storage;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.Tests;

/// <summary>A throw-away database in the temp folder, deleted after each test.</summary>
public sealed class TempStore : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "finances-tests-" + Guid.NewGuid().ToString("N"));
    public Database Db { get; }
    public TransactionService Transactions { get; }
    public ImportService Imports { get; }
    public SummaryService Summaries { get; }

    public TempStore()
    {
        Db = new Database(Path.Combine(_dir, "test.db"));
        Transactions = new TransactionService(Db, () => "CAD");
        Imports = new ImportService(Db, () => "CAD", Transactions);
        Summaries = new SummaryService(Db);
    }

    public void Dispose()
    {
        Db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

public class ServiceTests
{
    private static async Task<CommitResult> Import(TempStore s, Spec spec, string name, long? account = null)
    {
        var draft = await s.Imports.ParseAsync(name, Render(spec));
        return s.Imports.Commit(new CommitRequest(draft.Id, account, null, spec.Currency, draft.Parse.Kind, []));
    }

    [Fact]
    public async Task Import_stores_exact_amounts_and_summaries_add_up()
    {
        using var s = new TempStore();
        var spec = Sample(Style.EnglishWithdrawalsDeposits, 2025, 10, 30, 4);
        var r = await Import(s, spec, "october.pdf");
        Assert.Equal(spec.Transactions.Count, r.Added);

        var sum = s.Summaries.Summarize(new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 31), Granularity.Month, "CAD");
        Assert.Equal("October, from Wednesday 1 to Friday 31, 2025", sum.Title);
        var transfers = spec.Transactions.Where(t => t.Label.Contains("E-TRANSFER")).Sum(t => t.Amount);
        Assert.Equal(spec.Transactions.Where(t => t.Amount > 0).Sum(t => t.Amount) - transfers, sum.Earnings);
        Assert.Equal(-spec.Transactions.Where(t => t.Amount < 0).Sum(t => t.Amount), sum.Expenses);
        Assert.Equal(sum.Earnings - sum.Expenses, sum.Balance);
        Assert.Equal(31, sum.Series.Count);
        Assert.Equal(sum.Expenses, sum.Series.Sum(b => b.Expenses));
        Assert.Equal(sum.Expenses, sum.ByCategory.Sum(x => x.Amount));
        Assert.True(sum.CategoryPie.Count <= SummaryService.PieSlices + 1);
        Assert.Equal(sum.Expenses, sum.CategoryPie.Sum(x => x.Amount));
    }

    [Fact]
    public async Task Same_file_twice_is_refused_and_overlaps_are_not_counted_twice()
    {
        using var s = new TempStore();
        var spec = Sample(Style.FrenchDebitCredit, 2025, 10, 20, 9);
        var first = await Import(s, spec, "a.pdf");

        var again = await s.Imports.ParseAsync("a.pdf", Render(spec));
        Assert.True(again.AlreadyImported);
        Assert.Throws<InvalidOperationException>(() => s.Imports.Commit(new CommitRequest(again.Id, first.AccountId, null, "EUR", AccountKind.Bank, [])));

        // A second statement repeating the last week of the first one.
        var overlap = spec with { Transactions = spec.Transactions.Where(t => t.Date.Day >= 24).ToList(), Opening = 0 };
        var draft = await s.Imports.ParseAsync("b.pdf", Render(overlap));
        s.Imports.SetAccount(draft.Id, first.AccountId);
        Assert.All(draft.Rows, r => Assert.True(r.Duplicate));
        var r2 = s.Imports.Commit(new CommitRequest(draft.Id, first.AccountId, null, "EUR", AccountKind.Bank, []));
        Assert.Equal(0, r2.Added);
        Assert.Equal(overlap.Transactions.Count, r2.SkippedDuplicates);
    }

    [Fact]
    public async Task Category_correction_is_learned_and_applied_to_similar_operations()
    {
        using var s = new TempStore();
        var spec = new Spec(Style.EnglishWithdrawalsDeposits, "Northwind Bank", new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 31), 500m,
        [
            new(new DateOnly(2025, 3, 3), "JOES CORNER SHOP 0012 LAVAL QC", -12.40m),
            new(new DateOnly(2025, 3, 9), "JOES CORNER SHOP 0040 MONTREAL QC", -8.15m),
            new(new DateOnly(2025, 3, 15), "PAYROLL DEPOSIT EXAMPLE CORP", 900m),
        ]);
        await Import(s, spec, "march.pdf");
        var txs = s.Db.AllTransactions();
        var shop = txs.First(t => t.RawLabel.StartsWith("JOES"));
        Assert.Equal("other", shop.CategoryId);

        var changed = s.Transactions.SetCategory(shop.Id, "groceries");
        Assert.Equal(1, changed);
        Assert.All(s.Db.AllTransactions().Where(t => t.RawLabel.StartsWith("JOES")), t => Assert.Equal("groceries", t.CategoryId));

        s.Transactions.SetBrand(shop.Id, "Joe's Corner");
        Assert.All(s.Db.AllTransactions().Where(t => t.RawLabel.StartsWith("JOES")), t => Assert.Equal("Joe's Corner", t.Brand));

        // A later statement from the same shop is recognised straight away.
        var next = await s.Imports.ParseAsync("april.pdf", Render(spec with
        {
            Start = new DateOnly(2025, 4, 1), End = new DateOnly(2025, 4, 30),
            Transactions = [new(new DateOnly(2025, 4, 2), "JOES CORNER SHOP 0012 LAVAL QC", -5.00m)],
        }));
        Assert.Equal("Joe's Corner", next.Rows[0].Brand);
        Assert.Equal("groceries", next.Rows[0].CategoryId);
    }

    [Fact]
    public void Manual_operations_and_decimal_sums()
    {
        using var s = new TempStore();
        for (var i = 0; i < 10; i++)
            s.Transactions.Create(new TransactionInput("2025-06-15", -0.10m, "Candy shop", "groceries", null, null));
        s.Transactions.Create(new TransactionInput("2025-06-16", 0.30m, "Found money", "otherincome", null, "pocket"));
        var sum = s.Summaries.Summarize(new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 30), Granularity.Month, "CAD");
        Assert.Equal(1.00m, sum.Expenses);
        Assert.Equal(0.30m, sum.Earnings);
        Assert.Equal(-0.70m, sum.Balance);
        Assert.Contains(s.Db.Accounts(), a => a.Name == TransactionService.ManualAccountName);
    }

    [Fact]
    public async Task Report_pdf_has_the_period_and_totals()
    {
        using var s = new TempStore();
        var spec = Sample(Style.EnglishCard, 2025, 5, 18, 2);
        await Import(s, spec, "card.pdf");
        var sum = s.Summaries.Summarize(new DateOnly(2025, 5, 1), new DateOnly(2025, 5, 31), Granularity.Month, "CAD");
        var pdf = ReportBuilder.Build(sum, "DD month YYYY", new DateTime(2025, 6, 2));
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var text = string.Join(' ', doc.GetPages().Select(p => p.Text));
        Assert.Contains("May,", text);
        Assert.Contains("Expenses per category", text);
        Assert.Contains(Formatting.Money(sum.Expenses, "CAD").Replace("−", ""), text);
        Assert.Equal("Finances 2025-05.pdf", ReportBuilder.FileName(sum));
    }

    [Fact]
    public void Period_titles()
    {
        Assert.Equal("October, from Wednesday 1 to Friday 31, 2025", Formatting.PeriodTitle(new(2025, 10, 1), new(2025, 10, 31), Granularity.Month));
        Assert.Equal("2025, from Wednesday, January 1 to Wednesday, December 31", Formatting.PeriodTitle(new(2025, 1, 1), new(2025, 12, 31), Granularity.Year));
        Assert.Equal("From Sunday, January 1, 2023 to Wednesday, December 31, 2025", Formatting.PeriodTitle(new(2023, 1, 1), new(2025, 12, 31), Granularity.Range));
        Assert.Equal("03 October 2025", Formatting.FormatDate(new(2025, 10, 3), "DD month YYYY"));
        Assert.Equal("10/03/2025", Formatting.FormatDate(new(2025, 10, 3), "MM/DD/YYYY"));
    }
}
