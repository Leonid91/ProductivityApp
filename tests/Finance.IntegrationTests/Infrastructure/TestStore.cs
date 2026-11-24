using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.IntegrationTests.Infrastructure;

/// <summary>
/// A real database file and the engine's services over it, in a folder of its own under the temp folder, deleted when
/// the test ends. Every test makes its own: no test sees another's data.
/// </summary>
public sealed class TestStore : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "finances-tests-" + Guid.NewGuid().ToString("N"));
    public string DatabasePath => Path.Combine(Folder, "test.db");
    public Database Db { get; }
    public TransactionService Transactions { get; }
    public ImportService Imports { get; }
    public SummaryService Summaries { get; }

    /// <summary>The bank every new database starts with (National Bank of Canada, in Canadian dollars).</summary>
    public long DefaultBank => Db.Banks()[0].Id;

    public TestStore()
    {
        Db = new Database(DatabasePath);
        Transactions = new TransactionService(Db);
        Imports = new ImportService(Db, () => "CAD", Transactions);
        Summaries = new SummaryService(Db);
    }

    /// <summary>A bank of the person's list; its date format the one of its country and banking system unless <paramref name="dateFormat"/> is given.</summary>
    public long AddBank(string name, BankingSystem system = BankingSystem.NorthAmerica, string currency = "CAD", string country = "", DateFormat? dateFormat = null) =>
        Db.AddBank(new Bank { Name = name, Country = country, System = system, Currency = currency, DateFormat = dateFormat });

    /// <summary>An account at the bank every database starts with.</summary>
    public long AddAccount(string name = "Chequing", string currency = "CAD", AccountKind kind = AccountKind.Bank) => AddAccountAt(DefaultBank, name, currency, kind);

    /// <summary>An account at <paramref name="bank"/>.</summary>
    public long AddAccountAt(long bank, string name = "Chequing", string currency = "CAD", AccountKind kind = AccountKind.Bank) =>
        Db.AddAccount(new Account { Name = name, Currency = currency, Kind = kind, BankId = bank });

    /// <summary>An operation entered by hand, as the Statements view does.</summary>
    public TransactionView Enter(long account, string day, decimal amount, string brand, string category, string? note = null) =>
        Transactions.Create(new TransactionInput(day, amount, brand, category, account, note));

    /// <summary>Reads a file as the import does once the person chose the bank every database starts with. Stopped when the test is.</summary>
    public Task<ImportDraft> ParseAsync(string fileName, byte[] bytes) => ParseAsync(fileName, bytes, DefaultBank);

    /// <summary>Reads a file as the import does once the person chose <paramref name="bank"/>: with its banking system and date format.</summary>
    public async Task<ImportDraft> ParseAsync(string fileName, byte[] bytes, long bank)
    {
        var draft = await Imports.ParseAsync(fileName, bytes, TestContext.Current.CancellationToken);
        return Imports.Choose(draft.Id, null, null, bank);
    }

    /// <summary>Reads a file and saves it into a new account at the bank every database starts with.</summary>
    public Task<CommitResult> ImportAsync(string fileName, byte[] bytes, string currency = "CAD", AccountKind kind = AccountKind.Bank, string? accountName = null) =>
        ImportAtAsync(DefaultBank, fileName, bytes, currency, kind, accountName);

    /// <summary>Reads a file and saves it into a new account at <paramref name="bank"/>, as the person does.</summary>
    public async Task<CommitResult> ImportAtAsync(long bank, string fileName, byte[] bytes, string currency = "CAD", AccountKind kind = AccountKind.Bank, string? accountName = null)
    {
        var draft = await Imports.ParseAsync(fileName, bytes, TestContext.Current.CancellationToken);
        Imports.Choose(draft.Id, null, kind, bank);
        return Imports.Commit(new CommitRequest(draft.Id, null, accountName, currency, kind, [], BankId: bank));
    }

    /// <summary>Reads a file and saves it into the account <paramref name="account"/>, as the person does.</summary>
    public async Task<CommitResult> ImportIntoAsync(long account, string fileName, byte[] bytes, string currency = "CAD")
    {
        var draft = await Imports.ParseAsync(fileName, bytes, TestContext.Current.CancellationToken);
        Imports.Choose(draft.Id, account, null, null);
        return Imports.Commit(new CommitRequest(draft.Id, account, null, currency, null, [], BankId: null));
    }

    /// <summary>Every statement a file gives (the first draft, then the other accounts it holds), each read at the bank every database starts with.</summary>
    public Task<List<ImportDraft>> ParseAllAsync(string fileName, byte[] bytes) => ParseAllAsync(fileName, bytes, DefaultBank);

    /// <summary>Every statement a file gives (the first draft, then the other accounts it holds), each read once <paramref name="bank"/> is chosen.</summary>
    public async Task<List<ImportDraft>> ParseAllAsync(string fileName, byte[] bytes, long bank)
    {
        var first = await Imports.ParseAsync(fileName, bytes, TestContext.Current.CancellationToken);
        return [.. new[] { first }.Concat(first.Others).Select(d => Imports.Choose(d.Id, null, null, bank))];
    }

    /// <summary>Saves every draft of a file into a new account each, at the bank every database starts with, in the currency each statement says.</summary>
    public List<CommitResult> CommitAll(IEnumerable<ImportDraft> drafts) => CommitAll(drafts, DefaultBank);

    /// <summary>Saves every draft of a file into a new account each, at <paramref name="bank"/>, in the currency each statement says.</summary>
    public List<CommitResult> CommitAll(IEnumerable<ImportDraft> drafts, long bank) => CommitEach(drafts, d => d.Parse.Currency, bank);

    /// <summary>Saves every draft of a file into a new account each, at <paramref name="bank"/>, in <paramref name="currency"/>.</summary>
    public List<CommitResult> CommitAll(IEnumerable<ImportDraft> drafts, string currency, long bank) => CommitEach(drafts, _ => currency, bank);

    /// <summary>Each draft into a new account of the kind its statement says, at <paramref name="bank"/>.</summary>
    private List<CommitResult> CommitEach(IEnumerable<ImportDraft> drafts, Func<ImportDraft, string> currency, long bank) =>
        [.. drafts.Select(d =>
        {
            Imports.Choose(d.Id, null, d.Parse.Kind, bank);
            return Imports.Commit(new CommitRequest(d.Id, null, null, currency(d), d.Parse.Kind, [], BankId: bank));
        })];

    /// <summary>
    /// Operations in other currencies converted with <paramref name="rates"/>, as the app wires it (AppServices): no
    /// amount where the rates know no rate for the day. The one null-propagation of the tests, kept until the engine
    /// offers this conversion itself.
    /// </summary>
    public void ConvertWith(ExchangeRates rates) => Transactions.Convert = (amount, from, to, day) => rates.Convert(amount, from, to, day)?.Amount;

    public PeriodSummary Summarize(DateOnly from, DateOnly to, Granularity granularity = Granularity.Month, string currency = "CAD") =>
        Summaries.Summarize(from, to, granularity, currency);

    public void Dispose()
    {
        Db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Folder, true); }
        catch (IOException) { }
    }
}
