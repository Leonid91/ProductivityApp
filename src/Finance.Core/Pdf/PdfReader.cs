using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace ProductivityApp.Finance.Pdf;

/// <summary>
/// Gets positioned words out of a PDF. Pages with a text layer are read directly; pages that are
/// only a picture (scans, photos printed to PDF) go through the text recognition built into Windows.
/// Everything runs on this machine.
/// </summary>
public static class PdfReader
{
    /// <summary>A page needs text recognition when it has (almost) no text but shows an image.</summary>
    private const int MinWordsForTextLayer = 6;

    public static async Task<IReadOnlyList<PageWords>> ReadAsync(byte[] pdf, CancellationToken ct = default)
    {
        var pages = new List<PageWords>();
        var needOcr = new List<int>();
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
                if (words.Count < MinWordsForTextLayer && page.GetImages().Any())
                    needOcr.Add(pages.Count);
                pages.Add(new PageWords { Number = page.Number, Width = page.Width, Height = page.Height, Words = words });
            }
        }

        if (needOcr.Count > 0)
        {
            var language = PickOcrLanguage();
            foreach (var index in needOcr)
            {
                ct.ThrowIfCancellationRequested();
                pages[index] = await OcrPageAsync(pdf, index, pages[index], language).ConfigureAwait(false);
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
