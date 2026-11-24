using System.Collections.Concurrent;
using System.Text.Json;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Movements between accounts for the money-moved diagram: the layouts seen in real use (one bank in a month and in a
/// year, several banks, four banks with Wise between them) and 150 invented ones (one bank to four, one to three
/// accounts each, two to ten movements, some with the outside, never two movements between the same two places the same
/// way), kept as data (InventedLayouts.json) under the seed that made them.
/// </summary>
public static class MovementLayouts
{
    private static long _ids;
    private static readonly ConcurrentDictionary<string, long> Ids = new(StringComparer.Ordinal);

    /// <summary>Each account its own id, by its bank and name.</summary>
    private static long Id(string bank, string account) => Ids.GetOrAdd(bank + "/" + account, _ => Interlocked.Increment(ref _ids));

    /// <summary>A movement between two accounts.</summary>
    public static MovementView Between(string from, string fromBank, string to, string toBank, decimal amount, string fromType = "Chequing account", string toType = "Chequing account") =>
        new("2026-03-01", "transfers", "x", amount, from, to, false, false, 1, null, fromType, toType, Id(fromBank, from), Id(toBank, to), fromBank, toBank);

    /// <summary>A movement from an account to the outside (<paramref name="outside"/>: its name).</summary>
    public static MovementView ToOutside(string from, string fromBank, string outside, decimal amount, string fromType = "Chequing account") =>
        new("2026-03-01", "transfers", "x", amount, from, outside, false, true, 1, null, fromType, null, Id(fromBank, from), null, fromBank, null);

    /// <summary>A movement from the outside (<paramref name="outside"/>: its name) to an account.</summary>
    public static MovementView FromOutside(string outside, string to, string toBank, decimal amount, string toType = "Chequing account") =>
        new("2026-03-01", "transfers", "x", amount, outside, to, true, false, 1, null, null, toType, null, Id(toBank, to), null, toBank);

    private const string Nbc = "National Bank of Canada";
    private const string Out = "Outside (not imported)";

    /// <summary>The names of the layouts seen in real use.</summary>
    public static readonly string[] RealNames = ["month", "year", "august", "october", "wise"];

    /// <summary>A layout seen in real use, and how many crossings it may have (none, but where the banks force one).</summary>
    public static (List<MovementView> Movements, int AllowedCrossings) Real(string name) => RealLayouts[name];

    private static readonly Dictionary<string, (List<MovementView> Movements, int AllowedCrossings)> RealLayouts = new()
    {
        // One bank in a month: a card paid, money out and a little in.
        ["month"] = ([Between("NBC Debit", Nbc, "NBC Credit", Nbc, 1587.47m, toType: "Credit card"), ToOutside("NBC Debit", Nbc, Out, 1871.18m),
            FromOutside(Out, "NBC Debit", Nbc, 2.30m)], 0),
        // The same bank over a year: both ways between chequing and savings, the card from both, the outside.
        ["year"] = ([
            Between("NBC Debit", Nbc, "NBC High Interest Account", Nbc, 18832.40m, toType: "Savings account"), Between("NBC High Interest Account", Nbc, "NBC Debit", Nbc, 55378.61m, fromType: "Savings account"),
            Between("NBC Debit", Nbc, "NBC Credit", Nbc, 16932.56m, toType: "Credit card"), Between("NBC High Interest Account", Nbc, "NBC Credit", Nbc, 957.34m, fromType: "Savings account", toType: "Credit card"),
            ToOutside("NBC Credit", Nbc, Out, 3.07m, fromType: "Credit card"), ToOutside("NBC Debit", Nbc, Out, 56254.30m),
            FromOutside(Out, "NBC Debit", Nbc, 702.98m), FromOutside(Out, "NBC High Interest Account", Nbc, 29057.42m, toType: "Savings account")], 0),
        // The samples' August: three accounts at one bank, two other banks with the outside.
        ["august"] = ([
            Between("National Bank of Canada account", Nbc, "National Bank of Canada card •••• 1234", Nbc, 383.92m, toType: "Credit card"),
            Between("National Bank of Canada savings", Nbc, "National Bank of Canada account", Nbc, 250m, fromType: "Savings account"),
            ToOutside("National Bank of Canada account", Nbc, Out, 500m),
            FromOutside(Out, "Fabrikam Card Services card •••• 1234", "Fabrikam Card Services", 750m, toType: "Credit card"),
            FromOutside(Out, "Northwind Bank account •••• 8901", "Northwind Bank", 120m)], 0),
        // The samples' October: six banks, a transfer from euros to dollars between two of them.
        ["october"] = ([
            Between("Caisse d'Epargne account •••• 8901", "Caisse d'Epargne", "Northwind Bank account •••• 8901", "Northwind Bank", 810.55m),
            FromOutside(Out, "Caisse d'Epargne account •••• 8901", "Caisse d'Epargne", 194.78m),
            FromOutside(Out, "Northwind Bank account •••• 8901", "Northwind Bank", 120m),
            FromOutside(Out, "Fabrikam Card Services card •••• 1234", "Fabrikam Card Services", 750m, toType: "Credit card"),
            FromOutside(Out, "Banque Exemple account •••• 8901", "Banque Exemple", 194.78m),
            FromOutside(Out, "Caisse Fictive account •••• 5678", "Caisse Fictive", 194.78m),
            FromOutside(Out, "Mutuelle Imaginaire account", "Mutuelle Imaginaire", 194.78m)], 0),
        // Four banks in three currencies, Wise between them, investments outside. Wise moves money with three banks:
        // two columns of banks cannot put all three beside it, one crossing is allowed.
        ["wise"] = ([
            Between("Northwind chequing", "Northwind Bank", "CAD", "Wise", 2000m), Between("CAD", "Wise", "EUR", "Wise", 1985m),
            Between("EUR", "Wise", "Compte courant", "Banque Exemple", 1983m), Between("Compte courant", "Banque Exemple", "Livret A", "Banque Exemple", 600m, toType: "Savings account"),
            Between("Northwind chequing", "Northwind Bank", "Northwind savings", "Northwind Bank", 900m, toType: "Savings account"),
            Between("Northwind chequing", "Northwind Bank", "Northwind card", "Northwind Bank", 1240m, toType: "Credit card"),
            Between("Contoso checking", "Contoso Bank", "USD", "Wise", 1500m), Between("USD", "Wise", "CAD", "Wise", 1490m),
            Between("Contoso checking", "Contoso Bank", "Contoso savings", "Contoso Bank", 700m, toType: "Savings account"),
            Between("Contoso checking", "Contoso Bank", "Contoso card", "Contoso Bank", 830m, toType: "Credit card"),
            ToOutside("Northwind savings", "Northwind Bank", "Investments (not imported)", 1000m, fromType: "Savings account"),
            ToOutside("Contoso savings", "Contoso Bank", "Investments (not imported)", 400m, fromType: "Savings account"),
            FromOutside(Out, "Northwind chequing", "Northwind Bank", 350m),
            Between("Compte courant", "Banque Exemple", "Northwind chequing", "Northwind Bank", 1310m)], 1),
    };

    private static readonly Lazy<Dictionary<int, List<MovementView>>> InventedLayouts = new(() =>
        JsonSerializer.Deserialize<Dictionary<int, List<MovementView>>>(typeof(MovementLayouts).Assembly.GetManifestResourceStream("Diagrams.InventedLayouts.json")!)!);

    /// <summary>The seeds of the invented layouts.</summary>
    public static IEnumerable<int> InventedSeeds => InventedLayouts.Value.Keys.Order();

    /// <summary>The invented layout of <paramref name="seed"/>.</summary>
    public static List<MovementView> Invented(int seed) => InventedLayouts.Value[seed];
}
