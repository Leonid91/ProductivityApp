using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// The log of every action: one file of three columns (date to the second, type, details with the call stack), read
/// newest first, cleared, moved to another folder.
/// </summary>
public sealed class ActionLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "finances-log-tests-" + Guid.NewGuid().ToString("N"));

    public ActionLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>The details of an entry without its first line (who and what) and its call stack.</summary>
    private static string Body(ActionLog.Entry e)
    {
        var text = e.Detail.Replace("\r", "");
        var start = text.IndexOf('\n') + 1;
        var stack = text.LastIndexOf("\nCall stack:\n", StringComparison.Ordinal);
        return start <= 0 || stack < start ? "" : text[start..stack];
    }

    private static string Heading(ActionLog.Entry e) => e.Detail.Split('\n')[0];

    private string[] FileLines()
    {
        using var file = new FileStream(Path.Combine(_dir, ActionLog.FileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(file).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Entries_are_read_newest_first_page_by_page()
    {
        using var log = new ActionLog(_dir);
        for (var i = 1; i <= 250; i++) log.Info("you", "Clicked", $"button {i}");
        log.Flush();

        var (total, first) = log.Read(0, 100);
        Assert.Equal(250, total);
        Assert.Equal(100, first.Count);
        Assert.Equal("button 250", Body(first[0]));
        Assert.Equal("button 151", Body(first[^1]));
        var (_, last) = log.Read(2, 100);
        Assert.Equal(50, last.Count);
        Assert.Equal("button 50", Body(last[0]));
        Assert.Equal("button 1", Body(last[^1]));
        Assert.Empty(log.Read(3, 100).Entries);
        Assert.All(first, e => { Assert.Equal("You: Clicked", Heading(e)); Assert.Equal("info", e.Level); });
    }

    [Fact]
    public void Every_line_has_three_columns_the_date_to_the_second_and_a_call_stack()
    {
        using var log = new ActionLog(_dir);
        log.Debug("app", "Decode", "read as Windows-1252");
        log.Info("engine", "settings.set", "{\"key\":\"theme\"}");
        log.Write("warning", "app", "Caught exception", DateTime.Now, "where: here");
        log.Error("app", "Exception (ui)", "System.Exception: boom");
        log.Flush();

        var lines = FileLines();
        Assert.Equal(4, lines.Length);
        Assert.All(lines, l =>
        {
            var f = l.Split('\t');
            Assert.Equal(3, f.Length);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", f[0]);
            Assert.Contains(f[1], ActionLog.Levels);
        });
        Assert.Equal(["debug", "info", "warning", "error"], lines.Select(l => l.Split('\t')[1]));
        // The call stack of the code that wrote each entry: this test, the log's own frames left out.
        Assert.All(log.Read(0, 10).Entries, e =>
        {
            Assert.Contains("\nCall stack:\n", e.Detail);
            var stack = e.Detail[(e.Detail.IndexOf("\nCall stack:\n", StringComparison.Ordinal) + 13)..];
            Assert.Contains(nameof(Every_line_has_three_columns_the_date_to_the_second_and_a_call_stack), stack.Split('\n')[0]);
        });
        Assert.Throws<ArgumentException>(() => log.Write("verbose", "app", "x", DateTime.Now, ""));
    }

    [Fact]
    public void Tabs_and_line_breaks_stay_inside_their_entry()
    {
        using var log = new ActionLog(_dir);
        log.Error("app", "Error in bridge", "Boom: a\tb\n   at Somewhere()\n   at Elsewhere() C:\\path\\file.cs");
        log.Info("engine", "settings.set", "{\"key\":\"theme\",\"value\":\"dark\"}");
        log.Flush();

        var (total, entries) = log.Read(0, 10);
        Assert.Equal(2, total);
        Assert.Equal("Boom: a\tb\n   at Somewhere()\n   at Elsewhere() C:\\path\\file.cs", Body(entries[1]));
        Assert.Equal("App: Error in bridge", Heading(entries[1]));
        Assert.Equal("error", entries[1].Level);
        // One line each in the file (read as a text editor does, while the app writes it).
        Assert.Equal(2, FileLines().Length);
    }

    [Fact]
    public void A_stack_taken_elsewhere_is_written_as_given()
    {
        using var log = new ActionLog(_dir);
        log.WriteWithStack("info", "app", "page", DateTime.Now, "    at go (https://finances.localhost/assets/index.js:1:200)", "{\"to\":\"logs\"}");
        log.Flush();
        var e = log.Read(0, 1).Entries[0];
        Assert.Equal("App: page\n{\"to\":\"logs\"}\nCall stack:\n    at go (https://finances.localhost/assets/index.js:1:200)", e.Detail);
    }

    [Fact]
    public void Clearing_empties_the_file_and_later_entries_go_on()
    {
        using var log = new ActionLog(_dir);
        for (var i = 0; i < 20; i++) log.Info("you", "Pressed", "Enter");
        log.Flush();
        log.Clear();
        Assert.Equal(0, log.Read(0, 100).Total);
        Assert.Equal(0, new FileInfo(log.FilePath).Length);

        log.Info("you", "Clicked", "after");
        log.Flush();
        var (total, entries) = log.Read(0, 100);
        Assert.Equal(1, total);
        Assert.Equal("after", Body(entries[0]));
    }

    [Fact]
    public void The_log_moves_to_another_folder_and_stays_there()
    {
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        using (var log = new ActionLog(_dir))
        {
            log.Info("you", "Clicked", "before the move");
            log.Flush();
            log.MoveTo(elsewhere);
            Assert.Equal(Path.Combine(elsewhere, ActionLog.FileName), log.FilePath);
            Assert.False(File.Exists(Path.Combine(_dir, ActionLog.FileName)));
            log.Info("you", "Clicked", "after the move");
            log.Flush();
            Assert.Equal(["after the move", "before the move"], log.Read(0, 10).Entries.Select(Body));
            // A folder that already holds a log is refused.
            Directory.CreateDirectory(Path.Combine(_dir, "other"));
            File.WriteAllText(Path.Combine(_dir, "other", ActionLog.FileName), "");
            Assert.Throws<IOException>(() => log.MoveTo(Path.Combine(_dir, "other")));
        }
        using (var again = new ActionLog(_dir))
        {
            // The next start finds it where it was moved, every entry still read.
            Assert.Equal(Path.Combine(elsewhere, ActionLog.FileName), again.FilePath);
            Assert.Equal(2, again.Read(0, 10).Total);
            again.MoveTo(_dir);
            Assert.Equal(Path.Combine(_dir, ActionLog.FileName), again.FilePath);
            Assert.False(File.Exists(Path.Combine(_dir, "log-location.txt")));
            Assert.Equal(2, again.Read(0, 10).Total);
        }
    }

    [Fact]
    public void A_log_of_five_columns_becomes_three()
    {
        File.WriteAllText(Path.Combine(_dir, ActionLog.FileName),
            "2026-11-15 22:39:10.298\tinfo\tengine\tlogs.clear\tstarted: 2026-11-15 22:39:10.298\\nrequest: {}\n" +
            "2026-11-15 22:39:12.119\terror\tapp\tException (ui)\tSystem.Runtime.InteropServices.COMException (0x8007139F): not in the correct state\\r\\n   at A.B()\n");
        using var log = new ActionLog(_dir);
        log.Info("app", "Start");
        log.Flush();
        var (total, entries) = log.Read(0, 10);
        Assert.Equal(3, total);
        Assert.Equal("App: Start", Heading(entries[0]));
        Assert.Equal(("2026-11-15 22:39:12", "error"), (entries[1].Time, entries[1].Level));
        Assert.Equal("App: Exception (ui)\nSystem.Runtime.InteropServices.COMException (0x8007139F): not in the correct state\r\n   at A.B()\n" +
            "Call stack: not recorded (written before call stacks were logged)", entries[1].Detail);
        Assert.Equal(("2026-11-15 22:39:10", "Engine: logs.clear"), (entries[2].Time, Heading(entries[2])));
        Assert.All(FileLines(), l => Assert.Equal(3, l.Split('\t').Length));
    }

    [Fact]
    public void An_older_error_log_becomes_entries()
    {
        File.WriteAllText(Path.Combine(_dir, ActionLog.FileName),
            "2026-11-01T10:00:00.0000000-04:00 [bridge] InvalidCastException: Bad cast\r\n   at A.B()\r\n   at C.D()\r\n" +
            "2026-11-02T11:30:00.0000000-04:00 [startup] IOException: Disk full\r\n\r\n");
        using var log = new ActionLog(_dir);
        log.Info("app", "App started");
        log.Flush();
        var (total, entries) = log.Read(0, 10);
        Assert.Equal(3, total);
        Assert.Equal("App: App started", Heading(entries[0]));
        Assert.Equal("App: startup\nIOException: Disk full\nCall stack:\n(empty)", entries[1].Detail);
        Assert.Equal("App: bridge\nInvalidCastException: Bad cast\nCall stack:\n   at A.B()\n   at C.D()", entries[2].Detail.Replace("\r", ""));
        Assert.StartsWith("2026-11-01 ", entries[2].Time);
    }

    [Fact]
    public void A_long_entry_is_cut_on_the_page_and_read_in_full()
    {
        using var log = new ActionLog(_dir);
        var answer = "{\"result\":\"" + new string('é', 300_000) + "\"}";
        log.Write("info", "engine", "periods.report", DateTime.Now, "request: {}", "\nanswer: ", answer);
        log.Info("you", "click", "short");
        log.Flush();

        var (total, entries) = log.Read(0, 10);
        Assert.Equal(2, total);
        // A short entry is cut only within its call stack: its details are all on the page.
        Assert.StartsWith("You: click\nshort\nCall stack:\n", entries[0].Detail);
        Assert.True(entries[1].More);
        Assert.True(entries[1].Detail.Length <= 4000);
        Assert.StartsWith("Engine: periods.report\nrequest: {}\nanswer: {", entries[1].Detail);
        var full = log.ReadEntry(entries[1].N)!;
        Assert.False(full.More);
        Assert.Equal("request: {}\nanswer: " + answer, Body(full));
        Assert.Null(log.ReadEntry(5));
    }

    [Fact]
    public void Entries_survive_a_restart()
    {
        using (var log = new ActionLog(_dir))
        {
            for (var i = 0; i < 5; i++) log.Info("you", "Scrolled", $"to {i}");
        }
        using var again = new ActionLog(_dir);
        again.Info("app", "App started");
        again.Flush();
        var (total, entries) = again.Read(0, 10);
        Assert.Equal(6, total);
        Assert.Equal("to 4", Body(entries[1]));
    }
}
