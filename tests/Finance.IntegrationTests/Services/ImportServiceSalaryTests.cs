using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// The salary learned from the statements imported: the pays of one employer on a pay cycle become salaries; other
/// income stays other income.
/// </summary>
public sealed class ImportServiceSalaryTests : IDisposable
{
    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    private List<Transaction> Coming(string label) =>
        [.. _store.Db.AllTransactions().Where(t => t.RawLabel == label && t.Amount > 0)];

    [Fact]
    public async Task Commit_PaysEveryTwoWeeks_AreSalariesLearnedFromTheHistory()
    {
        await _store.ImportAsync("chequing.csv", PayEveryTwoWeeks.Csv);

        var pays = Coming(PayEveryTwoWeeks.Employer).Where(t => t.Amount > 1000).Select(t => (t.CategoryId, t.CategorySource)).Distinct();
        Assert.Equal([("salary", ValueSource.Detected)], pays);
    }

    [Fact]
    public async Task Commit_ExpensesPaidBackByTheEmployer_StayOtherIncome()
    {
        await _store.ImportAsync("chequing.csv", PayEveryTwoWeeks.Csv);

        var back = Assert.Single(Coming(PayEveryTwoWeeks.Employer), t => t.Amount < 1000);
        Assert.Equal(Categories.OtherIncome, back.CategoryId);
    }

    [Theory]
    [InlineData(PayEveryTwoWeeks.Benefit)]
    [InlineData(PayEveryTwoWeeks.Interest)]
    public async Task Commit_MonthlyBenefitOrInterest_StaysOtherIncome(string label)
    {
        await _store.ImportAsync("chequing.csv", PayEveryTwoWeeks.Csv);

        Assert.Equal([Categories.OtherIncome], Coming(label).Select(t => t.CategoryId).Distinct());
    }

    [Fact]
    public async Task Parse_PaysOfTheFile_AreShownAsSalariesBeforeTheImport()
    {
        var draft = await _store.ParseAsync("chequing.csv", PayEveryTwoWeeks.Csv);

        Assert.Equal(PayEveryTwoWeeks.Pays, draft.Rows.Count(r => r.CategoryId == "salary" && r.CategorySource == ValueSource.Detected));
    }

    [Fact]
    public async Task Commit_PayOfTheNextMonth_IsLearnedFromThePaysBefore()
    {
        var first = await _store.ImportAsync("chequing.csv", PayEveryTwoWeeks.Csv);

        await _store.ImportIntoAsync(first.AccountId, "june.csv", PayEveryTwoWeeks.June);

        var june = Assert.Single(_store.Db.AllTransactions(), t => t.Date == new DateOnly(2026, 6, 12));
        Assert.Equal("salary", june.CategoryId);
    }
}
