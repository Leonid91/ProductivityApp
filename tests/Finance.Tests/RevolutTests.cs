using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// Statements of an online bank holding several currencies (the layouts of Revolut): the PDF statement of a currency
/// and the export of the app, where one file holds every currency and product, dates carry their time, the fee has a
/// column of its own and pending, declined and reverted operations are listed though they never moved money.
/// </summary>
public class RevolutTests
{
    private static async Task<List<StatementParse>> Read(TempStore s, string name, byte[] bytes)
    {
        var d = await s.Imports.ParseAsync(name, bytes);
        return [d.Parse, .. d.Others.Select(o => o.Parse)];
    }

    /// <summary>What an account really moved: each completed operation, then its fee as a line of its own.</summary>
    private static List<(DateOnly, decimal)> Moves(RevolutGenerator.Account a) =>
        [.. a.Done.SelectMany(l => new[] { (l.Date, l.Amount), (l.Date, -l.Fee) }).Where(x => x.Item2 != 0)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_export_of_every_currency_is_read_account_by_account_to_the_cent(bool french)
    {
        using var s = new TempStore();
        var accounts = RevolutGenerator.Sample();
        var parts = await Read(s, "account-statement_2026-01-01_2026-01-31.csv", RevolutGenerator.Csv(accounts, french));

        // Three accounts in the one file: euros, dollars, and the savings pocket in euros.
        Assert.Equal(3, parts.Count);
        foreach (var a in accounts)
        {
            var p = parts.Single(x => x.Currency == a.Currency && (x.Kind == AccountKind.Savings) == (a.Product == "Savings"));
            Assert.Equal(VerificationStatus.Verified, p.Status);
            Assert.Equal((a.Opening, a.Closing), (p.OpeningBalance!.Value, p.ClosingBalance!.Value));
            // Pending, declined and reverted operations are left out; a fee is a line of its own after its operation.
            Assert.Equal(Moves(a).OrderBy(x => x), p.Rows.Select(r => (r.Date, r.Amount)).OrderBy(x => x));
            Assert.DoesNotContain(p.Rows, r => r.Label.Contains("Hotel Imaginaire") || r.Label.Contains("Librairie Fictive") || r.Label.Contains("Sample Diner"));
        }
        var eur = parts.Single(x => x.Currency == "EUR" && x.Kind != AccountKind.Savings);
        // The day an operation was made (not the day it was completed), without its time.
        Assert.Contains(eur.Rows, r => r.Label == "Cash at Example Bank ATM" && r.Amount == -200m && r.Date == new DateOnly(2026, 1, 7));
        Assert.Contains(eur.Rows, r => r.Label.EndsWith(": Cash at Example Bank ATM") && r.Amount == -4m);
        Assert.Contains(eur.Rows, r => r.Label.EndsWith(": Premium plan fee") && r.Amount == -7.99m);
        Assert.DoesNotContain(eur.Rows, r => r.Amount == 0);
    }

    [Fact]
    public async Task The_accounts_of_the_export_are_saved_and_their_exchange_is_one_movement()
    {
        using var s = new TempStore();
        var accounts = RevolutGenerator.Sample();
        var first = await s.Imports.ParseAsync("revolut.csv", RevolutGenerator.Csv(accounts));
        var bank = s.Db.AddBank(new Bank { Name = "Revolut", Country = "France", System = BankingSystem.France, Currency = "EUR" });
        foreach (var d in new[] { first }.Concat(first.Others))
        {
            // Choosing an account type reads the part again: it must still be its own account, not the whole file.
            s.Imports.Choose(d.Id, null, d.Parse.Kind, bank);
            Assert.Equal(VerificationStatus.Verified, d.Parse.Status);
            s.Imports.Commit(new CommitRequest(d.Id, null, d.FileName, d.Parse.Currency, d.Parse.Kind, [], bank));
        }
        Assert.Equal(3, s.Db.Accounts().Count(a => a.BankId == bank));
        Assert.Equal(["EUR", "EUR", "USD"], s.Db.Accounts().Where(a => a.BankId == bank).Select(a => a.Currency).Order());
        // The same file again: nothing new.
        var again = await s.Imports.ParseAsync("revolut.csv", RevolutGenerator.Csv(accounts));
        Assert.True(again.AlreadyImported);
        Assert.All(again.Others, o => Assert.True(o.AlreadyImported));
        // Money put in the pocket left one account and reached the other: one movement inside the bank.
        var report = s.Summaries.Report(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), Granularity.Month, "EUR", new ExchangeRates(s.Db));
        Assert.Contains(report.Global.Movements, m => !m.FromOutside && !m.ToOutside && m.Amount == 300m);
    }

    [Fact]
    public async Task The_pdf_statement_of_a_currency_is_read_to_the_cent_without_its_reverted_operations()
    {
        using var s = new TempStore();
        foreach (var a in RevolutGenerator.Sample().Where(a => a.Product == "Current"))
        {
            var p = (await Read(s, $"{a.Currency} statement.pdf", RevolutGenerator.Pdf(a))).Single();
            Assert.Equal(VerificationStatus.Verified, p.Status);
            Assert.Equal(a.Currency, p.Currency);
            Assert.Equal("Revolut", p.Bank);
            Assert.Equal(AccountKind.Bank, p.Kind);
            Assert.Equal((a.Opening, a.Closing), (p.OpeningBalance!.Value, p.ClosingBalance!.Value));
            // On paper the fee is inside the money out of its operation, said on a line under it: read as the export
            // lists it, the operation then its fee.
            Assert.Equal(Moves(a), p.Rows.Select(r => (r.Date, r.Amount)));
            Assert.DoesNotContain(p.Rows, r => r.Label.Contains("€4.00") || r.Label.Contains("$0.33"));
            Assert.DoesNotContain(p.Rows, r => r.Label.Contains("Hotel Imaginaire"));
        }
    }

    [Fact]
    public async Task The_wording_of_the_statement_gets_sensible_categories()
    {
        using var s = new TempStore();
        var accounts = RevolutGenerator.Sample();
        var csv = await s.Imports.ParseAsync("revolut.csv", RevolutGenerator.Csv(accounts));
        var pdf = await s.Imports.ParseAsync("EUR statement.pdf", RevolutGenerator.Pdf(accounts[0]));
        foreach (var rows in new[] { csv.Rows, pdf.Rows })
        {
            // Money changed to another currency, put in a pocket or added from a card is moved, not spent or earned.
            Assert.All(rows.Where(r => r.Label.StartsWith("Exchanged to USD") || r.Label == "To pocket Holidays" || r.Label.StartsWith("Apple Pay Top-Up")),
                r => Assert.True(Categories.IsTransfer(r.CategoryId), $"{r.Label}: {r.CategoryId}"));
            Assert.Equal(3, rows.Count(r => r.Label.StartsWith("Exchanged to USD") || r.Label == "To pocket Holidays" || r.Label.StartsWith("Apple Pay Top-Up")));
            // What the bank took is a bank fee, the cash taken out is cash.
            Assert.All(rows.Where(r => r.Amount is -4m or -2.5m or -7.99m), r => Assert.Equal("fees", r.CategoryId));
            Assert.Equal("cash", rows.Single(r => r.Amount == -200m).CategoryId);
            Assert.Equal("groceries", rows.First(r => r.Label.StartsWith("Lidl")).CategoryId);
        }
        // In the export the bank says a payment to a person is a transfer.
        Assert.All(csv.Rows.Where(r => r.Label.StartsWith("To Jordan Example")), r => Assert.True(Categories.IsTransfer(r.CategoryId)));
    }

    [Fact]
    public async Task The_export_of_a_business_account_uses_the_amount_paid_not_the_one_before_conversion()
    {
        using var s = new TempStore();
        var a = RevolutGenerator.Sample()[0];
        var p = (await Read(s, "transaction-statement.csv", RevolutGenerator.BusinessCsv(a))).Single();
        Assert.Equal(VerificationStatus.Verified, p.Status);
        Assert.Equal("EUR", p.Currency);
        Assert.Equal(Moves(a).OrderBy(x => x), p.Rows.Select(r => (r.Date, r.Amount)).OrderBy(x => x));
    }

    [Fact]
    public async Task One_account_in_one_currency_stays_one_file_whatever_its_columns()
    {
        using var s = new TempStore();
        // A column naming who received each payment is not a list of the person's accounts.
        var csv = "Date,Description,Beneficiary account,Amount,Currency,Balance\n2026-03-01 08:00,Rent,FR76 0000 1111,-700.00,EUR,300.00\n2026-03-02T09:30:00Z,Phone,FR76 0000 2222,-20.00,EUR,280.00\n03/03/2026 10:15,Salary,,2000.00,EUR,2280.00\n";
        var parts = await Read(s, "export.csv", System.Text.Encoding.UTF8.GetBytes(csv));
        var p = Assert.Single(parts);
        Assert.Equal(VerificationStatus.Verified, p.Status);
        Assert.Equal([new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 3)], p.Rows.Select(r => r.Date));
    }
}
