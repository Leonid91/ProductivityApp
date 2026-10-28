using System.Globalization;
using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.Services;

/// <summary>Who is on the other side of money moved outside the app, and how much went each way.</summary>
public sealed record OutsideParty(string Name, decimal Sent, decimal Received);

/// <summary>An account box (centre X, Y; size W, H), or the outside blob (an ellipse of that size) with its parties.</summary>
public sealed record DiagramNode(string Id, string Name, string Type, bool Outside, double X, double Y, double W, double H, List<OutsideParty> Parties);

/// <summary>
/// One arrow: the total moved from one place to another. <see cref="Path"/> is the curve (SVG path, no fill) ending at the
/// base of <see cref="Head"/>, a filled triangle pointing at the destination. The amount sits at (LabelX, LabelY).
/// </summary>
public sealed record DiagramArrow(string From, string To, decimal Amount, int Count, bool Outside, string Path, string Head, double Width, double LabelX, double LabelY);

/// <summary>
/// The money-moved diagram, laid out once by the engine and drawn the same way on screen and in the PDF: the person's
/// accounts as boxes (name, then type), everything outside the app as one blob below them, and one arrow per direction
/// between two places, as thick as the money.
/// </summary>
public sealed record FlowDiagram(double Width, double Height, List<DiagramNode> Nodes, List<DiagramArrow> Arrows)
{
    public const string OutsideId = "__outside";
    private const double W = 1000, NodeW = 210, NodeH = 60, RowGap = 170, Margin = 14;

    public static FlowDiagram Build(IReadOnlyList<MovementView> movements)
    {
        // Laid out once, then again lower if an arrow arching over two neighbouring accounts would leave the top edge.
        var d = Build(movements, Margin);
        var top = d.Arrows.Count == 0 ? Margin : d.Arrows.Min(a => a.LabelY - 14 - a.Width / 2);
        return top >= Margin ? d : Build(movements, Margin + (Margin - top));
    }

    private static FlowDiagram Build(IReadOnlyList<MovementView> movements, double Top)
    {
        if (movements.Count == 0) return new FlowDiagram(W, 0, [], []);

        // Places: own accounts (by name) and the single outside blob.
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

        // Accounts in rows (up to four a row, the busiest first), the outside blob centred below.
        var accounts = moved.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).ToList();
        var perRow = accounts.Count <= 4 ? Math.Max(1, accounts.Count) : (int)Math.Ceiling(accounts.Count / Math.Ceiling(accounts.Count / 4.0));
        var nodes = new List<DiagramNode>();
        double lastY = Top + NodeH / 2;
        const double Lift = 70;
        for (var i = 0; i < accounts.Count; i++)
        {
            var row = i / perRow;
            var inRow = Math.Min(perRow, accounts.Count - row * perRow);
            var col = i - row * perRow;
            var x = W / inRow * (col + 0.5);
            // A gentle arc: accounts in the middle of a row sit higher, so arrows between neighbours and to the far
            // end of the row come in at different angles instead of piling up along one line.
            var arc = inRow < 3 ? 0 : 1 - Math.Abs(2 * (col + 0.5) / inRow - 1);
            var baseline = Top + NodeH / 2 + (inRow < 3 ? 0 : Lift) + row * (RowGap + Lift);
            var y = baseline - Lift * arc;
            lastY = Math.Max(lastY, baseline);
            nodes.Add(new DiagramNode(accounts[i], accounts[i], types[accounts[i]], false, x, y, NodeW, NodeH, []));
        }
        var height = lastY + NodeH / 2 + Margin;
        if (parties.Count > 0)
        {
            var list = parties.OrderByDescending(p => p.Value.Sent + p.Value.Received).Select(p => new OutsideParty(p.Key, p.Value.Sent, p.Value.Received)).ToList();
            var ry = Math.Max(58, 42 + list.Count * 10);
            var cy = lastY + NodeH / 2 + 150 + ry;
            nodes.Add(new DiagramNode(OutsideId, "Outside the app", "accounts not in the app, other people, investments", true, W / 2, cy, 560, ry * 2, list));
            height = cy + ry + Margin;
        }

        var byId = nodes.ToDictionary(n => n.Id);
        var max = edges.Values.Max(e => e.Amount);
        var arrows = new List<DiagramArrow>();
        foreach (var ((from, to), (amount, count)) in edges.OrderByDescending(e => e.Value.Amount))
        {
            if (from == to) continue;
            var a = byId[from];
            var b = byId[to];
            var width = 2 + 7 * Math.Sqrt((double)(amount / max));
            arrows.Add(Arrow(a, b, amount, count, from == OutsideId || to == OutsideId, width));
        }
        return new FlowDiagram(W, Math.Ceiling(height), nodes, arrows);
    }

    private static string Party(string name) => name.EndsWith(" (not imported)", StringComparison.Ordinal) ? name[..^" (not imported)".Length] : name;

    /// <summary>
    /// A curve bowed to the right of its direction, so the two directions between the same places never overlap;
    /// it leaves and reaches the shapes' edges, and ends in an arrowhead.
    /// </summary>
    private static DiagramArrow Arrow(DiagramNode a, DiagramNode b, decimal amount, int count, bool outside, double width)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        double nx = -dy / len, ny = dx / len;
        var bow = Math.Clamp(len * 0.2, 34, 110);
        double cx = (a.X + b.X) / 2 + nx * bow, cy = (a.Y + b.Y) / 2 + ny * bow;
        var (sx, sy) = Edge(a, cx - a.X, cy - a.Y, 4);
        var (ex, ey) = Edge(b, cx - b.X, cy - b.Y, 3);
        // Arrowhead along the curve's direction at its end.
        double ux = ex - cx, uy = ey - cy;
        var ul = Math.Max(1e-6, Math.Sqrt(ux * ux + uy * uy));
        ux /= ul; uy /= ul;
        var headLen = 11 + width * 1.3;
        var headHalf = 6 + width * 0.8;
        double bx = ex - ux * headLen, by = ey - uy * headLen;
        var head = $"M{F(ex)} {F(ey)} L{F(bx - uy * headHalf)} {F(by + ux * headHalf)} L{F(bx + uy * headHalf)} {F(by - ux * headHalf)} Z";
        var path = $"M{F(sx)} {F(sy)} Q{F(cx)} {F(cy)} {F(bx)} {F(by)}";
        double lx = 0.25 * sx + 0.5 * cx + 0.25 * ex, ly = 0.25 * sy + 0.5 * cy + 0.25 * ey;
        return new DiagramArrow(a.Id, b.Id, amount, count, outside, path, head, Math.Round(width, 2), Math.Round(lx, 1), Math.Round(ly, 1));
    }

    /// <summary>Where a line from the centre of a shape, in direction (vx, vy), leaves it (plus a small gap).</summary>
    private static (double X, double Y) Edge(DiagramNode n, double vx, double vy, double gap)
    {
        var l = Math.Sqrt(vx * vx + vy * vy);
        if (l < 1e-6) return (n.X, n.Y);
        vx /= l; vy /= l;
        double t;
        if (n.Outside)
        {
            double rx = n.W / 2, ry = n.H / 2;
            t = 1 / Math.Sqrt(vx * vx / (rx * rx) + vy * vy / (ry * ry));
        }
        else
        {
            var tx = Math.Abs(vx) < 1e-6 ? double.MaxValue : n.W / 2 / Math.Abs(vx);
            var ty = Math.Abs(vy) < 1e-6 ? double.MaxValue : n.H / 2 / Math.Abs(vy);
            t = Math.Min(tx, ty);
        }
        return (n.X + vx * (t + gap), n.Y + vy * (t + gap));
    }

    private static string F(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
}
