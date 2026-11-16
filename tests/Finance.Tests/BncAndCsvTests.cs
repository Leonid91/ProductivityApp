using System.Text;
using ProductivityApp.Finance.Classification;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Pdf;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;
using ProductivityApp.Finance.Text;
using static ProductivityApp.Finance.Testing.BncGenerator;

namespace ProductivityApp.Finance.Tests;

/// <summary>Banque Nationale layouts (fictitious data) and CSV exports of any bank.</summary>
public class BncAndCsvTests
{
    private static readonly Month Sept = Sample(2025, 9, 31);

    private static void AssertSame(List<Line> expected, StatementParse p)
    {
        var nonZero = expected.Where(l => l.Amount != 0).ToList();
        Assert.Equal(nonZero.Count, p.Rows.Count);
        for (var i = 0; i < nonZero.Count; i++)
        {
            Assert.Equal(nonZero[i].Date, p.Rows[i].Date);
            Assert.Equal(nonZero[i].Amount, p.Rows[i].Amount);
            Assert.Equal(TextNorm.Fold(nonZero[i].Description), TextNorm.Fold(p.Rows[i].Label));
            Assert.Equal(TextNorm.Fold(nonZero[i].Category), TextNorm.Fold(p.Rows[i].BankCategory));
        }
        Assert.Equal("National Bank of Canada", p.Bank);
        Assert.Equal("bnc", p.Profile);
        Assert.Equal("CAD", p.Currency);
        Assert.True(p.NewestFirst);
    }

    [Theory]
    [InlineData(BncAccount.Card, AccountKind.CreditCard, VerificationStatus.NoBalances)]
    [InlineData(BncAccount.Chequing, AccountKind.Bank, VerificationStatus.Verified)]
    [InlineData(BncAccount.Savings, AccountKind.Savings, VerificationStatus.Verified)]
    public async Task Bnc_pdf_history_is_read_exactly(BncAccount account, AccountKind kind, VerificationStatus status)
    {
        var p = StatementParser.Parse(await PdfReader.ReadAsync(BncGenerator.Pdf(Sept, account)), null, "CAD");
        AssertSame(Sept.Of(account), p);
        Assert.Equal(kind, p.Kind);
        Assert.Equal(status, p.Status);
        if (account == BncAccount.Card) Assert.Equal("1234", p.Last4);
        if (account == BncAccount.Chequing) Assert.Equal(2, p.ZeroRowsSkipped);
    }

    [Theory]
    [InlineData(BncAccount.Card, AccountKind.CreditCard, VerificationStatus.NoBalances)]
    [InlineData(BncAccount.Chequing, AccountKind.Bank, VerificationStatus.Verified)]
    [InlineData(BncAccount.Savings, AccountKind.Savings, VerificationStatus.Verified)]
    public void Bnc_csv_export_is_read_exactly(BncAccount account, AccountKind kind, VerificationStatus status)
    {
        var p = CsvStatementReader.Parse(Csv(Sept, account), FileName(account, "csv"), "CAD");
        AssertSame(Sept.Of(account), p);
        Assert.Equal(kind, p.Kind);
        Assert.Equal(status, p.Status);
        if (account == BncAccount.Card) Assert.Equal("1234", p.Last4);
    }

    [Fact]
    public async Task Bnc_month_counts_nothing_twice_and_csv_after_pdf_adds_nothing()
    {
        using var s = new TempStore();
        var kinds = new Dictionary<BncAccount, AccountKind> { [BncAccount.Card] = AccountKind.CreditCard, [BncAccount.Chequing] = AccountKind.Bank, [BncAccount.Savings] = AccountKind.Savings };
        var ids = new Dictionary<BncAccount, long>();
        foreach (var a in kinds.Keys)
        {
            var d = await s.Imports.ParseAsync(FileName(a, "pdf"), BncGenerator.Pdf(Sept, a));
            Assert.Null(d.AccountId);
            ids[a] = s.Imports.Commit(new CommitRequest(d.Id, null, null, "CAD", kinds[a], [], BankId: s.Bank)).AccountId;
        }
        foreach (var a in kinds.Keys)
        {
            var d = await s.Imports.ParseAsync(FileName(a, "csv"), Csv(Sept, a));
            var r = s.Imports.Commit(new CommitRequest(d.Id, ids[a], null, "CAD", null, []));
            Assert.Equal(0, r.Added);
        }
        Assert.Equal(3, s.Db.Accounts().Count);

        var sum = s.Summaries.Summarize(new DateOnly(2025, 9, 1), new DateOnly(2025, 9, 30), Granularity.Month, "CAD");
        var purchases = -Sept.Card.Where(l => l.Amount < 0).Sum(l => l.Amount);
        Assert.Equal(purchases + 9.95m, sum.Expenses);
        Assert.Equal(2 * 2310.45m + 3.17m, sum.Earnings);
        var all = s.Db.AllTransactions();
        Assert.All(all.Where(t => t.RawLabel.StartsWith("Paiement", StringComparison.Ordinal) && t.RawLabel != "Paiement des interets" && t.RawLabel != "Paiement de facture"),
            t => Assert.Equal(Categories.CardPayments, t.CategoryId));
        Assert.All(all.Where(t => t.CategoryId == Categories.CardPayments), t => Assert.Equal("Credit card payment", t.Brand));
        Assert.Equal(Categories.Investments, all.Single(t => t.RawLabel == "Paiement de facture").CategoryId);
        Assert.Equal(2, all.Count(t => t.RawLabel == "Transfert entre comptes" && t.CategoryId == Categories.Transfers));
    }

    [Fact]
    public async Task Nothing_is_imported_before_the_person_chooses_the_account()
    {
        using var s = new TempStore();
        var d = await s.Imports.ParseAsync(FileName(BncAccount.Chequing, "csv"), Csv(Sept, BncAccount.Chequing));
        Assert.Null(d.AccountId);
        Assert.Null(d.Kind);
        Assert.Throws<ArgumentException>(() => s.Imports.Commit(new CommitRequest(d.Id, null, null, "CAD", null, [])));
        Assert.Empty(s.Db.Accounts());
    }

    [Fact]
    public async Task Lines_of_another_account_in_the_same_account_are_never_taken_for_duplicates()
    {
        // Two files of the same day and amounts but different operations, both put into one account by the person:
        // every line must be kept.
        using var s = new TempStore();
        var savings = Csv(Sept, BncAccount.Savings);
        var chequing = Csv(Sept, BncAccount.Chequing);
        var d1 = await s.Imports.ParseAsync("a.csv", savings);
        var id = s.Imports.Commit(new CommitRequest(d1.Id, null, null, "CAD", AccountKind.Bank, [], BankId: s.Bank)).AccountId;
        var d2 = await s.Imports.ParseAsync("b.csv", chequing);
        var r = s.Imports.Commit(new CommitRequest(d2.Id, id, null, "CAD", null, []));
        Assert.Equal(Sept.Chequing.Count(l => l.Amount != 0), r.Added);
        Assert.Equal(0, r.SkippedDuplicates);
    }

    [Fact]
    public void Lines_dated_in_the_next_month_are_kept_in_that_month()
    {
        var csv = "Date;Description;Categorie;Debit;Credit;Solde\n\"2025-10-01\";\"Loyer Exemple\";\"Hypotheque et loyer\";\"1500.0\";\"0\";\"500.00\"\n\"2025-09-29\";Salaire;Revenus;\"0\";\"1975.0\";\"1975.00\"\n";
        var p = CsvStatementReader.Parse(Encoding.UTF8.GetBytes(csv), "x.csv", "CAD");
        Assert.Equal(2, p.Rows.Count);
        Assert.Equal(new DateOnly(2025, 10, 1), p.Rows[1].Date);
        Assert.Equal(-1500m, p.Rows[1].Amount);
        Assert.Equal("housing", new Classifier([]).Classify(p.Rows[1].Label, p.Rows[1].Amount, p.Rows[1].BankCategory).CategoryId);
    }

    [Fact]
    public void Generic_csv_with_header_oldest_first_month_day()
    {
        var csv = "Date,Description,Withdrawals,Deposits,Balance\n" +
                  "10/03/2025,TIM HORTONS #123,4.50,,995.50\n" +
                  "10/05/2025,PAYROLL DEPOSIT EXAMPLE CORP,,1500.00,2495.50\n" +
                  "10/14/2025,\"COSTCO WHOLESALE, LAVAL\",210.33,,2285.17\n";
        var p = CsvStatementReader.Parse(Encoding.UTF8.GetBytes(csv), "chequing.csv", "CAD");
        Assert.Equal(VerificationStatus.Verified, p.Status);
        Assert.Equal(1000.00m, p.OpeningBalance);
        Assert.Equal([-4.50m, 1500.00m, -210.33m], p.Rows.Select(r => r.Amount));
        Assert.Equal(new DateOnly(2025, 10, 14), p.Rows[2].Date);
        Assert.Equal("COSTCO WHOLESALE, LAVAL", p.Rows[2].Label);
    }

    [Fact]
    public void Generic_csv_without_header()
    {
        var csv = "2025-10-03,TIM HORTONS,4.50,,995.50\r\n2025-10-04,E-TRANSFER RECEIVED,,40.00,1035.50\r\n";
        var p = CsvStatementReader.Parse(Encoding.UTF8.GetBytes(csv), "export.csv", "CAD");
        Assert.Equal(VerificationStatus.Verified, p.Status);
        Assert.Equal([-4.50m, 40.00m], p.Rows.Select(r => r.Amount));
        Assert.Equal("TIM HORTONS", p.Rows[0].Label);
    }

    [Fact]
    public void French_csv_with_comma_decimals_in_windows_encoding()
    {
        var csv = "Date;Libellé;Montant;Devise\n12/10/2025;CB CARREFOUR MARKET;-1 234,56;EUR\n13/10/2025;VIR SEPA RECU SALAIRE;2 150,00;EUR\n";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var p = CsvStatementReader.Parse(Encoding.GetEncoding(1252).GetBytes(csv), "releve.csv", "CAD");
        Assert.Equal("EUR", p.Currency);
        Assert.Equal([-1234.56m, 2150.00m], p.Rows.Select(r => r.Amount));
        Assert.Equal(new DateOnly(2025, 10, 12), p.Rows[0].Date);
        Assert.Equal(VerificationStatus.NoBalances, p.Status);
    }

    [Fact]
    public void Card_csv_with_positive_purchases_is_turned_around()
    {
        var csv = "Transaction Date,Description,Amount\n2025-10-02,NETFLIX.COM,20.69\n2025-10-05,IGA EXTRA,54.10\n2025-10-09,PAYMENT THANK YOU,-74.79\n";
        var p = CsvStatementReader.Parse(Encoding.UTF8.GetBytes(csv), "visa-october.csv", "CAD");
        Assert.Equal(AccountKind.CreditCard, p.Kind);
        Assert.Equal([-20.69m, -54.10m, 74.79m], p.Rows.Select(r => r.Amount));
    }

    [Fact]
    public void Bank_categories_and_banking_systems()
    {
        var na = new Classifier([], BankingSystem.NorthAmerica);
        Assert.Equal("groceries", na.Classify("Commerce Inconnu", -12m, "Epicerie").CategoryId);
        Assert.Equal(ValueSource.Bank, na.Classify("Commerce Inconnu", -12m, "Epicerie").CategorySource);
        Assert.Equal("cardpayments", na.Classify("Paiement recu merci!", 120m, "Paiement carte de credit").CategoryId);
        Assert.Equal("cardpayments", na.Classify("PAIEMENT MASTERCARD", -120m).CategoryId);
        Assert.Equal("investments", na.Classify("Paiement de facture", -500m, "Investissements").CategoryId);
        Assert.Equal("otherincome", na.Classify("Paiement des interets", 3.17m, "Revenu d'interets").CategoryId);

        var fr = new Classifier([], BankingSystem.France);
        Assert.Equal("cardpayments", fr.Classify("PRLV FACTURE CARTE 0925", -642.10m).CategoryId);
        Assert.Equal("groceries", fr.Classify("CB CARREFOUR MARKET", -31.20m).CategoryId);
    }

    [Fact]
    public void Savings_movements_are_linked_without_any_wording()
    {
        using var s = new TempStore();
        var chequing = s.Db.AddAccount(new Account { Name = "Chequing", Currency = "CAD", Kind = AccountKind.Bank });
        var savings = s.Db.AddAccount(new Account { Name = "Savings", Currency = "CAD", Kind = AccountKind.Savings });
        var t1 = s.Db.AddTransaction(new Transaction { AccountId = chequing, Date = new(2025, 9, 3), Amount = -300m, Currency = "CAD", RawLabel = "WEB 0042", Brand = "Web", CategoryId = "other" });
        var t2 = s.Db.AddTransaction(new Transaction { AccountId = savings, Date = new(2025, 9, 4), Amount = 300m, Currency = "CAD", RawLabel = "WEB 0042", Brand = "Web", CategoryId = "otherincome" });
        Assert.Equal(2, TransferMatcher.Link(s.Db));
        Assert.Equal(Categories.Transfers, s.Db.GetTransaction(t1)!.CategoryId);
        Assert.Equal(ValueSource.Linked, s.Db.GetTransaction(t2)!.CategorySource);
    }
}
