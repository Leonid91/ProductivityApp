using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using QuestPDF.Fluent;
using WinPdf = Windows.Data.Pdf;

namespace ProductivityApp.Finance.Pdf;

/// <summary>
/// Gets positioned words out of a PDF. Pages with a text layer are read directly; pages that are
/// only a picture (scans, photos printed to PDF) go through the text recognition built into Windows.
/// Everything runs on this machine.
/// </summary>
public static partial class PdfReader
{
    /// <summary>A page needs text recognition when it has (almost) no text but shows an image.</summary>
    private const int MinWordsForTextLayer = 6;

    public static async Task<IReadOnlyList<PageWords>> ReadAsync(byte[] pdf, CancellationToken ct = default)
    {
        var pages = new List<PageWords>();
        var needOcr = new List<int>();
        // The picture that fills a scanned page, read at its own resolution (better than the page drawn again).
        var scans = new Dictionary<int, (byte[] Image, double Left, double Top, double Width, double Height)>();
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(pdf))
        {
            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var h = page.Height;
                var words = page.GetWords()
                    .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                    .Select(w => new Word(w.Text.Trim(), w.BoundingBox.Left, w.BoundingBox.Right, h - w.BoundingBox.Top, h - w.BoundingBox.Bottom))
                    .ToList();
                words = RemoveMarginCodes(words, page.Width);
                if (words.Count < MinWordsForTextLayer && page.GetImages().Any())
                {
                    needOcr.Add(pages.Count);
                    var image = page.GetImages().OrderByDescending(i => i.BoundingBox.Width * i.BoundingBox.Height).First();
                    var covers = image.BoundingBox.Width * image.BoundingBox.Height >= 0.6 * page.Width * page.Height;
                    byte[]? bytes = image.TryGetPng(out var png) ? png
                        : image.RawBytes.Length > 3 && image.RawBytes[0] == 0xFF && image.RawBytes[1] == 0xD8 ? image.RawBytes.ToArray() : null;
                    if (covers && bytes is not null)
                        scans[pages.Count] = (bytes, image.BoundingBox.Left, h - image.BoundingBox.Top, image.BoundingBox.Width, image.BoundingBox.Height);
                }
                pages.Add(new PageWords { Number = page.Number, Width = page.Width, Height = page.Height, Words = words });
            }
        }

        if (needOcr.Count > 0)
        {
            var language = PickOcrLanguage();
            foreach (var index in needOcr)
            {
                ct.ThrowIfCancellationRequested();
                PageWords? read = null;
                if (scans.TryGetValue(index, out var scan))
                {
                    try { read = await OcrImageAsync(scan.Image, scan.Left, scan.Top, scan.Width, scan.Height, pages[index], language).ConfigureAwait(false); }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException) { ProductivityApp.Finance.Services.Diagnostics.Report("PdfReader OCR of the page's picture (the page is read whole instead)", e); read = null; }
                }
                pages[index] = read ?? await OcrPageAsync(pdf, index, pages[index], language).ConfigureAwait(false);
                pages[index] = new PageWords { Number = pages[index].Number, Width = pages[index].Width, Height = pages[index].Height, Words = RepairAmounts(pages[index].Words), FromOcr = true };
            }
        }
        return pages;
    }

    public static bool OcrAvailable => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    private static Windows.Globalization.Language PickOcrLanguage()
    {
        var available = OcrEngine.AvailableRecognizerLanguages;
        if (available.Count == 0)
            throw new InvalidOperationException("This page is a scanned image and Windows has no text recognition language installed. Add French or English in Windows Settings > Time & language > Language.");
        // French recognition also reads English text and keeps accents, so it is preferred when installed.
        return available.FirstOrDefault(l => l.LanguageTag.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            ?? available[0];
    }

    private static async Task<PageWords> OcrPageAsync(byte[] pdf, int index, PageWords original, Windows.Globalization.Language language)
    {
        const double dpi = 300;
        var engine = OcrEngine.TryCreateFromLanguage(language)
            ?? throw new InvalidOperationException("Windows text recognition could not start.");
        using var input = new MemoryStream(pdf).AsRandomAccessStream();
        var doc = await WinPdf.PdfDocument.LoadFromStreamAsync(input);
        using var page = doc.GetPage((uint)index);
        // Windows reports page sizes in device independent pixels (1/96 inch).
        var scale = dpi / 96.0;
        var widthPx = (uint)Math.Min(OcrEngine.MaxImageDimension, page.Size.Width * scale);
        scale = widthPx / page.Size.Width;
        using var png = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(png, new WinPdf.PdfPageRenderOptions { DestinationWidth = widthPx });
        var decoder = await BitmapDecoder.CreateAsync(png);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);

        var toPoints = 72.0 / 96.0 / scale;
        var words = new List<Word>();
        foreach (var line in result.Lines)
        {
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                words.Add(new Word(w.Text, r.X * toPoints, (r.X + r.Width) * toPoints, r.Y * toPoints, (r.Y + r.Height) * toPoints));
            }
        }
        return new PageWords { Number = original.Number, Width = original.Width, Height = original.Height, Words = words, FromOcr = true };
    }

    /// <summary>
    /// Reads the picture of a scanned page. Small pictures (screenshots, low-resolution scans) are enlarged smoothly
    /// first: text recognition reads small digits and decimal points much better at about 2,600 pixels across.
    /// </summary>
    private static async Task<PageWords> OcrImageAsync(byte[] image, double left, double top, double width, double height, PageWords original, Windows.Globalization.Language language)
    {
        var engine = OcrEngine.TryCreateFromLanguage(language) ?? throw new InvalidOperationException("Windows text recognition could not start.");
        using var input = new MemoryStream(image).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(input);
        var scale = Math.Clamp(2600.0 / decoder.PixelWidth, 1, 4);
        scale = Math.Min(scale, OcrEngine.MaxImageDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)(decoder.PixelWidth * scale),
            ScaledHeight = (uint)(decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);
        double sx = width / transform.ScaledWidth, sy = height / transform.ScaledHeight;
        var words = new List<Word>();
        foreach (var line in result.Lines)
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                words.Add(new Word(w.Text, left + r.X * sx, left + (r.X + r.Width) * sx, top + r.Y * sy, top + (r.Y + r.Height) * sy));
            }
        return new PageWords { Number = original.Number, Width = original.Width, Height = original.Height, Words = words, FromOcr = true };
    }

    [GeneratedRegex(@"^(?<lead>[-+(]?\$?)(?<num>[\dOoSsIl]{1,3}(?:,[\dOoSsIl]{3})*[\dOoSsIl]*)(?<trail>-|\))?$")]
    private static partial Regex DigitsOnly();

    /// <summary>
    /// Text recognition often drops the decimal point of small printed amounts ("27500" for 275.00) or reads a 0 as
    /// an "o" ("95.oo"). A number of three digits or more with no decimals, aligned on the right edge of a column of
    /// amounts that do have cents, is read with its last two digits as cents; letters standing for digits inside a
    /// number are read as those digits. The statement's balances then check every amount.
    /// </summary>
    internal static IReadOnlyList<Word> RepairAmounts(IReadOnlyList<Word> words)
    {
        static string Digits(string t) => new([.. t.Select(c => c switch { 'O' or 'o' => '0', 'S' or 's' => '5', 'I' or 'l' => '1', _ => c })]);
        words = JoinNumberPieces(words);
        var fixedWords = words.Select(w =>
        {
            // "95.oo", "1o.00", "6,0O4.2O": letters inside an amount that has cents.
            var m = Regex.Match(w.Text, @"^([-+(]?\$?)([\dOoSsIl,]*\d[\dOoSsIl,]*[.,][\dOoSsIl]{2})(-|\))?$");
            return m.Success && Regex.IsMatch(m.Groups[2].Value, @"[OoSsIl]") ? w with { Text = m.Groups[1].Value + Digits(m.Groups[2].Value) + m.Groups[3].Value } : w;
        }).ToList();
        // Right edges of the amounts printed with cents: the columns of money.
        var edges = fixedWords.Where(w => Text.AmountParser.IsAmount(w.Text)).Select(w => w.Right).ToList();
        if (edges.Count < 2) return fixedWords;
        var tolerance = Math.Max(6, fixedWords.Where(w => Text.AmountParser.IsAmount(w.Text)).Average(w => w.Height) * 1.2);
        return fixedWords.Select(w =>
        {
            if (Text.AmountParser.IsAmount(w.Text)) return w;
            var m = DigitsOnly().Match(w.Text);
            if (!m.Success) return w;
            var digits = Digits(m.Groups["num"].Value).Replace(",", "");
            if (digits.Length < 3 || !digits.All(char.IsDigit) || digits.Count(char.IsDigit) < 2) return w;
            // Two or more columns of money agree on this right edge.
            if (edges.Count(e => Math.Abs(e - w.Right) <= tolerance) < 2) return w;
            var number = decimal.Parse(digits, CultureInfo.InvariantCulture) / 100m;
            return w with { Text = m.Groups["lead"].Value + number.ToString("#,##0.00", CultureInfo.InvariantCulture) + m.Groups["trail"].Value };
        }).ToList();
    }

    /// <summary>
    /// Pieces of one number that text recognition split apart ("1" "oono" for 100.00, "57" "96." for 57.96): side by
    /// side on the same line, closer than a character's height, both only digits (or letters read for digits).
    /// </summary>
    private static List<Word> JoinNumberPieces(IReadOnlyList<Word> words)
    {
        static bool Numeric(string t) => Regex.IsMatch(t, @"^[-+(]?\$?[\dOoSsIl.,]+[)\-]?$") && t.Count(char.IsDigit) >= 1;
        var outList = new List<Word>();
        foreach (var w in words.OrderBy(w => w.Top).ThenBy(w => w.Left))
        {
            var prev = outList.Count > 0 ? outList[^1] : null;
            // A short piece only (a reference number next to another one stays two numbers).
            if (prev is not null && Numeric(prev.Text) && Regex.IsMatch(w.Text, @"^[\dOoSsIl.,]+(?:-[\dOoSsIl]{2})?[)\-]?$")
                && Math.Min(prev.Text.Length, w.Text.Length) <= 3 && (prev.Text + w.Text).Count(char.IsLetterOrDigit) <= 9
                && Math.Abs(prev.CenterY - w.CenterY) < Math.Min(prev.Height, w.Height) * 0.5
                && w.Left - prev.Right is >= -1 and var gap && gap < Math.Min(prev.Height, w.Height) * 0.9)
            {
                // "oo-oo": a decimal point read as a dash.
                var text = Regex.Replace((prev.Text + w.Text).TrimEnd('.'), @"(?<=[\dOoSsIl])-(?=[\dOoSsIl]{2}$)", ".");
                outList[^1] = new Word(text, prev.Left, w.Right, Math.Min(prev.Top, w.Top), Math.Max(prev.Bottom, w.Bottom));
                continue;
            }
            outList.Add(w);
        }
        return outList;
    }

    /// <summary>
    /// Codes printed down the margin for the bank's mail sorting, one character above the other (Scotiabank prints
    /// one along the left edge): a dozen or more one- or two-character words in the outer fifth of the page, all at
    /// the same position, stacked a few points apart. They are not text of the statement, and left in they would join
    /// the lines beside them.
    /// </summary>
    internal static List<Word> RemoveMarginCodes(List<Word> words, double pageWidth)
    {
        var marks = words.Where(w => w.Text.Length <= 2 && (w.Right < pageWidth * 0.2 || w.Left > pageWidth * 0.8)).ToList();
        if (marks.Count < 12) return words;
        var remove = new HashSet<Word>();
        foreach (var column in marks.GroupBy(w => Math.Round(w.CenterX / 8)))
        {
            var stack = column.OrderBy(w => w.Top).ToList();
            if (stack.Count < 12) continue;
            var gaps = stack.Zip(stack.Skip(1), (a, b) => b.Top - a.Top).Where(g => g > 0).ToList();
            // Stacked characters: most of them a character's height apart or less.
            if (gaps.Count > 0 && gaps.Count(g => g <= Math.Max(8, stack.Average(w => w.Height) * 1.6)) >= gaps.Count * 0.6)
                foreach (var w in stack) remove.Add(w);
        }
        return remove.Count == 0 ? words : words.Where(w => !remove.Contains(w)).ToList();
    }

    /// <summary>A picture file (PNG, JPEG, WebP, BMP, GIF, TIFF), by its first bytes.</summary>
    public static bool IsImage(byte[] b) => b.Length > 12 && (
        (b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
        || (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        || (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        || (b[0] == 'B' && b[1] == 'M')
        || (b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
        || (b[0] == 'I' && b[1] == 'I' && b[2] == 42 && b[3] == 0) || (b[0] == 'M' && b[1] == 'M' && b[2] == 0 && b[3] == 42));

    /// <summary>
    /// A picture of a statement as a one-page PDF of the picture's size (96 pixels an inch), so it is read like a scanned
    /// statement (the page has no text, so it goes through text recognition) and its columns can be set on it.
    /// </summary>
    public static async Task<byte[]> ImageToPdfAsync(byte[] image)
    {
        // Decoded by Windows (any format it knows) and written again as PNG, which the PDF writer always takes.
        using var input = new MemoryStream(image).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(input);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var png = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, png);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[png.Size];
        png.Seek(0);
        await png.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        float width = bitmap.PixelWidth * 0.75f, height = bitmap.PixelHeight * 0.75f;
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(width, height, QuestPDF.Infrastructure.Unit.Point);
            page.Margin(0);
            page.Content().Image(bytes).FitArea();
        })).GeneratePdf();
    }

    /// <summary>Renders one page to a PNG, for the column template editor.</summary>
    public static async Task<byte[]> RenderPagePngAsync(byte[] pdf, int index, uint widthPx)
    {
        using var input = new MemoryStream(pdf).AsRandomAccessStream();
        var doc = await WinPdf.PdfDocument.LoadFromStreamAsync(input);
        if (index < 0 || index >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(index));
        using var page = doc.GetPage((uint)index);
        using var png = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(png, new WinPdf.PdfPageRenderOptions { DestinationWidth = widthPx });
        var bytes = new byte[png.Size];
        png.Seek(0);
        await png.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }
}
