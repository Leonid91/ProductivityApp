using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// The log of every action, written to a real file: one line of three columns per entry (date to the second, type,
/// details with the call stack), read newest first, cleared, moved to another folder, kept across restarts.
/// </summary>
public sealed class ActionLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "finances-log-tests-" + Guid.NewGuid().ToString("N"));

    public ActionLogTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); }
        catch (IOException) { }
    }

    private string LogFile => Path.Combine(_folder, ActionLog.FileName);

    /// <summary>The details of an entry without its first line (who and what) and its call stack (empty: an entry of no details).</summary>
    private static string Body(ActionLog.Entry e) =>
        System.Text.RegularExpressions.Regex.Match(e.Detail.Replace("\r", ""), @"^[^\n]*\n(?<body>[\s\S]*)\nCall stack:\n").Groups["body"].Value;

    private static string Heading(ActionLog.Entry e) => e.Detail.Split('\n')[0];

    /// <summary>The lines of the file, read while the log may still hold it open (as a text editor reads it).</summary>
    private string[] FileLines()
    {
        using var file = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(file).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary><paramref name="times"/> scrolls written by the person ("to 0", "to 1"…).</summary>
    private static void Scrolled(ActionLog log, int times) =>
        Enumerable.Range(0, times).ToList().ForEach(i => log.Info("you", "Scrolled", $"to {i}"));

    private ActionLog LogWith(int clicks)
    {
        var log = new ActionLog(_folder);
        Enumerable.Range(1, clicks).ToList().ForEach(i => log.Info("you", "Clicked", $"button {i}"));
        log.Flush();
        return log;
    }

    [Fact]
    public void Read_FirstPage_ReturnsTheNewestEntriesAndTheTotal()
    {
        using var log = LogWith(250);

        var (total, entries) = log.Read(0, 100);

        Assert.Equal(250, total);
        Assert.Equal(100, entries.Count);
        Assert.Equal(("button 250", "button 151"), (Body(entries[0]), Body(entries[^1])));
    }

    [Fact]
    public void Read_LastPage_ReturnsTheOldestEntries()
    {
        using var log = LogWith(250);

        var entries = log.Read(2, 100).Entries;

        Assert.Equal(50, entries.Count);
        Assert.Equal(("button 50", "button 1"), (Body(entries[0]), Body(entries[^1])));
    }

    [Fact]
    public void Read_PageAfterTheLast_ReturnsNoEntry()
    {
        using var log = LogWith(250);

        var entries = log.Read(3, 100).Entries;

        Assert.Empty(entries);
    }

    [Fact]
    public void Info_ByThePerson_IsHeadedWithWhoAndWhatAtTheInfoLevel()
    {
        using var log = LogWith(1);

        var entry = log.Read(0, 1).Entries.Single();

        Assert.Equal(("You: Clicked", "info"), (Heading(entry), entry.Level));
    }

    [Fact]
    public void Write_EveryLevel_WritesALineOfThreeColumnsDatedToTheSecond()
    {
        using var log = new ActionLog(_folder);

        log.Debug("app", "Decode", "read as Windows-1252");
        log.Info("engine", "settings.set", "{\"key\":\"theme\"}");
        log.Write("warning", "app", "Caught exception", DateTime.Now, "where: here");
        log.Error("app", "Exception (ui)", "System.Exception: boom");
        log.Flush();

        var columns = FileLines().Select(l => l.Split('\t')).ToList();
        Assert.All(columns, c => Assert.Equal(3, c.Length));
        Assert.All(columns, c => Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", c[0]));
        Assert.Equal(["debug", "info", "warning", "error"], columns.Select(c => c[1]));
    }

    [Fact]
    public void Write_AnyEntry_EndsWithTheCallStackOfTheCodeThatWroteIt()
    {
        using var log = new ActionLog(_folder);

        log.Info("you", "Clicked", "a button");
        log.Flush();

        var detail = log.Read(0, 1).Entries.Single().Detail;
        var stack = detail[(detail.IndexOf("\nCall stack:\n", StringComparison.Ordinal) + "\nCall stack:\n".Length)..];
        Assert.Contains(nameof(Write_AnyEntry_EndsWithTheCallStackOfTheCodeThatWroteIt), stack.Split('\n')[0]);
    }

    [Fact]
    public void Write_UnknownLevel_Throws()
    {
        using var log = new ActionLog(_folder);

        Assert.Throws<ArgumentException>(() => log.Write("verbose", "app", "x", DateTime.Now, ""));
    }

    [Fact]
    public void Error_DetailsWithTabsAndLineBreaks_StayInsideTheirEntryOnOneLineOfTheFile()
    {
        const string details = "Boom: a\tb\n   at Somewhere()\n   at Elsewhere() C:\\path\\file.cs";
        using var log = new ActionLog(_folder);

        log.Error("app", "Error in bridge", details);
        log.Info("engine", "settings.set", "{\"key\":\"theme\",\"value\":\"dark\"}");
        log.Flush();

        var older = log.Read(0, 10).Entries[1];
        Assert.Equal((details, "App: Error in bridge", "error"), (Body(older), Heading(older), older.Level));
        Assert.Equal(2, FileLines().Length);
    }

    [Fact]
    public void WriteWithStack_StackTakenInThePage_IsWrittenAsGiven()
    {
        using var log = new ActionLog(_folder);

        log.WriteWithStack("info", "app", "page", DateTime.Now, "    at go (https://finances.localhost/assets/index.js:1:200)", "{\"to\":\"logs\"}");
        log.Flush();

        Assert.Equal("App: page\n{\"to\":\"logs\"}\nCall stack:\n    at go (https://finances.localhost/assets/index.js:1:200)", log.Read(0, 1).Entries[0].Detail);
    }

    [Fact]
    public void Clear_LogWithEntries_EmptiesTheFile()
    {
        using var log = LogWith(20);

        log.Clear();

        Assert.Equal(0, log.Read(0, 100).Total);
        Assert.Equal(0, new FileInfo(log.FilePath).Length);
    }

    [Fact]
    public void Info_AfterClearing_IsTheOnlyEntry()
    {
        using var log = LogWith(20);
        log.Clear();

        log.Info("you", "Clicked", "after");
        log.Flush();

        var (total, entries) = log.Read(0, 100);
        Assert.Equal((1, "after"), (total, Body(entries[0])));
    }

    [Fact]
    public void MoveTo_EmptyFolder_MovesTheFileAndWritesThereFromThen()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_folder, "elsewhere")).FullName;
        using var log = LogWith(1);

        log.MoveTo(elsewhere);
        log.Info("you", "Clicked", "after the move");
        log.Flush();

        Assert.Equal(Path.Combine(elsewhere, ActionLog.FileName), log.FilePath);
        Assert.False(File.Exists(LogFile));
        Assert.Equal(["after the move", "button 1"], log.Read(0, 10).Entries.Select(Body));
    }

    [Fact]
    public void MoveTo_FolderThatHoldsALog_Throws()
    {
        var other = Directory.CreateDirectory(Path.Combine(_folder, "other")).FullName;
        File.WriteAllText(Path.Combine(other, ActionLog.FileName), "");
        using var log = LogWith(1);

        Assert.Throws<IOException>(() => log.MoveTo(other));
    }

    [Fact]
    public void Constructor_LogMovedBefore_FindsItWhereItWasMoved()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_folder, "elsewhere")).FullName;
        using (var log = LogWith(2)) log.MoveTo(elsewhere);

        using var again = new ActionLog(_folder);

        Assert.Equal(Path.Combine(elsewhere, ActionLog.FileName), again.FilePath);
        Assert.Equal(2, again.Read(0, 10).Total);
    }

    [Fact]
    public void MoveTo_BackToTheAppsFolder_ForgetsTheOtherPlaceAndKeepsTheEntries()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_folder, "elsewhere")).FullName;
        using (var log = LogWith(2)) log.MoveTo(elsewhere);
        using var again = new ActionLog(_folder);

        again.MoveTo(_folder);

        Assert.Equal(LogFile, again.FilePath);
        Assert.False(File.Exists(Path.Combine(_folder, "log-location.txt")));
        Assert.Equal(2, again.Read(0, 10).Total);
    }

    [Fact]
    public void Constructor_LogOfFiveColumns_RewritesItInThreeColumns()
    {
        File.WriteAllText(LogFile,
            "2026-11-15 22:39:10.298\tinfo\tengine\tlogs.clear\tstarted: 2026-11-15 22:39:10.298\\nrequest: {}\n" +
            "2026-11-15 22:39:12.119\terror\tapp\tException (ui)\tSystem.Runtime.InteropServices.COMException (0x8007139F): not in the correct state\\r\\n   at A.B()\n");

        using var log = new ActionLog(_folder);

        var entries = log.Read(0, 10).Entries;
        Assert.Equal(("2026-11-15 22:39:12", "error"), (entries[0].Time, entries[0].Level));
        Assert.Equal("App: Exception (ui)\nSystem.Runtime.InteropServices.COMException (0x8007139F): not in the correct state\r\n   at A.B()\n" +
            "Call stack: not recorded (written before call stacks were logged)", entries[0].Detail);
        Assert.Equal(("2026-11-15 22:39:10", "Engine: logs.clear"), (entries[1].Time, Heading(entries[1])));
        Assert.All(FileLines(), l => Assert.Equal(3, l.Split('\t').Length));
    }

    [Fact]
    public void Constructor_OlderErrorLog_ReadsEachErrorAsAnEntryWithItsStack()
    {
        File.WriteAllText(LogFile,
            "2026-11-01T10:00:00.0000000-04:00 [bridge] InvalidCastException: Bad cast\r\n   at A.B()\r\n   at C.D()\r\n" +
            "2026-11-02T11:30:00.0000000-04:00 [startup] IOException: Disk full\r\n\r\n");

        using var log = new ActionLog(_folder);

        var entries = log.Read(0, 10).Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal("App: startup\nIOException: Disk full\nCall stack:\n(empty)", entries[0].Detail);
        Assert.Equal("App: bridge\nInvalidCastException: Bad cast\nCall stack:\n   at A.B()\n   at C.D()", entries[1].Detail.Replace("\r", ""));
        Assert.StartsWith("2026-11-01 ", entries[1].Time);
    }

    [Fact]
    public void Read_VeryLongEntry_IsCutOnThePageAndSaysThereIsMore()
    {
        using var log = new ActionLog(_folder);
        log.Write("info", "engine", "periods.report", DateTime.Now, "request: {}", "\nanswer: ", "{\"result\":\"" + new string('é', 300_000) + "\"}");
        log.Flush();

        var entry = log.Read(0, 1).Entries.Single();

        Assert.True(entry.More);
        Assert.InRange(entry.Detail.Length, 1, 4000);
        Assert.StartsWith("Engine: periods.report\nrequest: {}\nanswer: {", entry.Detail);
    }

    [Fact]
    public void ReadEntry_VeryLongEntry_ReturnsItInFull()
    {
        var answer = "{\"result\":\"" + new string('é', 300_000) + "\"}";
        using var log = new ActionLog(_folder);
        log.Write("info", "engine", "periods.report", DateTime.Now, "request: {}", "\nanswer: ", answer);
        log.Flush();

        var full = log.ReadEntry(log.Read(0, 1).Entries.Single().N)!;

        Assert.False(full.More);
        Assert.Equal("request: {}\nanswer: " + answer, Body(full));
    }

    [Fact]
    public void Read_ShortEntryBesideALongOne_IsCutOnlyWithinItsCallStack()
    {
        using var log = new ActionLog(_folder);
        log.Write("info", "engine", "periods.report", DateTime.Now, "request: {}", "\nanswer: ", new string('x', 300_000));
        log.Info("you", "click", "short");
        log.Flush();

        var entry = log.Read(0, 10).Entries[0];

        Assert.StartsWith("You: click\nshort\nCall stack:\n", entry.Detail);
    }

    [Fact]
    public void ReadEntry_NumberOfNoEntry_ReturnsNull()
    {
        using var log = LogWith(1);

        Assert.Null(log.ReadEntry(5));
    }

    [Fact]
    public void Constructor_AfterARestart_ReadsTheEntriesWrittenBefore()
    {
        using (var log = new ActionLog(_folder)) Scrolled(log, 5);

        using var again = new ActionLog(_folder);
        again.Info("app", "App started");
        again.Flush();

        var (total, entries) = again.Read(0, 10);
        Assert.Equal((6, "to 4"), (total, Body(entries[1])));
    }
}
