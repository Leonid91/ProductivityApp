using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Storage;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.Tests;

/// <summary>The categories in use are shared by the whole engine: these tests run alone, after the others.</summary>
[CollectionDefinition("categories", DisableParallelization = true)]
public sealed class CategoryCollection;

[Collection("categories")]
public class CategoryTests
{
    [Fact]
    public void Categories_and_subcategories_nest_without_limit_and_are_kept()
    {
        var dir = Path.Combine(Path.GetTempPath(), "finances-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "test.db");
        try
        {
            string top, deepest;
            using (var db = new Database(path))
            {
                var cats = new CategoryService(db);
                var home = cats.Create("Home projects", null, CategoryKind.Expense, "#2e7d32");
                Assert.False(home.BuiltIn);
                Assert.Null(home.Parent);
                top = home.Id;
                // Twelve levels deep: there is no limit.
                var parent = home;
                for (var i = 1; i <= 12; i++) parent = cats.Create($"Level {i}", parent.Id, CategoryKind.Income, null);
                deepest = parent.Id;
                // A nested category takes the kind of the category it is in, whatever was asked.
                Assert.Equal(CategoryKind.Expense, parent.Kind);
                // Inside a built-in category it also takes its icon and its group.
                var gift = cats.Create("Wedding gift", "exc-gifts", CategoryKind.Expense, null);
                Assert.Equal("exc-gifts", gift.Parent);
                Assert.Equal(Categories.Exceptional, gift.Group);
                Assert.Equal(Categories.Get("exc-gifts").Icon, gift.Icon);
            }
            Categories.Use(Categories.BuiltIn);
            using (var db = new Database(path))
            {
                // Everything is read back from the database file.
                var chain = new List<string>();
                for (var c = Categories.Get(deepest); c.Parent is not null; c = Categories.Get(c.Parent)) chain.Add(c.Parent);
                Assert.Equal(12, chain.Count);
                Assert.Equal(top, chain[^1]);
                Assert.Equal("#2e7d32", Categories.Get(top).Color);
                Assert.Equal("Level 12", Categories.Get(deepest).Name);
            }
        }
        finally
        {
            Categories.Use(Categories.BuiltIn);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Existing_categories_are_renamed_recoloured_and_moved()
    {
        using var s = new TempStore();
        var cats = new CategoryService(s.Db);
        var food = cats.Create("Food", null, CategoryKind.Expense, null);
        var bakery = cats.Create("Bakery", food.Id, CategoryKind.Expense, null);

        var edited = cats.Update(bakery.Id, "  Bread & pastries ", food.Id, "#FF8800");
        Assert.Equal("Bread & pastries", edited.Name);
        Assert.Equal("#ff8800", edited.Color);

        // A built-in category can be renamed, coloured and nested too.
        var groceries = cats.Update("groceries", "Supermarket", food.Id, "#123456");
        Assert.Equal(food.Id, groceries.Parent);
        Assert.Equal("Supermarket", Categories.Get("groceries").Name);
        Assert.Equal(CategoryKind.Expense, groceries.Kind);

        // Moved out to the top, and the colour removed.
        Assert.Null(cats.Update(bakery.Id, "Bread & pastries", null, null).Parent);
        Assert.Null(Categories.Get(bakery.Id).Color);

        Assert.Throws<ArgumentException>(() => cats.Update(food.Id, "Food", "groceries", null)); // inside its own subcategory
        Assert.Throws<ArgumentException>(() => cats.Update(food.Id, "Food", food.Id, null)); // inside itself
        Assert.Throws<ArgumentException>(() => cats.Update(food.Id, "Food", "salary", null)); // expenses never go in income
        Assert.Throws<ArgumentException>(() => cats.Update(food.Id, " ", null, null));
        Assert.Throws<ArgumentException>(() => cats.Update(food.Id, "Food", null, "red"));
        Assert.Throws<KeyNotFoundException>(() => cats.Update("u999", "Nope", null, null));
    }

    [Fact]
    public async Task Operations_take_subcategories_and_follow_a_deleted_one_to_its_parent()
    {
        using var s = new TempStore();
        var cats = new CategoryService(s.Db);
        var spec = Sample(Style.EnglishWithdrawalsDeposits, 2025, 10, 30, 4);
        var draft = await s.Imports.ParseAsync("october.pdf", Render(spec));
        s.Imports.Commit(new CommitRequest(draft.Id, null, null, "CAD", AccountKind.Bank, []));

        var food = cats.Create("Food", null, CategoryKind.Expense, "#2e7d32");
        var snacks = cats.Create("Snacks", food.Id, CategoryKind.Expense, null);
        var candy = cats.Create("Candy", snacks.Id, CategoryKind.Expense, null);
        var spent = s.Db.AllTransactions().First(t => t.Amount < 0);
        s.Transactions.SetCategory(spent.Id, snacks.Id, allOfBrand: false);

        var sum = s.Summaries.Summarize(new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 31), Granularity.Month, "CAD");
        Assert.Contains(sum.ByCategory, x => x.Key == snacks.Id && x.Label == "Snacks" && x.Amount == -spent.Amount);

        // Deleting "Snacks": its operation goes to "Food", "Candy" moves up into "Food".
        cats.Delete(snacks.Id);
        Assert.False(Categories.Exists(snacks.Id));
        Assert.Equal(food.Id, s.Db.GetTransaction(spent.Id)!.CategoryId);
        Assert.Equal(food.Id, Categories.Get(candy.Id).Parent);

        Assert.Throws<InvalidOperationException>(() => cats.Delete("groceries"));
    }
}
