using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Reports;

/// <summary>
/// Layout of the money-moved diagram (the interface draws the same one in ui/src/components/FlowChart.tsx):
/// one node per source on the left and per destination on the right, as tall as the money through it, and one band per
/// flow. The person's own accounts come first, the outside last.
/// </summary>
public sealed class FlowLayout
{
    public const double NodeWidth = 7;
    public const double Gap = 14;
    public const double LabelWidth = 128;

    public sealed record Node(string Name, bool Left, bool Outside, decimal Total, double Y, double Height);
    public sealed record Band(Flow Flow, double Y0, double Y1, double Thickness);

    public required List<Node> Nodes { get; init; }
    public required List<Band> Bands { get; init; }
    public required double LeftX { get; init; }
    public required double RightX { get; init; }

    public static FlowLayout Compute(IReadOnlyList<Flow> flows, double width, double height)
    {
        var leftX = LabelWidth + NodeWidth;
        var rightX = width - LabelWidth - NodeWidth;
        var sources = Order(flows.GroupBy(f => (f.From, f.FromOutside)).Select(g => (g.Key.From, g.Key.FromOutside, Total: g.Sum(f => f.Amount))));
        var targets = Order(flows.GroupBy(f => (f.To, f.ToOutside)).Select(g => (g.Key.To, g.Key.ToOutside, Total: g.Sum(f => f.Amount))));
        var total = (double)flows.Sum(f => f.Amount);
        if (total <= 0) return new FlowLayout { Nodes = [], Bands = [], LeftX = leftX, RightX = rightX };
        var count = Math.Max(sources.Count, targets.Count);
        var scale = Math.Max(0, height - 4 - Gap * (count - 1)) / total;

        var nodes = new List<Node>();
        var leftY = new Dictionary<string, double>();
        var rightY = new Dictionary<string, double>();
        void Place(List<(string Name, bool Outside, decimal Total)> list, bool left)
        {
            var used = list.Sum(n => Math.Max(1.5, (double)n.Total * scale)) + Gap * (list.Count - 1);
            var y = Math.Max(2, (height - used) / 2);
            foreach (var n in list)
            {
                var h = Math.Max(1.5, (double)n.Total * scale);
                nodes.Add(new Node(n.Name, left, n.Outside, n.Total, y, h));
                (left ? leftY : rightY)[n.Name] = y;
                y += h + Gap;
            }
        }
        Place(sources, true);
        Place(targets, false);

        // Bands leave each source in the order of their destinations, and reach each destination in the order of their sources.
        var targetIndex = targets.Select((t, i) => (t.Name, i)).ToDictionary(x => x.Name, x => x.i);
        var sourceIndex = sources.Select((t, i) => (t.Name, i)).ToDictionary(x => x.Name, x => x.i);
        var bandsOut = new Dictionary<Flow, double>();
        foreach (var f in flows.OrderBy(f => sourceIndex[f.From]).ThenBy(f => targetIndex[f.To]))
        {
            var t = Math.Max(1, (double)f.Amount * scale);
            bandsOut[f] = leftY[f.From];
            leftY[f.From] += t;
        }
        var bands = new List<Band>();
        foreach (var f in flows.OrderBy(f => targetIndex[f.To]).ThenBy(f => sourceIndex[f.From]))
        {
            var t = Math.Max(1, (double)f.Amount * scale);
            bands.Add(new Band(f, bandsOut[f], rightY[f.To], t));
            rightY[f.To] += t;
        }
        return new FlowLayout { Nodes = nodes, Bands = bands, LeftX = leftX, RightX = rightX };
    }

    private static List<(string Name, bool Outside, decimal Total)> Order(IEnumerable<(string Name, bool Outside, decimal Total)> items) =>
        items.OrderBy(n => n.Outside ? 1 : 0).ThenByDescending(n => n.Total).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
