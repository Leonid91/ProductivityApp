using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>Tags on operations and on categories, several per operation, and the Tags pie that never counts money twice.</summary>
[Collection(SharedCategories.Name)]
public sealed class TagServiceTests : IDisposable
{
    private static readonly DateOnly October1 = new(2025, 10, 1);
    private static readonly DateOnly October31 = new(2025, 10, 31);

    private readonly TestStore _store = new();
    private readonly TagService _tags;
    private readonly long _account;

    public TagServiceTests()
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
    public void All_NewDatabase_HasNoTag() => Assert.Empty(_tags.All());

    [Fact]
    public void Create_NameWithSpacesAndColourInCapitals_IsTrimmedAndWrittenInSmallLetters()
    {
        var trip = _tags.Create(" Trip to Quebec ", "#1E88E5");

        Assert.Equal(("Trip to Quebec", "#1e88e5"), (trip.Name, trip.Color));
    }

    [Theory]
    [InlineData("trip to quebec", null)]   // the same name in another case
    [InlineData("", null)]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", null)] // 41 characters
    [InlineData("Work", "blue")]
    public void Create_NameTakenEmptyTooLongOrBadColour_Throws(string name, string? colour)
    {
        _tags.Create("Trip to Quebec", null);

        Assert.Throws<ArgumentException>(() => _tags.Create(name, colour));
    }

    [Fact]
    public void Update_NewNameWithoutColour_RenamesItAndRemovesItsColour()
    {
        var trip = _tags.Create("Trip", "#1e88e5");

        var renamed = _tags.Update(trip.Id, "Holidays", null);

        Assert.Equal(("Holidays", (string?)null), (renamed.Name, renamed.Color));
    }

    [Fact]
    public void SetOperationTags_OneTag_ShowsOnTheOperation()
    {
        var trip = _tags.Create("Trip", null);
        var op = Spend(3, 20m, "restaurants");

        _tags.SetOperationTags(op.Id, [trip.Id]);

        Assert.Equal([trip.Id], October().Transactions.Single(t => t.Id == op.Id).Tags!);
    }

    [Fact]
    public void Delete_TagOnAnOperation_RemovesItFromTheOperation()
    {
        var trip = _tags.Create("Trip", null);
        var op = Spend(3, 20m, "restaurants");
        _tags.SetOperationTags(op.Id, [trip.Id]);

        _tags.Delete(trip.Id);

        Assert.Empty(_tags.All());
        Assert.Empty(October().Transactions.Single(t => t.Id == op.Id).Tags!);
    }

    [Fact]
    public void SetOperationTags_DeletedTag_Throws()
    {
        var trip = _tags.Create("Trip", null);
        var op = Spend(3, 20m, "restaurants");
        _tags.Delete(trip.Id);

        Assert.Throws<KeyNotFoundException>(() => _tags.SetOperationTags(op.Id, [trip.Id]));
    }

    [Fact]
    public void SetCategoryTags_CategoryWithASubcategory_TagsTheOperationsOfBothWithoutCopyingTheTag()
    {
        var categories = new CategoryService(_store.Db);
        var food = categories.Create("Food", null, CategoryKind.Expense, null);
        var snacks = categories.Create("Snacks", food.Id, CategoryKind.Expense, null);
        var essentials = _tags.Create("Essentials", null);
        var meal = Spend(3, 30m, food.Id);
        var candy = Spend(4, 5m, snacks.Id);
        Spend(5, 50m, "shopping");

        _tags.SetCategoryTags(food.Id, [essentials.Id]);

        var byId = October().Transactions.ToDictionary(t => t.Id);
        Assert.Equal([essentials.Id], byId[meal.Id].Tags!);
        Assert.Equal([essentials.Id], byId[candy.Id].Tags!);
        Assert.Empty(byId[meal.Id].OwnTags!);
        Assert.Equal(2, byId.Values.Count(t => t.Tags!.Length > 0));
        Assert.Equal(35m, October().TagPie.Single().Amount);
    }

    [Fact]
    public void SetCategory_OperationMovedOutOfATaggedCategory_LosesTheTag()
    {
        var essentials = _tags.Create("Essentials", null);
        _tags.SetCategoryTags("groceries", [essentials.Id]);
        var op = Spend(3, 30m, "groceries");

        _store.Transactions.SetCategory(op.Id, "shopping", allOfBrand: false);

        Assert.Empty(October().Transactions.Single(t => t.Id == op.Id).Tags!);
    }

    [Fact]
    public void SetCategoryTags_NoTag_EmptiesTheTagsPie()
    {
        var essentials = _tags.Create("Essentials", null);
        _tags.SetCategoryTags("groceries", [essentials.Id]);
        Spend(3, 30m, "groceries");

        _tags.SetCategoryTags("groceries", []);

        Assert.Empty(October().TagPie);
    }

    [Fact]
    public void SetOperationTags_OperationOfATaggedCategory_GivesTheOperationEachTagOnce()
    {
        var travel = _tags.Create("Travel", null);
        var work = _tags.Create("Work", null);
        var family = _tags.Create("Family", null);
        _tags.SetCategoryTags("restaurants", [travel.Id, work.Id]);
        var meal = Spend(3, 40m, "restaurants");

        _tags.SetOperationTags(meal.Id, [family.Id, work.Id]);

        var shown = October().Transactions.Single(t => t.Id == meal.Id);
        Assert.Equal(new[] { travel.Id, work.Id, family.Id }.Order(), shown.Tags);
        Assert.Equal(new[] { family.Id, work.Id }.Order(), shown.OwnTags!.Order());
    }

    [Fact]
    public void SetCategoryTags_TagAddedToACategory_NeverChangesTheTotalsOfThePeriod()
    {
        var work = _tags.Create("Work", null);
        Spend(3, 40m, "restaurants");
        Spend(4, 60m, "shopping");
        var untagged = October();

        _tags.SetCategoryTags("restaurants", [work.Id]);

        Assert.Equal((untagged.Expenses, untagged.Balance), (October().Expenses, October().Balance));
    }
}
