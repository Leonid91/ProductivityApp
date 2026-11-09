using System.Globalization;
using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.Services;

/// <summary>Who is on the other side of money moved outside the app, and how much went each way.</summary>
public sealed record OutsideParty(string Name, decimal Sent, decimal Received);

/// <summary>An account box, or the outside box, centred on (X, Y) with size W, H; the outside box lists who is behind it.</summary>
public sealed record DiagramNode(string Id, string Name, string Type, bool Outside, double X, double Y, double W, double H, List<OutsideParty> Parties);

/// <summary>
/// One arrow: the total moved from one place to another. <see cref="Path"/> is the curve (SVG path, no fill) ending at the
/// base of <see cref="Head"/>, a filled triangle pointing at the destination. The amount sits at (LabelX, LabelY).
/// </summary>
public sealed record DiagramArrow(string From, string To, decimal Amount, int Count, bool Outside, string Path, string Head, double Width, double LabelX, double LabelY);

/// <summary>
/// The money-moved diagram, laid out once by the engine and drawn the same way on screen and in the PDF: the person's
/// accounts as boxes (name, then type), everything outside the app as one box of the same size below them, and one
/// arrow per direction between two places, as thick as the money.
/// </summary>
/// <remarks>
/// Every arrow has its own point on the side of each box it touches (no two arrows share a point, coming or going),
/// leaves and reaches the box square to that side, and goes around the other boxes. Points of one side are ordered by
/// where their arrows go, so arrows between the same two boxes run side by side instead of over each other. Amounts
/// sit where they cover no box, no arrowhead and as little of the other arrows as possible.
/// </remarks>
public sealed record FlowDiagram(double Width, double Height, List<DiagramNode> Nodes, List<DiagramArrow> Arrows)
{
    public const string OutsideId = "__outside";
    private const double W = 1000, MinNodeW = 210, NodeH = 60, RowGap = 170, OutsideGap = 190, Margin = 14, CharW = 8;

    public static FlowDiagram Build(IReadOnlyList<MovementView> movements)
    {
        // Laid out once, then again lower if an arrow or a label would leave the top edge.
        var (d, top) = Build(movements, Margin);
        return top >= Margin ? d : Build(movements, Margin + (Margin - top)).Diagram;
    }

    /// <summary>
    /// How many characters of a name fit in a box of width <paramref name="w"/>; longer names are cut with "…" by the
    /// drawings (screen and PDF), the full name shows on hover.
    /// </summary>
    public static string Fit(string name, double w)
    {
        var max = (int)Math.Floor((w - 16) / CharW);
        return name.Length <= max ? name : name[..Math.Max(1, max - 1)].TrimEnd() + "…";
    }

    private static (FlowDiagram Diagram, double Top) Build(IReadOnlyList<MovementView> movements, double Top)
    {
        if (movements.Count == 0) return (new FlowDiagram(W, 0, [], []), Margin);

        // Places: own accounts (by name) and the single outside box.
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        var moved = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var edges = new Dictionary<(string From, string To), (decimal Amount, int Count)>();
        var parties = new Dictionary<string, (decimal Sent, decimal Received)>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in movements)
        {
            var from = m.FromOutside ? OutsideId : m.From;
            var to = m.ToOutside ? OutsideId : m.To;
            if (!m.FromOutside) { types.TryAdd(m.From, m.FromType ?? ""); moved[m.From] = moved.GetValueOrDefault(m.From) + m.Amount; }
            if (!m.ToOutside) { types.TryAdd(m.To, m.ToType ?? ""); moved[m.To] = moved.GetValueOrDefault(m.To) + m.Amount; }
            var e = edges.GetValueOrDefault((from, to));
            edges[(from, to)] = (e.Amount + m.Amount, e.Count + 1);
            if (m.ToOutside) { var p = parties.GetValueOrDefault(Party(m.To)); parties[Party(m.To)] = (p.Sent + m.Amount, p.Received); }
            if (m.FromOutside) { var p = parties.GetValueOrDefault(Party(m.From)); parties[Party(m.From)] = (p.Sent, p.Received + m.Amount); }
        }

        // Accounts in rows (up to four a row, the busiest first), the outside box centred below. Every box has the same
        // size: wide enough for the longest name, as far as the row allows.
        var accounts = moved.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).ToList();
        var perRow = accounts.Count <= 4 ? Math.Max(1, accounts.Count) : (int)Math.Ceiling(accounts.Count / Math.Ceiling(accounts.Count / 4.0));
        var longest = accounts.Append("Outside the app").Max(a => a.Length);
        var nodeW = Math.Clamp(longest * CharW + 28, MinNodeW, Math.Max(MinNodeW, W / perRow - 80));
        var nodes = new List<DiagramNode>();
        double lastY = Top + NodeH / 2;
        // Room under a raised middle box for arrows running between its neighbours.
        const double Lift = 110;
        for (var i = 0; i < accounts.Count; i++)
        {
            var row = i / perRow;
            var inRow = Math.Min(perRow, accounts.Count - row * perRow);
            var col = i - row * perRow;
            var x = W / inRow * (col + 0.5);
            // A gentle arc: accounts in the middle of a row sit higher, so arrows between neighbours and to the far
            // end of the row come in at different angles instead of piling up along one line.
            var arc = inRow < 3 ? 0 : (1 - Math.Abs(2 * (col + 0.5) / inRow - 1) - 1.0 / inRow) / (1 - 1.0 / inRow);
            var baseline = Top + NodeH / 2 + (inRow < 3 ? 0 : Lift) + row * (RowGap + Lift);
            var y = baseline - Lift * arc;
            lastY = Math.Max(lastY, baseline);
            nodes.Add(new DiagramNode(accounts[i], accounts[i], types[accounts[i]], false, x, y, nodeW, NodeH, []));
        }
        if (parties.Count > 0)
        {
            var list = parties.OrderByDescending(p => p.Value.Sent + p.Value.Received).Select(p => new OutsideParty(p.Key, p.Value.Sent, p.Value.Received)).ToList();
            nodes.Add(new DiagramNode(OutsideId, "Outside the app", "Other accounts & people", true, W / 2, lastY + OutsideGap + NodeH, nodeW, NodeH, list));
        }

        var byId = nodes.ToDictionary(n => n.Id);
        var max = edges.Values.Max(e => e.Amount);
        var links = edges.Where(e => e.Key.From != e.Key.To).OrderByDescending(e => e.Value.Amount)
            .ThenBy(e => e.Key.From, StringComparer.Ordinal).ThenBy(e => e.Key.To, StringComparer.Ordinal).ToList();

        // 1. Sides: for each arrow, the pair of sides giving the shortest curve that goes through no other box.
        var ends = new List<End>();
        for (var i = 0; i < links.Count; i++)
        {
            var (from, to) = links[i].Key;
            var (a, b) = (byId[from], byId[to]);
            var best = (Cost: double.MaxValue, A: Sides.Bottom, B: Sides.Top);
            foreach (var sa in AllSides)
                foreach (var sb in AllSides)
                {
                    var (ax, ay, anx, any) = Port(a, sa, 0);
                    var (bx, by, bnx, bny) = Port(b, sb, 0);
                    var c = Curve(ax, ay, anx, any, bx, by, bnx, bny);
                    var cost = Cost(c, nodes, a, b) + (sa == Natural(a, b) ? 0 : 6) + (sb == Natural(b, a) ? 0 : 6);
                    if (cost < best.Cost) best = (cost, sa, sb);
                }
            ends.Add(new End(i, a, b, true, best.A, Pair(from, to)));
            ends.Add(new End(i, b, a, false, best.B, Pair(from, to)));
        }

        // 2. Points: each end its own point on its side, ordered by where the arrow goes.
        foreach (var group in ends.GroupBy(e => (e.Node.Id, e.Side)))
        {
            var horizontal = group.Key.Side is Sides.Top or Sides.Bottom;
            var ordered = group.OrderBy(e => horizontal ? e.Other.X : e.Other.Y).ThenBy(e => horizontal ? e.Other.Y : e.Other.X).ThenBy(e => e.PairOrder).ToList();
            var n = ordered.Count;
            var span = horizontal ? nodeW - 40 : NodeH - 16;
            for (var k = 0; k < n; k++)
            {
                var e = ordered[k];
                e.Spacing = n == 1 ? double.MaxValue : span / n;
                (e.X, e.Y, e.Nx, e.Ny) = Port(e.Node, e.Side, -span / 2 + span * (k + 0.5) / n);
            }
        }

        // 3. Curves and heads.
        var drawn = new List<(string From, string To, decimal Amount, int Count, Bez C, double Width, string Head, (double L, double T, double R, double B) HeadBox)>();
        for (var i = 0; i < links.Count; i++)
        {
            var ((from, to), (amount, count)) = links[i];
            var s = ends.First(e => e.Arrow == i && e.Leaving);
            var e = ends.First(e => e.Arrow == i && !e.Leaving);
            var room = Math.Min(s.Spacing, e.Spacing);
            var width = Math.Min(2 + 7 * Math.Sqrt((double)(amount / max)), room * 0.4);
            const double Gap = 3;
            var headLen = 11 + width * 1.3;
            var headHalf = Math.Min(6 + width * 0.8, room / 2 - 1.5);
            double tx = e.X + e.Nx * Gap, ty = e.Y + e.Ny * Gap;
            double bx = tx + e.Nx * headLen, by = ty + e.Ny * headLen;
            var c = Curve(s.X + s.Nx * Gap, s.Y + s.Ny * Gap, s.Nx, s.Ny, bx, by, e.Nx, e.Ny);
            // The head points into the box, square to its side.
            double px = e.Ny * headHalf, py = -e.Nx * headHalf;
            var head = $"M{F(tx)} {F(ty)} L{F(bx + px)} {F(by + py)} L{F(bx - px)} {F(by - py)} Z";
            var box = (Math.Min(tx, Math.Min(bx + px, bx - px)) - 2, Math.Min(ty, Math.Min(by + py, by - py)) - 2,
                       Math.Max(tx, Math.Max(bx + px, bx - px)) + 2, Math.Max(ty, Math.Max(by + py, by - py)) + 2);
            drawn.Add((from, to, amount, count, c, width, head, box));
        }

        // 4. Amounts: on their curve, clear of boxes, heads and amounts already placed, over as few other arrows as possible.
        var taken = nodes.Select(n => (L: n.X - n.W / 2 - 4, T: n.Y - n.H / 2 - 4, R: n.X + n.W / 2 + 4, B: n.Y + n.H / 2 + 4)).Concat(drawn.Select(d => d.HeadBox)).ToList();
        var samples = drawn.Select(d => Enumerable.Range(0, 41).Select(k => d.C.At(k / 40.0)).ToList()).ToList();
        var arrows = new List<DiagramArrow>();
        double top = double.MaxValue, bottom = nodes.Max(n => n.Y + n.H / 2);
        for (var i = 0; i < drawn.Count; i++)
        {
            var d = drawn[i];
            var w = Formatting.Money(d.Amount, "CAD").Length * 7.4 + 14;
            var label = d.C.At(0.5);
            var bestCost = double.MaxValue;
            // On the curve first; when two short arrows run side by side, just beside it (touching it).
            foreach (var t in new[] { 0.5, 0.44, 0.56, 0.38, 0.62, 0.32, 0.68, 0.26, 0.74, 0.2, 0.8 })
            foreach (var side in new[] { 0, 1, -1 })
            {
                var p = d.C.At(t);
                if (side != 0)
                {
                    var (ax, ay) = d.C.At(Math.Max(0, t - 0.01));
                    var (bx2, by2) = d.C.At(Math.Min(1, t + 0.01));
                    var len = Math.Max(1e-6, Math.Sqrt((bx2 - ax) * (bx2 - ax) + (by2 - ay) * (by2 - ay)));
                    // Moved off the line along its normal, far enough that the line runs along the label's edge.
                    double nx = -(by2 - ay) / len, ny = (bx2 - ax) / len;
                    var off = Math.Abs(nx) * (w / 2) + Math.Abs(ny) * 11 + d.Width / 2;
                    p = (p.X + side * nx * off, p.Y + side * ny * off);
                }
                double l = p.X - w / 2 - 3, r = p.X + w / 2 + 3, tp = p.Y - 14, bt = p.Y + 14;
                var cost = Math.Abs(t - 0.5) * 20 + (side == 0 ? 0 : 60)
                    + 1000 * taken.Count(b => r > b.L && l < b.R && bt > b.T && tp < b.B)
                    + 40 * samples.Where((_, j) => j != i).Sum(list => list.Count(q => q.X > l && q.X < r && q.Y > tp && q.Y < bt));
                if (cost < bestCost) { bestCost = cost; label = p; }
            }
            taken.Add((label.X - w / 2, label.Y - 11, label.X + w / 2, label.Y + 11));
            top = Math.Min(top, Math.Min(label.Y - 11, samples[i].Min(q => q.Y) - d.Width / 2));
            bottom = Math.Max(bottom, Math.Max(label.Y + 11, samples[i].Max(q => q.Y) + d.Width / 2));
            arrows.Add(new DiagramArrow(d.From, d.To, d.Amount, d.Count, d.From == OutsideId || d.To == OutsideId, d.C.Svg(), d.Head,
                Math.Round(d.Width, 2), Math.Round(label.X, 1), Math.Round(label.Y, 1)));
        }
        return (new FlowDiagram(W, Math.Ceiling(bottom + Margin), nodes, arrows), arrows.Count == 0 ? Margin : top);
    }

    private enum Sides { Top, Bottom, Left, Right }

    private static readonly Sides[] AllSides = [Sides.Top, Sides.Bottom, Sides.Left, Sides.Right];

    private sealed class End(int arrow, DiagramNode node, DiagramNode other, bool leaving, Sides side, int pairOrder)
    {
        public int Arrow { get; } = arrow;
        public DiagramNode Node { get; } = node;
        public DiagramNode Other { get; } = other;
        public bool Leaving { get; } = leaving;
        public Sides Side { get; } = side;
        /// <summary>Between the same two boxes, the same arrow comes first on both of them, so the two run side by side.</summary>
        public int PairOrder { get; } = pairOrder;
        public double X { get; set; }
        public double Y { get; set; }
        public double Nx { get; set; }
        public double Ny { get; set; }
        public double Spacing { get; set; }
    }

    /// <summary>A cubic curve from (X0, Y0) to (X3, Y3).</summary>
    private readonly record struct Bez(double X0, double Y0, double X1, double Y1, double X2, double Y2, double X3, double Y3)
    {
        public (double X, double Y) At(double t)
        {
            var m = 1 - t;
            return (m * m * m * X0 + 3 * m * m * t * X1 + 3 * m * t * t * X2 + t * t * t * X3,
                    m * m * m * Y0 + 3 * m * m * t * Y1 + 3 * m * t * t * Y2 + t * t * t * Y3);
        }

        public string Svg() => $"M{F(X0)} {F(Y0)} C{F(X1)} {F(Y1)} {F(X2)} {F(Y2)} {F(X3)} {F(Y3)}";
    }

    /// <summary>Leaves (x0, y0) along (n0x, n0y) and comes into (x3, y3) along the opposite of (n3x, n3y): square to both sides.</summary>
    private static Bez Curve(double x0, double y0, double n0x, double n0y, double x3, double y3, double n3x, double n3y)
    {
        var dist = Math.Sqrt((x3 - x0) * (x3 - x0) + (y3 - y0) * (y3 - y0));
        var reach = Math.Clamp(dist * 0.45, 28, 180);
        return new Bez(x0, y0, x0 + n0x * reach, y0 + n0y * reach, x3 + n3x * reach, y3 + n3y * reach, x3, y3);
    }

    /// <summary>A point on a side of a box, <paramref name="along"/> from the side's middle, and the side's outward normal.</summary>
    private static (double X, double Y, double Nx, double Ny) Port(DiagramNode n, Sides side, double along) => side switch
    {
        Sides.Top => (n.X + along, n.Y - n.H / 2, 0, -1),
        Sides.Bottom => (n.X + along, n.Y + n.H / 2, 0, 1),
        Sides.Left => (n.X - n.W / 2, n.Y + along, -1, 0),
        _ => (n.X + n.W / 2, n.Y + along, 1, 0),
    };

    /// <summary>Length of the curve, plus a heavy price for every bit of it inside a box or outside the drawing.</summary>
    private static double Cost(Bez c, List<DiagramNode> nodes, DiagramNode a, DiagramNode b)
    {
        double cost = 0, px = c.X0, py = c.Y0;
        for (var k = 1; k <= 40; k++)
        {
            var (x, y) = c.At(k / 40.0);
            cost += Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            (px, py) = (x, y);
            foreach (var n in nodes)
            {
                // Its own two boxes only must not be crossed; others are kept at a distance.
                var pad = n == a || n == b ? -2 : 14;
                if (Math.Abs(x - n.X) < n.W / 2 + pad && Math.Abs(y - n.Y) < n.H / 2 + pad) cost += 400;
            }
            if (x < 6 || x > W - 6) cost += 200;
        }
        return cost;
    }

    private static int Pair(string from, string to) => string.CompareOrdinal(from, to) < 0 ? 0 : 1;

    /// <summary>The side of a box facing another box (measured in box sizes).</summary>
    private static Sides Natural(DiagramNode n, DiagramNode other)
    {
        double dx = (other.X - n.X) / (n.W / 2), dy = (other.Y - n.Y) / (n.H / 2);
        if (Math.Abs(dy) >= Math.Abs(dx)) return dy >= 0 ? Sides.Bottom : Sides.Top;
        return dx >= 0 ? Sides.Right : Sides.Left;
    }

    private static string Party(string name) => name.EndsWith(" (not imported)", StringComparison.Ordinal) ? name[..^" (not imported)".Length] : name;

    private static string F(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
}
