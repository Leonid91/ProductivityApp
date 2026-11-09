using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

/// <summary>An amount converted at the rate of one day: <see cref="Rate"/> units of the target currency for one unit of the source.</summary>
public sealed record Converted(decimal Amount, decimal Rate, DateOnly RateDay);

/// <summary>Where the rates come from, the currency they are quoted against, and how fresh they are.</summary>
public sealed record RatesStatus(string SourceId, string Source, string Base, string Url, string? UpdatedAt, string? Latest,
    string? First, int Currencies, string? Error, bool FromPrevious);

/// <summary>A source of rates as the table of sources shows it: its last download, or why it failed.</summary>
public sealed record RateSourceView(string Id, string Name, string Url, string? Base, string? Latest, string? UpdatedAt, string? Error, bool InUse);

/// <summary>A file or an address that does not give exchange rates; the message says why, in the person's words.</summary>
public sealed class RatesFileException(string message) : Exception(message);

/// <summary>
/// Exchange rates of the source chosen in Settings, among the sources defined in Definitions > Exchange rates (the
/// European Central Bank by default, the Bank of Canada, or any other address giving a file in one of their layouts).
/// The whole history is downloaded once a day and kept in the database with the copy downloaded before it, used when
/// the latest one is missing or cannot be read; nothing older is kept.
/// <para>An amount is converted at the rate of its own day; on a weekend or holiday (no rate published), at the last
/// rate published before it. Two currencies other than the source's own go through it: 1 CAD in USD with the European
/// Central Bank is (USD per euro) ÷ (CAD per euro), the same day. Every converted amount is rounded to the cent; totals
/// add the rounded amounts.</para>
/// </summary>
public sealed class ExchangeRates(Database db, Func<string?>? sourceSetting = null)
{
    /// <summary>A file of rates larger than this is refused (the European Central Bank's whole history is under 1 MB zipped, the Bank of Canada's about 4 MB).</summary>
    public const int MaxDownloadBytes = 32 * 1024 * 1024;

    /// <summary>The stored text of each currency's rates (units of the currency per unit of the base); read into numbers when used.</summary>
    private (string SourceId, Dictionary<string, string> Raw, bool FromPrevious)? _loaded;
    private readonly Dictionary<string, (DateOnly[] Days, decimal[] PerBase)> _series = new(StringComparer.Ordinal);
    private List<RateSource>? _sources;
    private readonly Lock _gate = new();

    public IReadOnlyList<RateSource> Sources()
    {
        lock (_gate) return _sources ??= db.RateSources();
    }

    public RateSource? Find(string? id) => Sources().FirstOrDefault(s => s.Id == id);

    /// <summary>The source chosen in Settings (the first one defined when that one is gone).</summary>
    public RateSource Source => Find(sourceSetting?.Invoke()) ?? Sources().FirstOrDefault() ?? RateSource.Ecb;

    private (Dictionary<string, string> Raw, bool FromPrevious) Loaded(RateSource source)
    {
        lock (_gate)
        {
            if (_loaded is { } l && l.SourceId == source.Id) return (l.Raw, l.FromPrevious);
            _series.Clear();
            // The latest copy, or the one before it when the latest is missing or cannot be read.
            var current = db.LoadRateSet(source.Id, 0);
            var usable = current.Count > 0 && current.Values.All(Readable);
            var raw = usable ? current : db.LoadRateSet(source.Id, 1);
            _loaded = (source.Id, raw, !usable && raw.Count > 0);
            return (raw, !usable && raw.Count > 0);
        }
    }

    /// <summary>A copy is readable when its newest entry is "yyyy-MM-dd=number".</summary>
    private static bool Readable(string data)
    {
        if (data.Length < 12 || data[4] != '-' || data[10] != '=') return false;
        var end = data.IndexOf(';', 11);
        return decimal.TryParse(data.AsSpan(11, (end > 0 ? end : data.Length) - 11), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private (DateOnly[] Days, decimal[] PerBase)? Series(RateSource source, string currency)
    {
        var (raw, _) = Loaded(source);
        lock (_gate)
        {
            if (_series.TryGetValue(currency, out var known)) return known;
            if (!raw.TryGetValue(currency, out var data)) return null;
            // Newest first in the text: read backwards into ascending arrays, without intermediate lists.
            var parts = data.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var days = new DateOnly[parts.Length];
            var rates = new decimal[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                var p = parts[parts.Length - 1 - i].AsSpan();
                days[i] = DateOnly.ParseExact(p[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
                rates[i] = decimal.Parse(p[11..], NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            if (!IsAscending(days)) Array.Sort(days, rates);
            return _series[currency] = (days, rates);
        }
    }

    private static bool IsAscending(DateOnly[] days)
    {
        for (var i = 1; i < days.Length; i++) if (days[i] < days[i - 1]) return false;
        return true;
    }

    /// <summary>Forgets the rates and sources read from the database (after an update or a change of source).</summary>
    public void Reload()
    {
        lock (_gate) { _loaded = null; _series.Clear(); _sources = null; }
    }

    public RatesStatus Status() => Status(Source);

    public RatesStatus Status(RateSource source)
    {
        var (raw, previous) = Loaded(source);
        // Each currency's text runs from its newest day to its oldest (the order of the bank's file).
        var newest = raw.Values.Where(v => v.Length > 10).Select(v => v[..10]).DefaultIfEmpty().Max();
        var oldest = raw.Values.Where(v => v.Length > 10).Select(v => v.TrimEnd(';')).Select(v => v[(v.LastIndexOf(';') + 1)..][..10]).DefaultIfEmpty().Min();
        return new RatesStatus(source.Id, source.Name, source.Base ?? "", source.Url, db.GetSetting($"rates.{source.Id}.updatedAt"),
            newest, oldest, raw.Count + (raw.Count > 0 ? 1 : 0), db.GetSetting($"rates.{source.Id}.error") is { Length: > 0 } e ? e : null, previous);
    }

    /// <summary>Every source defined, with the last day of its rates and the error of its last download.</summary>
    public List<RateSourceView> Views()
    {
        var current = Source.Id;
        return [.. Sources().Select(s => new RateSourceView(s.Id, s.Name, s.Url, s.Base, db.RateSetLatest(s.Id), db.GetSetting($"rates.{s.Id}.updatedAt"),
            db.GetSetting($"rates.{s.Id}.error") is { Length: > 0 } e ? e : null, s.Id == current))];
    }

    /// <summary>
    /// Adds a source (no id) or changes one. A new address drops the rates downloaded from the old one: they are
    /// downloaded again from the new address, which checks it.
    /// </summary>
    public RateSource SaveSource(string? id, string name, string url)
    {
        name = name.Trim();
        url = url.Trim();
        if (name.Length is 0 or > 60) throw new ArgumentException("A name has 1 to 60 characters.");
        CheckAddress(url);
        var old = Find(id);
        if (id is not null && old is null) throw new ArgumentException("This source no longer exists.");
        if (Sources().Any(s => s.Id != id && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A source is already named {name}.");
        var changedAddress = old is null || old.Url != url;
        var saved = new RateSource(id ?? "s" + Guid.NewGuid().ToString("N")[..10], name, url, changedAddress ? null : old!.Base);
        db.InTransaction(() =>
        {
            db.SaveRateSource(saved);
            if (changedAddress && old is not null) db.ClearRates(saved.Id);
        });
        Reload();
        return saved;
    }

    public void RemoveSource(string id)
    {
        if (Find(id) is null) return;
        if (Source.Id == id) throw new InvalidOperationException("This source is in use: choose another one in Settings first.");
        db.RemoveRateSource(id);
        Reload();
    }

    /// <summary>An address the app downloads from: absolute, https, at most 2,000 characters.</summary>
    public static void CheckAddress(string url)
    {
        if (url.Length > 2000 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0)
            throw new ArgumentException("The address must be a full web address starting with https://.");
    }

    public bool Knows(string currency)
    {
        var source = Source;
        return currency == source.Base || Loaded(source).Raw.ContainsKey(currency);
    }

    /// <summary>Units of <paramref name="currency"/> for one unit of the base currency on <paramref name="day"/> (the last rate published up to that day).</summary>
    public (decimal PerBase, DateOnly Day)? PerBase(string currency, DateOnly day) => PerBase(Source, currency, day);

    private (decimal PerBase, DateOnly Day)? PerBase(RateSource source, string currency, DateOnly day)
    {
        if (currency == source.Base) return (1m, day);
        if (Series(source, currency) is not { } s) return null;
        var i = Array.BinarySearch(s.Days, day);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        return (s.PerBase[i], s.Days[i]);
    }

    /// <summary>The amount in <paramref name="to"/>, at the rate of <paramref name="day"/>; null when no rate is known for it.</summary>
    public Converted? Convert(decimal amount, string from, string to, DateOnly day) => Convert(Source, amount, from, to, day);

    /// <summary>The same with the rates of <paramref name="source"/> (read once for many conversions).</summary>
    public Converted? Convert(RateSource source, decimal amount, string from, string to, DateOnly day)
    {
        if (from == to) return new Converted(amount, 1m, day);
        var a = PerBase(source, from, day);
        var b = PerBase(source, to, day);
        if (a is null || b is null) return null;
        // The day the rate was published: the base currency has no rate of its own, so it is the other currency's day.
        var rateDay = from == source.Base ? b.Value.Day : to == source.Base ? a.Value.Day : a.Value.Day > b.Value.Day ? a.Value.Day : b.Value.Day;
        return new Converted(Math.Round(amount * b.Value.PerBase / a.Value.PerBase, 2, MidpointRounding.AwayFromZero),
            Math.Round(b.Value.PerBase / a.Value.PerBase, 6, MidpointRounding.AwayFromZero), rateDay);
    }

    /// <summary>True when today's copy has not been downloaded yet (rates are downloaded again every day).</summary>
    public bool DueToday() =>
        db.GetSetting($"rates.{Source.Id}.updatedAt") is not { Length: > 0 } at
        || DateTime.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime().Date < DateTime.Today;

    /// <summary>
    /// Downloads the whole history of a source's rates (the chosen one by default), which also checks its address; the
    /// copy kept so far becomes the fallback. A failure is kept as the source's error, in the person's words.
    /// </summary>
    public async Task<RatesStatus> UpdateAsync(string? sourceId = null, HttpClient? client = null, CancellationToken ct = default)
    {
        var source = Find(sourceId) ?? Source;
        var own = client is null;
        client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        try
        {
            CheckAddress(source.Url);
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
            request.Headers.UserAgent.ParseAdd("ProductivityApp/1.0");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new RatesFileException(response.StatusCode == HttpStatusCode.NotFound
                    ? "Nothing at this address (404): check it."
                    : $"The address answered with an error ({(int)response.StatusCode} {response.ReasonPhrase}).");
            var bytes = await ReadLimited(response, ct).ConfigureAwait(false);
            var (series, currency) = Parse(bytes);
            if (source.Base != currency)
            {
                source = source with { Base = currency };
                db.SaveRateSource(source);
            }
            Save(source, series);
            db.SetSetting($"rates.{source.Id}.updatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            db.SetSetting($"rates.{source.Id}.error", "");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or JsonException
            or RatesFileException or ArgumentException or FormatException)
        {
            db.SetSetting($"rates.{source.Id}.error", e switch
            {
                RatesFileException or ArgumentException => e.Message,
                TaskCanceledException => "The address took too long to answer.",
                HttpRequestException => "The address could not be reached: check it, and the connection to the Internet.",
                _ => "The file at this address is not a file of exchange rates the app can read.",
            });
        }
        finally
        {
            if (own) client.Dispose();
        }
        Reload();
        return Status(Find(source.Id) ?? source);
    }

    private static async Task<byte[]> ReadLimited(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxDownloadBytes) throw new RatesFileException("The file at this address is too large to be a file of exchange rates.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + read > MaxDownloadBytes) throw new RatesFileException("The file at this address is too large to be a file of exchange rates.");
            copy.Write(buffer, 0, read);
        }
        return copy.ToArray();
    }

    /// <summary>Keeps a downloaded copy of a source's rates as the latest; the one before stays as the fallback, older ones go.</summary>
    public void Save(RateSource source, Dictionary<string, string> series)
    {
        if (series.Count == 0) throw new RatesFileException("The file at this address holds no exchange rates.");
        db.SaveRateSet(source.Id, series);
        Reload();
    }

    /// <summary>
    /// Reads a downloaded file of rates, whatever its layout, and the currency they are quoted against: a table in the
    /// European Central Bank's layout (CSV, or ZIP holding one), or a Valet JSON file like the Bank of Canada's.
    /// </summary>
    public static (Dictionary<string, string> Series, string Base) Parse(byte[] bytes)
    {
        if (bytes is [0x50, 0x4B, ..])
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                ?? throw new RatesFileException("The ZIP file at this address holds no table of exchange rates.");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return ParseTable(reader.ReadToEnd());
        }
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (text.StartsWith('<'))
            throw new RatesFileException("This address gives a web page, not a file of rates: on that page, copy the link of the download instead.");
        if (text.StartsWith('{')) return ParseValet(bytes);
        return ParseTable(text);
    }

    public static Dictionary<string, string> ParseZip(byte[] zip) => Parse(zip).Series;

    /// <summary>The European Central Bank's table (rates per euro); see <see cref="ParseTable"/>.</summary>
    public static Dictionary<string, string> ParseCsv(string csv) => ParseTable(csv).Series;

    /// <summary>The Bank of Canada's Valet JSON (rates per Canadian dollar); see <see cref="ParseValet"/>.</summary>
    public static Dictionary<string, string> ParseBankOfCanada(byte[] json) => ParseValet(json).Series;

    /// <summary>
    /// The European Central Bank's layout: "Date,USD,JPY,...", then one line per working day, "N/A" where a currency had
    /// no rate, every rate in units per euro (the euro is the one currency without a column). Gives, per currency,
    /// "yyyy-MM-dd=rate;..." newest first.
    /// </summary>
    public static (Dictionary<string, string> Series, string Base) ParseTable(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = lines.Length > 0 ? lines[0].Split(',').Select(h => h.Trim().Trim('"')).ToArray() : [];
        if (header.Length < 2 || !header[0].Equals("Date", StringComparison.OrdinalIgnoreCase))
            throw new RatesFileException("The file at this address is not a table of exchange rates the app can read (its first column must be Date).");
        if (header.Contains("EUR"))
            throw new RatesFileException("The table does not say which currency its rates are against: the app reads tables of rates against the euro.");
        var points = new Dictionary<string, List<(DateOnly, decimal)>>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            if (!DateOnly.TryParseExact(cells[0].Trim().Trim('"'), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            for (var i = 1; i < cells.Length && i < header.Length; i++)
            {
                if (header[i].Length != 3 || !decimal.TryParse(cells[i].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) || rate <= 0) continue;
                if (!points.TryGetValue(header[i], out var list)) points[header[i]] = list = [];
                list.Add((day, rate));
            }
        }
        if (points.Count == 0) throw new RatesFileException("The table at this address holds no exchange rates.");
        return (Serialize(points), "EUR");
    }

    /// <summary>
    /// A Valet JSON file (the Bank of Canada's service): {"observations":[{"d":"2026-11-05","FXUSDCAD":{"v":"1.3989"},...}]},
    /// each series FX{currency}{base} the value of one unit of the currency in the base; kept as units of the currency
    /// per unit of the base.
    /// </summary>
    public static (Dictionary<string, string> Series, string Base) ParseValet(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("observations", out var observations) || observations.ValueKind != JsonValueKind.Array)
            throw new RatesFileException("The file at this address is not a file of exchange rates the app can read.");
        var points = new Dictionary<string, List<(DateOnly, decimal)>>(StringComparer.Ordinal);
        string? baseCurrency = null;
        foreach (var obs in observations.EnumerateArray())
        {
            if (obs.ValueKind != JsonValueKind.Object || !obs.TryGetProperty("d", out var d) || d.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            foreach (var p in obs.EnumerateObject())
            {
                if (p.Name.Length != 8 || !p.Name.StartsWith("FX", StringComparison.Ordinal) || !p.Name.AsSpan(2).ContainsOnlyAsciiUpper()) continue;
                if (p.Value.ValueKind != JsonValueKind.Object || !p.Value.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.String) continue;
                if (!decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var basePerUnit) || basePerUnit <= 0) continue;
                var quote = p.Name[5..8];
                baseCurrency ??= quote;
                if (quote != baseCurrency) throw new RatesFileException("The file mixes rates against several currencies.");
                var currency = p.Name[2..5];
                if (!points.TryGetValue(currency, out var list)) points[currency] = list = [];
                list.Add((day, 1m / basePerUnit));
            }
        }
        if (points.Count == 0 || baseCurrency is null) throw new RatesFileException("The file at this address holds no exchange rates.");
        return (Serialize(points), baseCurrency);
    }

    private static Dictionary<string, string> Serialize(Dictionary<string, List<(DateOnly Day, decimal Rate)>> points) =>
        points.ToDictionary(kv => kv.Key, kv =>
        {
            var sb = new StringBuilder(kv.Value.Count * 18);
            foreach (var (day, rate) in kv.Value.OrderByDescending(x => x.Day))
                sb.Append(Iso(day)).Append('=').Append(rate.ToString(CultureInfo.InvariantCulture)).Append(';');
            return sb.ToString();
        }, StringComparer.Ordinal);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

internal static class SpanText
{
    public static bool ContainsOnlyAsciiUpper(this ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (!char.IsAsciiLetterUpper(c)) return false;
        return true;
    }
}
