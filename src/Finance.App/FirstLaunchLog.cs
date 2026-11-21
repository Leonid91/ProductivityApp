using System.Globalization;
using System.Text;

namespace ProductivityApp.Host;

/// <summary>
/// The log of an install's first launch, to copy and send when the app does not open as it should: a short text file,
/// written only while the first launch of a build runs (a later launch of the same build writes nothing), at most 1 MB.
/// It holds the system, the app's build, each step of the start, each request of the interface (its name and whether
/// it worked, never its data), every error and warning in full, and what the interface drew. The start scripts of the
/// repository (start.sh) write their own steps first, in the same file.
/// File: first-launch.txt in the app's folder of the user's app data (next to the Finances data folder):
///   Windows  %LOCALAPPDATA%\ProductivityApp\first-launch.txt
///   macOS    ~/Library/Application Support/ProductivityApp/first-launch.txt
///   Linux    ~/.local/share/ProductivityApp/first-launch.txt
/// PRODUCTIVITYAPP_FIRST_LAUNCH_LOG=&lt;file&gt; writes it elsewhere (tests).
/// </summary>
internal sealed class FirstLaunchLog : IDisposable
{
    public const string FileName = "first-launch.txt";
    private const long MaxBytes = 1024 * 1024;
    private const int MaxDetail = 8 * 1024;
    /// <summary>Each part of an error's details is cut beyond this; its call stack never is.</summary>
    private const int MaxErrorPart = 16 * 1024;
    private const string BuildMark = "build: ";

    /// <summary>This process's log, while it is the first launch of its build (null otherwise).</summary>
    public static FirstLaunchLog? Current { get; private set; }

    /// <summary>The app's log is mirrored here (<see cref="Mirror"/>): its errors come with their call stacks that way.</summary>
    public bool Mirrored { get; set; }

    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _full;

    private FirstLaunchLog(StreamWriter writer) => _writer = writer;

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("PRODUCTIVITYAPP_FIRST_LAUNCH_LOG") is { Length: > 0 } path ? Path.GetFullPath(path)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "ProductivityApp", FileName);

    /// <summary>
    /// Starts the log when this is the first launch of this build (<see cref="Current"/>), with what the app runs on.
    /// Test runs (their own data folder) write none, unless PRODUCTIVITYAPP_FIRST_LAUNCH_LOG names a file.
    /// </summary>
    public static void Start(AppOptions options, string[] args, string window)
    {
        if (options.IsolatedRun && Environment.GetEnvironmentVariable("PRODUCTIVITYAPP_FIRST_LAUNCH_LOG") is null or "") return;
        try
        {
            var path = DefaultPath;
            var exe = Environment.ProcessPath ?? "";
            var info = File.Exists(exe) ? new FileInfo(exe) : null;
            var build = $"{exe} ({info?.Length ?? 0} bytes, {info?.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "?"} UTC)";
            var before = File.Exists(path) && new FileInfo(path).Length <= MaxBytes ? File.ReadAllText(path) : "";
            // This build already had its first launch.
            if (before.Contains(BuildMark + build + "\n", StringComparison.Ordinal)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // What a start script wrote just before (no launch in the file yet) is kept; an earlier build's launch is not.
            var keep = before.Length > 0 && !before.Contains("  " + BuildMark, StringComparison.Ordinal);
            var stream = new FileStream(path, keep ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var log = new FirstLaunchLog(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
            Current = log;
            log.Write("ProductivityApp, first launch of this build. Copy this whole file to report how the start went.");
            log.Write(BuildMark + build);
            log.Write("version: " + typeof(FirstLaunchLog).Assembly.GetName().Version);
            log.Write("system: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription + " (" + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture + ")");
            log.Write("app: " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture + ", " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + ", window: " + window);
            log.Write("language: " + CultureInfo.CurrentCulture.Name + ", time zone: " + TimeZoneInfo.Local.Id);
            log.Write("arguments: " + (args.Length == 0 ? "none" : string.Join(' ', args)));
            log.Write("data folder: " + options.DataDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No log rather than no app.
            Current = null;
        }
    }

    /// <summary>A line (or more), with the time of day; long text is cut (unless <paramref name="whole"/>).</summary>
    public void Write(string text, bool whole = false)
    {
        lock (_gate)
        {
            if (_full) return;
            try
            {
                if (!whole && text.Length > MaxDetail) text = text[..MaxDetail] + " [cut]";
                if (_writer.BaseStream.Length + text.Length * 3 > MaxBytes)
                {
                    _full = true;
                    _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  The log is full: nothing more is written.");
                    return;
                }
                _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text.Replace("\r\n", "\n").Replace("\n", "\n          "));
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { _full = true; }
        }
    }

    /// <summary>
    /// An entry of the app's log, mirrored: what happened (no details for ordinary steps, whose details can hold the
    /// person's data), warnings and errors in full with the full call stack of where they were written (an exception's
    /// own call stack is in its details). The interface's requests are written by the bridge itself.
    /// </summary>
    public void Mirror(string level, string heading, string[] detail, string stack)
    {
        if (heading.StartsWith("Engine:", StringComparison.Ordinal) && level is "info" or "debug") return;
        if (level is "warning" or "error")
        {
            var text = new StringBuilder(level).Append(": ").Append(heading).Append('\n');
            foreach (var part in detail) text.Append(part.Length > MaxErrorPart ? part[..MaxErrorPart] + " [cut]" : part);
            text.Append("\nCall stack:\n").Append(stack.Length > 0 ? stack : "(empty)");
            Write(text.ToString(), whole: true);
        }
        else if (level == "info") Write(heading);
    }

    public void Dispose()
    {
        Write("The app ended.");
        lock (_gate) { _full = true; _writer.Dispose(); }
        if (Current == this) Current = null;
    }
}
