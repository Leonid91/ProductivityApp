using System.Globalization;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Reports;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// A Canadian bank and a French bank together: each bank on its own in its currency, every bank together converted
/// to the global currency at the rate of each operation's day; the statements of the Caisse d'Epargne in every form
/// it offers; the French banking system (debit cards only, savings books).
/// </summary>
[Collection("categories")]
public class TwoBankTests
{
    /// <summary>Invented rates in the European Central Bank's layout: newest first, "N/A" where a currency had none.</summary>
    private const string Rates = """
        Date,USD,CAD,GBP,
        2025-10-10,1.1500,1.6200,0.8700,
        2025-10-09,1.1600,1.6300,N/A,
        2025-10-08,1.1700,1.6000,0.8650,
        """;

    private static ExchangeRates WithRates(TempStore s)
    {
        var rates = new ExchangeRates(s.Db);
        rates.Save(ExchangeRates.ParseCsv(Rates));
        return rates;
    }

    [Fact]
    public void Rates_of_the_day_or_the_last_day_before_and_through_the_euro()
    {
        using var s = new TempStore();
        var rates = WithRates(s);
        // The rate of the day.
        Assert.Equal(new Converted(162.00m, 1.62m, new DateOnly(2025, 10, 10)), rates.Convert(100m, "EUR", "CAD", new DateOnly(2025, 10, 10)));
        // Saturday: Friday's rate.
        Assert.Equal(new DateOnly(2025, 10, 10), rates.Convert(100m, "EUR", "CAD", new DateOnly(2025, 10, 11))!.RateDay);
        // CAD to EUR: divided, rounded to the cent.
        Assert.Equal(61.73m, rates.Convert(100m, "CAD", "EUR", new DateOnly(2025, 10, 10))!.Amount);
        // CAD to USD goes through the euro, the same day: 100 x 1.15 / 1.62.
        var cross = rates.Convert(100m, "CAD", "USD", new DateOnly(2025, 10, 10))!;
        Assert.Equal(Math.Round(100m * 1.15m / 1.62m, 2, MidpointRounding.AwayFromZero), cross.Amount);
        Assert.Equal(0.709877m, cross.Rate);
        // A day with no GBP rate ("N/A"): the day before that had one.
        Assert.Equal(new DateOnly(2025, 10, 8), rates.Convert(10m, "EUR", "GBP", new DateOnly(2025, 10, 9))!.RateDay);
        // Before the first rate, or a currency without rates: nothing is made up.
        Assert.Null(rates.Convert(10m, "EUR", "CAD", new DateOnly(2025, 10, 1)));
        Assert.Null(rates.Convert(10m, "EUR", "JPY", new DateOnly(2025, 10, 10)));
        Assert.Equal(new Converted(10m, 1m, new DateOnly(2025, 10, 1)), rates.Convert(10m, "EUR", "EUR", new DateOnly(2025, 10, 1)));
        Assert.Equal("2025-10-10", rates.Status().Latest);
    }

    /// <summary>National Bank in CAD and the Caisse d'Epargne in EUR, one month of each (the Caisse's PDF holds two accounts).</summary>
    private static async Task<(long National, long Caisse, CaisseEpargneGenerator.Statement Statement)> TwoBanks(TempStore s)
    {
        var caisse = s.Db.AddBank(new Bank { Name = "Caisse d'Epargne", Country = "France", System = BankingSystem.France, Currency = "EUR" });
        var national = s.Bank;
        var st = CaisseEpargneGenerator.Sample(2025, 10, 30);
        var first = await s.Imports.ParseAsync("releve.pdf", CaisseEpargneGenerator.Pdf(st));
        foreach (var d in new[] { first }.Concat(first.Others))
        {
            var kind = d.FileName.Contains("LIVRET", StringComparison.Ordinal) ? AccountKind.Savings : AccountKind.Bank;
            s.Imports.Choose(d.Id, null, kind, caisse);
            s.Imports.Commit(new CommitRequest(d.Id, null, null, "EUR", kind, [], BankId: caisse));
        }
        var bnc = BncGenerator.Sample(2025, 10, 4);
        var chq = await s.Imports.ParseAsync("chequing.pdf", BncGenerator.Pdf(bnc, BncGenerator.BncAccount.Chequing));
        s.Imports.Choose(chq.Id, null, AccountKind.Bank, national);
        s.Imports.Commit(new CommitRequest(chq.Id, null, null, "CAD", AccountKind.Bank, [], BankId: national));
        return (national, caisse, st);
    }

    [Fact]
    public async Task Each_bank_in_its_currency_then_all_banks_converted_operation_by_operation()
    {
        using var s = new TempStore();
        var rates = new ExchangeRates(s.Db);
        // A rate for every day of October 2025 (invented): 1.60 + day / 1000 CAD per euro.
        var csv = "Date,CAD,\n" + string.Join("\n", Enumerable.Range(1, 31).Reverse().Select(d => $"2025-10-{d:D2},{(1.60m + d / 1000m).ToString(CultureInfo.InvariantCulture)},"));
        rates.Save(ExchangeRates.ParseCsv(csv));
        var (_, _, st) = await TwoBanks(s);

        var r = s.Summaries.Report(new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 31), Granularity.Month, "CAD", rates);
        Assert.Equal(["Caisse d'Epargne", KnownBanks.National], r.Banks.Select(b => b.Name));
        var caisse = r.Banks[0].Summary;
        var national = r.Banks[1].Summary;
        Assert.Equal(("EUR", "CAD"), (caisse.Currency, national.Currency));
        // The Caisse in euros, as printed: the Livret A transfer is money moved, not spent.
        var current = st.Accounts[0];
        Assert.Equal(current.Debits - 200m, caisse.Expenses);
        Assert.Equal(current.Credits, caisse.Earnings);

        // All banks: every euro operation converted at the rate of its own day, rounded to the cent, then added up.
        decimal Cad(decimal eur, DateOnly d) => Math.Round(eur * (1.60m + d.Day / 1000m), 2, MidpointRounding.AwayFromZero);
        var spentCad = current.Lines.Where(l => l.Amount < 0 && !l.Label.Contains("LIVRET", StringComparison.Ordinal)).Sum(l => -Cad(l.Amount, l.Date));
        var earnedCad = current.Lines.Where(l => l.Amount > 0).Sum(l => Cad(l.Amount, l.Date));
        Assert.Equal(national.Expenses + spentCad, r.Global.Expenses);
        Assert.Equal(national.Earnings + earnedCad, r.Global.Earnings);
        Assert.Equal(r.Global.Earnings - r.Global.Expenses, r.Global.Balance);
        // The operations are sent once (in the banks' parts); all banks together list how each euro one was converted.
        Assert.Empty(r.Global.Transactions);
        Assert.Equal(caisse.Transactions.Count, r.Converted.Count);
        Assert.Empty(r.Unconverted);

        // How it was done, shown: one line for the Caisse's euros, before and after, and the rate of every day.
        var line = Assert.Single(r.Conversion.Lines);
        Assert.Equal(("Caisse d'Epargne", "EUR", "CAD"), (line.Group, line.From, line.To));
        Assert.Equal(caisse.Expenses, line.Expenses);
        Assert.Equal(spentCad, line.ConvertedExpenses);
        Assert.Equal(0, line.Unconverted);
        Assert.All(line.Rates, u => Assert.Equal(1.60m + int.Parse(u.Day[8..], CultureInfo.InvariantCulture) / 1000m, u.Rate));
        Assert.Equal(ExchangeRates.PageUrl, r.Conversion.PageUrl);
        // The salary: its amount in CAD at the rate of its day.
        var salary = caisse.Transactions.First(t => t.Amount == 2450.00m);
        Assert.Equal(Cad(2450.00m, DateOnly.ParseExact(salary.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)), r.Converted[salary.Id].Amount);

        // The Summary view is global: its balance is the one of all banks together.
        Assert.Equal(r.Global.Balance, Assert.Single(r.Global.Breakdown).Balance);
        // The report: each bank, then all banks with the conversions.
        var pdf = ReportBuilder.Build(r, "dd MMMM yyyy", new DateTime(2025, 11, 1), new ReportBuilder.Options());
        var text = string.Join(' ', (await ProductivityApp.Finance.Pdf.PdfReader.ReadAsync(pdf)).SelectMany(p => p.Words).Select(w => w.Text));
        Assert.Contains("All banks", text, StringComparison.Ordinal);
        Assert.Contains("converted", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_rate_for_their_day_operations_are_left_out_and_said_so()
    {
        using var s = new TempStore();
        await TwoBanks(s);
        // No rates downloaded at all.
        var r = s.Summaries.Report(new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 31), Granularity.Month, "CAD", new ExchangeRates(s.Db));
        var line = Assert.Single(r.Conversion.Lines);
        Assert.Equal(0, line.Operations);
        Assert.Equal(38, line.Unconverted);
        Assert.Equal(38, r.Unconverted.Count);
        Assert.Equal(r.Banks[1].Summary.Balance, r.Global.Balance);
        // In euros as the global currency, the Canadian bank is the one left out.
        r = s.Summaries.Report(new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 31), Granularity.Month, "EUR", new ExchangeRates(s.Db));
        Assert.Equal(("CAD", "EUR"), (Assert.Single(r.Conversion.Lines).From, r.Conversion.Lines[0].To));
        Assert.Equal(r.Banks[0].Summary.Balance, r.Global.Balance);
    }

    [Fact]
    public async Task Caisse_d_Epargne_statement_of_every_account_gives_one_balanced_statement_each()
    {
        using var s = new TempStore();
        var st = CaisseEpargneGenerator.Sample(2025, 9, 12);
        var first = await s.Imports.ParseAsync("releve.pdf", CaisseEpargneGenerator.Pdf(st));
        var drafts = new[] { first }.Concat(first.Others).ToList();
        Assert.Equal(2, drafts.Count);
        for (var i = 0; i < 2; i++)
        {
            var a = st.Accounts[i];
            var p = drafts[i].Parse;
            Assert.Equal(VerificationStatus.Verified, p.Status);
            Assert.Equal((a.Opening, a.Closing, a.Lines.Count, "EUR"), (p.OpeningBalance, p.ClosingBalance, p.Rows.Count, p.Currency));
            Assert.Equal(a.Lines.Select(l => (l.Date, l.Amount)), p.Rows.Select(r => (r.Date, r.Amount)));
            Assert.Equal(a.Number[^4..], p.Last4);
        }
        Assert.Equal((AccountKind.Bank, AccountKind.Savings), (drafts[0].Parse.Kind, drafts[1].Parse.Kind));
        Assert.EndsWith("· COMPTE DE DEPOT", drafts[0].FileName, StringComparison.Ordinal);
        Assert.EndsWith("· LIVRET A", drafts[1].FileName, StringComparison.Ordinal);
        // The second line under an operation stays in its label; fee lines and totals are not operations.
        Assert.Contains(drafts[0].Parse.Rows, r => r.Label.Contains("SALAIRE EXEMPLE SARL", StringComparison.Ordinal) && r.Label.Contains("SALAIRE SEPTEMBRE", StringComparison.Ordinal));
        Assert.DoesNotContain(drafts[0].Parse.Rows, r => r.Label.Contains("TOTAL DES", StringComparison.Ordinal) || r.Label.Contains("frais bancaires", StringComparison.Ordinal));
        // Both accounts can be saved, each once.
        foreach (var d in drafts) s.Imports.Commit(new CommitRequest(d.Id, null, null, "EUR", d.Parse.Kind, [], BankId: s.Bank));
        var again = await s.Imports.ParseAsync("releve.pdf", CaisseEpargneGenerator.Pdf(st));
        Assert.True(again.AlreadyImported && again.Others.All(o => o.AlreadyImported));
    }

    [Fact]
    public void Caisse_d_Epargne_exports_CSV_older_CSV_OFX_and_QIF_read_the_same_operations()
    {
        var st = CaisseEpargneGenerator.Sample(2025, 10, 30);
        var a = st.Accounts[0];
        var expected = a.Lines.Select(l => (l.Date, l.Amount)).OrderBy(x => x.Date).ThenBy(x => x.Amount).ToList();
        void Same(StatementParse p) => Assert.Equal(expected, p.Rows.Select(r => (r.Date, r.Amount)).OrderBy(x => x.Date).ThenBy(x => x.Amount).ToList());

        foreach (var comma in new[] { false, true })
        {
            var csv = CsvStatementReader.Parse(CaisseEpargneGenerator.Csv(a, comma), "export.csv", "EUR");
            Same(csv);
            Assert.Equal(AccountKind.Bank, csv.Kind);
            // The full label of the operation, without the simplified one repeated or the bank's tick ("0").
            Assert.All(csv.Rows.Where(r => r.Label.StartsWith("CB ", StringComparison.Ordinal)), r => Assert.Matches(@"^CB [A-Z ]+ FACT \d{6}$", r.Label));
            Assert.DoesNotContain(csv.Rows, r => r.Label.EndsWith(" 0", StringComparison.Ordinal) || r.Label.StartsWith("MONOPRIX CB", StringComparison.Ordinal));
            // The bank's sub-category when it says more: "Salaire" rather than "Revenus".
            Assert.Equal("Salaire", csv.Rows.Single(r => r.Amount == 2450m).BankCategory);
        }

        // The older export: account lines above the table, the closing balance above it and the opening one below.
        var old = CsvStatementReader.Parse(CaisseEpargneGenerator.OldCsv(st, a), "Caisse d'Epargne export.csv", "CAD");
        Same(old);
        Assert.Equal((VerificationStatus.Verified, a.Opening, a.Closing, "EUR", "5678"), (old.Status, old.OpeningBalance, old.ClosingBalance, old.Currency, old.Last4));
        // "Caisse d'Epargne" names the bank, not a savings account; the Livret A's own export is a savings account.
        Assert.Equal(AccountKind.Bank, old.Kind);
        Assert.Equal(AccountKind.Savings, CsvStatementReader.Parse(CaisseEpargneGenerator.OldCsv(st, st.Accounts[1]), "Caisse d'Epargne export.csv", "CAD").Kind);

        var ofx = MoneyFiles.ParseOfx(CaisseEpargneGenerator.Ofx(st, a), "CAD", null);
        Same(ofx);
        Assert.Equal(("EUR", a.Closing), (ofx.Currency, ofx.ClosingBalance));
        // Quicken: day/month dates and decimal commas ("-64,23" is minus sixty-four euros twenty-three).
        Same(MoneyFiles.ParseQif(CaisseEpargneGenerator.Qif(a), "export.qif", "EUR", null));
    }

    [Fact]
    public void French_banks_have_debit_cards_and_savings_books_not_credit_cards()
    {
        using var s = new TempStore();
        var fr = new ProductivityApp.Finance.Classification.Classifier([], BankingSystem.France);
        var na = new ProductivityApp.Finance.Classification.Classifier([], BankingSystem.NorthAmerica);
        // A card purchase comes out of the current account: an expense, never a card payment.
        Assert.Equal("groceries", fr.Classify("CB CARREFOUR MARKET FACT 031025", -64.23m).CategoryId);
        Assert.NotEqual(Categories.CardPayments, fr.Classify("PAIEMENT VISA CARREFOUR", -20m).CategoryId);
        // The monthly debit of a deferred-debit card is the only card movement.
        var deferred = fr.Classify("FACTURE CARTE DU 30/09/25", -412.50m);
        Assert.Equal((Categories.CardPayments, "Deferred debit card"), (deferred.CategoryId, deferred.Brand));
        Assert.Equal("Credit card payment", na.Classify("PAIEMENT VISA MERCI", -300m).Brand);
        // Livret A, LDDS, LEP: savings of one's own (moved between accounts), not investments.
        foreach (var label in new[] { "VIR LIVRET A", "VIREMENT LDDS", "VERSEMENT LEP" })
            Assert.Equal(Categories.Transfers, fr.Classify(label, -200m).CategoryId);
        Assert.Equal(Categories.Investments, fr.Classify("VERSEMENT ASSURANCE VIE", -100m).CategoryId);
        // A transfer that pays rent is rent; a plain French transfer is a bank transfer, not Interac.
        Assert.Equal("housing", fr.Classify("VIR SEPA LOYER SCI EXEMPLE", -720m).CategoryId);
        Assert.Equal("Bank transfer", fr.Classify("VIR SEPA", -50m).Brand);
        Assert.Equal("Current account (debit card)", AccountTypes.Label(AccountKind.Bank, BankingSystem.France));
        Assert.Equal("Deferred debit card", AccountTypes.Label(AccountKind.CreditCard, BankingSystem.France));
        Assert.Equal("Card statement payments", Categories.Get(Categories.CardPayments).Name);
    }
}
