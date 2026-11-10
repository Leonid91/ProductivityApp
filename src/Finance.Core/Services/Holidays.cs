using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

/// <summary>
/// A calendar as the table of holidays shows it: its place, its address, whether it is checked, and what its copy in
/// use holds: the number of holidays of <see cref="Year"/> (this year), read from the downloaded data (null without
/// it); with the day of its last download and why it failed.
/// </summary>
public sealed record HolidayCalendarView(string Id, string Location, string Url, bool Active, int? PerYear, int Year,
    string? UpdatedAt, string? Error);

/// <summary>A holiday of a checked calendar, with the place of its calendar.</summary>
public sealed record HolidayView(string Day, string Name, string Location);

/// <summary>A file or an address that does not give holidays; the message says why, in the person's words.</summary>
public sealed class HolidaysFileException(string message) : Exception(message);

/// <summary>
/// The calendars of public holidays of Definitions > Holidays. Each one is downloaded once a day (an ICS calendar or a
/// JSON file; an address with "{year}" is downloaded for this year, the ten before it and the next one) and kept with
/// the copy downloaded the day before, used when the latest one cannot be read; nothing older is kept. The holidays
/// of the checked calendars, together, are shown on the calendar and are not business days.
/// </summary>
public sealed class Holidays(Database db)
{
    /// <summary>A calendar file larger than this is refused (a calendar of thirty years is under 100 KB).</summary>
    public const int MaxDownloadBytes = 8 * 1024 * 1024;

    /// <summary>An address with "{year}" is downloaded for this year, this many years before it, and the next year.</summary>
    public const int YearsBack = 10;

    private const string YearMark = "{year}";

    private List<HolidayCalendar>? _calendars;
    private readonly Dictionary<string, Holiday[]> _loaded = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IReadOnlyList<HolidayCalendar> Calendars()
    {
        lock (_gate) return _calendars ??= db.HolidayCalendars();
    }

    public HolidayCalendar? Find(string? id) => Calendars().FirstOrDefault(c => c.Id == id);

    /// <summary>Forgets what was read from the database (after a download or a change of the table).</summary>
    public void Reload()
    {
        lock (_gate) { _calendars = null; _loaded.Clear(); }
    }

    /// <summary>The holidays of a calendar: its latest copy, or the one of the day before when the latest cannot be read.</summary>
    public Holiday[] Of(string id)
    {
        lock (_gate)
        {
            if (_loaded.TryGetValue(id, out var known)) return known;
            var days = Read(db.LoadHolidaySet(id, 0)) ?? Read(db.LoadHolidaySet(id, 1)) ?? [];
            return _loaded[id] = days;
        }
    }

    /// <summary>A stored copy: a JSON list of [day, name]; null when missing or unreadable.</summary>
    private static Holiday[]? Read(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        try
        {
            var rows = JsonSerializer.Deserialize<string[][]>(data);
            if (rows is not { Length: > 0 }) return null;
            return [.. rows.Select(r => new Holiday(DateOnly.ParseExact(r[0], "yyyy-MM-dd", CultureInfo.InvariantCulture), r[1]))];
        }
        catch (Exception e) when (e is JsonException or FormatException or IndexOutOfRangeException or ArgumentNullException)
        {
            return null;
        }
    }

    private static string Write(IEnumerable<Holiday> days) =>
        JsonSerializer.Serialize(days.Select(h => new[] { h.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), h.Name }));

    /// <summary>True when the copy in use of a calendar comes from the day before (today's cannot be read).</summary>
    public bool FromPrevious(string id) => Read(db.LoadHolidaySet(id, 0)) is null && Read(db.LoadHolidaySet(id, 1)) is not null;

    /// <summary>Every calendar defined, with the number of holidays of this year in its copy in use and its last error.</summary>
    public List<HolidayCalendarView> Views()
    {
        var year = DateTime.Today.Year;
        return [.. Calendars().Select(c =>
        {
            var days = Of(c.Id);
            var thisYear = days.Where(h => h.Day.Year == year).Select(h => h.Day).Distinct().Count();
            return new HolidayCalendarView(c.Id, c.Location, c.Url, c.Active, thisYear > 0 ? thisYear : null, year,
                db.GetSetting($"holidays.{c.Id}.updatedAt"), db.GetSetting($"holidays.{c.Id}.error") is { Length: > 0 } e ? e : null);
        })];
    }

    /// <summary>
    /// Adds a calendar (no id) or changes the place or the address of one. A new address drops the holidays downloaded
    /// from the old one: they are downloaded again from it, which checks it. A new calendar is not checked yet.
    /// </summary>
    public HolidayCalendar SaveCalendar(string? id, string location, string url)
    {
        location = location.Trim();
        url = url.Trim();
        if (location.Length == 0) throw new ArgumentException("Write the place of the calendar.");
        if (location.Length > 80) throw new ArgumentException("The place is too long (80 characters at most).");
        CheckAddress(url);
        var old = Find(id);
        if (id is not null && old is null) throw new ArgumentException("This calendar no longer exists.");
        if (Calendars().FirstOrDefault(c => c.Id != id && string.Equals(c.Location, location, StringComparison.OrdinalIgnoreCase)) is { } same)
            throw new ArgumentException($"{same.Location} is already in the table.");
        var saved = old is null ? new HolidayCalendar("h" + Guid.NewGuid().ToString("N")[..10], location, url, false) : old with { Location = location, Url = url };
        db.InTransaction(() =>
        {
            db.SaveHolidayCalendar(saved);
            if (old is not null && old.Url != url) db.ClearHolidays(saved.Id);
        });
        Reload();
        return saved;
    }

    /// <summary>Checks or unchecks a calendar: its holidays are shown and are not business days, or not.</summary>
    public void SetActive(string id, bool active)
    {
        var c = Find(id) ?? throw new ArgumentException("This calendar no longer exists.");
        db.SaveHolidayCalendar(c with { Active = active });
        Reload();
    }

    public void RemoveCalendar(string id)
    {
        if (Find(id) is null) return;
        db.RemoveHolidayCalendar(id);
        Reload();
    }

    /// <summary>An address the app downloads from: absolute, https, at most 2,000 characters ("{year}" allowed in it).</summary>
    public static void CheckAddress(string url)
    {
        var real = url.Replace(YearMark, "2000", StringComparison.OrdinalIgnoreCase);
        if (url.Length > 2000 || !Uri.TryCreate(real, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0)
            throw new ArgumentException("The address must be a full web address starting with https://.");
    }

    /// <summary>The addresses a calendar is downloaded from: itself, or one per year when it has "{year}".</summary>
    public static List<string> Addresses(string url, int thisYear) =>
        url.Contains(YearMark, StringComparison.OrdinalIgnoreCase)
            ? [.. Enumerable.Range(thisYear - YearsBack, YearsBack + 2).Select(y => url.Replace(YearMark, y.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))]
            : [url];

    /// <summary>The holidays of the checked calendars from <paramref name="from"/> to <paramref name="to"/>, day by day.</summary>
    public List<HolidayView> List(DateOnly from, DateOnly to) =>
        [.. Calendars().Where(c => c.Active)
            .SelectMany((c, order) => Of(c.Id).Where(h => h.Day >= from && h.Day <= to).Select(h => (h, c, order)))
            .OrderBy(x => x.h.Day).ThenBy(x => x.order)
            .Select(x => new HolidayView(x.h.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), x.h.Name, x.c.Location))];

    /// <summary>
    /// The business days from <paramref name="from"/> to <paramref name="to"/>: Monday to Friday, less the holidays of
    /// every checked calendar (a day that is a holiday in two of them counts once).
    /// </summary>
    public int BusinessDays(DateOnly from, DateOnly to)
    {
        if (to < from) return 0;
        var off = new HashSet<DateOnly>();
        foreach (var c in Calendars().Where(c => c.Active))
            foreach (var h in Of(c.Id))
                if (h.Day >= from && h.Day <= to && h.Day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) off.Add(h.Day);
        return Weekdays(from, to) - off.Count;
    }

    /// <summary>Monday to Friday from <paramref name="from"/> to <paramref name="to"/>, both counted.</summary>
    public static int Weekdays(DateOnly from, DateOnly to)
    {
        if (to < from) return 0;
        var days = to.DayNumber - from.DayNumber + 1;
        var weeks = days / 7;
        var count = weeks * 5;
        for (var d = from.AddDays(weeks * 7); d <= to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) count++;
        return count;
    }

    public bool DueToday(HolidayCalendar c) =>
        db.GetSetting($"holidays.{c.Id}.updatedAt") is not { Length: > 0 } at
        || DateTime.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime().Date < DateTime.Today;

    /// <summary>
    /// Downloads every calendar of the table, all of them (<paramref name="force"/>) or only the ones without today's
    /// copy, side by side; says whether a checked calendar got new holidays.
    /// </summary>
    public async Task<bool> UpdateAllAsync(bool force, HttpClient? client = null, CancellationToken ct = default)
    {
        var due = Calendars().Where(c => force || DueToday(c)).ToList();
        var before = due.Where(c => c.Active).ToDictionary(c => c.Id, c => Write(Of(c.Id)));
        await Task.WhenAll(due.Select(c => UpdateAsync(c.Id, client, ct))).ConfigureAwait(false);
        return before.Any(b => Write(Of(b.Key)) != b.Value);
    }

    /// <summary>
    /// Downloads a calendar again (which checks its address); the copy kept so far becomes the one of the day before.
    /// A failure is kept as the calendar's error, in the person's words; the copies kept so far stay in use.
    /// </summary>
    public async Task<HolidayCalendarView> UpdateAsync(string id, HttpClient? client = null, CancellationToken ct = default)
    {
        var c = Find(id) ?? throw new ArgumentException("This calendar no longer exists.");
        var own = client is null;
        client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        try
        {
            CheckAddress(c.Url);
            var addresses = Addresses(c.Url, DateTime.Today.Year);
            var files = await Task.WhenAll(addresses.Select(a => Download(client, a, addresses.Count > 1, ct))).ConfigureAwait(false);
            if (files.All(f => f is null)) throw new HolidaysFileException("Nothing at this address (404): check it.");
            var days = files.Where(f => f is not null).SelectMany(f => Parse(f!)).Distinct().OrderBy(h => h.Day).ThenBy(h => h.Name, StringComparer.Ordinal).ToList();
            Save(c.Id, days, DateOnly.FromDateTime(DateTime.Now));
            db.SetSetting($"holidays.{c.Id}.updatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            db.SetSetting($"holidays.{c.Id}.error", "");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException
            or HolidaysFileException or ArgumentException or FormatException or DecoderFallbackException)
        {
            db.SetSetting($"holidays.{c.Id}.error", e switch
            {
                HolidaysFileException or ArgumentException => e.Message,
                TaskCanceledException => "The address took too long to answer.",
                HttpRequestException => "The address could not be reached: check it, and the connection to the Internet.",
                _ => "The file at this address is not a calendar of holidays the app can read (ICS or JSON).",
            });
        }
        catch (Exception e)
        {
            // Anything else (a fault of the app or of the computer): said plainly; the copies kept so far stay in use.
            db.SetSetting($"holidays.{c.Id}.error", $"The holidays could not be downloaded: {e.Message}");
        }
        finally
        {
            if (own) client.Dispose();
        }
        Reload();
        return Views().FirstOrDefault(v => v.Id == id) ?? throw new ArgumentException("This calendar no longer exists.");
    }

    /// <summary>One file; null for a year the address has nothing for (404), when it is one of several years.</summary>
    private static async Task<byte[]?> Download(HttpClient client, string url, bool oneOfYears, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ProductivityApp/1.0");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound && oneOfYears) return null;
        if (!response.IsSuccessStatusCode)
            throw new HolidaysFileException(response.StatusCode == HttpStatusCode.NotFound
                ? "Nothing at this address (404): check it."
                : $"The address answered with an error ({(int)response.StatusCode} {response.ReasonPhrase}).");
        if (response.Content.Headers.ContentLength > MaxDownloadBytes) throw new HolidaysFileException("The file at this address is too large to be a calendar of holidays.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + read > MaxDownloadBytes) throw new HolidaysFileException("The file at this address is too large to be a calendar of holidays.");
            copy.Write(buffer, 0, read);
        }
        return copy.ToArray();
    }

    /// <summary>Keeps the holidays downloaded on <paramref name="day"/> as the copy in use (see <see cref="Database.SaveHolidaySet"/>).</summary>
    public void Save(string id, IReadOnlyCollection<Holiday> days, DateOnly day)
    {
        if (days.Count == 0) throw new HolidaysFileException("The file at this address holds no holidays.");
        db.SaveHolidaySet(id, Write(days), day);
        Reload();
    }

    /// <summary>
    /// Reads a downloaded calendar of holidays: an ICS calendar (one event per holiday; an event over several days
    /// gives each of them), or JSON: an object of "yyyy-MM-dd": name, or lists of objects with a "date" (an
    /// "observedDate" is preferred: the day off) and a name.
    /// </summary>
    public static List<Holiday> Parse(byte[] bytes)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (text.Length == 0) throw new HolidaysFileException("The address gave an empty answer: try again later.");
        if (text.StartsWith('<'))
            throw new HolidaysFileException("This address gives a web page, not a calendar file: on that page, copy the link of the download instead.");
        List<Holiday> days;
        if (text.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) days = ParseIcs(text);
        else if (text.StartsWith('{') || text.StartsWith('[')) days = ParseJson(text);
        else throw new HolidaysFileException("The file at this address is not a calendar of holidays the app can read (ICS or JSON).");
        if (days.Count == 0) throw new HolidaysFileException("The file at this address holds no holidays.");
        return days;
    }

    /// <summary>An ICS calendar: the day (DTSTART, to DTEND excluded when longer than a day) and the name (SUMMARY) of each event.</summary>
    public static List<Holiday> ParseIcs(string ics)
    {
        // Long lines are folded: a line starting with a space or a tab continues the one before.
        var unfolded = ics.Replace("\r\n", "\n").Replace("\n ", "").Replace("\n\t", "");
        var days = new List<Holiday>();
        DateOnly? start = null, end = null;
        string? name = null;
        var cancelled = false;
        var inEvent = false;
        foreach (var raw in unfolded.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { inEvent = true; start = end = null; name = null; cancelled = false; continue; }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                inEvent = false;
                if (start is not { } s || cancelled) continue;
                var title = string.IsNullOrWhiteSpace(name) ? "Holiday" : name.Trim();
                var last = end is { } e && e > s.AddDays(1) ? e.AddDays(-1) : s;
                if (last.DayNumber - s.DayNumber > 31) last = s;
                for (var d = s; d <= last; d = d.AddDays(1)) days.Add(new Holiday(d, title));
                continue;
            }
            if (!inEvent) continue;
            var colon = ValueStart(line);
            if (colon < 0) continue;
            var key = line[..colon];
            var semi = key.IndexOf(';');
            var prop = (semi >= 0 ? key[..semi] : key).ToUpperInvariant();
            var value = line[(colon + 1)..];
            switch (prop)
            {
                case "DTSTART": start = IcsDay(value); break;
                case "DTEND": end = IcsDay(value); break;
                case "SUMMARY": name = Unescape(value); break;
                case "STATUS": cancelled = value.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
            }
        }
        return [.. days.Distinct()];
    }

    /// <summary>Where the value of an ICS line starts: after the first colon outside quotes (parameters may quote one).</summary>
    private static int ValueStart(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) return i;
        }
        return -1;
    }

    private static DateOnly? IcsDay(string value) =>
        value.Trim().Length >= 8 && DateOnly.TryParseExact(value.Trim()[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Unescape(string v) =>
        v.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    private static readonly string[] NameFields = ["localName", "name", "nameEn", "nameFr", "summary", "title", "description"];

    /// <summary>JSON: an object of "yyyy-MM-dd": name (France's service), or every list of objects with a date anywhere in the file.</summary>
    public static List<Holiday> ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var days = new List<Holiday>();
        void Visit(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var props = e.EnumerateObject().ToList();
                    if (props.Count > 0 && props.All(p => IsDay(p.Name, out _) && p.Value.ValueKind == JsonValueKind.String))
                    {
                        foreach (var p in props) { IsDay(p.Name, out var d); days.Add(new Holiday(d, NameOf(p.Value.GetString()))); }
                        return;
                    }
                    if (DayOf(e) is { } day)
                    {
                        var name = NameFields.Select(f => e.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                        days.Add(new Holiday(day, NameOf(name)));
                        return;
                    }
                    foreach (var p in props) Visit(p.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) Visit(item);
                    break;
            }
        }
        Visit(doc.RootElement);
        return [.. days.Distinct()];
    }

    /// <summary>The day of a holiday object: its "observedDate" (the day off) or its "date".</summary>
    private static DateOnly? DayOf(JsonElement e)
    {
        foreach (var f in (ReadOnlySpan<string>)["observedDate", "date"])
            if (e.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.String && IsDay(v.GetString(), out var d)) return d;
        return null;
    }

    private static bool IsDay(string? s, out DateOnly day)
    {
        day = default;
        return s is { Length: >= 10 } && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    private static string NameOf(string? name) => string.IsNullOrWhiteSpace(name) ? "Holiday" : name.Trim();
}
