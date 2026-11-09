using System.Text.RegularExpressions;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

/// <summary>The person's tags: made, renamed, coloured, deleted; put on operations and on categories.</summary>
public sealed partial class TagService(Database db)
{
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    public IReadOnlyList<Tag> All() => db.TagIndex().Tags;

    public Tag Create(string name, string? color)
    {
        name = CheckName(name, 0);
        var id = db.AddTag(name, CheckColor(color));
        return db.TagIndex().ById[id];
    }

    public Tag Update(long id, string name, string? color)
    {
        Find(id);
        db.UpdateTag(id, CheckName(name, id), CheckColor(color));
        return db.TagIndex().ById[id];
    }

    public void Delete(long id)
    {
        Find(id);
        db.DeleteTag(id);
    }

    /// <summary>The tags of one operation itself; the tags it gets from its category stay with the category.</summary>
    public void SetOperationTags(long operationId, IReadOnlyCollection<long> tagIds)
    {
        if (db.GetTransaction(operationId) is null) throw new KeyNotFoundException("This operation no longer exists.");
        foreach (var t in tagIds) Find(t);
        db.SetOperationTags(operationId, tagIds);
    }

    /// <summary>The tags a category gives to every operation in it, and in the categories nested in it.</summary>
    public void SetCategoryTags(string categoryId, IReadOnlyCollection<long> tagIds)
    {
        if (!Categories.Exists(categoryId)) throw new KeyNotFoundException("This category no longer exists.");
        foreach (var t in tagIds) Find(t);
        db.SetCategoryTags(categoryId, tagIds);
    }

    private void Find(long id)
    {
        if (!db.TagExists(id)) throw new KeyNotFoundException("This tag no longer exists.");
    }

    private string CheckName(string name, long id)
    {
        name = name.Trim();
        if (name.Length is 0 or > 40) throw new ArgumentException("A tag name is 1 to 40 characters.");
        if (db.TagNameTaken(name, id)) throw new ArgumentException($"There is already a tag named “{name}”.");
        return name;
    }

    private static string? CheckColor(string? color) => color is null or "" ? null
        : ColorPattern().IsMatch(color) ? color.ToLowerInvariant() : throw new ArgumentException("A colour is written #rrggbb.");
}
