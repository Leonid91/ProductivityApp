using System.Globalization;
using System.Text;

namespace ProductivityApp.Finance.Storage;

/// <summary>
/// The files downloaded for the exchange rates and the holidays, kept as downloaded (their own format) in a folder
/// next to the database, one folder per kind, per source and per download:
/// <code>
/// downloads/
///   exchange-rates/
///     bank-of-canada/
///       2026-11-10/rates.json        the latest download (n), in use
///       2026-10-12/rates.json        the one before (n-1), used when the latest cannot be read
///   holidays/
///     quebec/
///       2026-11-10/holidays-2016.json ... holidays-2027.json   (one file per year)
///     france/
///       2026-11-10/holidays.json
/// </code>
/// Every name follows the same pattern whatever the format: the source's name in lowercase words joined by hyphens,
/// the day of the download (yyyy-MM-dd), then what the file holds ("rates", "holidays", with the year when there is one
/// file per year) and the extension of its own format (zip, csv, json, ics). Two downloads at most per source. A
/// download is written to a "…partial" folder first and renamed once complete, so a copy is never half written.
/// </summary>
public sealed class DownloadStore(string root)
{
    public const string ExchangeRates = "exchange-rates";
    public const string Holidays = "holidays";

    /// <summary>The downloads kept per source: the latest and the one before.</summary>
    public const int Kept = 2;

    private const string Partial = ".partial";

    public string Root => root;

    /// <summary>A download: its day, its folder and its files (by name).</summary>
    public sealed record Copy(DateOnly Day, string Folder, IReadOnlyList<string> Files);

    /// <summary>A name in lowercase words joined by hyphens, without accents: "Canada (Federal)" gives "canada-federal".</summary>
    public static string Slug(string name)
    {
        var plain = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) plain.Append(c);
        var slug = new StringBuilder();
        foreach (var c in plain.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        var s = slug.ToString().Trim('-');
        return s.Length == 0 ? "source" : s.Length > 60 ? s[..60].TrimEnd('-') : s;
    }

    /// <summary>The folder of a source's downloads.</summary>
    public string FolderOf(string kind, string name) => Path.Combine(root, kind, Slug(name));

    /// <summary>The downloads of a source, the latest first.</summary>
    public List<Copy> Copies(string kind, string name)
    {
        var folder = FolderOf(kind, name);
        if (!Directory.Exists(folder)) return [];
        return [.. Directory.GetDirectories(folder)
            .Select(d => (Dir: d, Ok: DateOnly.TryParseExact(Path.GetFileName(d), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day), Day: day))
            .Where(x => x.Ok)
            .OrderByDescending(x => x.Day)
            .Select(x => new Copy(x.Day, x.Dir, [.. Directory.GetFiles(x.Dir).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)]))];
    }

    /// <summary>
    /// Keeps a download of <paramref name="day"/> as the latest copy: written whole to a partial folder, then put in
    /// place of a download of the same day (the one before stays); a download of a new day makes the latest one the one
    /// before, and the older one is deleted.
    /// </summary>
    public void Save(string kind, string name, DateOnly day, IReadOnlyList<(string FileName, byte[] Bytes)> files)
    {
        var folder = FolderOf(kind, name);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var partial = target + Partial;
        if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
        Directory.CreateDirectory(partial);
        foreach (var (fileName, bytes) in files) File.WriteAllBytes(Path.Combine(partial, fileName), bytes);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.Move(partial, target);
        foreach (var old in Copies(kind, name).Skip(Kept)) Directory.Delete(old.Folder, recursive: true);
        // A partial folder left by a stop in the middle of a download is never a copy.
        foreach (var stale in Directory.GetDirectories(folder, "*" + Partial)) Directory.Delete(stale, recursive: true);
    }

    /// <summary>Deletes every download of a source.</summary>
    public void Delete(string kind, string name)
    {
        var folder = FolderOf(kind, name);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    /// <summary>The downloads of a source go with it when it is renamed.</summary>
    public void Rename(string kind, string oldName, string newName)
    {
        var from = FolderOf(kind, oldName);
        var to = FolderOf(kind, newName);
        if (from == to || !Directory.Exists(from)) return;
        if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
        Directory.Move(from, to);
    }

    /// <summary>The name of a downloaded file: what it holds, its year when it is one of a file per year, and its own format.</summary>
    public static string FileName(string content, int? year, byte[] bytes) =>
        $"{content}{(year is { } y ? "-" + y.ToString(CultureInfo.InvariantCulture) : "")}.{Extension(bytes)}";

    /// <summary>The extension of a downloaded file, from its content: zip, json, ics or csv.</summary>
    public static string Extension(byte[] bytes)
    {
        if (bytes is [0x50, 0x4B, ..]) return "zip";
        var start = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 64)).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (start.StartsWith('{') || start.StartsWith('[')) return "json";
        if (start.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) return "ics";
        return "csv";
    }

    /// <summary>Copies every download to another folder (the database moved there), then deletes them here.</summary>
    public void MoveTo(string newRoot)
    {
        if (!Directory.Exists(root) || string.Equals(Path.GetFullPath(root), Path.GetFullPath(newRoot), StringComparison.OrdinalIgnoreCase)) return;
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(newRoot, Path.GetRelativePath(root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
        }
        Directory.Delete(root, recursive: true);
    }
}
