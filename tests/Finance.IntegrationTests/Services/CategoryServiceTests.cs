using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>Categories made, nested, edited and deleted by the person, saved in the database.</summary>
[Collection(SharedCategories.Name)]
public sealed class CategoryServiceTests : IDisposable
{
    private readonly TestStore _store = new();
    private readonly CategoryService _categories;

    public CategoryServiceTests() => _categories = new CategoryService(_store.Db);

    public void Dispose()
    {
        _store.Dispose();
        Categories.Use(Categories.BuiltIn);
    }

    [Fact]
    public void Create_TopLevelCategory_IsTheirsAtTheTopWithItsColour()
    {
        var home = _categories.Create("Home projects", null, CategoryKind.Expense, "#2e7d32");

        Assert.Equal((false, null, "#2e7d32"), (home.BuiltIn, home.Parent, home.Color));
    }

    [Fact]
    public void Create_InsideAnotherCategory_TakesItsKindWhateverWasAsked()
    {
        var home = _categories.Create("Home projects", null, CategoryKind.Expense, null);

        var nested = _categories.Create("Paint", home.Id, CategoryKind.Income, null);

        Assert.Equal(CategoryKind.Expense, nested.Kind);
    }

    [Fact]
    public void Create_InsideABuiltInCategory_TakesItsIcon()
    {
        var gift = _categories.Create("Wedding gift", "exc-gifts", CategoryKind.Expense, null);

        Assert.Equal(("exc-gifts", Categories.Get("exc-gifts").Icon), (gift.Parent, gift.Icon));
    }

    [Fact]
    public void Create_TwelveLevelsDeep_IsKeptInTheDatabaseFile()
    {
        var top = _categories.Create("Home projects", null, CategoryKind.Expense, "#2e7d32");
        // Level 1 under the top, each next level under the one before.
        var levels = Enumerable.Range(1, 12).Aggregate(new List<Category> { top }, (made, i) => [.. made, _categories.Create($"Level {i}", made[^1].Id, CategoryKind.Expense, null)]);
        _store.Db.Dispose();
        Categories.Use(Categories.BuiltIn);

        using var reopened = new Database(_store.DatabasePath);

        Assert.Equal(levels.SkipLast(1).Select(c => (string?)c.Id).Prepend(null), levels.Select(c => Categories.Get(c.Id).Parent));
        Assert.Equal(("#2e7d32", "Level 12"), (Categories.Get(top.Id).Color, Categories.Get(levels[^1].Id).Name));
    }

    [Fact]
    public void Update_NameWithSpacesAndColourInCapitals_IsTrimmedAndWrittenInSmallLetters()
    {
        var food = _categories.Create("Food", null, CategoryKind.Expense, null);
        var bakery = _categories.Create("Bakery", food.Id, CategoryKind.Expense, null);

        var edited = _categories.Update(bakery.Id, "  Bread & pastries ", food.Id, "#FF8800");

        Assert.Equal(("Bread & pastries", "#ff8800"), (edited.Name, edited.Color));
    }

    [Fact]
    public void Update_BuiltInCategory_IsRenamedAndNestedLikeAnyOther()
    {
        var food = _categories.Create("Food", null, CategoryKind.Expense, null);

        var groceries = _categories.Update("groceries", "Supermarket", food.Id, "#123456");

        Assert.Equal((food.Id, CategoryKind.Expense), (groceries.Parent, groceries.Kind));
        Assert.Equal("Supermarket", Categories.Get("groceries").Name);
    }

    [Fact]
    public void Update_NoParentAndNoColour_MovesItToTheTopWithoutColour()
    {
        var food = _categories.Create("Food", null, CategoryKind.Expense, null);
        var bakery = _categories.Create("Bakery", food.Id, CategoryKind.Expense, "#ff8800");

        var moved = _categories.Update(bakery.Id, "Bakery", null, null);

        Assert.Null(moved.Parent);
        Assert.Null(Categories.Get(bakery.Id).Color);
    }

    public static TheoryData<string, string?, string?> RefusedEdits => new()
    {
        { "Food", "groceries", null },  // inside a category that would then be inside it
        { "Food", "salary", null },     // an expense inside an income
        { " ", null, null },            // no name
        { "Food", null, "red" },        // not a colour
    };

    [Theory]
    [MemberData(nameof(RefusedEdits))]
    public void Update_EditThatMakesNoSense_Throws(string name, string? parent, string? colour)
    {
        var food = _categories.Create("Food", null, CategoryKind.Expense, null);
        _categories.Update("groceries", "Groceries", food.Id, null);

        Assert.Throws<ArgumentException>(() => _categories.Update(food.Id, name, parent, colour));
    }

    [Fact]
    public void Update_InsideItself_Throws()
    {
        var food = _categories.Create("Food", null, CategoryKind.Expense, null);

        Assert.Throws<ArgumentException>(() => _categories.Update(food.Id, "Food", food.Id, null));
    }

    [Fact]
    public void Update_UnknownCategory_Throws() =>
        Assert.Throws<KeyNotFoundException>(() => _categories.Update("u999", "Nope", null, null));

    [Fact]
    public void Delete_CategoryWithOperationsAndSubcategories_GivesThemToItsParent()
    {
        var account = _store.AddAccount();
        var food = _categories.Create("Food", null, CategoryKind.Expense, null);
        var snacks = _categories.Create("Snacks", food.Id, CategoryKind.Expense, null);
        var candy = _categories.Create("Candy", snacks.Id, CategoryKind.Expense, null);
        var bought = _store.Enter(account, "2025-10-03", -4m, "Corner shop", snacks.Id);

        _categories.Delete(snacks.Id);

        Assert.False(Categories.Exists(snacks.Id));
        Assert.Equal(food.Id, _store.Db.GetTransaction(bought.Id)!.CategoryId);
        Assert.Equal(food.Id, Categories.Get(candy.Id).Parent);
    }

    [Fact]
    public void Delete_BuiltInCategory_Throws() =>
        Assert.Throws<InvalidOperationException>(() => _categories.Delete("groceries"));
}
