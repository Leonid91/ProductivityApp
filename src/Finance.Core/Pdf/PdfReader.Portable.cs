#if !WINDOWS
using BitMiracle.LibTiff.Classic;
using QuestPDF.Fluent;
using SkiaSharp;

namespace ProductivityApp.Finance.Pdf;

/// <summary>
/// The systems without Windows' own PDF drawing and text recognition (macOS, Linux): pages are drawn by PDFium, pictures
/// read by Skia (TIFF by LibTiff), words recognised by <see cref="TextRecognizer"/>. Same results as the Windows ones:
/// positions in PDF points from the page's top left.
/// </summary>
public static partial class PdfReader
{
    /// <summary>The largest side of a picture given to text recognition.</summary>
    private const int MaxOcrDimension = 10000;

    public static bool OcrAvailable => TextRecognizer.Current.Available;

    /// <summary>The systems PDFium and Skia ship for (this target runs on macOS and Linux).</summary>
    [System.Runtime.Versioning.SupportedOSPlatformGuard("linux")]
    [System.Runtime.Versioning.SupportedOSPlatformGuard("macos")]
    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    private static bool Desktop => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    private const string NoPdfium = "PDF pages can only be drawn on Windows, macOS and Linux.";

    /// <summary>The recogniser, with the reason when there is none (a page that needs it fails with that reason).</summary>
    private static TextRecognizer PickOcrLanguage() =>
        TextRecognizer.Current.Available ? TextRecognizer.Current : throw new InvalidOperationException(TextRecognizer.Current.Missing);

    private static async Task<PageWords> OcrPageAsync(byte[] pdf, int index, PageWords original, TextRecognizer recognizer)
    {
        if (!Desktop) throw new PlatformNotSupportedException(NoPdfium);
        const double dpi = 300;
        // Page sizes are in points (1/72 inch).
        var widthPx = (int)Math.Min(MaxOcrDimension, original.Width / 72.0 * dpi);
        using var bitmap = PDFtoImage.Conversion.ToImage(pdf, index, null, new PDFtoImage.RenderOptions { Width = widthPx, WithAspectRatio = true });
        var png = Encode(bitmap);
        var found = await recognizer.RecognizeAsync(png, bitmap.Width, bitmap.Height).ConfigureAwait(false);
        var toPoints = original.Width / bitmap.Width;
        var words = found.Select(w => new Word(w.Text, w.X * toPoints, (w.X + w.Width) * toPoints, w.Y * toPoints, (w.Y + w.Height) * toPoints)).ToList();
        return new PageWords { Number = original.Number, Width = original.Width, Height = original.Height, Words = words, FromOcr = true };
    }

    /// <summary>
    /// Reads the picture of a scanned page. Small pictures (screenshots, low-resolution scans) are enlarged smoothly
    /// first: text recognition reads small digits and decimal points much better at about 2,600 pixels across.
    /// </summary>
    private static async Task<PageWords> OcrImageAsync(byte[] image, double left, double top, double width, double height, PageWords original, TextRecognizer recognizer)
    {
        using var decoded = Decode(image);
        var scale = Math.Clamp(2600.0 / decoded.Width, 1, 4);
        scale = Math.Min(scale, MaxOcrDimension / (double)Math.Max(decoded.Width, decoded.Height));
        int w = (int)(decoded.Width * scale), h = (int)(decoded.Height * scale);
        using var scaled = w == decoded.Width && h == decoded.Height ? decoded.Copy() : decoded.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        var found = await recognizer.RecognizeAsync(Encode(scaled), w, h).ConfigureAwait(false);
        double sx = width / w, sy = height / h;
        var words = found.Select(r => new Word(r.Text, left + r.X * sx, left + (r.X + r.Width) * sx, top + r.Y * sy, top + (r.Y + r.Height) * sy)).ToList();
        return new PageWords { Number = original.Number, Width = original.Width, Height = original.Height, Words = words, FromOcr = true };
    }

    /// <summary>
    /// A picture of a statement as a one-page PDF of the picture's size (96 pixels an inch), so it is read like a scanned
    /// statement (the page has no text, so it goes through text recognition) and its columns can be set on it.
    /// </summary>
    public static Task<byte[]> ImageToPdfAsync(byte[] image)
    {
        // Decoded (any format: PNG, JPEG, WebP, BMP, GIF, TIFF) and written again as PNG, which the PDF writer always takes.
        using var bitmap = Decode(image);
        var bytes = Encode(bitmap);
        float width = bitmap.Width * 0.75f, height = bitmap.Height * 0.75f;
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        return Task.FromResult(Document.Create(doc => doc.Page(page =>
        {
            page.Size(width, height, QuestPDF.Infrastructure.Unit.Point);
            page.Margin(0);
            page.Content().Image(bytes).FitArea();
        })).GeneratePdf());
    }

    /// <summary>Renders one page to a PNG, for the column template editor.</summary>
    public static Task<byte[]> RenderPagePngAsync(byte[] pdf, int index, uint widthPx)
    {
        if (!Desktop) throw new PlatformNotSupportedException(NoPdfium);
        if (index < 0 || index >= PDFtoImage.Conversion.GetPageCount(pdf)) throw new ArgumentOutOfRangeException(nameof(index));
        using var bitmap = PDFtoImage.Conversion.ToImage(pdf, index, null, new PDFtoImage.RenderOptions { Width = (int)widthPx, WithAspectRatio = true });
        return Task.FromResult(Encode(bitmap));
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("The picture could not be written.");
        return data.ToArray();
    }

    /// <summary>A picture file decoded, turned the way its camera says (EXIF orientation), on a white background.</summary>
    internal static SKBitmap Decode(byte[] image)
    {
        if (image.Length > 4 && ((image[0] == 'I' && image[1] == 'I' && image[2] == 42) || (image[0] == 'M' && image[1] == 'M' && image[3] == 42)))
            return DecodeTiff(image);
        using var codec = SKCodec.Create(new MemoryStream(image)) ?? throw new InvalidDataException("This picture could not be read.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var raw = new SKBitmap(info);
        var result = codec.GetPixels(info, raw.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            raw.Dispose();
            throw new InvalidDataException($"This picture could not be read ({result}).");
        }
        using (raw) return Flatten(Orient(raw, codec.EncodedOrigin));
    }

    private static SKBitmap DecodeTiff(byte[] image)
    {
        using var tiff = Tiff.ClientOpen("picture", "r", new MemoryStream(image), new TiffStream()) ?? throw new InvalidDataException("This picture could not be read.");
        var width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        var height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        var raster = new int[width * height];
        // ReadRGBAImageOriented gives ABGR words, rows from the top (the orientation the file asks for applied).
        if (!tiff.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT)) throw new InvalidDataException("This picture could not be read.");
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(raster, 0, bitmap.GetPixels(), raster.Length);
        using (bitmap) return Flatten(bitmap);
    }

    /// <summary>Transparent parts become white, as Windows' decoder shows them to text recognition.</summary>
    private static SKBitmap Flatten(SKBitmap source)
    {
        var flat = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(flat);
        canvas.Clear(SKColors.White);
        using (var picture = SKImage.FromBitmap(source)) canvas.DrawImage(picture, 0, 0, SKSamplingOptions.Default);
        return flat;
    }

    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return source.Copy();
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = swap ? source.Height : source.Width, h = swap ? source.Width : source.Height;
        var turned = new SKBitmap(new SKImageInfo(w, h, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(turned);
        // The matrix that takes the stored pixels to the way the picture is shown.
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        canvas.SetMatrix(m);
        using (var picture = SKImage.FromBitmap(source)) canvas.DrawImage(picture, 0, 0, SKSamplingOptions.Default);
        return turned;
    }
}
#endif
