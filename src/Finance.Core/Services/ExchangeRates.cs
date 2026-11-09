using System.Globalization;
using System.IO.Compression;
using System.Text;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

/// <summary>An amount converted at the rate of one day: <see cref="Rate"/> units of the target currency for one unit of the source.</summary>
public sealed record Converted(decimal Amount, decimal Rate, DateOnly RateDay);

/// <summary>Where the rates come from and how fresh they are.</summary>
public sealed record RatesStatus(string Source, string PageUrl, string DataUrl, string? UpdatedAt, string? Latest, string? First, int Currencies, string? Error);

/// <summary>
/// Exchange rates: the euro foreign exchange reference rates the European Central Bank publishes every working day,
/// free, for about 30 currencies since 1999. The whole history is one file, downloaded and kept in the database, so
/// conversions work offline afterwards.
/// <para>An amount is converted at the rate of its own day; on a weekend or holiday (no rate published), at the last
/// rate published before it. Two currencies other than the euro go through the euro: 1 CAD in USD is (USD per euro) ÷
/// (CAD per euro), the same day. Every converted amount is rounded to the cent; totals add the rounded amounts.</para>
/// </summary>
public sealed class ExchangeRates(Database db)
{
    public const string Source = "European Central Bank, euro foreign exchange reference rates";
    public const string PageUrl = "https://www.ecb.europa.eu/stats/policy_and_exchange_rates/euro_reference_exchange_rates/html/index.en.html";
    public const string DataUrl = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-hist.zip";

    /// <summary>The stored text of each currency's rates; a currency is read into numbers only when it is used.</summary>
    private Dictionary<string, string>? _raw;
    private readonly Dictionary<string, (DateOnly[] Days, decimal[] PerEuro)> _series = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private Dictionary<string, string> Raw()
    {
        lock (_gate) return _raw ??= db.LoadRates();
    }

    private (DateOnly[] Days, decimal[] PerEuro)? Series(string currency)
    {
        lock (_gate)
        {
            if (_series.TryGetValue(currency, out var known)) return known;
            if (!(_raw ??= db.LoadRates()).TryGetValue(currency, out var data)) return null;
            var points = data.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('='))
                .Select(p => (Day: DateOnly.ParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture), Rate: decimal.Parse(p[1], CultureInfo.InvariantCulture)))
                .OrderBy(p => p.Day).ToList();
            if (points.Count == 0) return null;
            return _series[currency] = ([.. points.Select(p => p.Day)], [.. points.Select(p => p.Rate)]);
        }
    }

    /// <summary>Forgets the rates read from the database (after an update).</summary>
    public void Reload()
    {
        lock (_gate) { _raw = null; _series.Clear(); }
    }

    public RatesStatus Status()
    {
        var raw = Raw();
        // Each currency's text runs from its newest day to its oldest (the order of the bank's file).
        var newest = raw.Values.Where(v => v.Length > 10).Select(v => v[..10]).DefaultIfEmpty().Max();
        var oldest = raw.Values.Where(v => v.Length > 10).Select(v => v.TrimEnd(';')).Select(v => v[(v.LastIndexOf(';') + 1)..][..10]).DefaultIfEmpty().Min();
        return new RatesStatus(Source, PageUrl, DataUrl, db.GetSetting("rates.updatedAt"), newest, oldest,
            raw.Count + (raw.Count > 0 ? 1 : 0), db.GetSetting("rates.error") is { Length: > 0 } e ? e : null);
    }

    public bool Knows(string currency) => currency == "EUR" || Raw().ContainsKey(currency);

    /// <summary>Units of <paramref name="currency"/> for one euro on <paramref name="day"/> (the last rate published up to that day).</summary>
    public (decimal PerEuro, DateOnly Day)? PerEuro(string currency, DateOnly day)
    {
        if (currency == "EUR") return (1m, day);
        if (Series(currency) is not { } s) return null;
        var i = Array.BinarySearch(s.Days, day);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        return (s.PerEuro[i], s.Days[i]);
    }

    /// <summary>The amount in <paramref name="to"/>, at the rate of <paramref name="day"/>; null when no rate is known for it.</summary>
    public Converted? Convert(decimal amount, string from, string to, DateOnly day)
    {
        if (from == to) return new Converted(amount, 1m, day);
        var a = PerEuro(from, day);
        var b = PerEuro(to, day);
        if (a is null || b is null) return null;
        // The day the rate was published: the euro has no rate of its own, so it is the other currency's day.
        var rateDay = from == "EUR" ? b.Value.Day : to == "EUR" ? a.Value.Day : a.Value.Day > b.Value.Day ? a.Value.Day : b.Value.Day;
        return new Converted(Math.Round(amount * b.Value.PerEuro / a.Value.PerEuro, 2, MidpointRounding.AwayFromZero),
            Math.Round(b.Value.PerEuro / a.Value.PerEuro, 6, MidpointRounding.AwayFromZero), rateDay);
    }

    /// <summary>Downloads the whole history of reference rates from the European Central Bank and keeps it.</summary>
    public async Task<RatesStatus> UpdateAsync(HttpClient? client = null, CancellationToken ct = default)
    {
        var own = client is null;
        client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, DataUrl);
            request.Headers.UserAgent.ParseAdd("ProductivityApp/1.0");
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var zip = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            Save(ParseZip(zip));
            db.SetSetting("rates.updatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            db.SetSetting("rates.error", "");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
        {
            db.SetSetting("rates.error", e is TaskCanceledException ? "The download took too long." : "The rates could not be downloaded: " + e.Message);
        }
        finally
        {
            if (own) client.Dispose();
        }
        return Status();
    }

    /// <summary>Keeps rates read from the European Central Bank's file (also used by tests with a file of their own).</summary>
    public void Save(Dictionary<string, string> series)
    {
        if (series.Count == 0) throw new InvalidDataException("The file holds no exchange rates.");
        db.SaveRates(series);
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
    /// currency had no rate. Gives, per currency, "yyyy-MM-dd=rate;..." (units per euro).
    /// </summary>
    public static Dictionary<string, string> ParseCsv(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return [];
        var header = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        var points = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            if (!DateOnly.TryParseExact(cells[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            for (var i = 1; i < cells.Length && i < header.Length; i++)
            {
                if (header[i].Length != 3 || !decimal.TryParse(cells[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) || rate <= 0) continue;
                if (!points.TryGetValue(header[i], out var sb)) points[header[i]] = sb = new StringBuilder();
                sb.Append(Iso(day)).Append('=').Append(rate.ToString(CultureInfo.InvariantCulture)).Append(';');
            }
        }
        return points.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
