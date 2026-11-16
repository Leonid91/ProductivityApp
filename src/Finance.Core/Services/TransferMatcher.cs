using System.Text;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

/// <summary>
/// Finds the two halves of a movement between the person's own accounts (paying the credit card from chequing,
/// moving money to savings, sending euros from a French bank to a Canadian one...) so that neither half is counted as
/// spending or income.
/// </summary>
/// <remarks>
/// Halves in one currency have the same amount. Halves in two currencies (an international transfer) differ by the
/// rate and fees of the banks; two ways of finding them are tried every time the movements are linked, and the one
/// pairing the most operations is kept:
/// <list type="bullet">
/// <item>the general way: the amount received is the amount sent at the rate of its day, within
/// <see cref="CurrencyTolerance"/>, up to <see cref="Window"/> days apart, worded as a transfer on one side at least;</item>
/// <item>the way of the transfer services first, then the general way for the rest: a half naming the service (or in
/// an account held there) pairs with the other half up to <see cref="ServiceWindow"/> days apart, and the exchanges
/// between the currencies of an account held at the service (a balance in CAD to a balance in EUR) are paired as
/// movements. A transfer service is one of <see cref="Services"/>, or any bank of the person holding money in several
/// currencies when the wording of an operation names it.</item>
/// </list>
/// </remarks>
public static class TransferMatcher
{
    /// <summary>Days allowed between the two halves (banks post them on different days).</summary>
    public const int Window = 4;

    /// <summary>Days allowed between the two halves of a transfer through a transfer service (it may hold the money a few days).</summary>
    public const int ServiceWindow = 6;

    /// <summary>
    /// How far the amount received in another currency may be from the amount sent converted at the rate of its day: the
    /// banks' own exchange rate and fees (a few percent).
    /// </summary>
    public const decimal CurrencyTolerance = 0.04m;

    /// <summary>
    /// The transfer services known by name, recognised even when the person has no account there (in the wording of an
    /// operation, or as the bank of an account); any other bank holding several currencies is one too (see the remarks).
    /// </summary>
    public static readonly (string Name, string[] Words)[] Services =
    [
        ("Wise", ["WISE", "TRANSFERWISE"]),
        ("Revolut", ["REVOLUT"]),
    ];

    /// <summary>Converts an amount from one currency to another at the rate of a day (null when no rate is known).</summary>
    public delegate decimal? Converter(decimal amount, string from, string to, DateOnly day);

    /// <param name="convert">
    /// Pairs the halves of a movement between accounts in two currencies (the amount received is the amount sent
    /// converted, within <see cref="CurrencyTolerance"/>); without it only halves in the same currency are paired.
    /// </param>
    public static int Link(Database db, Converter? convert = null)
    {
        var accounts = db.Accounts().ToDictionary(a => a.Id);
        var changed = CardSpendingWithoutCard(db, accounts);
        if (accounts.Count < 2)
        {
            WriteLinks(db, []);
            return changed;
        }
        var bankNames = db.Banks().ToDictionary(b => b.Id, b => b.Name);
        // The person's banks holding money in several currencies are transfer services too, whatever their name, when a
        // wording names them (an ordinary bank with a second currency is not one for every operation of its accounts).
        var known = Known.ToList();
        foreach (var b in accounts.Values.Where(a => a.BankId is not null).GroupBy(a => a.BankId!.Value))
            if (b.Select(a => a.Currency).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 && bankNames.GetValueOrDefault(b.Key) is { } name
                && Words(name) is { Length: > 2 } padded && Service(name, known) is null)
                known.Add((name, [padded]));
        // The same wordings come back on many operations, and every operation of an account has its bank: each one is
        // looked up once.
        var seen = new Dictionary<(string, string), string?>();
        var held = accounts.Values.ToDictionary(a => a.Id, a => a.BankId is { } b && bankNames.GetValueOrDefault(b) is { } n ? Service(n, Known) : null);
        string? ServiceOf(Transaction t)
        {
            if (!seen.TryGetValue((t.RawLabel, t.Brand), out var s)) seen[(t.RawLabel, t.Brand)] = s = Service(t.RawLabel + " " + t.Brand, known);
            return s ?? held.GetValueOrDefault(t.AccountId);
        }
        // A movement the person marked by hand still pairs with its other half (to show where the money went);
        // anything else they categorised by hand stays out of the pairing.
        var txs = db.AllTransactions().Where(t => t.CategorySource != ValueSource.User || IsMovement(t)).ToList();
        var service = txs.ToDictionary(t => t.Id, ServiceOf);
        var outgoing = txs.Where(t => t.Amount < 0).OrderBy(t => t.Date).ThenBy(t => t.Id).ToList();
        var incoming = txs.Where(t => t.Amount > 0).GroupBy(t => (t.Amount, t.Currency)).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Date).ToList());
        var used = new HashSet<long>();
        var pairs = new List<(Transaction Out, Transaction In)>();

        foreach (var o in outgoing)
        {
            if (used.Contains(o.Id) || !incoming.TryGetValue((-o.Amount, o.Currency), out var list)) continue;
            Transaction? best = null;
            var bestGap = int.MaxValue;
            var window = service[o.Id] is not null ? ServiceWindow : Window;
            foreach (var i in list)
            {
                if (used.Contains(i.Id) || i.AccountId == o.AccountId) continue;
                var days = Math.Abs(i.Date.DayNumber - o.Date.DayNumber);
                if (days > Math.Max(window, service[i.Id] is not null ? ServiceWindow : Window)) continue;
                // Same amount on two candidates: the one whose wording agrees wins (a card payment goes to the card,
                // a transfer between accounts to the other account).
                var cardSide = accounts.GetValueOrDefault(i.AccountId)?.Kind == AccountKind.CreditCard;
                var cardWording = o.CategoryId == Categories.CardPayments;
                var gap = days * 10 + (cardSide == cardWording ? 0 : 5);
                if (gap >= bestGap) continue;
                if (!Related(o, i, accounts, service)) continue;
                best = i;
                bestGap = gap;
            }
            if (best is null) continue;
            used.Add(o.Id);
            used.Add(best.Id);
            pairs.Add((o, best));
        }

        // Then the halves in two currencies, among what is left: each way tried on its own, the one pairing the most kept.
        if (convert is not null && txs.Select(t => t.Currency).Distinct().Skip(1).Any())
        {
            var general = Across(outgoing, txs, used, convert, accounts, service, services: false);
            var withServices = Across(outgoing, txs, used, convert, accounts, service, services: true);
            var kept = withServices.Count > general.Count ? withServices : general;
            pairs.AddRange(kept);
        }

        db.InTransaction(() =>
        {
            foreach (var (o, i) in pairs) Pair(o, i);
            WriteLinks(db, pairs);
        });
        return changed;

        void Pair(Transaction o, Transaction best)
        {
            var cardInvolved = accounts[o.AccountId].Kind == AccountKind.CreditCard || accounts[best.AccountId].Kind == AccountKind.CreditCard
                               || o.CategoryId == Categories.CardPayments || best.CategoryId == Categories.CardPayments;
            var category = cardInvolved ? Categories.CardPayments
                : o.CategoryId == Categories.Investments || best.CategoryId == Categories.Investments ? Categories.Investments
                : Categories.Transfers;
            foreach (var t in new[] { o, best })
            {
                if (t.CategorySource == ValueSource.User) continue;
                if (t.CategoryId == category && t.CategorySource == ValueSource.Linked) continue;
                t.CategoryId = category;
                t.CategorySource = ValueSource.Linked;
                db.UpdateTransaction(t);
                changed++;
            }
        }
    }

    /// <summary>
    /// The halves in two currencies found one way (see the remarks of the class), among what <paramref name="taken"/>
    /// leaves; nothing is written.
    /// </summary>
    private static List<(Transaction Out, Transaction In)> Across(List<Transaction> outgoing, List<Transaction> txs, HashSet<long> taken, Converter convert,
        Dictionary<long, Account> accounts, Dictionary<long, string?> service, bool services)
    {
        var used = new HashSet<long>(taken);
        var pairs = new List<(Transaction, Transaction)>();
        // What is left, by day: only the days around each outgoing half are looked at.
        var left = txs.Where(t => t.Amount > 0 && !used.Contains(t.Id)).GroupBy(t => t.Date.DayNumber).ToDictionary(g => g.Key, g => g.ToList());
        // With the services: their halves first (wider window, their own wording), then the general way for the rest.
        foreach (var round in services ? new[] { true, false } : [false])
        {
            foreach (var o in outgoing)
            {
                if (used.Contains(o.Id)) continue;
                Transaction? best = null;
                var bestScore = decimal.MaxValue;
                var expectedIn = new Dictionary<string, decimal?>(StringComparer.Ordinal);
                var window = round ? ServiceWindow : Window;
                for (var day = o.Date.DayNumber - window; day <= o.Date.DayNumber + window; day++)
                {
                    foreach (var i in left.GetValueOrDefault(day) ?? [])
                    {
                        if (used.Contains(i.Id) || i.AccountId == o.AccountId || i.Currency == o.Currency) continue;
                        if (round ? !ThroughService(o, i, accounts, service) : !RelatedAcrossCurrencies(o, i, accounts, service)) continue;
                        if (!expectedIn.TryGetValue(i.Currency, out var e)) expectedIn[i.Currency] = e = convert(-o.Amount, o.Currency, i.Currency, o.Date);
                        if (e is not { } expected || expected <= 0) continue;
                        var gap = Math.Abs(i.Amount - expected) / expected;
                        if (gap > CurrencyTolerance) continue;
                        var score = gap * 100 + Math.Abs(day - o.Date.DayNumber);
                        if (score >= bestScore) continue;
                        best = i;
                        bestScore = score;
                    }
                }
                if (best is null) continue;
                used.Add(o.Id);
                used.Add(best.Id);
                pairs.Add((o, best));
            }
        }
        return pairs;
    }

    /// <summary>The transfer service an operation names (or whose name a bank has), or null.</summary>
    public static string? Service(string text) => Service(text, Known);

    private static string? Service(string text, IEnumerable<(string Name, string[] Padded)> services)
    {
        var padded = Words(text);
        foreach (var (name, keys) in services)
            foreach (var k in keys)
                if (padded.Contains(k, StringComparison.Ordinal)) return name;
        return null;
    }

    /// <summary>The known services, each word between spaces (as <see cref="Words"/> writes a text).</summary>
    private static readonly (string Name, string[] Padded)[] Known = [.. Services.Select(s => (s.Name, s.Words.Select(w => " " + w + " ").ToArray()))];

    /// <summary>A text in capitals, every sign a space, with a space at both ends: words are found whole.</summary>
    private static string Words(string text)
    {
        var words = new StringBuilder(text.Length + 2).Append(' ');
        foreach (var c in text) words.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ');
        return words.Append(' ').ToString();
    }

    /// <summary>
    /// Two halves through a transfer service: one of them names it (or is in an account held there), they do not name two
    /// different services, and neither is a purchase or a salary.
    /// </summary>
    private static bool ThroughService(Transaction o, Transaction i, Dictionary<long, Account> accounts, Dictionary<long, string?> service)
    {
        var (a, b) = (service[o.Id], service[i.Id]);
        if (a is null && b is null) return false;
        if (a is not null && b is not null && a != b) return false;
        if (accounts.GetValueOrDefault(o.AccountId)?.Kind == AccountKind.CreditCard && !IsMovement(o)) return false;
        if (accounts.GetValueOrDefault(i.AccountId)?.Kind == AccountKind.CreditCard && !IsMovement(i)) return false;
        return (a is not null || !IsSpendingOrIncome(o)) && (b is not null || !IsSpendingOrIncome(i));
    }

    /// <summary>
    /// Two amounts in two currencies are one movement only when the money is said to move: at least one side worded as a
    /// transfer (or card payment, investment), and neither side a purchase, a salary or another known spending or income.
    /// </summary>
    private static bool RelatedAcrossCurrencies(Transaction o, Transaction i, Dictionary<long, Account> accounts, Dictionary<long, string?> service) =>
        (IsMovement(o) || IsMovement(i)) && !IsSpendingOrIncome(o) && !IsSpendingOrIncome(i) && Related(o, i, accounts, service);

    /// <summary>
    /// Two opposite amounts on two accounts are one movement when a credit card or a savings account is involved
    /// (North America), or a savings account (France), or when the wording already says it is a transfer, or names a
    /// transfer service (money put on or taken from a Wise or Revolut account).
    /// </summary>
    private static bool Related(Transaction o, Transaction i, Dictionary<long, Account> accounts, Dictionary<long, string?> service)
    {
        var a = accounts.GetValueOrDefault(o.AccountId)?.Kind;
        var b = accounts.GetValueOrDefault(i.AccountId)?.Kind;
        // A purchase or a salary is never half of a transfer, whatever its amount: at least one side must be worded as
        // a movement (card payment, transfer, investment), except between savings and another own account.
        if (a == AccountKind.CreditCard && !IsMovement(o)) return false;
        if (b == AccountKind.CreditCard && !IsMovement(i)) return false;
        if (IsMovement(o) || IsMovement(i)) return true;
        if (service[o.Id] is not null || service[i.Id] is not null) return ThroughService(o, i, accounts, service);
        return (a == AccountKind.Savings || b == AccountKind.Savings) && !IsSpendingOrIncome(o) && !IsSpendingOrIncome(i);
    }

    private static bool IsMovement(Transaction t) => Categories.Get(t.CategoryId).Kind == CategoryKind.Transfer;

    /// <summary>Recognised spending or income (a shop, a salary...), as opposed to an unknown "other" line.</summary>
    private static bool IsSpendingOrIncome(Transaction t) => t.CategoryId is not (Categories.OtherExpense or Categories.OtherIncome) && !IsMovement(t);

    /// <summary>The pairings found replace the previous ones: only the operations whose other half changed are written.</summary>
    private static void WriteLinks(Database db, List<(Transaction Out, Transaction In)> pairs)
    {
        var other = new Dictionary<long, long>(pairs.Count * 2);
        foreach (var (o, i) in pairs) { other[o.Id] = i.Id; other[i.Id] = o.Id; }
        db.InTransaction(() =>
        {
            foreach (var t in db.AllTransactions())
            {
                long? now = other.TryGetValue(t.Id, out var x) ? x : null;
                if (t.LinkedId != now) db.SetLinkOf(t.Id, now);
            }
        });
    }

    /// <summary>
    /// With no credit card account imported, paying the card from chequing is the only trace of what was bought with
    /// it: those payments count as "Card statement spending". Once a card account exists they are card payments again.
    /// </summary>
    private static int CardSpendingWithoutCard(Database db, Dictionary<long, Account> accounts)
    {
        var hasCard = accounts.Values.Any(a => a.Kind == AccountKind.CreditCard);
        var changed = 0;
        db.InTransaction(() =>
        {
            foreach (var t in db.AllTransactions().Where(t => t.CategorySource != ValueSource.User && t.Amount < 0))
            {
                var kind = accounts.GetValueOrDefault(t.AccountId)?.Kind;
                if (kind == AccountKind.CreditCard) continue;
                var next = !hasCard && t.CategoryId == Categories.CardPayments ? Categories.CardSpending
                    : hasCard && t.CategoryId == Categories.CardSpending ? Categories.CardPayments
                    : null;
                if (next is null) continue;
                t.CategoryId = next;
                db.UpdateTransaction(t);
                changed++;
            }
        });
        return changed;
    }
}
