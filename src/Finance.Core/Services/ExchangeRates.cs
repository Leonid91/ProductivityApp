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

/// <summary>
/// A source of rates as the table of sources shows it: the bank, its address, what its copy in use holds (the number
/// of currencies, its own included, and the last day of rates: read from the downloaded data, null without it), and
/// why its last download failed.
/// </summary>
public sealed record RateSourceView(string Id, string Name, string Format, string Url, string? Base, string? Latest, int Currencies,
    string? UpdatedAt, string? Error, bool InUse);

/// <summary>A file or an address that does not give exchange rates; the message says why, in the person's words.</summary>
public sealed class RatesFileException(string message) : Exception(message);

/// <summary>
/// Exchange rates of the source chosen in Settings, among the sources defined in Definitions > Exchange rates (the
/// European Central Bank by default, the Bank of Canada, or any other address giving a file in one of their layouts).
/// The whole history is downloaded once a day and kept as the bank's own file (see <see cref="DownloadStore"/>) with
/// the copy downloaded before it, used when the latest one is missing or cannot be read; nothing older is kept. A copy
/// is read once, when first used, and kept in memory.
/// <para>An amount is converted at the rate of its own day; on a weekend or holiday (no rate published), at the last
/// rate published before it. Two currencies other than the source's own go through it: 1 CAD in USD with the European
/// Central Bank is (USD per euro) ÷ (CAD per euro), the same day. Every converted amount is rounded to the cent; totals
/// add the rounded amounts.</para>
/// </summary>
public sealed class ExchangeRates(Database db, Func<string?>? sourceSetting = null)
{
    /// <summary>A file of rates larger than this is refused (the European Central Bank's whole history is under 1 MB zipped, the Bank of Canada's about 4 MB).</summary>
    public const int MaxDownloadBytes = 32 * 1024 * 1024;

    private readonly DownloadStore _store = new(db.DownloadsFolder);

    /// <summary>Each source's copy in use, read from its file: each currency's rates as text (units of the currency per unit of the base), read into numbers when used.</summary>
    private readonly Dictionary<string, (Dictionary<string, string> Raw, bool FromPrevious)> _copies = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Source, string Currency), (DateOnly[] Days, decimal[] PerBase)> _series = [];
    private List<RateSource>? _sources;
    private readonly Lock _gate = new();

    public IReadOnlyList<RateSource> Sources()
    {
        lock (_gate) return _sources ??= db.RateSources();
    }

    public RateSource? Find(string? id) => Sources().FirstOrDefault(s => s.Id == id);

    /// <summary>The source chosen in Settings (the first one defined when that one is gone).</summary>
    public RateSource Source => Find(sourceSetting?.Invoke()) ?? Sources().FirstOrDefault() ?? RateSource.Ecb;

    /// <summary>The downloads kept for a source, the latest first (its files are in <see cref="DownloadStore.ExchangeRates"/>).</summary>
    public List<DownloadStore.Copy> Copies(RateSource source) => _store.Copies(DownloadStore.ExchangeRates, source.Name);

    private (Dictionary<string, string> Raw, bool FromPrevious) Loaded(RateSource source)
    {
        lock (_gate)
        {
            if (_copies.TryGetValue(source.Id, out var known)) return known;
            // The latest copy, or the one before it when the latest is missing or cannot be read.
            var bank = CentralBank.Find(source.Format);
            var loaded = (Raw: new Dictionary<string, string>(), FromPrevious: false);
            foreach (var (copy, i) in Copies(source).Take(DownloadStore.Kept).Select((c, i) => (c, i)))
            {
                var file = copy.Files.FirstOrDefault(f => f.StartsWith("rates.", StringComparison.Ordinal));
                if (file is null) continue;
                try
                {
                    var (series, _) = Parse(File.ReadAllBytes(Path.Combine(copy.Folder, file)), bank);
                    if (series.Count == 0 || !series.Values.All(Readable)) continue;
                    loaded = (series, i > 0);
                    break;
                }
                catch (Exception e) when (e is RatesFileException or IOException or InvalidDataException or JsonException or FormatException or UnauthorizedAccessException)
                {
                    // Not readable (changed by hand, cut short): the copy before it is used.
                    ProductivityApp.Finance.Services.Diagnostics.Report($"ExchangeRates.Load {source.Id} {file}", e);
                }
            }
            return _copies[source.Id] = loaded;
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
            if (_series.TryGetValue((source.Id, currency), out var known)) return known;
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
            return _series[(source.Id, currency)] = (days, rates);
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
        lock (_gate) { _copies.Clear(); _series.Clear(); _sources = null; }
    }

    public RatesStatus Status() => Status(Source);

    public RatesStatus Status(RateSource source)
    {
        var (raw, previous) = Loaded(source);
        var newest = Newest(raw);
        var oldest = raw.Values.Where(v => v.Length > 10).Select(v => v.TrimEnd(';')).Select(v => v[(v.LastIndexOf(';') + 1)..][..10]).DefaultIfEmpty().Min();
        return new RatesStatus(source.Id, source.Name, source.Base ?? "", source.Url, db.GetSetting($"rates.{source.Id}.updatedAt"),
            newest, oldest, raw.Count + (raw.Count > 0 ? 1 : 0), db.GetSetting($"rates.{source.Id}.error") is { Length: > 0 } e ? e : null, previous);
    }

    /// <summary>The last day of rates of a copy (each currency's text runs from its newest day to its oldest).</summary>
    private static string? Newest(Dictionary<string, string> raw) => raw.Values.Where(v => v.Length > 10).Select(v => v[..10]).DefaultIfEmpty().Max();

    /// <summary>Every source defined, with what its copy in use holds and the error of its last download.</summary>
    public List<RateSourceView> Views()
    {
        var current = Source.Id;
        return [.. Sources().Select(s =>
        {
            var (raw, _) = Loaded(s);
            return new RateSourceView(s.Id, s.Name, s.Format, s.Url, s.Base, raw.Count > 0 ? Newest(raw) : null,
                raw.Count > 0 ? raw.Count + 1 : 0, db.GetSetting($"rates.{s.Id}.updatedAt"),
                db.GetSetting($"rates.{s.Id}.error") is { Length: > 0 } e ? e : null, s.Id == current);
        })];
    }

    /// <summary>
    /// Adds a central bank (no id: <paramref name="format"/> says which, once each) or changes the address of one. A
    /// new address drops the rates downloaded from the old one: they are downloaded again from the new address, which
    /// checks it.
    /// </summary>
    public RateSource SaveSource(string? id, string? format, string url)
    {
        url = url.Trim();
        CheckAddress(url);
        var old = Find(id);
        if (id is not null && old is null) throw new ArgumentException("This source no longer exists.");
        var bank = old is null ? CentralBank.Find(format) ?? throw new ArgumentException("Choose a central bank of the list.") : null;
        if (bank is not null && Sources().Any(s => s.Format == bank.Format))
            throw new ArgumentException($"The {bank.Name} is already in the table.");
        var changedAddress = old is null || old.Url != url;
        var saved = old is null
            ? new RateSource("s" + Guid.NewGuid().ToString("N")[..10], bank!.Name, url, bank.Currency, bank.Format)
            : old with { Url = url };
        db.InTransaction(() =>
        {
            db.SaveRateSource(saved);
            if (changedAddress && old is not null) db.ClearRates(saved.Id);
        });
        if (changedAddress && old is not null) _store.Delete(DownloadStore.ExchangeRates, saved.Name);
        Reload();
        return saved;
    }

    public void RemoveSource(string id)
    {
        if (Find(id) is null) return;
        if (Source.Id == id) throw new InvalidOperationException("This bank is in use: choose another one first.");
        _store.Delete(DownloadStore.ExchangeRates, Find(id)!.Name);
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

    /// <summary>True when today's copy of the source in use has not been downloaded yet (rates are downloaded again every day).</summary>
    public bool DueToday() => DueToday(Source);

    public bool DueToday(RateSource source) =>
        db.GetSetting($"rates.{source.Id}.updatedAt") is not { Length: > 0 } at
        || DateTime.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime().Date < DateTime.Today;

    /// <summary>
    /// Downloads the rates of every source defined, all of them (<paramref name="force"/>) or only the ones without
    /// today's copy, side by side; says whether the copy of the source in use changed.
    /// </summary>
    public async Task<bool> UpdateAllAsync(bool force, HttpClient? client = null, CancellationToken ct = default)
    {
        var inUse = Source.Id;
        var due = Sources().Where(s => force || DueToday(s)).ToList();
        var done = await Task.WhenAll(due.Select(s => UpdateAsync(s.Id, client, ct))).ConfigureAwait(false);
        return done.Any(d => d.SourceId == inUse && d.Error is null);
    }

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
            var (series, currency) = Parse(bytes, CentralBank.Find(source.Format));
            if (source.Base != currency)
            {
                source = source with { Base = currency };
                db.SaveRateSource(source);
            }
            Save(source, bytes, DateOnly.FromDateTime(DateTime.Now));
            db.SetSetting($"rates.{source.Id}.updatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            db.SetSetting($"rates.{source.Id}.error", "");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or JsonException
            or RatesFileException or ArgumentException or FormatException)
        {
            ProductivityApp.Finance.Services.Diagnostics.Report($"ExchangeRates.Update {source.Id} {source.Url}", e);
            db.SetSetting($"rates.{source.Id}.error", e switch
            {
                RatesFileException or ArgumentException => e.Message,
                TaskCanceledException => "The address took too long to answer.",
                HttpRequestException => "The address could not be reached: check it, and the connection to the Internet.",
                _ => "The file at this address is not a file of exchange rates the app can read.",
            });
        }
        catch (Exception e)
        {
            // Anything else (a fault of the app or of the computer): said plainly; the copies kept so far stay in use.
            ProductivityApp.Finance.Services.Diagnostics.Report($"ExchangeRates.Update {source.Id} {source.Url}", e);
            db.SetSetting($"rates.{source.Id}.error", $"The rates could not be downloaded: {e.Message}");
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

    /// <summary>
    /// Keeps a source's file of rates downloaded on <paramref name="day"/>, as it is, as the copy in use (see <see
    /// cref="DownloadStore.Save"/>); a file that does not give rates in its bank's layout is refused.
    /// </summary>
    public void Save(RateSource source, byte[] file) => Save(source, file, DateOnly.FromDateTime(DateTime.Now));

    public void Save(RateSource source, byte[] file, DateOnly day)
    {
        var (series, _) = Parse(file, CentralBank.Find(source.Format));
        if (series.Count == 0) throw new RatesFileException("The file at this address holds no exchange rates.");
        _store.Save(DownloadStore.ExchangeRates, source.Name, day, [(DownloadStore.FileName("rates", null, file), file)]);
        Reload();
    }

    /// <summary>
    /// Reads a downloaded file of rates, whatever its layout, and the currency they are quoted against: a table in the
    /// European Central Bank's layout (CSV, or ZIP holding one), a Valet JSON file like the Bank of Canada's, or the
    /// US Federal Reserve's H.10 table.
    /// </summary>
    public static (Dictionary<string, string> Series, string Base) Parse(byte[] bytes) => Parse(bytes, null);

    /// <summary>
    /// The same, for the file of one central bank: a file in another bank's layout is refused, saying so (the address
    /// is probably the one of another file).
    /// </summary>
    public static (Dictionary<string, string> Series, string Base) Parse(byte[] bytes, CentralBank? bank)
    {
        var text = bytes is [0x50, 0x4B, ..] ? null : Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (text is { Length: 0 }) throw new RatesFileException("The address gave an empty answer: try again later.");
        if (text is not null && text.StartsWith('<'))
            throw new RatesFileException("This address gives a web page, not a file of rates: on that page, copy the link of the download instead.");
        var layout = text is null ? "ecb" : text.StartsWith('{') ? "boc" : text.StartsWith("\"Series Description\"", StringComparison.Ordinal) ? "fed" : "ecb";
        if (bank is not null && layout != bank.Format)
            throw new RatesFileException($"This file is not in the layout of the {bank.Name}: check the address.");
        if (text is null)
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                ?? throw new RatesFileException("The ZIP file at this address holds no table of exchange rates.");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return ParseTable(reader.ReadToEnd());
        }
        return layout switch { "boc" => ParseValet(bytes), "fed" => ParseFed(text), _ => ParseTable(text) };
    }

    /// <summary>
    /// The US Federal Reserve's H.10 table (Data Download Program, "layout=seriescolumn"): five lines describing each
    /// column ("Currency:" names the currency, "Unique Identifier:" says which way it is quoted: RXI$US_ is US dollars
    /// per unit of the currency, RXI_ units of the currency per US dollar; "Multiplier:"), a line "Time Period", then
    /// one line per day, "ND" where a currency had no rate. Kept, like the others, as units of the currency per US dollar.
    /// </summary>
    public static (Dictionary<string, string> Series, string Base) ParseFed(string csv)
    {
        static string[] Cells(string line) => [.. line.Split(',').Select(c => c.Trim().Trim('"').Trim())];
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[]? currencies = null, ids = null, multipliers = null;
        var points = new Dictionary<string, List<(DateOnly, decimal)>>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var cells = Cells(line);
            switch (cells[0])
            {
                case "Currency:": currencies = cells; continue;
                case "Unique Identifier:": ids = cells; continue;
                case "Multiplier:": multipliers = cells; continue;
            }
            if (currencies is null || ids is null
                || !DateOnly.TryParseExact(cells[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            for (var i = 1; i < cells.Length && i < currencies.Length && i < ids.Length; i++)
            {
                var currency = currencies[i];
                if (currency.Length != 3 || !currency.AsSpan().ContainsOnlyAsciiUpper() || currency == "USD") continue;
                if (!decimal.TryParse(cells[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0) continue;
                if (multipliers is not null && i < multipliers.Length && decimal.TryParse(multipliers[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0)
                    value *= m;
                var perDollar = ids[i].Contains("$US", StringComparison.Ordinal) ? Math.Round(1m / value, 10, MidpointRounding.AwayFromZero) : value;
                if (!points.TryGetValue(currency, out var list)) points[currency] = list = [];
                list.Add((day, perDollar));
            }
        }
        if (currencies is null || ids is null)
            throw new RatesFileException("The file at this address is not a table of the US Federal Reserve the app can read.");
        if (points.Count == 0) throw new RatesFileException("The table at this address holds no exchange rates.");
        return (Serialize(points), "USD");
    }

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
