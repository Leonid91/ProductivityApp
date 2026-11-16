using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.Tests;

public class BankTests
{
    [Fact]
    public async Task A_new_account_goes_to_its_bank_and_a_currency_change_reaches_every_operation()
    {
        using var s = new TempStore();
        var national = Assert.Single(s.Db.Banks());
        Assert.Equal("National Bank of Canada", national.Name);
        Assert.Equal("CAD", national.Currency);

        var spec = Sample(Style.FrenchDebitCredit, 2025, 10, 20, 9);
        var draft = await s.Imports.ParseAsync("a.pdf", Render(spec));
        var r = s.Imports.Commit(new CommitRequest(draft.Id, null, null, "EUR", AccountKind.Bank, [], NewBankName: "Fabrikam Bank"));
        var account = s.Db.Accounts().Single(a => a.Id == r.AccountId);
        var bank = s.Db.Banks().Single(b => b.Name == "Fabrikam Bank");
        Assert.Equal(bank.Id, account.BankId);
        Assert.Equal("EUR", account.Currency);
        Assert.All(s.Db.AllTransactions(), t => Assert.Equal("EUR", t.Currency));

        account.Currency = "CHF";
        s.Db.UpdateAccount(account);
        Assert.All(s.Db.AllTransactions(), t => Assert.Equal("CHF", t.Currency));

        // A bank on the France system reads its accounts that way.
        bank.System = BankingSystem.France;
        s.Db.UpdateBank(bank);
        Assert.Equal(BankingSystem.France, s.Db.Accounts().Single(a => a.Id == r.AccountId).System);
    }

    [Fact]
    public async Task An_account_is_never_opened_without_a_bank()
    {
        using var s = new TempStore();
        var draft = await s.Imports.ParseAsync("a.pdf", Render(Sample(Style.FrenchDebitCredit, 2025, 10, 20, 9)));
        Assert.Throws<ArgumentException>(() => s.Imports.Commit(new CommitRequest(draft.Id, null, null, "CAD", AccountKind.Bank, [])));
        Assert.Empty(s.Db.Accounts());
        Assert.Empty(s.Db.AllTransactions());
    }

    [Fact]
    public async Task A_brand_category_set_from_the_brand_list_wins_over_its_learned_wordings()
    {
        using var s = new TempStore();
        var spec = Sample(Style.EnglishWithdrawalsDeposits, 2025, 10, 30, 4);
        var draft = await s.Imports.ParseAsync("october.pdf", Render(spec));
        s.Imports.Commit(new CommitRequest(draft.Id, null, null, "CAD", AccountKind.Bank, [], BankId: s.Bank));
        var t = s.Db.AllTransactions().First(x => x.Amount < 0 && x.RawLabel.Length > 0);
        // Learned for its wording and its brand.
        s.Transactions.SetCategory(t.Id, "entertainment");
        Assert.Contains(s.Db.BrandRules(), x => x.MerchantKey.Length > 0 && x.Brand == t.Brand);

        s.Transactions.SetBrandCategory(t.Brand, "health");
        Assert.All(s.Db.AllTransactions().Where(x => x.Brand == t.Brand), x => Assert.Equal("health", x.CategoryId));
        Assert.All(s.Db.BrandRules().Where(x => x.Brand == t.Brand), x => Assert.Equal("health", x.CategoryId));
    }
}
