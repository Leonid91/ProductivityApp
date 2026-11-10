using System.Globalization;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// The money-moved diagram: every arrow has its own start and end point (never shared, coming or going), comes into
/// a box square to its side, goes through no box, and the outside box is the size of the accounts. Checked on a
/// small and a busy layout of invented accounts.
/// </summary>
public class FlowDiagramTests
{
    private static MovementView M(string from, string to, decimal amount, bool fromOutside = false, bool toOutside = false) =>
        new("2026-03-01", "transfer", "x", amount, from, to, fromOutside, toOutside, 1, null, fromOutside ? null : "Chequing account", toOutside ? null : "Credit card");

    public static TheoryData<string> Layouts => ["two", "busy", "five", "banks", "four banks"];

    /// <summary>A movement between accounts of the given banks (null: the outside).</summary>
    private static MovementView B(string from, string? fromBank, string to, string? toBank, decimal amount) =>
        new("2026-03-01", "transfer", "x", amount, from, to, fromBank is null, toBank is null, 1, null, fromBank is null ? null : "Chequing account",
            toBank is null ? null : "Savings account", FromBank: fromBank, ToBank: toBank);

    private static List<MovementView> Layout(string name) => name == "banks"
        ? [B("Northwind chequing", "Northwind Bank", "Northwind card", "Northwind Bank", 383.92m), B("Northwind savings", "Northwind Bank", "Northwind chequing", "Northwind Bank", 250m),
           B("Northwind chequing", "Northwind Bank", "Investments (not imported)", null, 500m), B("Outside (not imported)", null, "Fabrikam card", "Fabrikam", 750m),
           B("Contoso account", "Contoso", "Northwind chequing", "Northwind Bank", 80m), B("Northwind chequing", "Northwind Bank", "Contoso account", "Contoso", 1200m),
           B("Northwind chequing", "Northwind Bank", "Northwind savings", "Northwind Bank", 40m)]
        : name == "four banks"
        ? [B("Compte courant", "Banque Exemple", "Northwind chequing", "Northwind Bank", 800m), B("Livret A", "Caisse Fictive", "Compte courant", "Banque Exemple", 300m),
           B("Compte courant", "Banque Exemple", "Livret A", "Caisse Fictive", 120m), B("Northwind chequing", "Northwind Bank", "Contoso account", "Contoso", 60m),
           B("Northwind chequing", "Northwind Bank", "Northwind savings", "Northwind Bank", 450m), B("Northwind savings", "Northwind Bank", "Outside (not imported)", null, 90m),
           B("Outside (not imported)", null, "Compte courant", "Banque Exemple", 2100m)]
        : name == "busy"
        ? [M("Daily", "Card", 1200m), M("Card", "Daily", 35m), M("Daily", "Outside (not imported)", 800m, toOutside: true),
           M("Outside (not imported)", "Daily", 2600m, fromOutside: true), M("Card", "Outside (not imported)", 15m, toOutside: true),
           M("Outside (not imported)", "Card", 9m, fromOutside: true), M("Daily", "Savings", 300m), M("Savings", "Daily", 100m)]
        : name == "two"
        ? [M("Sample Debit", "Sample Credit", 1587.47m), M("Sample Debit", "Investments (not imported)", 1871.18m, toOutside: true),
           M("A. Friend (not imported)", "Sample Debit", 2.30m, fromOutside: true)]
        : [M("Northwind chequing account", "Northwind card •••• 1234", 383.92m), M("Northwind savings", "Northwind chequing account", 250m),
           M("Northwind chequing account", "Investments (not imported)", 500m, toOutside: true), M("Outside (not imported)", "Fabrikam card •••• 1234", 750m, fromOutside: true),
           M("Outside (not imported)", "Contoso account •••• 8901", 120m, fromOutside: true), M("Contoso account •••• 8901", "Northwind chequing account", 80m),
           M("Northwind chequing account", "Northwind savings", 40m)];

    /// <summary>A point of an arrow's line (one curve, or several end to end), t from 0 to 1.</summary>
    private static (double X, double Y) At(double[] p, double t)
    {
        var parts = (p.Length - 2) / 6;
        var k = Math.Min((int)(t * parts), parts - 1);
        var u = t * parts - k;
        var o = 6 * k;
        double m = 1 - u;
        return (m * m * m * p[o] + 3 * m * m * u * p[o + 2] + 3 * m * u * u * p[o + 4] + u * u * u * p[o + 6],
                m * m * m * p[o + 1] + 3 * m * m * u * p[o + 3] + 3 * m * u * u * p[o + 5] + u * u * u * p[o + 7]);
    }

    /// <summary>The numbers of an SVG path: "M x y C x1 y1 x2 y2 x y", with more "C" curves when the line bends in a gap.</summary>
    private static double[] Numbers(string path) => path.Split([' ', 'M', 'C', 'L', 'Z'], StringSplitOptions.RemoveEmptyEntries)
        .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Arrows_have_their_own_points_come_in_square_and_go_around_boxes(string layout)
    {
        var d = FlowDiagram.Build(Layout(layout));
        var outside = d.Nodes.Single(n => n.Outside);
        Assert.All(d.Nodes, n => { Assert.Equal(outside.W, n.W); Assert.Equal(outside.H, n.H); });

        // Start of each line and tip of each head: all different, at least 10 apart.
        var points = new List<(double X, double Y)>();
        foreach (var a in d.Arrows)
        {
            var p = Numbers(a.Path);
            var h = Numbers(a.Head);
            points.Add((p[0], p[1]));
            points.Add((h[0], h[1]));
            // Comes in square to the side: the last part of the curve is straight up, down, left or right.
            double dx = p[^2] - p[^4], dy = p[^1] - p[^3];
            Assert.True(Math.Abs(dx) < 0.2 || Math.Abs(dy) < 0.2, $"{a.From} -> {a.To} comes in at an angle");
            // The head's tip is just outside the destination box, and the line through no other box.
            var to = d.Nodes.Single(n => n.Id == a.To);
            Assert.InRange(Math.Max(Math.Abs(h[0] - to.X) - to.W / 2, Math.Abs(h[1] - to.Y) - to.H / 2), 0, 6);
            for (var k = 1; k < 60; k++)
            {
                var (x, y) = At(p, k / 60.0);
                Assert.False(d.Nodes.Any(n => Math.Abs(x - n.X) < n.W / 2 && Math.Abs(y - n.Y) < n.H / 2), $"{a.From} -> {a.To} goes through {d.Nodes.FirstOrDefault(n => Math.Abs(x - n.X) < n.W / 2 && Math.Abs(y - n.Y) < n.H / 2)?.Name} at {x:0},{y:0} path {a.Path} nodes {string.Join(" | ", d.Nodes.Select(n => $"{n.Name}@{n.X:0},{n.Y:0}"))} banks {string.Join(" | ", d.Banks.Select(n => $"{n.Name}@{n.X:0},{n.Y:0} {n.W:0}x{n.H:0}"))}");
            }
        }
        for (var i = 0; i < points.Count; i++)
            for (var j = i + 1; j < points.Count; j++)
                Assert.True(Math.Abs(points[i].X - points[j].X) + Math.Abs(points[i].Y - points[j].Y) >= 10, $"two arrows share a point at {points[i]}");

        // Heads large enough to see, never over one another; lines as thick as their money.
        var heads = d.Arrows.Select(a => Numbers(a.Head)).Select(h => (L: h.Where((_, i) => i % 2 == 0).Min(), R: h.Where((_, i) => i % 2 == 0).Max(),
            T: h.Where((_, i) => i % 2 == 1).Min(), B: h.Where((_, i) => i % 2 == 1).Max(), Half: Math.Sqrt(Math.Pow(h[2] - h[4], 2) + Math.Pow(h[3] - h[5], 2)) / 2)).ToList();
        Assert.All(heads, h => Assert.True(h.Half >= 7, $"head too small: {h.Half}"));
        for (var i = 0; i < heads.Count; i++)
            for (var j = i + 1; j < heads.Count; j++)
                Assert.False(heads[i].R > heads[j].L && heads[i].L < heads[j].R && heads[i].B > heads[j].T && heads[i].T < heads[j].B,
                    $"heads of {d.Arrows[i].Amount} and {d.Arrows[j].Amount} overlap");
        var byAmount = d.Arrows.OrderBy(a => a.Amount).ToList();
        for (var i = 1; i < byAmount.Count; i++)
            Assert.True(byAmount[i].Width >= byAmount[i - 1].Width, "a larger amount is drawn thinner");
        Assert.True(byAmount[^1].Width >= 2 * byAmount[0].Width || byAmount[^1].Amount < 10 * byAmount[0].Amount, "thickness does not follow the money");

        // Every account inside its bank; an arrow between two accounts of one bank stays inside it; no amount over a bank's name.
        foreach (var n in d.Nodes.Where(n => !n.Outside))
            Assert.Contains(d.Banks, b => n.X - n.W / 2 >= b.X - b.W / 2 && n.X + n.W / 2 <= b.X + b.W / 2 && n.Y - n.H / 2 >= b.Y - b.H / 2 && n.Y + n.H / 2 <= b.Y + b.H / 2);
        DiagramBank BankOf(string id) { var n = d.Nodes.Single(x => x.Id == id); return d.Banks.First(b => Math.Abs(n.X - b.X) < b.W / 2 && Math.Abs(n.Y - b.Y) < b.H / 2); }
        foreach (var a in d.Arrows.Where(a => !a.Outside && BankOf(a.From) == BankOf(a.To)))
        {
            var b = BankOf(a.From);
            var p = Numbers(a.Path);
            for (var k = 0; k <= 60; k++)
            {
                var (x, y) = At(p, k / 60.0);
                Assert.True(Math.Abs(x - b.X) <= b.W / 2 && Math.Abs(y - b.Y) <= b.H / 2, $"{a.From} -> {a.To} leaves {b.Name}");
            }
        }
        foreach (var a in d.Arrows)
            Assert.DoesNotContain(d.Banks, b => Math.Abs(a.LabelX - b.X) < 120 && a.LabelY > b.Y - b.H / 2 && a.LabelY - 11 < b.Y - b.H / 2 + 38);

        // Amounts never on top of each other.
        var labels = d.Arrows.Select(a => (a.LabelX, a.LabelY, W: Formatting.Money(a.Amount, "CAD").Length * 7.4 + 14)).ToList();
        for (var i = 0; i < labels.Count; i++)
            for (var j = i + 1; j < labels.Count; j++)
                Assert.False(Math.Abs(labels[i].LabelX - labels[j].LabelX) < (labels[i].W + labels[j].W) / 2 && Math.Abs(labels[i].LabelY - labels[j].LabelY) < 22,
                    $"amounts {d.Arrows[i].Amount} and {d.Arrows[j].Amount} overlap");
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
