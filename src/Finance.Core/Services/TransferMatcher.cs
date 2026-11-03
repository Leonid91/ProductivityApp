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

    public static int Link(Database db)
    {
        var accounts = db.Accounts().ToDictionary(a => a.Id);
        var changed = CardSpendingWithoutCard(db, accounts);
        db.ClearLinks();
        if (accounts.Count < 2) return changed;
        // A movement the person marked by hand still pairs with its other half (to show where the money went);
        // anything else they categorised by hand stays out of the pairing.
        var txs = db.AllTransactions().Where(t => t.CategorySource != ValueSource.User || IsMovement(t)).ToList();
        var outgoing = txs.Where(t => t.Amount < 0).OrderBy(t => t.Date).ToList();
        var incoming = txs.Where(t => t.Amount > 0).GroupBy(t => (t.Amount, t.Currency)).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Date).ToList());
        var used = new HashSet<long>();

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
                    var days = Math.Abs(i.Date.DayNumber - o.Date.DayNumber);
                    if (days > Window) continue;
                    // Same amount on two candidates: the one whose wording agrees wins (a card payment goes to the card,
                    // a transfer between accounts to the other account).
                    var cardSide = accounts.GetValueOrDefault(i.AccountId)?.Kind == AccountKind.CreditCard;
                    var cardWording = o.CategoryId == Categories.CardPayments;
                    var gap = days * 10 + (cardSide == cardWording ? 0 : 5);
                    if (gap >= bestGap) continue;
                    if (!Related(o, i, accounts)) continue;
                    best = i;
                    bestGap = gap;
                }
                if (best is null) continue;
                used.Add(o.Id);
                used.Add(best.Id);
                var cardInvolved = accounts[o.AccountId].Kind == AccountKind.CreditCard || accounts[best.AccountId].Kind == AccountKind.CreditCard
                                   || o.CategoryId == Categories.CardPayments || best.CategoryId == Categories.CardPayments;
                var category = cardInvolved ? Categories.CardPayments
                    : o.CategoryId == Categories.Investments || best.CategoryId == Categories.Investments ? Categories.Investments
                    : Categories.Transfers;
                db.SetLink(o.Id, best.Id);
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
        });
        return changed;
    }

    /// <summary>
    /// Two opposite amounts on two accounts are one movement when a credit card or a savings account is involved
    /// (North America), or a savings account (France), or when the wording already says it is a transfer.
    /// </summary>
    private static bool Related(Transaction o, Transaction i, Dictionary<long, Account> accounts)
    {
        var a = accounts.GetValueOrDefault(o.AccountId)?.Kind;
        var b = accounts.GetValueOrDefault(i.AccountId)?.Kind;
        // A purchase or a salary is never half of a transfer, whatever its amount: at least one side must be worded as
        // a movement (card payment, transfer, investment), except between savings and another own account.
        if (a == AccountKind.CreditCard && !IsMovement(o)) return false;
        if (b == AccountKind.CreditCard && !IsMovement(i)) return false;
        if (IsMovement(o) || IsMovement(i)) return true;
        return (a == AccountKind.Savings || b == AccountKind.Savings) && !IsSpendingOrIncome(o) && !IsSpendingOrIncome(i);
    }

    private static bool IsMovement(Transaction t) => Categories.Get(t.CategoryId).Kind == CategoryKind.Transfer;

    /// <summary>Recognised spending or income (a shop, a salary...), as opposed to an unknown "other" line.</summary>
    private static bool IsSpendingOrIncome(Transaction t) => t.CategoryId is not (Categories.OtherExpense or Categories.OtherIncome) && !IsMovement(t);

    /// <summary>
    /// With no credit card account imported, paying the card from chequing is the only trace of what was bought with
    /// it: those payments count as "Credit card spending". Once a card account exists they are card payments again.
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
