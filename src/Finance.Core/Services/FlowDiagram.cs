using System.Text;
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
/// <remarks><see cref="Lost"/>: what the banks kept on the international transfers of the arrow (their rate and fees), or null.</remarks>
public sealed record DiagramArrow(string From, string To, decimal Amount, int Count, bool Outside, string Path, string Head, double Width, double LabelX, double LabelY,
    decimal? Lost = null);

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
    private const double W = 1000, MinNodeW = 210, NodeH = 60, BaseOutsideGap = 170, Margin = 14, CharW = 8;

    /// <summary>Inside a bank: room around its accounts (its name above them) and between them, for the arrows.</summary>
    private const double PadX = 28, PadTop = 46, PadBottom = 28, BaseGapX = 130, BaseGapY = 104;

    /// <summary>Between two banks side by side, and between two rows of banks.</summary>
    private const double BaseBankGap = 130, BaseBankRowGap = 150;

    /// <summary>Banks in two columns with the outside box between them: room on each side of it, and between two rows.</summary>
    private const double BaseSideGap = 80, BaseMiddleRowGap = 80;

    public static FlowDiagram Build(IReadOnlyList<MovementView> movements)
    {
        // Laid out, then again with taller or wider boxes when the arrows of one side need more room than it has (every
        // box keeps the same size), and lower when an arrow or a label would leave the top edge. Only when a box can
        // grow no wider are all arrows drawn a little thinner, the same for all, so their thickness still follows the money.
        // Several layouts are drawn when there are several banks (orders of the banks, the outside box between them or
        // below): the one with the fewest arrows crossing, lines through boxes and crowded amounts is kept.
        var key = Signature(movements);
        lock (Remembered) { if (Remembered.TryGetValue(key, out var known)) return known; }
        // Each candidate drawn once as it is to be scored (side by side on the processor's cores); the best one then gets
        // the room it needs.
        var first = Build(movements, Margin, NodeH, 0, 1, 1, 0);
        var scores = new double[first.Candidates];
        scores[0] = first.Score;
        Parallel.For(1, first.Candidates, c => scores[c] = Build(movements, Margin, NodeH, 0, 1, 1, c).Score);
        // The best one laid out with its room; when it stays crowded (a line through a box, an amount without a free
        // place), the next best, until one is clear (or the best of them).
        Layout? kept = null;
        foreach (var c in Enumerable.Range(0, scores.Length).OrderBy(c => scores[c]).Take(3))
        {
            var settled = Settle(movements, c);
            if (kept is null || (kept.Value.Crowded && !settled.Crowded) || (settled.Crowded == kept.Value.Crowded && settled.Score < kept.Value.Score)) kept = settled;
            if (!settled.Crowded) break;
        }
        var diagram = kept!.Value.Diagram;
        lock (Remembered)
        {
            if (Remembered.Count >= 200) Remembered.Clear();
            Remembered[key] = diagram;
        }
        return diagram;
    }

    /// <summary>Diagrams already laid out, by what they show (a period seen again is not laid out again).</summary>
    private static readonly Dictionary<string, FlowDiagram> Remembered = new(StringComparer.Ordinal);

    /// <summary>Everything a diagram is made from, as one text.</summary>
    private static string Signature(IReadOnlyList<MovementView> movements)
    {
        var sb = new StringBuilder();
        foreach (var m in movements)
            sb.Append(CultureInfo.InvariantCulture, $"{m.From}|{m.To}|{m.FromOutside}|{m.ToOutside}|{m.FromAccount}|{m.ToAccount}|{m.FromBank}|{m.ToBank}|{m.FromType}|{m.ToType}|{m.Amount}|{m.Lost}\n");
        return sb.ToString();
    }

    /// <summary>One candidate layout, laid out again until its boxes and arrows have their room.</summary>
    private static Layout Settle(IReadOnlyList<MovementView> movements, int candidate)
    {
        var (top, h, w, k, room) = (Margin, NodeH, 0.0, 1.0, 1.0);
        Layout r = default;
        LastRounds = 0;
        for (var round = 0; round < 14; round++)
        {
            LastRounds++;
            r = Build(movements, top, h, w, k, room, candidate);
            // An amount that finds no free place, or a line through a box: more room between the boxes, everywhere.
            if (r.Crowded && room < 2.6) { room *= 1.2; continue; }
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
        return r;
    }

    private const double MaxNodeH = 132;

    /// <summary>The narrowest account box when banks stack their accounts (they then leave free lanes on their sides).</summary>
    private const double StackedNodeW = 176;

    /// <summary>How many times the chosen layout of the last diagram was laid out (to check that every layout settles quickly).</summary>
    [ThreadStatic] public static int LastRounds;

    private static double Shrink(double need, double have) => Math.Clamp(have / need, 0.4, 0.98);

    /// <param name="Score">How good it is (lower is better): arrows crossing, lines through boxes, crowded amounts, length and height.</param>
    /// <param name="Candidates">How many candidate layouts there are.</param>
    private readonly record struct Layout(FlowDiagram Diagram, double Top, double NodeW, double MaxW, double NeedW, double NeedH, bool Crowded, double Score = 0, int Candidates = 1);

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
    /// <param name="Room">How much more room than usual between boxes (1 = the usual gaps).</param>
    /// <param name="Candidate">Which candidate layout (see <see cref="Build(IReadOnlyList{MovementView})"/>).</param>
    private static Layout Build(IReadOnlyList<MovementView> movements, double Top, double NodeH, double MinW, double K, double Room, int Candidate)
    {
        if (movements.Count == 0) return new Layout(new FlowDiagram(W, 0, [], [], []), Margin, 0, 0, 0, 0, false);
        double GapX = BaseGapX * Room, GapY = BaseGapY * Room, BankGap = BaseBankGap * Room, BankRowGap = BaseBankRowGap * Room,
            SideGap = BaseSideGap * Room, MiddleRowGap = BaseMiddleRowGap * Room, OutsideGap = BaseOutsideGap * Room;

        // Places: own accounts (each account its own box, even when two accounts of two banks have one name) and the
        // single outside box.
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var bankOf = new Dictionary<string, string>(StringComparer.Ordinal);
        static string Key(long? account, string name) => account is { } id ? "a" + id.ToString(CultureInfo.InvariantCulture) : "n:" + name;
        var moved = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var edges = new Dictionary<(string From, string To), (decimal Amount, int Count)>();
        var lostOf = new Dictionary<(string From, string To), decimal>();
        var parties = new Dictionary<string, (decimal Sent, decimal Received)>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in movements)
        {
            var from = m.FromOutside ? OutsideId : Key(m.FromAccount, m.From);
            var to = m.ToOutside ? OutsideId : Key(m.ToAccount, m.To);
            if (!m.FromOutside) { types.TryAdd(from, m.FromType ?? ""); names.TryAdd(from, m.From); bankOf.TryAdd(from, m.FromBank ?? Movements.OtherAccounts); moved[from] = moved.GetValueOrDefault(from) + m.Amount; }
            if (!m.ToOutside) { types.TryAdd(to, m.ToType ?? ""); names.TryAdd(to, m.To); bankOf.TryAdd(to, m.ToBank ?? Movements.OtherAccounts); moved[to] = moved.GetValueOrDefault(to) + m.Amount; }
            var e = edges.GetValueOrDefault((from, to));
            edges[(from, to)] = (e.Amount + m.Amount, e.Count + 1);
            if (m.Lost is { } l) lostOf[(from, to)] = lostOf.GetValueOrDefault((from, to)) + l;
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
        var stacked = groups.Count >= 2;
        int Cols(int n) => stacked || n <= 1 ? 1 : n <= 4 ? 2 : 3;
        // A bank of accounts one above the other: room on its sides for the arrows going past an account.
        var padX = stacked && groups.Any(g => g.Accounts.Count > 2) ? PadX + 26 : PadX;
        var widest = groups.Max(g => Cols(g.Accounts.Count));
        var maxW = Math.Max(MinNodeW, Math.Min(420, (W - 2 * Margin - 2 * padX - (widest - 1) * GapX) / widest));
        var longest = accounts.Select(a => names[a]).Append("Outside the app").Max(a => a.Length);
        var nodeW = Math.Max(Math.Clamp(longest * CharW + 28, stacked ? StackedNodeW : MinNodeW, maxW), Math.Min(MinW, maxW));
        var sizes = groups.Select(g =>
        {
            var cols = Cols(g.Accounts.Count);
            var rows = (int)Math.Ceiling(g.Accounts.Count / (double)cols);
            // Wide enough for its name too.
            var w = Math.Max(cols * nodeW + (cols - 1) * GapX + 2 * padX, g.Bank.Length * 8.6 + 2 * padX);
            return (W: Math.Min(w, W - 2 * Margin), H: PadTop + rows * NodeH + (rows - 1) * GapY + PadBottom, Cols: cols);
        }).ToList();
        // Where each bank goes (its centre), then the outside box.
        var place = new (double X, double Y)[groups.Count];
        double outsideX = W / 2, outsideY = 0;
        var bottomOfBanks = Top;
        void Placement(bool between)
        {
        outsideX = W / 2;
        bottomOfBanks = Top;
        var leftW = groups.Where((_, g) => g % 2 == 0).Max(g => sizes[groups.IndexOf(g)].W);
        var rightW = groups.Count > 1 ? groups.Where((_, g) => g % 2 == 1).Max(g => sizes[groups.IndexOf(g)].W) : 0;
        // The room between the columns and the outside box grows with the rest, as far as the width allows (the kind of
        // layout never changes with it).
        var middle = between && parties.Count > 0 && groups.Count >= 3 && leftW + rightW + nodeW + 2 * BaseSideGap <= W - 2 * Margin;
        var sideGap = Math.Min(SideGap, (W - 2 * Margin - leftW - rightW - nodeW) / 2);
        if (middle)
        {
            // Two columns of banks, the outside box between them, half way down.
            double y0 = Top;
            for (var g = 0; g < groups.Count; g += 2)
            {
                var rowH = Math.Max(sizes[g].H, g + 1 < groups.Count ? sizes[g + 1].H : 0);
                place[g] = (W / 2 - nodeW / 2 - sideGap - sizes[g].W / 2, y0 + sizes[g].H / 2);
                if (g + 1 < groups.Count) place[g + 1] = (W / 2 + nodeW / 2 + sideGap + sizes[g + 1].W / 2, y0 + sizes[g + 1].H / 2);
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
                : narrow.Count >= 2 && sizes[narrow[^2]].W + sizes[narrow[^1]].W + nodeW + 2 * BaseSideGap <= room ? [narrow[^2], narrow[^1]]
                : narrow.Count >= 1 && sizes[narrow[^1]].W + nodeW + BaseSideGap <= room ? [narrow[^1]]
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
                var gap = Math.Min(SideGap, (room - sizes[l].W - sizes[r].W - nodeW) / 2);
                place[l] = (W / 2 - nodeW / 2 - gap - sizes[l].W / 2, y0 + sizes[l].H / 2);
                place[r] = (W / 2 + nodeW / 2 + gap + sizes[r].W / 2, y0 + sizes[r].H / 2);
                outsideY = y0 + rowH / 2;
                bottomOfBanks = y0 + rowH;
            }
            else if (tail.Count == 1)
            {
                var t = tail[0];
                var gap = Math.Min(SideGap, room - sizes[t].W - nodeW);
                var x0 = (W - (sizes[t].W + gap + nodeW)) / 2;
                place[t] = (x0 + sizes[t].W / 2, y0 + sizes[t].H / 2);
                outsideX = x0 + sizes[t].W + gap + nodeW / 2;
                outsideY = y0 + sizes[t].H / 2;
                bottomOfBanks = y0 + sizes[t].H;
            }
            else outsideY = bottomOfBanks + OutsideGap + NodeH / 2;
        }
        }

        // The order of the banks: the busiest first, or, for up to six banks, the order whose arrows are shortest (the
        // banks sending money to each other, and to the outside, side by side).
        var original = groups.Select((x, n) => (Group: x, Size: sizes[n])).ToList();
        var candidates = new List<(List<int> Order, bool Between)>();
        // The outside box between the banks is only a choice from three banks on.
        foreach (var between in groups.Count >= 3 && parties.Count > 0 ? new[] { true, false } : [false])
        {
            if (groups.Count is <= 1 or > 6) { candidates.Add(([.. Enumerable.Range(0, groups.Count)], between)); continue; }
            var bankOfAccount = accounts.ToDictionary(a => a, a => bankOf[a], StringComparer.Ordinal);
            var links2 = edges.Keys.Where(e => e.From != e.To).ToList();
            var ranked = new List<(double Length, List<int> Order)>();
            foreach (var order in Orders(groups.Count))
            {
                groups = [.. order.Select(n => original[n].Group)];
                sizes = [.. order.Select(n => original[n].Size)];
                Placement(between);
                var centre = groups.Select((x, n) => (x.Bank, P: place[n])).ToDictionary(x => x.Bank, x => x.P, StringComparer.Ordinal);
                (double X, double Y) At(string node) => node == OutsideId ? (outsideX, outsideY) : centre[bankOfAccount[node]];
                var straight = links2.Where(e => e.From == OutsideId || e.To == OutsideId || bankOfAccount[e.From] != bankOfAccount[e.To])
                    .Select(e => (P: At(e.From), Q: At(e.To))).ToList();
                // Short arrows, and arrows that would not cross one another drawn straight from bank to bank.
                var crossings = 0;
                for (var u = 0; u < straight.Count; u++)
                    for (var v = u + 1; v < straight.Count; v++)
                        if (Cross(straight[u].P, straight[u].Q, straight[v].P, straight[v].Q)) crossings++;
                ranked.Add((straight.Sum(x => Math.Sqrt((x.P.X - x.Q.X) * (x.P.X - x.Q.X) + (x.P.Y - x.Q.Y) * (x.P.Y - x.Q.Y)))
                    + 400 * crossings + 0.05 * (bottomOfBanks - Top), order));
            }
            candidates.AddRange(ranked.OrderBy(x => x.Length).Take(3).Select(x => (x.Order, between)));
        }
        var chosen = candidates[Math.Min(Candidate, candidates.Count - 1)];
        groups = [.. chosen.Order.Select(n => original[n].Group)];
        sizes = [.. chosen.Order.Select(n => original[n].Size)];
        Placement(chosen.Between);

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
            var slots = new List<(double X, double Y)>();
            for (var a = 0; a < list.Count; a++)
            {
                var r = a / cols;
                var c = a % cols;
                // The last row, when not full, centred in its bank.
                var inRow = Math.Min(cols, list.Count - r * cols);
                var rowW = inRow * nodeW + (inRow - 1) * GapX;
                slots.Add((bank.X - rowW / 2 + nodeW / 2 + c * (nodeW + GapX), bankTop + PadTop + NodeH / 2 + r * (NodeH + GapY)));
            }
            // Each account where its money goes: the point it is pulled to is the middle of the other banks (and the
            // outside box) it moves money with, each counted once per arrow. The accounts pulled furthest from the
            // middle of their bank choose first, each the free place nearest its point; the others keep their order.
            (double X, double Y)? Pull(string account)
            {
                var others = edges.Keys.Where(e => e.From != e.To && (e.From == account || e.To == account))
                    .Select(e => e.From == account ? e.To : e.From)
                    .Where(o => o == OutsideId || bankOf.GetValueOrDefault(o) != bankOf[account])
                    .Select(o => o == OutsideId ? (outsideX, outsideY) : place[groups.FindIndex(x => x.Bank == bankOf[o])]).ToList();
                return others.Count == 0 ? null : (others.Average(q => q.Item1), others.Average(q => q.Item2));
            }
            var pulls = list.ToDictionary(a => a, Pull, StringComparer.Ordinal);
            var slotOf = new int[list.Count];
            var free = Enumerable.Range(0, slots.Count).ToList();
            foreach (var a in list.Where(a => pulls[a] is not null)
                         .OrderByDescending(a => Math.Abs(pulls[a]!.Value.Y - bank.Y) + Math.Abs(pulls[a]!.Value.X - bank.X) / 2))
            {
                var target = pulls[a]!.Value;
                var slot = free.OrderBy(n => Math.Abs(slots[n].X - target.X) / 2 + Math.Abs(slots[n].Y - target.Y)).First();
                slotOf[list.IndexOf(a)] = slot;
                free.Remove(slot);
            }
            foreach (var a in list.Where(a => pulls[a] is null))
            {
                slotOf[list.IndexOf(a)] = free[0];
                free.RemoveAt(0);
            }
            for (var a = 0; a < list.Count; a++)
            {
                var (ax, ay) = slots[slotOf[a]];
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

        // 1. Sides: for each arrow, the pair of sides and the curve that go through no other box, cross as few of the other
        // arrows as possible and turn as little as possible. A side already used by other arrows costs a little more (the
        // short sides of a box much more), so arrows spread out. Chosen once for every arrow (the largest first), then once
        // more each knowing all the others.
        var ends = new List<End>();
        var used = new Dictionary<(string, Sides), int>();
        // How far each arrow's curve reaches out of its boxes (null: by the distance between them). A shallower curve
        // runs between two rows of accounts, or between accounts and the edge of their bank, where a deep one cannot.
        var reachOf = new double?[links.Count];
        // An arrow that cannot reach its box in one curve without crossing another box or arrow bends once in a gap
        // between banks (beside a bank, or between two rows of banks).
        var viaOf = new Via?[links.Count];
        var vias = Gaps(banks, nodes.FirstOrDefault(n => n.Outside), BankGap, BankRowGap);
        var choice = new (Sides A, Sides B)[links.Count];
        var lines = new (double X, double Y)[]?[links.Count];
        // The top of an account on the first row of its bank is under the bank's name: never used.
        bool UnderName(DiagramNode n, Sides side) => side == Sides.Top && bankBox.TryGetValue(n.Id, out var k) && n.Y - n.H / 2 - (k.Y - k.H / 2) < PadTop + 4;
        double Crowd(DiagramNode n, Sides side) => used.GetValueOrDefault((n.Id, side)) * (side is Sides.Left or Sides.Right ? 45 : 8) + (UnderName(n, side) ? 5000 : 0);
        for (var pass = 0; pass < 3; pass++)
        {
            for (var i = 0; i < links.Count; i++)
            {
                var (from, to) = links[i].Key;
                var (a, b) = (byId[from], byId[to]);
                var others = Enumerable.Range(0, links.Count).Where(j => j != i && lines[j] is not null).Select(j => lines[j]!).ToList();
                // The last pass: only an arrow still crossing another is drawn again, crossing then costing much more.
                if (pass == 2 && Crossings(lines[i]!, others) == 0) continue;
                var crossingCost = pass == 2 ? 3 * CrossingCost : CrossingCost;
                if (pass >= 1) { used[(a.Id, choice[i].A)]--; used[(b.Id, choice[i].B)]--; }
                var best = (Cost: double.MaxValue, A: Sides.Bottom, B: Sides.Top, Reach: (double?)null, Via: (Via?)null, Line: Array.Empty<(double X, double Y)>());
                // Every candidate costed without the other arrows, then the most promising ones with them.
                var tried = new List<(double Cost, Sides A, Sides B, double? Reach, Via? Via, Route C)>();
                void Try(Sides sa, Sides sb, double? reach, Via? via, double extra)
                {
                    var (ax, ay, anx, any) = Port(a, sa, 0);
                    var (bx, by, bnx, bny) = Port(b, sb, 0);
                    var c = Route.Of(ax, ay, anx, any, bx, by, bnx, bny, reach, via);
                    tried.Add((Cost(c, nodes, a, b, banks, bankBox) + extra + Crowd(a, sa) + Crowd(b, sb), sa, sb, reach, via, c));
                }
                void Pick()
                {
                    foreach (var t in tried.OrderBy(t => t.Cost).Take(12))
                    {
                        if (t.Cost >= best.Cost) break;
                        var line = Line(t.C);
                        var cost = t.Cost + crossingCost * Crossings(line, others);
                        if (cost < best.Cost) best = (cost, t.A, t.B, t.Reach, t.Via, line);
                    }
                    tried.Clear();
                }
                foreach (var sa in AllSides)
                    foreach (var sb in AllSides)
                        foreach (var reach in Reaches)
                            Try(sa, sb, reach, null, (sa == Natural(a, b) ? 0 : 6) + (sb == Natural(b, a) ? 0 : 6) + (reach is null ? 0 : 4));
                Pick();
                // Crossing a box or another arrow: through a gap between banks, when that crosses less.
                if (best.Cost >= CrossingCost)
                    foreach (var via in vias.OrderBy(v => Math.Abs(v.X - (a.X + b.X) / 2) + Math.Abs(v.Y - (a.Y + b.Y) / 2)).Take(10))
                    {
                        var at = new DiagramNode("", "", "", false, via.X, via.Y, 1, 1, []);
                        foreach (var sa in AllSides)
                            foreach (var sb in AllSides)
                                Try(sa, sb, null, via, 20 + (sa == Natural(a, at) ? 0 : 6) + (sb == Natural(b, at) ? 0 : 6));
                    }
                Pick();
                choice[i] = (best.A, best.B);
                reachOf[i] = best.Reach;
                viaOf[i] = best.Via;
                lines[i] = best.Line;
                used[(a.Id, best.A)] = used.GetValueOrDefault((a.Id, best.A)) + 1;
                used[(b.Id, best.B)] = used.GetValueOrDefault((b.Id, best.B)) + 1;
            }
        }
        for (var i = 0; i < links.Count; i++)
        {
            var (from, to) = links[i].Key;
            var (a, b) = (byId[from], byId[to]);
            var (half, _) = HeadSize(size[i], K);
            ends.Add(new End(i, a, b, true, choice[i].A, Pair(from, to)) { Room = K * size[i] });
            ends.Add(new End(i, b, a, false, choice[i].B, Pair(from, to)) { Room = 2 * half });
        }

        // 2. Points: each end its own stretch of its side, as wide as its line (leaving) or its head (arriving), ordered
        // by where the arrow goes, with the spare room shared out between them. A side too short for its arrows asks
        // for larger boxes.
        double needW = 0, needH = 0;
        foreach (var group in ends.GroupBy(e => (e.Node.Id, e.Side)))
        {
            var horizontal = group.Key.Side is Sides.Top or Sides.Bottom;
            // Ordered by where each arrow heads a little way from the box (not where its other box is: two arrows
            // between the same two boxes may leave in two directions), so none crosses another as they leave.
            (double X, double Y) Heading(End e)
            {
                var line = lines[e.Arrow]!;
                return e.Leaving ? line[Math.Min(6, line.Length - 1)] : line[Math.Max(0, line.Length - 7)];
            }
            var ordered = group.OrderBy(e => horizontal ? Heading(e).X : Heading(e).Y).ThenBy(e => horizontal ? e.Other.X : e.Other.Y).ThenBy(e => e.PairOrder).ToList();
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
        var samples = drawn.Select(d => Enumerable.Range(0, 81).Select(k => d.C.At(k / 80.0)).ToList()).ToList();
        // A line through a box: the boxes need more room between them.
        var crowded = drawn.Any(d => samples[drawn.IndexOf(d)].Skip(4).SkipLast(4).Any(q => nodes.Any(n => Math.Abs(q.X - n.X) < n.W / 2 - 1 && Math.Abs(q.Y - n.Y) < n.H / 2 - 1)));
        var arrows = new List<DiagramArrow>();
        double top = double.MaxValue, bottom = Math.Max(nodes.Max(n => n.Y + n.H / 2), banks.Count == 0 ? 0 : banks.Max(b => b.Y + b.H / 2));
        for (var i = 0; i < drawn.Count; i++)
        {
            var d = drawn[i];
            var w = Formatting.Money(d.Amount, "CAD").Length * 7.4 + 14;
            var label = d.C.At(0.5);
            var bestCost = double.MaxValue;
            // How long the arrow is: an amount on it must leave enough of it to be seen.
            var length = samples[i].Zip(samples[i].Skip(1), (u, v) => Math.Sqrt((u.X - v.X) * (u.X - v.X) + (u.Y - v.Y) * (u.Y - v.Y))).Sum();
            // On the curve first; when that would hide too much of a short arrow, or two arrows run side by side, just
            // beside it (touching it), or a little further.
            foreach (var t in new[] { 0.5, 0.44, 0.56, 0.38, 0.62, 0.32, 0.68, 0.26, 0.74, 0.2, 0.8 })
            foreach (var side in new[] { 0, 1, -1, 2, -2 })
            {
                var p = d.C.At(t);
                if (side != 0)
                {
                    var (ax, ay) = d.C.At(Math.Max(0, t - 0.01));
                    var (bx2, by2) = d.C.At(Math.Min(1, t + 0.01));
                    var len = Math.Max(1e-6, Math.Sqrt((bx2 - ax) * (bx2 - ax) + (by2 - ay) * (by2 - ay)));
                    // Moved off the line along its normal, far enough that the line runs along the label's edge.
                    double nx = -(by2 - ay) / len, ny = (bx2 - ax) / len;
                    var off = (Math.Abs(nx) * (w / 2) + Math.Abs(ny) * 11 + d.Width / 2) * (Math.Abs(side) == 2 ? 1.6 : 1);
                    p = (p.X + Math.Sign(side) * nx * off, p.Y + Math.Sign(side) * ny * off);
                }
                double l = p.X - w / 2 - 3, r = p.X + w / 2 + 3, tp = p.Y - 14, bt = p.Y + 14;
                // The part of its own arrow it hides: at least VisibleLine of the arrow is left to see.
                var hidden = length * samples[i].Count(q => q.X > l && q.X < r && q.Y > tp && q.Y < bt) / samples[i].Count;
                var cost = Math.Abs(t - 0.5) * 20 + (side == 0 ? 0 : Math.Abs(side) == 1 ? 60 : 90)
                    + 1000 * taken.Count(b => r > b.L && l < b.R && bt > b.T && tp < b.B)
                    + (length - hidden < VisibleLine ? 1000 : 0)
                    // Never on a bank's border line, never out of the drawing.
                    + 300 * banks.Count(b => OnBorder(b, l, tp, r, bt))
                    + (l < 2 || r > W - 2 ? 1000 : 0)
                    + 40 * samples.Where((_, j) => j != i).Sum(list => list.Count(q => q.X > l && q.X < r && q.Y > tp && q.Y < bt)) / 2;
                if (cost < bestCost) { bestCost = cost; label = p; }
            }
            if (bestCost >= 1000) crowded = true;
            taken.Add((label.X - w / 2, label.Y - 11, label.X + w / 2, label.Y + 11));
            top = Math.Min(top, Math.Min(label.Y - 11, samples[i].Min(q => q.Y) - d.Width / 2));
            bottom = Math.Max(bottom, Math.Max(label.Y + 11, samples[i].Max(q => q.Y) + d.Width / 2));
            arrows.Add(new DiagramArrow(d.From, d.To, d.Amount, d.Count, d.From == OutsideId || d.To == OutsideId, d.C.Svg(), d.Head,
                Math.Round(d.Width, 2), Math.Round(label.X, 1), Math.Round(label.Y, 1), lostOf.TryGetValue((d.From, d.To), out var lost) ? lost : null));
        }
        // How good it is: arrows crossing one another, lines through boxes, amounts without a free place, then length
        // and height.
        var crossed = 0;
        for (var u = 0; u < samples.Count; u++)
            for (var v = u + 1; v < samples.Count; v++)
                crossed += Crossings([.. samples[u]], [[.. samples[v]]]) > 0 ? 1 : 0;
        var through = samples.Sum(line => line.Skip(4).SkipLast(4).Count(q => nodes.Any(n => Math.Abs(q.X - n.X) < n.W / 2 - 1 && Math.Abs(q.Y - n.Y) < n.H / 2 - 1)));
        var drawnLength = samples.Sum(line => line.Zip(line.Skip(1), (u, v) => Math.Sqrt((u.X - v.X) * (u.X - v.X) + (u.Y - v.Y) * (u.Y - v.Y))).Sum());
        var score = 1000 * crossed + 300 * through + (crowded ? 2000 : 0) + 0.2 * drawnLength + 0.3 * bottom;
        return new Layout(new FlowDiagram(W, Math.Ceiling(bottom + Margin), nodes, arrows, banks), arrows.Count == 0 ? Margin : Math.Min(top, Top), nodeW, maxW, needW, needH, crowded,
            score, candidates.Count);
    }

    private enum Sides { Top, Bottom, Left, Right }

    /// <summary>Every order of n banks (n at most six), the first one unchanged.</summary>
    private static IEnumerable<List<int>> Orders(int n)
    {
        var items = Enumerable.Range(0, n).ToList();
        IEnumerable<List<int>> Permute(List<int> rest)
        {
            if (rest.Count <= 1) { yield return [.. rest]; yield break; }
            for (var k = 0; k < rest.Count; k++)
            {
                var first = rest[k];
                var others = rest.Where((_, m) => m != k).ToList();
                foreach (var tail in Permute(others)) yield return [first, .. tail];
            }
        }
        return Permute(items);
    }

    /// <summary>True when the rectangle (l, t, r, b) lies across a border line of the bank.</summary>
    private static bool OnBorder(DiagramBank k, double l, double t, double r, double b)
    {
        double kl = k.X - k.W / 2, kr = k.X + k.W / 2, kt = k.Y - k.H / 2, kb = k.Y + k.H / 2;
        if (r < kl || l > kr || b < kt || t > kb) return false;
        var inside = l > kl && r < kr && t > kt && b < kb;
        return !inside;
    }

    /// <summary>What crossing another arrow costs a route (a box costs more: crossing one is never chosen when avoidable).</summary>
    private const double CrossingCost = 260;

    /// <summary>The part of every arrow left to see beside its amount, at least.</summary>
    public const double VisibleLine = 28;

    /// <summary>A route as a line of points, to find where it crosses others.</summary>
    private static (double X, double Y)[] Line(Route c) => [.. Enumerable.Range(0, 25).Select(k => c.At(k / 24.0))];

    /// <summary>How many times a line crosses the other lines (touching at an end does not count).</summary>
    private static int Crossings((double X, double Y)[] line, List<(double X, double Y)[]> others)
    {
        var count = 0;
        double minX = line.Min(p => p.X), maxX = line.Max(p => p.X), minY = line.Min(p => p.Y), maxY = line.Max(p => p.Y);
        foreach (var o in others)
        {
            if (o.Length == 0 || o.Max(p => p.X) < minX || o.Min(p => p.X) > maxX || o.Max(p => p.Y) < minY || o.Min(p => p.Y) > maxY) continue;
            for (var k = 0; k + 1 < line.Length; k++)
                for (var m = 0; m + 1 < o.Length; m++)
                    if (Cross(line[k], line[k + 1], o[m], o[m + 1])) count++;
        }
        return count;
    }

    /// <summary>True when segment a-b crosses segment c-d (strictly: neither touches the other at an end).</summary>
    public static bool Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
    {
        static double Side((double X, double Y) p, (double X, double Y) q, (double X, double Y) r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        double d1 = Side(c, d, a), d2 = Side(c, d, b), d3 = Side(a, b, c), d4 = Side(a, b, d);
        return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) && ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
    }

    /// <summary>The reaches tried for each arrow (see <c>reachOf</c>).</summary>
    private static readonly double?[] Reaches = [null, 40, 70, 120, 190];

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
    private static List<Via> Gaps(List<DiagramBank> banks, DiagramNode? outside, double BankGap, double BankRowGap)
    {
        var list = new List<Via>();
        foreach (var b in banks)
        {
            double left = b.X - b.W / 2, right = b.X + b.W / 2, top = b.Y - b.H / 2, bottom = b.Y + b.H / 2;
            var below = outside is not null && outside.Y > bottom && banks.All(o => o.Y - o.H / 2 < bottom + 1)
                ? bottom + (outside.Y - outside.H / 2 - bottom) / 2 : bottom + BankRowGap / 2;
            // Beside the bank: the middle of the gap to the next bank, or of the free lane along the edge of the drawing.
            foreach (var x in new[] { left - BankGap / 2, right + BankGap / 2, left / 2, (right + W) / 2 })
                if (x > 16 && x < W - 16 && (x < left - 14 || x > right + 14)) list.Add(new Via(x, b.Y, true));
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
            var (a1, a2) = (Room(x0, y0, n0x, n0y, r1), Room(x3, y3, n3x, n3y, r2));
            return new Route(new Bez(x0, y0, x0 + n0x * a1, y0 + n0y * a1, v.X - tx * r1, v.Y - ty * r1, v.X, v.Y),
                new Bez(v.X, v.Y, v.X + tx * r2, v.Y + ty * r2, x3 + n3x * a2, y3 + n3y * a2, x3, y3));
        }
    }

    /// <summary>Leaves (x0, y0) along (n0x, n0y) and comes into (x3, y3) along the opposite of (n3x, n3y): square to both sides.</summary>
    private static Bez Curve(double x0, double y0, double n0x, double n0y, double x3, double y3, double n3x, double n3y, double? reachOut = null)
    {
        var dist = Math.Sqrt((x3 - x0) * (x3 - x0) + (y3 - y0) * (y3 - y0));
        var reach = reachOut ?? Math.Clamp(dist * 0.45, 28, 180);
        // Never further out than the edge of the drawing (a curve bulges about three quarters of its reach).
        return new Bez(x0, y0, x0 + n0x * Room(x0, y0, n0x, n0y, reach), y0 + n0y * Room(x0, y0, n0x, n0y, reach),
            x3 + n3x * Room(x3, y3, n3x, n3y, reach), y3 + n3y * Room(x3, y3, n3x, n3y, reach), x3, y3);
    }

    /// <summary>How far a curve may reach from (x, y) along (nx, ny): <paramref name="reach"/>, or less near a side edge of the drawing.</summary>
    private static double Room(double x, double y, double nx, double ny, double reach)
    {
        var free = nx > 0.5 ? W - Margin - 4 - x : nx < -0.5 ? x - Margin - 4 : double.MaxValue;
        return Math.Max(12, Math.Min(reach, free / 0.75));
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
        double turn = 0, heading = double.NaN;
        for (var k = 1; k <= steps; k++)
        {
            var (x, y) = c.At(k / (double)steps);
            var step = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            cost += step;
            // How much the line turns along the way: a natural curve turns little; a hairpin or a loop much.
            if (step > 1e-6)
            {
                var h = Math.Atan2(y - py, x - px);
                if (!double.IsNaN(heading)) { var dh = Math.Abs(h - heading); turn += Math.Min(dh, 2 * Math.PI - dh); }
                heading = h;
            }
            (px, py) = (x, y);
            foreach (var n in nodes)
            {
                // Its own two boxes only must not be crossed; others are kept at a distance.
                var pad = n == a || n == b ? -2 : 14;
                if (Math.Abs(x - n.X) < n.W / 2 + pad && Math.Abs(y - n.Y) < n.H / 2 + pad) cost += 400;
            }
            // Out of the drawing: as bad as through a box.
            if (x < Margin || x > W - Margin) cost += 400;
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
                // A bank's name is never crossed (as a box).
                if (y < bank.Y - bank.H / 2 + PadTop - 8 && Math.Abs(x - bank.X) < Math.Min(bank.W / 2, bank.Name.Length * 4.6 + 16)) cost += 400;
            }
        }
        // Turning more than a U (a loop, or a hairpin then back) costs, the more the more it turns.
        if (turn > 1.1 * Math.PI) cost += 220 * (turn - 1.1 * Math.PI);
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
