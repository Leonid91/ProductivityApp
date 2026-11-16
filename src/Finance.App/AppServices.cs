using System.Text.Json;
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
    /// <summary>The log of every action (Logs page; Settings > Data > Log location).</summary>
    public ActionLog Logs { get; }
    public string LogPath => Logs.FilePath;

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
        // Pies: every slice ("true"), or the five largest and "Other" ("false"): the category pies (and the recurring,
        // income and tag pies), and the brand pie on its own.
        ["pieShowAllCategories"] = "true",
        ["pieShowAllBrands"] = "false",
        // The hours worked in a week (the net salary per hour, Analytics).
        ["hoursPerWeek"] = "40",
        // The two currencies of the calendar's exchange rates ("CAD>EUR": what one CAD is worth in EUR; "CAD<EUR": one EUR in CAD).
        ["calendarRates"] = "",
        // Charts by first-level category, nested categories counted in theirs ("true"), or by every category ("false").
        ["chartsTopLevel"] = "true",
        // Names of the three kinds of operation the categories sit under (renamed in Categories).
        ["section.expense"] = "Expenses",
        ["section.income"] = "Income",
        ["section.transfer"] = "Transfers",
    };

    /// <summary>Raised whenever classification rules change, so existing operations are classified again at start.</summary>
    private const string RulesVersion = "6";

    public AppServices(string dataDir, string started = "")
    {
        DataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        Logs = new ActionLog(dataDir);
        // Exceptions the engine catches and handles: written in full too.
        Diagnostics.Caught += (where, e) => Logs.Write("warning", "app", "Caught exception", DateTime.Now, "where: " + where + "\nexception: " + e);
        Logs.Info("app", "Start", started);
        Open();
    }

    private void Open()
    {
        Db = new Database(Path.Combine(DbFolder, DbFile));
        Logs.Info("app", "Database.Open", $"path: {Path.Combine(DbFolder, DbFile)}\nsize: {new FileInfo(Path.Combine(DbFolder, DbFile)).Length} bytes");
        Summaries = new SummaryService(Db);
        Transactions = new TransactionService(Db);
        Imports = new ImportService(Db, () => Setting("currency"), Transactions);
        Categories = new CategoryService(Db);
        Tags = new TagService(Db);
        Rates = new ExchangeRates(Db, () => Setting("ratesSource"));
        // Movements between accounts in two currencies are paired with the rates of the bank in use.
        var rates = Rates;
        Transactions.Convert = (amount, from, to, day) => rates.Convert(amount, from, to, day)?.Amount;
        Holidays = new Holidays(Db, () => Setting("weekendDays"), () => Setting("hoursPerWeek"));
        if (Db.GetSetting("rulesVersion") != RulesVersion)
        {
            Transactions.ReapplyRules();
            Db.SetSetting("rulesVersion", RulesVersion);
            Logs.Info("app", "Transactions.ReapplyRules", "rulesVersion: " + RulesVersion);
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

    /// <summary>
    /// Makes the latest months as the window shows them first (kept by the report service): done while the window and
    /// its browser start, so the first periods show as soon as the page asks for them.
    /// </summary>
    public void Warm()
    {
        try
        {
            foreach (var m in Summaries.Index(null).Months.Take(3))
            {
                var from = new DateOnly(m.Year, m.Month, 1);
                Summaries.Report(from, from.AddMonths(1).AddDays(-1), Granularity.Month, Setting("currency"), Rates, Holidays);
            }
            Logs.Info("app", "Warm", "months: " + string.Join(", ", Summaries.Index(null).Months.Take(3).Select(m => $"{m.Year}-{m.Month:00}")));
        }
        catch (Exception ex) { Log(ex, "warm"); }
    }

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
            case "calendarHideCardPayments" or "calendarHideTransfers" or "calendarHideInvestments" or "pieShowAllCategories" or "pieShowAllBrands" or "chartsTopLevel" when value is not ("true" or "false"):
                throw new ArgumentException("Unknown choice.");
            case "hoursPerWeek" when !int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var hours) || hours is < 1 or > 168:
                throw new ArgumentException("The hours of a week are a whole number from 1 to 168.");
            case "calendarRates" when value.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Z]{3}[<>][A-Z]{3}$"):
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
                or "calendarHideCardPayments" or "calendarHideTransfers" or "calendarHideInvestments" or "pieShowAllCategories" or "pieShowAllBrands" or "chartsTopLevel"
                or "hoursPerWeek" or "calendarRates"
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
        catch (JsonException e) { Diagnostics.Report("AppServices.IsStringList", e); return false; }
    }

    /// <summary>An error, with its call stack, as an entry of the log (written on the log's own thread, never failing).</summary>
    public void Log(Exception ex, string where) =>
        Logs.Error("app", "Exception (" + where + ")", ex.ToString());

    public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);

    public WindowPlacement? Placement()
    {
        var raw = Db.GetSetting("window");
        return raw is null ? null : JsonSerializer.Deserialize<WindowPlacement>(raw);
    }

    public void SavePlacement(WindowPlacement p) => Db.SetSetting("window", JsonSerializer.Serialize(p));

    public void Dispose()
    {
        Db.Dispose();
        Logs.Info("app", "Exit");
        Logs.Dispose();
    }
}
