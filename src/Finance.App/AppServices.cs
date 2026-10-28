using System.Text.Json;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Host;

/// <summary>The engine objects, created once per run over the local database.</summary>
internal sealed class AppServices : IDisposable
{
    public Database Db { get; }
    public SummaryService Summaries { get; }
    public TransactionService Transactions { get; }
    public ImportService Imports { get; }
    public string DataDir { get; }
    public string LogPath { get; }

    public static readonly Dictionary<string, string> DefaultSettings = new()
    {
        ["currency"] = "CAD",
        ["dateFormat"] = Formatting.DateFormats[0],
        ["theme"] = "light",
        ["reportsFolder"] = "",
        ["bankingSystem"] = "northAmerica",
        ["bankFocus"] = "auto",
        // Month view: transfers, card payments and investments in their own folded table ("separate"), left out
        // ("hidden"), or among the other operations ("inline").
        ["periodTransfers"] = "separate",
        ["calendarHideCardPayments"] = "true",
        // Calendar: every transfer, card payment and investment left out (only spending and income shown).
        ["calendarHideTransfers"] = "true",
        // Pies: every category and brand ("true"), or the five largest and "Other" ("false").
        ["pieShowAll"] = "true",
    };

    /// <summary>Raised whenever classification rules change, so existing operations are classified again at start.</summary>
    private const string RulesVersion = "4";

    /// <summary>Banks whose statements and exports the reader has been proven on.</summary>
    public static readonly (string Id, string Name, bool Proven)[] BankProfiles = [("auto", "Any bank", false), ("bnc", "Banque Nationale (BNC)", true)];

    public BankingSystem System => Setting("bankingSystem") == "france" ? BankingSystem.France : BankingSystem.NorthAmerica;

    public AppServices(string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        LogPath = Path.Combine(dataDir, "app.log");
        Db = new Database(Path.Combine(dataDir, "finances.db"));
        Summaries = new SummaryService(Db, () => System);
        Transactions = new TransactionService(Db, () => Setting("currency"), () => System);
        Imports = new ImportService(Db, () => Setting("currency"), Transactions) { BankFocus = () => Setting("bankFocus") };
        if (Db.GetSetting("rulesVersion") != RulesVersion)
        {
            Transactions.ReapplyRules();
            Db.SetSetting("rulesVersion", RulesVersion);
        }
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
            case "dateFormat" when !Formatting.DateFormats.Contains(value):
                throw new ArgumentException("Unknown date format.");
            case "theme" when value is not ("light" or "dark"):
                throw new ArgumentException("Unknown theme.");
            case "reportsFolder" when value.Length > 0 && !Directory.Exists(value):
                throw new DirectoryNotFoundException("This folder does not exist.");
            case "bankingSystem" when value is not ("northAmerica" or "france"):
                throw new ArgumentException("Unknown banking system.");
            case "bankFocus" when !BankProfiles.Any(p => p.Id == value):
                throw new ArgumentException("Unknown bank.");
            case "periodTransfers" when value is not ("separate" or "hidden" or "inline"):
                throw new ArgumentException("Unknown choice.");
            case "calendarHideCardPayments" or "calendarHideTransfers" or "pieShowAll" when value is not ("true" or "false"):
                throw new ArgumentException("Unknown choice.");
            case "currency" or "dateFormat" or "theme" or "reportsFolder" or "window" or "bankingSystem" or "bankFocus" or "periodTransfers"
                or "calendarHideCardPayments" or "calendarHideTransfers" or "pieShowAll":
                Db.SetSetting(key, value);
                break;
            default:
                throw new ArgumentException($"Unknown setting '{key}'.");
        }
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
