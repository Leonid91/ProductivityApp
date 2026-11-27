using ProductivityApp.Finance.Classification;
using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.Services;

/// <summary>
/// Learns the salary from the history: money one payer sends again and again on a pay cycle (every week, every two
/// weeks, twice a month or every month) at about the same amount is a salary, whatever the bank wrote ("Dépôt direct
/// ACME", "VIR SEPA RECU ACME SAS"). Income named as something else (interest, a benefit, a refund) never is, and a
/// payment of the same payer off the cycle or far from the usual amount (expenses paid back, a one-off) stays other income.
/// </summary>
/// <remarks>
/// Only what the person did not choose is learned: a category they set on an operation, or for a brand (Brands, or an
/// operation saved for all its brand), always wins.
/// </remarks>
public static class SalaryDetector
{
    /// <summary>One operation looked at: <see cref="Index"/> is the caller's own reference to it.</summary>
    public sealed record Line(int Index, DateOnly Date, decimal Amount, string Currency, string Payer);

    /// <summary>The pays needed before a payer is taken for an employer.</summary>
    public const int MinimumPays = 3;

    /// <summary>Pay cycles in days, longest first, with the days a pay may move (a weekend, a holiday, a shorter month).</summary>
    private static readonly (double Days, double Slack)[] Cycles = [(30.44, 5), (15.22, 3.5), (14, 3), (7, 1.5)];

    /// <summary>
    /// The operation may be a salary: money coming in, not between the person's own accounts, that the app (not the
    /// person) filed as a salary, as other income, or as a transfer received from a company.
    /// </summary>
    public static bool Candidate(decimal amount, string categoryId, ValueSource source, bool linked, string rawLabel, string bankCategory, BankingSystem system)
    {
        if (amount <= 0 || linked || source is ValueSource.User or ValueSource.Learned or ValueSource.Linked) return false;
        if (categoryId == "salary") return true;
        if (Classifier.NamesOtherIncome(rawLabel, bankCategory)) return false;
        if (categoryId == Categories.OtherIncome) return true;
        return categoryId == Categories.Transfers && Classifier.NamesCompany(rawLabel)
            && Classifier.MovementOf(" " + Classifier.Normalize(rawLabel) + " ", bankCategory, system) == Movement.MoneyTransfer;
    }

    /// <summary>The <see cref="Line.Index"/> of every line that is a salary.</summary>
    public static HashSet<int> Find(IEnumerable<Line> lines)
    {
        var found = new HashSet<int>();
        foreach (var payer in lines.Where(l => l.Amount > 0 && l.Payer.Length > 0).GroupBy(l => (l.Payer, l.Currency)))
        {
            var all = payer.ToList();
            if (all.Count < MinimumPays) continue;
            var usualAmount = Median(all.Select(l => l.Amount).ToList());
            var usual = all.Where(l => l.Amount >= usualAmount / 2 && l.Amount <= usualAmount * 2).OrderBy(l => l.Date).ToList();
            foreach (var (days, slack) in Cycles)
            {
                var pays = OnCycle(usual, days, slack);
                if (pays.Count < MinimumPays || !Steady(pays, days, slack)) continue;
                foreach (var l in pays) found.Add(l.Index);
                break;
            }
        }
        return found;
    }

    /// <summary>The lines with another one a cycle or two away: a payment on another day (expenses paid back) is not a pay.</summary>
    private static List<Line> OnCycle(List<Line> lines, double days, double slack)
    {
        bool Fits(int apart) => apart > 0 && (Math.Abs(apart - days) <= slack || Math.Abs(apart - 2 * days) <= slack);
        return lines.Where(l => lines.Any(o => Fits(Math.Abs(o.Date.DayNumber - l.Date.DayNumber)))).ToList();
    }

    /// <summary>Most pays follow the one before by one cycle (a missed pay leaves a gap of two).</summary>
    private static bool Steady(List<Line> pays, double days, double slack)
    {
        var dates = pays.Select(l => l.Date.DayNumber).Distinct().Order().ToList();
        var gaps = dates.Zip(dates.Skip(1), (a, b) => b - a).ToList();
        var oneCycle = gaps.Count(g => Math.Abs(g - days) <= slack);
        return oneCycle >= MinimumPays - 1 && oneCycle >= gaps.Count * 0.6;
    }

    private static decimal Median(List<decimal> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
}
