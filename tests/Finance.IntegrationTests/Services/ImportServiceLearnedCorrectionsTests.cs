using System.Text;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>What the import takes from the person's corrections of earlier operations (made through the TransactionService).</summary>
public sealed class ImportServiceLearnedCorrectionsTests : IDisposable
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
    public async Task Commit_LaterOperationOfABrandWhoseCategoryWasChanged_FollowsTheBrand()
    {
        var first = await _store.ImportAsync("a.csv", Csv(("2026-01-03", "BOUTIQUE ZETA", -20.99m), ("2026-02-03", "BOUTIQUE ZETA", -21.99m)));
        var op = _store.Db.AllTransactions()[0];
        _store.Transactions.SetCategory(op.Id, "entertainment", allOfBrand: true);

        await _store.ImportIntoAsync(first.AccountId, "b.csv", Csv(("2026-07-03", "BOUTIQUE ZETA", -5m)));

        Assert.All(_store.Db.AllTransactions(), t => Assert.Equal("entertainment", t.CategoryId));
    }
}
