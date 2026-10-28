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
    public long? SuggestedAccountId { get; set; }
    public BankTemplate? Template { get; set; }
}

/// <summary>Edits the person made to a draft row before saving it.</summary>
public sealed record DraftEdit(int Index, string? Brand, string? CategoryId, bool Include);

public sealed record CommitRequest(Guid DraftId, long? AccountId, string? NewAccountName, string Currency, AccountKind Kind, List<DraftEdit> Edits);

public sealed record CommitResult(long StatementId, long AccountId, int Added, int SkippedDuplicates, string? FirstDate, string? LastDate);

/// <summary>Turns a statement PDF into a draft the person reviews, then saves it.</summary>
public sealed class ImportService(Database db, Func<string> defaultCurrency, TransactionService transactions)
{
    private readonly ConcurrentDictionary<Guid, ImportDraft> _drafts = new();

    public ImportDraft? Get(Guid id) => _drafts.GetValueOrDefault(id);

    public void Discard(Guid id) => _drafts.TryRemove(id, out _);

    public async Task<ImportDraft> ParseAsync(string fileName, byte[] bytes, CancellationToken ct = default)
    {
        var isPdf = bytes.Length >= 5 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F';
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!isPdf)
        {
            if (!CsvStatementReader.LooksLikeCsv(bytes, fileName)) throw new InvalidDataException("This file is neither a PDF statement nor a CSV export.");
            var csv = new ImportDraft
            {
                FileName = fileName, FileHash = hash, Bytes = bytes, Pages = [],
                Parse = Focus(CsvStatementReader.Parse(bytes, fileName, defaultCurrency())),
                AlreadyImported = db.HasStatementHash(hash),
            };
            Fill(csv);
            _drafts[csv.Id] = csv;
            return csv;
        }
        var pages = await PdfReader.ReadAsync(bytes, ct).ConfigureAwait(false);

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
        parse = Focus(parse);

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
        draft.Parse = Focus(StatementParser.Parse(draft.Pages, template, defaultCurrency()));
        draft.Template = template;
        Fill(draft);
        return draft;
    }

    /// <summary>Bank chosen in the settings: "bnc" (Banque Nationale) or "auto".</summary>
    public Func<string> BankFocus { get; init; } = () => "auto";

    /// <summary>With a bank in focus, statements that name no bank are taken to be from that bank.</summary>
    private StatementParse Focus(StatementParse p)
    {
        if (BankFocus() == "bnc" && p.Bank is null) p.Bank = "Banque Nationale";
        return p;
    }

    private void Fill(ImportDraft draft)
    {
        var classifier = transactions.NewClassifier();
        draft.Rows = draft.Parse.Rows.Select((r, i) =>
        {
            var c = classifier.Classify(r.Label, r.Amount, r.BankCategory);
            return new DraftRow
            {
                Index = i, Date = r.Date, Label = r.Label, Amount = r.Amount, Balance = r.Balance, MerchantKey = c.MerchantKey, BankCategory = r.BankCategory,
                Brand = c.Brand, BrandSource = c.BrandSource, CategoryId = c.CategoryId, CategorySource = c.CategorySource,
                Page = r.Page, Top = r.Top,
            };
        }).ToList();

        var accounts = db.Accounts();
        var p = draft.Parse;
        var match = accounts.FirstOrDefault(a => p.Last4 is not null && a.Last4 == p.Last4 && a.Kind == p.Kind && a.Currency == p.Currency && (a.Bank is null || p.Bank is null || a.Bank == p.Bank))
                    ?? (accounts.Count(a => a.Bank is not null && a.Bank == p.Bank && a.Kind == p.Kind && a.Currency == p.Currency) == 1
                        ? accounts.First(a => a.Bank == p.Bank && a.Kind == p.Kind && a.Currency == p.Currency)
                        : null);
        draft.SuggestedAccountId = match?.Id;
        MarkDuplicates(draft, match?.Id);
    }

    /// <summary>
    /// Marks rows already stored for this account (overlapping statements, or the PDF and the CSV of the same month,
    /// whose labels are written differently). Rows are matched one for one on date and amount: two identical
    /// coffees the same day are two operations.
    /// </summary>
    private void MarkDuplicates(ImportDraft draft, long? accountId)
    {
        foreach (var r in draft.Rows) r.Duplicate = false;
        if (accountId is null || draft.Rows.Count == 0) return;
        var from = draft.Rows.Min(r => r.Date);
        var to = draft.Rows.Max(r => r.Date);
        var existing = db.TransactionsForAccount(accountId.Value, from, to)
            .GroupBy(t => (t.Date, t.Amount))
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var r in draft.Rows)
        {
            var key = (r.Date, r.Amount);
            if (existing.TryGetValue(key, out var n) && n > 0)
            {
                r.Duplicate = true;
                existing[key] = n - 1;
            }
        }
    }

    public void SetAccount(Guid id, long? accountId)
    {
        var draft = _drafts.GetValueOrDefault(id) ?? throw new KeyNotFoundException("This import is no longer open.");
        MarkDuplicates(draft, accountId);
    }

    public CommitResult Commit(CommitRequest request)
    {
        var draft = _drafts.GetValueOrDefault(request.DraftId) ?? throw new KeyNotFoundException("This import is no longer open.");
        if (draft.AlreadyImported) throw new InvalidOperationException("This statement was already imported.");
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
            accountId = request.AccountId ?? db.AddAccount(new Account
            {
                Name = string.IsNullOrWhiteSpace(request.NewAccountName) ? DefaultAccountName(draft.Parse) : request.NewAccountName.Trim(),
                Currency = request.Currency,
                Kind = request.Kind,
                Bank = draft.Parse.Bank,
                Last4 = draft.Parse.Last4,
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
                    AccountId = accountId, StatementId = statementId, Date = r.Date, Amount = r.Amount, Currency = request.Currency,
                    RawLabel = r.Label, MerchantKey = r.MerchantKey, Brand = r.Brand, BrandSource = r.BrandSource, BankCategory = r.BankCategory,
                    CategoryId = r.CategoryId, CategorySource = r.CategorySource,
                };
                if (edits.TryGetValue(r.Index, out var e))
                {
                    if (e.Brand is { } b && b.Trim().Length > 0 && b.Trim() != r.Brand) { t.Brand = b.Trim(); t.BrandSource = ValueSource.User; }
                    if (e.CategoryId is { } c && c != r.CategoryId) { t.CategoryId = c; t.CategorySource = ValueSource.User; }
                    if (t.BrandSource == ValueSource.User || t.CategorySource == ValueSource.User)
                        learned.Add((t.MerchantKey, t.Brand, t.CategorySource == ValueSource.User ? t.CategoryId : null));
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
        if (learned.Count > 0) transactions.ReapplyRules();
        else TransferMatcher.Link(db, transactions.System);
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
