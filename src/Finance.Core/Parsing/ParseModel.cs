using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Parsing;

public enum ColumnRole { Ignore = 0, Date = 1, ValueDate = 2, Description = 3, Debit = 4, Credit = 5, Amount = 6, Balance = 7, Category = 8 }

public enum StatementKindHint { Auto = 0, Bank = 1, CreditCard = 2 }

public enum VerificationStatus { Verified = 0, Mismatch = 1, NoBalances = 2, NoTransactions = 3 }

/// <summary>One column of a bank template, as fractions (0..1) of the page width.</summary>
public sealed class TemplateColumn
{
    public ColumnRole Role { get; set; }
    public double Left { get; set; }
    public double Right { get; set; }
}

/// <summary>
/// Column layout set once by hand for a bank whose statements the automatic reader cannot follow.
/// It is applied automatically to every later statement whose first page contains <see cref="MatchText"/>.
/// </summary>
public sealed class BankTemplate
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string MatchText { get; set; } = "";
    public List<TemplateColumn> Columns { get; set; } = [];
    public DateOrder DateOrder { get; set; }
    public StatementKindHint Kind { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ParsedRow
{
    public DateOnly Date { get; set; }
    public string Label { get; set; } = "";
    /// <summary>Signed: negative is money out.</summary>
    public decimal Amount { get; set; }
    public decimal? Balance { get; set; }
    /// <summary>Category printed or exported by the bank, if the statement has one.</summary>
    public string BankCategory { get; set; } = "";
    public int Page { get; set; }
    public double Top { get; set; }
}

public sealed class StatementParse
{
    public List<ParsedRow> Rows { get; set; } = [];
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }
    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }
    public string Currency { get; set; } = "CAD";
    public string Language { get; set; } = "en";
    public AccountKind Kind { get; set; }
    public string? Bank { get; set; }
    public string? Last4 { get; set; }
    public VerificationStatus Status { get; set; }
    /// <summary>Printed closing balance minus the closing balance computed from the rows.</summary>
    public decimal? Difference { get; set; }
    public decimal? ComputedClosing { get; set; }
    /// <summary>Rows whose printed running balance disagrees with the computed one.</summary>
    public int RowBalanceMismatches { get; set; }
    public int RowBalanceChecks { get; set; }
    public bool UsedOcr { get; set; }
    public string Method { get; set; } = "";
    public DateOrder DateOrder { get; set; }
    /// <summary>Columns found on the first table page, as page-width fractions, to start the template editor from.</summary>
    public List<TemplateColumn> Columns { get; set; } = [];
    public string FirstPageText { get; set; } = "";
    /// <summary>Known bank layout that was recognised ("bnc"), or null.</summary>
    public string? Profile { get; set; }
    /// <summary>The statement lists the newest operation first (online history exports).</summary>
    public bool NewestFirst { get; set; }
    /// <summary>Lines with a zero amount (waived fees...) that were left out.</summary>
    public int ZeroRowsSkipped { get; set; }

    public bool Verified => Status == VerificationStatus.Verified;
}
