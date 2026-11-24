using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.BncGenerator;

namespace ProductivityApp.Finance.IntegrationTests.Infrastructure;

/// <summary>
/// September 2025 at the National Bank of Canada (invented, in its layouts): a card, a chequing and a savings account,
/// each as the PDF history and as the CSV export.
/// </summary>
public static class NationalBankSeptember
{
    public static readonly Month September = Sample(2025, 9, 31);

    private static readonly Dictionary<BncAccount, Lazy<byte[]>> Pdfs = Enum.GetValues<BncAccount>().ToDictionary(a => a, a => new Lazy<byte[]>(() => BncGenerator.Pdf(September, a)));

    /// <summary>The PDF history of <paramref name="account"/>, drawn once.</summary>
    public static byte[] PdfOf(BncAccount account) => Pdfs[account].Value;

    public static readonly Dictionary<BncAccount, AccountKind> Kinds = new() { [BncAccount.Card] = AccountKind.CreditCard, [BncAccount.Chequing] = AccountKind.Bank, [BncAccount.Savings] = AccountKind.Savings };

    /// <summary>The PDF of each of the three accounts saved into a new account each, then their CSV exports into the same accounts.</summary>
    public static async Task<IReadOnlyDictionary<BncAccount, CommitResult>> PdfThenCsvAsync(TestStore store)
    {
        // One after the other, as a person imports them: each PDF into a new account, then each CSV into its account.
        var accounts = await Kinds.Aggregate(Task.FromResult(new Dictionary<BncAccount, long>()), async (saved, k) =>
        {
            var done = await saved;
            done[k.Key] = (await store.ImportAsync(FileName(k.Key, "pdf"), PdfOf(k.Key), kind: k.Value)).AccountId;
            return done;
        });
        return await Kinds.Keys.Aggregate(Task.FromResult(new Dictionary<BncAccount, CommitResult>()), async (saved, account) =>
        {
            var done = await saved;
            done[account] = await store.ImportIntoAsync(accounts[account], FileName(account, "csv"), Csv(September, account));
            return done;
        });
    }
}
