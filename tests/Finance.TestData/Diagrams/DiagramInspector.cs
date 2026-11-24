using System.Globalization;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// What makes a money-moved diagram readable, checked on any diagram: every account inside its bank; no line through a
/// box, over a bank's name or out of the drawing; an arrow between two accounts of one bank inside it; every arrow its
/// own start and end, coming in square to its box; heads large enough, never over one another; lines as thick as their
/// money; every amount clear of boxes, banks' names, heads, other amounts and the edges, leaving enough of its own arrow
/// to see. Lists every problem found (none: readable). Every rule is a check of its own, run on every diagram: no rule
/// depends on another's result.
/// </summary>
public static class DiagramInspector
{
    private readonly record struct Box(double L, double T, double R, double B);

    /// <summary>The numbers of an SVG path: "M x y C x1 y1 x2 y2 x y", with more "C" curves when the line bends in a gap.</summary>
    private static double[] Numbers(string path) => path.Split([' ', 'M', 'C', 'L', 'Z'], StringSplitOptions.RemoveEmptyEntries)
        .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();

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

    private static List<(double X, double Y)> Points(DiagramArrow a, int n = 80)
    {
        var p = Numbers(a.Path);
        return [.. Enumerable.Range(0, n + 1).Select(k => At(p, k / (double)n))];
    }

    /// <summary>Every pair (i, j) of 0..count-1 with i before j.</summary>
    private static IEnumerable<(int I, int J)> Pairs(int count) => Enumerable.Range(0, count).SelectMany(i => Enumerable.Range(i + 1, count - i - 1).Select(j => (i, j)));

    /// <summary>How many pairs of arrows cross each other.</summary>
    public static int Crossings(FlowDiagram d)
    {
        var lines = d.Arrows.Select(a => Points(a, 60)).ToList();
        return Pairs(lines.Count).Count(p => Enumerable.Range(0, lines[p.I].Count - 1).Any(u => Enumerable.Range(0, lines[p.J].Count - 1)
            .Any(v => FlowDiagram.Cross(lines[p.I][u], lines[p.I][u + 1], lines[p.J][v], lines[p.J][v + 1]))));
    }

    private static bool Overlap(Box a, Box b) => a.R > b.L && a.L < b.R && a.B > b.T && a.T < b.B;

    private static bool Holds(DiagramBank b, DiagramNode n) =>
        n.X - n.W / 2 >= b.X - b.W / 2 && n.X + n.W / 2 <= b.X + b.W / 2 && n.Y - n.H / 2 >= b.Y - b.H / 2 && n.Y + n.H / 2 <= b.Y + b.H / 2;

    private static Box HeadBox(double[] h) => new(h.Where((_, i) => i % 2 == 0).Min(), h.Where((_, i) => i % 2 == 1).Min(), h.Where((_, i) => i % 2 == 0).Max(), h.Where((_, i) => i % 2 == 1).Max());

    private static double NameHalfWidth(DiagramBank b) => Math.Min(b.W / 2, b.Name.Length * 4.6);

    private static Box LabelBox(DiagramArrow a)
    {
        var half = (Formatting.Money(a.Amount, "CAD").Length * 7.4 + 14) / 2;
        return new(a.LabelX - half, a.LabelY - 11, a.LabelX + half, a.LabelY + 11);
    }

    /// <summary>Every rule of readability the diagram breaks, one line each; empty when it is readable.</summary>
    public static List<string> Problems(FlowDiagram d)
    {
        var node = d.Nodes.ToDictionary(n => n.Id);
        var heads = d.Arrows.Select(a => HeadBox(Numbers(a.Head))).ToList();
        var boxes = d.Nodes.Select(n => new Box(n.X - n.W / 2, n.Y - n.H / 2, n.X + n.W / 2, n.Y + n.H / 2))
            .Concat(d.Banks.Select(b => new Box(b.X - NameHalfWidth(b), b.Y - b.H / 2 + 14, b.X + NameHalfWidth(b), b.Y - b.H / 2 + 36)))
            .ToList();
        var labels = d.Arrows.Select(LabelBox).ToList();
        var ends = d.Arrows.SelectMany(a => new[] { Numbers(a.Path).Take(2).ToArray(), Numbers(a.Head).Take(2).ToArray() }).Select(e => (X: e[0], Y: e[1])).ToList();
        var byAmount = d.Arrows.OrderBy(a => a.Amount).ToList();

        // Each arrow's line without its two ends, and the banks that hold both its accounts (an arrow inside a bank).
        var lines = d.Arrows.Select(a => (Arrow: a, Points: Points(a).Skip(1).SkipLast(1).ToList(),
            Banks: d.Banks.Where(b => !node[a.From].Outside && !node[a.To].Outside && Holds(b, node[a.From]) && Holds(b, node[a.To])).ToList())).ToList();

        IEnumerable<(bool Ok, string Problem)> Rules() =>
        [
            // The outside's box is the size of every box.
            .. d.Nodes.Where(n => n.Outside).SelectMany(o => d.Nodes.Select(n => (n.W == o.W && n.H == o.H, $"the box of {n.Name} is not the size of the outside box"))),
            // Every account inside exactly one bank.
            .. d.Nodes.Where(n => !n.Outside).Select(n => (d.Banks.Count(b => Holds(b, n)) == 1, $"{n.Name} is not inside exactly one bank")),
            // Each arrow comes in square to its box, its head close to it and large enough.
            .. d.Arrows.Select(a => Numbers(a.Path)).Zip(d.Arrows, (p, a) => (Math.Abs(p[^2] - p[^4]) < 0.2 || Math.Abs(p[^1] - p[^3]) < 0.2, $"{a.From} -> {a.To} comes in at an angle")),
            .. d.Arrows.Select(a => (A: a, H: Numbers(a.Head), To: node[a.To])).Select(x => (Gap: Math.Max(Math.Abs(x.H[0] - x.To.X) - x.To.W / 2, Math.Abs(x.H[1] - x.To.Y) - x.To.H / 2), x.A))
                .Select(x => (x.Gap >= 0 && x.Gap <= 6, $"the head of {x.A.From} -> {x.A.To} is {x.Gap:0.0} from its box")),
            .. d.Arrows.Select(a => (A: a, H: Numbers(a.Head))).Select(x => (Math.Sqrt(Math.Pow(x.H[2] - x.H[4], 2) + Math.Pow(x.H[3] - x.H[5], 2)) / 2 >= 7, $"the head of {x.A.From} -> {x.A.To} is too small")),
            // Through no box (more than half a line's width in: touching a corner is not going through), no bank's name,
            // never out of the drawing; inside its bank between two accounts of one bank.
            .. lines.Select(l => (!l.Points.Any(p => d.Nodes.Any(n => Math.Abs(p.X - n.X) < n.W / 2 - 3 && Math.Abs(p.Y - n.Y) < n.H / 2 - 3)), $"{l.Arrow.From} -> {l.Arrow.To} goes through a box")),
            .. lines.Select(l => (!l.Points.Any(p => d.Banks.Any(b => Math.Abs(p.X - b.X) < NameHalfWidth(b) && p.Y > b.Y - b.H / 2 + 6 && p.Y < b.Y - b.H / 2 + 36)), $"{l.Arrow.From} -> {l.Arrow.To} goes over a bank's name")),
            .. lines.Select(l => (l.Points.All(p => p.X >= 0 && p.X <= d.Width), $"{l.Arrow.From} -> {l.Arrow.To} leaves the drawing")),
            .. lines.SelectMany(l => l.Banks.Select(k => (l.Points.All(p => Math.Abs(p.X - k.X) <= k.W / 2 && Math.Abs(p.Y - k.Y) <= k.H / 2), $"{l.Arrow.From} -> {l.Arrow.To} leaves {k.Name}"))),
            // Every arrow its own start and end; heads never over one another; thicker for more money.
            .. Pairs(ends.Count).Select(p => (Math.Abs(ends[p.I].X - ends[p.J].X) + Math.Abs(ends[p.I].Y - ends[p.J].Y) >= 6, $"two arrows share a point at {ends[p.I]}")),
            .. Pairs(heads.Count).Select(p => (!Overlap(heads[p.I], heads[p.J]), $"the heads of {d.Arrows[p.I].Amount} and {d.Arrows[p.J].Amount} overlap")),
            .. byAmount.Skip(1).Zip(byAmount, (more, less) => (more.Width >= less.Width, $"{more.Amount} is drawn thinner than {less.Amount}")),
            // Amounts: clear of every box, bank's name, head, other amount and edge, and of their own arrow enough to see it.
            .. labels.Zip(d.Arrows, (box, a) => (!boxes.Any(b => Overlap(box, b)), $"the amount {a.Amount} is over a box or a bank's name")),
            .. labels.Zip(d.Arrows, (box, a) => (!heads.Any(h => Overlap(box, h)), $"the amount {a.Amount} is over an arrowhead")),
            .. labels.Zip(d.Arrows, (box, a) => (box.L >= 0 && box.R <= d.Width, $"the amount {a.Amount} is out of the drawing")),
            .. labels.Zip(d.Arrows, (box, a) => (Visible(a, box) >= FlowDiagram.VisibleLine - 2, $"the amount {a.Amount} hides its arrow ({Visible(a, box):0} left to see)")),
            .. Pairs(labels.Count).Select(p => (!Overlap(labels[p.I], labels[p.J]), $"the amounts {d.Arrows[p.I].Amount} and {d.Arrows[p.J].Amount} overlap")),
        ];
        return [.. Rules().Where(r => !r.Ok).Select(r => r.Problem)];
    }

    /// <summary>How much of an arrow's line its amount leaves to see.</summary>
    private static double Visible(DiagramArrow a, Box box)
    {
        var line = Points(a);
        var length = line.Zip(line.Skip(1), (u, v) => Math.Sqrt((u.X - v.X) * (u.X - v.X) + (u.Y - v.Y) * (u.Y - v.Y))).Sum();
        return length - length * line.Count(q => q.X > box.L && q.X < box.R && q.Y > box.T && q.Y < box.B) / line.Count;
    }
}
