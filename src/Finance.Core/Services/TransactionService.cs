using ProductivityApp.Finance.Classification;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

public sealed record TransactionInput(string Date, decimal Amount, string Brand, string CategoryId, long? AccountId, string? Note);

/// <summary>Manual changes to operations. Every brand or category correction teaches the dictionary.</summary>
public sealed class TransactionService(Database db, Func<string> defaultCurrency)
{
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
            if (t.MerchantKey.Length > 0) db.UpsertKeyRule(t.MerchantKey, t.Brand, categoryId);
            db.UpsertBrandCategory(t.Brand, categoryId);
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
            if (t.MerchantKey.Length > 0) db.UpsertKeyRule(t.MerchantKey, brand, t.CategorySource == ValueSource.User ? t.CategoryId : null);
        });
        // The brand may carry a learned category.
        if (t.CategorySource != ValueSource.User)
        {
            var c = new Classifier(db.BrandRules()).Classify(t.RawLabel.Length > 0 ? t.RawLabel : brand, t.Amount);
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
        if ((brandChanged || categoryChanged) && t.MerchantKey.Length > 0)
        {
            db.UpsertKeyRule(t.MerchantKey, t.Brand, categoryChanged ? t.CategoryId : null);
            if (categoryChanged) db.UpsertBrandCategory(t.Brand, t.CategoryId);
            ReapplyRules();
        }
        return SummaryService.View(db.GetTransaction(id)!);
    }

    public void Delete(long id) => db.DeleteTransaction(id);

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

    /// <summary>Re-runs the dictionary over every operation the person has not corrected by hand. Returns how many changed.</summary>
    public int ReapplyRules()
    {
        var classifier = new Classifier(db.BrandRules());
        var changed = 0;
        db.InTransaction(() =>
        {
            foreach (var t in db.AllTransactions())
            {
                if (t.BrandSource == ValueSource.User && t.CategorySource == ValueSource.User) continue;
                var c = classifier.Classify(t.RawLabel.Length > 0 ? t.RawLabel : t.Brand, t.Amount);
                var dirty = false;
                if (t.BrandSource != ValueSource.User && t.RawLabel.Length > 0 && c.Brand != t.Brand) { t.Brand = c.Brand; t.BrandSource = c.BrandSource; dirty = true; }
                if (t.CategorySource != ValueSource.User && c.CategoryId != t.CategoryId) { t.CategoryId = c.CategoryId; t.CategorySource = c.CategorySource; dirty = true; }
                if (!dirty) continue;
                db.UpdateTransaction(t);
                changed++;
            }
        });
        return changed;
    }
}
