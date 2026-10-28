using ProductivityApp.Finance.Classification;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

public sealed record TransactionInput(string Date, decimal Amount, string Brand, string CategoryId, long? AccountId, string? Note);

/// <summary>Manual changes to operations. Every brand or category correction teaches the dictionary.</summary>
public sealed class TransactionService(Database db, Func<string> defaultCurrency, Func<BankingSystem>? bankingSystem = null)
{
    public BankingSystem System => bankingSystem?.Invoke() ?? BankingSystem.NorthAmerica;

    public Classifier NewClassifier() => new(db.BrandRules(), System);

    public const string ManualAccountName = "Cash & manual entries";

    /// <summary>Sets the category of one operation and teaches it for the merchant and the brand. Returns how many other operations followed.</summary>
    public int SetCategory(long id, string categoryId)
    {
        if (!Categories.Exists(categoryId)) throw new ArgumentException($"Unknown category '{categoryId}'.");
        var t = db.GetTransaction(id) ?? throw new KeyNotFoundException("This operation no longer exists.");
        t.CategoryId = categoryId;
        t.CategorySource = ValueSource.User;
        db.InTransaction(() =>
        {
            db.UpdateTransaction(t);
            Learn(t, t.Brand, categoryId);
        });
        return ReapplyRules();
    }

    /// <summary>Renames the brand of one operation and teaches it for the merchant. Returns how many other operations followed.</summary>
    public int SetBrand(long id, string brand)
    {
        brand = brand.Trim();
        if (brand.Length == 0) throw new ArgumentException("The brand name is empty.");
        if (brand.Length > 80) throw new ArgumentException("The brand name is too long.");
        var t = db.GetTransaction(id) ?? throw new KeyNotFoundException("This operation no longer exists.");
        t.Brand = brand;
        t.BrandSource = ValueSource.User;
        db.InTransaction(() =>
        {
            db.UpdateTransaction(t);
            Learn(t, brand, t.CategorySource == ValueSource.User ? t.CategoryId : null);
        });
        // The brand may carry a learned category.
        if (t.CategorySource != ValueSource.User)
        {
            var c = NewClassifier().Classify(t.RawLabel.Length > 0 ? t.RawLabel : brand, t.Amount, t.BankCategory, KindOf(t.AccountId));
            if (c.CategoryId != t.CategoryId) { t.CategoryId = c.CategoryId; t.CategorySource = c.CategorySource; db.UpdateTransaction(t); }
        }
        return ReapplyRules();
    }

    public TransactionView Create(TransactionInput input)
    {
        var t = new Transaction();
        Apply(t, input);
        t.AccountId = input.AccountId ?? ManualAccountId();
        t.Currency = db.Accounts().First(a => a.Id == t.AccountId).Currency;
        t.Id = db.AddTransaction(t);
        return SummaryService.View(t);
    }

    public TransactionView Update(long id, TransactionInput input)
    {
        var t = db.GetTransaction(id) ?? throw new KeyNotFoundException("This operation no longer exists.");
        var brandChanged = input.Brand.Trim() != t.Brand;
        var categoryChanged = input.CategoryId != t.CategoryId;
        Apply(t, input, keepSources: true);
        if (brandChanged) t.BrandSource = ValueSource.User;
        if (categoryChanged) t.CategorySource = ValueSource.User;
        if (input.AccountId is { } a && db.Accounts().Any(x => x.Id == a)) t.AccountId = a;
        db.UpdateTransaction(t);
        if (brandChanged || categoryChanged)
        {
            Learn(t, t.Brand, categoryChanged ? t.CategoryId : null);
            ReapplyRules();
        }
        return SummaryService.View(db.GetTransaction(id)!);
    }

    public void Delete(long id) => db.DeleteTransaction(id);

    /// <summary>
    /// Remembers a correction. A real merchant is learned by its name (and its brand for the category);
    /// generic bank wording ("Paiement de facture", "Transfert entre comptes") only together with its exact amount.
    /// </summary>
    private void Learn(Transaction t, string brand, string? categoryId)
    {
        var label = t.RawLabel.Length > 0 ? t.RawLabel : brand;
        var key = Classifier.LearningKey(label, t.Amount, t.BankCategory, System);
        if (key.Length > 0) db.UpsertKeyRule(key, brand, categoryId);
        if (categoryId is not null && !Classifier.IsGenericLabel(label, t.BankCategory, System)) db.UpsertBrandCategory(brand, categoryId);
    }

    private AccountKind KindOf(long accountId) => db.Accounts().FirstOrDefault(a => a.Id == accountId)?.Kind ?? AccountKind.Bank;

    private static void Apply(Transaction t, TransactionInput input, bool keepSources = false)
    {
        if (!DateOnly.TryParseExact(input.Date, "yyyy-MM-dd", out var date)) throw new ArgumentException("The date is not valid.");
        if (input.Amount == 0) throw new ArgumentException("The amount cannot be zero.");
        if (Math.Abs(input.Amount) > 1_000_000_000m) throw new ArgumentException("The amount is too large.");
        if (!Categories.Exists(input.CategoryId)) throw new ArgumentException($"Unknown category '{input.CategoryId}'.");
        var brand = input.Brand.Trim();
        if (brand.Length == 0) throw new ArgumentException("The brand name is empty.");
        t.Date = date;
        t.Amount = decimal.Round(input.Amount, 2, MidpointRounding.AwayFromZero);
        t.Brand = brand;
        t.CategoryId = input.CategoryId;
        t.Note = input.Note?.Trim() ?? "";
        if (!keepSources)
        {
            t.BrandSource = ValueSource.User;
            t.CategorySource = ValueSource.User;
            t.MerchantKey = Classifier.MerchantKey(Classifier.Normalize(brand));
        }
    }

    private long ManualAccountId()
    {
        var existing = db.Accounts().FirstOrDefault(a => a.Name == ManualAccountName);
        return existing?.Id ?? db.AddAccount(new Account { Name = ManualAccountName, Currency = defaultCurrency(), Kind = AccountKind.Bank });
    }

    /// <summary>
    /// Re-runs the dictionary over every operation the person has not corrected by hand, then links the movements
    /// between their own accounts again. Returns how many operations ended up different.
    /// </summary>
    public int ReapplyRules()
    {
        var before = db.AllTransactions().ToDictionary(t => t.Id, t => (t.Brand, t.CategoryId));
        var classifier = NewClassifier();
        var kinds = db.Accounts().ToDictionary(a => a.Id, a => a.Kind);
        db.InTransaction(() =>
        {
            foreach (var t in db.AllTransactions())
            {
                if (t.BrandSource == ValueSource.User && t.CategorySource == ValueSource.User) continue;
                var c = classifier.Classify(t.RawLabel.Length > 0 ? t.RawLabel : t.Brand, t.Amount, t.BankCategory, kinds.GetValueOrDefault(t.AccountId));
                var dirty = false;
                if (t.BrandSource != ValueSource.User && t.RawLabel.Length > 0 && c.Brand != t.Brand) { t.Brand = c.Brand; t.BrandSource = c.BrandSource; dirty = true; }
                if (t.CategorySource != ValueSource.User && (c.CategoryId != t.CategoryId || t.CategorySource == ValueSource.Linked)) { t.CategoryId = c.CategoryId; t.CategorySource = c.CategorySource; dirty = true; }
                if (dirty) db.UpdateTransaction(t);
            }
        });
        TransferMatcher.Link(db, System);
        return db.AllTransactions().Count(t => before.TryGetValue(t.Id, out var b) && (b.Brand != t.Brand || b.CategoryId != t.CategoryId));
    }
}
