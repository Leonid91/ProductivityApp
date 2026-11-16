using System.Globalization;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// What makes a money-moved diagram readable, checked on any diagram: every account inside its bank; no line through a
/// box, over a bank's name or out of the drawing; an arrow between two accounts of one bank inside it; every arrow its
/// own start and end, coming in square to its box; heads large enough, never over one another; lines as thick as their
/// money; every amount clear of boxes, banks' names, heads, other amounts and the edges, leaving enough of its own arrow
/// to see. Lists every problem found (none: readable).
/// </summary>
public static class DiagramInspector
{
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

    /// <summary>How many pairs of arrows cross each other.</summary>
    public static int Crossings(FlowDiagram d)
    {
        var lines = d.Arrows.Select(a => Points(a, 60)).ToList();
        var count = 0;
        for (var i = 0; i < lines.Count; i++)
            for (var j = i + 1; j < lines.Count; j++)
                if (Enumerable.Range(0, lines[i].Count - 1).Any(u => Enumerable.Range(0, lines[j].Count - 1).Any(v => FlowDiagram.Cross(lines[i][u], lines[i][u + 1], lines[j][v], lines[j][v + 1]))))
                    count++;
        return count;
    }

    private static bool Overlap((double L, double T, double R, double B) a, (double L, double T, double R, double B) b) => a.R > b.L && a.L < b.R && a.B > b.T && a.T < b.B;

    /// <summary>Every rule of readability the diagram breaks, one line each; empty when it is readable.</summary>
    public static List<string> Problems(FlowDiagram d)
    {
        var problems = new List<string>();
        void Expect(bool ok, string problem) { if (!ok) problems.Add(problem); }

        var outside = d.Nodes.SingleOrDefault(n => n.Outside);
        if (outside is not null)
            foreach (var n in d.Nodes) Expect(n.W == outside.W && n.H == outside.H, $"the box of {n.Name} is not the size of the outside box");
        // Every account inside its bank.
        DiagramBank? BankOf(DiagramNode n)
        {
            var holding = d.Banks.Where(b => n.X - n.W / 2 >= b.X - b.W / 2 && n.X + n.W / 2 <= b.X + b.W / 2 && n.Y - n.H / 2 >= b.Y - b.H / 2 && n.Y + n.H / 2 <= b.Y + b.H / 2).ToList();
            return holding.Count == 1 ? holding[0] : null;
        }
        foreach (var n in d.Nodes.Where(n => !n.Outside)) Expect(BankOf(n) is not null, $"{n.Name} is not inside exactly one bank");
        if (problems.Count > 0) return problems;

        var heads = new List<(double L, double T, double R, double B)>();
        var ends = new List<(double X, double Y)>();
        foreach (var a in d.Arrows)
        {
            var p = Numbers(a.Path);
            var h = Numbers(a.Head);
            ends.Add((p[0], p[1]));
            ends.Add((h[0], h[1]));
            // Comes in square to the side: the last part of the curve is straight up, down, left or right.
            double dx = p[^2] - p[^4], dy = p[^1] - p[^3];
            Expect(Math.Abs(dx) < 0.2 || Math.Abs(dy) < 0.2, $"{a.From} -> {a.To} comes in at an angle");
            var to = d.Nodes.Single(n => n.Id == a.To);
            var headGap = Math.Max(Math.Abs(h[0] - to.X) - to.W / 2, Math.Abs(h[1] - to.Y) - to.H / 2);
            Expect(headGap is >= 0 and <= 6, $"the head of {a.From} -> {a.To} is {headGap:0.0} from its box");
            heads.Add((h.Where((_, i) => i % 2 == 0).Min(), h.Where((_, i) => i % 2 == 1).Min(), h.Where((_, i) => i % 2 == 0).Max(), h.Where((_, i) => i % 2 == 1).Max()));
            Expect(Math.Sqrt(Math.Pow(h[2] - h[4], 2) + Math.Pow(h[3] - h[5], 2)) / 2 >= 7, $"the head of {a.From} -> {a.To} is too small");
            // Through no box, no bank's name, never out of the drawing; inside its bank between two accounts of one bank.
            var from = d.Nodes.Single(n => n.Id == a.From);
            var sameBank = !from.Outside && !to.Outside && BankOf(from) == BankOf(to) ? BankOf(from) : null;
            foreach (var (x, y) in Points(a).Skip(1).SkipLast(1))
            {
                // Into a box by more than half a line's width (touching a corner is not going through).
                if (d.Nodes.Any(n => Math.Abs(x - n.X) < n.W / 2 - 3 && Math.Abs(y - n.Y) < n.H / 2 - 3)) { problems.Add($"{a.From} -> {a.To} goes through a box at {x:0},{y:0}"); break; }
                if (d.Banks.Any(b => Math.Abs(x - b.X) < Math.Min(b.W / 2, b.Name.Length * 4.6) && y > b.Y - b.H / 2 + 6 && y < b.Y - b.H / 2 + 36)) { problems.Add($"{a.From} -> {a.To} goes over a bank's name at {x:0},{y:0}"); break; }
                if (x < 0 || x > d.Width) { problems.Add($"{a.From} -> {a.To} leaves the drawing at {x:0},{y:0}"); break; }
                if (sameBank is { } k && !(Math.Abs(x - k.X) <= k.W / 2 && Math.Abs(y - k.Y) <= k.H / 2)) { problems.Add($"{a.From} -> {a.To} leaves {k.Name}"); break; }
            }
        }
        for (var i = 0; i < ends.Count; i++)
            for (var j = i + 1; j < ends.Count; j++)
                Expect(Math.Abs(ends[i].X - ends[j].X) + Math.Abs(ends[i].Y - ends[j].Y) >= 6, $"two arrows share a point at {ends[i]}");
        for (var i = 0; i < heads.Count; i++)
            for (var j = i + 1; j < heads.Count; j++)
                Expect(!Overlap(heads[i], heads[j]), $"the heads of {d.Arrows[i].Amount} and {d.Arrows[j].Amount} overlap");
        var byAmount = d.Arrows.OrderBy(a => a.Amount).ToList();
        for (var i = 1; i < byAmount.Count; i++) Expect(byAmount[i].Width >= byAmount[i - 1].Width, $"{byAmount[i].Amount} is drawn thinner than {byAmount[i - 1].Amount}");

        // Amounts: clear of every box, bank's name, head, other amount and edge, and of their own arrow enough to see it.
        var labels = d.Arrows.Select(a => (a, Box: (L: a.LabelX - (Formatting.Money(a.Amount, "CAD").Length * 7.4 + 14) / 2, T: a.LabelY - 11,
            R: a.LabelX + (Formatting.Money(a.Amount, "CAD").Length * 7.4 + 14) / 2, B: a.LabelY + 11))).ToList();
        var boxes = d.Nodes.Select(n => (L: n.X - n.W / 2, T: n.Y - n.H / 2, R: n.X + n.W / 2, B: n.Y + n.H / 2))
            .Concat(d.Banks.Select(b => (L: b.X - Math.Min(b.W / 2, b.Name.Length * 4.6), T: b.Y - b.H / 2 + 14, R: b.X + Math.Min(b.W / 2, b.Name.Length * 4.6), B: b.Y - b.H / 2 + 36)))
            .ToList();
        foreach (var (a, box) in labels)
        {
            Expect(!boxes.Any(b => Overlap(box, b)), $"the amount {a.Amount} is over a box or a bank's name");
            Expect(!heads.Any(h => Overlap(box, h)), $"the amount {a.Amount} is over an arrowhead");
            Expect(box.L >= 0 && box.R <= d.Width, $"the amount {a.Amount} is out of the drawing");
            var line = Points(a);
            var length = line.Zip(line.Skip(1), (u, v) => Math.Sqrt((u.X - v.X) * (u.X - v.X) + (u.Y - v.Y) * (u.Y - v.Y))).Sum();
            var hidden = length * line.Count(q => q.X > box.L && q.X < box.R && q.Y > box.T && q.Y < box.B) / line.Count;
            Expect(length - hidden >= FlowDiagram.VisibleLine - 2, $"the amount {a.Amount} hides its arrow ({length - hidden:0} of {length:0} left to see)");
        }
        for (var i = 0; i < labels.Count; i++)
            for (var j = i + 1; j < labels.Count; j++)
                Expect(!Overlap(labels[i].Box, labels[j].Box), $"the amounts {labels[i].a.Amount} and {labels[j].a.Amount} overlap");
        return problems;
    }
}
