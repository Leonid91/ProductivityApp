using System.Globalization;

namespace ProductivityApp.Finance.Services;

/// <summary>
/// One line of the timeline: an account of the person (named without the name of its bank when it starts with it,
/// <see cref="Title"/> being its full name) or, last, everything outside the app. What came in, what went out and the
/// difference, over the period.
/// </summary>
public sealed record TimelineLane(string Id, string Name, string Type, string? Bank, bool Outside, decimal In, decimal Out, decimal Net, string? Title = null)
{
    /// <summary>
    /// The money of the line moved by transfers: <see cref="Start"/> at the start of the period, then after each step
    /// (<see cref="Levels"/>, one per step) what came in added and what went out taken away. It starts at what the line
    /// needs to give everything it gives before receiving more (never below nothing), so a line is as wide as the
    /// money that goes through it.
    /// </summary>
    public decimal Start { get; init; }

    public List<decimal> Levels { get; init; } = [];
}

/// <summary>A bank: the lines of its accounts, one after the other, from <see cref="First"/>.</summary>
public sealed record TimelineBank(string Name, int First, int Count);

/// <summary>
/// The money moved from one line to another in one step of time (a day, a week, a month or a year): its step, the two
/// lines, the total and the number of movements. <see cref="Kind"/>: "inside" a bank, "between" two banks, or "outside"
/// (to or from the outside). <see cref="Lost"/>: what the banks kept on its international transfers. Sent / Received:
/// both amounts of a single international transfer, each in its own currency.
/// </summary>
public sealed record TimelineEvent(int Slot, string Date, int From, int To, decimal Amount, int Count, string Kind,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? Lost = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? Sent = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SentCurrency = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? Received = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ReceivedCurrency = null);

/// <summary>
/// The transfers of a period over time: one line per account, the accounts of a bank together, the outside last; time
/// from left to right in steps (<see cref="Slots"/>: the first day of each) of a day, a week, a month or a year, by
/// the length of the period; every movement from the line it left to the line it reached, at its step. Movements
/// between the same two lines in one step are one event. The totals count each movement once.
/// </summary>
public sealed record TransferTimeline(string Unit, List<string> Slots, List<TimelineLane> Lanes, List<TimelineBank> Banks, List<TimelineEvent> Events,
    int Count, decimal Inside, decimal Between, decimal SentOutside, decimal ReceivedOutside, decimal? Lost)
{
    public const string OutsideId = "__outside";

    public static readonly TransferTimeline Empty = new("day", [], [], [], [], 0, 0, 0, 0, 0, null);

    /// <summary>Steps of a day up to this many days, of a week up to <see cref="WeekSpan"/>, of a month up to <see cref="MonthSpan"/>, of a year beyond.</summary>
    public const int DaySpan = 45, WeekSpan = 190, MonthSpan = 1500;

    /// <summary>Orders of the banks (and of the accounts of one bank) are all tried up to this many: 7! = 5040 orders.</summary>
    private const int TryAll = 7;

    public static TransferTimeline Build(IReadOnlyList<MovementView> movements, DateOnly from, DateOnly to)
    {
        if (movements.Count == 0 || to < from) return Empty;

        // The lines: one per own account (by its id: two accounts of two banks can have one name), one for the outside.
        var keys = new Dictionary<string, (string Name, string Type, string? Bank, bool Outside)>();
        string Key(MovementView m, bool source)
        {
            var (outside, account, name, type, bank) = source ? (m.FromOutside, m.FromAccount, m.From, m.FromType, m.FromBank) : (m.ToOutside, m.ToAccount, m.To, m.ToType, m.ToBank);
            if (outside) { keys.TryAdd(OutsideId, ("Outside the app", "Other accounts & people", null, true)); return OutsideId; }
            var key = account is { } id ? "a" + id.ToString(CultureInfo.InvariantCulture) : "n" + bank + "/" + name;
            keys.TryAdd(key, (name, type ?? "", bank ?? Movements.OtherAccounts, false));
            return key;
        }
        var moves = movements.Select(m => (M: m, From: Key(m, true), To: Key(m, false), Day: Day(m.Date, from, to))).ToList();

        // How much two lines move together: lines moving money together are put close to each other.
        var weight = new Dictionary<(string, string), int>();
        foreach (var x in moves)
        {
            if (x.From == x.To) continue;
            var pair = string.CompareOrdinal(x.From, x.To) < 0 ? (x.From, x.To) : (x.To, x.From);
            weight[pair] = weight.GetValueOrDefault(pair) + 1;
        }
        var order = Order(keys, weight);
        var index = order.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);

        var unit = UnitOf(from, to);
        var slots = SlotsOf(from, to, unit);
        int Slot(DateOnly d)
        {
            // The last step that starts on or before the day.
            int lo = 0, hi = slots.Count - 1;
            while (lo < hi) { var mid = (lo + hi + 1) / 2; if (slots[mid] <= d) lo = mid; else hi = mid - 1; }
            return lo;
        }

        string Kind(string a, string b) => keys[a].Outside || keys[b].Outside ? "outside" : keys[a].Bank == keys[b].Bank ? "inside" : "between";
        var events = moves
            .GroupBy(x => (Slot: Slot(x.Day), x.From, x.To))
            .Select(g =>
            {
                var first = g.OrderBy(x => x.Day).First();
                var lost = g.Any(x => x.M.Lost is not null) ? g.Sum(x => x.M.Lost ?? 0) : (decimal?)null;
                var one = g.Count() == 1 ? first.M : null;
                return new TimelineEvent(g.Key.Slot, Iso(first.Day), index[g.Key.From], index[g.Key.To], g.Sum(x => x.M.Amount), g.Count(), Kind(g.Key.From, g.Key.To),
                    lost, one?.Sent, one?.SentCurrency, one?.Received, one?.ReceivedCurrency);
            })
            .OrderBy(e => e.Slot).ThenBy(e => e.From).ThenBy(e => e.To)
            .ToList();

        // What each line gains and gives at each step: the amount sent leaves it, what arrives (less what the banks kept)
        // comes in.
        var step = new decimal[order.Count, slots.Count];
        foreach (var e in events)
        {
            if (e.From == e.To) continue;
            step[e.From, e.Slot] -= e.Amount;
            step[e.To, e.Slot] += Arrived(e);
        }
        var lanes = order.Select((k, li) =>
        {
            var (name, type, bank, outside) = keys[k];
            var inn = moves.Where(x => x.To == k && x.From != k).Sum(x => x.M.Amount);
            var out_ = moves.Where(x => x.From == k && x.To != k).Sum(x => x.M.Amount);
            var shown = outside ? name : Short(name, bank);
            decimal level = 0, lowest = 0;
            for (var s = 0; s < slots.Count; s++) lowest = Math.Min(lowest, level += step[li, s]);
            var levels = new List<decimal>(slots.Count);
            level = -lowest;
            for (var s = 0; s < slots.Count; s++) levels.Add(level += step[li, s]);
            return new TimelineLane(k, shown, type, bank, outside, inn, out_, inn - out_, shown == name ? null : name) { Start = -lowest, Levels = levels };
        }).ToList();

        var banks = new List<TimelineBank>();
        for (var i = 0; i < lanes.Count; i++)
        {
            if (lanes[i].Outside) continue;
            if (banks.Count > 0 && banks[^1].Name == lanes[i].Bank) banks[^1] = banks[^1] with { Count = banks[^1].Count + 1 };
            else banks.Add(new TimelineBank(lanes[i].Bank!, i, 1));
        }

        decimal Total(string kind, Func<(MovementView M, string From, string To, DateOnly Day), bool>? where = null) =>
            moves.Where(x => Kind(x.From, x.To) == kind && (where is null || where(x))).Sum(x => x.M.Amount);
        return new TransferTimeline(unit, [.. slots.Select(Iso)], lanes, banks, events, moves.Count,
            Total("inside"), Total("between"), Total("outside", x => keys[x.To].Outside), Total("outside", x => keys[x.From].Outside && !keys[x.To].Outside),
            movements.Any(m => m.Lost is not null) ? movements.Sum(m => m.Lost ?? 0) : null);
    }

    /// <summary>What reaches the other line of an event: the amount sent less what the banks kept on the way.</summary>
    public static decimal Arrived(TimelineEvent e) => Math.Max(0, e.Amount - (e.Lost ?? 0));

    /// <summary>The step of time: short enough to tell the days of a month apart, long enough for years to fit.</summary>
    public static string UnitOf(DateOnly from, DateOnly to)
    {
        var days = to.DayNumber - from.DayNumber + 1;
        return days <= DaySpan ? "day" : days <= WeekSpan ? "week" : days <= MonthSpan ? "month" : "year";
    }

    /// <summary>The first day of every step: weeks of seven days from the first day, months and years of the calendar.</summary>
    private static List<DateOnly> SlotsOf(DateOnly from, DateOnly to, string unit)
    {
        var list = new List<DateOnly>();
        for (var d = from; d <= to;)
        {
            list.Add(d);
            var next = unit switch
            {
                "day" => d.AddDays(1),
                "week" => d.AddDays(7),
                "month" => new DateOnly(d.Year, d.Month, 1).AddMonths(1),
                _ => d.Year >= DateOnly.MaxValue.Year ? DateOnly.MaxValue : new DateOnly(d.Year + 1, 1, 1),
            };
            if (next <= d) break;
            d = next;
        }
        return list;
    }

    /// <summary>The day of a movement, kept inside the period (a half dated just outside it still shows at its edge).</summary>
    private static DateOnly Day(string iso, DateOnly from, DateOnly to) =>
        DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? (d < from ? from : d > to ? to : d) : from;

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Inside its bank an account named after it is named without it ("Northwind Bank Savings" → "Savings").</summary>
    private static string Short(string name, string? bank)
    {
        if (bank is not { Length: > 0 } || !name.StartsWith(bank, StringComparison.OrdinalIgnoreCase)) return name;
        var rest = name[bank.Length..].TrimStart(' ', '-', '·', ':', ',');
        return rest.Length == 0 ? name : char.ToUpperInvariant(rest[0]) + rest[1..];
    }

    /// <summary>
    /// The lines from top to bottom: the accounts of a bank together, the outside last, in the order that keeps the
    /// lines moving money together the closest (the sum, over every pair, of movements × lines apart, as small as it can
    /// be). Every order of the banks is tried, then every order of the accounts of each bank; with more than
    /// <see cref="TryAll"/> of them they stay in the order of their names. The same movements always give the same order.
    /// </summary>
    private static List<string> Order(Dictionary<string, (string Name, string Type, string? Bank, bool Outside)> keys, Dictionary<(string, string), int> weight)
    {
        var groups = keys.Where(k => !k.Value.Outside)
            .GroupBy(k => k.Value.Bank!)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(k => k.Value.Name, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key).ToList())
            .ToList();
        var outside = keys.ContainsKey(OutsideId);

        List<string> Flat(IEnumerable<List<string>> gs)
        {
            var flat = gs.SelectMany(g => g).ToList();
            if (outside) flat.Add(OutsideId);
            return flat;
        }
        long Cost(List<string> flat)
        {
            var at = new Dictionary<string, int>(flat.Count);
            for (var i = 0; i < flat.Count; i++) at[flat[i]] = i;
            long c = 0;
            foreach (var (pair, w) in weight) c += (long)w * Math.Abs(at[pair.Item1] - at[pair.Item2]);
            return c;
        }

        if (groups.Count is > 1 and <= TryAll)
        {
            var best = groups;
            var bestCost = Cost(Flat(groups));
            foreach (var p in Permutations(groups.Count))
            {
                var candidate = p.Select(i => groups[i]).ToList();
                var c = Cost(Flat(candidate));
                if (c < bestCost) (best, bestCost) = (candidate, c);
            }
            groups = best;
        }
        // Twice: the best order of one bank's accounts depends on where the accounts of the others ended up.
        for (var pass = 0; pass < 2; pass++)
            for (var g = 0; g < groups.Count; g++)
            {
                var accounts = groups[g];
                if (accounts.Count is < 2 or > TryAll) continue;
                var bestCost = Cost(Flat(groups));
                var best = accounts;
                foreach (var p in Permutations(accounts.Count))
                {
                    groups[g] = [.. p.Select(i => accounts[i])];
                    var c = Cost(Flat(groups));
                    if (c < bestCost) (best, bestCost) = (groups[g], c);
                }
                groups[g] = best;
            }
        return Flat(groups);
    }

    /// <summary>Every order of 0..n-1, in lexicographic order (the first one, 0..n-1 itself, comes first).</summary>
    private static IEnumerable<int[]> Permutations(int n)
    {
        var a = Enumerable.Range(0, n).ToArray();
        while (true)
        {
            yield return (int[])a.Clone();
            var i = n - 2;
            while (i >= 0 && a[i] >= a[i + 1]) i--;
            if (i < 0) yield break;
            var j = n - 1;
            while (a[j] <= a[i]) j--;
            (a[i], a[j]) = (a[j], a[i]);
            Array.Reverse(a, i + 1, n - i - 1);
        }
    }
}
