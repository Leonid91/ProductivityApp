using System.Text;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Pdf;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.IntegrationTests.Import;

/// <summary>
/// Files read and saved through the import, as the person does it: every format it takes, the account and bank they go
/// to, files refused or already imported.
/// </summary>
public sealed class ImportServiceTests : IDisposable
{
    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    private static void AssertSameOperations(StatementParse p)
    {
        var ops = ExportFormatsGenerator.Ops;
        Assert.Equal(ops.Select(o => (o.Date, o.Amount)), p.Rows.Select(r => (r.Date, r.Amount)));
        Assert.All(ops.Zip(p.Rows), x => Assert.Contains(x.First.Label.Split(' ')[0], x.Second.Label, StringComparison.OrdinalIgnoreCase));
    }

    public static TheoryData<string, byte[]> TableFiles => new()
    {
        { "activity.xlsx", ExportFormatsGenerator.Xlsx() },
        { "activity.ods", ExportFormatsGenerator.OpenDocument(text: false) },
        { "statement.odt", ExportFormatsGenerator.OpenDocument(text: true) },
        { "statement.docx", ExportFormatsGenerator.Docx() },
        { "activity.xls", ExportFormatsGenerator.HtmlXls() },
        { "activity.html", ExportFormatsGenerator.HtmlXls() },
    };

    [Theory]
    [MemberData(nameof(TableFiles))]
    public async Task ParseAsync_TableOfOperationsInAnyOfficeOrWebFormat_ReadsTheSameOperationsBalanced(string fileName, byte[] bytes)
    {
        var draft = await _store.ParseAsync(fileName, bytes);

        AssertSameOperations(draft.Parse);
        Assert.Equal(VerificationStatus.Verified, draft.Parse.Status);
    }

    [Theory]
    [InlineData("activity.ofx", false, false, AccountKind.Bank)]
    [InlineData("activity.qfx", false, true, AccountKind.CreditCard)]
    [InlineData("activity.qbo", true, false, AccountKind.Bank)]
    public async Task ParseAsync_MoneyFileOldOrXml_ReadsTheOperationsBalancesAndKind(string fileName, bool xml, bool card, AccountKind kind)
    {
        var draft = await _store.ParseAsync(fileName, ExportFormatsGenerator.Ofx(xml, card));

        AssertSameOperations(draft.Parse);
        var opening = ExportFormatsGenerator.Opening;
        Assert.Equal(("OFX", opening, opening + ExportFormatsGenerator.Ops.Sum(o => o.Amount), kind),
            (draft.Parse.Method, draft.Parse.OpeningBalance, draft.Parse.ClosingBalance, draft.Parse.Kind));
    }

    [Fact]
    public async Task ParseAsync_QuickenFile_ReadsTheOperations()
    {
        var draft = await _store.ParseAsync("activity.qif", ExportFormatsGenerator.QifMonthFirst());

        AssertSameOperations(draft.Parse);
    }

    [Fact]
    public async Task ParseAsync_UnknownFile_ThrowsListingTheFormatsTaken()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _store.ParseAsync("notes.bin", [1, 2, 3, 0, 5, 6, 7, 8, 9, 10, 11, 12, 13]));

        Assert.Contains("Excel", error.Message);
    }

    [Fact]
    public async Task ParseAsync_CsvInWindows1252_ReadsEveryCharacterAsWrittenAndSaysSoOnceInTheLog()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string text = "Date;Libellé;Montant;Devise\n12/10/2025;CAFÉ DU COIN SAINT-ÉTIENNE;-4,50;EUR\n13/10/2025;VIR SEPA REÇU;100,00;EUR\n";
        var name = $"releve-{Guid.NewGuid():N}.csv";
        var notes = new List<string>();
        void Noted(string where, string what) { if (what.StartsWith(name, StringComparison.Ordinal)) lock (notes) notes.Add(what); }
        Diagnostics.Noted += Noted;
        ImportDraft draft;
        try
        {
            draft = await _store.ParseAsync(name, Encoding.GetEncoding(1252).GetBytes(text));
            await _store.ParseAsync("utf8-" + name, Encoding.UTF8.GetBytes(text));
        }
        finally { Diagnostics.Noted -= Noted; }

        Assert.Equal(["CAFÉ DU COIN SAINT-ÉTIENNE", "VIR SEPA REÇU"], draft.Parse.Rows.Select(r => r.Label));
        Assert.Equal([name + ": not UTF-8, read as Windows-1252."], notes);
    }

    [Fact]
    public async Task ParseAsync_PictureOfAStatement_IsReadByTextRecognitionAndBalances()
    {
        Assert.SkipUnless(PdfReader.OcrAvailable, "No text recognition on this machine (Windows: no recognition language installed; Linux: no Tesseract).");
        var spec = Sample(Style.EnglishWithdrawalsDeposits, 2025, 10, 12, 21);
        var png = await PdfReader.RenderPagePngAsync(Render(spec), 0, 1700);

        var draft = await _store.ParseAsync("statement photo.png", png);

        Assert.True(draft.Parse.UsedOcr);
        Assert.Equal(VerificationStatus.Verified, draft.Parse.Status);
        Assert.Equal(spec.Transactions.Select(t => t.Amount), draft.Parse.Rows.Select(r => r.Amount));
    }

    // ------------------------------------------------------------------------------------------------ saving

    [Fact]
    public async Task Commit_GeneratedPdfStatement_StoresEveryOperationToTheCentAndTheMonthAddsUp()
    {
        var spec = Sample(Style.EnglishWithdrawalsDeposits, 2025, 10, 30, 4);

        var result = await _store.ImportAsync("october.pdf", Render(spec));

        var october = _store.Summarize(new(2025, 10, 1), new(2025, 10, 31));
        var transfers = spec.Transactions.Where(t => t.Label.Contains("E-TRANSFER")).Sum(t => t.Amount);
        Assert.Equal(spec.Transactions.Count, result.Added);
        Assert.Equal(-spec.Transactions.Where(t => t.Amount < 0).Sum(t => t.Amount), october.Expenses);
        Assert.Equal(spec.Transactions.Where(t => t.Amount > 0).Sum(t => t.Amount) - transfers, october.Earnings);
        Assert.Equal(october.Earnings - october.Expenses, october.Balance);
    }

    [Fact]
    public async Task ParseAsync_BeforeTheAccountIsChosen_SaysNoAccountAndNoKind()
    {
        var draft = await _store.ParseAsync("chequing.csv", BncGenerator.Csv(BncGenerator.Sample(2025, 9, 31), BncGenerator.BncAccount.Chequing));

        Assert.Equal((null, null), (draft.AccountId, draft.Kind));
    }

    [Fact]
    public async Task Commit_NoAccountAndNoKindChosen_ThrowsAndSavesNothing()
    {
        var draft = await _store.ParseAsync("chequing.csv", BncGenerator.Csv(BncGenerator.Sample(2025, 9, 31), BncGenerator.BncAccount.Chequing));

        Assert.Throws<ArgumentException>(() => _store.Imports.Commit(new CommitRequest(draft.Id, null, null, "CAD", null, [])));
        Assert.Empty(_store.Db.Accounts());
    }

    [Fact]
    public async Task Commit_NewAccountWithoutABank_ThrowsAndSavesNothing()
    {
        var draft = await _store.ParseAsync("a.pdf", Render(Sample(Style.FrenchDebitCredit, 2025, 10, 20, 9)));

        Assert.Throws<ArgumentException>(() => _store.Imports.Commit(new CommitRequest(draft.Id, null, null, "CAD", AccountKind.Bank, [])));
        Assert.Empty(_store.Db.Accounts());
        Assert.Empty(_store.Db.AllTransactions());
    }

    [Theory]
    [InlineData(DateFormat.DayMonthYear, 4, 3)]
    [InlineData(DateFormat.MonthDayYear, 3, 4)]
    public async Task Choose_BankWithADateFormat_ReadsTheDatesOfItsFileInThatFormat(DateFormat format, int month, int day)
    {
        var bank = _store.AddBank("Exemple", dateFormat: format);
        var draft = await _store.ParseAsync("export.csv", Encoding.UTF8.GetBytes("Date,Description,Amount\n03/04/2026,Bakery,-4.50\n"));

        _store.Imports.Choose(draft.Id, null, AccountKind.Bank, bank);

        Assert.Equal(new DateOnly(2026, month, day), draft.Rows[0].Date);
    }

    [Fact]
    public async Task Choose_DateFormatOtherThanTheBanks_ReadsTheFileInTheChosenFormat()
    {
        var bank = _store.AddBank("Exemple", dateFormat: DateFormat.DayMonthYear);
        var draft = await _store.ParseAsync("export.csv", Encoding.UTF8.GetBytes("Date,Description,Amount\n03/04/2026,Bakery,-4.50\n"), bank);

        _store.Imports.Choose(draft.Id, null, AccountKind.Bank, bank, DateFormat.MonthDayYear);

        Assert.Equal(new DateOnly(2026, 3, 4), draft.Rows[0].Date);
    }

    [Fact]
    public async Task Commit_DatesThatDoNotFitTheDateFormat_ThrowsAndSavesNothing()
    {
        var bank = _store.AddBank("Exemple", dateFormat: DateFormat.MonthDayYear);
        var draft = await _store.ParseAsync("export.csv", Encoding.UTF8.GetBytes("Date,Description,Amount\n31/03/2026,Bakery,-4.50\n"), bank);

        var error = Assert.Throws<InvalidOperationException>(() => _store.Imports.Commit(new CommitRequest(draft.Id, null, null, "CAD", AccountKind.Bank, [], bank)));

        Assert.Contains("written DD/MM/YYYY, not MM/DD/YYYY", error.Message);
        Assert.Empty(_store.Db.AllTransactions());
    }

    [Fact]
    public async Task Commit_WithAnotherDateFormatChosen_MakesItTheBanksFormat()
    {
        var bank = _store.AddBank("Exemple", dateFormat: DateFormat.DayMonthYear);
        var draft = await _store.ParseAsync("export.csv", Encoding.UTF8.GetBytes("Date,Description,Amount\n03/14/2026,Bakery,-4.50\n"), bank);
        _store.Imports.Choose(draft.Id, null, AccountKind.Bank, bank, DateFormat.MonthDayYear);

        _store.Imports.Commit(new CommitRequest(draft.Id, null, null, "CAD", AccountKind.Bank, [], bank));

        Assert.Equal(DateFormat.MonthDayYear, _store.Db.Banks().Single(b => b.Id == bank).DateFormat);
    }

    [Fact]
    public async Task Choose_AccountAtAFrenchBank_ReadsAmbiguousDatesDayFirst()
    {
        var bank = _store.AddBank("Exemple", BankingSystem.France, "EUR");
        var account = _store.AddAccount("Compte courant", "EUR", bank: bank);
        var draft = await _store.ParseAsync("export.csv", Encoding.UTF8.GetBytes("Date,Description,Amount\n03/04/2026,Bakery,-4.50\n05/04/2026,Salary,2000.00\n"));

        _store.Imports.Choose(draft.Id, account, null);

        Assert.Equal(new DateOnly(2026, 4, 3), draft.Rows[0].Date);
    }

    private long FrenchBank() => _store.AddBank("Banque Exemple", BankingSystem.France, "EUR", "France");

    [Fact]
    public void Banks_NewDatabase_IsTheNationalBankOfCanadaInCanadianDollars()
    {
        var bank = Assert.Single(_store.Db.Banks());

        Assert.Equal(("National Bank of Canada", "CAD"), (bank.Name, bank.Currency));
    }

    [Fact]
    public async Task Commit_NewAccountAtANewBank_AddsTheBankAndPutsTheAccountAndItsOperationsInItsCurrency()
    {
        var draft = await _store.ParseAsync("a.pdf", Render(Sample(Style.FrenchDebitCredit, 2025, 10, 20, 9)));

        var result = _store.Imports.Commit(new CommitRequest(draft.Id, null, null, "EUR", AccountKind.Bank, [], NewBankName: "Fabrikam Bank"));

        var account = _store.Db.Accounts().Single(a => a.Id == result.AccountId);
        Assert.Equal((_store.Db.Banks().Single(b => b.Name == "Fabrikam Bank").Id, "EUR"), (account.BankId, account.Currency));
        Assert.All(_store.Db.AllTransactions(), t => Assert.Equal("EUR", t.Currency));
    }

    [Fact]
    public async Task UpdateAccount_OtherCurrency_ReachesEveryOperationOfTheAccount()
    {
        var result = await _store.ImportAsync("a.pdf", Render(Sample(Style.FrenchDebitCredit, 2025, 10, 20, 9)), "EUR", bank: FrenchBank());
        var account = _store.Db.Accounts().Single(a => a.Id == result.AccountId);

        account.Currency = "CHF";
        _store.Db.UpdateAccount(account);

        Assert.All(_store.Db.AllTransactions(), t => Assert.Equal("CHF", t.Currency));
    }

    [Fact]
    public async Task UpdateBank_FrenchBankingSystem_ReachesItsAccounts()
    {
        var bank = _store.AddBank("Fabrikam Bank", currency: "EUR");
        var result = await _store.ImportAsync("a.pdf", Render(Sample(Style.FrenchDebitCredit, 2025, 10, 20, 9)), "EUR", bank: bank);
        var saved = _store.Db.Banks().Single(b => b.Id == bank);

        saved.System = BankingSystem.France;
        _store.Db.UpdateBank(saved);

        Assert.Equal(BankingSystem.France, _store.Db.Accounts().Single(a => a.Id == result.AccountId).System);
    }

    [Fact]
    public async Task ParseAsync_FileAlreadyImported_SaysSoAndCommitThrows()
    {
        var spec = Sample(Style.FrenchDebitCredit, 2025, 10, 20, 9);
        var bank = FrenchBank();
        var first = await _store.ImportAsync("a.pdf", Render(spec), "EUR", bank: bank);

        var again = await _store.ParseAsync("a.pdf", Render(spec), bank);

        Assert.True(again.AlreadyImported);
        Assert.Throws<InvalidOperationException>(() => _store.Imports.Commit(new CommitRequest(again.Id, first.AccountId, null, "EUR", null, [])));
    }
}
