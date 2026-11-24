using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>The two halves of money moved between the person's accounts, paired in the database.</summary>
public sealed class TransferMatcherTests : IDisposable
{
    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Link_SavingsMovementWithoutAnyTellingWording_PairsBothHalvesAsATransfer()
    {
        var chequing = _store.AddAccount("Chequing");
        var savings = _store.AddAccount("Savings", kind: AccountKind.Savings);
        var sent = _store.Db.AddTransaction(new Transaction { AccountId = chequing, Date = new(2025, 9, 3), Amount = -300m, Currency = "CAD", RawLabel = "WEB 0042", Brand = "Web", CategoryId = "other" });
        var received = _store.Db.AddTransaction(new Transaction { AccountId = savings, Date = new(2025, 9, 4), Amount = 300m, Currency = "CAD", RawLabel = "WEB 0042", Brand = "Web", CategoryId = "otherincome" });

        var linked = TransferMatcher.Link(_store.Db);

        Assert.Equal(2, linked);
        Assert.Equal(Categories.Transfers, _store.Db.GetTransaction(sent)!.CategoryId);
        Assert.Equal(ValueSource.Linked, _store.Db.GetTransaction(received)!.CategorySource);
    }
}
