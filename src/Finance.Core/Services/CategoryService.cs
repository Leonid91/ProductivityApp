using System.Text.RegularExpressions;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

/// <summary>
/// The person's categories: made, renamed, coloured, nested at any depth and deleted. A category only nests in one of
/// the same kind (expenses in expenses, income in income), so totals never mix spending and income.
/// </summary>
public sealed partial class CategoryService(Database db)
{
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    public Category Create(string name, string? parentId, CategoryKind kind, string? color)
    {
        name = CheckName(name);
        if (parentId is not null) kind = Parent(parentId).Kind;
        var id = db.AddCategory(name, kind, parentId, CheckColor(color));
        return Categories.Get(id);
    }

    public Category Update(string id, string name, string? parentId, string? color)
    {
        var c = Find(id);
        name = CheckName(name);
        if (parentId is not null)
        {
            var parent = Parent(parentId);
            if (parent.Kind != c.Kind) throw new ArgumentException("A category can only be placed in a category of the same kind.");
            // Never inside itself or one of its own nested categories.
            for (var p = parent; p is not null; p = p.Parent is null ? null : Categories.Get(p.Parent))
                if (p.Id == id) throw new ArgumentException("A category cannot be placed inside itself.");
        }
        db.UpdateCategory(id, name, parentId, CheckColor(color));
        return Categories.Get(id);
    }

    /// <summary>
    /// Deletes a category the person made. Its operations go to the category it was in (or "Other" of its kind), its
    /// nested categories move up one level. Built-in categories stay: the engine relies on them.
    /// </summary>
    public void Delete(string id)
    {
        var c = Find(id);
        if (c.BuiltIn) throw new InvalidOperationException("Built-in categories cannot be deleted.");
        var replacement = c.Parent ?? c.Kind switch
        {
            CategoryKind.Income => Categories.OtherIncome,
            CategoryKind.Transfer => Categories.Transfers,
            _ => Categories.OtherExpense,
        };
        db.DeleteCategory(id, replacement, c.Parent);
    }

    private static Category Find(string id) =>
        Categories.Exists(id) ? Categories.Get(id) : throw new KeyNotFoundException("This category no longer exists.");

    private static Category Parent(string id) =>
        Categories.Exists(id) ? Categories.Get(id) : throw new ArgumentException("The category it goes in no longer exists.");

    private static string CheckName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 60) throw new ArgumentException("A category name is 1 to 60 characters.");
        return name;
    }

    private static string? CheckColor(string? color) => color is null or "" ? null
        : ColorPattern().IsMatch(color) ? color.ToLowerInvariant() : throw new ArgumentException("A colour is written #rrggbb.");
}
