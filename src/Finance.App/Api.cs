using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Reports;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Host;

/// <summary>Everything the interface can ask the engine. Each method takes and returns plain JSON.</summary>
internal static class Api
{
    public static void Register(Bridge b, AppServices s, MainForm window, bool nonClientRegions)
    {
        // ------------------------------------------------------------ app & window
        b.Register("app.init", _ => new
        {
            version = typeof(Api).Assembly.GetName().Version?.ToString(3),
            settings = s.Settings(),
            categories = Categories.All.Select(c => new { c.Id, c.Name, kind = c.Kind, c.Icon, c.Group }),
            dateFormats = Formatting.DateFormats,
            ocrAvailable = Finance.Pdf.PdfReader.OcrAvailable,
            nonClientRegions,
            maximized = window.Maximized,
            dataFolder = s.DataDir,
            bankProfiles = AppServices.BankProfiles.Select(p => new { p.Id, p.Name, p.Proven }),
        });
        b.Register("app.ready", _ => null);
        b.Register("window.minimize", _ => { window.WindowState = FormWindowState.Minimized; return null; });
        b.Register("window.toggleMaximize", _ => { window.ToggleMaximize(); return new { maximized = window.Maximized }; });
        b.Register("window.close", _ => { window.BeginInvoke(window.Close); return null; });
        b.Register("window.drag", _ => { window.StartDrag(); return null; });
        b.Register("window.resize", p => { window.StartResize(p.Get<string>("edge")); return null; });

        b.Register("settings.set", p =>
        {
            var key = p.Get<string>("key");
            s.SetSetting(key, p.Get<string>("value"));
            if (key == "theme") window.ApplyTheme(s.Setting("theme") == "dark");
            // How own-account movements are recognised depends on the banking system.
            if (key == "bankingSystem") s.Transactions.ReapplyRules();
            return s.Settings();
        });

        // ------------------------------------------------------------ periods
        b.Register("periods.index", p => s.Summaries.Index(p.Opt<string>("currency") ?? s.Setting("currency")));
        b.Register("periods.summary", p => s.Summaries.Summarize(
            ParseDate(p.Get<string>("from")), ParseDate(p.Get<string>("to")),
            p.Opt<Granularity?>("granularity") ?? Granularity.Month,
            p.Opt<string>("currency") ?? s.Setting("currency")));

        // ------------------------------------------------------------ operations
        b.Register("tx.setCategory", p => new { changed = s.Transactions.SetCategory(p.Get<long>("id"), p.Get<string>("categoryId"), p.Opt<bool?>("allOfBrand") ?? true) });
        b.Register("tx.setBrand", p => new { changed = s.Transactions.SetBrand(p.Get<long>("id"), p.Get<string>("brand")) });
        b.Register("tx.create", p => s.Transactions.Create(p.Get<TransactionInput>("input")));
        b.Register("tx.update", p => s.Transactions.Update(p.Get<long>("id"), p.Get<TransactionInput>("input"), p.Opt<bool?>("allOfBrand") ?? true));
        b.Register("tx.delete", p => { s.Transactions.Delete(p.Get<long>("id")); return null; });

        // ------------------------------------------------------------ brands & dictionary
        b.Register("brands.list", _ => Brands(s));
        b.Register("brands.setCategory", p =>
        {
            var brand = p.Get<string>("brand");
            var category = p.Get<string>("categoryId");
            if (!Categories.Exists(category)) throw new ArgumentException("Unknown category.");
            s.Db.UpsertBrandCategory(brand, category);
            return new { changed = s.Transactions.ReapplyRules() };
        });
        b.Register("brands.deleteRule", p =>
        {
            s.Db.DeleteBrandRule(p.Get<long>("id"));
            return new { changed = s.Transactions.ReapplyRules() };
        });

        // ------------------------------------------------------------ accounts & statements
        b.Register("accounts.list", _ => s.Db.Accounts());
        b.Register("accounts.update", p =>
        {
            var a = s.Db.Accounts().FirstOrDefault(x => x.Id == p.Get<long>("id")) ?? throw new KeyNotFoundException("This account no longer exists.");
            var name = p.Get<string>("name").Trim();
            if (name.Length is 0 or > 80) throw new ArgumentException("The account name must be 1 to 80 characters.");
            a.Name = name;
            var kindChanged = false;
            if (p.Opt<AccountKind?>("kind") is { } kind && kind != a.Kind) { a.Kind = kind; kindChanged = true; }
            s.Db.UpdateAccount(a);
            if (kindChanged) s.Transactions.ReapplyRules();
            return a;
        });
        b.Register("accounts.delete", p => { s.Db.DeleteAccount(p.Get<long>("id")); s.Transactions.ReapplyRules(); return null; });
        b.Register("statements.list", _ =>
        {
            var accounts = s.Db.Accounts().ToDictionary(a => a.Id);
            return s.Db.Statements().Select(st => new
            {
                st.Id, st.AccountId, account = accounts.GetValueOrDefault(st.AccountId)?.Name ?? "", st.FileName,
                periodStart = st.PeriodStart?.ToString("yyyy-MM-dd"), periodEnd = st.PeriodEnd?.ToString("yyyy-MM-dd"),
                st.OpeningBalance, st.ClosingBalance, st.Verified, st.Method, importedAt = st.ImportedAt.ToString("O"), st.TransactionCount,
                currency = accounts.GetValueOrDefault(st.AccountId)?.Currency ?? "",
            });
        });
        b.Register("statements.delete", p => { s.Db.DeleteStatement(p.Get<long>("id")); s.Transactions.ReapplyRules(); return null; });

        // ------------------------------------------------------------ import
        b.RegisterBackground("import.parseFile", async p =>
        {
            var path = p.Get<string>("path");
            if (!File.Exists(path)) throw new FileNotFoundException("This file no longer exists.");
            if (new FileInfo(path).Length > 60 * 1024 * 1024) throw new InvalidDataException("This file is larger than 60 MB.");
            if (Path.GetExtension(path).ToLowerInvariant() is not (".pdf" or ".csv" or ".txt" or ".tsv")) throw new InvalidDataException("Only PDF statements and CSV exports can be imported.");
            var draft = await s.Imports.ParseAsync(Path.GetFileName(path), await File.ReadAllBytesAsync(path));
            return DraftView(draft, s);
        });
        b.RegisterBackground("import.parseBytes", async p =>
        {
            var name = p.Get<string>("name");
            var bytes = Convert.FromBase64String(p.Get<string>("base64"));
            if (bytes.Length > 60 * 1024 * 1024) throw new InvalidDataException("This file is larger than 60 MB.");
            var draft = await s.Imports.ParseAsync(Path.GetFileName(name), bytes);
            return DraftView(draft, s);
        });
        // The person says which account the file belongs to (or the type of a new account); nothing is guessed.
        b.RegisterBackground("import.choose", p =>
        {
            var id = p.Get<Guid>("draftId");
            return Task.FromResult<object?>(DraftView(s.Imports.Choose(id, p.Opt<long?>("accountId"), p.Opt<AccountKind?>("kind")), s));
        });
        b.Register("import.commit", p => s.Imports.Commit(new CommitRequest(
            p.Get<Guid>("draftId"), p.Opt<long?>("accountId"), p.Opt<string>("newAccountName"),
            p.Get<string>("currency"), p.Opt<AccountKind?>("kind"), p.Opt<List<DraftEdit>>("edits") ?? [])));
        b.Register("import.discard", p => { s.Imports.Discard(p.Get<Guid>("draftId")); return null; });
        b.RegisterBackground("import.page", async p =>
        {
            var id = p.Get<Guid>("draftId");
            var draft = s.Imports.Get(id) ?? throw new KeyNotFoundException("This import is no longer open.");
            var index = p.Get<int>("page");
            if (index < 0 || index >= draft.Pages.Count) throw new ArgumentException("No such page.");
            var width = (uint)Math.Clamp(p.Opt<int?>("width") ?? 1400, 400, 3000);
            var png = await s.Imports.RenderPageAsync(id, index, width);
            var page = draft.Pages[index];
            return new
            {
                png = Convert.ToBase64String(png),
                widthPt = page.Width,
                heightPt = page.Height,
                pageCount = draft.Pages.Count,
                fromOcr = page.FromOcr,
            };
        });
        b.RegisterBackground("import.preview", p =>
        {
            var id = p.Get<Guid>("draftId");
            var template = p.Opt<BankTemplate>("template");
            return Task.FromResult<object?>(DraftView(s.Imports.Reparse(id, template), s));
        });

        // ------------------------------------------------------------ templates
        b.Register("templates.list", _ => s.Db.Templates());
        b.Register("templates.save", p =>
        {
            var t = p.Get<BankTemplate>("template");
            t.Name = t.Name.Trim();
            t.MatchText = t.MatchText.Trim();
            if (t.Name.Length is 0 or > 80) throw new ArgumentException("Give the template a name.");
            if (t.MatchText.Length < 3) throw new ArgumentException("The recognition text must be at least 3 characters.");
            if (!t.Columns.Any(c => c.Role == ColumnRole.Date)) throw new ArgumentException("Mark the date column.");
            if (!t.Columns.Any(c => c.Role is ColumnRole.Debit or ColumnRole.Credit or ColumnRole.Amount)) throw new ArgumentException("Mark at least one amount column.");
            t.Id = s.Db.SaveTemplate(t);
            return t;
        });
        b.Register("templates.delete", p => { s.Db.DeleteTemplate(p.Get<long>("id")); return null; });

        // ------------------------------------------------------------ files (in-app browser, no system dialogs)
        b.Register("fs.places", _ => Places());
        b.RegisterBackground("fs.list", p => Task.FromResult<object?>(ListFolder(p.Get<string>("path"), p.Opt<bool?>("foldersOnly") ?? false)));
        b.Register("shell.open", p =>
        {
            var path = p.Get<string>("path");
            if (!File.Exists(path) || !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("The report is no longer there.");
            // Only reports this app wrote can be opened from the interface.
            var reports = s.Setting("reportsFolder") is { Length: > 0 } rf ? rf : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(reports), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Only reports can be opened.");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        });
        b.Register("shell.reveal", p =>
        {
            var path = p.Get<string>("path");
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("It is no longer there.");
            Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
            return null;
        });

        // ------------------------------------------------------------ reports
        b.Register("report.defaults", p =>
        {
            var summary = SummaryFrom(p, s);
            return new { folder = ReportsFolder(s), fileName = ReportBuilder.FileName(summary) };
        });
        b.RegisterBackground("report.export", p =>
        {
            var summary = SummaryFrom(p, s);
            var folder = p.Opt<string>("folder") is { Length: > 0 } f ? f : ReportsFolder(s);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("This folder does not exist.");
            var name = p.Opt<string>("fileName") is { Length: > 0 } n ? n.Trim() : ReportBuilder.FileName(summary);
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("The file name contains characters Windows does not allow.");
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
            var path = Path.Combine(folder, name);
            var pdf = ReportBuilder.Build(summary, s.Setting("dateFormat"), DateTime.Now, ReportOptions(s));
            File.WriteAllBytes(path, pdf);
            s.Db.SetSetting("reportsFolder", folder);
            return Task.FromResult<object?>(new { path });
        });

        // ------------------------------------------------------------ data
        b.Register("data.eraseAll", _ => { s.Db.EraseAll(); return null; });
    }

    private static DateOnly ParseDate(string s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new ArgumentException("Invalid date.");

    private static PeriodSummary SummaryFrom(JsonElement p, AppServices s) => s.Summaries.Summarize(
        ParseDate(p.Get<string>("from")), ParseDate(p.Get<string>("to")),
        p.Opt<Granularity?>("granularity") ?? Granularity.Month, p.Opt<string>("currency") ?? s.Setting("currency"));

    private static ReportBuilder.Options ReportOptions(AppServices s) => new(
        PieShowAll: s.Setting("pieShowAll") != "false",
        Transfers: s.Setting("periodTransfers"));

    private static string ReportsFolder(AppServices s)
    {
        var saved = s.Setting("reportsFolder");
        if (saved.Length > 0 && Directory.Exists(saved)) return saved;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static object DraftView(ImportDraft d, AppServices s)
    {
        var p = d.Parse;
        return new
        {
            id = d.Id,
            d.FileName,
            d.AlreadyImported,
            status = p.Status,
            p.Difference,
            p.ComputedClosing,
            opening = p.OpeningBalance,
            closing = p.ClosingBalance,
            periodStart = p.PeriodStart?.ToString("yyyy-MM-dd"),
            periodEnd = p.PeriodEnd?.ToString("yyyy-MM-dd"),
            p.Currency,
            kind = d.Kind,
            accountId = d.AccountId,
            p.Bank,
            p.Last4,
            p.Method,
            p.UsedOcr,
            p.Profile,
            p.NewestFirst,
            p.ZeroRowsSkipped,
            p.Language,
            p.DateOrder,
            rowChecks = p.RowBalanceChecks,
            rowMismatches = p.RowBalanceMismatches,
            defaultAccountName = ImportService.DefaultAccountName(p),
            template = d.Template,
            columns = p.Columns,
            pageCount = d.Pages.Count,
            suggestedMatch = SuggestMatch(p.FirstPageText, p.Bank),
            rows = d.Rows.Select(r => new
            {
                r.Index, date = r.Date.ToString("yyyy-MM-dd"), r.Label, r.Amount, r.Balance, r.Brand, r.CategoryId,
                brandSource = r.BrandSource.ToString(), categorySource = r.CategorySource.ToString(), r.Duplicate, r.Page, r.Top,
            }),
        };
    }

    /// <summary>Proposes the recognition text for a template: the bank name if known, otherwise the first line of the statement.</summary>
    private static string SuggestMatch(string firstPage, string? bank)
    {
        if (bank is not null) return bank;
        var line = firstPage.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length >= 4 && l.Any(char.IsLetter) && !l.Any(char.IsDigit));
        return line is null ? "" : line.Length > 40 ? line[..40] : line;
    }

    private static object Brands(AppServices s)
    {
        var rules = s.Db.BrandRules();
        var txs = s.Db.AllTransactions();
        var brandCats = rules.Where(r => r.MerchantKey.Length == 0).ToDictionary(r => r.Brand, StringComparer.OrdinalIgnoreCase);
        var brands = txs.GroupBy(t => t.Brand, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var main = g.GroupBy(t => t.CategoryId).OrderByDescending(x => x.Count()).First().Key;
            return new
            {
                brand = g.First().Brand,
                count = g.Count(),
                spent = -g.Where(t => t.Amount < 0).Sum(t => t.Amount),
                received = g.Where(t => t.Amount > 0).Sum(t => t.Amount),
                categoryId = brandCats.TryGetValue(g.Key, out var r) && r.CategoryId is not null ? r.CategoryId : main,
                learned = brandCats.ContainsKey(g.Key) || rules.Any(x => x.MerchantKey.Length > 0 && string.Equals(x.Brand, g.Key, StringComparison.OrdinalIgnoreCase)),
                currency = g.First().Currency,
                lastDate = g.Max(t => t.Date).ToString("yyyy-MM-dd"),
            };
        }).OrderByDescending(x => x.count).ToList();
        return new
        {
            brands,
            rules = rules.OrderByDescending(r => r.UpdatedAt).Select(r => new { r.Id, r.MerchantKey, r.Brand, r.CategoryId, updatedAt = r.UpdatedAt.ToString("O") }),
            builtInCount = BuiltInBrandsCount,
        };
    }

    private static readonly int BuiltInBrandsCount = Finance.Classification.BuiltInBrands.All.Count;

    private static object Places()
    {
        var list = new List<object>();
        void Add(string name, Environment.SpecialFolder f, string icon)
        {
            var path = Environment.GetFolderPath(f);
            if (path.Length > 0 && Directory.Exists(path)) list.Add(new { name, path, icon });
        }
        Add("Desktop", Environment.SpecialFolder.DesktopDirectory, "desktop");
        Add("Documents", Environment.SpecialFolder.MyDocuments, "documents");
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) list.Add(new { name = "Downloads", path = downloads, icon = "downloads" });
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network))
        {
            var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local disk" : drive.VolumeLabel;
            list.Add(new { name = $"{label} ({drive.Name.TrimEnd('\\')})", path = drive.RootDirectory.FullName, icon = drive.DriveType == DriveType.Removable ? "usb" : "drive" });
        }
        return list;
    }

    private static object ListFolder(string path, bool foldersOnly)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) throw new DirectoryNotFoundException("This folder does not exist.");
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var folders = dir.EnumerateDirectories("*", options).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => new { name = d.Name, path = d.FullName, isFolder = true, size = 0L, modified = d.LastWriteTime.ToString("O") });
        var files = foldersOnly
            ? []
            : dir.EnumerateFiles("*", options).Where(f => f.Extension.ToLowerInvariant() is ".pdf" or ".csv").OrderByDescending(f => f.LastWriteTime)
                .Select(f => new { name = f.Name, path = f.FullName, isFolder = false, size = f.Length, modified = f.LastWriteTime.ToString("O") });
        return new { path = dir.FullName, parent = dir.Parent?.FullName, entries = folders.Concat(files).Take(2000).ToList() };
    }
}
