namespace ProductivityApp.Finance.Reports;

/// <summary>Chart colours shared with the interface (ui/src/theme/palette.ts). Validated for colour-vision deficiencies.</summary>
public static class Palette
{
    public static readonly string[] SeriesLight = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948"];
    public const string Other = "#a3a29c";
    public const string Ink = "#1b1b1a";
    public const string InkSecondary = "#52514e";
    public const string InkMuted = "#7a7974";
    public const string Hairline = "#e4e3df";
    public const string Surface = "#f6f5f2";
    public const string Positive = "#006300";
    public const string Negative = "#d03b3b";
    public const string Accent = "#2a78d6";
    public const string Gold = "#9a6a00";
    /// <summary>Money going to or coming from outside the imported accounts.</summary>
    public const string OutsideInk = "#c4551f";
    public const string OutsideSoft = "#fdf1ea";
    public const string NegativeSoft = "#fcf3f2";
    public const string PositiveSoft = "#f1f8f1";
    public const string NegativeStrong = "#f6d9d6";
    public const string PositiveStrong = "#d5ead5";
    public const string WarningSoft = "#fdf4e3";
    public const string WarningInk = "#8a5a00";

    /// <summary>Colour of slice <paramref name="index"/>: the eight series colours, then evenly spread hues (same as the interface).</summary>
    public static string Series(int index)
    {
        if (index < SeriesLight.Length) return SeriesLight[index];
        var hue = (index - SeriesLight.Length) * 137.508 % 360;
        return Hsl(hue, 0.55, 0.52);
    }

    private static string Hsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        int B(double v) => (int)Math.Round((v + m) * 255);
        return $"#{B(r):x2}{B(g):x2}{B(b):x2}";
    }
}
