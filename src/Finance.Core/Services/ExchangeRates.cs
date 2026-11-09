using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

/// <summary>An amount converted at the rate of one day: <see cref="Rate"/> units of the target currency for one unit of the source.</summary>
public sealed record Converted(decimal Amount, decimal Rate, DateOnly RateDay);

/// <summary>Where the rates come from, the currency they are quoted against, and how fresh they are.</summary>
public sealed record RatesStatus(string SourceId, string Source, string Base, string PageUrl, string DataUrl, string? UpdatedAt, string? Latest,
    string? First, int Currencies, string? Error, bool FromPrevious);

/// <summary>A central bank publishing free daily reference rates, and how its file is read.</summary>
/// <param name="Base">The currency every rate is quoted against.</param>
public sealed record RateSource(string Id, string Name, string Base, string PageUrl, string DataUrl)
{
    public static readonly RateSource Ecb = new("ecb", "European Central Bank", "EUR",
        "https://www.ecb.europa.eu/stats/policy_and_exchange_rates/euro_reference_exchange_rates/html/index.en.html",
        "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-hist.zip");

    public static readonly RateSource BankOfCanada = new("boc", "Bank of Canada", "CAD",
        "https://www.bankofcanada.ca/rates/exchange/daily-exchange-rates/",
        "https://www.bankofcanada.ca/valet/observations/group/FX_RATES_DAILY/json?start_date=2017-01-01");

    public static readonly IReadOnlyList<RateSource> All = [Ecb, BankOfCanada];

    public static RateSource Get(string? id) => All.FirstOrDefault(s => s.Id == id) ?? Ecb;
}

/// <summary>
/// Exchange rates of a central bank chosen in Settings: the European Central Bank by default (about 30 currencies against
/// the euro, every working day since 1999), or the Bank of Canada (26 currencies against the Canadian dollar, every
/// business day since 2017). The whole history is downloaded once a day and kept in the database with the copy
/// downloaded before it, used when the latest one is missing or cannot be read; nothing older is kept.
/// <para>An amount is converted at the rate of its own day; on a weekend or holiday (no rate published), at the last
/// rate published before it. Two currencies other than the bank's own go through it: 1 CAD in USD with the European
/// Central Bank is (USD per euro) ÷ (CAD per euro), the same day. Every converted amount is rounded to the cent; totals
/// add the rounded amounts.</para>
/// </summary>
public sealed class ExchangeRates(Database db, Func<string?>? sourceSetting = null)
{
    public RateSource Source => RateSource.Get(sourceSetting?.Invoke());

    /// <summary>The stored text of each currency's rates (units of the currency per unit of the base); read into numbers when used.</summary>
    private (string SourceId, Dictionary<string, string> Raw, bool FromPrevious)? _loaded;
    private readonly Dictionary<string, (DateOnly[] Days, decimal[] PerBase)> _series = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private (Dictionary<string, string> Raw, bool FromPrevious) Loaded()
    {
        var source = Source;
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
        return decimal.TryParse(data[11..(end > 0 ? end : data.Length)], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private (DateOnly[] Days, decimal[] PerBase)? Series(string currency)
    {
        var (raw, _) = Loaded();
        lock (_gate)
        {
            if (_series.TryGetValue(currency, out var known)) return known;
            if (!raw.TryGetValue(currency, out var data)) return null;
            var points = data.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('='))
                .Select(p => (Day: DateOnly.ParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture), Rate: decimal.Parse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture)))
                .OrderBy(p => p.Day).ToList();
            if (points.Count == 0) return null;
            return _series[currency] = ([.. points.Select(p => p.Day)], [.. points.Select(p => p.Rate)]);
        }
    }

    /// <summary>Forgets the rates read from the database (after an update or a change of source).</summary>
    public void Reload()
    {
        lock (_gate) { _loaded = null; _series.Clear(); }
    }

    public RatesStatus Status()
    {
        var source = Source;
        var (raw, previous) = Loaded();
        // Each currency's text runs from its newest day to its oldest (the order of the bank's file).
        var newest = raw.Values.Where(v => v.Length > 10).Select(v => v[..10]).DefaultIfEmpty().Max();
        var oldest = raw.Values.Where(v => v.Length > 10).Select(v => v.TrimEnd(';')).Select(v => v[(v.LastIndexOf(';') + 1)..][..10]).DefaultIfEmpty().Min();
        return new RatesStatus(source.Id, source.Name, source.Base, source.PageUrl, source.DataUrl, db.GetSetting($"rates.{source.Id}.updatedAt"),
            newest, oldest, raw.Count + (raw.Count > 0 ? 1 : 0), db.GetSetting($"rates.{source.Id}.error") is { Length: > 0 } e ? e : null, previous);
    }

    public bool Knows(string currency) => currency == Source.Base || Loaded().Raw.ContainsKey(currency);

    /// <summary>Units of <paramref name="currency"/> for one unit of the base currency on <paramref name="day"/> (the last rate published up to that day).</summary>
    public (decimal PerBase, DateOnly Day)? PerBase(string currency, DateOnly day)
    {
        if (currency == Source.Base) return (1m, day);
        if (Series(currency) is not { } s) return null;
        var i = Array.BinarySearch(s.Days, day);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        return (s.PerBase[i], s.Days[i]);
    }

    /// <summary>The amount in <paramref name="to"/>, at the rate of <paramref name="day"/>; null when no rate is known for it.</summary>
    public Converted? Convert(decimal amount, string from, string to, DateOnly day)
    {
        if (from == to) return new Converted(amount, 1m, day);
        var a = PerBase(from, day);
        var b = PerBase(to, day);
        if (a is null || b is null) return null;
        // The day the rate was published: the base currency has no rate of its own, so it is the other currency's day.
        var baseCurrency = Source.Base;
        var rateDay = from == baseCurrency ? b.Value.Day : to == baseCurrency ? a.Value.Day : a.Value.Day > b.Value.Day ? a.Value.Day : b.Value.Day;
        return new Converted(Math.Round(amount * b.Value.PerBase / a.Value.PerBase, 2, MidpointRounding.AwayFromZero),
            Math.Round(b.Value.PerBase / a.Value.PerBase, 6, MidpointRounding.AwayFromZero), rateDay);
    }

    /// <summary>True when today's copy has not been downloaded yet (rates are downloaded again every day).</summary>
    public bool DueToday() =>
        db.GetSetting($"rates.{Source.Id}.updatedAt") is not { Length: > 0 } at
        || DateTime.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime().Date < DateTime.Today;

    /// <summary>Downloads the whole history of the chosen bank's rates; the copy kept so far becomes the fallback.</summary>
    public async Task<RatesStatus> UpdateAsync(HttpClient? client = null, CancellationToken ct = default)
    {
        var source = Source;
        var own = client is null;
        client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.DataUrl);
            request.Headers.UserAgent.ParseAdd("ProductivityApp/1.0");
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            Save(source, source == RateSource.BankOfCanada ? ParseBankOfCanada(bytes) : ParseZip(bytes));
            db.SetSetting($"rates.{source.Id}.updatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            db.SetSetting($"rates.{source.Id}.error", "");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or JsonException)
        {
            db.SetSetting($"rates.{source.Id}.error", e is TaskCanceledException ? "The download took too long." : "The rates could not be downloaded: " + e.Message);
        }
        finally
        {
            if (own) client.Dispose();
        }
        return Status();
    }

    /// <summary>Keeps a downloaded copy of a bank's rates as the latest; the one before stays as the fallback, older ones go.</summary>
    public void Save(RateSource source, Dictionary<string, string> series)
    {
        if (series.Count == 0) throw new InvalidDataException("The file holds no exchange rates.");
        db.SaveRateSet(source.Id, series);
        Reload();
    }

    public static Dictionary<string, string> ParseZip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The file holds no exchange rates.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return ParseCsv(reader.ReadToEnd());
    }

    /// <summary>
    /// The European Central Bank's CSV: "Date,USD,JPY,...", then one line per working day, newest first, "N/A" where a
    /// currency had no rate. Gives, per currency, "yyyy-MM-dd=rate;..." newest first (units per euro).
    /// </summary>
    public static Dictionary<string, string> ParseCsv(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return [];
        var header = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        var points = new Dictionary<string, List<(DateOnly, decimal)>>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            if (!DateOnly.TryParseExact(cells[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            for (var i = 1; i < cells.Length && i < header.Length; i++)
            {
                if (header[i].Length != 3 || !decimal.TryParse(cells[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) || rate <= 0) continue;
                if (!points.TryGetValue(header[i], out var list)) points[header[i]] = list = [];
                list.Add((day, rate));
            }
        }
        return Serialize(points);
    }

    /// <summary>
    /// The Bank of Canada's daily rates: {"observations":[{"d":"2026-11-05","FXUSDCAD":{"v":"1.3989"},...}]}, each the
    /// value of one unit of the currency in Canadian dollars; kept as units of the currency per Canadian dollar.
    /// </summary>
    public static Dictionary<string, string> ParseBankOfCanada(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var points = new Dictionary<string, List<(DateOnly, decimal)>>(StringComparer.Ordinal);
        foreach (var obs in doc.RootElement.GetProperty("observations").EnumerateArray())
        {
            if (!obs.TryGetProperty("d", out var d) || !DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            foreach (var p in obs.EnumerateObject())
            {
                if (p.Name.Length != 8 || !p.Name.StartsWith("FX", StringComparison.Ordinal) || !p.Name.EndsWith("CAD", StringComparison.Ordinal)) continue;
                if (p.Value.ValueKind != JsonValueKind.Object || !p.Value.TryGetProperty("v", out var v)) continue;
                if (!decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var cadPerUnit) || cadPerUnit <= 0) continue;
                var currency = p.Name[2..5];
                if (!points.TryGetValue(currency, out var list)) points[currency] = list = [];
                list.Add((day, 1m / cadPerUnit));
            }
        }
        return Serialize(points);
    }

    private static Dictionary<string, string> Serialize(Dictionary<string, List<(DateOnly Day, decimal Rate)>> points) =>
        points.ToDictionary(kv => kv.Key, kv =>
        {
            var sb = new StringBuilder();
            foreach (var (day, rate) in kv.Value.OrderByDescending(x => x.Day))
                sb.Append(Iso(day)).Append('=').Append(rate.ToString(CultureInfo.InvariantCulture)).Append(';');
            return sb.ToString();
        }, StringComparer.Ordinal);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
