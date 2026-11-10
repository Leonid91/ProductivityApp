using ProductivityApp.Finance.Classification;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

/// <param name="Earned">For money back from an investment: the part that was gain, counted as income (null: none given).</param>
public sealed record TransactionInput(string Date, decimal Amount, string Brand, string CategoryId, long? AccountId, string? Note, decimal? Earned = null);

/// <summary>Manual changes to operations. Every brand or category correction teaches the dictionary.</summary>
public sealed class TransactionService(Database db, Func<string> defaultCurrency)
{
    /// <summary>Operations are read with the banking system of their account's bank.</summary>
    public Classifier NewClassifier(BankingSystem system = BankingSystem.NorthAmerica) => new(db.BrandRules(), system);

    private BankingSystem SystemOf(long accountId) => db.Accounts().FirstOrDefault(a => a.Id == accountId)?.System ?? BankingSystem.NorthAmerica;


    /// <summary>
    /// Sets the category of one operation. With <paramref name="allOfBrand"/>, every operation of the same brand, past and
    /// future, gets it too (the brand is learned); otherwise only this one changes and nothing is learned.
    /// Returns how many other operations changed.
    /// </summary>
    public int SetCategory(long id, string categoryId, bool allOfBrand = true)
    {
        if (!Categories.Exists(categoryId)) throw new ArgumentException($"Unknown category '{categoryId}'.");
        var t = db.GetTransaction(id) ?? throw new KeyNotFoundException("This operation no longer exists.");
        var before = db.AllTransactions().ToDictionary(x => x.Id, x => x.CategoryId);
        t.CategoryId = categoryId;
        t.CategorySource = ValueSource.User;
        db.InTransaction(() =>
        {
            db.UpdateTransaction(t);
            if (allOfBrand) ApplyToBrand(t, categoryId);
        });
        ReapplyRules();
        return db.AllTransactions().Count(x => x.Id != id && before.TryGetValue(x.Id, out var c) && c != x.CategoryId);
    }

    /// <summary>Every operation of the brand takes the category, and new ones will too.</summary>
    private void ApplyToBrand(Transaction t, string categoryId)
    {
        var label = t.RawLabel.Length > 0 ? t.RawLabel : t.Brand;
        var key = Classifier.LearningKey(label, t.Amount, t.BankCategory, SystemOf(t.AccountId));
        if (key.Length > 0) db.UpsertKeyRule(key, t.Brand, categoryId);
        db.UpsertBrandCategory(t.Brand, categoryId);
        db.SetCategoryOf(db.AllTransactions()
            .Where(x => x.Id != t.Id && string.Equals(x.Brand, t.Brand, StringComparison.OrdinalIgnoreCase)
                && !(x.CategoryId == categoryId && x.CategorySource == ValueSource.User))
            .Select(x => x.Id).ToList(), categoryId, ValueSource.User);
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
            var c = NewClassifier(SystemOf(t.AccountId)).Classify(t.RawLabel.Length > 0 ? t.RawLabel : brand, t.Amount, t.BankCategory, KindOf(t.AccountId));
            if (c.CategoryId != t.CategoryId) { t.CategoryId = c.CategoryId; t.CategorySource = c.CategorySource; db.UpdateTransaction(t); }
        }
        return ReapplyRules();
    }

    public TransactionView Create(TransactionInput input)
    {
        var t = new Transaction();
        Apply(t, input);
        // Every operation is in an account, and every account is at a bank.
        t.AccountId = input.AccountId is { } id && db.Accounts().Any(a => a.Id == id) ? id : throw new ArgumentException("Choose the account of the operation.");
        t.Currency = db.Accounts().First(a => a.Id == t.AccountId).Currency;
        t.Id = db.AddTransaction(t);
        return SummaryService.View(t, db.TagIndex());
    }

    /// <summary>Saves the edits of one operation. A new category goes to the whole brand only with <paramref name="allOfBrand"/>.</summary>
    public TransactionView Update(long id, TransactionInput input, bool allOfBrand = true)
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
            db.InTransaction(() =>
            {
                if (brandChanged) Learn(t, t.Brand, null);
                if (categoryChanged && allOfBrand) ApplyToBrand(t, t.CategoryId);
            });
            ReapplyRules();
        }
        return SummaryService.View(db.GetTransaction(id)!, db.TagIndex());
    }

    public void Delete(long id) => db.DeleteTransaction(id);

    /// <summary>
    /// The category of a brand, set from the brand list: every operation of the brand and every wording learned for it
    /// take it. Returns how many operations changed.
    /// </summary>
    /// <summary>
    /// Renames a brand everywhere: every operation of it and every wording learned for it take the new name (kept as
    /// the person's choice). Two brands renamed to one become one. Says how many operations changed.
    /// </summary>
    public int RenameBrand(string from, string to)
    {
        to = to.Trim();
        if (to.Length == 0) throw new ArgumentException("The brand name is empty.");
        if (to.Length > 80) throw new ArgumentException("The brand name is too long.");
        if (to == from) return 0;
        var txs = db.AllTransactions().Where(x => string.Equals(x.Brand, from, StringComparison.OrdinalIgnoreCase)).ToList();
        db.InTransaction(() =>
        {
            foreach (var t in txs)
            {
                t.Brand = to;
                t.BrandSource = ValueSource.User;
                db.UpdateTransaction(t);
            }
            db.RenameBrandRules(from, to);
        });
        ReapplyRules();
        return txs.Count;
    }

    public int SetBrandCategory(string brand, string categoryId)
    {
        if (!Categories.Exists(categoryId)) throw new ArgumentException($"Unknown category '{categoryId}'.");
        var before = db.AllTransactions().ToDictionary(x => x.Id, x => x.CategoryId);
        db.InTransaction(() =>
        {
            db.UpsertBrandCategory(brand, categoryId);
            db.SetBrandRulesCategory(brand, categoryId);
            db.SetCategoryOf(db.AllTransactions()
                .Where(x => string.Equals(x.Brand, brand, StringComparison.OrdinalIgnoreCase) && x.CategoryId != categoryId)
                .Select(x => x.Id).ToList(), categoryId, ValueSource.User);
        });
        ReapplyRules();
        return db.AllTransactions().Count(x => before.TryGetValue(x.Id, out var c) && c != x.CategoryId);
    }

    /// <summary>
    /// Remembers a correction. A real merchant is learned by its name (and its brand for the category);
    /// generic bank wording ("Paiement de facture", "Transfert entre comptes") only together with its exact amount.
    /// </summary>
    private void Learn(Transaction t, string brand, string? categoryId)
    {
        var label = t.RawLabel.Length > 0 ? t.RawLabel : brand;
        var system = SystemOf(t.AccountId);
        var key = Classifier.LearningKey(label, t.Amount, t.BankCategory, system);
        if (key.Length > 0) db.UpsertKeyRule(key, brand, categoryId);
        if (categoryId is not null && !Classifier.IsGenericLabel(label, t.BankCategory, system)) db.UpsertBrandCategory(brand, categoryId);
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
        // Only money coming back from a movement (an investment closed, a transfer in) has an earned part.
        if (input.Earned is { } earned && earned != 0)
        {
            if (t.Amount < 0 || !Categories.IsTransfer(t.CategoryId)) throw new ArgumentException("Only money coming back from an investment or a transfer can have an earned part.");
            if (earned < 0 || earned > t.Amount) throw new ArgumentException("The earned part must be between 0 and the amount received.");
            t.Earned = decimal.Round(earned, 2, MidpointRounding.AwayFromZero);
        }
        else t.Earned = null;
        if (!keepSources)
        {
            t.BrandSource = ValueSource.User;
            t.CategorySource = ValueSource.User;
            t.MerchantKey = Classifier.MerchantKey(Classifier.Normalize(brand));
        }
    }

    /// <summary>
    /// Re-runs the dictionary over every operation the person has not corrected by hand, then links the movements
    /// between their own accounts again. Returns how many operations ended up different.
    /// </summary>
    public int ReapplyRules()
    {
        var before = db.AllTransactions().ToDictionary(t => t.Id, t => (t.Brand, t.CategoryId));
        var classifiers = new Dictionary<BankingSystem, Classifier>();
        var accounts = db.Accounts().ToDictionary(a => a.Id);
        db.InTransaction(() =>
        {
            foreach (var t in db.AllTransactions())
            {
                if (t.BrandSource == ValueSource.User && t.CategorySource == ValueSource.User) continue;
                var account = accounts.GetValueOrDefault(t.AccountId);
                var system = account?.System ?? BankingSystem.NorthAmerica;
                if (!classifiers.TryGetValue(system, out var classifier)) classifiers[system] = classifier = NewClassifier(system);
                var c = classifier.Classify(t.RawLabel.Length > 0 ? t.RawLabel : t.Brand, t.Amount, t.BankCategory, account?.Kind ?? AccountKind.Bank);
                var dirty = false;
                if (t.BrandSource != ValueSource.User && t.RawLabel.Length > 0 && c.Brand != t.Brand) { t.Brand = c.Brand; t.BrandSource = c.BrandSource; dirty = true; }
                if (t.CategorySource != ValueSource.User && (c.CategoryId != t.CategoryId || t.CategorySource == ValueSource.Linked)) { t.CategoryId = c.CategoryId; t.CategorySource = c.CategorySource; dirty = true; }
                if (dirty) db.UpdateTransaction(t);
            }
        });
        TransferMatcher.Link(db);
        return db.AllTransactions().Count(t => before.TryGetValue(t.Id, out var b) && (b.Brand != t.Brand || b.CategoryId != t.CategoryId));
    }
}
