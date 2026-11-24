using System.Text;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Testing;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.UnitTests.Parsing;

public class CsvStatementReaderTests
{
    private const string NoCurrency = "USD";

    private static StatementParse Parse(string csv, string fileName = "export.csv", string defaultCurrency = NoCurrency) =>
        CsvStatementReader.Parse(Encoding.UTF8.GetBytes(csv), fileName, defaultCurrency);

    private static List<(DateOnly, decimal)> Operations(StatementParse p) => [.. p.Rows.Select(r => (r.Date, r.Amount)).OrderBy(x => x)];

    // ------------------------------------------------------------------------------------------- any bank's layout

    [Fact]
    public void Parse_HeaderOldestFirstMonthDayDates_ReadsOperationsAndBalances()
    {
        const string csv = "Date,Description,Withdrawals,Deposits,Balance\n10/03/2025,TIM HORTONS #123,4.50,,995.50\n10/05/2025,PAYROLL DEPOSIT EXAMPLE CORP,,1500.00,2495.50\n10/14/2025,\"COSTCO WHOLESALE, LAVAL\",210.33,,2285.17\n";

        var result = Parse(csv, "chequing.csv", "CAD");

        Assert.Equal((VerificationStatus.Verified, 1000.00m), (result.Status, result.OpeningBalance));
        Assert.Equal([-4.50m, 1500.00m, -210.33m], result.Rows.Select(r => r.Amount));
        Assert.Equal((new DateOnly(2025, 10, 14), "COSTCO WHOLESALE, LAVAL"), (result.Rows[2].Date, result.Rows[2].Label));
    }

    [Fact]
    public void Parse_NoHeaderLine_FindsTheColumnsByTheirContent()
    {
        const string csv = "2025-10-03,TIM HORTONS,4.50,,995.50\r\n2025-10-04,E-TRANSFER RECEIVED,,40.00,1035.50\r\n";

        var result = Parse(csv, "export.csv", "CAD");

        Assert.Equal(VerificationStatus.Verified, result.Status);
        Assert.Equal([-4.50m, 40.00m], result.Rows.Select(r => r.Amount));
        Assert.Equal("TIM HORTONS", result.Rows[0].Label);
    }

    [Fact]
    public void Parse_CardExportWithPositivePurchases_TurnsTheSignsAround()
    {
        const string csv = "Transaction Date,Description,Amount\n2025-10-02,NETFLIX.COM,20.69\n2025-10-05,IGA EXTRA,54.10\n2025-10-09,PAYMENT THANK YOU,-74.79\n";

        var result = Parse(csv, "visa-october.csv", "CAD");

        Assert.Equal(AccountKind.CreditCard, result.Kind);
        Assert.Equal([-20.69m, -54.10m, 74.79m], result.Rows.Select(r => r.Amount));
    }

    [Fact]
    public void Parse_CurrencyTheExamplesNeverUse_ReadsAndBalancesIt()
    {
        const string csv = "Booking date;Text;Amount (CHF);Balance (CHF)\n03.02.2026;Migros Example;-45.10 CHF;954.90 CHF\n05.02.2026;Lohn Example AG;CHF 5'200.00;CHF 6'154.90\n09.02.2026;Miete;-1'800.00;4'354.90\n";

        var result = Parse(csv, "export.csv", "CHF");

        Assert.Equal(VerificationStatus.Verified, result.Status);
        Assert.Equal([-45.10m, 5200.00m, -1800.00m], result.Rows.Select(r => r.Amount));
        Assert.Equal(new DateOnly(2026, 2, 5), result.Rows[1].Date);
        Assert.Equal((1000.00m, 4354.90m), (result.OpeningBalance!.Value, result.ClosingBalance!.Value));
    }

    [Theory]
    [InlineData("Date;Text;Amount (CHF);Balance (CHF)\n03.02.2026;Migros;-45.10;954.90\n05.02.2026;Lohn;5200.00;6154.90\n", "CHF")]
    [InlineData("Date;Text;Amount;Balance\n03.02.2026;Migros;-45.10 CHF;954.90 CHF\n05.02.2026;Lohn;5200.00 CHF;6154.90 CHF\n", "CHF")]
    [InlineData("Date,Description,Amount\n2026-02-03,Bakery,-4.50 €\n2026-02-05,Salary,2000.00 €\n", "EUR")]
    [InlineData("Date,Description,Amount\n2026-02-03,Bakery,-$4.50\n2026-02-05,Salary,$2000.00\n", "CAD")]   // dollars of no country: the person's
    public void Parse_MoneyColumnsForAPersonInCanadianDollars_IsInTheCurrencyTheyNameElseThePersons(string csv, string currency)
    {
        var result = Parse(csv, "export.csv", "CAD");

        Assert.Equal(currency, result.Currency);
    }

    [Fact]
    public void Parse_OperationsThatNeverMovedMoney_LeavesThemOut()
    {
        const string csv = "Date,Description,Amount,Status,Balance\n2026-04-01 09:00:00,Coffee,-3.50,Completed,96.50\n2026-04-02 10:00:00,Hotel,-200.00,Declined,\n2026-04-03T11:00:00Z,Books,-20.00,Pending,\n"
            + "2026-04-04 12:00,Refund,5.00,Posted,101.50\n2026-04-05 13:00,Taxi,-11.00,Cancelled,\n";

        var result = Parse(csv, "history.csv");

        Assert.Equal(VerificationStatus.Verified, result.Status);
        Assert.Equal([(new DateOnly(2026, 4, 1), -3.50m), (new DateOnly(2026, 4, 4), 5.00m)], result.Rows.Select(r => (r.Date, r.Amount)));
    }

    [Fact]
    public void Parse_FeeColumnInANewestFirstExport_MakesTheFeeALineOfItsOwn()
    {
        const string csv = "Date,Details,Amount,Commission,Balance\n2026-05-03,Wire abroad,-100.00,2.00,48.00\n2026-05-02,Bakery,-50.00,0,150.00\n2026-05-01,Salary,200.00,,200.00\n";

        var result = Parse(csv, "history.csv");

        Assert.Equal((VerificationStatus.Verified, true), (result.Status, result.NewestFirst));
        Assert.Equal([200.00m, -50.00m, -100.00m, -2.00m], result.Rows.Select(r => r.Amount));
        Assert.Equal("Commission: Wire abroad", result.Rows[^1].Label);
        Assert.Equal((0m, 48.00m), (result.OpeningBalance!.Value, result.ClosingBalance!.Value));
    }

    [Fact]
    public void Parse_LineDatedInTheNextMonth_KeepsItsOwnDate()
    {
        const string csv = "Date;Description;Categorie;Debit;Credit;Solde\n\"2025-10-01\";\"Loyer Exemple\";\"Hypotheque et loyer\";\"1500.0\";\"0\";\"500.00\"\n\"2025-09-29\";Salaire;Revenus;\"0\";\"1975.0\";\"1975.00\"\n";

        var result = Parse(csv, "x.csv", "CAD");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal((new DateOnly(2025, 10, 1), -1500m, "Hypotheque et loyer"), (result.Rows[1].Date, result.Rows[1].Amount, result.Rows[1].BankCategory));
    }

    [Fact]
    public void Parse_FrenchCommaDecimalsInWindows1252_ReadsAmountsAndCurrency()
    {
        const string csv = "Date;Libellé;Montant;Devise\n12/10/2025;CB CARREFOUR MARKET;-1 234,56;EUR\n13/10/2025;VIR SEPA RECU SALAIRE;2 150,00;EUR\n";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var result = CsvStatementReader.Parse(Encoding.GetEncoding(1252).GetBytes(csv), "releve.csv", "CAD");

        Assert.Equal(("EUR", VerificationStatus.NoBalances), (result.Currency, result.Status));
        Assert.Equal([-1234.56m, 2150.00m], result.Rows.Select(r => r.Amount));
        Assert.Equal(new DateOnly(2025, 10, 12), result.Rows[0].Date);
    }

    [Theory]
    [InlineData("Credit Mutuel export 2026", false, false)]
    [InlineData("Caisse d'Epargne compte courant", false, false)]
    [InlineData("Caisse d'Epargne Livret A", false, true)]
    [InlineData("Credit Agricole Visa", true, false)]
    [InlineData("American Express Gold", true, false)]
    public void Parse_FileNameOnlyTellsTheKind_TheBanksNameNeverDecidesIt(string fileName, bool card, bool savings)
    {
        const string csv = "Date,Description,Amount,Balance\n2026-02-03,Shop Example,-10.00,90.00\n2026-02-05,Shop Example,-5.00,85.00\n";

        var result = Parse(csv, fileName + ".csv", "EUR");

        Assert.Equal((card, savings), (result.Kind == AccountKind.CreditCard, result.Kind == AccountKind.Savings));
    }

    [Theory]
    [InlineData(';')]
    [InlineData(',')]
    [InlineData('\t')]
    public void Parse_ShortTableUnderAsManyLinesOfTitle_ReadsItAndProvesItWithTheBalanceUnderIt(char separator)
    {
        var d = separator;
        var csv = $"Savings account No 99887766\nA Sample\nFrom 2026-05-01 to 2026-05-31\nDate{d}Description{d}Withdrawal{d}Deposit{d}Balance\n"
            + $"2026-05-12{d}Transfer from chequing{d}{d}150.00{d}4360.00\n{d}Balance on 2026-05-31{d}{d}{d}4360.00\n";

        var result = Parse(csv);

        Assert.Equal([(new DateOnly(2026, 5, 12), 150.00m)], result.Rows.Select(r => (r.Date, r.Amount)));
        Assert.Equal((VerificationStatus.Verified, 4210.00m, 4360.00m), (result.Status, result.OpeningBalance, result.ClosingBalance));
    }

    [Fact]
    public void Parse_BalancePrintedUnderTheTableDisagreesWithTheRunningBalance_IsAMismatch()
    {
        const string csv = "Savings account No 99887766\nA Sample\nFrom 2026-05-01 to 2026-05-31\nDate;Description;Withdrawal;Deposit;Balance\n"
            + "2026-05-12;Transfer from chequing;;150.00;4360.00\n;Balance on 2026-05-31;;;4300.00\n";

        var result = Parse(csv);

        Assert.Equal(VerificationStatus.Mismatch, result.Status);
    }

    // ------------------------------------------------------------------------------------------- dates in the bank's format

    /// <summary>3 April and 5 April, or 4 March and 4 May: every number is 12 or less.</summary>
    private static readonly byte[] TwoNumbersOf12OrLess = Encoding.UTF8.GetBytes("Date,Description,Amount\n03/04/2026,Bakery,-4.50\n05/04/2026,Salary,2000.00\n");

    [Theory]
    [InlineData(DateFormat.DayMonthYear, 4, 3)]
    [InlineData(DateFormat.MonthDayYear, 3, 4)]
    public void Parse_CsvAtABankOfAFormat_ReadsTheDatesInThatFormat(DateFormat format, int month, int day)
    {
        var result = CsvStatementReader.Parse(TwoNumbersOf12OrLess, "export.csv", "CAD", null, format);

        Assert.Equal((new DateOnly(2026, month, day), (string?)null), (result.Rows[0].Date, result.DateFormatMismatch));
    }

    [Theory]
    [InlineData("Date,Description,Amount\n03/14/2026,Bakery,-4.50\n", DateFormat.DayMonthYear, "written MM/DD/YYYY, not DD/MM/YYYY")]
    [InlineData("Date,Description,Amount\n14/03/2026,Bakery,-4.50\n", DateFormat.MonthDayYear, "written DD/MM/YYYY, not MM/DD/YYYY")]
    [InlineData("Date,Description,Amount\n14/03/2026,Bakery,-4.50\n", DateFormat.YearMonthDay, "written DD/MM/YYYY, not YYYY-MM-DD")]
    [InlineData("Date,Description,Amount\n03/04/2026,Bakery,-4.50\n", DateFormat.YearMonthDay, "written with the year last, not YYYY-MM-DD")]
    public void Parse_CsvWhoseDatesDoNotFitTheBanksFormat_SaysHowTheyAreWritten(string csv, DateFormat format, string said)
    {
        var result = CsvStatementReader.Parse(Encoding.UTF8.GetBytes(csv), "export.csv", "CAD", null, format);

        Assert.Contains(said, result.DateFormatMismatch);
    }

    [Theory]
    [InlineData(DateFormat.DayMonthYear)]
    [InlineData(DateFormat.MonthDayYear)]
    [InlineData(DateFormat.YearMonthDay)]
    public void Parse_CsvWithDatesYearFirst_FitsEveryFormat(DateFormat format)
    {
        var result = CsvStatementReader.Parse(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-03-14,Bakery,-4.50\n"), "export.csv", "CAD", null, format);

        Assert.Equal((new DateOnly(2026, 3, 14), (string?)null), (result.Rows[0].Date, result.DateFormatMismatch));
    }

    [Fact]
    public void Parse_CsvBeforeABankIsChosen_ReportsNoMismatch()
    {
        var result = Parse("Date,Description,Amount\n03/14/2026,Bakery,-4.50\n", "export.csv", "CAD");

        Assert.Null(result.DateFormatMismatch);
    }

    // ------------------------------------------------------------------------------------------- several accounts

    [Fact]
    public void Accounts_TwoAccountsWhoseBalancesFollowAccountByAccount_SplitsTheFileInTwoBalancedParts()
    {
        const string csv = "Date,Account,Description,Amount,Balance\n2026-06-01,Everyday,Bakery,-5.00,95.00\n2026-06-01,Rainy day,Interest,1.00,501.00\n2026-06-02,Everyday,Bus,-3.00,92.00\n2026-06-03,Rainy day,Deposit,50.00,551.00\n";

        var parts = CsvStatementReader.Accounts(CsvStatementReader.Table(Encoding.UTF8.GetBytes(csv)), NoCurrency);

        Assert.Equal(["Everyday", "Rainy day"], parts.Select(p => p.Title));
        Assert.All(parts, p => Assert.Equal(VerificationStatus.Verified, CsvStatementReader.Parse(CsvStatementReader.Write(p.Table), "x.csv", NoCurrency).Status));
    }

    [Theory]
    // Lines naming who was paid, in a file that balances as one account.
    [InlineData("Date,Account,Description,Amount,Balance\n2026-06-01,ACME Ltd,Bakery,-5.00,95.00\n2026-06-02,City Transit,Bus,-3.00,92.00\n2026-06-03,Employer Inc,Pay,50.00,142.00\n")]
    // No balance to prove two accounts.
    [InlineData("Date,Account,Description,Amount\n2026-06-01,A,Bakery,-5.00\n2026-06-02,B,Bus,-3.00\n")]
    public void Accounts_NothingProvingTwoAccounts_KeepsTheFileWhole(string csv)
    {
        var parts = CsvStatementReader.Accounts(CsvStatementReader.Table(Encoding.UTF8.GetBytes(csv)), NoCurrency);

        Assert.Single(parts);
    }

    // ------------------------------------------------------------------------------------------- text encoding

    private const string FrenchText = "Date;Libellé;Montant;Devise\n12/10/2025;CAFÉ DU COIN SAINT-ÉTIENNE;-4,50;EUR\n13/10/2025;VIR SEPA REÇU;100,00;EUR\n";

    private static byte[] Windows1252(string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252).GetBytes(text);
    }

    [Fact]
    public void IsWindows1252_AccentsWrittenInWindows1252_ReturnsTrue() => Assert.True(CsvStatementReader.IsWindows1252(Windows1252(FrenchText)));

    [Theory]
    [InlineData(FrenchText)]
    [InlineData("Date;Amount\n")]
    public void IsWindows1252_TextValidInUtf8_ReturnsFalse(string text) => Assert.False(CsvStatementReader.IsWindows1252(Encoding.UTF8.GetBytes(text)));

    [Theory]
    [InlineData(1252)]
    [InlineData(65001)]
    public void Decode_TextInWindows1252OrUtf8_ReturnsEveryCharacterAsWritten(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(codePage).GetBytes(FrenchText);

        var result = CsvStatementReader.Decode(bytes);

        Assert.Equal(FrenchText, result);
    }

    // ------------------------------------------------------------------------------------------- National Bank of Canada

    private static readonly BncGenerator.Month September = BncGenerator.Sample(2025, 9, 31);

    [Theory]
    [InlineData(BncGenerator.BncAccount.Card)]
    [InlineData(BncGenerator.BncAccount.Chequing)]
    [InlineData(BncGenerator.BncAccount.Savings)]
    public void Parse_NationalBankExport_ReadsEveryLineAsWritten(BncGenerator.BncAccount account)
    {
        var result = CsvStatementReader.Parse(BncGenerator.Csv(September, account), BncGenerator.FileName(account, "csv"), "CAD");

        BncAssert.SameLines(September.Of(account), result);
    }

    [Theory]
    [InlineData(BncGenerator.BncAccount.Card, AccountKind.CreditCard, VerificationStatus.NoBalances)]
    [InlineData(BncGenerator.BncAccount.Chequing, AccountKind.Bank, VerificationStatus.Verified)]
    [InlineData(BncGenerator.BncAccount.Savings, AccountKind.Savings, VerificationStatus.Verified)]
    public void Parse_NationalBankExport_ReturnsTheAccountsKindAndBalanceCheck(BncGenerator.BncAccount account, AccountKind kind, VerificationStatus status)
    {
        var result = CsvStatementReader.Parse(BncGenerator.Csv(September, account), BncGenerator.FileName(account, "csv"), "CAD");

        Assert.Equal((kind, status), (result.Kind, result.Status));
    }

    [Fact]
    public void Parse_NationalBankCardExport_ReadsTheCardsLastFourDigits()
    {
        var result = CsvStatementReader.Parse(BncGenerator.Csv(September, BncGenerator.BncAccount.Card), BncGenerator.FileName(BncGenerator.BncAccount.Card, "csv"), "CAD");

        Assert.Equal("1234", result.Last4);
    }

    // ------------------------------------------------------------------------------------------- Caisse d'Epargne

    private static readonly CaisseEpargneGenerator.Statement CaisseOctober = CaisseEpargneGenerator.Sample(2025, 10, 30);

    private static List<(DateOnly, decimal)> Operations(CaisseEpargneGenerator.Account a) => [.. a.Lines.Select(l => (l.Date, l.Amount)).OrderBy(x => x)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_CaisseEpargneExport_ReadsTheSameOperationsAsTheStatement(bool decimalComma)
    {
        var account = CaisseOctober.Accounts[0];

        var result = CsvStatementReader.Parse(CaisseEpargneGenerator.Csv(account, decimalComma), "export.csv", "EUR");

        Assert.Equal(Operations(account), Operations(result));
        Assert.Equal(AccountKind.Bank, result.Kind);
    }

    [Fact]
    public void Parse_CaisseEpargneExport_KeepsTheFullLabelWithoutTheSimplifiedOneOrTheBanksTick()
    {
        var result = CsvStatementReader.Parse(CaisseEpargneGenerator.Csv(CaisseOctober.Accounts[0]), "export.csv", "EUR");

        Assert.All(result.Rows.Where(r => r.Label.StartsWith("CB ", StringComparison.Ordinal)), r => Assert.Matches(@"^CB [A-Z ]+ FACT \d{6}$", r.Label));
        Assert.DoesNotContain(result.Rows, r => r.Label.EndsWith(" 0", StringComparison.Ordinal) || r.Label.StartsWith("MONOPRIX CB", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_CaisseEpargneExport_ReturnsTheBanksMoreDetailedSubcategory()
    {
        var result = CsvStatementReader.Parse(CaisseEpargneGenerator.Csv(CaisseOctober.Accounts[0]), "export.csv", "EUR");

        Assert.Equal("Salaire", result.Rows.Single(r => r.Amount == 2450m).BankCategory);
    }

    [Fact]
    public void Parse_CaisseEpargneOlderExport_ReadsBalancesAboveAndBelowTheTable()
    {
        var account = CaisseOctober.Accounts[0];

        var result = CsvStatementReader.Parse(CaisseEpargneGenerator.OldCsv(CaisseOctober, account), "Caisse d'Epargne export.csv", "CAD");

        Assert.Equal(Operations(account), Operations(result));
        Assert.Equal((VerificationStatus.Verified, account.Opening, account.Closing, "EUR", "5678"), (result.Status, result.OpeningBalance, result.ClosingBalance, result.Currency, result.Last4));
    }

    [Theory]
    [InlineData(0, AccountKind.Bank)]
    [InlineData(1, AccountKind.Savings)]
    public void Parse_CaisseEpargneOlderExport_TakesTheKindFromTheAccountNotTheBanksName(int account, AccountKind kind)
    {
        var result = CsvStatementReader.Parse(CaisseEpargneGenerator.OldCsv(CaisseOctober, CaisseOctober.Accounts[account]), "Caisse d'Epargne export.csv", "CAD");

        Assert.Equal(kind, result.Kind);
    }

    // ------------------------------------------------------------------------------------------- CIC (France)

    private static readonly CicGenerator.Statement CicNovember = CicGenerator.Sample(2025, 11, 9);

    [Fact]
    public void Parse_CicExport_ReadsEveryOperationAndBalancesWithTheRunningBalance()
    {
        var account = CicNovember.Accounts[0];

        var result = CsvStatementReader.Parse(CicGenerator.Csv(account), "CIC 00020123401.csv", "CAD");

        Assert.Equal([.. account.Lines.Select(l => (l.Date, l.Amount)).OrderBy(x => x)], Operations(result));
        Assert.Equal((VerificationStatus.Verified, account.Opening, account.Closing, AccountKind.Bank), (result.Status, result.OpeningBalance, result.ClosingBalance, result.Kind));
    }

    [Fact]
    public void Parse_CicExport_ReadsItsIso885915TextAsWritten()
    {
        var result = CsvStatementReader.Parse(CicGenerator.Csv(CicNovember.Accounts[0]), "CIC 00020123401.csv", "CAD");

        Assert.Contains(result.Rows, r => r.Label == "PRLV SEPA EDF CLIENTS PARTICULIERS REF MANDAT EX00001234");
    }

    [Fact]
    public void Parse_CicExportNamingNoCurrency_IsInEurosByItsSepaOperations()
    {
        var result = CsvStatementReader.Parse(CicGenerator.Csv(CicNovember.Accounts[0]), "CIC 00020123401.csv", "CAD");

        Assert.Equal("EUR", result.Currency);
    }
}
