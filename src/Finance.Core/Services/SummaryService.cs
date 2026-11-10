using System.Globalization;
using System.Text.Json.Serialization;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

public sealed record Slice(string Key, string Label, decimal Amount, double Share);

/// <summary>One day, month or year. Moved is money moved between own accounts; CardPaid is credit card payments; Invested is investments.</summary>
public sealed record Bar(string Key, string Label, decimal Expenses, decimal Earnings, decimal Moved = 0, decimal CardPaid = 0, decimal Invested = 0);

/// <param name="Recurring">How often it comes back ("monthly", "twice a month", "yearly"), or null when it is not recurring.</param>
/// <remarks>Empty fields are left out of what the interface receives (a period of ten years holds tens of thousands of operations).</remarks>
public sealed record TransactionView(
    long Id, string Date, decimal Amount, string Currency, string Brand, string CategoryId, string CategorySource,
    string BrandSource, string RawLabel, string Note, long AccountId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? StatementId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Recurring = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? LinkedId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Earned = null, long[]? OwnTags = null, long[]? Tags = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Original = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OriginalCurrency = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Rate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RateDay = null);

/// <summary>
/// One part of a period at the best resolution (years for more than a year, months otherwise) and its balance:
/// what was earned minus what was spent, as in the period's totals.
/// </summary>
public sealed record PeriodBalance(string From, string To, string Title, decimal Expenses, decimal Earnings, decimal Balance);

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
    /// <summary>
    /// Expenses per first-level category: every nested category counted once, in the category it sits in at the top
    /// (Groceries and Restaurants under Food make one Food slice).
    /// </summary>
    public required List<Slice> ByTopCategory { get; init; }
    /// <summary>
    /// Expenses per tag. An operation with several tags (its own and its category's) is in one slice named after all of
    /// them ("Travel + Work"), so no money is counted twice; operations without a tag are left out.
    /// </summary>
    public required List<Slice> TagPie { get; init; }
    /// <summary>The period as its year alone (a year), in years (more than a year) or in months, each with its balance (the Summary view).</summary>
    public required List<PeriodBalance> Breakdown { get; init; }
    /// <summary>Every tag (names for the tag ids of the operations).</summary>
    public required List<Tag> Tags { get; init; }
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

/// <summary>
/// One bank's part of a period, in the global display currency (<see cref="Currency"/>); <see cref="Native"/> is the
/// currency the bank's accounts are in. A group "Other accounts" holds cash and manual entries.
/// </summary>
public sealed record BankPart(string Key, string Name, string Currency, string Native, BankingSystem? System, PeriodSummary Summary);

/// <summary>One rate used: for operations of <see cref="Day"/>, the rate published on <see cref="RateDay"/>.</summary>
public sealed record RateUse(string Day, string RateDay, decimal Rate);

/// <summary>
/// How the operations of one bank in one currency were converted: what they add up to before and after, the rates used
/// (the lowest, the highest, every one by day) and what could not be converted (no rate known for its day).
/// </summary>
public sealed record ConversionLine(
    string Group, string From, string To, int Operations, decimal Expenses, decimal Earnings, decimal ConvertedExpenses, decimal ConvertedEarnings,
    decimal? LowestRate, decimal? HighestRate, decimal? EffectiveRate, List<RateUse> Rates, int Unconverted, decimal UnconvertedAmount, int OldRates);

/// <summary>The conversions behind a period's global figures, and where the rates come from.</summary>
/// <param name="Base">The currency the bank quotes every rate against (two other currencies are converted through it).</param>
public sealed record Conversion(string To, string Source, string Base, string Url, string? UpdatedAt, string? Latest, List<ConversionLine> Lines);

/// <summary>
/// The net salary of a period worked out per hour and per year, in the display currency: the salary received
/// (<see cref="Net"/>, every bank together) over the period's business days (weekend days and the checked calendars'
/// holidays are not worked) of <see cref="HoursPerDay"/> hours (a 40-hour week shared by the working days of the
/// week); per year: the same daily pay over <see cref="YearBusinessDays"/>, the business days of a year of the period
/// (on average when it runs over several years).
/// </summary>
public sealed record SalaryRate(decimal Net, int BusinessDays, decimal HoursPerDay, decimal PerHour, decimal YearBusinessDays, decimal PerYear);

/// <summary>A period bank by bank, then every bank together, all in the global display currency.</summary>
public sealed class PeriodReport
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Title { get; init; }
    public required Granularity Granularity { get; init; }
    /// <summary>The global display currency.</summary>
    public required string Currency { get; init; }
    public required List<BankPart> Banks { get; init; }
    public required PeriodSummary Global { get; init; }
    public required Conversion Conversion { get; init; }
    /// <summary>
    /// Ids of the operations left out of every total (no rate known for their day): the banks' parts list them in
    /// their own currency. The banks' parts hold the operations once; <see cref="Global"/> carries the totals and
    /// charts, not the operations again.
    /// </summary>
    public required List<long> Unconverted { get; init; }
    /// <summary>The days that are not weekend days, less the holidays of the checked calendars (null when no calendars were given).</summary>
    public int? BusinessDays { get; init; }
    /// <summary>The net salary per hour and per year (null without salary in the period, or without business days).</summary>
    public SalaryRate? Salary { get; init; }
}

public sealed class SummaryService(Database db)
{
    /// <summary>Pies show this many slices, the rest folds into "Other".</summary>
    public const int PieSlices = 5;

    /// <summary>The months and years with operations in <paramref name="currency"/>, or in any currency (every bank) when null.</summary>
    public PeriodIndex Index(string? currency)
    {
        var months = db.MonthsWithData();
        var currencies = months.Select(m => m.Currency).Distinct().OrderBy(c => c == currency ? 0 : 1).ThenBy(c => c).ToList();
        var mine = months.Where(m => currency is null || m.Currency == currency)
            .GroupBy(m => (m.Year, m.Month)).Select(g => new MonthEntry(g.Key.Year, g.Key.Month, g.Sum(m => m.Count)))
            .OrderByDescending(m => m.Year).ThenByDescending(m => m.Month).ToList();
        var years = mine.Select(m => m.Year).Distinct().OrderByDescending(y => y).ToList();
        // The day of the first operation: "All time" runs from it to today.
        var first = db.AllTransactions().FirstOrDefault(t => currency is null || t.Currency == currency)?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string? last = mine.Count == 0 ? null : $"{mine[0].Year:D4}-{mine[0].Month:D2}";
        return new PeriodIndex(mine, years, currencies, first, last);
    }

    private (long Version, Dictionary<long, string> Recurring, Dictionary<long, Transaction> ById)? _history;
    private readonly Lock _gate = new();

    /// <summary>
    /// What is recurring is found over the whole history, the same for every period: worked out once, again only
    /// after the data changes.
    /// </summary>
    private (Dictionary<long, string> Recurring, Dictionary<long, Transaction> ById) History()
    {
        lock (_gate)
        {
            if (_history is { } h && h.Version == db.Version) return (h.Recurring, h.ById);
            var version = db.Version;
            var everything = db.AllTransactions();
            // Found in each currency on its own (amounts of different currencies are never compared).
            var recurring = new Dictionary<long, string>();
            foreach (var group in everything.GroupBy(t => t.Currency))
                foreach (var (id, how) in RecurringDetector.Detect(group.ToList())) recurring[id] = how;
            var byId = everything.ToDictionary(t => t.Id);
            _history = (version, recurring, byId);
            return (recurring, byId);
        }
    }

    /// <summary>A period in one currency: only the operations in that currency, nothing converted.</summary>
    public PeriodSummary Summarize(DateOnly from, DateOnly to, Granularity g, string currency)
    {
        if (to < from) throw new ArgumentException("The period ends before it starts.");
        var txs = db.Transactions(from, to).Where(t => t.Currency == currency).ToList();
        return Build(txs, from, to, g, currency, null, db.Accounts().ToDictionary(a => a.Id));
    }

    /// <summary>
    /// A period bank by bank, then every bank together, every amount in <paramref name="global"/> (the display
    /// currency): an operation in another currency is converted at the rate of its day (<see cref="ExchangeRates"/>),
    /// with every conversion shown. An operation without a rate for its day is listed in its own currency and left
    /// out of the totals.
    /// </summary>
    public PeriodReport Report(DateOnly from, DateOnly to, Granularity g, string global, ExchangeRates rates, Holidays? holidays = null)
    {
        if (to < from) throw new ArgumentException("The period ends before it starts.");
        var all = db.Transactions(from, to);
        var accounts = db.Accounts().ToDictionary(a => a.Id);
        var banks = db.Banks().ToDictionary(b => b.Id);
        // Banks in the order of Bank Configuration: the one whose accounts came first, first; cash and manual entries last.
        var firstAccount = accounts.Values.Where(a => a.BankId is not null).GroupBy(a => a.BankId!.Value).ToDictionary(x => x.Key, x => x.Min(a => a.Id));
        var groups = all.GroupBy(t => accounts.TryGetValue(t.AccountId, out var a) && a.BankId is { } b && banks.ContainsKey(b) ? b : (long?)null)
            .OrderBy(x => x.Key is { } id ? firstAccount.GetValueOrDefault(id, long.MaxValue - 1) : long.MaxValue).ThenBy(x => x.Key)
            .ToList();

        // The source of the rates is read once for the whole period.
        var source = rates.Source;
        var parts = new List<BankPart>();
        var lines = new List<ConversionLine>();
        var everything = new List<Transaction>();
        var everyConversion = new Dictionary<long, (Transaction Original, Converted Rate)>();
        foreach (var group in groups)
        {
            var bank = group.Key is { } id ? banks[id] : null;
            var name = bank?.Name ?? "Other accounts";
            var txs = group.ToList();

            var (converted, toGlobal, globalLines) = ConvertAll(txs, global, rates, source, name);
            lines.AddRange(globalLines);
            var left = txs.Where(t => t.Currency != global && !toGlobal.ContainsKey(t.Id)).ToList();
            parts.Add(new BankPart(group.Key is { } k ? k.ToString(CultureInfo.InvariantCulture) : "other", name, global, bank?.Currency ?? global, bank?.System,
                Build(converted, from, to, g, global, toGlobal, accounts, unconverted: left, diagram: false)));
            everything.AddRange(converted);
            foreach (var (txId, c) in toGlobal) everyConversion[txId] = c;
        }
        everything.Sort((a, b) => a.Date != b.Date ? a.Date.CompareTo(b.Date) : a.Id.CompareTo(b.Id));
        var status = rates.Status(source);
        // The operations are in the banks' parts already: not listed again.
        var globalSummary = Build(everything, from, to, g, global, everyConversion, accounts, listOperations: false);
        return new PeriodReport
        {
            From = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            To = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Title = Formatting.PeriodTitle(from, to, g),
            Granularity = g,
            Currency = global,
            Banks = parts,
            Global = globalSummary,
            Conversion = new Conversion(global, status.Source, status.Base, status.Url, status.UpdatedAt, status.Latest, lines),
            Unconverted = [.. all.Where(t => t.Currency != global && !everyConversion.ContainsKey(t.Id)).Select(t => t.Id)],
            BusinessDays = holidays?.BusinessDays(from, to),
            Salary = holidays is null ? null : SalaryOf(everything, from, to, holidays),
        };
    }

    /// <summary>The category of salaries, or one nested in it.</summary>
    private static bool IsSalary(string categoryId)
    {
        for (var c = Categories.Get(categoryId); ; c = Categories.Get(c.Parent!))
        {
            if (c.Id == "salary") return true;
            if (c.Parent is null || !Categories.Exists(c.Parent)) return false;
        }
    }

    /// <summary>See <see cref="SalaryRate"/>; <paramref name="txs"/> are in the display currency.</summary>
    public static SalaryRate? SalaryOf(IEnumerable<Transaction> txs, DateOnly from, DateOnly to, Holidays holidays)
    {
        var net = txs.Where(t => IsSalary(t.CategoryId)).Sum(t => t.Amount);
        var days = holidays.BusinessDays(from, to);
        var perWeek = holidays.WorkingDaysPerWeek();
        if (net <= 0 || days <= 0 || perWeek <= 0) return null;
        var hoursPerDay = Holidays.HoursPerWeek / perWeek;
        var years = Enumerable.Range(from.Year, to.Year - from.Year + 1).ToList();
        var yearDays = (decimal)years.Sum(y => holidays.BusinessDays(new DateOnly(y, 1, 1), new DateOnly(y, 12, 31))) / years.Count;
        return new SalaryRate(net, days, Math.Round(hoursPerDay, 2),
            Math.Round(net / (days * hoursPerDay), 2, MidpointRounding.AwayFromZero), Math.Round(yearDays, 1),
            Math.Round(net / days * yearDays, 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Operations in <paramref name="to"/>: the ones in another currency converted at the rate of their day, the ones
    /// without a known rate left out. Gives, per currency converted, how it was done.
    /// </summary>
    private static (List<Transaction> Converted, Dictionary<long, (Transaction Original, Converted Rate)> Conversions, List<ConversionLine> Lines)
        ConvertAll(List<Transaction> txs, string to, ExchangeRates rates, RateSource source, string group)
    {
        var result = new List<Transaction>(txs.Count);
        var conversions = new Dictionary<long, (Transaction, Converted)>();
        var lines = new List<ConversionLine>();
        foreach (var byCurrency in txs.GroupBy(t => t.Currency).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (byCurrency.Key == to) { result.AddRange(byCurrency); continue; }
            var done = new List<(Transaction Original, Transaction Copy, Converted Rate)>();
            var missing = new List<Transaction>();
            foreach (var t in byCurrency)
            {
                if (rates.Convert(source, t.Amount, t.Currency, to, t.Date) is not { } c) { missing.Add(t); continue; }
                var copy = Copy(t, c.Amount, to, t.Earned is { } e ? rates.Convert(source, e, t.Currency, to, t.Date)?.Amount : null);
                done.Add((t, copy, c));
                conversions[t.Id] = (t, c);
                result.Add(copy);
            }
            var (expenses, earnings) = Totals(done.Select(d => d.Original));
            var (convertedExpenses, convertedEarnings) = Totals(done.Select(d => d.Copy));
            var nativeAll = done.Sum(d => Math.Abs(d.Original.Amount));
            var uses = done.GroupBy(d => d.Original.Date).OrderBy(x => x.Key)
                .Select(x => new RateUse(x.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), x.First().Rate.RateDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), x.First().Rate.Rate))
                .ToList();
            lines.Add(new ConversionLine(group, byCurrency.Key, to, done.Count, expenses, earnings, convertedExpenses, convertedEarnings,
                done.Count == 0 ? null : done.Min(d => d.Rate.Rate), done.Count == 0 ? null : done.Max(d => d.Rate.Rate),
                nativeAll == 0 ? null : Math.Round(done.Sum(d => Math.Abs(d.Copy.Amount)) / nativeAll, 6, MidpointRounding.AwayFromZero),
                uses, missing.Count, missing.Sum(t => t.Amount),
                // Rates more than a week older than the operation: the rates were not updated since.
                done.Count(d => d.Original.Date.DayNumber - d.Rate.RateDay.DayNumber > 7)));
        }
        return (result, conversions, lines);
    }

    private static Transaction Copy(Transaction t, decimal amount, string currency, decimal? earned) => new()
    {
        Id = t.Id, AccountId = t.AccountId, StatementId = t.StatementId, Date = t.Date, Amount = amount, Currency = currency,
        RawLabel = t.RawLabel, MerchantKey = t.MerchantKey, Brand = t.Brand, BrandSource = t.BrandSource, CategoryId = t.CategoryId,
        CategorySource = t.CategorySource, Note = t.Note, BankCategory = t.BankCategory, LinkedId = t.LinkedId, Earned = earned,
    };

    private PeriodSummary Build(List<Transaction> txs, DateOnly from, DateOnly to, Granularity g, string currency,
        Dictionary<long, (Transaction Original, Converted Rate)>? converted, Dictionary<long, Account> accounts, bool listOperations = true,
        List<Transaction>? unconverted = null, bool diagram = true)
    {
        var (recurring, byId) = History();
        var tags = db.TagIndex();
        var movements = Movements.Build(txs, byId, accounts, db.Banks().ToDictionary(b => b.Id, b => b.Name));
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
        var topSlices = Slices(byCategory.GroupBy(kv => Categories.Root(kv.Key).Id)
            .Select(g => (g.Key, Categories.Get(g.Key).Name, g.Sum(kv => kv.Value))), expenses);
        var recurringOut = txs.Where(t => t.Amount < 0 && recurring.ContainsKey(t.Id)).ToList();
        var recurringOutTotal = -recurringOut.Sum(t => t.Amount);
        var burden = -recurringOut.Where(t => t.CategoryId != "housing").Sum(t => t.Amount);
        var recurringOutSlices = Slices(recurringOut.GroupBy(t => t.Brand, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, g.First().Brand, -g.Sum(t => t.Amount))), recurringOutTotal);
        var incomeSlices = Slices(incomeBy.Select(kv => (kv.Key, brandNames[kv.Key], kv.Value)), earnings);
        var brandSlices = Slices(byBrand.Select(kv => (kv.Key, brandNames[kv.Key], kv.Value)), expenses);

        var (series, unit) = Series(txs, from, to, g);

        // Tags: each tagged expense once, in the slice of its whole set of tags.
        var tagged = new Dictionary<string, (string Label, decimal Amount)>(StringComparer.Ordinal);
        foreach (var t in txs)
        {
            if (t.Amount >= 0 || Categories.Get(t.CategoryId).Kind == CategoryKind.Transfer) continue;
            var set = tags.Of(t.Id, t.CategoryId).Where(tags.ById.ContainsKey).ToArray();
            if (set.Length == 0) continue;
            var key = string.Join('+', set);
            var label = string.Join(" + ", set.Select(id => tags.ById[id]).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Select(x => x.Name));
            var prev = tagged.GetValueOrDefault(key, (Label: label, Amount: 0m));
            tagged[key] = (label, prev.Amount - t.Amount);
        }
        var tagSlices = Slices(tagged.Select(kv => (kv.Key, kv.Value.Label, kv.Value.Amount)), tagged.Values.Sum(x => x.Amount));

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
            // Operations without a rate for their day are listed too, in their own currency (outside every total).
            Transactions = !listOperations ? [] : txs.Concat(unconverted ?? []).OrderBy(t => t.Date).ThenBy(t => t.Id).Select(t => converted is not null && converted.TryGetValue(t.Id, out var c)
                ? View(t, recurring.GetValueOrDefault(t.Id), tags) with
                {
                    Original = c.Original.Amount, OriginalCurrency = c.Original.Currency, Rate = c.Rate.Rate,
                    RateDay = c.Rate.RateDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                }
                : View(t, recurring.GetValueOrDefault(t.Id), tags)).ToList(),
            ByCategory = categorySlices,
            CategoryPie = FoldPie(categorySlices),
            ByTopCategory = topSlices,
            TagPie = tagSlices,
            Tags = [.. tags.Tags],
            Breakdown = Breakdown(txs, from, to, g),
            ByBrand = brandSlices,
            BrandPie = FoldPie(brandSlices),
            Series = series,
            SeriesUnit = unit,
            RecurringExpenses = recurringOutTotal,
            RecurringExpensePie = recurringOutSlices,
            IncomePie = incomeSlices,
            Movements = movements,
            Flows = Movements.Flows(movements),
            // A bank's part has none: the diagram is drawn once for every bank (see PeriodReport.Global).
            Diagram = diagram ? FlowDiagram.Build(movements) : new FlowDiagram(0, 0, [], [], []),
            RecurringWithoutHousing = burden,
            RecurringWithoutHousingShare = earnings > 0 ? (double)(burden / earnings) : null,
            ExpensesShare = earnings > 0 ? (double)(expenses / earnings) : null,
        };
    }

    public static TransactionView View(Transaction t, TagIndex? tags = null) => View(t, null, tags);

    public static TransactionView View(Transaction t, string? recurring, TagIndex? tags) => new(
        t.Id, t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), t.Amount, t.Currency, t.Brand, t.CategoryId,
        t.CategorySource.ToString(), t.BrandSource.ToString(), t.RawLabel, t.Note, t.AccountId, t.StatementId,
        recurring, t.LinkedId, t.Earned, tags?.Own(t.Id) ?? [], tags?.Of(t.Id, t.CategoryId) ?? []);

    /// <summary>What was spent and earned, counted as in the period's totals (an investment's earned part is income).</summary>
    public static (decimal Expenses, decimal Earnings) Totals(IEnumerable<Transaction> txs)
    {
        decimal expenses = 0, earnings = 0;
        foreach (var t in txs)
        {
            if (Categories.Get(t.CategoryId).Kind == CategoryKind.Transfer)
            {
                if (t.Amount > 0 && t.Earned is > 0 and var gain) earnings += gain;
                continue;
            }
            if (t.Amount < 0) expenses -= t.Amount; else earnings += t.Amount;
        }
        return (expenses, earnings);
    }

    /// <summary>The period cut in years when it is longer than a year, in months otherwise, each part kept inside the period.</summary>
    public static List<PeriodBalance> Breakdown(List<Transaction> txs, DateOnly from, DateOnly to, Granularity g = Granularity.Range)
    {
        // A year is shown as the year alone; any other period month by month, or year by year beyond a year.
        var years = g == Granularity.Year || to.DayNumber - from.DayNumber + 1 > 366;
        var parts = new List<PeriodBalance>();
        var start = from;
        while (start <= to)
        {
            var end = years ? new DateOnly(start.Year, 12, 31) : new DateOnly(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month));
            if (end > to) end = to;
            var (a, b) = (start, end);
            var (expenses, earnings) = Totals(txs.Where(t => t.Date >= a && t.Date <= b));
            var title = Formatting.PeriodTitle(a, b, years ? Granularity.Year : Granularity.Month);
            parts.Add(new PeriodBalance(a.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), b.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                title, expenses, earnings, earnings - expenses));
            start = end.AddDays(1);
        }
        return parts;
    }

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
