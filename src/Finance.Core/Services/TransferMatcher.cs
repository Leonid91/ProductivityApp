using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Services;

/// <summary>
/// Finds the two halves of a movement between the person's own accounts (paying the credit card from chequing,
/// moving money to savings...) so that neither half is counted as spending or income.
/// </summary>
public static class TransferMatcher
{
    /// <summary>Days allowed between the two halves (banks post them on different days).</summary>
    public const int Window = 4;

    public static int Link(Database db, BankingSystem system)
    {
        var accounts = db.Accounts().ToDictionary(a => a.Id);
        if (accounts.Count < 2) return 0;
        var txs = db.AllTransactions().Where(t => t.CategorySource != ValueSource.User).ToList();
        var outgoing = txs.Where(t => t.Amount < 0).OrderBy(t => t.Date).ToList();
        var incoming = txs.Where(t => t.Amount > 0).GroupBy(t => (t.Amount, t.Currency)).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Date).ToList());
        var used = new HashSet<long>();
        var changed = 0;

        db.InTransaction(() =>
        {
            foreach (var o in outgoing)
            {
                if (used.Contains(o.Id) || !incoming.TryGetValue((-o.Amount, o.Currency), out var list)) continue;
                Transaction? best = null;
                var bestGap = int.MaxValue;
                foreach (var i in list)
                {
                    if (used.Contains(i.Id) || i.AccountId == o.AccountId) continue;
                    var gap = Math.Abs(i.Date.DayNumber - o.Date.DayNumber);
                    if (gap > Window || gap >= bestGap) continue;
                    if (!Related(o, i, accounts, system)) continue;
                    best = i;
                    bestGap = gap;
                }
                if (best is null) continue;
                used.Add(o.Id);
                used.Add(best.Id);
                var category = o.CategoryId == Categories.Investments || best.CategoryId == Categories.Investments ? Categories.Investments : Categories.Transfers;
                foreach (var t in new[] { o, best })
                {
                    if (t.CategoryId == category && t.CategorySource == ValueSource.Linked) continue;
                    t.CategoryId = category;
                    t.CategorySource = ValueSource.Linked;
                    db.UpdateTransaction(t);
                    changed++;
                }
            }
        });
        return changed;
    }

    /// <summary>
    /// Two opposite amounts on two accounts are one movement when a credit card or a savings account is involved
    /// (North America), or a savings account (France), or when the wording already says it is a transfer.
    /// </summary>
    private static bool Related(Transaction o, Transaction i, Dictionary<long, Account> accounts, BankingSystem system)
    {
        var a = accounts.GetValueOrDefault(o.AccountId)?.Kind;
        var b = accounts.GetValueOrDefault(i.AccountId)?.Kind;
        if (IsMovement(o) || IsMovement(i)) return true;
        if (a == AccountKind.Savings || b == AccountKind.Savings) return true;
        // Paying the card: money leaves chequing and arrives on the card. (In France a deferred-debit card is settled
        // the same way, once a month.)
        if (b == AccountKind.CreditCard && a != AccountKind.CreditCard) return true;
        return system == BankingSystem.NorthAmerica && a == AccountKind.CreditCard && b != AccountKind.CreditCard && o.RawLabel.Length > 0;
    }

    private static bool IsMovement(Transaction t) => Categories.Get(t.CategoryId).Kind == CategoryKind.Transfer;
}
