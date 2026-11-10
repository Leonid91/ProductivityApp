using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.Tests;

/// <summary>Tags on operations and on categories, several per operation, and the Tags pie that never counts money twice.</summary>
[Collection("categories")]
public class TagTests
{
    private static async Task<TempStore> October()
    {
        var s = new TempStore();
        var draft = await s.Imports.ParseAsync("october.pdf", Render(Sample(Style.EnglishWithdrawalsDeposits, 2025, 10, 30, 4)));
        s.Imports.Commit(new CommitRequest(draft.Id, null, null, "CAD", AccountKind.Bank, [], BankId: s.Bank));
        return s;
    }

    private static PeriodSummary Sum(TempStore s) => s.Summaries.Summarize(new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 31), Granularity.Month, "CAD");

    [Fact]
    public async Task Tags_are_made_renamed_and_deleted_and_none_come_with_the_app()
    {
        using var s = await October();
        var tags = new TagService(s.Db);
        Assert.Empty(tags.All());
        var trip = tags.Create(" Trip to Quebec ", "#1E88E5");
        Assert.Equal("Trip to Quebec", trip.Name);
        Assert.Equal("#1e88e5", trip.Color);
        Assert.Throws<ArgumentException>(() => tags.Create("trip to quebec", null)); // names are unique, whatever the case
        Assert.Throws<ArgumentException>(() => tags.Create("", null));
        Assert.Throws<ArgumentException>(() => tags.Create(new string('x', 41), null));
        Assert.Throws<ArgumentException>(() => tags.Create("Work", "blue"));
        var renamed = tags.Update(trip.Id, "Holidays", null);
        Assert.Equal("Holidays", renamed.Name);
        Assert.Null(renamed.Color);

        var op = s.Db.AllTransactions().First(t => t.Amount < 0);
        tags.SetOperationTags(op.Id, [trip.Id]);
        Assert.Equal([trip.Id], Sum(s).Transactions.Single(t => t.Id == op.Id).Tags);
        tags.Delete(trip.Id);
        Assert.Empty(tags.All());
        Assert.Empty(Sum(s).Transactions.Single(t => t.Id == op.Id).Tags!);
        Assert.Throws<KeyNotFoundException>(() => tags.SetOperationTags(op.Id, [trip.Id]));
    }

    [Fact]
    public async Task A_tag_on_a_category_reaches_every_operation_of_it_and_of_the_categories_nested_in_it()
    {
        using var s = await October();
        var tags = new TagService(s.Db);
        var cats = new CategoryService(s.Db);
        var food = cats.Create("Food (test)", null, CategoryKind.Expense, null);
        var snacks = cats.Create("Snacks (test)", food.Id, CategoryKind.Expense, null);
        var essentials = tags.Create("Essentials", null);
        tags.SetCategoryTags(food.Id, [essentials.Id]);

        var spent = s.Db.AllTransactions().Where(t => t.Amount < 0 && !Categories.IsTransfer(t.CategoryId)).ToList();
        s.Transactions.SetCategory(spent[0].Id, food.Id, allOfBrand: false);
        s.Transactions.SetCategory(spent[1].Id, snacks.Id, allOfBrand: false);
        var sum = Sum(s);
        var byId = sum.Transactions.ToDictionary(t => t.Id);
        Assert.Equal([essentials.Id], byId[spent[0].Id].Tags);
        Assert.Equal([essentials.Id], byId[spent[1].Id].Tags);
        Assert.Empty(byId[spent[0].Id].OwnTags!); // the category's tag is not copied onto the operation
        Assert.DoesNotContain(sum.Transactions, t => t.Id != spent[0].Id && t.Id != spent[1].Id && t.Tags!.Length > 0);
        Assert.Equal(-(spent[0].Amount + spent[1].Amount), sum.TagPie.Single().Amount);

        // An operation moved out of the category loses the tag; removed from the category, it leaves all of them.
        s.Transactions.SetCategory(spent[1].Id, "shopping", allOfBrand: false);
        Assert.Empty(Sum(s).Transactions.Single(t => t.Id == spent[1].Id).Tags!);
        tags.SetCategoryTags(food.Id, []);
        Assert.Empty(Sum(s).TagPie);
        cats.Delete(snacks.Id);
        cats.Delete(food.Id);
    }

    [Fact]
    public async Task Several_tags_per_category_and_operation_count_the_money_once()
    {
        using var s = await October();
        var tags = new TagService(s.Db);
        var travel = tags.Create("Travel", null);
        var work = tags.Create("Work", null);
        var family = tags.Create("Family", null);
        // Restaurants carries two tags; one restaurant meal also has its own third tag, and one carries a tag twice.
        tags.SetCategoryTags("restaurants", [travel.Id, work.Id]);
        var ops = s.Db.AllTransactions().Where(t => t.Amount < 0 && !Categories.IsTransfer(t.CategoryId)).ToList();
        var meal = ops.First(t => t.CategoryId != "restaurants");
        s.Transactions.SetCategory(meal.Id, "restaurants", allOfBrand: false);
        tags.SetOperationTags(meal.Id, [family.Id, work.Id]);
        var other = ops.First(t => t.Id != meal.Id && t.CategoryId != "restaurants");
        tags.SetOperationTags(other.Id, [family.Id]);

        var sum = Sum(s);
        var byId = sum.Transactions.ToDictionary(t => t.Id);
        Assert.Equal(new[] { travel.Id, work.Id, family.Id }.Order(), byId[meal.Id].Tags);
        Assert.Equal(new[] { family.Id, work.Id }.Order(), byId[meal.Id].OwnTags!.Order());

        // Each tagged expense in exactly one slice: the pie adds up to the tagged expenses, counted once.
        var tagged = sum.Transactions.Where(t => t.Amount < 0 && !Categories.IsTransfer(t.CategoryId) && t.Tags!.Length > 0).Sum(t => -t.Amount);
        Assert.Equal(tagged, sum.TagPie.Sum(x => x.Amount));
        Assert.Equal(1, sum.TagPie.Sum(x => x.Share), 6);
        Assert.Contains(sum.TagPie, x => x.Label == "Family + Travel + Work" && x.Amount == -meal.Amount);
        Assert.Contains(sum.TagPie, x => x.Label == "Family" && x.Amount == -other.Amount);
        Assert.Contains(sum.TagPie, x => x.Label == "Travel + Work");
        // Totals of the period never change with tags.
        tags.SetCategoryTags("restaurants", []);
        Assert.Equal(sum.Expenses, Sum(s).Expenses);
        Assert.Equal(sum.Balance, Sum(s).Balance);
    }

    [Fact]
    public async Task The_summary_shows_a_month_or_a_year_alone_and_cuts_other_periods_in_months_or_years()
    {
        using var s = await October();
        var month = Sum(s);
        var only = Assert.Single(month.Breakdown);
        Assert.Equal(("2025-10-01", "2025-10-31"), (only.From, only.To));
        Assert.Equal(month.Balance, only.Balance);
        Assert.StartsWith("October 2025", only.Title);

        var year = s.Summaries.Summarize(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), Granularity.Year, "CAD");
        // A year: the year alone.
        var whole = Assert.Single(year.Breakdown);
        Assert.Equal(("2025-01-01", "2025-12-31", year.Balance), (whole.From, whole.To, whole.Balance));

        // A custom range from mid-September to mid-November: three parts, each kept inside the range.
        var custom = s.Summaries.Summarize(new DateOnly(2025, 9, 15), new DateOnly(2025, 11, 10), Granularity.Range, "CAD");
        Assert.Equal(["2025-09-15", "2025-10-01", "2025-11-01"], custom.Breakdown.Select(b => b.From));
        Assert.Equal("2025-11-10", custom.Breakdown[^1].To);
        Assert.Equal(custom.Balance, custom.Breakdown.Sum(b => b.Balance));

        // More than a year: in years.
        var two = s.Summaries.Summarize(new DateOnly(2024, 6, 1), new DateOnly(2026, 2, 28), Granularity.Range, "CAD");
        Assert.Equal(["2024-06-01", "2025-01-01", "2026-01-01"], two.Breakdown.Select(b => b.From));
        Assert.Equal(two.Balance, two.Breakdown.Sum(b => b.Balance));
        Assert.Equal(month.Balance, two.Breakdown[1].Balance);
    }
}
