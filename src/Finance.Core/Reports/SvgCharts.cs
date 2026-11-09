using System.Globalization;
using System.Text;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Reports;

/// <summary>Charts drawn as SVG for the PDF reports: deterministic, same layout every time.</summary>
public static class SvgCharts
{
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static string SliceColor(int index, string key) => key == "__other" ? Palette.Other : Palette.Series(index);

    public static string Donut(IReadOnlyList<Slice> slices, double size)
    {
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(size)}\" height=\"{F(size)}\" viewBox=\"0 0 {F(size)} {F(size)}\">");
        var cx = size / 2;
        var r = size / 2 - 2;
        var inner = r * 0.62;
        var total = slices.Sum(s => (double)s.Amount);
        if (total <= 0)
        {
            sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cx)}\" r=\"{F((r + inner) / 2)}\" fill=\"none\" stroke=\"{Palette.Hairline}\" stroke-width=\"{F(r - inner)}\"/>");
        }
        else if (slices.Count == 1)
        {
            sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cx)}\" r=\"{F((r + inner) / 2)}\" fill=\"none\" stroke=\"{SliceColor(0, slices[0].Key)}\" stroke-width=\"{F(r - inner)}\"/>");
        }
        else
        {
            var angle = -Math.PI / 2;
            for (var i = 0; i < slices.Count; i++)
            {
                var sweep = (double)slices[i].Amount / total * Math.PI * 2;
                if (sweep <= 0) continue;
                var a0 = angle;
                var a1 = angle + sweep;
                angle = a1;
                var large = sweep > Math.PI ? 1 : 0;
                double X(double rad, double a) => cx + rad * Math.Cos(a);
                double Y(double rad, double a) => cx + rad * Math.Sin(a);
                sb.Append($"<path d=\"M{F(X(r, a0))} {F(Y(r, a0))} A{F(r)} {F(r)} 0 {large} 1 {F(X(r, a1))} {F(Y(r, a1))} L{F(X(inner, a1))} {F(Y(inner, a1))} A{F(inner)} {F(inner)} 0 {large} 0 {F(X(inner, a0))} {F(Y(inner, a0))} Z\" fill=\"{SliceColor(i, slices[i].Key)}\" stroke=\"#ffffff\" stroke-width=\"2\" stroke-linejoin=\"round\"/>");
            }
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Bars of expenses, one per day, month or year, with a hairline grid and a few axis labels.</summary>
    public static string Bars(IReadOnlyList<Bar> bars, double width, double height, string currency)
    {
        const double left = 52, bottom = 18, top = 6, right = 4;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(width)}\" height=\"{F(height)}\" viewBox=\"0 0 {F(width)} {F(height)}\" font-family=\"Inter\">");
        var max = bars.Count == 0 ? 0 : (double)bars.Max(b => b.Expenses);
        var niceMax = NiceCeiling(max);
        var plotW = width - left - right;
        var plotH = height - top - bottom;
        for (var i = 0; i <= 4; i++)
        {
            var v = niceMax * i / 4;
            var y = top + plotH - plotH * i / 4;
            sb.Append($"<line x1=\"{F(left)}\" x2=\"{F(width - right)}\" y1=\"{F(y)}\" y2=\"{F(y)}\" stroke=\"{(i == 0 ? "#d6d5d0" : "#eeede9")}\" stroke-width=\"0.5\"/>");
            sb.Append($"<text x=\"{F(left - 6)}\" y=\"{F(y + 2.5)}\" font-size=\"7\" fill=\"{Palette.InkMuted}\" text-anchor=\"end\">{Escape(Formatting.Money(decimal.Round((decimal)v), currency).Replace(".00", ""))}</text>");
        }
        if (bars.Count > 0)
        {
            var band = plotW / bars.Count;
            var barW = Math.Max(1.5, Math.Min(band * 0.62, 26));
            // Every day of a month is labelled, 1 to the last; longer series keep about twelve labels.
            var labelEvery = bars.Count <= 31 ? 1 : (int)Math.Ceiling(bars.Count / 12.0);
            for (var i = 0; i < bars.Count; i++)
            {
                var x = left + band * i + (band - barW) / 2;
                var v = (double)bars[i].Expenses;
                if (v > 0 && niceMax > 0)
                {
                    var h = Math.Max(1, plotH * v / niceMax);
                    var y = top + plotH - h;
                    var rad = Math.Min(2, Math.Min(barW / 2, h));
                    sb.Append($"<path d=\"M{F(x)} {F(top + plotH)} L{F(x)} {F(y + rad)} Q{F(x)} {F(y)} {F(x + rad)} {F(y)} L{F(x + barW - rad)} {F(y)} Q{F(x + barW)} {F(y)} {F(x + barW)} {F(y + rad)} L{F(x + barW)} {F(top + plotH)} Z\" fill=\"{Palette.Accent}\"/>");
                }
                var showLabel = labelEvery == 1 || i == 0 || (i + 1) % labelEvery == 0;
                if (showLabel)
                    sb.Append($"<text x=\"{F(left + band * i + band / 2)}\" y=\"{F(height - 5)}\" font-size=\"7\" fill=\"{Palette.InkMuted}\" text-anchor=\"middle\">{Escape(bars[i].Label)}</text>");
            }
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// The money-moved diagram as laid out by the engine (<see cref="Services.FlowDiagram"/>): account boxes (name, then
    /// type), the outside box (same size, orange), and arrows pointing where the money went with their amounts.
    /// Blue between own accounts, orange to or from the outside.
    /// </summary>
    public static string FlowDiagram(Services.FlowDiagram d, double width, double height, string currency)
    {
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(width)}\" height=\"{F(height)}\" viewBox=\"0 0 {F(d.Width)} {F(d.Height)}\" preserveAspectRatio=\"xMidYMid meet\" font-family=\"Inter\">");
        foreach (var a in d.Arrows)
        {
            var color = a.Outside ? Palette.OutsideInk : Palette.Accent;
            sb.Append($"<path d=\"{a.Path}\" fill=\"none\" stroke=\"{color}\" stroke-opacity=\"0.8\" stroke-width=\"{F(a.Width)}\" stroke-linecap=\"round\"/>");
        }
        // Arrowheads on top of every line, so none is hidden.
        foreach (var a in d.Arrows)
            sb.Append($"<path d=\"{a.Head}\" fill=\"{(a.Outside ? Palette.OutsideInk : Palette.Accent)}\" stroke=\"#ffffff\" stroke-width=\"1\"/>");
        foreach (var a in d.Arrows)
        {
            var text = Formatting.Money(a.Amount, currency);
            var w = text.Length * 7.4 + 14;
            sb.Append($"<rect x=\"{F(a.LabelX - w / 2)}\" y=\"{F(a.LabelY - 11)}\" width=\"{F(w)}\" height=\"22\" rx=\"11\" fill=\"#ffffff\" stroke=\"{(a.Outside ? Palette.OutsideInk : Palette.Accent)}\" stroke-width=\"1\"/>");
            sb.Append($"<text x=\"{F(a.LabelX)}\" y=\"{F(a.LabelY + 4.5)}\" font-size=\"12.5\" font-weight=\"600\" fill=\"{Palette.Ink}\" text-anchor=\"middle\">{Escape(text)}</text>");
        }
        foreach (var n in d.Nodes)
        {
            // The outside box: same size as the accounts, in orange.
            var (fill, stroke, ink) = n.Outside ? (Palette.OutsideSoft, Palette.OutsideInk, Palette.OutsideInk) : ("#ffffff", Palette.Ink, Palette.Ink);
            sb.Append($"<rect x=\"{F(n.X - n.W / 2)}\" y=\"{F(n.Y - n.H / 2)}\" width=\"{F(n.W)}\" height=\"{F(n.H)}\" rx=\"12\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"1.5\"/>");
            sb.Append($"<text x=\"{F(n.X)}\" y=\"{F(n.Y - 3)}\" font-size=\"15\" font-weight=\"700\" fill=\"{ink}\" text-anchor=\"middle\">{Escape(Services.FlowDiagram.Fit(n.Name, n.W))}</text>");
            sb.Append($"<text x=\"{F(n.X)}\" y=\"{F(n.Y + 16)}\" font-size=\"12\" fill=\"{Palette.InkMuted}\" text-anchor=\"middle\">{Escape(n.Type)}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    public static double NiceCeiling(double v)
    {
        if (v <= 0) return 4;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
            if (m * exp >= v) return m * exp;
        return 10 * exp;
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
