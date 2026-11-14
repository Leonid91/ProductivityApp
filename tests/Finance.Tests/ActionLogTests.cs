using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Tests;

/// <summary>The log of every action: one file, read newest first, cleared, moved to another folder.</summary>
public sealed class ActionLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "finances-log-tests-" + Guid.NewGuid().ToString("N"));

    public ActionLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
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
        Assert.Equal("button 250", first[0].Detail);
        Assert.Equal("button 151", first[^1].Detail);
        var (_, last) = log.Read(2, 100);
        Assert.Equal(50, last.Count);
        Assert.Equal("button 50", last[0].Detail);
        Assert.Equal("button 1", last[^1].Detail);
        Assert.Empty(log.Read(3, 100).Entries);
        Assert.All(first, e => { Assert.Equal("you", e.Source); Assert.Equal("Clicked", e.Action); Assert.Equal("info", e.Level); });
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
        Assert.Equal("Boom: a\tb\n   at Somewhere()\n   at Elsewhere() C:\\path\\file.cs", entries[1].Detail);
        Assert.Equal("error", entries[1].Level);
        // One line each in the file (read as a text editor does, while the app writes it).
        using var file = new FileStream(Path.Combine(_dir, ActionLog.FileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Equal(2, new StreamReader(file).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
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
        Assert.Equal("after", entries[0].Detail);
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
            Assert.Equal(["after the move", "before the move"], log.Read(0, 10).Entries.Select(e => e.Detail));
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
        Assert.Equal("App started", entries[0].Action);
        Assert.Equal("startup", entries[1].Action);
        Assert.Equal("IOException: Disk full", entries[1].Detail);
        Assert.Equal("bridge", entries[2].Action);
        Assert.Equal("InvalidCastException: Bad cast\n   at A.B()\n   at C.D()", entries[2].Detail.Replace("\r", ""));
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
        Assert.False(entries[0].More);
        Assert.True(entries[1].More);
        Assert.True(entries[1].Detail.Length <= 4000);
        Assert.StartsWith("request: {}\nanswer: {", entries[1].Detail);
        var full = log.ReadEntry(entries[1].N)!;
        Assert.False(full.More);
        Assert.Equal("request: {}\nanswer: " + answer, full.Detail);
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
        Assert.Equal("to 4", entries[1].Detail);
    }
}
