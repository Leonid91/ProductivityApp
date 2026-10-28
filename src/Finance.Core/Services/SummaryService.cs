using System.Globalization;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

public sealed record Slice(string Key, string Label, decimal Amount, double Share);

public sealed record Bar(string Key, string Label, decimal Expenses, decimal Earnings);

public sealed record TransactionView(
    long Id, string Date, decimal Amount, string Currency, string Brand, string CategoryId, string CategorySource,
    string BrandSource, string RawLabel, string Note, long AccountId, long? StatementId);

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
        string? first = mine.Count == 0 ? null : $"{mine[^1].Year:D4}-{mine[^1].Month:D2}";
        string? last = mine.Count == 0 ? null : $"{mine[0].Year:D4}-{mine[0].Month:D2}";
        return new PeriodIndex(mine, years, currencies, first, last);
    }

    public PeriodSummary Summarize(DateOnly from, DateOnly to, Granularity g, string currency)
    {
        if (to < from) throw new ArgumentException("The period ends before it starts.");
        var txs = db.Transactions(from, to).Where(t => t.Currency == currency).ToList();

        decimal expenses = 0, earnings = 0, transfersOut = 0, transfersIn = 0;
        var byCategory = new Dictionary<string, decimal>();
        var byBrand = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var brandNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in txs)
        {
            var transfer = Categories.Get(t.CategoryId).Kind == CategoryKind.Transfer;
            if (transfer)
            {
                if (t.Amount < 0) transfersOut -= t.Amount; else transfersIn += t.Amount;
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
            else earnings += t.Amount;
        }

        var categorySlices = Slices(byCategory.Select(kv => (kv.Key, Categories.Get(kv.Key).Name, kv.Value)), expenses);
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
            Transactions = txs.OrderBy(t => t.Date).ThenBy(t => t.Id).Select(View).ToList(),
            ByCategory = categorySlices,
            CategoryPie = FoldPie(categorySlices),
            ByBrand = brandSlices,
            BrandPie = FoldPie(brandSlices),
            Series = series,
            SeriesUnit = unit,
        };
    }

    public static TransactionView View(Transaction t) => new(
        t.Id, t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), t.Amount, t.Currency, t.Brand, t.CategoryId,
        t.CategorySource.ToString(), t.BrandSource.ToString(), t.RawLabel, t.Note, t.AccountId, t.StatementId);

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
                bars.Add(new Bar(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.Day.ToString(CultureInfo.InvariantCulture),
                    -day.Where(t => t.Amount < 0).Sum(t => t.Amount), day.Where(t => t.Amount > 0).Sum(t => t.Amount)));
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
