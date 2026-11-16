using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Movements between accounts for the money-moved diagram: the layouts seen in real use (one bank in a month and in a
/// year, several banks, four banks with Wise between them) and invented ones, made from a seed.
/// </summary>
public static class MovementLayouts
{
    private static long _ids;
    private static readonly Dictionary<string, long> Ids = [];

    private static long Id(string key)
    {
        lock (Ids) return Ids.TryGetValue(key, out var v) ? v : Ids[key] = Interlocked.Increment(ref _ids);
    }

    /// <summary>A movement between two accounts (a null bank: the outside), each account its own id by its bank and name.</summary>
    public static MovementView Between(string from, string? fromBank, string to, string? toBank, decimal amount, string fromType = "Chequing account", string toType = "Chequing account") =>
        new("2026-03-01", "transfers", "x", amount, from, to, fromBank is null, toBank is null, 1, null, fromBank is null ? null : fromType, toBank is null ? null : toType,
            fromBank is null ? null : Id(fromBank + "/" + from), toBank is null ? null : Id(toBank + "/" + to), fromBank, toBank);

    private const string Nbc = "National Bank of Canada";
    private const string Out = "Outside (not imported)";

    /// <summary>The names of the layouts seen in real use.</summary>
    public static readonly string[] RealNames = ["month", "year", "august", "october", "wise"];

    /// <summary>A layout seen in real use, and how many crossings it may have (none, but where the banks force one).</summary>
    public static (List<MovementView> Movements, int AllowedCrossings) Real(string name) => name switch
    {
        // One bank in a month: a card paid, money out and a little in.
        "month" => ([Between("NBC Debit", Nbc, "NBC Credit", Nbc, 1587.47m, toType: "Credit card"), Between("NBC Debit", Nbc, Out, null, 1871.18m),
            Between(Out, null, "NBC Debit", Nbc, 2.30m)], 0),
        // The same bank over a year: both ways between chequing and savings, the card from both, the outside.
        "year" => ([
            Between("NBC Debit", Nbc, "NBC High Interest Account", Nbc, 18832.40m, toType: "Savings account"), Between("NBC High Interest Account", Nbc, "NBC Debit", Nbc, 55378.61m, fromType: "Savings account"),
            Between("NBC Debit", Nbc, "NBC Credit", Nbc, 16932.56m, toType: "Credit card"), Between("NBC High Interest Account", Nbc, "NBC Credit", Nbc, 957.34m, fromType: "Savings account", toType: "Credit card"),
            Between("NBC Credit", Nbc, Out, null, 3.07m, fromType: "Credit card"), Between("NBC Debit", Nbc, Out, null, 56254.30m),
            Between(Out, null, "NBC Debit", Nbc, 702.98m), Between(Out, null, "NBC High Interest Account", Nbc, 29057.42m, toType: "Savings account")], 0),
        // The samples' August: three accounts at one bank, two other banks with the outside.
        "august" => ([
            Between("National Bank of Canada account", Nbc, "National Bank of Canada card •••• 1234", Nbc, 383.92m, toType: "Credit card"),
            Between("National Bank of Canada savings", Nbc, "National Bank of Canada account", Nbc, 250m, fromType: "Savings account"),
            Between("National Bank of Canada account", Nbc, Out, null, 500m),
            Between(Out, null, "Fabrikam Card Services card •••• 1234", "Fabrikam Card Services", 750m, toType: "Credit card"),
            Between(Out, null, "Northwind Bank account •••• 8901", "Northwind Bank", 120m)], 0),
        // The samples' October: six banks, a transfer from euros to dollars between two of them.
        "october" => ([
            Between("Caisse d'Epargne account •••• 8901", "Caisse d'Epargne", "Northwind Bank account •••• 8901", "Northwind Bank", 810.55m),
            Between(Out, null, "Caisse d'Epargne account •••• 8901", "Caisse d'Epargne", 194.78m),
            Between(Out, null, "Northwind Bank account •••• 8901", "Northwind Bank", 120m),
            Between(Out, null, "Fabrikam Card Services card •••• 1234", "Fabrikam Card Services", 750m, toType: "Credit card"),
            Between(Out, null, "Banque Exemple account •••• 8901", "Banque Exemple", 194.78m),
            Between(Out, null, "Caisse Fictive account •••• 5678", "Caisse Fictive", 194.78m),
            Between(Out, null, "Mutuelle Imaginaire account", "Mutuelle Imaginaire", 194.78m)], 0),
        // Four banks in three currencies, Wise between them, investments outside. Wise moves money with three banks:
        // two columns of banks cannot put all three beside it, one crossing is allowed.
        "wise" => ([
            Between("Northwind chequing", "Northwind Bank", "CAD", "Wise", 2000m), Between("CAD", "Wise", "EUR", "Wise", 1985m),
            Between("EUR", "Wise", "Compte courant", "Banque Exemple", 1983m), Between("Compte courant", "Banque Exemple", "Livret A", "Banque Exemple", 600m, toType: "Savings account"),
            Between("Northwind chequing", "Northwind Bank", "Northwind savings", "Northwind Bank", 900m, toType: "Savings account"),
            Between("Northwind chequing", "Northwind Bank", "Northwind card", "Northwind Bank", 1240m, toType: "Credit card"),
            Between("Contoso checking", "Contoso Bank", "USD", "Wise", 1500m), Between("USD", "Wise", "CAD", "Wise", 1490m),
            Between("Contoso checking", "Contoso Bank", "Contoso savings", "Contoso Bank", 700m, toType: "Savings account"),
            Between("Contoso checking", "Contoso Bank", "Contoso card", "Contoso Bank", 830m, toType: "Credit card"),
            Between("Northwind savings", "Northwind Bank", "Investments (not imported)", null, 1000m, fromType: "Savings account"),
            Between("Contoso savings", "Contoso Bank", "Investments (not imported)", null, 400m, fromType: "Savings account"),
            Between(Out, null, "Northwind chequing", "Northwind Bank", 350m),
            Between("Compte courant", "Banque Exemple", "Northwind chequing", "Northwind Bank", 1310m)], 1),
        _ => throw new ArgumentException($"Unknown layout '{name}'.", nameof(name)),
    };

    /// <summary>
    /// An invented layout: one bank to four, one to three accounts each, two to ten movements, some with the outside,
    /// never two movements between the same two places the same way. Always the same for the same seed.
    /// </summary>
    public static List<MovementView> Invented(int seed)
    {
        var rng = new Random(seed);
        var banks = Enumerable.Range(0, rng.Next(1, 5)).Select(b => (Name: $"Bank {(char)('A' + b)} of test", Accounts: rng.Next(1, 4))).ToList();
        var accounts = banks.SelectMany(b => Enumerable.Range(0, b.Accounts).Select(a => (Bank: b.Name, Name: $"{b.Name[..6]} account {a + 1}"))).ToList();
        var movements = new List<MovementView>();
        var seen = new HashSet<(string, string)>();
        while (movements.Count == 0)
            for (var m = rng.Next(2, 11); m > 0; m--)
            {
                var outside = rng.NextDouble() < 0.3;
                var x = accounts[rng.Next(accounts.Count)];
                var y = accounts[rng.Next(accounts.Count)];
                if (!outside && x == y) continue;
                var amount = Math.Round((decimal)(rng.NextDouble() * 5000 + 5), 2);
                var movement = outside
                    ? rng.Next(2) == 0 ? Between(x.Name, x.Bank, Out, null, amount) : Between(Out, null, x.Name, x.Bank, amount)
                    : Between(x.Name, x.Bank, y.Name, y.Bank, amount);
                if (seen.Add((movement.From, movement.To))) movements.Add(movement);
            }
        return movements;
    }
}
