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
    long Id, string Date, decimal Amount, string Currency, string Brand, string CategoryId, [property: JsonIgnore] string CategorySource,
    [property: JsonIgnore] string BrandSource, string RawLabel, string Note, long AccountId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? StatementId,
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

public sealed record PeriodSummary
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
    /// <summary>
    /// When <see cref="Transactions"/> holds only the latest operations (a long period sent in pieces, see <see
    /// cref="PeriodReport.Listing"/>): how many the period has in all. Null when they are all there.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? TransactionCount { get; init; }
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
    /// <summary>The transfers over time, account by account (drawn once, for every bank together, like the diagram).</summary>
    public TransferTimeline Timeline { get; init; } = TransferTimeline.Empty;
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
/// The net salary of a period worked out per day, per hour and per year, in the display currency: the salary alone
/// (<see cref="Net"/>, every bank together; no other income) over the period's working days (<see cref="WorkingDays"/>:
/// every day but the weekend days; holidays are paid, so they count) of <see cref="HoursPerDay"/> hours (the hours of
/// a week entered in Settings, shared by the working days of the week); per year: the same daily pay over
/// <see cref="YearWorkingDays"/>, the working days of a year of the period (on average when it runs over several years).
/// </summary>
public sealed record SalaryRate(decimal Net, int WorkingDays, decimal HoursPerDay, decimal PerDay, decimal PerHour, decimal YearWorkingDays, decimal PerYear, decimal HoursPerWeek);

/// <summary>A period bank by bank, then every bank together, all in the global display currency.</summary>
public sealed record PeriodReport
{
    /// <summary>
    /// The same report with only the latest <paramref name="first"/> operations of each bank (and how many it has in
    /// all): a long period reaches the interface at once, its older operations follow in pieces (<see cref="Slice"/>).
    /// </summary>
    public PeriodReport Listing(int first) => Banks.All(b => b.Summary.Transactions.Count <= first) ? this : this with
    {
        Banks = [.. Banks.Select(b => b.Summary.Transactions.Count <= first ? b : b with
        {
            Summary = b.Summary with { Transactions = b.Summary.Transactions[^first..], TransactionCount = b.Summary.Transactions.Count },
        })],
    };

    /// <summary>The operations of a bank from one place to another (by date, then id: the order they are listed in).</summary>
    public List<TransactionView> Slice(string bank, int start, int end)
    {
        var all = Banks.FirstOrDefault(b => b.Key == bank)?.Summary.Transactions ?? throw new ArgumentException("This bank is not in the period.");
        start = Math.Clamp(start, 0, all.Count);
        return all[start..Math.Clamp(end, start, all.Count)];
    }

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
    /// The last reports made, kept while nothing they depend on changed (the operations, the settings, the rates, the
    /// holidays, the day): going back to a period, or scrolling back to it, shows it at once.
    /// </summary>
    private readonly LinkedList<(ReportKey Key, PeriodReport Report)> _reports = new();
    private sealed record ReportKey(DateOnly From, DateOnly To, Granularity G, string Global, ExchangeRates Rates, string Source, Holidays? Holidays, string Weekend, decimal Hours, long Version, long Bookkeeping, DateOnly Today);

    /// <summary>At most this many reports are kept, holding at most <see cref="KeptOperations"/> operations together.</summary>
    private const int KeptReports = 8, KeptOperations = 60_000;

    /// <summary>Forgets the reports kept (to measure how long a report takes to make).</summary>
    public void ForgetReports() { lock (_reports) _reports.Clear(); }

    /// <summary>
    /// A period bank by bank, then every bank together, every amount in <paramref name="global"/> (the display
    /// currency): an operation in another currency is converted at the rate of its day (<see cref="ExchangeRates"/>),
    /// with every conversion shown. An operation without a rate for its day is listed in its own currency and left
    /// out of the totals.
    /// </summary>
    /// <remarks>The report returned is shared with the next callers asking for the same period: it is never changed.</remarks>
    public PeriodReport Report(DateOnly from, DateOnly to, Granularity g, string global, ExchangeRates rates, Holidays? holidays = null)
    {
        // The source of rates and the weekend days chosen are read as the report would read them.
        var key = new ReportKey(from, to, g, global, rates, rates.Source.Id, holidays, holidays is null ? "" : string.Join(',', holidays.Weekend().Order()), holidays?.WeekHours() ?? 0,
            db.Version, db.BookkeepingVersion, DateOnly.FromDateTime(DateTime.Today));
        lock (_reports)
        {
            for (var n = _reports.First; n is not null; n = n.Next)
                if (n.Value.Key == key)
                {
                    _reports.Remove(n);
                    _reports.AddFirst(n);
                    return n.Value.Report;
                }
        }
        var report = Make(from, to, g, global, rates, holidays);
        // Read again after: a write while it was made leaves it out of the kept ones.
        if (db.Version != key.Version || db.BookkeepingVersion != key.Bookkeeping) return report;
        lock (_reports)
        {
            _reports.AddFirst((key, report));
            static int Size(PeriodReport r) => r.Banks.Sum(b => b.Summary.Transactions.Count) + r.Global.Transactions.Count;
            while (_reports.Count > KeptReports || (_reports.Count > 1 && _reports.Sum(x => Size(x.Report)) > KeptOperations)) _reports.RemoveLast();
        }
        return report;
    }

    private PeriodReport Make(DateOnly from, DateOnly to, Granularity g, string global, ExchangeRates rates, Holidays? holidays)
    {
        if (to < from) throw new ArgumentException("The period ends before it starts.");
        var all = db.Transactions(from, to);
        var accounts = db.Accounts().ToDictionary(a => a.Id);
        var banks = db.Banks().ToDictionary(b => b.Id);
        // Read once for the whole report (each bank's part and every bank together name their accounts' banks).
        var bankNames = banks.ToDictionary(b => b.Key, b => b.Value.Name);
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
                Build(converted, from, to, g, global, toGlobal, accounts, unconverted: left, diagram: false, rates: rates, bankNames: bankNames)));
            everything.AddRange(converted);
            foreach (var (txId, c) in toGlobal) everyConversion[txId] = c;
        }
        everything.Sort((a, b) => a.Date != b.Date ? a.Date.CompareTo(b.Date) : a.Id.CompareTo(b.Id));
        var status = rates.Status(source);
        // The operations are in the banks' parts already: not listed again.
        var globalSummary = Build(everything, from, to, g, global, everyConversion, accounts, listOperations: false, rates: rates, bankNames: bankNames);
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
        var weekend = holidays.Weekend();
        // Holidays are paid: only the weekend days are left out.
        var days = Holidays.WorkingDays(from, to, weekend);
        var perWeek = holidays.WorkingDaysPerWeek();
        if (net <= 0 || days <= 0 || perWeek <= 0) return null;
        var weekHours = holidays.WeekHours();
        var hoursPerDay = weekHours / perWeek;
        var years = Enumerable.Range(from.Year, to.Year - from.Year + 1).ToList();
        var yearDays = (decimal)years.Sum(y => Holidays.WorkingDays(new DateOnly(y, 1, 1), new DateOnly(y, 12, 31), weekend)) / years.Count;
        return new SalaryRate(net, days, Math.Round(hoursPerDay, 2),
            Math.Round(net / days, 2, MidpointRounding.AwayFromZero),
            Math.Round(net / (days * hoursPerDay), 2, MidpointRounding.AwayFromZero), Math.Round(yearDays, 1),
            Math.Round(net / days * yearDays, 2, MidpointRounding.AwayFromZero), weekHours);
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
        List<Transaction>? unconverted = null, bool diagram = true, ExchangeRates? rates = null, IReadOnlyDictionary<long, string>? bankNames = null)
    {
        var (recurring, byId) = History();
        var tags = db.TagIndex();
        var movements = Movements.Build(txs, byId, accounts, bankNames ?? db.Banks().ToDictionary(b => b.Id, b => b.Name));
        // An international transfer: what the banks kept, the amount sent at the rate of its day less the amount received.
        if (rates is not null) movements = [.. movements.Select(m => m.Sent is { } s && m.SentCurrency is { } sc && m.Received is { } r && m.ReceivedCurrency is { } rc
            && DateOnly.TryParse(m.Date, CultureInfo.InvariantCulture, out var day)
            && rates.Convert(s, sc, rc, day) is { } expected && rates.Convert(expected.Amount - r, rc, currency, day) is { } lost
                ? m with { Lost = lost.Amount } : m)];
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
            Transactions = !listOperations ? [] : InOrder(txs, unconverted).Select(t => converted is not null && converted.TryGetValue(t.Id, out var c)
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
            Timeline = diagram ? TransferTimeline.Build(movements, from, to) : TransferTimeline.Empty,
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
        // Each operation of the period goes once to its part.
        int PartOf(DateOnly d) => years ? d.Year - from.Year : (d.Year - from.Year) * 12 + d.Month - from.Month;
        var count = to < from ? 0 : PartOf(to) + 1;
        var byPart = new List<Transaction>[count];
        for (var i = 0; i < count; i++) byPart[i] = [];
        foreach (var t in txs)
            if (t.Date >= from && t.Date <= to) byPart[PartOf(t.Date)].Add(t);
        var start = from;
        while (start <= to)
        {
            var end = years ? new DateOnly(start.Year, 12, 31) : new DateOnly(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month));
            if (end > to) end = to;
            var (a, b) = (start, end);
            var (expenses, earnings) = Totals(byPart[PartOf(a)]);
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

    /// <summary>The operations by date then id (sorted only when they are not already, as when read from the database).</summary>
    private static IEnumerable<Transaction> InOrder(List<Transaction> txs, List<Transaction>? more)
    {
        var all = more is { Count: > 0 } ? [.. txs, .. more] : txs;
        for (var i = 1; i < all.Count; i++)
            if (all[i - 1].Date > all[i].Date || (all[i - 1].Date == all[i].Date && all[i - 1].Id > all[i].Id))
                return all.OrderBy(t => t.Date).ThenBy(t => t.Id);
        return all;
    }

    private static (List<Bar>, string) Series(List<Transaction> txs, DateOnly from, DateOnly to, Granularity g)
    {
        // Each operation goes once to its bar (its day, month or year), however long the period.
        var days = to.DayNumber - from.DayNumber + 1;
        // A day per bar up to a month; beyond, a month per bar.
        var unit = g == Granularity.Month || days <= 31 ? "day" : "month";
        var first = new DateOnly(from.Year, unit == "year" ? 1 : from.Month, 1);
        int BarOf(DateOnly d) => unit == "day" ? (d >= from && d <= to ? d.DayNumber - from.DayNumber : -1)
            : unit == "month" ? (d.Year - first.Year) * 12 + d.Month - first.Month : d.Year - first.Year;
        var count = unit == "day" ? days : unit == "month" ? (to.Year - first.Year) * 12 + to.Month - first.Month + 1 : to.Year - first.Year + 1;
        var spent = new decimal[count];
        var earned = new decimal[count];
        // Movements between own accounts, by bar: other, card payments, investments (money out, money in).
        var moved = new (decimal Out, decimal In)[count, 3];
        foreach (var t in txs)
        {
            var i = BarOf(t.Date);
            if (i < 0 || i >= count) continue;
            if (Categories.Get(t.CategoryId).Kind != CategoryKind.Transfer)
            {
                if (t.Amount < 0) spent[i] -= t.Amount; else if (t.Amount > 0) earned[i] += t.Amount;
            }
            else if (unit == "day")
            {
                var k = t.CategoryId == Categories.CardPayments ? 1 : t.CategoryId == Categories.Investments ? 2 : 0;
                if (t.Amount < 0) moved[i, k].Out -= t.Amount; else if (t.Amount > 0) moved[i, k].In += t.Amount;
            }
        }
        // A movement seen on both of its accounts counts once: the larger of what left and what arrived.
        decimal MovedAt(int i, int k) => Math.Max(moved[i, k].Out, moved[i, k].In);
        var bars = new List<Bar>(count);
        for (var i = 0; i < count; i++)
        {
            if (unit == "day")
            {
                var d = from.AddDays(i);
                bars.Add(new Bar(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.Day.ToString(CultureInfo.InvariantCulture), spent[i], earned[i], MovedAt(i, 0), MovedAt(i, 1), MovedAt(i, 2)));
            }
            else if (unit == "month")
            {
                var m = first.AddMonths(i);
                var label = g == Granularity.Year ? m.ToString("MMM", Formatting.English) : m.ToString("MMM yy", Formatting.English);
                bars.Add(new Bar(m.ToString("yyyy-MM", CultureInfo.InvariantCulture), label, spent[i], earned[i]));
            }
            else
            {
                var y = (first.Year + i).ToString(CultureInfo.InvariantCulture);
                bars.Add(new Bar(y, y, spent[i], earned[i]));
            }
        }
        return (bars, unit);
    }
}
