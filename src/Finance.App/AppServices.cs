using System.Text.Json;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Host;

/// <summary>The engine objects, created once per run over the local database.</summary>
internal sealed class AppServices : IDisposable
{
    public Database Db { get; private set; } = null!;
    public SummaryService Summaries { get; private set; } = null!;
    public TransactionService Transactions { get; private set; } = null!;
    public ImportService Imports { get; private set; } = null!;
    public CategoryService Categories { get; private set; } = null!;
    public TagService Tags { get; private set; } = null!;
    public ExchangeRates Rates { get; private set; } = null!;
    public Holidays Holidays { get; private set; } = null!;
    public string DataDir { get; }
    public string LogPath { get; }

    private const string DbFile = "finances.db";

    /// <summary>Holds the folder the person moved the database to (the database cannot say where it is itself).</summary>
    private string LocationFile => Path.Combine(DataDir, "database-location.txt");

    /// <summary>Folder of the database: the data folder, or the one the person chose.</summary>
    public string DbFolder => File.Exists(LocationFile) && File.ReadAllText(LocationFile).Trim() is { Length: > 0 } f && Directory.Exists(f) ? f : DataDir;

    public static readonly Dictionary<string, string> DefaultSettings = new()
    {
        // Global display currency: every bank together is converted to it and shown in it.
        ["currency"] = "CAD",
        // Source of the daily exchange rates converting the other currencies (one of Definitions > Exchange rates).
        ["ratesSource"] = "boc",
        // Weekend days, not business days: ISO numbers, Monday 1 to Sunday 7 (Definitions > Holidays).
        ["weekendDays"] = "6,7",
        // The calendar's first day of the week: 0 Sunday (North America), 1 Monday (Europe), 6 Saturday (Middle East).
        ["calendarFirstDay"] = "0",
        ["dateFormat"] = Formatting.DateFormats[0],
        ["theme"] = "light",
        ["reportsFolder"] = "",
        // Transfers, card payments and investments: in their own folded table ("separate"), their own open table
        // ("table"), left out ("hidden"), or among the other operations ("inline").
        ["periodTransfers"] = "separate",
        // Calendar: what is left out.
        ["calendarHideTransfers"] = "true",
        ["calendarHideCardPayments"] = "true",
        ["calendarHideInvestments"] = "false",
        // Pies: every category and brand ("true"), or the five largest and "Other" ("false").
        ["pieShowAll"] = "false",
        // Charts by first-level category, nested categories counted in theirs ("true"), or by every category ("false").
        ["chartsTopLevel"] = "true",
        // Names of the three kinds of operation the categories sit under (renamed in Categories).
        ["section.expense"] = "Expenses",
        ["section.income"] = "Income",
        ["section.transfer"] = "Transfers",
    };

    /// <summary>Raised whenever classification rules change, so existing operations are classified again at start.</summary>
    private const string RulesVersion = "6";

    public AppServices(string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        LogPath = Path.Combine(dataDir, "app.log");
        Open();
    }

    private void Open()
    {
        Db = new Database(Path.Combine(DbFolder, DbFile));
        Summaries = new SummaryService(Db);
        Transactions = new TransactionService(Db, () => Setting("currency"));
        Imports = new ImportService(Db, () => Setting("currency"), Transactions);
        Categories = new CategoryService(Db);
        Tags = new TagService(Db);
        Rates = new ExchangeRates(Db, () => Setting("ratesSource"));
        // Movements between accounts in two currencies are paired with the rates of the bank in use.
        var rates = Rates;
        Transactions.Convert = (amount, from, to, day) => rates.Convert(amount, from, to, day)?.Amount;
        Holidays = new Holidays(Db, () => Setting("weekendDays"));
        if (Db.GetSetting("rulesVersion") != RulesVersion)
        {
            Transactions.ReapplyRules();
            Db.SetSetting("rulesVersion", RulesVersion);
        }
    }

    /// <summary>Moves the database to another folder (any folder) and goes on with it there.</summary>
    public void MoveDatabase(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("This folder does not exist.");
        if (string.Equals(folder, Path.TrimEndingDirectorySeparator(DbFolder), StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(Path.Combine(folder, DbFile))) throw new IOException("This folder already holds a finances database.");
        var from = Path.Combine(DbFolder, DbFile);
        var downloads = Db.DownloadsFolder;
        Db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            File.Copy(from, Path.Combine(folder, DbFile));
            if (string.Equals(folder, Path.TrimEndingDirectorySeparator(DataDir), StringComparison.OrdinalIgnoreCase)) File.Delete(LocationFile);
            else File.WriteAllText(LocationFile, folder);
            File.Delete(from);
            foreach (var extra in new[] { "-wal", "-shm" }) File.Delete(from + extra);
            // The downloaded rates and holidays go with it.
            new DownloadStore(downloads).MoveTo(Path.Combine(folder, "downloads"));
        }
        finally
        {
            Open();
        }
    }

    /// <summary>
    /// New rates (or another bank's) can pair halves of movements between accounts in two currencies that could not be
    /// paired before: the movements are linked again when accounts are in more than one currency.
    /// </summary>
    public void RatesChanged()
    {
        if (Db.Accounts().Select(a => a.Currency).Distinct().Skip(1).Any()) Transactions.ReapplyRules();
    }

    public string Setting(string key) => Db.GetSetting(key) ?? DefaultSettings.GetValueOrDefault(key) ?? "";

    public Dictionary<string, string> Settings()
    {
        var all = new Dictionary<string, string>(DefaultSettings);
        foreach (var (k, v) in Db.AllSettings()) all[k] = v;
        return all;
    }

    public void SetSetting(string key, string value)
    {
        switch (key)
        {
            case "currency" when value.Length != 3 || !value.All(char.IsAsciiLetterUpper):
                throw new ArgumentException("A currency is a three-letter code such as CAD.");
            case "ratesSource" when Rates.Find(value) is null:
                throw new ArgumentException("Unknown source of exchange rates.");
            case "dateFormat" when !Formatting.DateFormats.Contains(value):
                throw new ArgumentException("Unknown date format.");
            case "theme" when value is not ("light" or "dark"):
                throw new ArgumentException("Unknown theme.");
            case "reportsFolder" when value.Length > 0 && !Directory.Exists(value):
                throw new DirectoryNotFoundException("This folder does not exist.");
            case "periodTransfers" when value is not ("separate" or "table" or "hidden" or "inline"):
                throw new ArgumentException("Unknown choice.");
            case "calendarHideCardPayments" or "calendarHideTransfers" or "calendarHideInvestments" or "pieShowAll" or "chartsTopLevel" when value is not ("true" or "false"):
                throw new ArgumentException("Unknown choice.");
            case "section.expense" or "section.income" or "section.transfer" when value.Trim().Length is 0 or > 40:
                throw new ArgumentException("A name has 1 to 40 characters.");
            // Weekend days: ISO numbers of the week's days (Monday 1 to Sunday 7), comma separated; empty for none.
            case "calendarFirstDay" when value is not ("0" or "1" or "6"):
                throw new ArgumentException("The week starts on Sunday, Monday or Saturday.");
            case "weekendDays" when !System.Text.RegularExpressions.Regex.IsMatch(value, "^([1-7](,[1-7]){0,6})?$"):
                throw new ArgumentException("Weekend days are numbers from 1 (Monday) to 7 (Sunday).");
            // The order the person gave the rows of a table: a list of row keys.
            case not null when key.StartsWith("order.", StringComparison.Ordinal) && (key.Length > 60 || value.Length > 200_000 || !IsStringList(value)):
                throw new ArgumentException("Invalid row order.");
            case "currency" or "ratesSource" or "dateFormat" or "theme" or "reportsFolder" or "window" or "periodTransfers"
                or "calendarHideCardPayments" or "calendarHideTransfers" or "calendarHideInvestments" or "pieShowAll" or "chartsTopLevel"
                or "section.expense" or "section.income" or "section.transfer" or "weekendDays" or "calendarFirstDay":
            case not null when key.StartsWith("order.", StringComparison.Ordinal):
                Db.SetSetting(key, value);
                break;
            default:
                throw new ArgumentException($"Unknown setting '{key}'.");
        }
    }

    private static bool IsStringList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) is not null; }
        catch (JsonException) { return false; }
    }

    public void Log(Exception ex, string where)
    {
        try
        {
            // Only the error itself: no amounts, labels or file contents ever go to the log.
            File.AppendAllText(LogPath, $"{DateTime.Now:O} [{where}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // The log is a convenience; a locked or full disk must not take the app down.
        }
    }

    public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);

    public WindowPlacement? Placement()
    {
        var raw = Db.GetSetting("window");
        return raw is null ? null : JsonSerializer.Deserialize<WindowPlacement>(raw);
    }

    public void SavePlacement(WindowPlacement p) => Db.SetSetting("window", JsonSerializer.Serialize(p));

    public void Dispose() => Db.Dispose();
}
