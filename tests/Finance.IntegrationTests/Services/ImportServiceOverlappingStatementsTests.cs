using System.Globalization;
using System.Text;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.IntegrationTests.Infrastructure.ChequingYear;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// Statements of one account imported in any order and with any overlap: every operation is stored once, never twice
/// and never lost.
/// </summary>
public sealed class ImportServiceOverlappingStatementsTests : IDisposable
{
    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    private static string FileName(Spec spec) => $"{spec.Start:yyyy-MM-dd} {spec.End:yyyy-MM-dd}.pdf";

    /// <summary>A statement saved into a new account.</summary>
    private Task<CommitResult> ImportAsync(Spec spec) => _store.ImportAsync(FileName(spec), Render(spec));

    /// <summary>A statement saved into the account <paramref name="account"/>.</summary>
    private Task<CommitResult> ImportAsync(Spec spec, long account) => _store.ImportIntoAsync(account, FileName(spec), Render(spec));

    private Task<CommitResult> ImportAsync(byte[] csv, long account) => _store.ImportIntoAsync(account, "export.csv", csv);

    /// <summary>What one account holds: each operation by its day and amount, as many times as it is stored.</summary>
    private List<(DateOnly, decimal)> Stored(long account) => [.. _store.Db.AllTransactions().Where(t => t.AccountId == account).Select(t => (t.Date, t.Amount)).Order()];

    private static List<(DateOnly, decimal)> Expected(IEnumerable<FakeTransaction> txs) => [.. txs.Select(t => (t.Date, t.Amount)).Order()];

    [Fact]
    public async Task Commit_TwoPeriodsOneAfterTheOther_KeepsEveryOperationOnce()
    {
        var first = Between(Year, D(1, 1), D(10, 15));
        var account = (await ImportAsync(Statement(first, D(1, 1), D(10, 15)))).AccountId;

        var second = await ImportAsync(Statement(Between(Year, D(10, 16), D(11, 10)), D(10, 16), D(11, 10), 1000m + first.Sum(t => t.Amount)), account);

        Assert.Equal((Between(Year, D(10, 16), D(11, 10)).Count, 0), (second.Added, second.SkippedDuplicates));
        Assert.Equal(Expected(Year), Stored(account));
    }

    [Fact]
    public async Task Commit_TwoStatementsSharingADay_HoldsThatDayOnce()
    {
        var day = D(10, 15);
        var account = (await ImportAsync(Statement(Between(Year, D(9, 1), day), D(9, 1), day))).AccountId;

        var second = await ImportAsync(Statement(Between(Year, day, D(10, 31)), day, D(10, 31)), account);

        Assert.Equal(Year.Count(t => t.Date == day), second.SkippedDuplicates);
        Assert.Equal(Expected(Between(Year, D(9, 1), D(10, 31))), Stored(account));
    }

    [Fact]
    public async Task Commit_PartOfAPeriodThenTheWholePeriod_AddsOnlyWhatIsMissing()
    {
        var october = Between(Year, D(10, 1), D(10, 31));
        // An export of the first twenty days, one line in three left out (a partial download).
        var part = Between(october, D(10, 1), D(10, 20)).Where((_, i) => i % 3 != 0).ToList();
        var account = (await _store.ImportAsync("export.csv", Csv(part))).AccountId;

        var full = await ImportAsync(Statement(october, D(10, 1), D(10, 31)), account);

        Assert.Equal((october.Count - part.Count, part.Count), (full.Added, full.SkippedDuplicates));
        Assert.Equal(Expected(october), Stored(account));
    }

    [Fact]
    public async Task Commit_TheWholePeriodThenPartOfIt_AddsNothing()
    {
        var october = Between(Year, D(10, 1), D(10, 31));
        var account = (await ImportAsync(Statement(october, D(10, 1), D(10, 31)))).AccountId;
        var part = Between(october, D(10, 5), D(10, 25)).Where((_, i) => i % 2 == 0).ToList();

        var again = await ImportAsync(Csv(part), account);

        Assert.Equal((0, part.Count), (again.Added, again.SkippedDuplicates));
        Assert.Equal(Expected(october), Stored(account));
    }

    private static Spec SeptemberToOctober20 => Statement(Between(Year, D(9, 1), D(10, 20)), D(9, 1), D(10, 20));

    private static Spec OctoberToNovember10 => Statement(Between(Year, D(10, 1), D(11, 10)), D(10, 1), D(11, 10));

    [Fact]
    public async Task Commit_LaterOfTwoCrossingPeriods_HoldsTheirCommonDaysOnce()
    {
        var account = (await ImportAsync(SeptemberToOctober20)).AccountId;

        var second = await ImportAsync(OctoberToNovember10, account);

        Assert.Equal(Between(Year, D(10, 1), D(10, 20)).Count, second.SkippedDuplicates);
        Assert.Equal(Expected(Between(Year, D(9, 1), D(11, 10))), Stored(account));
    }

    [Fact]
    public async Task Commit_EarlierOfTwoCrossingPeriods_HoldsTheirCommonDaysOnce()
    {
        var account = (await ImportAsync(OctoberToNovember10)).AccountId;

        var second = await ImportAsync(SeptemberToOctober20, account);

        Assert.Equal(Between(Year, D(10, 1), D(10, 20)).Count, second.SkippedDuplicates);
        Assert.Equal(Expected(Between(Year, D(9, 1), D(11, 10))), Stored(account));
    }

    [Fact]
    public async Task Commit_PeriodInsideAnotherAfterIt_AddsNothing()
    {
        var outer = Statement(Between(Year, D(1, 1), D(10, 31)), D(1, 1), D(10, 31));
        var inner = Statement(Between(Year, D(3, 1), D(4, 30)), D(3, 1), D(4, 30));
        var account = (await ImportAsync(outer)).AccountId;

        var second = await ImportAsync(inner, account);

        Assert.Equal((0, inner.Transactions.Count), (second.Added, second.SkippedDuplicates));
        Assert.Equal(Expected(outer.Transactions), Stored(account));
    }

    [Fact]
    public async Task Commit_PeriodAroundAnotherAfterIt_AddsOnlyTheDaysAround()
    {
        var outer = Statement(Between(Year, D(1, 1), D(10, 31)), D(1, 1), D(10, 31));
        var inner = Statement(Between(Year, D(3, 1), D(4, 30)), D(3, 1), D(4, 30));
        var account = (await ImportAsync(inner)).AccountId;

        var second = await ImportAsync(outer, account);

        Assert.Equal((outer.Transactions.Count - inner.Transactions.Count, inner.Transactions.Count), (second.Added, second.SkippedDuplicates));
        Assert.Equal(Expected(outer.Transactions), Stored(account));
    }

    [Fact]
    public async Task Commit_SameMonthAsTheBanksCsvWithShorterWordingInAnotherCase_AddsNothing()
    {
        var october = Between(Year, D(10, 1), D(10, 31));
        var account = (await ImportAsync(Statement(october, D(10, 1), D(10, 31)))).AccountId;
        static string Shorter(string label) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(label.ToLowerInvariant()).Replace(" Montreal Qc", "").Replace(" Montreal", "");

        var export = await ImportAsync(Csv(october, Shorter), account);

        Assert.Equal((0, october.Count), (export.Added, export.SkippedDuplicates));
        Assert.Equal(Expected(october), Stored(account));
    }

    [Fact]
    public async Task Commit_TwoIdenticalCoffeesOnOneDay_CountsThemOneForOne()
    {
        var coffee = new FakeTransaction(D(10, 7), "CAFE EXEMPLE MONTREAL", -4.75m);
        var month = new List<FakeTransaction> { new(D(10, 2), "METRO #123 MONTREAL QC", -52.10m), coffee, coffee, new(D(10, 12), "PAYROLL DEPOSIT EXAMPLE CORP", 2100m) };
        var account = (await _store.ImportAsync("export.csv", Csv([month[0], coffee]))).AccountId;

        var full = await ImportAsync(Statement(month, D(10, 1), D(10, 31)), account);

        Assert.Equal((2, 2), (full.Added, full.SkippedDuplicates));
        Assert.Equal(Expected(month), Stored(account));
    }

    [Fact]
    public async Task Commit_BothCoffeesAgainInAnotherExport_AddsNothing()
    {
        var coffee = new FakeTransaction(D(10, 7), "CAFE EXEMPLE MONTREAL", -4.75m);
        var account = (await ImportAsync(Statement([coffee, coffee], D(10, 1), D(10, 31)))).AccountId;

        var again = await ImportAsync(Csv([coffee, coffee]), account);

        Assert.Equal((0, 2), (again.Added, again.SkippedDuplicates));
    }

    [Fact]
    public async Task Commit_OneDayOneAmountTwoDifferentOperations_KeepsBoth()
    {
        var month = new List<FakeTransaction> { new(D(10, 9), "METRO #123 MONTREAL QC", -50m), new(D(10, 9), "TELEPHONE EXEMPLE INC", -50m), new(D(10, 15), "PAYROLL DEPOSIT EXAMPLE CORP", 2100m) };
        var account = (await _store.ImportAsync("export.csv", Csv([month[0]]))).AccountId;

        var full = await ImportAsync(Statement(month, D(10, 1), D(10, 31)), account);

        Assert.Equal((2, 1), (full.Added, full.SkippedDuplicates));
        Assert.Equal(Expected(month), Stored(account));
    }

    [Fact]
    public async Task Commit_SameOperationsDatedOnThePostingDayInAnotherFile_AreTheSameOperations()
    {
        var october = new List<FakeTransaction> { new(D(10, 3), "METRO #123 MONTREAL QC", -52.10m), new(D(10, 14), "LIBRAIRIE EXEMPLE", -62.10m), new(D(10, 30), "CAFE EXEMPLE MONTREAL", -4.75m) };
        var account = (await ImportAsync(Statement(october, D(10, 1), D(10, 31)))).AccountId;
        // The card export dates each purchase the day the bank posted it (two days later), and goes on into November.
        var posted = new List<FakeTransaction> { new(D(10, 16), "LIBRAIRIE EXEMPLE", -62.10m), new(D(11, 1), "CAFE EXEMPLE MONTREAL", -4.75m), new(D(11, 4), "CAFE EXEMPLE MONTREAL", -4.75m) };

        var result = await ImportAsync(Csv(posted), account);

        Assert.Equal((1, 2), (result.Added, result.SkippedDuplicates));
        Assert.Equal(4, Stored(account).Count);
    }

    [Fact]
    public async Task Commit_TwoRealPurchasesOnBothSidesOfTheEndOfAStatement_KeepsBoth()
    {
        var account = (await ImportAsync(Statement([new(D(10, 14), "CAFE EXEMPLE MONTREAL", -4.75m)], D(10, 1), D(10, 15)))).AccountId;

        var second = await ImportAsync(Statement([new(D(10, 16), "CAFE EXEMPLE MONTREAL", -4.75m)], D(10, 16), D(10, 31)), account);

        Assert.Equal((1, 0), (second.Added, second.SkippedDuplicates));
        Assert.Equal(2, Stored(account).Count);
    }

    [Fact]
    public async Task Commit_TwoCrossingFilesOpenedTogether_SavesTheSecondWithoutDoubles()
    {
        var account = (await ImportAsync(Statement(Between(Year, D(8, 1), D(8, 31)), D(8, 1), D(8, 31)))).AccountId;
        var a = await _store.ParseAsync("a.pdf", Render(Statement(Between(Year, D(9, 1), D(10, 20)), D(9, 1), D(10, 20))));
        var b = await _store.ParseAsync("b.pdf", Render(Statement(Between(Year, D(10, 1), D(11, 10)), D(10, 1), D(11, 10))));
        _store.Imports.Choose(a.Id, account, null);
        _store.Imports.Choose(b.Id, account, null);
        _store.Imports.Commit(new CommitRequest(a.Id, account, null, "CAD", null, []));

        var second = _store.Imports.Commit(new CommitRequest(b.Id, account, null, "CAD", null, []));

        Assert.Equal(Between(Year, D(10, 1), D(10, 20)).Count, second.SkippedDuplicates);
        Assert.Equal(Expected(Between(Year, D(8, 1), D(11, 10))), Stored(account));
    }

    [Fact]
    public async Task Commit_StatementDeletedThenImportedAgain_ComesBackOnce()
    {
        var october = Between(Year, D(10, 1), D(10, 31));
        var spec = Statement(october, D(10, 1), D(10, 31));
        var first = await ImportAsync(spec);
        _store.Db.DeleteStatement(first.StatementId);
        _store.Transactions.ReapplyRules();

        var again = await ImportAsync(spec, first.AccountId);

        Assert.Equal((october.Count, 0), (again.Added, again.SkippedDuplicates));
        Assert.Equal(Expected(october), Stored(first.AccountId));
    }

    [Fact]
    public async Task Commit_CrossingExportAfterOperationsWereCorrectedByHand_RecognisesThemAndKeepsTheCorrections()
    {
        var october = Between(Year, D(10, 1), D(10, 31));
        var account = (await ImportAsync(Statement(october, D(10, 1), D(10, 31)))).AccountId;
        var phone = _store.Db.AllTransactions().Single(t => t.RawLabel.StartsWith("TELEPHONE", StringComparison.Ordinal));
        _store.Transactions.Update(phone.Id, new TransactionInput("2026-10-20", phone.Amount, "My phone", "subscriptions", account, "work line"));

        var again = await ImportAsync(Csv(Between(october, D(10, 15), D(10, 31))), account);

        Assert.Equal(0, again.Added);
        var kept = _store.Db.GetTransaction(phone.Id)!;
        Assert.Equal(("My phone", "subscriptions", "work line"), (kept.Brand, kept.CategoryId, kept.Note));
    }

    [Fact]
    public async Task Commit_StatementHoldingAnOperationEnteredByHand_KeepsBoth()
    {
        var account = (await ImportAsync(Statement([new(D(9, 3), "METRO #123 MONTREAL QC", -12m)], D(9, 1), D(9, 30)))).AccountId;
        _store.Enter(account, "2026-10-14", -4.75m, "Cafe Exemple", "restaurants");

        var result = await ImportAsync(Statement([new(D(10, 14), "CAFE EXEMPLE MONTREAL", -4.75m), new(D(10, 25), "METRO #123 MONTREAL QC", -61.40m)], D(10, 1), D(10, 31)), account);

        Assert.Equal((2, 0), (result.Added, result.SkippedDuplicates));
        Assert.Equal(4, Stored(account).Count);
    }
}
