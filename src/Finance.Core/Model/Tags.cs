namespace ProductivityApp.Finance.Model;

/// <summary>
/// A tag the person makes (none come with the app): put on single operations, or on a category so that every
/// operation of that category (and of the categories nested in it) carries it. An operation can carry several tags.
/// </summary>
public sealed record Tag(long Id, string Name, string? Color);

/// <summary>Which tags operations and categories carry, read once per change of the data.</summary>
public sealed class TagIndex
{
    public static readonly TagIndex Empty = new([], new Dictionary<long, long[]>(), new Dictionary<string, long[]>());

    public TagIndex(IReadOnlyList<Tag> tags, IReadOnlyDictionary<long, long[]> byTransaction, IReadOnlyDictionary<string, long[]> byCategory)
    {
        Tags = tags;
        ByTransaction = byTransaction;
        ByCategory = byCategory;
        ById = tags.ToDictionary(t => t.Id);
    }

    public IReadOnlyList<Tag> Tags { get; }
    public IReadOnlyDictionary<long, Tag> ById { get; }
    /// <summary>Tags put on the operation itself.</summary>
    public IReadOnlyDictionary<long, long[]> ByTransaction { get; }
    /// <summary>Tags put on the category itself (not counting the categories it is nested in).</summary>
    public IReadOnlyDictionary<string, long[]> ByCategory { get; }

    public long[] Own(long transactionId) => ByTransaction.TryGetValue(transactionId, out var t) ? t : [];

    /// <summary>Tags an operation of this category gets from it: those of the category and of every category it is nested in.</summary>
    public long[] FromCategory(string categoryId)
    {
        var tags = new SortedSet<long>();
        var c = Categories.Get(categoryId);
        for (var depth = 0; depth < 64; depth++)
        {
            if (ByCategory.TryGetValue(c.Id, out var t)) tags.UnionWith(t);
            if (c.Parent is null || !Categories.Exists(c.Parent)) break;
            c = Categories.Get(c.Parent);
        }
        return [.. tags];
    }

    /// <summary>Every tag an operation carries, its own and its category's, each once, in id order.</summary>
    public long[] Of(long transactionId, string categoryId)
    {
        var own = Own(transactionId);
        var inherited = FromCategory(categoryId);
        if (inherited.Length == 0) return own;
        if (own.Length == 0) return inherited;
        return [.. own.Union(inherited).Where(ById.ContainsKey).OrderBy(x => x)];
    }
}
