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

    public static TheoryData<string> Layouts => ["two", "five"];

    private static List<MovementView> Layout(string name) => name == "two"
        ? [M("Sample Debit", "Sample Credit", 1587.47m), M("Sample Debit", "Investments (not imported)", 1871.18m, toOutside: true),
           M("A. Friend (not imported)", "Sample Debit", 2.30m, fromOutside: true)]
        : [M("Northwind chequing account", "Northwind card •••• 1234", 383.92m), M("Northwind savings", "Northwind chequing account", 250m),
           M("Northwind chequing account", "Investments (not imported)", 500m, toOutside: true), M("Outside (not imported)", "Fabrikam card •••• 1234", 750m, fromOutside: true),
           M("Outside (not imported)", "Contoso account •••• 8901", 120m, fromOutside: true), M("Contoso account •••• 8901", "Northwind chequing account", 80m),
           M("Northwind chequing account", "Northwind savings", 40m)];

    /// <summary>The numbers of an SVG path: "M x y C x1 y1 x2 y2 x y".</summary>
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
            double dx = p[6] - p[4], dy = p[7] - p[5];
            Assert.True(Math.Abs(dx) < 0.2 || Math.Abs(dy) < 0.2, $"{a.From} -> {a.To} comes in at an angle");
            // The head's tip is just outside the destination box, and the line through no other box.
            var to = d.Nodes.Single(n => n.Id == a.To);
            Assert.InRange(Math.Max(Math.Abs(h[0] - to.X) - to.W / 2, Math.Abs(h[1] - to.Y) - to.H / 2), 0, 6);
            for (var k = 1; k < 40; k++)
            {
                double t = k / 40.0, m = 1 - t;
                var x = m * m * m * p[0] + 3 * m * m * t * p[2] + 3 * m * t * t * p[4] + t * t * t * p[6];
                var y = m * m * m * p[1] + 3 * m * m * t * p[3] + 3 * m * t * t * p[5] + t * t * t * p[7];
                Assert.DoesNotContain(d.Nodes, n => Math.Abs(x - n.X) < n.W / 2 && Math.Abs(y - n.Y) < n.H / 2);
            }
        }
        for (var i = 0; i < points.Count; i++)
            for (var j = i + 1; j < points.Count; j++)
                Assert.True(Math.Abs(points[i].X - points[j].X) + Math.Abs(points[i].Y - points[j].Y) >= 10, $"two arrows share a point at {points[i]}");

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
