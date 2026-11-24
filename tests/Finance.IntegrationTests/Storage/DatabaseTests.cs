using System.Text;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.IntegrationTests.Storage;

/// <summary>The database file: what a new one holds, and changes of a bank or an account that reach what they hold.</summary>
public sealed class DatabaseTests : IDisposable
{
    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Banks_NewDatabase_IsTheNationalBankOfCanadaInCanadianDollars()
    {
        var bank = Assert.Single(_store.Db.Banks());

        Assert.Equal(("National Bank of Canada", "CAD"), (bank.Name, bank.Currency));
    }

    [Fact]
    public async Task UpdateAccount_OtherCurrency_ReachesEveryOperationOfTheAccount()
    {
        var bank = _store.AddBank("Banque Exemple", BankingSystem.France, "EUR", "France");
        var result = await _store.ImportAtAsync(bank, "a.pdf", Render(Sample(Style.FrenchDebitCredit, 2025, 10, 20, 9)), "EUR");
        var account = _store.Db.Accounts().Single(a => a.Id == result.AccountId);
        account.Currency = "CHF";

        _store.Db.UpdateAccount(account);

        Assert.All(_store.Db.AllTransactions(), t => Assert.Equal("CHF", t.Currency));
    }

    [Fact]
    public async Task UpdateAccount_AccountWhoseMoneyComesInUsDollars_ConvertsItsOperationsFromDollarsAtTheDaysRate()
    {
        var rates = new ExchangeRates(_store.Db);
        rates.Save(RateSource.Ecb, Encoding.UTF8.GetBytes(RateFiles.EuropeanCentralBank));
        await TwoBanksOctober.ImportBothBanksAsync(_store);
        var account = _store.Db.Accounts().First(a => a.BankId == _store.DefaultBank);
        account.Currency = "USD";

        _store.Db.UpdateAccount(account);

        var national = _store.Summaries.Report(new(2025, 10, 8), new(2025, 10, 10), Granularity.Range, "CAD", rates).Banks.Single(b => b.Name == KnownBanks.National).Summary;
        var tx = national.Transactions.First(t => t.AccountId == account.Id);
        var rate = new Dictionary<string, decimal> { ["2025-10-10"] = 1.62m / 1.15m, ["2025-10-09"] = 1.63m / 1.16m, ["2025-10-08"] = 1.60m / 1.17m }[tx.Date];
        Assert.All(national.Transactions.Where(t => t.AccountId == account.Id), t => Assert.Equal(("CAD", "USD"), (t.Currency, t.OriginalCurrency)));
        Assert.Equal(Math.Round(tx.Original!.Value * rate, 2, MidpointRounding.AwayFromZero), tx.Amount);
    }

    [Fact]
    public async Task UpdateBank_FrenchBankingSystem_ReachesItsAccounts()
    {
        var bank = _store.AddBank("Fabrikam Bank", currency: "EUR");
        var result = await _store.ImportAtAsync(bank, "a.pdf", Render(Sample(Style.FrenchDebitCredit, 2025, 10, 20, 9)), "EUR");
        var saved = _store.Db.Banks().Single(b => b.Id == bank);
        saved.System = BankingSystem.France;

        _store.Db.UpdateBank(saved);

        Assert.Equal(BankingSystem.France, _store.Db.Accounts().Single(a => a.Id == result.AccountId).System);
    }
}
