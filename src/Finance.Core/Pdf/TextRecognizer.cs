#if !WINDOWS
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ProductivityApp.Finance.Pdf;

/// <summary>A word found by text recognition, in pixels of the picture from its top left.</summary>
public readonly record struct RecognizedWord(string Text, double X, double Y, double Width, double Height);

/// <summary>
/// Text recognition on the systems without Windows' own: macOS's built-in one (Vision), Tesseract elsewhere (Linux).
/// French recognition is preferred: it also reads English and keeps accents.
/// </summary>
public abstract class TextRecognizer
{
    public static TextRecognizer Current { get; } = OperatingSystem.IsMacOS() ? new VisionRecognizer() : new TesseractRecognizer();

    public abstract bool Available { get; }

    /// <summary>What to do when <see cref="Available"/> is false.</summary>
    public abstract string Missing { get; }

    /// <summary>The words of a PNG picture of the given size.</summary>
    public abstract Task<IReadOnlyList<RecognizedWord>> RecognizeAsync(byte[] png, int width, int height);
}

/// <summary>Tesseract's command line (the tesseract-ocr package), reading the picture from its input.</summary>
internal sealed class TesseractRecognizer : TextRecognizer
{
    private readonly Lazy<(string? Exe, string? Languages)> _setup = new(Find);

    public override bool Available => _setup.Value.Exe is not null && _setup.Value.Languages is not null;

    public override string Missing => "This page is a scanned image and text recognition is not installed. Install Tesseract (the tesseract-ocr package), then import the file again.";

    private static (string?, string?) Find()
    {
        var name = OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract";
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/usr/bin", "/usr/local/bin", "/opt/homebrew/bin"]);
        var exe = dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
        if (exe is null) return (null, null);
        try
        {
            var (code, output, _) = Run(exe, ["--list-langs"], null);
            // "List of available languages in ... (3):" then one per line.
            var langs = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).Where(l => l != "osd").ToList();
            if (code != 0 || langs.Count == 0) return (exe, null);
            var wanted = new[] { "fra", "eng" }.Where(langs.Contains).ToList();
            return (exe, wanted.Count > 0 ? string.Join('+', wanted) : langs[0]);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Services.Diagnostics.Report("Tesseract could not be started", e);
            return (null, null);
        }
    }

    public override Task<IReadOnlyList<RecognizedWord>> RecognizeAsync(byte[] png, int width, int height) => Task.Run(() =>
    {
        var (exe, languages) = _setup.Value;
        if (exe is null || languages is null) throw new InvalidOperationException(Missing);
        var (code, tsv, error) = Run(exe, ["stdin", "stdout", "-l", languages, "--dpi", "300", "tsv"], png);
        if (code != 0) throw new InvalidOperationException("Text recognition failed: " + error.Trim());
        // level page block par line word left top width height conf text; level 5 = a word.
        var words = new List<RecognizedWord>();
        foreach (var line in tsv.Split('\n').Skip(1))
        {
            var c = line.TrimEnd('\r').Split('\t');
            if (c.Length < 12 || c[0] != "5" || string.IsNullOrWhiteSpace(c[11])) continue;
            double N(int i) => double.Parse(c[i], CultureInfo.InvariantCulture);
            words.Add(new RecognizedWord(c[11].Trim(), N(6), N(7), N(8), N(9)));
        }
        return (IReadOnlyList<RecognizedWord>)words;
    });

    private static (int Code, string Output, string Error) Run(string exe, string[] args, byte[]? input)
    {
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start) ?? throw new InvalidOperationException("Tesseract could not be started.");
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            p.StandardInput.BaseStream.Write(input);
            p.StandardInput.Close();
        }
        p.WaitForExit();
        return (p.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}

/// <summary>
/// macOS's built-in text recognition (Vision's VNRecognizeTextRequest), called through the Objective-C runtime. Each
/// line found is split into its words, and each word's box asked of Vision.
/// </summary>
internal sealed class VisionRecognizer : TextRecognizer
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const string Vision = "/System/Library/Frameworks/Vision.framework/Vision";

    private readonly Lazy<bool> _available = new(() =>
    {
        try { return NativeLibrary.TryLoad(Vision, out _) && objc_getClass("VNRecognizeTextRequest") != 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { Services.Diagnostics.Report("Vision could not be loaded", e); return false; }
    });

    public override bool Available => _available.Value;

    public override string Missing => "This page is a scanned image and this Mac's text recognition could not be started.";

    public override Task<IReadOnlyList<RecognizedWord>> RecognizeAsync(byte[] png, int width, int height) => Task.Run(() =>
    {
        if (!Available) throw new InvalidOperationException(Missing);
        var pool = objc_autoreleasePoolPush();
        nint data = 0, source = 0, image = 0, handler = 0, request = 0;
        try
        {
            data = CFDataCreate(0, png, png.Length);
            source = CGImageSourceCreateWithData(data, 0);
            image = source == 0 ? 0 : CGImageSourceCreateImageAtIndex(source, 0, 0);
            if (image == 0) throw new InvalidDataException("The picture could not be given to text recognition.");

            request = Send(Send(Class("VNRecognizeTextRequest"), Sel("alloc")), Sel("init"));
            SendLong(request, Sel("setRecognitionLevel:"), 0); // VNRequestTextRecognitionLevelAccurate
            SendPtr(request, Sel("setRecognitionLanguages:"), Array(Str("fr-FR"), Str("en-US")));

            var options = Send(Class("NSDictionary"), Sel("dictionary"));
            handler = SendPtr(Send(Class("VNImageRequestHandler"), Sel("alloc")), Sel("initWithCGImage:options:"), image, options);
            if (SendBoolErr(handler, Sel("performRequests:error:"), Array(request), out var error) == 0)
                throw new InvalidOperationException("Text recognition failed: " + Text(Send(error, Sel("localizedDescription"))));

            var words = new List<RecognizedWord>();
            var results = Send(request, Sel("results"));
            for (nuint i = 0, n = Count(results); i < n; i++)
            {
                var candidates = SendULong(Send2(results, Sel("objectAtIndex:"), i), Sel("topCandidates:"), 1);
                if (Count(candidates) == 0) continue;
                var candidate = Send2(candidates, Sel("objectAtIndex:"), 0);
                var text = Text(Send(candidate, Sel("string")));
                // Each word of the line (UTF-16 offsets, as NSString counts them), with its own box.
                var start = -1;
                for (var k = 0; k <= text.Length; k++)
                {
                    var space = k == text.Length || char.IsWhiteSpace(text[k]);
                    if (!space && start < 0) start = k;
                    if (!space || start < 0) continue;
                    var box = SendRange(candidate, Sel("boundingBoxForRange:error:"), new NSRange((nuint)start, (nuint)(k - start)), out _);
                    if (box != 0 && Rect(box) is { } r)
                        // Vision's boxes are fractions of the picture, from its bottom left.
                        words.Add(new RecognizedWord(text[start..k], r.X * width, (1 - r.Y - r.Height) * height, r.Width * width, r.Height * height));
                    start = -1;
                }
            }
            return (IReadOnlyList<RecognizedWord>)words;
        }
        finally
        {
            if (handler != 0) Send(handler, Sel("release"));
            if (request != 0) Send(request, Sel("release"));
            if (image != 0) CFRelease(image);
            if (source != 0) CFRelease(source);
            if (data != 0) CFRelease(data);
            objc_autoreleasePoolPop(pool);
        }
    });

    private static nint Class(string name) => objc_getClass(name) is var c and not 0 ? c : throw new InvalidOperationException($"{name} is not available on this Mac.");

    private static nint Sel(string name) => sel_registerName(name);

    /// <summary>An autoreleased NSString.</summary>
    private static nint Str(string s) => SendStr(Class("NSString"), Sel("stringWithUTF8String:"), s);

    private static string Text(nint nsString) => nsString == 0 ? "" : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String"))) ?? "";

    private static nuint Count(nint array) => array == 0 ? 0 : (nuint)Send(array, Sel("count"));

    private static unsafe nint Array(params nint[] items)
    {
        fixed (nint* p = items) return SendArray(Class("NSArray"), Sel("arrayWithObjects:count:"), p, (nuint)items.Length);
    }

    /// <summary>The boundingBox of an observation, read through key-value coding (an NSValue holding a CGRect).</summary>
    private static unsafe (double X, double Y, double Width, double Height)? Rect(nint observation)
    {
        var value = SendPtr(observation, Sel("valueForKey:"), Str("boundingBox"));
        if (value == 0) return null;
        var r = stackalloc double[4];
        SendGetValue(value, Sel("getValue:size:"), r, (nuint)(4 * sizeof(double)));
        return (r[0], r[1], r[2], r[3]);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NSRange(nuint Location, nuint Length);

    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send2(nint receiver, nint selector, nuint a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendULong(nint receiver, nint selector, nuint a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendLong(nint receiver, nint selector, nint a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendPtr(nint receiver, nint selector, nint a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendPtr(nint receiver, nint selector, nint a, nint b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendStr(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern unsafe nint SendArray(nint receiver, nint selector, nint* items, nuint count);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte SendBoolErr(nint receiver, nint selector, nint a, out nint error);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendRange(nint receiver, nint selector, NSRange range, out nint error);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern unsafe void SendGetValue(nint receiver, nint selector, double* buffer, nuint size);
    [DllImport(CoreFoundation)] private static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint obj);
    [DllImport(ImageIO)] private static extern nint CGImageSourceCreateWithData(nint data, nint options);
    [DllImport(ImageIO)] private static extern nint CGImageSourceCreateImageAtIndex(nint source, nint index, nint options);
}
#endif
