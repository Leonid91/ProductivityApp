using System.Globalization;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Real statements published on the internet (Statements/Real/SOURCES.md), recorded from the published pages as they
/// are: every letter where the bank printed it (A4, in points from the top-left corner).
/// <para>A recording (Statements/Real/*.txt) has one line per row of letters: <c>page|baseline|size+font|x,letter|x,letter…</c>,
/// font R (regular), B (bold) or T (the text up the margin).</para>
/// </summary>
public static class RealStatements
{
    /// <summary>Every recording: "bank-yyyy-mm" (the month the statement covers), "bank-livret-yyyy-mm" for a savings account.</summary>
    public static IReadOnlyList<string> Names { get; } =
        [.. typeof(RealStatements).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("Real.", StringComparison.Ordinal))
            .Select(n => n["Real.".Length..^".txt".Length]).Order(StringComparer.Ordinal)];

    public sealed record Row(int Page, double Baseline, double Size, char Font, IReadOnlyList<(double X, string Text)> Letters);

    /// <summary>The recording itself.</summary>
    public static IReadOnlyList<Row> Rows(string name)
    {
        using var stream = typeof(RealStatements).Assembly.GetManifestResourceStream($"Real.{name}.txt")
            ?? throw new ArgumentException($"No recording {name}", nameof(name));
        using var reader = new StreamReader(stream);
        var rows = new List<Row>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            var parts = line.Split('|');
            var style = parts[2];
            var letters = parts.Skip(3).Select(p => (D(p[..p.IndexOf(',')]), p[(p.IndexOf(',') + 1)..])).OrderBy(l => l.Item1).ToList();
            rows.Add(new Row(int.Parse(parts[0], CultureInfo.InvariantCulture), D(parts[1]), D(style[..^1]), style[^1], letters));
        }
        return rows;
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>
    /// How much narrower than Arial the row's letters are printed (BoursoBank's, about 0.9): the room each letter takes,
    /// measured from one letter to the next, over Arial's (most letters follow each other within a word).
    /// </summary>
    public static double Narrowing(Row row)
    {
        var ratios = new List<double>();
        for (var i = 0; i + 1 < row.Letters.Count; i++)
        {
            var (x, text) = row.Letters[i];
            if (text.Length != 1) continue;
            var q = (row.Letters[i + 1].X - x) / (Advance(text[0], row.Font) * row.Size);
            if (q is > 0.6 and < 1.4) ratios.Add(q);
        }
        if (ratios.Count < 3) return 1;
        ratios.Sort();
        return Math.Min(1, ratios[ratios.Count / 2]);
    }

    /// <summary>The row's words: letters closer than a fifth of the size after the room the one before takes.</summary>
    public static IReadOnlyList<(double X, string Text)> Words(Row row)
    {
        var r = Narrowing(row);
        var words = new List<(double X, double End, string Text)>();
        foreach (var (x, text) in row.Letters)
        {
            var end = x + text.Sum(c => Advance(c, row.Font)) * row.Size * r;
            if (words.Count > 0 && row.Font != 'T' && x - words[^1].End < 0.2 * row.Size)
                words[^1] = (words[^1].X, end, words[^1].Text + text);
            else words.Add((x, end, text));
        }
        return [.. words.Select(w => (w.X, w.Text))];
    }

    /// <summary>The statement as a PDF: every letter at its place, in Arial as the banks print (or a font with Arial's
    /// widths where the system has no Arial), as narrow as the row's.</summary>
    public static byte[] Pdf(string name)
    {
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddTrueTypeFont(Font("arial.ttf", "Arial.ttf", "LiberationSans-Regular.ttf"));
        var bold = builder.AddTrueTypeFont(Font("arialbd.ttf", "Arial Bold.ttf", "LiberationSans-Bold.ttf"));
        var times = builder.AddTrueTypeFont(Font("times.ttf", "Times New Roman.ttf", "LiberationSerif-Regular.ttf"));
        foreach (var page in Rows(name).GroupBy(r => r.Page).OrderBy(g => g.Key))
        {
            var pdfPage = builder.AddPage(595, 842);
            foreach (var row in page)
            {
                var font = row.Font switch { 'B' => bold, 'T' => times, _ => regular };
                var size = row.Font == 'T' ? row.Size : row.Size * Narrowing(row);
                foreach (var (x, text) in row.Letters) pdfPage.AddText(text, size, new PdfPoint(x, 842 - row.Baseline), font);
            }
        }
        return builder.Build();
    }

    /// <summary>A font of the system: Windows, macOS, or Linux (Liberation, with the same widths as Arial and Times).</summary>
    private static byte[] Font(string windows, string mac, string linux)
    {
        string[] places =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), windows),
            Path.Combine("/System/Library/Fonts/Supplemental", mac),
            Path.Combine("/Library/Fonts", mac),
            Path.Combine("/usr/share/fonts/truetype/liberation", linux),
            Path.Combine("/usr/share/fonts/liberation-sans", linux),
            Path.Combine("/usr/share/fonts/liberation-serif", linux),
        ];
        var found = places.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"No {windows} (or {mac}, {linux}) on this system to draw the statement with.");
        return File.ReadAllBytes(found);
    }

    // Arial's (Helvetica's) widths, in thousandths of the size, for the characters 32 to 126; accented letters as their letter.
    private static readonly int[] Regular = [278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584];
    private static readonly int[] Bold = [278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611, 975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556, 333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584];

    private static double Advance(char c, char font)
    {
        var plain = c.ToString().Normalize(System.Text.NormalizationForm.FormD)[0];
        var table = font == 'B' ? Bold : Regular;
        var i = plain - 32;
        return (i >= 0 && i < table.Length ? table[i] : 556) / 1000.0;
    }
}
