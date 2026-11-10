using System.Globalization;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// The money-moved diagram stays readable, whatever it shows: checked on the layouts seen in real use (one bank in a
/// month and in a year, several banks, four banks with Wise between them) and on hundreds of invented ones.
/// </summary>
/// <remarks>
/// Every diagram, always: every account inside the box of its bank; no line through a box, over a bank's name or out of
/// the drawing; an arrow between two accounts of one bank inside that bank; every arrow its own start and end (never a
/// point shared with another), coming into its box square to its side; heads large enough and never over one another;
/// lines as thick as their money; every amount clear of the boxes, the banks' names, the heads, the other amounts and the
/// edges of the drawing, and leaving at least <see cref="FlowDiagram.VisibleLine"/> of its own arrow to see. The layouts
/// seen in real use, besides: no two arrows crossing.
/// </remarks>
public class FlowDiagramTests
{
    private static long _ids;

    /// <summary>A movement between two accounts (a null bank: the outside), each account its own id by its name.</summary>
    private static MovementView B(string from, string? fromBank, string to, string? toBank, decimal amount, string fromType = "Chequing account", string toType = "Chequing account") =>
        new("2026-03-01", "transfers", "x", amount, from, to, fromBank is null, toBank is null, 1, null, fromBank is null ? null : fromType, toBank is null ? null : toType,
            fromBank is null ? null : Id(fromBank + "/" + from), toBank is null ? null : Id(toBank + "/" + to), fromBank, toBank);

    private static readonly Dictionary<string, long> Ids = [];

    private static long Id(string key)
    {
        lock (Ids) return Ids.TryGetValue(key, out var v) ? v : Ids[key] = Interlocked.Increment(ref _ids);
    }

    private const string Nbc = "National Bank of Canada";

    /// <summary>Layouts seen in real use, and how many crossings each may have (none, but where the banks force one).</summary>
    public static TheoryData<string> RealLayouts => ["month", "year", "august", "october", "wise"];

    private static (List<MovementView> Movements, int Crossings) Real(string name) => name switch
    {
        // One bank in a month: a card paid, money out and a little in.
        "month" => ([B("NBC Debit", Nbc, "NBC Credit", Nbc, 1587.47m, toType: "Credit card"), B("NBC Debit", Nbc, "Outside (not imported)", null, 1871.18m),
            B("Outside (not imported)", null, "NBC Debit", Nbc, 2.30m)], 0),
        // The same bank over a year: both ways between chequing and savings, the card from both, the outside.
        "year" => ([
            B("NBC Debit", Nbc, "NBC High Interest Account", Nbc, 18832.40m, toType: "Savings account"), B("NBC High Interest Account", Nbc, "NBC Debit", Nbc, 55378.61m, fromType: "Savings account"),
            B("NBC Debit", Nbc, "NBC Credit", Nbc, 16932.56m, toType: "Credit card"), B("NBC High Interest Account", Nbc, "NBC Credit", Nbc, 957.34m, fromType: "Savings account", toType: "Credit card"),
            B("NBC Credit", Nbc, "Outside (not imported)", null, 3.07m, fromType: "Credit card"), B("NBC Debit", Nbc, "Outside (not imported)", null, 56254.30m),
            B("Outside (not imported)", null, "NBC Debit", Nbc, 702.98m), B("Outside (not imported)", null, "NBC High Interest Account", Nbc, 29057.42m, toType: "Savings account")], 0),
        // The samples' August: three accounts at one bank, two other banks with the outside.
        "august" => ([
            B("National Bank of Canada account", Nbc, "National Bank of Canada card •••• 1234", Nbc, 383.92m, toType: "Credit card"),
            B("National Bank of Canada savings", Nbc, "National Bank of Canada account", Nbc, 250m, fromType: "Savings account"),
            B("National Bank of Canada account", Nbc, "Outside (not imported)", null, 500m),
            B("Outside (not imported)", null, "Fabrikam Card Services card •••• 1234", "Fabrikam Card Services", 750m, toType: "Credit card"),
            B("Outside (not imported)", null, "Northwind Bank account •••• 8901", "Northwind Bank", 120m)], 0),
        // The samples' October: six banks, a transfer from euros to dollars between two of them.
        "october" => ([
            B("Caisse d'Epargne account •••• 8901", "Caisse d'Epargne", "Northwind Bank account •••• 8901", "Northwind Bank", 810.55m),
            B("Outside (not imported)", null, "Caisse d'Epargne account •••• 8901", "Caisse d'Epargne", 194.78m),
            B("Outside (not imported)", null, "Northwind Bank account •••• 8901", "Northwind Bank", 120m),
            B("Outside (not imported)", null, "Fabrikam Card Services card •••• 1234", "Fabrikam Card Services", 750m, toType: "Credit card"),
            B("Outside (not imported)", null, "Banque Exemple account •••• 8901", "Banque Exemple", 194.78m),
            B("Outside (not imported)", null, "Caisse Fictive account •••• 5678", "Caisse Fictive", 194.78m),
            B("Outside (not imported)", null, "Mutuelle Imaginaire account", "Mutuelle Imaginaire", 194.78m)], 0),
        // Four banks in three currencies, Wise between them, investments outside. Wise moves money with three banks:
        // two columns of banks cannot put all three beside it, one crossing is allowed.
        _ => ([
            B("Northwind chequing", "Northwind Bank", "CAD", "Wise", 2000m), B("CAD", "Wise", "EUR", "Wise", 1985m),
            B("EUR", "Wise", "Compte courant", "Banque Exemple", 1983m), B("Compte courant", "Banque Exemple", "Livret A", "Banque Exemple", 600m, toType: "Savings account"),
            B("Northwind chequing", "Northwind Bank", "Northwind savings", "Northwind Bank", 900m, toType: "Savings account"),
            B("Northwind chequing", "Northwind Bank", "Northwind card", "Northwind Bank", 1240m, toType: "Credit card"),
            B("Contoso checking", "Contoso Bank", "USD", "Wise", 1500m), B("USD", "Wise", "CAD", "Wise", 1490m),
            B("Contoso checking", "Contoso Bank", "Contoso savings", "Contoso Bank", 700m, toType: "Savings account"),
            B("Contoso checking", "Contoso Bank", "Contoso card", "Contoso Bank", 830m, toType: "Credit card"),
            B("Northwind savings", "Northwind Bank", "Investments (not imported)", null, 1000m, fromType: "Savings account"),
            B("Contoso savings", "Contoso Bank", "Investments (not imported)", null, 400m, fromType: "Savings account"),
            B("Outside (not imported)", null, "Northwind chequing", "Northwind Bank", 350m),
            B("Compte courant", "Banque Exemple", "Northwind chequing", "Northwind Bank", 1310m)], 1),
    };

    [Theory]
    [MemberData(nameof(RealLayouts))]
    public void Layouts_seen_in_real_use_are_readable_and_arrows_do_not_cross(string name)
    {
        var (movements, crossings) = Real(name);
        var d = FlowDiagram.Build(movements);
        DiagramChecks.AssertReadable(d);
        Assert.True(DiagramChecks.Crossings(d) <= crossings, $"{DiagramChecks.Crossings(d)} crossings in {name}");
    }

    [Fact]
    public void A_hundred_and_fifty_invented_layouts_are_readable()
    {
        // From one bank to four, one to three accounts each, two to ten movements, some with the outside.
        var rng = new Random(20261110);
        var failed = new List<string>();
        for (var n = 0; n < 150; n++)
        {
            var banks = Enumerable.Range(0, rng.Next(1, 5)).Select(b => (Name: $"Bank {(char)('A' + b)} of test", Accounts: rng.Next(1, 4))).ToList();
            var accounts = banks.SelectMany(b => Enumerable.Range(0, b.Accounts).Select(a => (Bank: b.Name, Name: $"{b.Name[..6]} account {a + 1}"))).ToList();
            var movements = new List<MovementView>();
            var seen = new HashSet<(string, string)>();
            var count = rng.Next(2, 11);
            for (var m = 0; m < count; m++)
            {
                var outside = rng.NextDouble() < 0.3;
                var x = accounts[rng.Next(accounts.Count)];
                var y = accounts[rng.Next(accounts.Count)];
                if (!outside && x == y) continue;
                var amount = Math.Round((decimal)(rng.NextDouble() * 5000 + 5), 2);
                var mv = outside
                    ? rng.Next(2) == 0 ? B(x.Name, x.Bank, "Outside (not imported)", null, amount) : B("Outside (not imported)", null, x.Name, x.Bank, amount)
                    : B(x.Name, x.Bank, y.Name, y.Bank, amount);
                if (seen.Add((mv.From, mv.To))) movements.Add(mv);
            }
            if (movements.Count == 0) continue;
            var d = FlowDiagram.Build(movements);
            try { DiagramChecks.AssertReadable(d); }
            catch (Exception e) { failed.Add($"layout {n} ({movements.Count} movements, {banks.Count} banks): {e.Message.Split((char)10)[0]}"); }
        }
        Assert.True(failed.Count == 0, $"{failed.Count} layouts not readable: " + string.Join(" | ", failed.Take(8)));
    }

    [Fact]
    public void A_layout_is_quick_and_seen_again_at_once()
    {
        var (movements, _) = Real("wise");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var d = FlowDiagram.Build([.. movements]);
        Assert.True(clock.ElapsedMilliseconds < 4000, $"{clock.ElapsedMilliseconds} ms");
        clock.Restart();
        Assert.Same(d, FlowDiagram.Build([.. movements]));
        Assert.True(clock.ElapsedMilliseconds < 50, $"{clock.ElapsedMilliseconds} ms the second time");
    }

    [Fact]
    public void Long_names_are_cut_to_fit_their_box()
    {
        Assert.Equal("Sample Debit", FlowDiagram.Fit("Sample Debit", 210));
        var cut = FlowDiagram.Fit("Fabrikam Card Services card •••• 1234", 210);
        Assert.EndsWith("…", cut);
        Assert.True(cut.Length * 8 <= 210 - 16 + 8);
    }
}
