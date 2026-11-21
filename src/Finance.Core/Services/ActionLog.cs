using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ProductivityApp.Finance.Services;

/// <summary>
/// The log of everything that happens: every action of the person in the window (the raw event), every request the
/// window makes to the engine with its full parameters and full answer, every error with its full text and call stack,
/// the window's own events. One line per entry in a text file, three columns separated by tabs: the date to the second,
/// the type (debug, info, warning, error) and the details: who wrote it and what ("Engine: holidays.save"), everything
/// about it, then the full call stack of where it was written. Written on a thread of its own so nothing waits for the disk. The file lives in the data folder, or in the folder the
/// person chose (Settings > Data > Log location).
/// </summary>
public sealed class ActionLog : IDisposable
{
    public const string FileName = "app.log";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    /// <summary>The types of entry, from the least to the most serious.</summary>
    public static readonly IReadOnlyList<string> Levels = ["debug", "info", "warning", "error"];
    /// <summary>A page of the log reads this much of each entry at most; the rest is read when asked (<see cref="ReadEntry"/>).</summary>
    private const int ShownBytes = 16 * 1024;
    private const int ShownChars = 4000;

    /// <summary>An entry; More: the details go on beyond what was read (read in full with <see cref="ReadEntry"/>).</summary>
    public sealed record Entry(long N, string Time, string Level, string Detail, bool More, long Size);

    /// <summary>An entry waiting to be written: its details in parts, written one after the other (never joined in memory).</summary>
    private sealed record Pending(DateTime Time, string Level, string Heading, string[] Detail, string Stack);

    private readonly string _dataDir;
    private readonly BlockingCollection<Pending> _queue = new();
    private readonly Thread _writer;
    private readonly object _gate = new();
    /// <summary>Where each line of the file starts: a page of the log is read without reading the rest.</summary>
    private readonly List<long> _starts = [];
    private FileStream? _file;
    private readonly ManualResetEventSlim _ready = new();
    private long _lastChanged;
    private long _added, _done;

    /// <summary>Raised (on the writer's thread, at most a few times a second) when entries were written or cleared, while <see cref="Watched"/>.</summary>
    public event Action? Changed;

    /// <summary>Told of each entry as it is written (type, heading, details): the first launch's own log.</summary>
    public Action<string, string, string[]>? Mirror { get; set; }

    /// <summary>The Logs page is open: only then is it told of new entries (nothing is sent to the window otherwise).</summary>
    public bool Watched { get; set; }

    public ActionLog(string dataDir)
    {
        _dataDir = dataDir;
        _writer = new Thread(Run) { IsBackground = true, Name = "log writer", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    /// <summary>Holds the folder the person moved the log to.</summary>
    private string LocationFile => Path.Combine(_dataDir, "log-location.txt");

    /// <summary>Folder of the log file: the data folder, or the one the person chose (while it exists).</summary>
    public string Folder => File.Exists(LocationFile) && File.ReadAllText(LocationFile).Trim() is { Length: > 0 } f && Directory.Exists(f) ? f : _dataDir;

    public string FilePath => Path.Combine(Folder, FileName);

    // ---------------------------------------------------------------- writing

    public void Debug(string source, string action, string detail = "") => Write("debug", source, action, DateTime.Now, detail);

    public void Info(string source, string action, string detail = "") => Write("info", source, action, DateTime.Now, detail);

    public void Error(string source, string action, string detail) => Write("error", source, action, DateTime.Now, detail);

    /// <summary>
    /// An entry, with the call stack of the code that writes it; its details may come in parts (a request and its
    /// answer), written one after the other as they are.
    /// </summary>
    public void Write(string level, string source, string action, DateTime time, params string[] detail) =>
        WriteWithStack(level, source, action, time, CallStack(), detail);

    /// <summary>An entry whose call stack was taken elsewhere (the window's own, for what the window writes).</summary>
    public void WriteWithStack(string level, string source, string action, DateTime time, string stack, params string[] detail)
    {
        if (!Levels.Contains(level)) throw new ArgumentException($"Unknown type of log entry '{level}'.");
        Mirror?.Invoke(level, Heading(source, action), detail);
        try { _queue.Add(new Pending(time, level, Heading(source, action), detail, stack)); Interlocked.Increment(ref _added); }
        catch (InvalidOperationException) { /* closing: nothing more is written */ }
    }

    /// <summary>The call stack of the code that writes to the log (the log's own frames left out).</summary>
    public static string CallStack()
    {
        var own = typeof(ActionLog).FullName!;
        var frames = new StackTrace(1, true).GetFrames().SkipWhile(f => f.GetMethod()?.DeclaringType?.FullName?.StartsWith(own, StringComparison.Ordinal) == true);
        return new StackTrace(frames).ToString().TrimEnd();
    }

    /// <summary>The first line of an entry's details: who wrote it and what ("Engine: holidays.save").</summary>
    private static string Heading(string source, string action)
    {
        var who = source switch { "you" => "You", "app" => "App", "engine" => "Engine", "window" => "Window", _ => source };
        return action.Length == 0 ? who : who + ": " + action;
    }

    private const string NoStack = "Call stack: not recorded (written before call stacks were logged)";

    /// <summary>One line per entry: backslashes, tabs and line breaks inside a value are written as \\, \t, \r and \n.</summary>
    private static string Clean(string value) =>
        value.IndexOfAny(['\\', '\t', '\r', '\n']) < 0 ? value : value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");

    private static string Unclean(string value)
    {
        if (!value.Contains('\\')) return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var c = value[++i];
                sb.Append(c switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => c });
            }
            else sb.Append(value[i]);
        }
        return sb.ToString();
    }

    private void Run()
    {
        lock (_gate)
        {
            try { OpenFile(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _file = null; }
        }
        _ready.Set();
        foreach (var first in _queue.GetConsumingEnumerable())
        {
            var batch = new List<Pending> { first };
            while (batch.Count < 2000 && _queue.TryTake(out var more)) batch.Add(more);
            lock (_gate)
            {
                try
                {
                    _file ??= OpenFileOrNull();
                    if (_file is not null)
                    {
                        _file.Seek(0, SeekOrigin.End);
                        foreach (var e in batch)
                        {
                            _starts.Add(_file.Position);
                            Put(string.Join('\t', e.Time.ToString(TimeFormat, CultureInfo.InvariantCulture), e.Level, Clean(e.Heading)));
                            if (e.Detail.Any(p => p.Length > 0)) Put(Clean("\n"));
                            foreach (var part in e.Detail) Put(Clean(part));
                            Put(Clean("\nCall stack:\n" + (e.Stack.Length > 0 ? e.Stack : "(empty)")));
                            Put("\n");
                        }
                        _file.Flush();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A full or vanished disk must not take the app down: the entries are lost, the app goes on.
                    _file?.Dispose();
                    _file = null;
                    _starts.Clear();
                }
            }
            Interlocked.Add(ref _done, batch.Count);
            RaiseChanged();
        }
    }

    private void Put(string text)
    {
        if (text.Length == 0) return;
        _file!.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Waits until every entry written so far is in the file.</summary>
    public void Flush(TimeSpan? timeout = null)
    {
        var end = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds;
        while (Interlocked.Read(ref _done) < Interlocked.Read(ref _added) && Environment.TickCount64 < end) Thread.Sleep(1);
    }

    private void RaiseChanged()
    {
        // At most about four times a second, and only while the Logs page is open: it reloads on it.
        if (!Watched) return;
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastChanged) < 250 && _queue.Count > 0) return;
        Interlocked.Exchange(ref _lastChanged, now);
        try { Changed?.Invoke(); }
        catch (Exception) { /* the window may be closing */ }
    }

    private FileStream? OpenFileOrNull()
    {
        try { OpenFile(); return _file; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Opens the file (made if missing), turns an older log into entries, and finds where each line starts.</summary>
    private void OpenFile()
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        ConvertOlderLog();
        Index();
    }

    /// <summary>
    /// A log written by an earlier version becomes entries of three columns: the five-column lines (time to the
    /// millisecond, type, source, action, details) and, before them, error lines with their call stack on the lines after.
    /// </summary>
    private void ConvertOlderLog()
    {
        if (_file!.Length == 0) return;
        _file.Seek(0, SeekOrigin.Begin);
        var head = new byte[Math.Min(64, (int)_file.Length)];
        _file.ReadExactly(head);
        var start = Encoding.UTF8.GetString(head);
        if (start.Length > 20 && start[19] == '\t') return;
        var columns = start.Length > 24 && start[23] == '\t';
        _file.Seek(0, SeekOrigin.Begin);
        string text;
        using (var reader = new StreamReader(_file, Encoding.UTF8, true, 4096, leaveOpen: true)) text = reader.ReadToEnd();
        var lines = new List<string>();
        if (columns)
        {
            foreach (var line in text.Split('\n'))
            {
                var f = line.TrimEnd('\r').Split('\t', 5);
                if (f.Length < 5 || f[0].Length < 19) continue;
                var level = Levels.Contains(f[1]) ? f[1] : "info";
                lines.Add(string.Join('\t', f[0][..19], level, Clean(Heading(Unclean(f[2]), Unclean(f[3]))) + (f[4].Length > 0 ? Clean("\n") + f[4] : "") + Clean("\n" + NoStack)));
            }
        }
        else
        {
            string? time = null, where = null, message = null;
            var stack = new StringBuilder();
            void Flush()
            {
                if (time is null) return;
                lines.Add(string.Join('\t', time, "error", Clean(Heading("app", where!) + "\n" + message + "\nCall stack:\n" + (stack.Length > 0 ? stack.ToString().TrimEnd() : "(empty)"))));
                stack.Clear();
            }
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\S+) \[([^\]]*)\] (.*)$");
                if (m.Success && DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
                {
                    Flush();
                    time = t.ToString(TimeFormat, CultureInfo.InvariantCulture);
                    where = m.Groups[2].Value;
                    message = m.Groups[3].Value;
                }
                else if (time is not null && line.Length > 0) stack.AppendLine(line);
            }
            Flush();
        }
        var bytes = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
        _file.SetLength(0);
        _file.Write(bytes);
        _file.Flush();
    }

    private void Index()
    {
        _starts.Clear();
        var file = _file!;
        file.Seek(0, SeekOrigin.Begin);
        var buffer = new byte[1 << 16];
        long at = 0;
        var lineStart = true;
        int read;
        while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (lineStart) { _starts.Add(at + i); lineStart = false; }
                if (buffer[i] == (byte)'\n') lineStart = true;
            }
            at += read;
        }
    }

    // ---------------------------------------------------------------- reading

    /// <summary>A page of the log, newest entry first (page 0 is the latest); long details are cut (Entry.More).</summary>
    public (int Total, List<Entry> Entries) Read(int page, int size)
    {
        _ready.Wait(TimeSpan.FromSeconds(10));
        page = Math.Max(0, page);
        size = Math.Clamp(size, 1, 1000);
        lock (_gate)
        {
            var entries = new List<Entry>();
            if (_file is null) return (0, entries);
            // Changed outside the app (emptied in a text editor): found again.
            if (_starts.Count > 0 && _starts[^1] >= _file.Length) Index();
            var total = _starts.Count;
            var hi = total - 1 - page * size;
            var lo = Math.Max(0, hi - size + 1);
            for (var n = hi; n >= lo; n--) entries.Add(ReadLine(n, ShownBytes, ShownChars));
            return (total, entries);
        }
    }

    /// <summary>One entry in full (its number from <see cref="Read"/>), or null when the log no longer has it.</summary>
    public Entry? ReadEntry(long n)
    {
        _ready.Wait(TimeSpan.FromSeconds(10));
        lock (_gate)
        {
            if (_file is null || n < 0 || n >= _starts.Count) return null;
            return ReadLine((int)n, int.MaxValue, int.MaxValue);
        }
    }

    private Entry ReadLine(int n, int maxBytes, int maxChars)
    {
        var from = _starts[n];
        var to = n + 1 < _starts.Count ? _starts[n + 1] : _file!.Length;
        var size = to - from;
        var take = (int)Math.Min(size, maxBytes);
        var bytes = new byte[take];
        _file!.Seek(from, SeekOrigin.Begin);
        _file.ReadExactly(bytes);
        var line = Encoding.UTF8.GetString(bytes).TrimEnd('\n', '\r');
        var f = line.Split('\t', 3);
        if (f.Length < 3) return new Entry(n, "", "info", Unclean(line), take < size, size);
        var detail = f[2];
        var more = take < size || detail.Length > maxChars;
        if (detail.Length > maxChars) detail = detail[..maxChars];
        // A cut in the middle of a \n, \t or \\ written for the line: the half sign is left out.
        if (more && detail.EndsWith('\\') && (detail.Length - detail.TrimEnd('\\').Length) % 2 == 1) detail = detail[..^1];
        return new Entry(n, f[0], f[1], Unclean(detail), more, size);
    }

    // ---------------------------------------------------------------- clearing and moving

    /// <summary>Empties the log file (entries waiting to be written are dropped too).</summary>
    public void Clear()
    {
        while (_queue.TryTake(out _)) Interlocked.Increment(ref _done);
        _ready.Wait(TimeSpan.FromSeconds(10));
        lock (_gate)
        {
            _file ??= OpenFileOrNull();
            if (_file is null) throw new IOException("The log file cannot be opened: " + FilePath);
            _file.SetLength(0);
            _file.Flush();
            _starts.Clear();
        }
        RaiseChanged();
    }

    /// <summary>Moves the log file to another folder (any folder) and goes on writing it there.</summary>
    public void MoveTo(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("This folder does not exist: " + folder);
        _ready.Wait(TimeSpan.FromSeconds(10));
        lock (_gate)
        {
            var from = FilePath;
            if (string.Equals(folder, Path.TrimEndingDirectorySeparator(Folder), StringComparison.OrdinalIgnoreCase)) return;
            var to = Path.Combine(folder, FileName);
            if (File.Exists(to)) throw new IOException("This folder already holds a log file: " + to);
            _file?.Dispose();
            _file = null;
            try
            {
                if (File.Exists(from)) File.Copy(from, to);
                if (string.Equals(folder, Path.TrimEndingDirectorySeparator(_dataDir), StringComparison.OrdinalIgnoreCase)) File.Delete(LocationFile);
                else File.WriteAllText(LocationFile, folder);
                if (File.Exists(from)) File.Delete(from);
            }
            finally
            {
                _file = OpenFileOrNull();
            }
        }
        RaiseChanged();
    }

    /// <summary>Writes what is waiting, then closes the file.</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(10));
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}
