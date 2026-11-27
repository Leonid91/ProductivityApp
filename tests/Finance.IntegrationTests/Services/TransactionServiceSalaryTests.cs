using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>The category the person gives the brand of a salary learned from the history always wins.</summary>
public sealed class TransactionServiceSalaryTests : IDisposable
{
    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task SetBrandCategory_EmployerFiledAsOtherIncome_IsNeverLearnedAsASalaryAgain()
    {
        await _store.ImportAsync("chequing.csv", PayEveryTwoWeeks.Csv);
        var brand = _store.Db.AllTransactions().First(t => t.RawLabel == PayEveryTwoWeeks.Employer).Brand;

        _store.Transactions.SetBrandCategory(brand, Categories.OtherIncome);
        _store.Transactions.ReapplyRules();

        Assert.Equal([Categories.OtherIncome], _store.Db.AllTransactions().Where(t => t.RawLabel == PayEveryTwoWeeks.Employer).Select(t => t.CategoryId).Distinct());
    }

    [Fact]
    public async Task SetBrandCategory_OtherIncomeFiledAsSalary_IsASalary()
    {
        await _store.ImportAsync("chequing.csv", PayEveryTwoWeeks.Csv);
        var brand = _store.Db.AllTransactions().First(t => t.RawLabel == PayEveryTwoWeeks.Benefit).Brand;

        _store.Transactions.SetBrandCategory(brand, "salary");

        Assert.Equal(["salary"], _store.Db.AllTransactions().Where(t => t.RawLabel == PayEveryTwoWeeks.Benefit).Select(t => t.CategoryId).Distinct());
    }
}
