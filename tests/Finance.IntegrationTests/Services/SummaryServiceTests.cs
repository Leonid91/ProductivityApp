using System.Globalization;
using System.Text;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>What a period sums up from the operations saved in the database: totals, pies, bars, parts, recurring payments, movements.</summary>
public sealed class SummaryServiceTests : IDisposable
{
    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    // ------------------------------------------------------------------------------------------------ totals and pies

    /// <summary>October 2026: a brand written in two cases, a brand in two categories, a small brand, a transfer, a salary.</summary>
    private PeriodReport OctoberWithBrands()
    {
        var account = _store.AddAccount();
        _store.Enter(account, "2026-10-02", -100m, "FreshMart", "groceries");
        _store.Enter(account, "2026-10-03", -20m, "freshmart", "groceries");
        _store.Enter(account, "2026-10-04", -12.60m, "Market Corner", "groceries");
        _store.Enter(account, "2026-10-05", -5m, "FreshMart", "restaurants");
        _store.Enter(account, "2026-10-06", -42.10m, "Pharmacy", "health");
        _store.Enter(account, "2026-10-07", -500m, "To savings", Categories.Investments);
        _store.Enter(account, "2026-10-15", 2000m, "Employer", "salary");
        return _store.Summaries.Report(D(2026, 10, 1), D(2026, 10, 31), Granularity.Month, "CAD", new ExchangeRates(_store.Db));
    }

    [Fact]
    public void Report_ExpensesAndAMovementToSavings_CountsOnlyTheExpenses()
    {
        var report = OctoberWithBrands();

        Assert.Equal((179.70m, 2000m), (report.Global.Expenses, report.Global.Earnings));
    }

    [Fact]
    public void Report_AnyPeriod_EveryPieHoldsEveryExpenseOnce()
    {
        var summary = OctoberWithBrands().Global;

        Assert.Equal(summary.Expenses, summary.ByCategory.Sum(x => x.Amount));
        Assert.Equal(summary.Expenses, summary.ByTopCategory.Sum(x => x.Amount));
        Assert.Equal(summary.Expenses, summary.ByBrand.Sum(x => x.Amount));
    }

    [Fact]
    public void Report_BrandWrittenInTwoCasesAndInTwoCategories_IsOneSliceHoldingAllOfIt()
    {
        var byBrand = OctoberWithBrands().Global.ByBrand;

        Assert.Equal(125m, byBrand.Single(x => x.Label.Equals("FreshMart", StringComparison.OrdinalIgnoreCase)).Amount);
        Assert.Equal(3, byBrand.Count);
    }

    [Fact]
    public void Report_SmallBrandOfACategory_IsASliceOfItsOwn()
    {
        var byBrand = OctoberWithBrands().Global.ByBrand;

        Assert.Equal(12.60m, byBrand.Single(x => x.Label == "Market Corner").Amount);
    }

    [Fact]
    public void Report_OneBank_GivesTheBanksPartTheSameTotalsAsAllBanks()
    {
        var report = OctoberWithBrands();

        var bank = Assert.Single(report.Banks).Summary;
        Assert.Equal((report.Global.Expenses, report.Global.Earnings), (bank.Expenses, bank.Earnings));
        Assert.Equal(132.60m, bank.ByCategory.Single(x => x.Key == "groceries").Amount);
    }

    [Fact]
    public void Summarize_ManyCategories_KeepsThePieToItsSlicesPlusOneHoldingTheRest()
    {
        var account = _store.AddAccount();
        var categories = Categories.All.Where(c => c.Kind == CategoryKind.Expense && c.Parent is null).Take(SummaryService.PieSlices + 4).ToList();
        for (var i = 0; i < categories.Count; i++) _store.Enter(account, "2025-10-05", -(10m + i), $"Shop {i}", categories[i].Id);

        var october = _store.Summarize(D(2025, 10, 1), D(2025, 10, 31));

        Assert.Equal(SummaryService.PieSlices + 1, october.CategoryPie.Count);
        Assert.Equal(october.Expenses, october.CategoryPie.Sum(x => x.Amount));
    }

    // ------------------------------------------------------------------------------------------------ bars and parts

    [Theory]
    [InlineData(2027, 2)]
    [InlineData(2028, 2)]
    [InlineData(2027, 4)]
    [InlineData(2027, 12)]
    public void Summarize_Month_HasOneBarForEachOfItsDays(int year, int month)
    {
        var summary = _store.Summarize(D(year, month, 1), Formatting.MonthEnd(year, month));

        Assert.Equal(Enumerable.Range(1, DateTime.DaysInMonth(year, month)).Select(d => d.ToString(CultureInfo.InvariantCulture)), summary.Series.Select(b => b.Label));
    }

    [Fact]
    public void Summarize_Month_AddsTheDaysBarsUpToTheMonthsExpenses()
    {
        var account = _store.AddAccount();
        _store.Enter(account, "2025-10-03", -12.40m, "Market", "groceries");
        _store.Enter(account, "2025-10-03", -7.60m, "Bakery", "groceries");
        _store.Enter(account, "2025-10-30", -80m, "Hardware", "shopping");

        var october = _store.Summarize(D(2025, 10, 1), D(2025, 10, 31));

        Assert.Equal((100m, 20m), (october.Series.Sum(b => b.Expenses), october.Series[2].Expenses));
    }

    private void SpendEveryMonth(int fromYear, int toYear)
    {
        var account = _store.AddAccount();
        for (var y = fromYear; y <= toYear; y++)
            for (var m = 1; m <= 12; m++) _store.Enter(account, $"{y}-{m:00}-10", -m, "Market", "groceries");
    }

    [Fact]
    public void Summarize_Month_IsOnePartAlone()
    {
        SpendEveryMonth(2025, 2025);

        var october = _store.Summarize(D(2025, 10, 1), D(2025, 10, 31));

        var only = Assert.Single(october.Breakdown);
        Assert.Equal(("2025-10-01", "2025-10-31", october.Balance), (only.From, only.To, only.Balance));
        Assert.StartsWith("October 2025", only.Title);
    }

    [Fact]
    public void Summarize_Year_IsOnePartAlone()
    {
        SpendEveryMonth(2025, 2025);

        var year = _store.Summarize(D(2025, 1, 1), D(2025, 12, 31), Granularity.Year);

        var whole = Assert.Single(year.Breakdown);
        Assert.Equal(("2025-01-01", "2025-12-31", year.Balance), (whole.From, whole.To, whole.Balance));
    }

    [Fact]
    public void Summarize_RangeOfLessThanAYear_IsCutInMonthsKeptInsideTheRange()
    {
        SpendEveryMonth(2025, 2025);

        var range = _store.Summarize(D(2025, 9, 15), D(2025, 11, 10), Granularity.Range);

        Assert.Equal(["2025-09-15", "2025-10-01", "2025-11-01"], range.Breakdown.Select(b => b.From));
        Assert.Equal("2025-11-10", range.Breakdown[^1].To);
        Assert.Equal(range.Balance, range.Breakdown.Sum(b => b.Balance));
    }

    [Fact]
    public void Summarize_RangeOfMoreThanAYear_IsCutInYears()
    {
        SpendEveryMonth(2024, 2026);

        var range = _store.Summarize(D(2024, 6, 1), D(2026, 2, 28), Granularity.Range);

        Assert.Equal(["2024-06-01", "2025-01-01", "2026-01-01"], range.Breakdown.Select(b => b.From));
        Assert.Equal(range.Balance, range.Breakdown.Sum(b => b.Balance));
        Assert.Equal(-78m, range.Breakdown[1].Balance);
    }

    // ------------------------------------------------------------------------------------------------ recurring payments

    private sealed record Line(DateOnly Date, string Label, string Category, decimal Amount);

    /// <summary>A National Bank style chequing export (newest first, running balance), from a list of lines.</summary>
    private static byte[] ChequingCsv(IEnumerable<Line> lines, decimal opening)
    {
        var rows = new List<string>();
        var balance = opening;
        foreach (var l in lines.OrderBy(l => l.Date))
        {
            balance += l.Amount;
            rows.Add(string.Join(';', Q(l.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), Q(l.Label), Q(l.Category),
                Q(l.Amount < 0 ? (-l.Amount).ToString(CultureInfo.InvariantCulture) : "0"),
                Q(l.Amount > 0 ? l.Amount.ToString(CultureInfo.InvariantCulture) : "0"), Q(balance.ToString("0.00", CultureInfo.InvariantCulture))));
        }
        rows.Reverse();
        return Encoding.UTF8.GetBytes("Date;Description;Categorie;Debit;Credit;Solde\n" + string.Join('\n', rows) + "\n");
        static string Q(string s) => "\"" + s + "\"";
    }

    /// <summary>
    /// Four months of a chequing account: rent and insurance early in the month, the phone on the 12th, salary twice a
    /// month, groceries at varying prices, a café at the same price every month, and transfers to another person.
    /// </summary>
    private static List<Line> FourMonths(int seed)
    {
        var rng = new Random(seed);
        var lines = new List<Line>();
        for (var m = 1; m <= 4; m++)
        {
            var rentDay = 1 + seed % 5;
            lines.Add(new(D(2026, m, rentDay), "Rent Prop Mgmt", "Hypotheque et loyer", -(1200 + seed * 10)));
            lines.Add(new(D(2026, m, rentDay), "Assurance Habitation Desjardins", "Assurances", -42.17m));
            lines.Add(new(D(2026, m, 12), "Videotron", "Internet", -(61.50m + (m % 3) * 0.25m)));
            lines.Add(new(D(2026, m, 14), "Employeur Inc", "Revenus", 2100.55m));
            lines.Add(new(D(2026, m, 28), "Employeur Inc", "Revenus", 2100.55m));
            lines.Add(new(D(2026, m, 9), "Cafe Olimpico", "Cafes", -4.75m));
            for (var g = 0; g < 4; g++) lines.Add(new(D(2026, m, 6 + g * 5), "Super C", "Epicerie", -Math.Round((decimal)(20 + rng.NextDouble() * 80), 2)));
            lines.Add(new(D(2026, m, 20), "Virement INTERAC", "Transfert", -Math.Round((decimal)(10 + rng.NextDouble() * 50), 2)));
        }
        return lines;
    }

    private async Task<PeriodSummary> RecurringMonthAsync(int month)
    {
        await _store.ImportAsync("jan-apr.csv", ChequingCsv(FourMonths(2), 3000));
        return _store.Summarize(D(2026, month, 1), Formatting.MonthEnd(2026, month));
    }

    private static List<string> RecurringBrands(PeriodSummary s) => [.. s.Transactions.Where(t => t.Recurring is not null).Select(t => t.Brand).Distinct()];

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Summarize_PaymentsAndIncomeOfEveryMonth_AreRecurring(int month)
    {
        var recurring = RecurringBrands(await RecurringMonthAsync(month));

        Assert.Contains(recurring, b => b.StartsWith("Rent", StringComparison.Ordinal));
        Assert.Contains(recurring, b => b.Contains("Desjardins", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(recurring, b => b.StartsWith("Vid", StringComparison.OrdinalIgnoreCase) && b.EndsWith("otron", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(recurring, b => b.Contains("Employeur", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Summarize_EverydayPurchasesAndTransfers_AreNotRecurring(int month)
    {
        var summary = await RecurringMonthAsync(month);

        Assert.DoesNotContain(RecurringBrands(summary), b => b.Contains("Super C", StringComparison.OrdinalIgnoreCase) || b.Contains("Olimpico", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(summary.Transactions, t => t.Recurring is not null && Categories.IsTransfer(t.CategoryId));
    }

    [Fact]
    public async Task Summarize_RecurringPayments_AddUpInTheirTotalAndTheirPie()
    {
        var summary = await RecurringMonthAsync(2);

        var recurring = -summary.Transactions.Where(t => t.Recurring is not null && t.Amount < 0).Sum(t => t.Amount);
        Assert.Equal(recurring, summary.RecurringExpenses);
        Assert.Equal(recurring, summary.RecurringExpensePie.Sum(x => x.Amount));
    }

    [Fact]
    public async Task Summarize_IncomeOfAMonth_AddsUpInTheIncomePie()
    {
        var summary = await RecurringMonthAsync(3);

        Assert.Equal((4201.10m, 4201.10m), (summary.Earnings, summary.IncomePie.Sum(x => x.Amount)));
    }

    [Fact]
    public async Task Summarize_RecurringPaymentsWithRent_GivesTheBurdenWithoutHousingAndItsShareOfEarnings()
    {
        var summary = await RecurringMonthAsync(2);

        const decimal rent = 1220m; // the rent of FourMonths(2)
        Assert.Equal(summary.RecurringExpenses - rent, summary.RecurringWithoutHousing);
        Assert.Equal((double)(summary.RecurringWithoutHousing / summary.Earnings), summary.RecurringWithoutHousingShare!.Value, 9);
        Assert.Equal((double)(summary.Expenses / summary.Earnings), summary.ExpensesShare!.Value, 9);
    }

    // ------------------------------------------------------------------------------------------------ movements

    /// <summary>A chequing account sending money to savings, paying its card, sending to a person and investing; the card and the savings imported too.</summary>
    private async Task<PeriodSummary> MarchWithMovementsAsync()
    {
        await _store.ImportAsync("chq.csv", ChequingCsv(
        [
            new(D(2026, 3, 2), "Transfert entre comptes", "Transfert", -250m),
            new(D(2026, 3, 5), "Paiement Mastercard", "Paiement carte de credit", -80.10m),
            new(D(2026, 3, 9), "Virement INTERAC", "Transfert", -35m),
            new(D(2026, 3, 10), "Paiement de facture", "Investissements", -500m),
            new(D(2026, 3, 15), "Employeur Inc", "Revenus", 2000m),
        ], 1000), accountName: "Chequing");
        await _store.ImportAsync("sav.csv", ChequingCsv([new(D(2026, 3, 2), "Transfert entre comptes", "Transfert", 250m)], 5000), kind: AccountKind.Savings, accountName: "Savings");
        const string card = "Date;\"Numero de Carte\";Description;Categorie;Debit;Credit\n\"2026-03-05\";\"************1111\";\"Paiement recu merci!\";\"Paiement carte de credit\";\"0\";\"80.10\"\n\"2026-03-04\";\"************1111\";IGA;Epicerie;\"80.10\";\"0\"\n";
        await _store.ImportAsync("card.csv", Encoding.UTF8.GetBytes(card), kind: AccountKind.CreditCard, accountName: "Card");
        return _store.Summarize(D(2026, 3, 1), D(2026, 3, 31));
    }

    [Fact]
    public async Task Summarize_TransferLinesOfTwoAccountsAndSingleHalves_MakesOneMovementEach()
    {
        var summary = await MarchWithMovementsAsync();

        Assert.Equal(4, summary.Movements.Count);
        Assert.Contains(summary.Movements, m => (m.From, m.To, m.Amount, m.FromOutside, m.ToOutside) == ("Chequing", "Savings", 250m, false, false));
        Assert.Contains(summary.Movements, m => (m.From, m.To, m.Amount) == ("Chequing", "Card", 80.10m) && m.OtherId is not null);
        Assert.Contains(summary.Movements, m => (m.From, m.To, m.Amount, m.ToOutside) == ("Chequing", Movements.Outside, 35m, true));
        Assert.Contains(summary.Movements, m => (m.From, m.To, m.Amount) == ("Chequing", Movements.OutsideInvestments, 500m));
    }

    [Fact]
    public async Task Summarize_CardPurchaseOfTheAmountOfACardPayment_StaysSpending()
    {
        var summary = await MarchWithMovementsAsync();

        Assert.Equal(80.10m, summary.Expenses);
    }

    [Fact]
    public async Task Summarize_Movements_AddUpToTheFlows()
    {
        var summary = await MarchWithMovementsAsync();

        Assert.Equal(summary.Movements.Sum(m => m.Amount), summary.Flows.Sum(f => f.Amount));
        Assert.Equal("Chequing account", summary.Movements.First(m => m.From == "Chequing").FromType);
        Assert.Null(summary.Movements.First(m => m.ToOutside).ToType);
    }

    [Fact]
    public async Task Summarize_Movements_DrawsOneBoxPerOwnAccountAndOneOutsideNamingWhoIsBehindIt()
    {
        var diagram = (await MarchWithMovementsAsync()).Diagram;

        Assert.Equal(["Card", "Chequing", "Savings"], diagram.Nodes.Where(n => !n.Outside).Select(n => n.Name).Order());
        Assert.Equal(("Credit card", "Savings account"), (diagram.Nodes.Single(n => n.Name == "Card").Type, diagram.Nodes.Single(n => n.Name == "Savings").Type));
        var outside = diagram.Nodes.Single(n => n.Outside);
        Assert.Contains(outside.Parties, x => x.Name == "Investments" && x.Sent == 500m);
        Assert.Equal(535m, outside.Parties.Sum(x => x.Sent));
    }

    [Fact]
    public async Task Summarize_Movements_DrawsOneArrowPerDirectionBetweenTwoPlaces()
    {
        var summary = await MarchWithMovementsAsync();
        var diagram = summary.Diagram;
        string Box(string name) => diagram.Nodes.Single(n => n.Name == name).Id;

        Assert.Equal(summary.Flows.Select(f => (f.FromOutside ? "out" : f.From, f.ToOutside ? "out" : f.To)).Distinct().Count(), diagram.Arrows.Count);
        Assert.Contains(diagram.Arrows, a => (a.From, a.To, a.Amount, a.Outside) == (Box("Chequing"), Box("Savings"), 250m, false));
        Assert.Contains(diagram.Arrows, a => (a.From, a.To, a.Amount, a.Outside) == (Box("Chequing"), FlowDiagram.OutsideId, 535m, true));
        Assert.All(diagram.Arrows, a => Assert.StartsWith("M", a.Path));
        Assert.All(diagram.Arrows, a => Assert.EndsWith("Z", a.Head));
    }

    [Fact]
    public async Task ImportAsync_ClosedTermInvestmentFiledAsAWithdrawal_IsOwnMoneyBackNotIncome()
    {
        await _store.ImportAsync("april.csv", ChequingCsv(
        [
            new(D(2026, 4, 16), "Employeur Inc", "Revenus", 2000m),
            new(D(2026, 4, 21), "Placement", "Retrait", 7587.51m),
            new(D(2026, 4, 21), "Placement", "Retrait", 13469.91m),
            new(D(2026, 4, 25), "Achat Boutique", "Magasinage", -40m),
        ], 1000), kind: AccountKind.Savings);

        var april = _store.Summarize(D(2026, 4, 1), D(2026, 4, 30));

        Assert.All(_store.Db.AllTransactions().Where(t => t.RawLabel == "Placement"), t => Assert.Equal(Categories.Investments, t.CategoryId));
        Assert.Equal(2000m, april.Earnings);
        Assert.Contains(april.Movements, m => m.From == Movements.OutsideInvestments && m.Amount == 13469.91m);
    }

    // ------------------------------------------------------------------------------------------------ reports kept and sent in pieces

    private string _rateSource = "ecb";

    private (long Account, ExchangeRates Rates) MarchAccount()
    {
        var account = _store.AddAccount();
        _store.Enter(account, "2026-03-04", -40m, "Market", "groceries");
        return (account, new ExchangeRates(_store.Db, () => _rateSource));
    }

    private PeriodReport March(ExchangeRates rates, string currency = "CAD") =>
        _store.Summaries.Report(D(2026, 3, 1), D(2026, 3, 31), Granularity.Month, currency, rates);

    [Fact]
    public void Report_SamePeriodAskedAgain_ReturnsTheReportAlreadyMade()
    {
        var (_, rates) = MarchAccount();

        var first = March(rates);

        Assert.Same(first, March(rates));
    }

    [Fact]
    public void Report_OtherCurrencyOrOtherPeriod_MakesItsOwnReport()
    {
        var (_, rates) = MarchAccount();

        var first = March(rates);

        Assert.NotSame(first, March(rates, "EUR"));
        Assert.NotSame(first, _store.Summaries.Report(D(2026, 3, 1), D(2026, 3, 30), Granularity.Range, "CAD", rates));
    }

    [Fact]
    public void Report_AfterAnOperationIsAdded_IsMadeAgainWithIt()
    {
        var (account, rates) = MarchAccount();
        var first = March(rates);

        _store.Enter(account, "2026-03-05", -10m, "Bakery", "groceries");

        var second = March(rates);
        Assert.NotSame(first, second);
        Assert.Equal(-50m, second.Banks.Single().Summary.Transactions.Sum(t => t.Amount));
    }

    [Fact]
    public void Report_AfterASettingIsWritten_IsMadeAgain()
    {
        var (_, rates) = MarchAccount();
        var first = March(rates);

        _store.Db.SetSetting("anything", "changed");

        Assert.NotSame(first, March(rates));
    }

    [Fact]
    public void Report_AfterAnotherSourceOfRatesIsChosen_IsMadeAgain()
    {
        var (_, rates) = MarchAccount();
        var first = March(rates);

        _rateSource = "boc";

        Assert.NotSame(first, March(rates));
    }

    private (PeriodReport Full, List<TransactionView> All) FebruaryOfTwentyEightOperations()
    {
        var account = _store.AddAccount();
        for (var d = 1; d <= 28; d++) _store.Enter(account, $"2026-02-{d:00}", -d, "Market", "groceries");
        var full = _store.Summaries.Report(D(2026, 2, 1), D(2026, 2, 28), Granularity.Month, "CAD", new ExchangeRates(_store.Db));
        return (full, full.Banks.Single().Summary.Transactions);
    }

    [Fact]
    public void Listing_LongPeriod_SendsTheLatestOperationsWithTheCountAndTheWholeTotals()
    {
        var (full, all) = FebruaryOfTwentyEightOperations();

        var part = full.Listing(10).Banks.Single().Summary;

        Assert.Equal(all.TakeLast(10).Select(t => t.Id), part.Transactions.Select(t => t.Id));
        Assert.Equal((28, full.Banks.Single().Summary.Expenses), (part.TransactionCount, part.Expenses));
        Assert.Null(full.Banks.Single().Summary.TransactionCount);
    }

    [Fact]
    public void Slice_OlderOperationsInPieces_MakeTheWholeListAgainInItsOrder()
    {
        var (full, all) = FebruaryOfTwentyEightOperations();
        var part = full.Listing(10).Banks.Single();

        var pieces = full.Slice(part.Key, 0, 4).Concat(full.Slice(part.Key, 4, 11)).Concat(full.Slice(part.Key, 11, 18));

        Assert.Equal(all.Take(18).Select(t => t.Id), pieces.Select(t => t.Id));
    }

    [Fact]
    public void Listing_ShortPeriod_SendsTheReportWhole()
    {
        var (full, _) = FebruaryOfTwentyEightOperations();

        Assert.Same(full, full.Listing(100));
    }
}
