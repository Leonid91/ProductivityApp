namespace ProductivityApp.Finance.Pdf;

/// <summary>A word on a page, in points, origin at the top-left corner.</summary>
public sealed record Word(string Text, double Left, double Right, double Top, double Bottom)
{
    public double Height => Bottom - Top;
    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Top + Bottom) / 2;
}

public sealed class PageWords
{
    public required int Number { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required IReadOnlyList<Word> Words { get; init; }
    /// <summary>True when the words were obtained by text recognition on a scanned image.</summary>
    public bool FromOcr { get; init; }
}

/// <summary>A visual line: words sharing a baseline, sorted left to right.</summary>
public sealed class TextLine
{
    public required int Page { get; init; }
    public required IReadOnlyList<Word> Words { get; init; }
    public double Top => Words.Min(w => w.Top);
    public double Bottom => Words.Max(w => w.Bottom);
    public double Height => Words.Average(w => w.Height);
    public double Left => Words[0].Left;
    public double Right => Words[^1].Right;
    public string Text => string.Join(' ', Words.Select(w => w.Text));
    public IReadOnlyList<string> Tokens => Words.Select(w => w.Text).ToList();
}
