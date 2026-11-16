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
    public long AddBank(string name, BankingSystem system = BankingSystem.NorthAmerica, string currency = "CAD", string? country = null, DateFormat? dateFormat = null) =>
        Db.AddBank(new Bank { Name = name, Country = country ?? "", System = system, Currency = currency, DateFormat = dateFormat });

    public long AddAccount(string name = "Chequing", string currency = "CAD", AccountKind kind = AccountKind.Bank, long? bank = null) =>
        Db.AddAccount(new Account { Name = name, Currency = currency, Kind = kind, BankId = bank ?? DefaultBank });

    /// <summary>An operation entered by hand, as the Statements view does.</summary>
    public TransactionView Enter(long account, string day, decimal amount, string brand, string category, string? note = null) =>
        Transactions.Create(new TransactionInput(day, amount, brand, category, account, note));

    /// <summary>
    /// Reads a file as the import does once the person chose its bank (<paramref name="bank"/>, else the bank every
    /// database starts with): with that bank's banking system and date format. Stopped when the test is.
    /// </summary>
    public async Task<ImportDraft> ParseAsync(string fileName, byte[] bytes, long? bank = null)
    {
        var draft = await Imports.ParseAsync(fileName, bytes, TestContext.Current.CancellationToken);
        return Imports.Choose(draft.Id, null, null, bank ?? DefaultBank);
    }

    /// <summary>Reads a file and saves it into an account, as the person does: into <paramref name="account"/>, or a new one at <paramref name="bank"/>.</summary>
    public async Task<CommitResult> ImportAsync(string fileName, byte[] bytes, string currency = "CAD", AccountKind kind = AccountKind.Bank,
        long? account = null, long? bank = null, string? accountName = null)
    {
        var draft = await Imports.ParseAsync(fileName, bytes, TestContext.Current.CancellationToken);
        Imports.Choose(draft.Id, account, account is null ? kind : null, account is null ? bank ?? DefaultBank : null);
        return Imports.Commit(new CommitRequest(draft.Id, account, accountName, currency, account is null ? kind : null, [], BankId: account is null ? bank ?? DefaultBank : null));
    }

    /// <summary>Every statement a file gives (the first draft, then the other accounts it holds), each read once its bank is chosen.</summary>
    public async Task<List<ImportDraft>> ParseAllAsync(string fileName, byte[] bytes, long? bank = null)
    {
        var first = await Imports.ParseAsync(fileName, bytes, TestContext.Current.CancellationToken);
        return [.. new[] { first }.Concat(first.Others).Select(d => Imports.Choose(d.Id, null, null, bank ?? DefaultBank))];
    }

    /// <summary>
    /// Saves every draft of a file into a new account each, at <paramref name="bank"/>, with the kind each statement says
    /// and in <paramref name="currency"/> (the currency each statement says when none is given).
    /// </summary>
    public List<CommitResult> CommitAll(IEnumerable<ImportDraft> drafts, string? currency = null, long? bank = null) =>
        [.. drafts.Select(d =>
        {
            Imports.Choose(d.Id, null, d.Parse.Kind, bank ?? DefaultBank);
            return Imports.Commit(new CommitRequest(d.Id, null, null, currency ?? d.Parse.Currency, d.Parse.Kind, [], BankId: bank ?? DefaultBank));
        })];

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
