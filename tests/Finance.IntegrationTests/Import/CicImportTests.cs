using System.Globalization;
using System.Text;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Import;

/// <summary>
/// The CIC, a French bank: its statements in every form it offers (the monthly PDF with every account, the CSV export,
/// the Excel export with a sheet per account, OFX and QIF) imported by a person whose own currency is the Canadian
/// dollar. Every one is a statement in euros, and every euro is converted at the European Central Bank's rate of its day.
/// </summary>
public sealed class CicImportTests : IDisposable
{
    private static readonly CicGenerator.Statement October = CicGenerator.Sample(2025, 10, 5);
    private static readonly CicGenerator.Account Current = October.Accounts[0];
    private static readonly CicGenerator.Account LivretA = October.Accounts[1];

    private readonly TestStore _store = new();
    private readonly long _cic;

    public CicImportTests() => _cic = _store.AddBank("CIC", BankingSystem.France, "EUR", "France");

    public void Dispose() => _store.Dispose();

    private static List<(DateOnly, decimal)> Ops(CicGenerator.Account a) => [.. a.Lines.Select(l => (l.Date, l.Amount)).Order()];

    private static List<(DateOnly, decimal)> Ops(ImportDraft d) => [.. d.Parse.Rows.Select(r => (r.Date, r.Amount)).Order()];

    public static TheoryData<string, byte[]> EveryForm => new()
    {
        { "Releve CIC.pdf", CicGenerator.Pdf(October) },
        { "CIC 00020123401.csv", CicGenerator.Csv(Current) },
        { "CIC export.xlsx", CicGenerator.Xlsx(October) },
        { "CIC.ofx", CicGenerator.Ofx(October, Current) },
        { "CIC.qif", CicGenerator.Qif(Current) },
    };

    public static TheoryData<string, byte[]> FormsOfEveryAccount => new()
    {
        { "Releve CIC.pdf", CicGenerator.Pdf(October) },
        { "CIC export.xlsx", CicGenerator.Xlsx(October) },
    };

    // ------------------------------------------------------------------------------------------------ reading

    [Theory]
    [MemberData(nameof(EveryForm))]
    public async Task ParseAsync_AnyFormOfTheStatementForAPersonInCanadianDollars_IsAStatementInEuros(string fileName, byte[] bytes)
    {
        var drafts = await _store.ParseAllAsync(fileName, bytes);

        Assert.All(drafts, d => Assert.Equal("EUR", d.Parse.Currency));
    }

    [Theory]
    [MemberData(nameof(EveryForm))]
    public async Task ParseAsync_AnyFormOfTheStatement_ReadsEveryOperationOfTheCurrentAccount(string fileName, byte[] bytes)
    {
        var drafts = await _store.ParseAllAsync(fileName, bytes);

        Assert.Equal(Ops(Current), Ops(drafts[0]));
    }

    [Theory]
    [MemberData(nameof(FormsOfEveryAccount))]
    public async Task ParseAsync_FileOfEveryAccount_GivesOneBalancedStatementPerAccount(string fileName, byte[] bytes)
    {
        var drafts = await _store.ParseAllAsync(fileName, bytes);

        Assert.Equal([(Current.Opening, Current.Closing), (LivretA.Opening, LivretA.Closing)], drafts.Select(d => (d.Parse.OpeningBalance!.Value, d.Parse.ClosingBalance!.Value)));
        Assert.All(drafts, d => Assert.Equal(VerificationStatus.Verified, d.Parse.Status));
        Assert.Equal([AccountKind.Bank, AccountKind.Savings], drafts.Select(d => d.Parse.Kind));
        Assert.Equal(Ops(LivretA), Ops(drafts[1]));
    }

    [Fact]
    public async Task ParseAsync_MonthlyPdf_GivesEachAccountItsNumber()
    {
        var drafts = await _store.ParseAllAsync("Releve CIC.pdf", CicGenerator.Pdf(October));

        Assert.Equal([Current.Number[^4..], LivretA.Number[^4..]], drafts.Select(d => d.Parse.Last4));
    }

    [Fact]
    public async Task ParseAsync_MonthlyPdf_KeepsTheShopOfACardPaymentAndLeavesBalancesAndTotalsOut()
    {
        var rows = (await _store.ParseAllAsync("Releve CIC.pdf", CicGenerator.Pdf(October)))[0].Parse.Rows;

        Assert.Contains(rows, r => r.Label.StartsWith("PAIEMENT CB", StringComparison.Ordinal) && r.Label.Contains("CARREFOUR CITY", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Label.Contains("SOLDE", StringComparison.Ordinal) || r.Label.Contains("Total des mouvements", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ParseAsync_ExcelExport_NamesEachStatementAfterItsAccountsSheet()
    {
        var drafts = await _store.ParseAllAsync("CIC export.xlsx", CicGenerator.Xlsx(October));

        Assert.Equal(["CIC export · C/C EUROCOMPTE DUO CONFORT.xlsx", "CIC export · LIVRET A.xlsx"], drafts.Select(d => d.FileName));
    }

    [Theory]
    [MemberData(nameof(FormsOfEveryAccount))]
    public async Task ParseAsync_FileOfEveryAccountSavedBefore_SaysEveryAccountIsAlreadyImported(string fileName, byte[] bytes)
    {
        _store.CommitAll(await _store.ParseAllAsync(fileName, bytes), bank: _cic);

        var again = await _store.ParseAllAsync(fileName, bytes);

        Assert.All(again, d => Assert.True(d.AlreadyImported));
    }

    [Theory]
    [InlineData("PAIEMENT CB 0310 PARIS CARREFOUR CITY CARTE 0742XXXX5521", -23.40, "groceries")]
    [InlineData("VIR LIVRET A", -150, Categories.Transfers)]
    [InlineData("RETRAIT DAB 0710 PARIS 11 CARTE 0742XXXX5521", -40, "cash")]
    [InlineData("F COTIS EUROCOMPTE DUO CONFORT", -11.50, "fees")]
    [InlineData("VIR SEPA LOYER SCI EXEMPLE", -760, "housing")]
    public async Task ParseAsync_StatementOfAFrenchBank_ClassifiesItsOperationsTheFrenchWay(string label, double amount, string category)
    {
        var csv = Encoding.UTF8.GetBytes($"Date;Libellé;Montant\n03/10/2025;{label};{((decimal)amount).ToString(CultureInfo.GetCultureInfo("fr-FR"))}\n");
        var draft = await _store.ParseAsync("cic.csv", csv);

        _store.Imports.Choose(draft.Id, null, AccountKind.Bank, _cic);

        Assert.Equal(category, draft.Rows.Single().CategoryId);
    }

    // ------------------------------------------------------------------------------------------------ converted at the ECB's rate

    /// <summary>The European Central Bank's euro rate in Canadian dollars, invented: 1.5 plus a thousandth for each day of the month.</summary>
    private static decimal CadPerEuro(DateOnly day) => 1.5m + day.Day / 1000m;

    /// <summary>The day whose rate applies: the bank publishes none on Saturdays and Sundays, so the Friday before.</summary>
    private static DateOnly RateDay(DateOnly day) => day.DayOfWeek switch
    {
        DayOfWeek.Saturday => day.AddDays(-1),
        DayOfWeek.Sunday => day.AddDays(-2),
        _ => day,
    };

    private static decimal InCad(decimal euros, DateOnly day) => Math.Round(euros * CadPerEuro(RateDay(day)), 2, MidpointRounding.AwayFromZero);

    private ExchangeRates EcbRates()
    {
        var rates = new ExchangeRates(_store.Db, () => "ecb");
        var history = RateFiles.EuropeanCentralBankHistory(new(2025, 9, 20), new(2025, 11, 5), ["USD", "CAD"], (c, d) => c == "CAD" ? CadPerEuro(d) : 1.1m);
        rates.Save(RateSource.Ecb, Encoding.UTF8.GetBytes(history));
        return rates;
    }

    /// <summary>The monthly PDF imported at the CIC, both accounts in the currency the statement says, reported for October in <paramref name="currency"/>.</summary>
    private async Task<PeriodReport> ImportedOctoberAsync(string currency)
    {
        var rates = EcbRates();
        _store.CommitAll(await _store.ParseAllAsync("Releve CIC.pdf", CicGenerator.Pdf(October)), bank: _cic);
        _store.Transactions.Convert = (amount, from, to, day) => rates.Convert(amount, from, to, day)?.Amount;
        return _store.Summaries.Report(October.Start, October.End, Granularity.Month, currency, rates);
    }

    private BankPart CicPart(PeriodReport r) => r.Banks.Single(b => b.Name == "CIC");

    [Fact]
    public async Task Commit_MonthlyPdfAtAFrenchBank_SavesEveryAccountInEuros()
    {
        await ImportedOctoberAsync("CAD");

        Assert.All(_store.Db.Accounts(), a => Assert.Equal(("EUR", _cic), (a.Currency, a.BankId)));
        Assert.All(_store.Db.AllTransactions(), t => Assert.Equal("EUR", t.Currency));
    }

    [Fact]
    public async Task Report_InCanadianDollars_SaysTheBanksOwnCurrencyIsTheEuro()
    {
        var report = await ImportedOctoberAsync("CAD");

        Assert.Equal(("CAD", "EUR"), (CicPart(report).Summary.Currency, CicPart(report).Native));
    }

    [Fact]
    public async Task Report_InCanadianDollars_ConvertsEachOperationAtTheEcbRateOfItsDay()
    {
        var report = await ImportedOctoberAsync("CAD");

        var shown = CicPart(report).Summary.Transactions;
        Assert.Equal(Current.Lines.Count + LivretA.Lines.Count, shown.Count);
        Assert.All(shown, t => Assert.Equal(("CAD", "EUR"), (t.Currency, t.OriginalCurrency)));
        Assert.All(shown, t => Assert.Equal(InCad(t.Original!.Value, DateOnly.ParseExact(t.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)), t.Amount));
    }

    [Fact]
    public async Task Report_InCanadianDollars_OperationOnASaturdayTakesFridaysRate()
    {
        var report = await ImportedOctoberAsync("CAD");

        var saturday = CicPart(report).Summary.Transactions.First(t => DateOnly.ParseExact(t.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayOfWeek == DayOfWeek.Saturday);
        var day = DateOnly.ParseExact(saturday.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(Math.Round(saturday.Original!.Value * CadPerEuro(day.AddDays(-1)), 2, MidpointRounding.AwayFromZero), saturday.Amount);
    }

    [Fact]
    public async Task Report_InCanadianDollars_AddsUpTheConvertedExpensesLeavingTheMoneyPutOnTheLivretAOut()
    {
        var report = await ImportedOctoberAsync("CAD");

        var spent = Current.Lines.Where(l => l.Amount < 0 && l.Label != "VIR LIVRET A").Sum(l => -InCad(l.Amount, l.Date));
        Assert.Equal(spent, CicPart(report).Summary.Expenses);
        Assert.Equal(spent, report.Global.Expenses);
    }

    [Fact]
    public async Task Report_InCanadianDollars_ShowsHowTheEurosWereConverted()
    {
        var report = await ImportedOctoberAsync("CAD");

        var line = Assert.Single(report.Conversion.Lines);
        Assert.Equal(("CIC", "EUR", "CAD", 0), (line.Group, line.From, line.To, line.Unconverted));
        Assert.Equal(Current.Debits - 150m, line.Expenses);
        Assert.All(line.Rates, u => Assert.Equal(CadPerEuro(RateDay(DateOnly.ParseExact(u.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture))), u.Rate));
        Assert.Equal((RateSource.Ecb.Url, "EUR"), (report.Conversion.Url, report.Conversion.Base));
        Assert.Empty(report.Unconverted);
    }

    [Fact]
    public async Task Report_InEuros_ShowsTheAmountsAsTheBankWroteThem()
    {
        var report = await ImportedOctoberAsync("EUR");

        Assert.Equal(Current.Lines.Concat(LivretA.Lines).Select(l => l.Amount).Order(), CicPart(report).Summary.Transactions.Select(t => t.Amount).Order());
        Assert.Equal(Current.Debits - 150m, report.Global.Expenses);
        Assert.Empty(report.Conversion.Lines);
    }

    [Fact]
    public async Task Report_MoneyPutOnTheLivretA_IsOneMovementInsideTheBank()
    {
        var report = await ImportedOctoberAsync("EUR");

        var names = _store.Db.Accounts().ToDictionary(a => a.Kind, a => a.Name);
        var inside = Assert.Single(report.Global.Movements, m => !m.FromOutside && !m.ToOutside);
        Assert.Equal((names[AccountKind.Bank], names[AccountKind.Savings], 150m), (inside.From, inside.To, inside.Amount));
    }

    [Fact]
    public async Task Report_WithoutAnyRate_LeavesTheEurosOutOfTheCanadianTotalsAndSaysSo()
    {
        _store.CommitAll(await _store.ParseAllAsync("Releve CIC.pdf", CicGenerator.Pdf(October)), bank: _cic);

        var report = _store.Summaries.Report(October.Start, October.End, Granularity.Month, "CAD", new ExchangeRates(_store.Db));

        Assert.Equal(Current.Lines.Count + LivretA.Lines.Count, report.Unconverted.Count);
        Assert.Equal((0m, 0m), (report.Global.Expenses, report.Global.Earnings));
    }
}
