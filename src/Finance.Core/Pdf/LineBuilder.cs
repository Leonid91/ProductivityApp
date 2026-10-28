using System.Text.RegularExpressions;

namespace ProductivityApp.Finance.Pdf;

public static partial class LineBuilder
{
    /// <summary>Groups the words of a page into lines and rejoins numbers split on thousands spaces ("1" "234,56").</summary>
    public static List<TextLine> Build(PageWords page)
    {
        var lines = new List<List<Word>>();
        foreach (var w in page.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => w.CenterY).ThenBy(w => w.Left))
        {
            List<Word>? best = null;
            var bestOverlap = 0.0;
            // Only the last few lines can share a baseline with this word.
            for (var i = Math.Max(0, lines.Count - 3); i < lines.Count; i++)
            {
                var line = lines[i];
                var top = line.Average(x => x.Top);
                var bottom = line.Average(x => x.Bottom);
                var overlap = Math.Min(bottom, w.Bottom) - Math.Max(top, w.Top);
                var ratio = overlap / Math.Max(0.1, Math.Min(bottom - top, w.Height));
                if (ratio > 0.5 && ratio > bestOverlap) { best = line; bestOverlap = ratio; }
            }
            if (best is null) lines.Add([w]);
            else best.Add(w);
        }

        var result = new List<TextLine>(lines.Count);
        foreach (var l in lines)
        {
            var sorted = l.OrderBy(x => x.Left).ToList();
            result.Add(new TextLine { Page = page.Number, Words = MergeSplitNumbers(sorted) });
        }
        return result.OrderBy(x => x.Top).ToList();
    }

    [GeneratedRegex(@"^[-+(]?(?:\$|€)?\d{1,3}$")]
    private static partial Regex LeadingGroup();

    [GeneratedRegex(@"^\d{3}(?:[ .,']\d{3})*(?:[.,]\d{2})?(?:\)|-|\$|€|CR|DR)?$")]
    private static partial Regex FollowingGroup();

    // Text recognition sometimes cuts "42,24" into "42" "," "24" or "42," "24".
    [GeneratedRegex(@"^[-+(]?\d[\d ]*[.,]?$|^[.,]$")]
    private static partial Regex NumberHead();

    [GeneratedRegex(@"^[.,]?\d")]
    private static partial Regex NumberTail();

    private static List<Word> MergeSplitNumbers(List<Word> words)
    {
        var output = new List<Word>(words.Count);
        foreach (var w in words)
        {
            if (output.Count > 0)
            {
                var prev = output[^1];
                var gap = w.Left - prev.Right;
                var maxGap = Math.Max(prev.Height, w.Height) * 0.45;
                var punctuationJoin = prev.Text is "," or "." || prev.Text[^1] is ',' or '.' || w.Text[0] is ',' or '.' || w.Text is "," or ".";
                if (gap >= -1 && gap < maxGap * 0.8 && punctuationJoin && NumberHead().IsMatch(prev.Text) && (NumberTail().IsMatch(w.Text) || w.Text is "," or "."))
                {
                    output[^1] = new Word(prev.Text + w.Text, prev.Left, w.Right, Math.Min(prev.Top, w.Top), Math.Max(prev.Bottom, w.Bottom));
                    continue;
                }
                // A full group with cents ("174,65") right after 1-3 digits is a thousands split even with a wide gap.
                var groupGap = w.Text.Length >= 6 && (w.Text[3] is ',' or '.') ? Math.Max(prev.Height, w.Height) * 0.85 : maxGap;
                if (gap >= -0.5 && gap < groupGap && LeadingGroup().IsMatch(prev.Text) && FollowingGroup().IsMatch(w.Text))
                {
                    output[^1] = new Word(prev.Text + " " + w.Text, prev.Left, w.Right, Math.Min(prev.Top, w.Top), Math.Max(prev.Bottom, w.Bottom));
                    continue;
                }
                // Detached currency or sign suffixes printed right after a number: "12,34 €", "12.34 CR".
                if (gap >= -0.5 && gap < maxGap && w.Text is "€" or "$" or "CR" or "DR" or "-" && char.IsDigit(prev.Text[^1]))
                {
                    output[^1] = new Word(prev.Text + " " + w.Text, prev.Left, w.Right, Math.Min(prev.Top, w.Top), Math.Max(prev.Bottom, w.Bottom));
                    continue;
                }
            }
            output.Add(w);
        }
        return output;
    }
}
