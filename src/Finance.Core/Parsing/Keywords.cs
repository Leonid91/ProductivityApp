using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Parsing;

internal enum SummaryKind { None, Opening, Closing, BalanceAsOf, Intermediate, Total }

/// <summary>French and English wording found on bank and card statements. All entries are folded (upper case, no accents).</summary>
internal static class Keywords
{
    public static readonly string[] Opening =
    [
        "SOLDE PRECEDENT", "ANCIEN SOLDE", "SOLDE D'OUVERTURE", "SOLDE DE DEPART", "SOLDE DE DEBUT", "SOLDE INITIAL",
        "SOLDE REPORTE", "SOLDE ANTERIEUR", "SOLDE EN DEBUT DE PERIODE", "SOLDE DU RELEVE PRECEDENT", "SOLDE AU DEBUT",
        "OPENING BALANCE", "PREVIOUS BALANCE", "BALANCE FORWARD", "BEGINNING BALANCE", "STARTING BALANCE",
        "BALANCE BROUGHT FORWARD", "PREVIOUS STATEMENT BALANCE", "BALANCE FROM PREVIOUS STATEMENT", "BALANCE FROM LAST STATEMENT",
    ];

    public static readonly string[] Closing =
    [
        "NOUVEAU SOLDE", "SOLDE DE CLOTURE", "SOLDE FINAL", "SOLDE DE FIN", "SOLDE EN FIN DE PERIODE", "SOLDE A LA FIN",
        "SOLDE DU RELEVE", "SOLDE ACTUEL",
        "CLOSING BALANCE", "NEW BALANCE", "ENDING BALANCE", "FINAL BALANCE", "BALANCE AT END", "STATEMENT BALANCE", "CURRENT BALANCE",
    ];

    public static readonly string[] Intermediate =
    [
        "CARRIED FORWARD", "A REPORTER", "REPORT A NOUVEAU", "SOLDE INTERMEDIAIRE", "SOUS-TOTAL", "SOUS TOTAL", "SUBTOTAL", "SUB-TOTAL", "SUB TOTAL",
        "SOLDE DU JOUR", "DAILY BALANCE", "TOTAL AMOUNT", "TOTAL DEBITS", "TOTAL CREDITS", "NO. OF DEBITS", "NO. OF CREDITS", "NO OF DEBITS", "NO OF CREDITS",
    ];

    public static readonly string[] BalanceAsOf = ["SOLDE AU", "SOLDE LE", "BALANCE AS OF", "BALANCE AS AT", "BALANCE ON"];

    public static readonly string[] Card =
    [
        "CARTE DE CREDIT", "CREDIT CARD", "LIMITE DE CREDIT", "CREDIT LIMIT", "PAIEMENT MINIMUM", "MINIMUM PAYMENT",
        "PAYMENT DUE", "DATE D'ECHEANCE DU PAIEMENT", "PAIEMENT MINIMAL", "CREDIT DISPONIBLE", "AVAILABLE CREDIT", "CARTE A DEBIT DIFFERE",
    ];

    public static readonly string[] Savings =
    [
        "EPARGNE", "SAVINGS", "INTERET ELEVE", "HIGH INTEREST", "HIGH-INTEREST", "CELI", "TFSA", "REER", "RRSP", "LIVRET", "COMPTE A TERME",
        "PLACEMENT", "INVESTMENT ACCOUNT",
    ];

    public static readonly string[] IncomeHints =
    [
        "DEPOT", "DEPOSIT", "VIREMENT RECU", "VIR RECU", "VIR INST RE", "VIR SEPA RECU", "VIREMENT DE ", "REMBOURSEMENT", "REFUND",
        "SALAIRE", "PAYROLL", "SALARY", "PAIE ", "INTERET CREDITE", "INTEREST PAID", "INTEREST EARNED", "REMISE", "RECEIVED", "RECU ",
        "ALLOCATION", "PENSION", "DIVIDEND", "CASHBACK", "PAYMENT - THANK YOU", "PAIEMENT - MERCI", "PAYMENT THANK YOU", "PAIEMENT MERCI",
        "PAIEMENT RECU", "PAYMENT RECEIVED",
    ];

    public static readonly string[] KnownBanks =
    [
        "Desjardins", "Banque Nationale", "National Bank", "RBC", "Royal Bank", "Banque Royale", "TD Canada Trust", "Banque TD", "TD Bank",
        "BMO", "Bank of Montreal", "Banque de Montréal", "Scotiabank", "Banque Scotia", "CIBC", "Tangerine", "Simplii", "EQ Bank",
        "HSBC", "Banque Laurentienne", "Laurentian Bank", "KOHO", "Wealthsimple", "Neo Financial", "ATB", "Coast Capital", "Vancity",
        "BNP Paribas", "Société Générale", "Crédit Agricole", "LCL", "Caisse d'Epargne", "Banque Populaire", "Crédit Mutuel", "CIC",
        "La Banque Postale", "BoursoBank", "Boursorama", "Hello bank", "Fortuneo", "Revolut", "N26", "Monzo", "Barclays", "Lloyds",
        "Chase", "Bank of America", "Wells Fargo", "Citibank", "Capital One", "American Express", "Amex", "PC Financial", "Rogers Bank",
    ];

    /// <summary>
    /// The names of the banks as whole words (with or without their apostrophe), longest first; the card issuers stay
    /// (their name says the account is a card).
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex BankNames = new(
        @"(?<![A-Z0-9])(?:" + string.Join("|", KnownBanks.Where(b => b is not ("American Express" or "Amex")).Select(TextNorm.Fold)
            .SelectMany(b => new[] { b, b.Replace("'", " "), b.Replace("'", "") }).Distinct().OrderByDescending(b => b.Length)
            .Select(System.Text.RegularExpressions.Regex.Escape)) + @")(?![A-Z0-9])",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A folded text without the names of the banks in it: a bank's name says nothing of the account ("Caisse
    /// d'Épargne" is not a savings account, "Crédit Mutuel" not a credit card).
    /// </summary>
    public static string WithoutBankNames(string folded) => BankNames.Replace(folded, " ");

    public static bool ContainsAny(string folded, IEnumerable<string> keys) => keys.Any(k => folded.Contains(k, StringComparison.Ordinal));

    public static SummaryKind ClassifySummary(string folded)
    {
        if (string.IsNullOrWhiteSpace(folded)) return SummaryKind.None;
        if (ContainsAny(folded, Intermediate)) return SummaryKind.Intermediate;
        if (ContainsAny(folded, Opening)) return SummaryKind.Opening;
        if (ContainsAny(folded, Closing)) return SummaryKind.Closing;
        if (ContainsAny(folded, BalanceAsOf)) return SummaryKind.BalanceAsOf;
        if (folded.StartsWith("TOTAL", StringComparison.Ordinal) || folded.StartsWith("SOLDE", StringComparison.Ordinal) || folded.StartsWith("BALANCE", StringComparison.Ordinal))
            return SummaryKind.Total;
        return SummaryKind.None;
    }
}
