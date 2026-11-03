using System.Globalization;
using ProductivityApp.Finance.Classification;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

// ==================================================================================================== recurring

/// <summary>
/// Finds the payments and income that come back regularly (rent, insurance, phone, subscriptions, salary...),
/// over the whole history, so each period can show them apart with their total.
/// </summary>
public static class RecurringDetector
{
    /// <summary>Recurring by nature: one payment in these categories is enough.</summary>
    public static readonly HashSet<string> BillCategories = ["housing", "utilities", "telecom", "subscriptions", "insurance", "salary"];

    /// <summary>
    /// Bought again and again at the same price without being a commitment (a coffee, a bus fare, the same take-away):
    /// never recurring, whatever the amounts.
    /// </summary>
    public static readonly HashSet<string> EverydayCategories = ["groceries", "restaurants", "transport", "car", "cash", "travel", "gifts", "refunds"];

    /// <summary>
    /// Returns, for every recurring operation, how often it comes back ("monthly", "twice a month", "yearly").
    /// Operations are grouped by brand and direction, then by amount (two phone lines at the same operator are two
    /// series). A series is recurring when it shows up in at least two different months at a steady amount, at most
    /// a few times a month.
    /// </summary>
    public static Dictionary<long, string> Detect(IReadOnlyList<Transaction> all)
    {
        var result = new Dictionary<long, string>();
        var counted = all.Where(t => Categories.Get(t.CategoryId).Kind != CategoryKind.Transfer).ToList();

        // A bill category is enough on its own, except for a known shop the bank filed under a bill heading
        // (an Amazon purchase under "Internet"): that one must come back like anything else.
        var shops = BuiltInBrands.All.Where(b => !BillCategories.Contains(b.CategoryId)).Select(b => b.Brand).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in counted.Where(t => BillCategories.Contains(t.CategoryId) && !shops.Contains(t.Brand)))
            result[t.Id] = "monthly";

        foreach (var group in counted.GroupBy(t => (Brand: t.Brand.Trim().ToUpperInvariant(), In: t.Amount > 0)))
        {
            var income = group.Key.In;
            var tolerance = income ? 1.4m : 1.25m;
            foreach (var series in Clusters(group.ToList(), tolerance))
            {
                var months = series.GroupBy(t => t.Date.Year * 12 + t.Date.Month).ToList();
                if (months.Count < 2) continue;
                var perMonth = (double)series.Count / months.Count;
                var category = series.GroupBy(t => t.CategoryId).OrderByDescending(g => g.Count()).First().Key;
                if (EverydayCategories.Contains(category)) continue;
                var bill = (BillCategories.Contains(category) && !shops.Contains(series[0].Brand)) || income;
                // Something else (a hairdresser, an online shop) must come back exactly once a month at a near-equal price.
                if (!bill && (months.Any(m => m.Count() != 1) || Spread(series) > 1.06m)) continue;
                if (perMonth > 2.5) continue;
                var span = months.Max(m => m.Key) - months.Min(m => m.Key);
                var frequency = months.Count >= 2 && span >= 11 * (months.Count - 1) ? "yearly"
                    : perMonth >= 1.8 ? "twice a month"
                    : "monthly";
                foreach (var t in series) result[t.Id] = frequency;
            }
        }
        return result;
    }

    private static decimal Spread(List<Transaction> s)
    {
        var a = s.Select(t => Math.Abs(t.Amount)).ToList();
        return a.Min() == 0 ? decimal.MaxValue : a.Max() / a.Min();
    }

    /// <summary>Splits operations into series of similar amounts (each within <paramref name="tolerance"/> times the smallest).</summary>
    private static IEnumerable<List<Transaction>> Clusters(List<Transaction> items, decimal tolerance)
    {
        var sorted = items.OrderBy(t => Math.Abs(t.Amount)).ToList();
        var current = new List<Transaction>();
        foreach (var t in sorted)
        {
            if (current.Count > 0 && Math.Abs(t.Amount) > Math.Abs(current[0].Amount) * tolerance)
            {
                yield return current;
                current = [];
            }
            current.Add(t);
        }
        if (current.Count > 0) yield return current;
    }
}

// ==================================================================================================== movements

/// <summary>
/// One movement of money, seen once: from an account (or the outside) to another account (or the outside).
/// FromType / ToType name the type of an own account ("Chequing account"); null for the outside.
/// </summary>
public sealed record MovementView(
    string Date, string CategoryId, string Label, decimal Amount, string From, string To, bool FromOutside, bool ToOutside,
    long EditId, long? OtherId, string? FromType = null, string? ToType = null);

/// <summary>The total moved from one place to another over a period.</summary>
public sealed record Flow(string From, string To, decimal Amount, bool FromOutside, bool ToOutside, int Count);

public static class Movements
{
    public const string Outside = "Outside (not imported)";
    public const string OutsideInvestments = "Investments (not imported)";
    public const string OutsideBank = "Bank account (not imported)";
    public const string OutsideCard = "Credit card (not imported)";

    /// <summary>Names the classifier gives to bank movements that do not say who is on the other side.</summary>
    private static readonly HashSet<string> GenericBrands = new(StringComparer.OrdinalIgnoreCase)
    {
        "Interac e-Transfer", "Transfer between accounts", "Credit card payment", "Bill payment", "Unknown", "Bank fees",
    };

    /// <summary>
    /// Every transfer, card payment and investment of the period, each movement once. Its two halves (when both accounts
    /// are imported) make one line; a half alone goes to or comes from the outside: an account that is not imported,
    /// another person, an investment account.
    /// </summary>
    public static List<MovementView> Build(IReadOnlyList<Transaction> period, IReadOnlyDictionary<long, Transaction> linked, IReadOnlyDictionary<long, Account> accounts)
    {
        string? Type(string name, bool outside) => outside ? null
            : accounts.Values.FirstOrDefault(a => a.Name == name) is { } a ? AccountTypes.Label(a.Kind, a.System) : null;
        var raw = BuildRaw(period, linked, accounts);
        return raw.Select(m => m with { FromType = Type(m.From, m.FromOutside), ToType = Type(m.To, m.ToOutside) }).ToList();
    }

    private static List<MovementView> BuildRaw(IReadOnlyList<Transaction> period, IReadOnlyDictionary<long, Transaction> linked, IReadOnlyDictionary<long, Account> accounts)
    {
        var list = new List<MovementView>();
        var seen = new HashSet<long>();
        var cards = accounts.Values.Where(a => a.Kind == AccountKind.CreditCard).ToList();
        string Name(long id) => accounts.TryGetValue(id, out var a) ? a.Name : "Account";
        foreach (var t in period.Where(t => Categories.IsTransfer(t.CategoryId)).OrderBy(t => t.Date).ThenBy(t => t.Id))
        {
            if (!seen.Add(t.Id)) continue;
            var other = t.LinkedId is { } lid && linked.TryGetValue(lid, out var o) ? o : null;
            if (other is not null)
            {
                seen.Add(other.Id);
                var (out_, in_) = t.Amount < 0 ? (t, other) : (other, t);
                list.Add(new MovementView(Iso(out_.Date), out_.CategoryId, out_.Brand, Math.Abs(t.Amount), Name(out_.AccountId), Name(in_.AccountId),
                    false, false, out_.Id, in_.Id));
                continue;
            }
            var kind = accounts.TryGetValue(t.AccountId, out var acc) ? acc.Kind : AccountKind.Bank;
            string counterpart;
            var counterpartOutside = true;
            if (t.CategoryId == Categories.CardPayments && kind != AccountKind.CreditCard && t.Amount < 0)
            {
                // A card paid from this account: the card is the person's own even when its other half is not imported.
                if (cards.Count == 1) { counterpart = cards[0].Name; counterpartOutside = false; }
                else counterpart = OutsideCard;
            }
            else if (kind == AccountKind.CreditCard && t.Amount > 0) counterpart = OutsideBank;
            else if (t.CategoryId == Categories.Investments) counterpart = OutsideInvestments;
            // A transfer that names someone (Interac from or to a person, another bank) says who is on the other side.
            else if (!GenericBrands.Contains(t.Brand)) counterpart = $"{t.Brand} (not imported)";
            else counterpart = Outside;
            list.Add(t.Amount < 0
                ? new MovementView(Iso(t.Date), t.CategoryId, t.Brand, -t.Amount, Name(t.AccountId), counterpart, false, counterpartOutside, t.Id, null)
                : new MovementView(Iso(t.Date), t.CategoryId, t.Brand, t.Amount, counterpart, Name(t.AccountId), counterpartOutside, false, t.Id, null));
        }
        return list;
    }

    public static List<Flow> Flows(IEnumerable<MovementView> movements) => movements
        .GroupBy(m => (m.From, m.To, m.FromOutside, m.ToOutside))
        .Select(g => new Flow(g.Key.From, g.Key.To, g.Sum(m => m.Amount), g.Key.FromOutside, g.Key.ToOutside, g.Count()))
        .OrderByDescending(f => f.Amount)
        .ToList();

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
