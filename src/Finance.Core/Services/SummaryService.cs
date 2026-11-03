using System.Globalization;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

public sealed record Slice(string Key, string Label, decimal Amount, double Share);

/// <summary>One day, month or year. Moved is money moved between own accounts; CardPaid is credit card payments; Invested is investments.</summary>
public sealed record Bar(string Key, string Label, decimal Expenses, decimal Earnings, decimal Moved = 0, decimal CardPaid = 0, decimal Invested = 0);

/// <param name="Recurring">How often it comes back ("monthly", "twice a month", "yearly"), or null when it is not recurring.</param>
public sealed record TransactionView(
    long Id, string Date, decimal Amount, string Currency, string Brand, string CategoryId, string CategorySource,
    string BrandSource, string RawLabel, string Note, long AccountId, long? StatementId,
    string? Recurring = null, long? LinkedId = null, decimal? Earned = null);

public sealed class PeriodSummary
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required Granularity Granularity { get; init; }
    public required string Title { get; init; }
    public required string Currency { get; init; }
    public required decimal Expenses { get; init; }
    public required decimal Earnings { get; init; }
    public required decimal Balance { get; init; }
    /// <summary>Money moved between own accounts, left out of expenses and earnings.</summary>
    public required decimal TransfersOut { get; init; }
    public required decimal TransfersIn { get; init; }
    public required List<TransactionView> Transactions { get; init; }
    public required List<Slice> ByCategory { get; init; }
    public required List<Slice> CategoryPie { get; init; }
    public required List<Slice> ByBrand { get; init; }
    public required List<Slice> BrandPie { get; init; }
    public required List<Bar> Series { get; init; }
    /// <summary>Recurring payments of the period (rent, insurance, phone, subscriptions...) and their total.</summary>
    public required decimal RecurringExpenses { get; init; }
    public required List<Slice> RecurringExpensePie { get; init; }
    /// <summary>Every income of the period by source (salary, refunds, interest, the earned part of investments...).</summary>
    public required List<Slice> IncomePie { get; init; }
    /// <summary>Transfers, card payments and investments, each movement once, from one account to another.</summary>
    public required List<MovementView> Movements { get; init; }
    public required List<Flow> Flows { get; init; }
    /// <summary>The money-moved diagram, laid out (same drawing on screen and in the PDF).</summary>
    public required FlowDiagram Diagram { get; init; }
    /// <summary>Recurring payments without housing (rent, mortgage): the subscription burden.</summary>
    public required decimal RecurringWithoutHousing { get; init; }
    /// <summary><see cref="RecurringWithoutHousing"/> as a share of earnings; null without earnings.</summary>
    public double? RecurringWithoutHousingShare { get; init; }
    /// <summary>Expenses as a share of earnings; null without earnings.</summary>
    public double? ExpensesShare { get; init; }
    /// <summary>"day", "month" or "year": what one bar of <see cref="Series"/> stands for.</summary>
    public required string SeriesUnit { get; init; }
}

public sealed record MonthEntry(int Year, int Month, int Count);

public sealed record PeriodIndex(List<MonthEntry> Months, List<int> Years, List<string> Currencies, string? First, string? Last);

public sealed class SummaryService(Database db)
{
    /// <summary>Pies show this many slices, the rest folds into "Other".</summary>
    public const int PieSlices = 5;

    public PeriodIndex Index(string currency)
    {
        var months = db.MonthsWithData();
        var currencies = months.Select(m => m.Currency).Distinct().OrderBy(c => c == currency ? 0 : 1).ThenBy(c => c).ToList();
        var mine = months.Where(m => m.Currency == currency).Select(m => new MonthEntry(m.Year, m.Month, m.Count)).ToList();
        var years = mine.Select(m => m.Year).Distinct().OrderByDescending(y => y).ToList();
        // The day of the first operation: "All time" runs from it to today.
        var first = db.AllTransactions().FirstOrDefault(t => t.Currency == currency)?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string? last = mine.Count == 0 ? null : $"{mine[0].Year:D4}-{mine[0].Month:D2}";
        return new PeriodIndex(mine, years, currencies, first, last);
    }

    private (long Version, string Currency, Dictionary<long, string> Recurring, Dictionary<long, Transaction> ById)? _history;
    private readonly Lock _gate = new();

    /// <summary>
    /// What is recurring is found over the whole history, the same for every period: worked out once, again only
    /// after the data changes.
    /// </summary>
    private (Dictionary<long, string> Recurring, Dictionary<long, Transaction> ById) History(string currency)
    {
        lock (_gate)
        {
            if (_history is { } h && h.Version == db.Version && h.Currency == currency) return (h.Recurring, h.ById);
            var version = db.Version;
            var everything = db.AllTransactions().Where(t => t.Currency == currency).ToList();
            var recurring = RecurringDetector.Detect(everything);
            var byId = everything.ToDictionary(t => t.Id);
            _history = (version, currency, recurring, byId);
            return (recurring, byId);
        }
    }

    public PeriodSummary Summarize(DateOnly from, DateOnly to, Granularity g, string currency)
    {
        if (to < from) throw new ArgumentException("The period ends before it starts.");
        var txs = db.Transactions(from, to).Where(t => t.Currency == currency).ToList();
        var (recurring, byId) = History(currency);
        var accounts = db.Accounts().ToDictionary(a => a.Id);
        var movements = Movements.Build(txs, byId, accounts);
        var fromIso = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toIso = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        decimal expenses = 0, earnings = 0, transfersOut = 0, transfersIn = 0;
        var byCategory = new Dictionary<string, decimal>();
        var byBrand = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var brandNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var incomeBy = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in txs)
        {
            var transfer = Categories.Get(t.CategoryId).Kind == CategoryKind.Transfer;
            if (transfer)
            {
                if (t.Amount < 0) transfersOut -= t.Amount; else transfersIn += t.Amount;
                // The part of an investment coming back that the person says was gain is income.
                if (t.Amount > 0 && t.Earned is > 0 and var gain)
                {
                    earnings += gain;
                    incomeBy[t.Brand] = incomeBy.GetValueOrDefault(t.Brand) + gain;
                    brandNames.TryAdd(t.Brand, t.Brand);
                }
                continue;
            }
            if (t.Amount < 0)
            {
                var spent = -t.Amount;
                expenses += spent;
                byCategory[t.CategoryId] = byCategory.GetValueOrDefault(t.CategoryId) + spent;
                byBrand[t.Brand] = byBrand.GetValueOrDefault(t.Brand) + spent;
                brandNames.TryAdd(t.Brand, t.Brand);
            }
            else
            {
                earnings += t.Amount;
                incomeBy[t.Brand] = incomeBy.GetValueOrDefault(t.Brand) + t.Amount;
                brandNames.TryAdd(t.Brand, t.Brand);
            }
        }

        var categorySlices = Slices(byCategory.Select(kv => (kv.Key, Categories.Get(kv.Key).Name, kv.Value)), expenses);
        var recurringOut = txs.Where(t => t.Amount < 0 && recurring.ContainsKey(t.Id)).ToList();
        var recurringOutTotal = -recurringOut.Sum(t => t.Amount);
        var burden = -recurringOut.Where(t => t.CategoryId != "housing").Sum(t => t.Amount);
        var recurringOutSlices = Slices(recurringOut.GroupBy(t => t.Brand, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, g.First().Brand, -g.Sum(t => t.Amount))), recurringOutTotal);
        var incomeSlices = Slices(incomeBy.Select(kv => (kv.Key, brandNames[kv.Key], kv.Value)), earnings);
        var brandSlices = Slices(byBrand.Select(kv => (kv.Key, brandNames[kv.Key], kv.Value)), expenses);

        var (series, unit) = Series(txs, from, to, g);

        return new PeriodSummary
        {
            From = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            To = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Granularity = g,
            Title = Formatting.PeriodTitle(from, to, g),
            Currency = currency,
            Expenses = expenses,
            Earnings = earnings,
            Balance = earnings - expenses,
            TransfersOut = transfersOut,
            TransfersIn = transfersIn,
            Transactions = txs.OrderBy(t => t.Date).ThenBy(t => t.Id).Select(t => View(t, recurring.GetValueOrDefault(t.Id))).ToList(),
            ByCategory = categorySlices,
            CategoryPie = FoldPie(categorySlices),
            ByBrand = brandSlices,
            BrandPie = FoldPie(brandSlices),
            Series = series,
            SeriesUnit = unit,
            RecurringExpenses = recurringOutTotal,
            RecurringExpensePie = recurringOutSlices,
            IncomePie = incomeSlices,
            Movements = movements,
            Flows = Movements.Flows(movements),
            Diagram = FlowDiagram.Build(movements),
            RecurringWithoutHousing = burden,
            RecurringWithoutHousingShare = earnings > 0 ? (double)(burden / earnings) : null,
            ExpensesShare = earnings > 0 ? (double)(expenses / earnings) : null,
        };
    }

    public static TransactionView View(Transaction t) => View(t, null);

    public static TransactionView View(Transaction t, string? recurring) => new(
        t.Id, t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), t.Amount, t.Currency, t.Brand, t.CategoryId,
        t.CategorySource.ToString(), t.BrandSource.ToString(), t.RawLabel, t.Note, t.AccountId, t.StatementId,
        recurring, t.LinkedId, t.Earned);

    private static List<Slice> Slices(IEnumerable<(string Key, string Label, decimal Amount)> items, decimal total) =>
        items.OrderByDescending(x => x.Amount).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Select(x => new Slice(x.Key, x.Label, x.Amount, total == 0 ? 0 : (double)(x.Amount / total)))
            .ToList();

    public static List<Slice> FoldPie(List<Slice> slices)
    {
        if (slices.Count <= PieSlices + 1) return slices;
        var head = slices.Take(PieSlices).ToList();
        var rest = slices.Skip(PieSlices).ToList();
        head.Add(new Slice("__other", "Other", rest.Sum(s => s.Amount), rest.Sum(s => s.Share)));
        return head;
    }

    /// <summary>The larger of what left and what arrived: a movement seen on both of its accounts counts once.</summary>
    private static decimal Moved(IEnumerable<Transaction> t) =>
        Math.Max(-t.Where(x => x.Amount < 0).Sum(x => x.Amount), t.Where(x => x.Amount > 0).Sum(x => x.Amount));

    private static (List<Bar>, string) Series(List<Transaction> txs, DateOnly from, DateOnly to, Granularity g)
    {
        var counted = txs.Where(t => Categories.Get(t.CategoryId).Kind != CategoryKind.Transfer).ToList();
        var days = to.DayNumber - from.DayNumber + 1;
        if (g == Granularity.Month || days <= 62)
        {
            var bars = new List<Bar>();
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var day = counted.Where(t => t.Date == d).ToList();
                var moves = txs.Where(t => t.Date == d && Categories.IsTransfer(t.CategoryId)).ToList();
                bars.Add(new Bar(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.Day.ToString(CultureInfo.InvariantCulture),
                    -day.Where(t => t.Amount < 0).Sum(t => t.Amount), day.Where(t => t.Amount > 0).Sum(t => t.Amount),
                    Moved(moves.Where(t => t.CategoryId is not (Categories.CardPayments or Categories.Investments))),
                    Moved(moves.Where(t => t.CategoryId == Categories.CardPayments)), Moved(moves.Where(t => t.CategoryId == Categories.Investments))));
            }
            return (bars, "day");
        }
        if (g == Granularity.Year || days <= 366 * 3)
        {
            var bars = new List<Bar>();
            for (var m = new DateOnly(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1))
            {
                var month = counted.Where(t => t.Date.Year == m.Year && t.Date.Month == m.Month).ToList();
                var label = g == Granularity.Year ? m.ToString("MMM", Formatting.English) : m.ToString("MMM yy", Formatting.English);
                bars.Add(new Bar(m.ToString("yyyy-MM", CultureInfo.InvariantCulture), label,
                    -month.Where(t => t.Amount < 0).Sum(t => t.Amount), month.Where(t => t.Amount > 0).Sum(t => t.Amount)));
            }
            return (bars, "month");
        }
        var years = new List<Bar>();
        for (var y = from.Year; y <= to.Year; y++)
        {
            var year = counted.Where(t => t.Date.Year == y).ToList();
            years.Add(new Bar(y.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture),
                -year.Where(t => t.Amount < 0).Sum(t => t.Amount), year.Where(t => t.Amount > 0).Sum(t => t.Amount)));
        }
        return (years, "year");
    }
}
