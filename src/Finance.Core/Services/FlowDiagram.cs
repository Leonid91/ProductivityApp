using System.Globalization;
using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.Services;

/// <summary>Who is on the other side of money moved outside the app, and how much went each way.</summary>
public sealed record OutsideParty(string Name, decimal Sent, decimal Received);

/// <summary>An account box, or the outside box, centred on (X, Y) with size W, H; the outside box lists who is behind it.</summary>
/// <remarks>Inside its bank's box an account is named without the bank's name it starts with; <see cref="Title"/> is its full name.</remarks>
public sealed record DiagramNode(string Id, string Name, string Type, bool Outside, double X, double Y, double W, double H, List<OutsideParty> Parties, string? Title = null);

/// <summary>
/// One arrow: the total moved from one place to another. <see cref="Path"/> is the curve (SVG path, no fill) ending at the
/// base of <see cref="Head"/>, a filled triangle pointing at the destination. The amount sits at (LabelX, LabelY).
/// </summary>
public sealed record DiagramArrow(string From, string To, decimal Amount, int Count, bool Outside, string Path, string Head, double Width, double LabelX, double LabelY);

/// <summary>A bank: a box of the same shape as the accounts, larger, its name at the top, holding its accounts; centred on (X, Y).</summary>
public sealed record DiagramBank(string Name, double X, double Y, double W, double H);

/// <summary>
/// The money-moved diagram, laid out once by the engine and drawn the same way on screen and in the PDF: every bank
/// as a box holding its accounts (name, then type), everything outside the app as one box of the size of an account
/// below them, and one arrow per direction between two places, as thick as the money: from an account to another of the
/// same bank inside the bank's box, from an account of one bank to an account of another between the two banks.
/// </summary>
/// <remarks>
/// Every arrow has its own point on the side of each box it touches (no two arrows share a point, coming or going),
/// leaves and reaches the box square to that side, and goes around the other boxes. Points of one side are ordered by
/// where their arrows go, so arrows between the same two boxes run side by side instead of over each other. Amounts
/// sit where they cover no box, no arrowhead and as little of the other arrows as possible.
/// </remarks>
public sealed record FlowDiagram(double Width, double Height, List<DiagramNode> Nodes, List<DiagramArrow> Arrows, List<DiagramBank> Banks)
{
    public const string OutsideId = "__outside";
    private const double W = 1000, MinNodeW = 210, NodeH = 60, OutsideGap = 170, Margin = 14, CharW = 8;

    /// <summary>Inside a bank: room around its accounts (its name above them) and between them, for the arrows.</summary>
    private const double PadX = 28, PadTop = 46, PadBottom = 28, GapX = 90, GapY = 96;

    /// <summary>Between two banks side by side, and between two rows of banks.</summary>
    private const double BankGap = 130, BankRowGap = 150;

    /// <summary>Banks in two columns with the outside box between them: room on each side of it, and between two rows.</summary>
    private const double SideGap = 80, MiddleRowGap = 80;

    public static FlowDiagram Build(IReadOnlyList<MovementView> movements)
    {
        // Laid out, then again with taller or wider boxes when the arrows of one side need more room than it has (every
        // box keeps the same size), and lower when an arrow or a label would leave the top edge. Only when a box can
        // grow no wider are all arrows drawn a little thinner, the same for all, so their thickness still follows the money.
        var (top, h, w, k) = (Margin, NodeH, 0.0, 1.0);
        Layout r = default;
        for (var round = 0; round < 8; round++)
        {
            r = Build(movements, top, h, w, k);
            if (r.NeedH > h + 0.5) { h = Math.Min(r.NeedH, MaxNodeH); if (r.NeedH > MaxNodeH) k *= Shrink(r.NeedH - 16, MaxNodeH - 16); continue; }
            if (r.NeedW > r.NodeW + 0.5)
            {
                if (r.NeedW <= r.MaxW) w = r.NeedW;
                else { w = r.MaxW; k *= Shrink(r.NeedW - 40, r.MaxW - 40); }
                continue;
            }
            if (r.Top < Margin) { top += Margin - r.Top; continue; }
            break;
        }
        return r.Diagram;
    }

    private const double MaxNodeH = 132;

    private static double Shrink(double need, double have) => Math.Clamp(have / need, 0.4, 0.98);

    private readonly record struct Layout(FlowDiagram Diagram, double Top, double NodeW, double MaxW, double NeedW, double NeedH);

    /// <summary>How thick an arrow is (before any shrinking): from 2.5 for the smallest amount to 11 for the largest.</summary>
    private static double Size(decimal amount, decimal max) => 2.5 + 8.5 * Math.Sqrt((double)(amount / max));

    /// <summary>Half the base and the length of an arrowhead for an arrow of size <paramref name="s"/>: large enough to see.</summary>
    private static (double Half, double Length) HeadSize(double s, double k) => (Math.Max(7, k * (8 + 0.7 * s)), Math.Max(12, k * (15 + 1.3 * s)));

    /// <summary>Space between two neighbouring ends on one side of a box.</summary>
    private const double EndGap = 7;

    /// <summary>
    /// How many characters of a name fit in a box of width <paramref name="w"/>; longer names are cut with "…" by the
    /// drawings (screen and PDF), the full name shows on hover.
    /// </summary>
    public static string Fit(string name, double w)
    {
        var max = (int)Math.Floor((w - 16) / CharW);
        return name.Length <= max ? name : name[..Math.Max(1, max - 1)].TrimEnd() + "…";
    }

    /// <param name="NodeH">Height of every box.</param>
    /// <param name="MinW">Width every box has at least (to give the arrows of a side their room).</param>
    /// <param name="K">How much thinner than their full size all arrows are drawn (1 = full size).</param>
    private static Layout Build(IReadOnlyList<MovementView> movements, double Top, double NodeH, double MinW, double K)
    {
        if (movements.Count == 0) return new Layout(new FlowDiagram(W, 0, [], [], []), Margin, 0, 0, 0, 0);

        // Places: own accounts (each account its own box, even when two accounts of two banks have one name) and the
        // single outside box.
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var bankOf = new Dictionary<string, string>(StringComparer.Ordinal);
        static string Key(long? account, string name) => account is { } id ? "a" + id.ToString(CultureInfo.InvariantCulture) : "n:" + name;
        var moved = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var edges = new Dictionary<(string From, string To), (decimal Amount, int Count)>();
        var parties = new Dictionary<string, (decimal Sent, decimal Received)>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in movements)
        {
            var from = m.FromOutside ? OutsideId : Key(m.FromAccount, m.From);
            var to = m.ToOutside ? OutsideId : Key(m.ToAccount, m.To);
            if (!m.FromOutside) { types.TryAdd(from, m.FromType ?? ""); names.TryAdd(from, m.From); bankOf.TryAdd(from, m.FromBank ?? Movements.OtherAccounts); moved[from] = moved.GetValueOrDefault(from) + m.Amount; }
            if (!m.ToOutside) { types.TryAdd(to, m.ToType ?? ""); names.TryAdd(to, m.To); bankOf.TryAdd(to, m.ToBank ?? Movements.OtherAccounts); moved[to] = moved.GetValueOrDefault(to) + m.Amount; }
            var e = edges.GetValueOrDefault((from, to));
            edges[(from, to)] = (e.Amount + m.Amount, e.Count + 1);
            if (m.ToOutside) { var p = parties.GetValueOrDefault(Party(m.To)); parties[Party(m.To)] = (p.Sent + m.Amount, p.Received); }
            if (m.FromOutside) { var p = parties.GetValueOrDefault(Party(m.From)); parties[Party(m.From)] = (p.Sent, p.Received + m.Amount); }
        }

        // Inside its bank, an account named after it ("Caisse d'Epargne account •••• 8901") is named without the bank
        // ("Account •••• 8901"); its full name shows on hover.
        var titles = new Dictionary<string, string>(names, StringComparer.Ordinal);
        foreach (var key in names.Keys.ToList())
        {
            var bank = bankOf[key];
            var name = names[key];
            if (bank != Movements.OtherAccounts && name.Length > bank.Length + 1 && name.StartsWith(bank + " ", StringComparison.OrdinalIgnoreCase))
            {
                var rest = name[(bank.Length + 1)..].TrimStart();
                names[key] = char.ToUpperInvariant(rest[0]) + rest[1..];
            }
        }

        // Banks, the busiest first, each holding its accounts (the busiest first) in rows of up to two (three from five
        // accounts on). Every account box has the same size: wide enough for the longest name, as far as the widest bank
        // allows. From three banks on, when they fit, they sit in two columns with the outside box between them, every
        // account a short way from it; otherwise side by side as far as the width allows, then on further rows, the
        // outside box below.
        var accounts = moved.OrderByDescending(x => x.Value).ThenBy(x => names[x.Key], StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).ToList();
        var groups = accounts.GroupBy(a => bankOf[a], StringComparer.Ordinal)
            .Select(g => (Bank: g.Key, Accounts: g.ToList(), Moved: g.Sum(a => moved[a])))
            .OrderByDescending(g => g.Moved).ThenBy(g => g.Bank, StringComparer.Ordinal).ToList();
        static int Cols(int n) => n <= 1 ? 1 : n <= 4 ? 2 : 3;
        var widest = groups.Max(g => Cols(g.Accounts.Count));
        var maxW = Math.Max(MinNodeW, Math.Min(420, (W - 2 * Margin - 2 * PadX - (widest - 1) * GapX) / widest));
        var longest = accounts.Select(a => names[a]).Append("Outside the app").Max(a => a.Length);
        var nodeW = Math.Max(Math.Clamp(longest * CharW + 28, MinNodeW, maxW), Math.Min(MinW, maxW));
        var sizes = groups.Select(g =>
        {
            var cols = Cols(g.Accounts.Count);
            var rows = (int)Math.Ceiling(g.Accounts.Count / (double)cols);
            // Wide enough for its name too.
            var w = Math.Max(cols * nodeW + (cols - 1) * GapX + 2 * PadX, g.Bank.Length * 8.6 + 2 * PadX);
            return (W: Math.Min(w, W - 2 * Margin), H: PadTop + rows * NodeH + (rows - 1) * GapY + PadBottom, Cols: cols);
        }).ToList();
        // Where each bank goes (its centre), then the outside box.
        var place = new (double X, double Y)[groups.Count];
        double outsideX = W / 2, outsideY;
        var bottomOfBanks = Top;
        var leftW = groups.Where((_, g) => g % 2 == 0).Max(g => sizes[groups.IndexOf(g)].W);
        var rightW = groups.Count > 1 ? groups.Where((_, g) => g % 2 == 1).Max(g => sizes[groups.IndexOf(g)].W) : 0;
        var middle = parties.Count > 0 && groups.Count >= 3 && leftW + rightW + nodeW + 2 * SideGap <= W - 2 * Margin;
        if (middle)
        {
            // Two columns of banks, the outside box between them, half way down.
            double y0 = Top;
            for (var g = 0; g < groups.Count; g += 2)
            {
                var rowH = Math.Max(sizes[g].H, g + 1 < groups.Count ? sizes[g + 1].H : 0);
                place[g] = (W / 2 - nodeW / 2 - SideGap - sizes[g].W / 2, y0 + sizes[g].H / 2);
                if (g + 1 < groups.Count) place[g + 1] = (W / 2 + nodeW / 2 + SideGap + sizes[g + 1].W / 2, y0 + sizes[g + 1].H / 2);
                bottomOfBanks = y0 + rowH;
                y0 += rowH + MiddleRowGap;
            }
            outsideY = (Top + bottomOfBanks) / 2;
        }
        else
        {
            // The last row: the outside box between two banks of one column of accounts, or beside one, when they fit;
            // every other bank on the rows above it, as many side by side as fit.
            var narrow = Enumerable.Range(0, groups.Count).Where(g => sizes[g].Cols == 1).ToList();
            var room = W - 2 * Margin;
            List<int> tail = parties.Count == 0 ? []
                : narrow.Count >= 2 && sizes[narrow[^2]].W + sizes[narrow[^1]].W + nodeW + 2 * SideGap <= room ? [narrow[^2], narrow[^1]]
                : narrow.Count >= 1 && sizes[narrow[^1]].W + nodeW + SideGap <= room ? [narrow[^1]]
                : [];
            var rest = Enumerable.Range(0, groups.Count).Where(g => !tail.Contains(g)).ToList();
            var y0 = Top;
            for (var n = 0; n < rest.Count;)
            {
                // One row of banks: as many as fit.
                var row = new List<int> { rest[n] };
                var width = sizes[rest[n]].W;
                while (n + row.Count < rest.Count && width + BankGap + sizes[rest[n + row.Count]].W <= room)
                {
                    width += BankGap + sizes[rest[n + row.Count]].W;
                    row.Add(rest[n + row.Count]);
                }
                var x0 = (W - width) / 2;
                foreach (var r in row)
                {
                    place[r] = (x0 + sizes[r].W / 2, y0 + sizes[r].H / 2);
                    x0 += sizes[r].W + BankGap;
                }
                var rowH = row.Max(r => sizes[r].H);
                bottomOfBanks = y0 + rowH;
                y0 += rowH + BankRowGap;
                n += row.Count;
            }
            if (tail.Count == 2)
            {
                var (l, r) = (tail[0], tail[1]);
                var rowH = Math.Max(sizes[l].H, sizes[r].H);
                place[l] = (W / 2 - nodeW / 2 - SideGap - sizes[l].W / 2, y0 + sizes[l].H / 2);
                place[r] = (W / 2 + nodeW / 2 + SideGap + sizes[r].W / 2, y0 + sizes[r].H / 2);
                outsideY = y0 + rowH / 2;
                bottomOfBanks = y0 + rowH;
            }
            else if (tail.Count == 1)
            {
                var t = tail[0];
                var x0 = (W - (sizes[t].W + SideGap + nodeW)) / 2;
                place[t] = (x0 + sizes[t].W / 2, y0 + sizes[t].H / 2);
                outsideX = x0 + sizes[t].W + SideGap + nodeW / 2;
                outsideY = y0 + sizes[t].H / 2;
                bottomOfBanks = y0 + sizes[t].H;
            }
            else outsideY = bottomOfBanks + OutsideGap + NodeH / 2;
        }

        var nodes = new List<DiagramNode>();
        var banks = new List<DiagramBank>();
        var bankBox = new Dictionary<string, DiagramBank>(StringComparer.Ordinal);
        for (var g = 0; g < groups.Count; g++)
        {
            var (bw, bh, cols) = sizes[g];
            var bank = new DiagramBank(groups[g].Bank, place[g].X, place[g].Y, bw, bh);
            banks.Add(bank);
            var list = groups[g].Accounts;
            var bankTop = bank.Y - bh / 2;
            for (var a = 0; a < list.Count; a++)
            {
                var r = a / cols;
                var c = a % cols;
                // The last row, when not full, centred in its bank.
                var inRow = Math.Min(cols, list.Count - r * cols);
                var rowW = inRow * nodeW + (inRow - 1) * GapX;
                var ax = bank.X - rowW / 2 + nodeW / 2 + c * (nodeW + GapX);
                var ay = bankTop + PadTop + NodeH / 2 + r * (NodeH + GapY);
                nodes.Add(new DiagramNode(list[a], names[list[a]], types[list[a]], false, ax, ay, nodeW, NodeH, [], titles[list[a]]));
                bankBox[list[a]] = bank;
            }
        }
        if (parties.Count > 0)
        {
            var list = parties.OrderByDescending(p => p.Value.Sent + p.Value.Received).Select(p => new OutsideParty(p.Key, p.Value.Sent, p.Value.Received)).ToList();
            nodes.Add(new DiagramNode(OutsideId, "Outside the app", "Other accounts & people", true, outsideX, outsideY, nodeW, NodeH, list));
        }

        var byId = nodes.ToDictionary(n => n.Id);
        var max = edges.Values.Max(e => e.Amount);
        var links = edges.Where(e => e.Key.From != e.Key.To).OrderByDescending(e => e.Value.Amount)
            .ThenBy(e => e.Key.From, StringComparer.Ordinal).ThenBy(e => e.Key.To, StringComparer.Ordinal).ToList();

        // Every arrow as thick as its money, its head in proportion (and never too small to see).
        var size = links.Select(l => Size(l.Value.Amount, max)).ToArray();

        // 1. Sides: for each arrow, the pair of sides giving the shortest curve that goes through no other box. A side
        // already used by other arrows costs a little more (the short sides of a box much more), so arrows spread out.
        var ends = new List<End>();
        var used = new Dictionary<(string, Sides), int>();
        // How far each arrow's curve reaches out of its boxes (null: by the distance between them). A shallower curve
        // runs between two rows of accounts, or between accounts and the edge of their bank, where a deep one cannot.
        var reachOf = new double?[links.Count];
        // An arrow that cannot reach its box in one curve without crossing another box bends once in a gap between
        // banks (beside a bank, or between two rows of banks).
        var viaOf = new Via?[links.Count];
        var vias = Gaps(banks, nodes.FirstOrDefault(n => n.Outside));
        double Crowd(DiagramNode n, Sides side) => used.GetValueOrDefault((n.Id, side)) * (side is Sides.Left or Sides.Right ? 45 : 8);
        for (var i = 0; i < links.Count; i++)
        {
            var (from, to) = links[i].Key;
            var (a, b) = (byId[from], byId[to]);
            var best = (Cost: double.MaxValue, A: Sides.Bottom, B: Sides.Top, Reach: (double?)null);
            foreach (var sa in AllSides)
                foreach (var sb in AllSides)
                    foreach (var reach in Reaches)
                    {
                        var (ax, ay, anx, any) = Port(a, sa, 0);
                        var (bx, by, bnx, bny) = Port(b, sb, 0);
                        var c = Route.Of(ax, ay, anx, any, bx, by, bnx, bny, reach, null);
                        var cost = Cost(c, nodes, a, b, banks, bankBox) + (sa == Natural(a, b) ? 0 : 6) + (sb == Natural(b, a) ? 0 : 6) + Crowd(a, sa) + Crowd(b, sb)
                            + (reach is null ? 0 : 4);
                        if (cost < best.Cost) best = (cost, sa, sb, reach);
                    }
            reachOf[i] = best.Reach;
            // Crossing something: through a gap between banks, when that crosses less.
            if (best.Cost >= 400)
            {
                var bestVia = (Cost: best.Cost, A: best.A, B: best.B, Via: (Via?)null);
                foreach (var via in vias)
                    foreach (var sa in AllSides)
                        foreach (var sb in AllSides)
                        {
                            var (ax, ay, anx, any) = Port(a, sa, 0);
                            var (bx, by, bnx, bny) = Port(b, sb, 0);
                            var c = Route.Of(ax, ay, anx, any, bx, by, bnx, bny, null, via);
                            var at = new DiagramNode("", "", "", false, via.X, via.Y, 1, 1, []);
                            var cost = Cost(c, nodes, a, b, banks, bankBox) + 20 + Crowd(a, sa) + Crowd(b, sb)
                                + (sa == Natural(a, at) ? 0 : 6) + (sb == Natural(b, at) ? 0 : 6);
                            if (cost < bestVia.Cost) bestVia = (cost, sa, sb, via);
                        }
                if (bestVia.Via is not null)
                {
                    best = (bestVia.Cost, bestVia.A, bestVia.B, null);
                    reachOf[i] = null;
                    viaOf[i] = bestVia.Via;
                }
            }
            var (half, _) = HeadSize(size[i], K);
            ends.Add(new End(i, a, b, true, best.A, Pair(from, to)) { Room = K * size[i] });
            ends.Add(new End(i, b, a, false, best.B, Pair(from, to)) { Room = 2 * half });
            used[(a.Id, best.A)] = used.GetValueOrDefault((a.Id, best.A)) + 1;
            used[(b.Id, best.B)] = used.GetValueOrDefault((b.Id, best.B)) + 1;
        }

        // 2. Points: each end its own stretch of its side, as wide as its line (leaving) or its head (arriving), ordered
        // by where the arrow goes, with the spare room shared out between them. A side too short for its arrows asks
        // for larger boxes.
        double needW = 0, needH = 0;
        foreach (var group in ends.GroupBy(e => (e.Node.Id, e.Side)))
        {
            var horizontal = group.Key.Side is Sides.Top or Sides.Bottom;
            var ordered = group.OrderBy(e => horizontal ? e.Other.X : e.Other.Y).ThenBy(e => horizontal ? e.Other.Y : e.Other.X).ThenBy(e => e.PairOrder).ToList();
            var span = horizontal ? nodeW - 40 : NodeH - 16;
            var need = ordered.Sum(e => e.Room) + EndGap * (ordered.Count - 1);
            if (horizontal) needW = Math.Max(needW, need + 40); else needH = Math.Max(needH, need + 16);
            var gap = Math.Max(EndGap, (span - ordered.Sum(e => e.Room)) / (ordered.Count + 1));
            var at = -(ordered.Sum(e => e.Room) + gap * (ordered.Count - 1)) / 2;
            foreach (var e in ordered)
            {
                (e.X, e.Y, e.Nx, e.Ny) = Port(e.Node, e.Side, at + e.Room / 2);
                at += e.Room + gap;
            }
        }

        // Arrows bending in the same gap run side by side there, in the order of where they go, never through one point.
        var spread = new Via?[links.Count];
        // One gap: the same line up and down (or across), whatever the point each arrow bends at.
        foreach (var group in Enumerable.Range(0, links.Count).Where(i => viaOf[i] is not null)
                     .GroupBy(i => (viaOf[i]!.Value.Vertical, Math.Round(viaOf[i]!.Value.Vertical ? viaOf[i]!.Value.X : viaOf[i]!.Value.Y))))
        {
            var v = viaOf[group.First()]!.Value;
            // The account each arrow goes to (or comes from, for the outside). Running up or down a gap, the arrows
            // leaving it to the left come first, the one leaving lowest outermost; then those leaving to the right, the
            // one leaving lowest outermost: none crosses another. Across a gap, the same turned a quarter.
            DiagramNode End(int i) => byId[links[i].Key.To].Outside ? byId[links[i].Key.From] : byId[links[i].Key.To];
            var order = (v.Vertical
                ? group.OrderBy(i => End(i).X < v.X ? 0 : 1).ThenBy(i => End(i).X < v.X ? -End(i).Y : End(i).Y)
                : group.OrderBy(i => End(i).Y < v.Y ? 0 : 1).ThenBy(i => End(i).Y < v.Y ? -End(i).X : End(i).X)).ToList();
            var room = order.Sum(i => K * size[i]) + 12 * (order.Count - 1);
            var at = -room / 2;
            foreach (var i in order)
            {
                var shift = at + K * size[i] / 2;
                spread[i] = v.Vertical ? viaOf[i]!.Value with { X = v.X + shift } : viaOf[i]!.Value with { Y = v.Y + shift };
                at += K * size[i] + 12;
            }
        }

        // 3. Curves and heads.
        var drawn = new List<(string From, string To, decimal Amount, int Count, Route C, double Width, string Head, (double L, double T, double R, double B) HeadBox)>();
        for (var i = 0; i < links.Count; i++)
        {
            var ((from, to), (amount, count)) = links[i];
            var s = ends.First(e => e.Arrow == i && e.Leaving);
            var e = ends.First(e => e.Arrow == i && !e.Leaving);
            var width = K * size[i];
            const double Gap = 3;
            var (headHalf, headLen) = HeadSize(size[i], K);
            double tx = e.X + e.Nx * Gap, ty = e.Y + e.Ny * Gap;
            double bx = tx + e.Nx * headLen, by = ty + e.Ny * headLen;
            var c = Route.Of(s.X + s.Nx * Gap, s.Y + s.Ny * Gap, s.Nx, s.Ny, bx, by, e.Nx, e.Ny, reachOf[i], spread[i]);
            // The head points into the box, square to its side.
            double px = e.Ny * headHalf, py = -e.Nx * headHalf;
            var head = $"M{F(tx)} {F(ty)} L{F(bx + px)} {F(by + py)} L{F(bx - px)} {F(by - py)} Z";
            var box = (Math.Min(tx, Math.Min(bx + px, bx - px)) - 2, Math.Min(ty, Math.Min(by + py, by - py)) - 2,
                       Math.Max(tx, Math.Max(bx + px, bx - px)) + 2, Math.Max(ty, Math.Max(by + py, by - py)) + 2);
            drawn.Add((from, to, amount, count, c, width, head, box));
        }

        // 4. Amounts: on their curve, clear of boxes, heads and amounts already placed, over as few other arrows as possible.
        var taken = nodes.Select(n => (L: n.X - n.W / 2 - 4, T: n.Y - n.H / 2 - 4, R: n.X + n.W / 2 + 4, B: n.Y + n.H / 2 + 4))
            // The banks' names stay readable.
            .Concat(banks.Select(b => (L: b.X - b.W / 2, T: b.Y - b.H / 2, R: b.X + b.W / 2, B: b.Y - b.H / 2 + PadTop - 6)))
            .Concat(drawn.Select(d => d.HeadBox)).ToList();
        var samples = drawn.Select(d => Enumerable.Range(0, 41).Select(k => d.C.At(k / 40.0)).ToList()).ToList();
        var arrows = new List<DiagramArrow>();
        double top = double.MaxValue, bottom = Math.Max(nodes.Max(n => n.Y + n.H / 2), banks.Count == 0 ? 0 : banks.Max(b => b.Y + b.H / 2));
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
        return new Layout(new FlowDiagram(W, Math.Ceiling(bottom + Margin), nodes, arrows, banks), arrows.Count == 0 ? Margin : Math.Min(top, Top), nodeW, maxW, needW, needH);
    }

    private enum Sides { Top, Bottom, Left, Right }

    /// <summary>The reaches tried for each arrow (see <c>reachOf</c>).</summary>
    private static readonly double?[] Reaches = [null, 40, 65, 100, 150, 220];

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
        /// <summary>How much of its side the end takes: the width of its line, or of its head.</summary>
        public double Room { get; init; }
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

    /// <summary>A point in a gap between banks an arrow can bend at, and the way it runs there (up or down, or across).</summary>
    private readonly record struct Via(double X, double Y, bool Vertical);

    /// <summary>The middle of every gap beside a bank (running up and down) and above and below it (running across).</summary>
    private static List<Via> Gaps(List<DiagramBank> banks, DiagramNode? outside)
    {
        var list = new List<Via>();
        foreach (var b in banks)
        {
            double left = b.X - b.W / 2, right = b.X + b.W / 2, top = b.Y - b.H / 2, bottom = b.Y + b.H / 2;
            var below = outside is not null && outside.Y > bottom && banks.All(o => o.Y - o.H / 2 < bottom + 1)
                ? bottom + (outside.Y - outside.H / 2 - bottom) / 2 : bottom + BankRowGap / 2;
            foreach (var x in new[] { left - BankGap / 2, right + BankGap / 2 })
                if (x > 10 && x < W - 10) list.Add(new Via(x, b.Y, true));
            foreach (var x in new[] { b.X, left - BankGap / 2, right + BankGap / 2 })
            {
                if (x <= 10 || x >= W - 10) continue;
                list.Add(new Via(x, below, false));
                if (top - BankRowGap / 2 > Margin) list.Add(new Via(x, top - BankRowGap / 2, false));
            }
        }
        return [.. list.Distinct()];
    }

    /// <summary>An arrow's line: one curve, or two meeting smoothly at a point in a gap between banks.</summary>
    private readonly record struct Route(Bez A, Bez? B)
    {
        public (double X, double Y) At(double t) => B is not { } b ? A.At(t) : t < 0.5 ? A.At(t * 2) : b.At(t * 2 - 1);

        public string Svg() => B is not { } b ? A.Svg() : A.Svg() + $" C{F(b.X1)} {F(b.Y1)} {F(b.X2)} {F(b.Y2)} {F(b.X3)} {F(b.Y3)}";

        public double X0 => A.X0;

        public double Y0 => A.Y0;

        /// <summary>From (x0, y0) along (n0x, n0y) into (x3, y3) against (n3x, n3y), through <paramref name="via"/> when given.</summary>
        public static Route Of(double x0, double y0, double n0x, double n0y, double x3, double y3, double n3x, double n3y, double? reach, Via? via)
        {
            if (via is not { } v) return new Route(Curve(x0, y0, n0x, n0y, x3, y3, n3x, n3y, reach), null);
            // Through the gap the way it runs, towards the arrow's end.
            double tx = v.Vertical ? 0 : Math.Sign(x3 - x0) is var sx && sx != 0 ? sx : 1;
            double ty = v.Vertical ? (Math.Sign(y3 - y0) is var sy && sy != 0 ? sy : 1) : 0;
            var r1 = Math.Clamp(Math.Sqrt((v.X - x0) * (v.X - x0) + (v.Y - y0) * (v.Y - y0)) * 0.45, 28, 160);
            var r2 = Math.Clamp(Math.Sqrt((x3 - v.X) * (x3 - v.X) + (y3 - v.Y) * (y3 - v.Y)) * 0.45, 28, 160);
            return new Route(new Bez(x0, y0, x0 + n0x * r1, y0 + n0y * r1, v.X - tx * r1, v.Y - ty * r1, v.X, v.Y),
                new Bez(v.X, v.Y, v.X + tx * r2, v.Y + ty * r2, x3 + n3x * r2, y3 + n3y * r2, x3, y3));
        }
    }

    /// <summary>Leaves (x0, y0) along (n0x, n0y) and comes into (x3, y3) along the opposite of (n3x, n3y): square to both sides.</summary>
    private static Bez Curve(double x0, double y0, double n0x, double n0y, double x3, double y3, double n3x, double n3y, double? reachOut = null)
    {
        var dist = Math.Sqrt((x3 - x0) * (x3 - x0) + (y3 - y0) * (y3 - y0));
        var reach = reachOut ?? Math.Clamp(dist * 0.45, 28, 180);
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

    /// <summary>
    /// Length of the curve, plus a heavy price for every bit of it inside a box or outside the drawing; between two
    /// accounts of one bank, for every bit outside that bank; between two banks, for every bit inside a third bank or
    /// over a bank's name.
    /// </summary>
    private static double Cost(Route c, List<DiagramNode> nodes, DiagramNode a, DiagramNode b, List<DiagramBank> banks, Dictionary<string, DiagramBank> bankBox)
    {
        var bankA = bankBox.GetValueOrDefault(a.Id);
        var bankB = bankBox.GetValueOrDefault(b.Id);
        var sameBank = bankA is not null && bankA == bankB;
        double cost = 0, px = c.X0, py = c.Y0;
        var steps = c.B is null ? 40 : 60;
        for (var k = 1; k <= steps; k++)
        {
            var (x, y) = c.At(k / (double)steps);
            var step = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            cost += step;
            (px, py) = (x, y);
            foreach (var n in nodes)
            {
                // Its own two boxes only must not be crossed; others are kept at a distance.
                var pad = n == a || n == b ? -2 : 14;
                if (Math.Abs(x - n.X) < n.W / 2 + pad && Math.Abs(y - n.Y) < n.H / 2 + pad) cost += 400;
            }
            if (x < 6 || x > W - 6) cost += 200;
            if (sameBank && (Math.Abs(x - bankA!.X) > bankA.W / 2 - 6 || Math.Abs(y - bankA.Y) > bankA.H / 2 - 6)) cost += 400;
            foreach (var bank in banks)
            {
                // Another bank is gone around, as an account is, and kept at a distance (never along its edge).
                var other = bank != bankA && bank != bankB;
                var pad = other && !sameBank ? 14 : 0;
                if (Math.Abs(x - bank.X) >= bank.W / 2 + pad || Math.Abs(y - bank.Y) >= bank.H / 2 + pad) continue;
                if (other && !sameBank) cost += 400;
                // Leaving its own bank (for another bank or the outside): out of it by the shortest way.
                else if (!sameBank) cost += 2.5 * step;
                if (y < bank.Y - bank.H / 2 + PadTop - 8) cost += 120;
            }
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
