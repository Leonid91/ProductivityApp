using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>The Tags pie of a period: one slice per set of tags, so no money is counted twice.</summary>
[Collection(SharedCategories.Name)]
public sealed class SummaryServiceTagsTests : IDisposable
{
    private static readonly DateOnly October1 = new(2025, 10, 1);
    private static readonly DateOnly October31 = new(2025, 10, 31);

    private readonly TestStore _store = new();
    private readonly TagService _tags;
    private readonly long _account;

    public SummaryServiceTagsTests()
    {
        _tags = new TagService(_store.Db);
        _account = _store.AddAccount();
    }

    public void Dispose()
    {
        _store.Dispose();
        Categories.Use(Categories.BuiltIn);
    }

    private TransactionView Spend(int day, decimal amount, string category) => _store.Enter(_account, $"2025-10-{day:00}", -amount, $"Shop {day}", category);

    private PeriodSummary October() => _store.Summarize(October1, October31);

    [Fact]
    public void Summarize_SeveralTagsPerOperation_PutsEachTaggedExpenseInExactlyOneSlice()
    {
        var travel = _tags.Create("Travel", null);
        var work = _tags.Create("Work", null);
        var family = _tags.Create("Family", null);
        _tags.SetCategoryTags("restaurants", [travel.Id, work.Id]);
        var meal = Spend(3, 40m, "restaurants");
        Spend(4, 25m, "restaurants");
        var gift = Spend(5, 60m, "shopping");
        Spend(6, 99m, "shopping");
        _tags.SetOperationTags(meal.Id, [family.Id]);
        _tags.SetOperationTags(gift.Id, [family.Id]);

        var pie = October().TagPie;

        Assert.Equal([("Family", 60m), ("Family + Travel + Work", 40m), ("Travel + Work", 25m)], pie.Select(x => (x.Label, x.Amount)).Order());
        Assert.Equal(1, pie.Sum(x => x.Share), 6);
    }
}
