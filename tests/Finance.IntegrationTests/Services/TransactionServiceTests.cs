using System.Text;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>Operations entered and corrected by the person, and what the app learns from the corrections.</summary>
public sealed class TransactionServiceTests : IDisposable
{
    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    private static byte[] Csv(params (string Day, string Label, decimal Amount)[] lines) =>
        Encoding.UTF8.GetBytes("Date,Description,Amount\n" + string.Concat(lines.Select(l => $"{l.Day},{l.Label},{l.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n")));

    /// <summary>Two purchases at two branches of one corner shop and a salary, imported.</summary>
    private async Task<long> ImportCornerShopAsync()
    {
        var r = await _store.ImportAsync("march.csv", Csv(("2025-03-03", "JOES CORNER SHOP 0012 LAVAL QC", -12.40m),
            ("2025-03-09", "JOES CORNER SHOP 0040 MONTREAL QC", -8.15m), ("2025-03-15", "PAYROLL DEPOSIT EXAMPLE CORP", 900m)));
        return r.AccountId;
    }

    private List<Transaction> CornerShop() => [.. _store.Db.AllTransactions().Where(t => t.RawLabel.StartsWith("JOES", StringComparison.Ordinal))];

    [Fact]
    public async Task SetCategory_OperationOfAShopAtTwoBranches_ChangesEveryOperationOfTheShop()
    {
        await ImportCornerShopAsync();
        var shop = CornerShop()[0];

        var changed = _store.Transactions.SetCategory(shop.Id, "groceries");

        Assert.Equal(1, changed);
        Assert.All(CornerShop(), t => Assert.Equal("groceries", t.CategoryId));
    }

    [Fact]
    public async Task SetBrand_OperationOfAShopAtTwoBranches_NamesEveryOperationOfTheShop()
    {
        await ImportCornerShopAsync();

        _store.Transactions.SetBrand(CornerShop()[0].Id, "Joe's Corner");

        Assert.All(CornerShop(), t => Assert.Equal("Joe's Corner", t.Brand));
    }

    [Fact]
    public async Task ParseAsync_LaterStatementOfACorrectedShop_GivesItTheLearnedBrandAndCategory()
    {
        await ImportCornerShopAsync();
        var shop = CornerShop()[0];
        _store.Transactions.SetCategory(shop.Id, "groceries");
        _store.Transactions.SetBrand(shop.Id, "Joe's Corner");

        var april = await _store.ParseAsync("april.csv", Csv(("2025-04-02", "JOES CORNER SHOP 0012 LAVAL QC", -5.00m)));

        Assert.Equal(("Joe's Corner", "groceries"), (april.Rows[0].Brand, april.Rows[0].CategoryId));
    }

    [Fact]
    public void SetCategory_OnlyThisOperation_ChangesNoOther()
    {
        var account = _store.AddAccount();
        var first = _store.Enter(account, "2026-01-03", -20.99m, "Boutique Zeta", "shopping");
        _store.Enter(account, "2026-02-03", -21.99m, "Boutique Zeta", "shopping");

        var others = _store.Transactions.SetCategory(first.Id, "gifts", allOfBrand: false);

        Assert.Equal(0, others);
        Assert.Single(_store.Db.AllTransactions(), t => t.CategoryId == "gifts");
    }

    [Fact]
    public async Task ImportAsync_LaterOperationOfABrandWhoseCategoryWasChanged_FollowsTheBrand()
    {
        var first = await _store.ImportAsync("a.csv", Csv(("2026-01-03", "BOUTIQUE ZETA", -20.99m), ("2026-02-03", "BOUTIQUE ZETA", -21.99m)));
        var op = _store.Db.AllTransactions()[0];
        _store.Transactions.SetCategory(op.Id, "entertainment", allOfBrand: true);

        await _store.ImportAsync("b.csv", Csv(("2026-07-03", "BOUTIQUE ZETA", -5m)), account: first.AccountId);

        Assert.All(_store.Db.AllTransactions(), t => Assert.Equal("entertainment", t.CategoryId));
    }

    [Fact]
    public async Task SetBrandCategory_BrandWithLearnedWordings_WinsOverTheWordingsAndChangesTheirRules()
    {
        await ImportCornerShopAsync();
        var shop = CornerShop()[0];
        _store.Transactions.SetCategory(shop.Id, "entertainment");

        _store.Transactions.SetBrandCategory(shop.Brand, "health");

        Assert.All(CornerShop(), t => Assert.Equal("health", t.CategoryId));
        Assert.All(_store.Db.BrandRules().Where(x => x.Brand == shop.Brand), x => Assert.Equal("health", x.CategoryId));
    }

    [Fact]
    public void RenameBrand_BrandWrittenInTwoCases_RenamesBothAndKeepsWhatWasLearnedForIt()
    {
        var account = _store.AddAccount();
        _store.Enter(account, "2026-12-04", -20m, "Cafe Lune", "restaurants");
        _store.Enter(account, "2026-12-05", -12m, "cafe lune", "restaurants");
        _store.Enter(account, "2026-12-06", -30m, "Marché Vert", "groceries");
        _store.Transactions.SetBrandCategory("Cafe Lune", "groceries");

        var renamed = _store.Transactions.RenameBrand("Cafe Lune", "Café de la Lune");

        Assert.Equal(2, renamed);
        Assert.Equal(["Café de la Lune", "Café de la Lune", "Marché Vert"], _store.Db.AllTransactions().Select(t => t.Brand).Order());
        Assert.All(_store.Db.AllTransactions().Where(t => t.Brand == "Café de la Lune"), t => Assert.Equal("groceries", t.CategoryId));
    }

    [Fact]
    public void RenameBrand_EmptyName_Throws()
    {
        var account = _store.AddAccount();
        _store.Enter(account, "2026-12-06", -30m, "Marché Vert", "groceries");

        Assert.Throws<ArgumentException>(() => _store.Transactions.RenameBrand("Marché Vert", " "));
    }

    [Fact]
    public void Create_WithoutAccount_Throws() =>
        Assert.Throws<ArgumentException>(() => _store.Transactions.Create(new TransactionInput("2025-06-15", -1m, "Candy shop", "groceries", null, null)));

    [Fact]
    public void Create_TenOperationsOfTenCents_AddUpToExactlyOneUnit()
    {
        var account = _store.AddAccount();

        for (var i = 0; i < 10; i++) _store.Enter(account, "2025-06-15", -0.10m, "Candy shop", "groceries");
        _store.Enter(account, "2025-06-16", 0.30m, "Found money", "otherincome", "pocket");

        var june = _store.Summarize(new(2025, 6, 1), new(2025, 6, 30));
        Assert.Equal((1.00m, 0.30m, -0.70m), (june.Expenses, june.Earnings, june.Balance));
    }

    [Fact]
    public void Update_PartOfMoneyBackEnteredAsEarned_CountsOnlyThatPartAsIncome()
    {
        var account = _store.AddAccount(kind: AccountKind.Savings);
        var back = _store.Enter(account, "2026-04-21", 13469.91m, "Placement", Categories.Investments);

        _store.Transactions.Update(back.Id, new TransactionInput("2026-04-21", 13469.91m, "Placement", Categories.Investments, account, null, 469.91m));

        var april = _store.Summarize(new(2026, 4, 1), new(2026, 4, 30));
        Assert.Equal((469.91m, 469.91m), (april.Earnings, april.Transactions.Single(t => t.Id == back.Id).Earned));
        Assert.Contains(april.Movements, m => m.Amount == 13469.91m);
    }

    [Fact]
    public void Update_EarnedPartLargerThanTheMoneyThatCameBack_Throws()
    {
        var account = _store.AddAccount(kind: AccountKind.Savings);
        var back = _store.Enter(account, "2026-04-21", 13469.91m, "Placement", Categories.Investments);

        Assert.Throws<ArgumentException>(() => _store.Transactions.Update(back.Id,
            new TransactionInput("2026-04-21", 13469.91m, "Placement", Categories.Investments, account, null, 20000m)));
    }

    [Fact]
    public void Update_EarnedPartOnSpending_Throws()
    {
        var account = _store.AddAccount();
        var bought = _store.Enter(account, "2026-04-25", -40m, "Achat Boutique", "shopping");

        Assert.Throws<ArgumentException>(() => _store.Transactions.Update(bought.Id,
            new TransactionInput("2026-04-25", -40m, "Achat Boutique", "shopping", account, null, 5m)));
    }

    [Fact]
    public void SameOnDay_OperationEnteredTwice_CountsTheOnesAlreadyThereWhateverTheCaseOfTheBrand()
    {
        var account = _store.AddAccount();
        var coffee = _store.Enter(account, "2026-10-14", -4.75m, "Cafe Exemple", "restaurants");
        _store.Enter(account, "2026-10-14", -4.75m, "Cafe Exemple", "restaurants");

        Assert.Equal(2, _store.Transactions.SameOnDay("2026-10-14", -4.75m, "cafe exemple", account, null));
        Assert.Equal(1, _store.Transactions.SameOnDay("2026-10-14", -4.75m, "Cafe Exemple", account, coffee.Id));
    }

    [Theory]
    [InlineData("2026-10-14", -5.75)]
    [InlineData("2026-10-15", -4.75)]
    public void SameOnDay_OtherAmountOrOtherDay_IsZero(string day, double amount)
    {
        var account = _store.AddAccount();
        _store.Enter(account, "2026-10-14", -4.75m, "Cafe Exemple", "restaurants");

        Assert.Equal(0, _store.Transactions.SameOnDay(day, (decimal)amount, "Cafe Exemple", account, null));
    }
}
