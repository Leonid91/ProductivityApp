using System.Collections.Concurrent;
using System.Security.Cryptography;
using ProductivityApp.Finance.Classification;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Pdf;
using ProductivityApp.Finance.Storage;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Services;

public sealed class DraftRow
{
    public int Index { get; set; }
    public DateOnly Date { get; set; }
    public string Label { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal? Balance { get; set; }
    public string MerchantKey { get; set; } = "";
    public string BankCategory { get; set; } = "";
    public string Brand { get; set; } = "";
    public ValueSource BrandSource { get; set; }
    public string CategoryId { get; set; } = "";
    public ValueSource CategorySource { get; set; }
    /// <summary>Already in the data from an earlier, overlapping statement.</summary>
    public bool Duplicate { get; set; }
    public int Page { get; set; }
    public double Top { get; set; }
}

public sealed class ImportDraft
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string FileName { get; init; }
    public required string FileHash { get; init; }
    public required byte[] Bytes { get; init; }
    public required IReadOnlyList<PageWords> Pages { get; init; }
    public required StatementParse Parse { get; set; }
    public List<DraftRow> Rows { get; set; } = [];
    public bool AlreadyImported { get; init; }
    /// <summary>Account the person chose for this file; nothing is chosen for them.</summary>
    public long? AccountId { get; set; }
    /// <summary>Account type the person chose (for a new account, or the type of the chosen account).</summary>
    public AccountKind? Kind { get; set; }
    public BankTemplate? Template { get; set; }
    /// <summary>Banking system of the chosen account's bank (or of the bank chosen for a new account).</summary>
    public BankingSystem System { get; set; }
    /// <summary>The other accounts of the same file (a statement of several accounts gives one draft per account).</summary>
    public List<ImportDraft> Others { get; } = [];
}

/// <summary>
/// Edits the person made to a draft row before saving it. <see cref="AllOfBrand"/> false: the new category is for this
/// line only and is not learned for the brand.
/// </summary>
public sealed record DraftEdit(int Index, string? Brand, string? CategoryId, bool Include, bool? AllOfBrand = null);


/// <param name="BankId">Bank of a new account; or <paramref name="NewBankName"/> to add the bank to the list.</param>
public sealed record CommitRequest(Guid DraftId, long? AccountId, string? NewAccountName, string Currency, AccountKind? Kind, List<DraftEdit> Edits,
    long? BankId = null, string? NewBankName = null);

public sealed record CommitResult(long StatementId, long AccountId, int Added, int SkippedDuplicates, string? FirstDate, string? LastDate);

/// <summary>
/// Turns a statement into a draft the person reviews, then saves it. Statements come as PDF (with text, or scanned),
/// as a picture of the page, or as the files banks let people download: CSV, Excel, OpenDocument, Word, web pages,
/// OFX / QFX / QBO and QIF.
/// </summary>
public sealed class ImportService(Database db, Func<string> defaultCurrency, TransactionService transactions)
{
    /// <summary>Every kind of file the import takes, by extension.</summary>
    public static readonly string[] Extensions =
    [
        ".pdf", ".csv", ".tsv", ".txt", ".xlsx", ".xlsm", ".xls", ".ods", ".odt", ".docx", ".htm", ".html", ".ofx", ".qfx", ".qbo", ".qif",
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff",
    ];

    private readonly ConcurrentDictionary<Guid, ImportDraft> _drafts = new();

    public ImportDraft? Get(Guid id) => _drafts.GetValueOrDefault(id);

    public void Discard(Guid id) => _drafts.TryRemove(id, out _);

    public async Task<ImportDraft> ParseAsync(string fileName, byte[] bytes, CancellationToken ct = default)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        // A picture of a statement (a photo, a screenshot) becomes a one-page PDF and is read like a scanned statement.
        if (PdfReader.IsImage(bytes)) bytes = await PdfReader.ImageToPdfAsync(bytes).ConfigureAwait(false);
        var isPdf = bytes.Length >= 5 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F';
        if (!isPdf)
        {
            if (TableFiles.Kind(bytes, fileName) is null && MoneyFiles.Kind(bytes, fileName) is null && !CsvStatementReader.LooksLikeCsv(bytes, fileName))
                throw new InvalidDataException("This file is not a statement the app can read: PDF, picture, CSV, Excel, OpenDocument, Word, OFX, QFX, QBO or QIF.");
            var file = new ImportDraft
            {
                FileName = fileName, FileHash = hash, Bytes = bytes, Pages = [],
                Parse = ReadFile(bytes, fileName, null),
                AlreadyImported = db.HasStatementHash(hash),
            };
            Fill(file);
            _drafts[file.Id] = file;
            return file;
        }
        var pages = await PdfReader.ReadAsync(bytes, ct).ConfigureAwait(false);
        // One statement of several accounts (current account and savings books): one draft per account.
        var sections = AccountSections.Split(pages);
        if (sections.Count > 1)
        {
            var drafts = sections.Select((sec, i) => PdfDraft($"{fileName} · {sec.Title}", $"{hash}#{i + 1}", bytes, sec.Pages, sec)).ToList();
            drafts[0].Others.AddRange(drafts.Skip(1));
            return drafts[0];
        }
        return PdfDraft(fileName, hash, bytes, pages);
    }

    private ImportDraft PdfDraft(string fileName, string hash, byte[] bytes, IReadOnlyList<PageWords> pages, AccountSections.Section? section = null)
    {
        var firstPage = TextNorm.Fold(string.Join(' ', pages[0].Words.Select(w => w.Text)));
        var template = db.Templates()
            .Where(t => t.MatchText.Trim().Length > 0 && firstPage.Contains(TextNorm.Fold(t.MatchText.Trim()), StringComparison.Ordinal))
            .OrderByDescending(t => t.MatchText.Length)
            .FirstOrDefault();

        var parse = StatementParser.Parse(pages, template, defaultCurrency());
        if (template is not null && !parse.Verified)
        {
            var auto = StatementParser.Parse(pages, null, defaultCurrency());
            if (auto.Verified) { parse = auto; template = null; }
        }
        // One account of a statement of several: its own number, and its type from its own title (a savings book or not).
        if (section is not null)
        {
            if (section.Number.Length >= 4) parse.Last4 = section.Number[^4..];
            if (parse.Kind != AccountKind.CreditCard)
                parse.Kind = Keywords.ContainsAny(TextNorm.Fold(section.Title), Keywords.Savings) ? AccountKind.Savings : AccountKind.Bank;
        }

        var draft = new ImportDraft
        {
            FileName = fileName,
            FileHash = hash,
            Bytes = bytes,
            Pages = pages,
            Parse = parse,
            AlreadyImported = db.HasStatementHash(hash),
            Template = template,
        };
        Fill(draft);
        _drafts[draft.Id] = draft;
        return draft;
    }

    /// <summary>Reads the draft again with a template being edited (live preview), or with the automatic reader when null.</summary>
    public ImportDraft Reparse(Guid id, BankTemplate? template)
    {
        var draft = _drafts.GetValueOrDefault(id) ?? throw new KeyNotFoundException("This import is no longer open.");
        if (draft.Pages.Count == 0) throw new InvalidOperationException("Columns can only be set on PDF statements.");
        draft.Template = template;
        Read(draft);
        return draft;
    }

    /// <summary>Reads the file for the account type the person chose (a card statement is read as a card statement).</summary>
    private void Read(ImportDraft draft)
    {
        if (draft.Pages.Count == 0)
            draft.Parse = ReadFile(draft.Bytes, draft.FileName, draft.Kind);
        else
        {
            var hint = draft.Kind switch
            {
                AccountKind.CreditCard => StatementKindHint.CreditCard,
                AccountKind.Bank or AccountKind.Savings => StatementKindHint.Bank,
                _ => StatementKindHint.Auto,
            };
            var t = draft.Template;
            if (t is not null && hint != StatementKindHint.Auto) t = new BankTemplate { Id = t.Id, Name = t.Name, MatchText = t.MatchText, Columns = t.Columns, DateOrder = t.DateOrder, Kind = hint, UpdatedAt = t.UpdatedAt };
            draft.Parse = StatementParser.Parse(draft.Pages, t, defaultCurrency(), hint);
        }
        if (draft.Kind is { } k) draft.Parse.Kind = k;
        Fill(draft);
    }

    /// <summary>Reads a file that is not a PDF: a table file (Excel, OpenDocument, Word, web page), OFX / QFX / QBO, QIF or CSV.</summary>
    private StatementParse ReadFile(byte[] bytes, string fileName, AccountKind? kind)
    {
        if (TableFiles.Kind(bytes, fileName) is { } table)
            return CsvStatementReader.ParseTable(TableFiles.Read(bytes, fileName), fileName, defaultCurrency(), kind, table);
        return MoneyFiles.Kind(bytes, fileName) switch
        {
            "OFX" => MoneyFiles.ParseOfx(bytes, defaultCurrency(), kind),
            "QIF" => MoneyFiles.ParseQif(bytes, fileName, defaultCurrency(), kind),
            _ => CsvStatementReader.Parse(bytes, fileName, defaultCurrency(), kind),
        };
    }

    private void Fill(ImportDraft draft)
    {
        var classifier = transactions.NewClassifier(draft.System);
        var kind = draft.Kind ?? draft.Parse.Kind;
        draft.Rows = draft.Parse.Rows.Select((r, i) =>
        {
            var c = classifier.Classify(r.Label, r.Amount, r.BankCategory, kind);
            return new DraftRow
            {
                Index = i, Date = r.Date, Label = r.Label, Amount = r.Amount, Balance = r.Balance, MerchantKey = c.MerchantKey, BankCategory = r.BankCategory,
                Brand = c.Brand, BrandSource = c.BrandSource, CategoryId = c.CategoryId, CategorySource = c.CategorySource,
                Page = r.Page, Top = r.Top,
            };
        }).ToList();

        MarkDuplicates(draft, draft.AccountId);
    }

    /// <summary>Days apart the same operation can be dated in two files of one account (the day of the purchase, the day the bank posted it).</summary>
    public const int DateShift = 3;

    /// <summary>
    /// Marks rows already stored in the chosen account: the same amount and the same operation (labels equal, one
    /// containing the other, or the same merchant), on the same day first. Matched one for one: two identical coffees
    /// the same day are two operations, and a card payment is never taken for a transfer of the same amount.
    /// </summary>
    /// <remarks>
    /// Then, among what is left on both sides, the same operation dated up to <see cref="DateShift"/> days apart (one
    /// file gives the day of the purchase, the other the day it was posted), but only when one of the two files covers
    /// the other's day without holding the operation itself: a file that covers a day and has nothing on it for that
    /// amount and that shop shows the operation under another day. Two real coffees on both sides of the end of a
    /// statement stay two. An operation entered by hand is the same operation when its brand is the bank's wording.
    /// </remarks>
    private void MarkDuplicates(ImportDraft draft, long? accountId)
    {
        foreach (var r in draft.Rows) r.Duplicate = false;
        if (accountId is null || draft.Rows.Count == 0) return;
        var from = draft.Rows.Min(r => r.Date);
        var to = draft.Rows.Max(r => r.Date);
        var pool = db.TransactionsForAccount(accountId.Value, from.AddDays(-DateShift), to.AddDays(DateShift)).ToList();
        var left = new HashSet<long>(pool.Select(t => t.Id));
        var exact = pool.GroupBy(t => (t.Date, t.Amount)).ToDictionary(g => g.Key, g => g.ToList());
        var unmatched = new List<DraftRow>();
        foreach (var r in draft.Rows)
        {
            var same = exact.GetValueOrDefault((r.Date, r.Amount))?.FirstOrDefault(t => left.Contains(t.Id) && SameOperation(Wording(t), r.Label));
            if (same is null) { unmatched.Add(r); continue; }
            r.Duplicate = true;
            left.Remove(same.Id);
        }
        if (unmatched.Count == 0 || left.Count == 0) return;

        // What each file says it covers: its period, or the days of its operations.
        var fileFrom = draft.Parse.PeriodStart ?? from;
        var fileTo = draft.Parse.PeriodEnd ?? to;
        var statements = db.Statements().Where(st => st.AccountId == accountId.Value).ToDictionary(st => st.Id);
        Dictionary<long, (DateOnly From, DateOnly To)>? spans = null;
        (DateOnly From, DateOnly To)? Covers(long? statementId)
        {
            if (statementId is not { } id || !statements.TryGetValue(id, out var st)) return null;
            if (st.PeriodStart is { } a && st.PeriodEnd is { } b) return (a, b);
            spans ??= db.TransactionsForAccount(accountId.Value, DateOnly.MinValue, DateOnly.MaxValue)
                .Where(t => t.StatementId is not null).GroupBy(t => t.StatementId!.Value)
                .ToDictionary(g => g.Key, g => (g.Min(t => t.Date), g.Max(t => t.Date)));
            return spans.TryGetValue(id, out var span) ? span : null;
        }
        foreach (var r in unmatched)
        {
            var near = pool
                .Where(t => left.Contains(t.Id) && t.Amount == r.Amount && t.Date != r.Date && Math.Abs(t.Date.DayNumber - r.Date.DayNumber) <= DateShift)
                .Where(t => SameOperation(Wording(t), r.Label))
                .Where(t => (t.Date >= fileFrom && t.Date <= fileTo) || (Covers(t.StatementId) is { } c && r.Date >= c.From && r.Date <= c.To))
                .OrderBy(t => Math.Abs(t.Date.DayNumber - r.Date.DayNumber)).ThenBy(t => t.Id)
                .FirstOrDefault();
            if (near is null) continue;
            r.Duplicate = true;
            left.Remove(near.Id);
        }
    }

    /// <summary>What an operation stored says: the bank's wording, or the brand of one entered by hand.</summary>
    private static string Wording(Transaction t) => t.RawLabel.Length > 0 ? t.RawLabel : t.Brand;

    public static bool SameOperation(string a, string b)
    {
        var na = Classifier.Normalize(a);
        var nb = Classifier.Normalize(b);
        if (na == nb) return true;
        if (na.Length >= 4 && nb.Length >= 4 && (na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal))) return true;
        var ka = Classifier.MerchantKey(na).Split(' ')[0];
        var kb = Classifier.MerchantKey(nb).Split(' ')[0];
        return ka.Length >= 3 && ka == kb;
    }

    /// <summary>The person picks the account (or a new one and its type); the file is then read for that type.</summary>
    public ImportDraft Choose(Guid id, long? accountId, AccountKind? newKind, long? bankId = null)
    {
        var draft = _drafts.GetValueOrDefault(id) ?? throw new KeyNotFoundException("This import is no longer open.");
        if (accountId is { } a)
        {
            var account = db.Accounts().FirstOrDefault(x => x.Id == a) ?? throw new KeyNotFoundException("This account no longer exists.");
            draft.AccountId = a;
            draft.Kind = account.Kind;
            draft.System = account.System;
        }
        else
        {
            draft.AccountId = null;
            draft.Kind = newKind;
            draft.System = db.Banks().FirstOrDefault(b => b.Id == bankId)?.System ?? BankingSystem.NorthAmerica;
        }
        Read(draft);
        return draft;
    }

    public CommitResult Commit(CommitRequest request)
    {
        var draft = _drafts.GetValueOrDefault(request.DraftId) ?? throw new KeyNotFoundException("This import is no longer open.");
        if (draft.AlreadyImported) throw new InvalidOperationException("This statement was already imported.");
        if (request.AccountId is null && request.Kind is null) throw new ArgumentException("Choose the account this file belongs to, or the type of the new account.");
        Choose(draft.Id, request.AccountId, request.Kind, request.BankId);
        var existing = request.AccountId is { } chosen ? db.Accounts().First(a => a.Id == chosen) : null;
        // An existing account keeps its currency; a new one takes the one chosen (its bank's by default).
        var currency = existing?.Currency ?? request.Currency;
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)) throw new ArgumentException("A currency is a three-letter code such as CAD.");
        if (draft.Rows.Count == 0) throw new InvalidOperationException("No operations were found in this statement.");
        var edits = request.Edits.ToDictionary(e => e.Index);
        foreach (var e in request.Edits)
            if (e.CategoryId is not null && !Categories.Exists(e.CategoryId)) throw new ArgumentException($"Unknown category '{e.CategoryId}'.");

        MarkDuplicates(draft, request.AccountId);
        long accountId = 0, statementId = 0;
        int added = 0, skipped = 0;
        var learned = new List<(string Key, string Brand, string? Category)>();
        db.InTransaction(() =>
        {
            var bankId = request.BankId;
            if (existing is null && bankId is null && request.NewBankName?.Trim() is { Length: > 0 } bankName)
                bankId = db.AddBank(new Bank { Name = bankName, Currency = currency, System = draft.System });
            if (request.AccountId is null && bankId is null) throw new ArgumentException("Choose the bank of the new account.");
            accountId = request.AccountId ?? db.AddAccount(new Account
            {
                Name = string.IsNullOrWhiteSpace(request.NewAccountName) ? DefaultAccountName(draft.Parse) : request.NewAccountName.Trim(),
                Currency = currency,
                Kind = request.Kind!.Value,
                Bank = draft.Parse.Bank,
                Last4 = draft.Parse.Last4,
                BankId = bankId,
            });
            var included = draft.Rows.Where(r => !edits.TryGetValue(r.Index, out var e) || e.Include).ToList();
            statementId = db.AddStatement(new StatementRecord
            {
                AccountId = accountId,
                FileName = Path.GetFileName(draft.FileName),
                FileHash = draft.FileHash,
                PeriodStart = draft.Parse.PeriodStart,
                PeriodEnd = draft.Parse.PeriodEnd,
                OpeningBalance = draft.Parse.OpeningBalance,
                ClosingBalance = draft.Parse.ClosingBalance,
                Verified = draft.Parse.Verified,
                Method = draft.Parse.Method + (draft.Parse.UsedOcr ? " + OCR" : ""),
                ImportedAt = DateTime.UtcNow,
            });
            foreach (var r in included)
            {
                if (r.Duplicate) { skipped++; continue; }
                var t = new Transaction
                {
                    AccountId = accountId, StatementId = statementId, Date = r.Date, Amount = r.Amount, Currency = currency,
                    RawLabel = r.Label, MerchantKey = r.MerchantKey, Brand = r.Brand, BrandSource = r.BrandSource, BankCategory = r.BankCategory,
                    CategoryId = r.CategoryId, CategorySource = r.CategorySource,
                };
                if (edits.TryGetValue(r.Index, out var e))
                {
                    if (e.Brand is { } b && b.Trim().Length > 0 && b.Trim() != r.Brand) { t.Brand = b.Trim(); t.BrandSource = ValueSource.User; }
                    if (e.CategoryId is { } c && c != r.CategoryId) { t.CategoryId = c; t.CategorySource = ValueSource.User; }
                    var teachCategory = t.CategorySource == ValueSource.User && e.AllOfBrand != false;
                    if (t.BrandSource == ValueSource.User || teachCategory)
                        learned.Add((t.MerchantKey, t.Brand, teachCategory ? t.CategoryId : null));
                }
                db.AddTransaction(t);
                added++;
            }
            foreach (var (key, brand, category) in learned)
            {
                if (key.Length > 0) db.UpsertKeyRule(key, brand, category);
                if (category is not null) db.UpsertBrandCategory(brand, category);
            }
        });
        // Classify and link again over everything: a transfer whose other half arrives in a later file is paired
        // with it, whatever the order the files were imported in.
        transactions.ReapplyRules();
        _drafts.TryRemove(draft.Id, out _);
        var kept = draft.Rows.Where(r => !r.Duplicate).ToList();
        return new CommitResult(statementId, accountId, added, skipped,
            kept.Count > 0 ? kept.Min(r => r.Date).ToString("yyyy-MM-dd") : null,
            kept.Count > 0 ? kept.Max(r => r.Date).ToString("yyyy-MM-dd") : null);
    }

    public static string DefaultAccountName(StatementParse p)
    {
        var kind = p.Kind switch { AccountKind.CreditCard => "Card", AccountKind.Savings => "Savings", _ => "Account" };
        var name = p.Bank is null ? kind : $"{p.Bank} {kind.ToLowerInvariant()}";
        return p.Last4 is null ? name : $"{name} •••• {p.Last4}";
    }

    public Task<byte[]> RenderPageAsync(Guid id, int page, uint width)
    {
        var draft = _drafts.GetValueOrDefault(id) ?? throw new KeyNotFoundException("This import is no longer open.");
        return PdfReader.RenderPagePngAsync(draft.Bytes, page, width);
    }
}
