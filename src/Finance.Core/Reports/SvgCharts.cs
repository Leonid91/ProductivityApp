using System.Globalization;
using System.Text;
using ProductivityApp.Finance.Services;
using Flow = ProductivityApp.Finance.Services.Flow;

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
    /// Where the money moved: sources on the left (accounts, or the outside), destinations on the right, one band per
    /// flow as wide as its amount. Blue between the person's own accounts, orange to or from the outside.
    /// </summary>
    public static string FlowDiagram(IReadOnlyList<Flow> flows, double width, double height, string currency)
    {
        var layout = FlowLayout.Compute(flows, width, height);
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(width)}\" height=\"{F(height)}\" viewBox=\"0 0 {F(width)} {F(height)}\" font-family=\"Inter\">");
        foreach (var b in layout.Bands)
        {
            var color = b.Flow.FromOutside || b.Flow.ToOutside ? Palette.OutsideInk : Palette.Accent;
            var xm = (layout.LeftX + layout.RightX) / 2;
            sb.Append($"<path d=\"M{F(layout.LeftX)} {F(b.Y0)} C{F(xm)} {F(b.Y0)} {F(xm)} {F(b.Y1)} {F(layout.RightX)} {F(b.Y1)} L{F(layout.RightX)} {F(b.Y1 + b.Thickness)} C{F(xm)} {F(b.Y1 + b.Thickness)} {F(xm)} {F(b.Y0 + b.Thickness)} {F(layout.LeftX)} {F(b.Y0 + b.Thickness)} Z\" fill=\"{color}\" fill-opacity=\"0.32\"/>");
            // The amount sits where the band leaves its source: bands never overlap there.
            if (b.Thickness >= 9)
                sb.Append($"<text x=\"{F(layout.LeftX + 6)}\" y=\"{F(b.Y0 + b.Thickness / 2 + 2.5)}\" font-size=\"7\" fill=\"{Palette.InkSecondary}\" text-anchor=\"start\">{Escape(Formatting.Money(b.Flow.Amount, currency))}</text>");
        }
        foreach (var n in layout.Nodes)
        {
            var color = n.Outside ? Palette.OutsideInk : Palette.Ink;
            var x = n.Left ? layout.LeftX - FlowLayout.NodeWidth : layout.RightX;
            sb.Append($"<rect x=\"{F(x)}\" y=\"{F(n.Y)}\" width=\"{F(FlowLayout.NodeWidth)}\" height=\"{F(Math.Max(1.5, n.Height))}\" rx=\"1.5\" fill=\"{color}\"/>");
            var tx = n.Left ? x - 5 : x + FlowLayout.NodeWidth + 5;
            var anchor = n.Left ? "end" : "start";
            var cy = n.Y + Math.Max(1.5, n.Height) / 2;
            sb.Append($"<text x=\"{F(tx)}\" y=\"{F(cy - 1)}\" font-size=\"7.5\" font-weight=\"600\" fill=\"{color}\" text-anchor=\"{anchor}\">{Escape(n.Name)}</text>");
            sb.Append($"<text x=\"{F(tx)}\" y=\"{F(cy + 8)}\" font-size=\"7\" fill=\"{Palette.InkMuted}\" text-anchor=\"{anchor}\">{Escape(Formatting.Money(n.Total, currency))}</text>");
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
