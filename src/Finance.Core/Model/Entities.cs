namespace ProductivityApp.Finance.Model;

/// <summary>Where a brand or category value came from. User wins over everything.</summary>
public enum ValueSource
{
    Default = 0, Keyword = 1, BuiltIn = 2, Learned = 3, User = 4,
    /// <summary>The category the bank itself printed or exported.</summary>
    Bank = 5,
    /// <summary>Recognised as the other half of a movement between two of the person's own accounts.</summary>
    Linked = 6,
}

/// <summary>Bank is a chequing / current account (with a debit card).</summary>
public enum AccountKind { Bank = 0, CreditCard = 1, Savings = 2 }

/// <summary>
/// North America: chequing, savings and credit card accounts are separate, and paying the card from chequing is a
/// movement between own accounts. France: purchases with the bank card come out of the current account directly;
/// a deferred-debit card is settled by one monthly debit.
/// </summary>
public enum BankingSystem { NorthAmerica = 0, France = 1 }

/// <summary>Account types named the way the banking system names them (same words as the interface).</summary>
public static class AccountTypes
{
    public static string Label(AccountKind kind, BankingSystem system) => (kind, system) switch
    {
        (AccountKind.CreditCard, BankingSystem.France) => "Deferred debit card",
        (AccountKind.Savings, BankingSystem.France) => "Savings (livret)",
        (AccountKind.Bank, BankingSystem.France) => "Current account",
        (AccountKind.CreditCard, _) => "Credit card",
        (AccountKind.Savings, _) => "Savings account",
        _ => "Chequing account",
    };
}

public sealed class Account
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Currency { get; set; } = "CAD";
    public AccountKind Kind { get; set; }
    public string? Bank { get; set; }
    /// <summary>Last four digits only. The full number is never stored.</summary>
    public string? Last4 { get; set; }
}

public sealed class Transaction
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public long? StatementId { get; set; }
    public DateOnly Date { get; set; }
    /// <summary>Signed: negative is money going out, positive is money coming in.</summary>
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "CAD";
    /// <summary>The label exactly as printed on the statement (empty for manual entries).</summary>
    public string RawLabel { get; set; } = "";
    public string MerchantKey { get; set; } = "";
    public string Brand { get; set; } = "";
    public ValueSource BrandSource { get; set; }
    public string CategoryId { get; set; } = Categories.OtherExpense;
    public ValueSource CategorySource { get; set; }
    public string Note { get; set; } = "";
    /// <summary>Category given by the bank in its export, if any ("Epicerie", "Paiement carte de credit"...).</summary>
    public string BankCategory { get; set; } = "";
    /// <summary>The other half of a movement between two of the person's own accounts, when it was found.</summary>
    public long? LinkedId { get; set; }
}

public sealed class StatementRecord
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string FileName { get; set; } = "";
    public string FileHash { get; set; } = "";
    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }
    public bool Verified { get; set; }
    public string Method { get; set; } = "";
    public DateTime ImportedAt { get; set; }
    public int TransactionCount { get; set; }
}

/// <summary>A learned dictionary entry: a merchant key (or a brand) mapped to a brand and/or a category.</summary>
public sealed class BrandRule
{
    public long Id { get; set; }
    /// <summary>Normalised merchant key, or empty when the rule is a brand-level category override.</summary>
    public string MerchantKey { get; set; } = "";
    public string Brand { get; set; } = "";
    public string? CategoryId { get; set; }
    public DateTime UpdatedAt { get; set; }
}
